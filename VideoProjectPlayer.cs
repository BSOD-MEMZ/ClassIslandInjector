using System.Diagnostics;
using System.Threading;

namespace ClassIslandInjector;

/// <summary>
/// 多轨视频工程播放器：以统一时钟（targetFps）驱动，每个轨道独立解码，
/// 同一时刻所有轨道上处于活跃区间的片段同时播放，由调用方叠放合成
/// （轨道号越大越靠上层）。片段在 [StartTime, StartTime+Duration) 区间内活跃，
/// 打开素材并跳到入点后逐帧拉取；播完整条时间轴后回到开头循环。
/// 帧回调在播放器线程触发，调用方负责把像素复制到自己的缓冲；播放器在收到
/// <see cref="MarkTrackConsumed"/> 之前不会覆写该轨道的帧缓冲（防 UI 异步复制竞态），
/// 并应把 UI 更新 Post 到 UI 线程。
/// 所有解码器的开关都在播放器线程串行执行，避免多线程释放/重建解码器的竞态
/// （取代旧版"解码线程回调里 Post 切换"的方案，从根上消除级联重开问题）。
/// </summary>
internal sealed class VideoProjectPlayer : IDisposable
{
    private readonly List<VideoClip> _clips;
    private readonly double _duration;
    private readonly int _maxDimension;
    private readonly int _targetFps;
    private readonly Action<VideoFrame, VideoClip, int> _onFrame;

    private readonly object _sync = new();
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _disposed;
    // 时间字段仅由播放线程写；UI 读取走 lock（C# 不允许 volatile double，用锁保证可见性）。
    private double _time;
    private volatile bool _seekRequested;
    private double _seekTo;
    private TrackState[] _tracks = [];

    private sealed class TrackState
    {
        public int Track;
        public VideoFrameSource? Source;
        public VideoClip? ActiveClip;
        /// <summary>UI 消费完本轨道上一帧后 Set，播放器据此才拉下一帧（防缓冲覆写）。</summary>
        public readonly ManualResetEventSlim Consumed = new(true);
    }

    public VideoProjectPlayer(VideoProject project, int maxDimension, int targetFps,
        Action<VideoFrame, VideoClip, int> onFrame)
    {
        // 浅拷贝片段列表：播放器只枚举自己的列表（编辑器增删/拖拽不破坏播放），
        // 但片段对象与工程共享引用，属性编辑（入出点/变换）可实时反映到预览。
        _clips = project.Clips.ToList();
        _duration = project.Duration;
        _maxDimension = maxDimension;
        _targetFps = Math.Max(1, targetFps);
        _onFrame = onFrame;
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

        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "VideoProject" };
        _worker.Start();
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
                return _time;
            }
        }
    }

    /// <summary>暂停播放（线程退出、时间冻结在当前位置），可 <see cref="Resume"/> 恢复。</summary>
    public void Pause()
    {
        _running = false;
        foreach (var state in _tracks)
        {
            state.Consumed.Set(); // 解除可能的帧消费等待
        }
    }

    /// <summary>从当前位置恢复播放（须先 <see cref="Pause"/>；时间不会自动复位）。</summary>
    public void Resume()
    {
        if (_running || _disposed || _duration <= 0)
        {
            return;
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

    /// <summary>主循环：统一时钟驱动各轨道同步拉帧。</summary>
    private void Loop()
    {
        var intervalMs = (long)(1000.0 / _targetFps);
        var sw = new Stopwatch();
        while (_running && !_disposed)
        {
            sw.Restart();
            var restartTracks = false;
            lock (_sync)
            {
                if (_seekRequested)
                {
                    // 跳转：复位时钟，下一拍按新时间重新打开各轨解码器。
                    _seekRequested = false;
                    _time = _seekTo;
                    restartTracks = true;
                }
                else if (_time >= _duration)
                {
                    // 播完整个时间轴：复位时钟，下一拍重新打开。
                    _time = 0;
                    restartTracks = true;
                }
            }

            if (restartTracks)
            {
                foreach (var state in _tracks)
                {
                    CloseTrack(state);
                }
            }

            var time = _time;
            foreach (var state in _tracks)
            {
                var clip = FindActiveClip(state.Track, time);
                if (!ReferenceEquals(clip, state.ActiveClip))
                {
                    CloseTrack(state);
                    if (clip != null)
                    {
                        state.Source = OpenSource(clip);
                    }

                    state.ActiveClip = clip;
                }

                if (state.Source != null && state.ActiveClip != null &&
                    state.Source.TryReadFrame(out var frame) && frame != null)
                {
                    try
                    {
                        _onFrame(frame, state.ActiveClip, state.Track);
                    }
                    catch
                    {
                        // 调用方异常不中断播放。
                    }

                    // 等 UI 消费完本轨道上一帧再覆写缓冲（超时继续，可接受丢帧）。
                    state.Consumed.Reset();
                    state.Consumed.Wait(300);
                }
            }

            if (intervalMs > 0)
            {
                var wait = intervalMs - sw.ElapsedMilliseconds;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }
            }

            // 时钟按整个循环拍的真实耗时推进（含睡眠）：解码/消费慢时播放变慢，但播放头与画面帧
            // 严格同步（原先每拍固定 +1/帧长，帧率不足时播放头会超前于画面，看起来"走到几秒才有画面"）。
            lock (_sync)
            {
                _time += sw.ElapsedMilliseconds / 1000.0;
            }
        }
    }

    /// <summary>关闭并释放轨道解码器（复位消费信号量，避免挂起）。</summary>
    private void CloseTrack(TrackState state)
    {
        state.Source?.Dispose();
        state.Source = null;
        state.Consumed.Set();
    }

    /// <summary>打开素材并跳到入点；失败返回 null（该轨道本周期静默无画面）。</summary>
    private VideoFrameSource? OpenSource(VideoClip clip)
    {
        try
        {
            var source = new VideoFrameSource();
            if (!source.Open(clip.SourcePath, _maxDimension))
            {
                source.Dispose();
                return null;
            }

            if (clip.InPoint > 0)
            {
                source.SeekTo(clip.InPoint);
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
