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
///  - **追赶有上限**（2026-09-29 实测后收紧，见 <see cref="CatchUpSeekFrames"/> /
///    <see cref="MaxSkipPerTick"/>）：源帧率远高于显示帧率时（手机视频常见 54fps vs 显示 24fps），
///    「把中间帧顺序解出来丢掉」的代价是每分钟 54 次解码 —— 单轨就要 0.63 个核，多轨时
///    解码全在**一条线程上串行**，永远追不上；一旦拍内解码超过拍长就会滚成
///    「拍越耗时→落后越多→解得越多」的正反馈，投递率崩到 1fps 左右，
///    表现就是「画面卡住只剩声音」（音频在另一条线程照常走）。
///    因此落后超过阈值直接 seek 跳过（实测 seek+解一帧 76ms，而硬解 32 帧要 370ms），
///    并且每拍的解码量封顶，保证单拍耗时可控、绝不雪崩。
///  - UI 未消费上帧（<see cref="MarkTrackConsumed"/> 未回）本拍不发帧，队列永不积压。
///
/// 帧回调在播放器线程触发，调用方负责把像素复制到自己的缓冲并把 UI 更新 Post 到 UI 线程。
/// 所有解码器的开关都在播放器线程串行执行，避免多线程释放/重建解码器的竞态。
/// 片段列表支持热同步（RefreshClips）：编辑器增删/拖拽/撤销后调用，播放器下一拍
/// 即按新列表调度（否则已删除的片段仍会继续播放）。
/// </summary>
internal sealed class VideoProjectPlayer : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[player] {message}");

    private readonly VideoProject _project;
    /// <summary>工程音频混音器（音频轨片段 + 视频片段自带原声），随播放启停。</summary>
    private readonly ProjectAudioMixer _mixer = new();
    private readonly int _maxDimension;
    private readonly int _targetFps;
    private readonly Action<VideoFrame, VideoClip, int> _onFrame;
    /// <summary>覆盖层帧生成尺寸（按输出比例，最长边 = _maxDimension）。</summary>
    private readonly int _overlayW;
    private readonly int _overlayH;
    /// <summary>活跃片段列表（RefreshClips 可整体替换；播放线程按拍枚举单引用，交换安全）。</summary>
    private List<VideoClip> _clips;
    /// <summary>工程总时长（随 RefreshClips 同步；循环复位用它判断）。</summary>
    private double _duration;
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
    /// <summary>上一统计周期内的拍耗时累计 / 峰值 / 拍数（诊断「一拍为什么这么慢」）。</summary>
    private double _tickSumMs;
    private double _tickMaxMs;
    private int _tickCount;
    /// <summary>上次音画漂移检查的时间戳。</summary>
    private long _lastAudioSyncCheck;
    /// <summary>音画漂移检查间隔（秒）。</summary>
    private const double AudioSyncCheckSeconds = 5.0;
    /// <summary>
    /// 允许的音画偏差（秒）：超过就把音频拉回视频时钟。
    /// 别调太小：声卡输出缓冲本身就有 ~0.1s 延迟（实测「声卡位置 vs 渲染位置」恒定偏差 0.12s），
    /// 容差贴着这个量会被噪声触发，而每次重定位都要丢弃并重开音频源 = 一次可听的断点。
    /// </summary>
    private const double AudioSyncToleranceSeconds = 0.30;

    /// <summary>两次音画重定位的最小间隔（秒）：一次重定位会丢掉音频源，别频繁做。</summary>
    private const double AudioSyncCooldownSeconds = 10.0;

    /// <summary>上次音画重定位的时间戳。</summary>
    private long _lastAudioResyncTimestamp;

    /// <summary>
    /// 落后多少帧就改用 seek 跳过（不再顺序解码追赶）。
    /// 8 帧@24fps ≈ 0.33s：再落后就说明「本拍解码量已经超过拍长」，顺序解码只会雪崩
    /// （实测 3 轨 × 800px 时单拍解码可涨到 1.1s，投递率崩到 1fps = 画面卡死）。
    /// </summary>
    private const int CatchUpSeekFrames = 8;

    /// <summary>
    /// 每拍单轨最多顺序解码丢弃多少帧（封顶值）。宁可让画面稍微落后于媒体时间，
    /// 也不能让单拍耗时失控 —— 投递率稳住比「每帧都精确」重要得多。
    /// </summary>
    private const int MaxSkipPerTick = 4;

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
        _project = project;
        _clips = BuildClipList();
        _duration = Math.Max(0, project.Duration);
        _maxDimension = maxDimension;
        _targetFps = Math.Max(1, targetFps);
        _onFrame = onFrame;
        // 覆盖层帧尺寸：保持输出宽高比，最长边不超过 maxDimension。
        var aspect = project.OutputWidth / Math.Max(1.0, project.OutputHeight);
        _overlayW = maxDimension;
        _overlayH = Math.Max(2, (int)(maxDimension / aspect));
    }

    /// <summary>按工程当前片段重建播放列表（跳过隐藏轨；音频片段只参与混音，不进视频轨）。</summary>
    private List<VideoClip> BuildClipList()
    {
        var trackCount = Math.Max(1, _project.TrackCount);
        return _project.Clips
            .Where(c => !c.IsAudio && c.Track >= 0 && c.Track < trackCount &&
                        _project.GetTrackState(c.Track) is not { Hidden: true })
            .ToList();
    }

    /// <summary>
    /// 重新同步工程片段（编辑器增删片段 / 拖拽 / 撤销重做 / 轨道隐藏后调用）：
    /// 重建片段列表、刷新总时长并按需扩展轨道状态。修复「编辑后播放器仍按构造时的
    /// 片段快照播放——已删除的片段继续出现」的问题。任意线程调用安全。
    /// </summary>
    public void RefreshClips()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _clips = BuildClipList();
            _duration = Math.Max(0, _project.Duration);
        }

        // 音频混音按同一份工程对象取活跃片段，编辑后不必重启输出；
        // 若起播时工程还没有音频内容（编辑器常见顺序：先播放、后加素材），
        // 这里会把音频输出补建起来，否则会一直静音。
        _mixer.UpdateProject(_project);
        _mixer.EnsureRunning(_project, CurrentTime);
        EnsureTrackStates();
    }

    /// <summary>轨道数增长时扩展轨道状态（新轨默认启用；播放线程遍历旧数组引用安全）。</summary>
    private void EnsureTrackStates()
    {
        var trackCount = _clips.Count == 0 ? 1 : _clips.Max(c => c.Track) + 1;
        if (_tracks.Length >= trackCount)
        {
            return;
        }

        var grown = new TrackState[trackCount];
        for (var i = 0; i < _tracks.Length; i++)
        {
            grown[i] = _tracks[i];
        }

        for (var i = _tracks.Length; i < trackCount; i++)
        {
            grown[i] = new TrackState { Track = i };
        }

        _tracks = grown;
    }

    /// <summary>
    /// 某轨道从「有活跃片段」变为「无活跃片段」时回调（播放器线程触发）。
    /// 调用方借此隐藏该轨显示图层：否则片段删除/播完后，最后一帧会一直冻结在画面上
    /// （时间轴里已经没有它，预览/主界面却仍显示）。
    /// </summary>
    public Action<int>? TrackCleared { get; set; }

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
        _lastAudioSyncCheck = Stopwatch.GetTimestamp();
        _worker = new Thread(Loop) { IsBackground = true, Name = "VideoProject" };
        _worker.Start();
        // 音频与视频共用同一工程时间轴，一起从 0 起播（没有音频内容时内部直接返回 false）。
        _mixer.Start(_project, 0);
        Log($"播放开始 时长={_duration:0.##}s 轨道={trackCount} 目标帧率={_targetFps}");
    }

    /// <summary>请求播放线程尽快退出（资源由 Dispose 释放）。</summary>
    public void Stop()
    {
        _running = false;
        _mixer.Stop();
    }

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

        // 音频跟着一起停：WASAPI 只是停止拉取，位置保留，Resume 时从原处继续。
        _mixer.Pause();
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
        // 暂停期间可能才把音频素材加进来：恢复时先确保输出存在，再继续播放。
        _mixer.EnsureRunning(_project, _frozen);
        _mixer.Resume();
    }

    /// <summary>跳转到指定时间（秒）。播放线程会在下一个循环拍应用：复位时钟并重开各轨解码器。</summary>
    public void Seek(double time)
    {
        lock (_sync)
        {
            _seekTo = Math.Max(0, time);
            _seekRequested = true;
            _lastAudioSyncCheck = Stopwatch.GetTimestamp(); // 跳转后重新计时（缓冲需要时间跟上）
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
                // 音频混音与视频轨共用同一条时间轴：跳转 / 循环复位时同步音频位置。
                _mixer.Seek(time);
                foreach (var state in _tracks)
                {
                    var targetClip = FindActiveClip(state.Track, time);
                    if (ReferenceEquals(targetClip, state.ActiveClip) &&
                        targetClip is { Kind: "Video" } && state.Source != null)
                    {
                        // 同片段跳转：复用已打开的解码器直接 seek（avformat_open_input +
                        // find_stream_info 对长视频要几百毫秒，重开是大跳卡顿的主因）。
                        var mediaTime = targetClip.SourceTimeAt(time - targetClip.StartTime);
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

            _tickSumMs += sw.Elapsed.TotalMilliseconds;
            _tickMaxMs = Math.Max(_tickMaxMs, sw.Elapsed.TotalMilliseconds);
            _tickCount++;
            LogStats();
            CheckAudioSync(time);

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

        // 退出前收尾：若 Dispose 已经在等（或被超时放弃），解码器由**本线程**释放 ——
        // 只有在这里释放才能保证「释放时不会有人正在读它」。Dispose 侧的 3s Join 超时后
        // 就是靠这条路径兜底（否则要么泄漏，要么释放竞态击穿进程）。
        if (_disposed)
        {
            foreach (var state in _tracks)
            {
                state.Source?.Dispose();
                state.Source = null;
            }

            Log("播放线程退出：已释放各轨解码器");
        }
    }

    /// <summary>
    /// 音画漂移校正：视频走系统墙钟、音频走声卡采样时钟，长视频里两者差 0.1% 也会累积成
    /// 肉眼可见的不同步（266s 的时间轴差 0.1% ≈ 0.27s）。每 5s 比一次「声卡可听位置」与墙钟，
    /// 超过 0.15s 就把音频重定位回墙钟（一次轻微重定位，比持续错位好）。
    /// </summary>
    private void CheckAudioSync(double time)
    {
        var now = Stopwatch.GetTimestamp();
        if ((now - _lastAudioSyncCheck) / (double)Stopwatch.Frequency < AudioSyncCheckSeconds)
        {
            return;
        }

        _lastAudioSyncCheck = now;
        if (time < 2.0 || !_mixer.IsRunning)
        {
            return; // 起播/刚跳转的前几秒缓冲还没填满，位置不可信
        }

        if ((now - _lastAudioResyncTimestamp) / (double)Stopwatch.Frequency < AudioSyncCooldownSeconds)
        {
            return; // 冷却中：别把重定位当常规操作做（每次都要重开音频源）
        }

        var drift = _mixer.AudibleTime - time;
        if (Math.Abs(drift) <= AudioSyncToleranceSeconds)
        {
            return;
        }

        _lastAudioResyncTimestamp = now;
        Log($"音画漂移 {drift:+0.###;-0.###}s → 音频重定位到 {time:0.###}s");
        _mixer.Seek(time);
    }

    /// <summary>单轨调度：按墙钟对应的目标帧号消费帧（前面丢弃、最后一帧显示）。</summary>
    private void PumpTrack(TrackState state, double time)
    {
        var clip = FindActiveClip(state.Track, time);
        if (!ReferenceEquals(clip, state.ActiveClip))
        {
            var hadClip = state.ActiveClip != null;
            CloseTrack(state);
            if (clip != null && clip.Kind == "Video")
            {
                // 打开素材时直接定位到当前时刻对应的媒体时间（拖播放头/中途切入的片段不再从入点重播）。
                var seedMediaTime = clip.SourceTimeAt(time - clip.StartTime);
                state.Source = OpenSource(clip, seedMediaTime);
                state.SourceFps = state.Source?.SourceFps ?? 0;
                state.LastFrameIndex = (long)(seedMediaTime * (state.SourceFps > 0 ? state.SourceFps : 25));
            }

            state.ActiveClip = clip;
            if (hadClip && clip == null)
            {
                // 该轨已无活跃片段（片段删除/播完/轨道隐藏）：通知调用方隐藏图层，
                // 否则最后一帧一直冻结在画面上（时间轴里已经没有它）。
                try
                {
                    TrackCleared?.Invoke(state.Track);
                }
                catch
                {
                    // 回调异常不中断播放。
                }
            }
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

            var mediaTime = state.ActiveClip.SourceTimeAt(time - state.ActiveClip.StartTime);
            var fps = state.SourceFps > 0 ? state.SourceFps : 25.0;
            var targetN = (long)(mediaTime * fps);
            var behind = targetN - state.LastFrameIndex;
            if (behind <= 0)
            {
                return; // 还没到下一帧时间（低帧率素材隔拍显示，防快放）。
            }

            if (behind > CatchUpSeekFrames)
            {
                // 落后约 0.33s 以上（大跳 / 源帧率远高于显示帧率）：一次 seek 跳过，
                // 别顺序解码追赶 —— 实测顺序解一帧 11.6ms、seek+解一帧 76ms，
                // 而硬解 32 帧要 370ms；「解码丢弃」把整拍吃光就是画面卡死的根因。
                if (state.Source.SeekTo(mediaTime))
                {
                    state.LastFrameIndex = targetN;
                    state.SeekCount++;
                    return;
                }

                behind = MaxSkipPerTick + 1; // seek 失败退化为有限度的顺序快进。
            }

            // 顺序消费 behind 帧：前 behind-1 帧丢弃（每帧几毫秒），最后一帧发给 UI。
            // skip 封顶（见 MaxSkipPerTick）：单拍解码量可控，落后就靠下一拍的 seek 收敛。
            var skip = (int)Math.Min(behind - 1, MaxSkipPerTick);
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
                // ⚠️ **必须先把消费门复位、再调回调**。反过来写会丢信号：回调里把帧交给 UI 后，
                // UI 线程可能在 `_onFrame` 返回**之前**就消费完并调 MarkTrackConsumed（Set），
                // 随后这里的 Reset 会把这个 Set 抹掉 —— 此后没有任何人来 Set，
                // 该轨 `!Consumed.IsSet` 恒成立 = **永久停止投递**（片段不换就一直不动），
                // 表现就是「画面突然卡住、只剩声音在走」。2026-09-29 压力测试复现。
                state.Consumed.Reset();
                try
                {
                    _onFrame(frame, state.ActiveClip, state.Track);
                }
                catch
                {
                    // 调用方异常不中断播放。
                }

                // 只等极短时间：这个等待是「让 UI 在拍内消化完」的延迟优化，
                // 真正的防覆写判断在下一拍的 `!Consumed.IsSet → return`。
                // 别等久（曾 150ms）：pump 是逐轨串行跑的，3 轨 + UI 忙时一拍会被拖到 450ms，
                // 投递率直接掉到 1fps（压力测试实测）。UI 慢就让这一轨本拍不出帧即可。
                state.Consumed.Wait(5);
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
                // 同样的顺序要求：先复位消费门再回调（见上面视频分支的说明），否则丢信号后永久不投递。
                state.Consumed.Reset();
                try
                {
                    _onFrame(state.OverlayFrame, state.ActiveClip, state.Track);
                }
                catch
                {
                    // 调用方异常不中断播放。
                }

                state.Consumed.Wait(5);
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
        var tick = _tickCount > 0
            ? $"拍 均{_tickSumMs / _tickCount:0.#}ms/峰{_tickMaxMs:0.#}ms（{_tickCount} 次/{_tickSumMs / 1000:0.#}s） | "
            : string.Empty;
        _tickSumMs = 0;
        _tickMaxMs = 0;
        _tickCount = 0;
        var parts = _tracks.Select(t =>
            $"轨{t.Track}={t.ActiveClip?.Kind ?? "-"} 显{t.ShownFrames} 丢{t.SkippedFrames} seek{t.SeekCount}" +
            $"[{IdleReason(t)}]");
        Log($"t={CurrentTime:0.##}s | {tick}{string.Join(" | ", parts)}");
    }

    /// <summary>
    /// 该轨当前「没在投递帧」的原因（进日志，一行定位）。
    /// 「画面卡住只剩声音」这类问题必须一眼看出卡在哪一环：没片段 / 没解码器 / 到尾巴 /
    /// 在等 UI 消化上一帧（UI 线程忙）/ 解码追不上。
    /// </summary>
    private static string IdleReason(TrackState t)
    {
        if (t.ActiveClip == null)
        {
            return "此刻无片段";
        }

        if (t.ActiveClip.Kind != "Video")
        {
            return t.OverlaySent ? "覆盖层已发" : "覆盖层待发";
        }

        if (t.Source == null)
        {
            return "无解码器";
        }

        if (t.Eof)
        {
            return "源已播完";
        }

        return t.Consumed.IsSet ? "正常" : "等UI消化";
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
        // 先解除帧消费等待，让播放线程尽快走到退出点（它此刻可能正卡在 Consumed.Wait 里）。
        foreach (var state in _tracks)
        {
            state.Consumed.Set();
        }

        // ⚠️ **必须先等线程退出，再释放解码器**。旧实现先 `state.Source.Dispose()` 再 Join：
        // 线程可能正卡在 FFmpegVideoDecoder.ReadFrame 里读那一路解码器，native 上下文被 free 掉
        // 之后继续读 → `System.AccessViolationException`（0xc0000005，coreclr.dll）
        // 直接击穿进程、托管层完全抓不到，表现为「点渲染并应用 → 卡死 → 静默崩溃」
        // （点渲染第一步就是 StopPreview → 这里）。
        _worker?.Join(3000);
        if (_worker is { IsAlive: true })
        {
            // 线程还卡在解码 / seek 里（大文件慢解、机械盘）：此时**绝不能**释放解码器，
            // 交给线程自己在退出前收尾（见 Loop 退出路径），本轮只放掉与它无关的音频输出。
            Log("播放线程未在 3s 内退出：解码器交给线程自行释放（避免释放竞态击穿进程）");
            _worker = null;
            _mixer.Dispose();
            return;
        }

        _worker = null;
        foreach (var state in _tracks)
        {
            state.Source?.Dispose();
            state.Source = null;
        }

        _mixer.Dispose();
    }
}
