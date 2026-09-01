using System.Diagnostics;
using System.Threading;

namespace ClassIslandInjector;

/// <summary>
/// 多轨视频工程播放器：以统一时钟（targetFps）驱动，每个轨道独立解码，
/// 同一时刻所有轨道上处于活跃区间的片段同时播放，由调用方叠放合成
/// （轨道号越大越靠上层）。片段在 [StartTime, StartTime+Duration) 区间内活跃。
///
/// 调度模型（2026-08-31 定稿，修复慢放/卡死）：
///  - 墙钟时钟：<c>time = clockBase + (Stopwatch elapsed)</c>，与每拍耗时完全解耦——
///    解码/快进再慢也只影响画面更新率，播放头永远按真实时间走（旧版按拍耗时累加，
///    大跳 seek 后形成"拍越耗时→时钟越跳→落后越多"的正反馈卡死）。
///  - 帧号消费：每拍计算目标帧号 <c>targetN = mediaTime × fps</c>，与该轨已消费帧号
///    <see cref="TrackState.LastFrameIndex"/> 的差 = 本拍应消费的帧数：前面的帧顺序
///    解码丢弃、最后一帧发给 UI。60fps 素材在 24fps 拍下每拍消费 2~3 帧，速度精确
///    （旧版每拍固定消费 1 帧 = 高帧率素材被慢放 2.5 倍）。
///  - 落后超过 48 帧（约 2s@24fps）：一次 SeekTo 跳过（顺序解码追不上时的兜底）。
///  - UI 未消费上帧（<see cref="MarkTrackConsumed"/> 未回）本拍不发帧，队列永不积压。
///
/// 帧回调在播放器线程触发，调用方负责把像素复制到自己的缓冲并把 UI 更新 Post 到 UI 线程。
/// 所有解码器的开关都在播放器线程串行执行，避免多线程释放/重建解码器的竞态。
/// </summary>
internal sealed class VideoProjectPlayer : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[player] {message}");

    private readonly List<VideoClip> _clips;
    private readonly double _duration;
    private readonly int _maxDimension;
    private readonly int _targetFps;
    private readonly Action<VideoFrame, VideoClip, int> _onFrame;
    /// <summary>覆盖层帧生成尺寸（按输出比例，最长边 = _maxDimension）。</summary>
    private readonly int _overlayW;
    private readonly int _overlayH;
    /// <summary>轨道是否启用（跳过隐藏轨）。</summary>
    private readonly bool[] _trackEnabled;
    /// <summary>硬件解码器名（"auto" = 按编码自动选 D3D11VA；null = 软解）。</summary>
    public string? HardwareDecoder { get; set; }

    private readonly object _sync = new();
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _disposed;
    // 墙钟：time = clockBase + (Stopwatch.GetTimestamp() - clockStart) / Frequency。
    // clockBase/clockStart 仅在 worker 线程（seek 处理/循环复位）与 Start/Resume 时写；UI 读走 lock。
    private double _clockBase;
    private long _clockStart;
    /// <summary>暂停/停止时冻结的时间（CurrentTime 在非运行态返回它）。</summary>
    private double _frozen;
    private volatile bool _seekRequested;
    private double _seekTo;
    private TrackState[] _tracks = [];
    /// <summary>统计日志节流时间戳。</summary>
    private long _lastStatsTimestamp;

    private sealed class TrackState
    {
        public int Track;
        public VideoFrameSource? Source;
        public VideoClip? ActiveClip;
        /// <summary>覆盖层片段生成的静态帧（只生成一次）。</summary>
        public VideoFrame? OverlayFrame;
        /// <summary>本段覆盖层帧是否已发送给 UI。</summary>
        public bool OverlaySent;
        /// <summary>UI 消费完本轨道上一帧后 Set，播放器据此才拉下一帧（防 UI 异步复制竞态）。</summary>
        public readonly ManualResetEventSlim Consumed = new(true);
        /// <summary>该轨已消费到的媒体帧号（相对片段开头，按源帧率换算）；-1 = 尚未消费。</summary>
        public long LastFrameIndex = -1;
        /// <summary>该轨源的帧率（打开时记录；&lt;=0 用 25 兜底）。</summary>
        public double SourceFps;
        /// <summary>该轨源已到 EOF（后续拍不再拉帧，也不触发追帧 seek）。</summary>
        public bool Eof;
        // ---- 统计（日志用） ----
        public long ShownFrames;
        public long SkippedFrames;
        public long SeekCount;
    }

    public VideoProjectPlayer(VideoProject project, int maxDimension, int targetFps,
        Action<VideoFrame, VideoClip, int> onFrame)
    {
        // 轨道启用：跳过隐藏轨。
        var trackCount = project.Clips.Count == 0 ? 1 : project.Clips.Max(c => c.Track) + 1;
        _trackEnabled = new bool[trackCount];
        for (var t = 0; t < trackCount; t++)
        {
            var st = project.GetTrackState(t);
            _trackEnabled[t] = st == null || !st.Hidden;
        }

        // 浅拷贝片段列表：播放器只枚举自己的列表（编辑器增删/拖拽不破坏播放），
        // 但片段对象与工程共享引用，属性编辑（入出点/变换）可实时反映到预览。
        // 隐藏轨的片段直接排除（播放时不显示）。
        _clips = project.Clips.Where(c => c.Track < _trackEnabled.Length && _trackEnabled[c.Track]).ToList();
        _duration = project.Duration;
        _maxDimension = maxDimension;
        _targetFps = Math.Max(1, targetFps);
        _onFrame = onFrame;
        // 覆盖层帧尺寸：保持输出宽高比，最长边不超过 maxDimension。
        var aspect = project.OutputWidth / Math.Max(1.0, project.OutputHeight);
        _overlayW = maxDimension;
        _overlayH = Math.Max(2, (int)(maxDimension / aspect));
    }

    public void Start()
    {
        if (_duration <= 0)
        {
            return;
        }

        var trackCount = _clips.Count == 0 ? 1 : _clips.Max(c => c.Track) + 1;
        _tracks = new TrackState[trackCount];
        for (var i = 0; i < trackCount; i++)
        {
            _tracks[i] = new TrackState { Track = i };
        }

        lock (_sync)
        {
            _clockBase = 0;
            _clockStart = Stopwatch.GetTimestamp();
            _frozen = 0;
        }

        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "VideoProject" };
        _worker.Start();
        Log($"播放开始 时长={_duration:0.##}s 轨道={trackCount} 目标帧率={_targetFps}");
    }

    /// <summary>请求播放线程尽快退出（资源由 Dispose 释放）。</summary>
    public void Stop() => _running = false;

    /// <summary>当前播放时间（秒）。暂停后保留在暂停位置。</summary>
    public double CurrentTime
    {
        get
        {
            lock (_sync)
            {
                // 运行中按墙钟实时计算；暂停/停止返回冻结值（墙钟不再流动）。
                return _running
                    ? _clockBase + (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency
                    : _frozen;
            }
        }
    }

    /// <summary>暂停播放（线程退出、时间冻结在当前位置），可 <see cref="Resume"/> 恢复。</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_running)
            {
                _frozen = _clockBase + (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
            }
        }

        _running = false;
        foreach (var state in _tracks)
        {
            state.Consumed.Set(); // 解除可能的帧消费等待
        }

        LogStats(force: true);
    }

    /// <summary>从当前位置恢复播放（须先 <see cref="Pause"/>；时间不会自动复位）。</summary>
    public void Resume()
    {
        if (_running || _disposed || _duration <= 0)
        {
            return;
        }

        lock (_sync)
        {
            _clockBase = _frozen;
            _clockStart = Stopwatch.GetTimestamp();
        }

        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "VideoProject" };
        _worker.Start();
    }

    /// <summary>跳转到指定时间（秒）。播放线程会在下一个循环拍应用：复位时钟并重开各轨解码器。</summary>
    public void Seek(double time)
    {
        lock (_sync)
        {
            _seekTo = Math.Max(0, time);
            _seekRequested = true;
        }
    }

    /// <summary>UI 线程处理完指定轨道的当前帧后调用，允许播放器覆写该轨道缓冲。</summary>
    public void MarkTrackConsumed(int track)
    {
        if (track >= 0 && track < _tracks.Length)
        {
            _tracks[track].Consumed.Set();
        }
    }

    /// <summary>主循环：墙钟驱动各轨道同步消费帧。</summary>
    private void Loop()
    {
        var intervalMs = (long)(1000.0 / _targetFps);
        var sw = new Stopwatch();
        while (_running && !_disposed)
        {
            sw.Restart();
            var restartTracks = false;
            double time;
            lock (_sync)
            {
                if (_seekRequested)
                {
                    // 跳转：时钟基准移到目标时间，下一拍按新时间重新打开各轨解码器。
                    _seekRequested = false;
                    _clockBase = Math.Max(0, _seekTo);
                    _clockStart = Stopwatch.GetTimestamp();
                    _frozen = _clockBase;
                    restartTracks = true;
                }
                else
                {
                    var now = _clockBase + (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
                    if (now >= _duration)
                    {
                        // 播完整个时间轴：复位时钟，下一拍重新打开。
                        _clockBase = 0;
                        _clockStart = Stopwatch.GetTimestamp();
                        restartTracks = true;
                    }
                }

                time = _clockBase + (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
            }

            if (restartTracks)
            {
                foreach (var state in _tracks)
                {
                    var targetClip = FindActiveClip(state.Track, time);
                    if (ReferenceEquals(targetClip, state.ActiveClip) &&
                        targetClip is { Kind: "Video" } && state.Source != null)
                    {
                        // 同片段跳转：复用已打开的解码器直接 seek（avformat_open_input +
                        // find_stream_info 对长视频要几百毫秒，重开是大跳卡顿的主因）。
                        var mediaTime = targetClip.InPoint + Math.Max(0, time - targetClip.StartTime);
                        state.Source.SeekTo(mediaTime);
                        var fps = state.SourceFps > 0 ? state.SourceFps : 25;
                        state.LastFrameIndex = (long)(mediaTime * fps);
                        state.Eof = false;
                        state.Consumed.Set();
                        continue;
                    }

                    CloseTrack(state);
                }
            }

            foreach (var state in _tracks)
            {
                PumpTrack(state, time);
            }

            LogStats();

            if (intervalMs > 0)
            {
                var wait = intervalMs - sw.ElapsedMilliseconds;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }
            }
            // 拍超耗时（解码慢）不补偿到时钟：墙钟永远按真实时间走，画面靠丢帧追赶。
        }
    }

    /// <summary>单轨调度：按墙钟对应的目标帧号消费帧（前面丢弃、最后一帧显示）。</summary>
    private void PumpTrack(TrackState state, double time)
    {
        var clip = FindActiveClip(state.Track, time);
        if (!ReferenceEquals(clip, state.ActiveClip))
        {
            CloseTrack(state);
            if (clip != null && clip.Kind == "Video")
            {
                // 打开素材时直接定位到当前时刻对应的媒体时间（拖播放头/中途切入的片段不再从入点重播）。
                var seedMediaTime = clip.InPoint + Math.Max(0, time - clip.StartTime);
                state.Source = OpenSource(clip, seedMediaTime);
                state.SourceFps = state.Source?.SourceFps ?? 0;
                state.LastFrameIndex = (long)(seedMediaTime * (state.SourceFps > 0 ? state.SourceFps : 25));
            }

            state.ActiveClip = clip;
        }

        if (state.ActiveClip == null)
        {
            return;
        }

        if (state.ActiveClip.Kind == "Video" && state.Source != null)
        {
            if (state.Eof || !state.Consumed.IsSet)
            {
                // EOF 或 UI 尚未消化上一帧：本拍不发（丢帧，时钟不慢放、队列不积压）。
                return;
            }

            var mediaTime = state.ActiveClip.InPoint + Math.Max(0, time - state.ActiveClip.StartTime);
            var fps = state.SourceFps > 0 ? state.SourceFps : 25.0;
            var targetN = (long)(mediaTime * fps);
            var behind = targetN - state.LastFrameIndex;
            if (behind <= 0)
            {
                return; // 还没到下一帧时间（低帧率素材隔拍显示，防快放）。
            }

            if (behind > 48)
            {
                // 落后约 2s 以上（大跳/解码长期跟不上）：一次 seek 跳过，避免顺序解码永远追不上。
                if (state.Source.SeekTo(mediaTime))
                {
                    state.LastFrameIndex = targetN;
                    state.SeekCount++;
                    Log($"轨{state.Track} 落后{behind}帧 → seek 到 {mediaTime:0.##}s");
                    return;
                }

                behind = 48; // seek 失败退化为顺序快进。
            }

            // 顺序消费 behind 帧：前 behind-1 帧丢弃（每帧几毫秒），最后一帧发给 UI。
            var skip = (int)Math.Min(behind - 1, 32);
            for (var i = 0; i < skip; i++)
            {
                state.SkippedFrames++;
                if (!state.Source.TryReadFrame(out _))
                {
                    state.Eof = true;
                    break;
                }
            }

            if (state.Eof)
            {
                state.LastFrameIndex = targetN;
                return;
            }

            if (state.Source.TryReadFrame(out var frame) && frame != null)
            {
                // LastFrameIndex 反映实际消费量（落后多时 targetN 一次追不完，下拍继续）。
                state.LastFrameIndex = Math.Min(state.LastFrameIndex + skip + 1, targetN);
                state.ShownFrames++;
                try
                {
                    _onFrame(frame, state.ActiveClip, state.Track);
                }
                catch
                {
                    // 调用方异常不中断播放。
                }

                state.Consumed.Reset();
                state.Consumed.Wait(150);
            }
            else
            {
                state.Eof = true; // EOF：画面停在最后一帧。
                state.LastFrameIndex = targetN;
            }
        }
        else if (state.ActiveClip.Kind != "Video" && !state.OverlaySent)
        {
            // 文本/形状/图片覆盖层：生成一次静态帧并发送（静态内容无需每拍重发）。
            state.OverlayFrame ??= OverlayFrameGenerator.Render(state.ActiveClip, _overlayW, _overlayH);
            if (state.OverlayFrame != null)
            {
                state.OverlaySent = true;
                try
                {
                    _onFrame(state.OverlayFrame, state.ActiveClip, state.Track);
                }
                catch
                {
                    // 调用方异常不中断播放。
                }

                state.Consumed.Reset();
                state.Consumed.Wait(300);
            }
        }
    }

    /// <summary>关闭并释放轨道解码器（复位消费信号量，避免挂起）。</summary>
    private void CloseTrack(TrackState state)
    {
        state.Source?.Dispose();
        state.Source = null;
        state.OverlayFrame = null;
        state.OverlaySent = false;
        state.LastFrameIndex = -1;
        state.Eof = false;
        state.Consumed.Set();
    }

    /// <summary>每 2 秒输出一次各轨调度统计（显示/丢弃/seek 帧数），用于诊断慢放与卡顿。</summary>
    private void LogStats(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && (now - _lastStatsTimestamp) / (double)Stopwatch.Frequency < 2.0)
        {
            return;
        }

        _lastStatsTimestamp = now;
        var parts = _tracks.Select(t =>
            $"轨{t.Track}={t.ActiveClip?.Kind ?? "-"} 显{t.ShownFrames} 丢{t.SkippedFrames} seek{t.SeekCount}" +
            (t.Eof ? "[EOF]" : ""));
        Log($"t={CurrentTime:0.##}s | {string.Join(" | ", parts)}");
    }

    /// <summary>
    /// 打开素材并定位到媒体时间（<paramref name="mediaTime"/> 与入点取大者）；
    /// 失败返回 null（该轨道本周期静默无画面）。
    /// </summary>
    private VideoFrameSource? OpenSource(VideoClip clip, double mediaTime)
    {
        try
        {
            var source = new VideoFrameSource { HardwareDecoder = HardwareDecoder };
            if (!source.Open(clip.SourcePath, _maxDimension))
            {
                source.Dispose();
                return null;
            }

            var seekTo = Math.Max(clip.InPoint, mediaTime);
            if (seekTo > 0.01)
            {
                source.SeekTo(seekTo);
            }

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前时刻指定轨道上的活跃片段（同轨重叠时取起始时间最晚的）。</summary>
    private VideoClip? FindActiveClip(int track, double time)
    {
        VideoClip? best = null;
        foreach (var clip in _clips)
        {
            if (clip.Track != track)
            {
                continue;
            }

            if (time >= clip.StartTime && time < clip.StartTime + clip.Duration)
            {
                if (best == null || clip.StartTime >= best.StartTime)
                {
                    best = clip;
                }
            }
        }

        return best;
    }

    public void Dispose()
    {
        _disposed = true;
        _running = false;
        foreach (var state in _tracks)
        {
            state.Consumed.Set(); // 解除等待
            state.Source?.Dispose();
            state.Source = null;
        }

        _worker?.Join(800);
        _worker = null;
    }
}
