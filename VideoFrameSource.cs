using System.Diagnostics;
using System.Threading;

namespace ClassIslandInjector;

/// <summary>
/// 用 FFmpeg（自带解码器）逐帧解码视频 → 32bpp BGRA，输出给调用方显示。
/// 纯 FFmpeg 路径，不再包含 Media Foundation（WMF）回退：MF 的解码管道在不同
/// 机器上差异大（RGB32/NV12 输出类型常被系统视频处理器拒绝），且曾经因
/// vtable 手动调用 SetCurrentMediaTypeByIndex 触发原生访问违规击穿宿主进程。
/// 因此现在仅支持 FFmpeg：启动时由 <see cref="FFmpegRuntime"/> 检测解码库是否
/// 存在，缺失时禁用视频背景选项并允许用户联机下载，解码失败则静默降级为无视频。
/// 后台线程以目标帧率驱动逐帧读取，通过 <see cref="Start"/> 传入的回调把帧交给
/// 调用方（注入器负责 Dispatcher.UIThread.Post + WriteableBitmap 更新）。
/// 内部用「UI 消费完毕」信号量同步，保证帧缓冲不被并发覆写；循环播放用
/// av_seek_frame 复位实现。所有调用均有 try/catch 兜底，失败静默降级。
/// </summary>
internal sealed class VideoFrameSource : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[video-fill] {message}");

    // ---- 实例状态 ----
    private FFmpegVideoDecoder? _ffmpeg;
    private Thread? _worker;
    private volatile bool _running;
    private int _width;
    private int _height;
    /// <summary>处理后 BGRA（top-down，alpha=0xFF）像素，交回调使用。</summary>
    private byte[]? _frameBuffer;
    private readonly ManualResetEventSlim _uiConsumed = new(true);
    private Action<VideoFrame>? _frameCallback;
    private double _targetFps = 24;
    private bool _loop = true;

    /// <summary>硬件解码器名（如 "h264_qsv"）；null = 软解。需在 Open 前设置，Open 失败自动回退软解。</summary>
    public string? HardwareDecoder { get; set; }

    /// <summary>
    /// 打开视频并建立 FFmpeg 解码（输出 BGRA，必要时缩放到 maxDimension）。
    /// 成功返回 true；任何错误返回 false（调用方降级为无视频）。
    /// </summary>
    public bool Open(string path, int maxDimension)
    {
        var ff = new FFmpegVideoDecoder { HardwareDecoder = HardwareDecoder };
        if (!ff.Open(path, maxDimension))
        {
            ff.Dispose();
            Log("FFmpeg 打开失败，无法解码视频");
            return false;
        }

        _ffmpeg = ff;
        _width = ff.OutputWidth;
        _height = ff.OutputHeight;
        _frameBuffer = new byte[_width * _height * 4];
        Log($"使用 FFmpeg 解码 {_width}x{_height}");
        return true;
    }

    /// <summary>启动后台解码线程。</summary>
    public void Start(Action<VideoFrame> frameCallback, double targetFps, bool loop)
    {
        _frameCallback = frameCallback;
        _targetFps = targetFps;
        _loop = loop;
        _running = true;
        _worker = new Thread(DecodeLoop) { IsBackground = true, Name = "VideoFill" };
        _worker.Start();
    }

    /// <summary>请求后台线程尽快退出（不 Join；资源由 Dispose 释放）。</summary>
    public void Stop() => _running = false;

    /// <summary>UI 线程处理完当前帧后调用，允许后台线程写入下一帧。</summary>
    public void MarkFrameConsumed() => _uiConsumed.Set();

    /// <summary>后台解码主循环：拉帧 → 限帧 → 等 UI 消费 → 回调。</summary>
    private void DecodeLoop()
    {
        var intervalMs = _targetFps > 0 ? (long)(1000.0 / _targetFps) : 0L;
        var sw = new Stopwatch();
        while (_running)
        {
            sw.Restart();
            if (!ReadNextFrame())
            {
                if (_loop && TryRestart())
                {
                    continue;
                }

                Log(_loop ? "播放结束（循环复位失败），停止" : "播放结束，停止");
                break;
            }

            if (intervalMs > 0)
            {
                var wait = intervalMs - sw.ElapsedMilliseconds;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }
            }

            // 等 UI 消费上一帧，避免覆写正在被读取的缓冲；超时则继续（可接受丢帧）。
            _uiConsumed.Wait(1000);
            _uiConsumed.Reset();
            if (!_running)
            {
                break;
            }

            try
            {
                _frameCallback?.Invoke(new VideoFrame(_frameBuffer!, _width, _height));
            }
            catch
            {
                _uiConsumed.Set();
            }
        }
    }

    /// <summary>读一帧并拷入 BGRA 帧缓冲；EOF / 错误返回 false。</summary>
    private bool ReadNextFrame() => TryReadFrame(out _);

    /// <summary>
    /// 按需拉取下一帧（供多轨播放器在统一时钟线程上逐轨拉帧，不启动自带解码线程）。
    /// 帧缓冲为复用实例，仅在下一次调用前有效；EOF / 错误返回 false 且 frame 为 null。
    /// </summary>
    public bool TryReadFrame(out VideoFrame? frame)
    {
        frame = null;
        if (_ffmpeg == null || !_ffmpeg.ReadFrame(out var pixels))
        {
            return false;
        }

        if (_frameBuffer == null || _frameBuffer.Length != pixels.Length)
        {
            _frameBuffer = new byte[pixels.Length];
        }

        Buffer.BlockCopy(pixels, 0, _frameBuffer, 0, pixels.Length);
        frame = new VideoFrame(_frameBuffer!, _width, _height);
        return true;
    }

    /// <summary>循环播放复位：seek 回开头并刷新解码器缓冲。</summary>
    private bool TryRestart() => _ffmpeg?.Restart() ?? false;

    /// <summary>跳到指定时间（秒），供片段入点裁剪（须在 Start 前调用）。</summary>
    public bool SeekTo(double seconds) => _ffmpeg?.SeekTo(seconds) ?? false;

    /// <summary>视频总时长（秒；0 表示未知）。</summary>
    public double Duration => _ffmpeg?.Duration ?? 0;

    /// <summary>源视频帧率（来自 avg_frame_rate；未知回退 25）。播放调度按它换算媒体帧时间，防高帧率素材被慢放。</summary>
    public double SourceFps => _ffmpeg?.SourceFps ?? 25;

    /// <summary>源视频原始分辨率（媒体信息展示用）。</summary>
    public (int Width, int Height) SourceSize => _ffmpeg is { } f ? (f.SourceWidth, f.SourceHeight) : (0, 0);

    public void Dispose()
    {
        _running = false;
        _uiConsumed.Set(); // 解除后台线程可能阻塞的等待
        _worker?.Join(800);
        _worker = null;
        _ffmpeg?.Dispose();
        _ffmpeg = null;
    }
}

/// <summary>一帧解码结果（像素为复用缓冲，仅回调执行期间有效）。</summary>
internal sealed class VideoFrame
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>每行字节数（= Width * 4，自上而下）。</summary>
    public int Stride => Width * 4;

    public VideoFrame(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }
}
