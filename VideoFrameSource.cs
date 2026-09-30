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
    /// <summary>自解码开始累计的帧数（诊断用：与「声明时长 × 帧率」对比可识别残缺/索引缺失的文件）。</summary>
    private long _decodedFrames;
    /// <summary>是否已就「可解码帧数远少于声明时长」告警过（只报一次，避免刷日志）。</summary>
    private bool _shortFileWarned;

    /// <summary>硬件解码器名（如 "h264_qsv"）；null = 软解。需在 Open 前设置，Open 失败自动回退软解。</summary>
    public string? HardwareDecoder { get; set; }

    /// <summary>
    /// 每次循环复位（EOF → 回到开头）时回调。供「视频背景播放声音」把音频也拉回开头 ——
    /// 音频与视频是两条独立循环的链路，周期一旦不一致（目标帧率 ≠ 源帧率、音轨比画面长等），
    /// 不给这个同步点就会越滚越错位。
    /// </summary>
    public Action? OnLoopRestart { get; set; }

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

    /// <summary>是否处于挂起（不解码、不回调，解码器与位置都保留）。</summary>
    private volatile bool _suspended;

    /// <summary>
    /// 挂起 / 恢复解码线程：编辑器打开时挂起主界面底图用 —— 低配机器上「预览 + 底图」两路解码
    /// 加两处贴图会互相抢核，表现为预览卡顿、声音断续。挂起期间不消耗 CPU，恢复后从原位置继续。
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (!suspended)
        {
            _uiConsumed.Set(); // 唤醒等待中的线程
        }
    }

    /// <summary>UI 线程处理完当前帧后调用，允许后台线程写入下一帧。</summary>
    public void MarkFrameConsumed() => _uiConsumed.Set();

    /// <summary>后台解码主循环：拉帧 → 限帧 → 等 UI 消费 → 回调。</summary>
    private void DecodeLoop()
    {
        var intervalMs = _targetFps > 0 ? (long)(1000.0 / _targetFps) : 0L;
        var sw = new Stopwatch();
        while (_running)
        {
            if (_suspended)
            {
                Thread.Sleep(50); // 挂起：不动解码器，等恢复
                continue;
            }

            sw.Restart();
            if (!ReadNextFrame())
            {
                // 首次到达 EOF 时对账：实际可解出的帧数 vs「声明时长 × 源帧率」。
                // 残缺的 fMP4 / DASH 分片（moof+mdat 序列被截断、缺 sidx/mfra 索引）会在只解出
                // 极少数帧时就 EOF，而容器声明的 Duration 仍是完整时长 —— 于是底图“一直循环开头那几秒”，
                // 用户会以为是插件坏了。这里把结论写进日志，指明是素材本身的问题。
                WarnIfFileTruncated();
                if (_loop && TryRestart())
                {
                    _decodedFrames = 0; // 复位后重新计数（下一轮的告警判断才准）
                    // 循环回到开头：通知外部（音频侧跟着回零，否则两条独立循环会越滚越错位）。
                    try
                    {
                        OnLoopRestart?.Invoke();
                    }
                    catch
                    {
                        // 同步失败不影响画面。
                    }

                    continue;
                }

                Log(_loop ? "播放结束（循环复位失败），停止" : "播放结束，停止");
                break;
            }

            _decodedFrames++;

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

    /// <summary>
    /// 首次 EOF 时对账：若「实际解出的帧数」远少于「声明时长 × 源帧率」，说明素材本身残缺
    /// （典型：fMP4 / DASH 分片流被截断，moof+mdat 序列不完整、缺 sidx/mfra 索引）。
    /// 这时循环播放只会反复播那几秒开头 —— 用户看到的是「底图一直循环开头」，
    /// 但根因在素材而不是解码/循环逻辑（循环复位本身实测正常）。只告警一次，措辞明确指向素材。
    /// </summary>
    private void WarnIfFileTruncated()
    {
        if (_shortFileWarned || _ffmpeg == null)
        {
            return;
        }

        _shortFileWarned = true;
        var declared = _ffmpeg.Duration;
        var fps = _ffmpeg.SourceFps;
        if (declared <= 0 || fps <= 0.5 || _decodedFrames <= 0)
        {
            return;
        }

        var expected = declared * fps;
        // 低于声明帧数的 30% 才算异常（正常文件因首帧对齐 / 末尾不完整帧会有小幅出入）。
        if (_decodedFrames >= expected * 0.3)
        {
            return;
        }

        Log($"⚠️ 素材疑似残缺：容器声明时长 {declared:0.##}s（约 {expected:0} 帧 @{fps:0.##}fps），" +
            $"但实际只能解出 {_decodedFrames} 帧（约 {_decodedFrames / fps:0.##}s 画面）。" +
            "循环播放会一直重复这一小段开头；请换用完整的视频文件（该文件多半是未下载完的 DASH/fMP4 分片流）。");
    }

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
