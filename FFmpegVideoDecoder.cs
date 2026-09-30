using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 用 FFmpeg（自带解码器）逐帧解码视频 → 32bpp BGRA（自上而下、alpha=0xFF）。
/// 不再回退系统 Media Foundation（本机 MF 解码管道异常，且其 vtable 手动调用曾触发
/// 原生崩溃）；解码库缺失时由 <see cref="FFmpegRuntime"/> 检测并引导下载。
/// 纯拉帧模型：<see cref="ReadFrame"/> 每次返回一帧，EOF 由调用方决定循环（Restart）。
/// </summary>
internal sealed unsafe class FFmpegVideoDecoder : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[video-ffmpeg] {message}");

    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _codecCtx;
    private SwsContext* _sws;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private int _streamIndex = -1;
    private int _outW;
    private int _outH;
    private byte[]? _bgra;

    /// <summary>视频总时长（秒，来自容器 duration；0 表示未知）。</summary>
    public double Duration { get; private set; }

    /// <summary>源视频帧率（来自 avg_frame_rate；异常时回退 25，供转码保持时长）。</summary>
    public double SourceFps { get; private set; } = 25;

    /// <summary>硬件解码器名（如 "h264_qsv"）；null/空 = 软解。Open 时尝试，失败自动回退软解。</summary>
    public string? HardwareDecoder { get; set; }

    /// <summary>源视频分辨率（解码前原始尺寸，供媒体信息展示）。</summary>
    public int SourceWidth { get; private set; }

    /// <summary>源视频原始高度。</summary>
    public int SourceHeight { get; private set; }

    /// <summary>硬件解码是否生效（Open 后可查）。</summary>
    public bool HardwareActive { get; private set; }

    private AVBufferRef* _hwDevice;
    private AVFrame* _hwFrame;
    private AVPixelFormat _swsInFormat;
    /// <summary>硬件解码是否激活（Open 内部状态，失败回退软解时清除）。</summary>
    private bool _hwActive;
    /// <summary>ReadFrame 与 Dispose 的互斥门：防止「解码中释放」造成 native 上下文被 free 后继续读。</summary>
    private readonly object _gate = new();
    /// <summary>已释放（Dispose 幂等 + ReadFrame 早退，避免踩已 free 的指针）。</summary>
    private bool _disposed;

    /// <summary>
    /// 已送过 NULL 包（flush）让解码器吐出 B 帧重排缓冲里的延迟帧。
    /// <para>
    /// HEVC/H.264 默认开帧级多线程解码，解码器会**延迟若干帧**才吐第一个输出帧
    /// （重排缓冲）。所以读到文件尾之后不能立刻判 EOF —— 必须先
    /// <c>avcodec_send_packet(ctx, NULL)</c> 把缓冲里剩下的帧排空。
    /// 少了这一步就会「文件明明有 5220 帧，却只解出开头几十帧」。
    /// </para>
    /// </summary>
    private bool _draining;

    /// <summary>输出帧宽（BGRA）。</summary>
    public int OutputWidth => _outW;

    /// <summary>输出帧高（BGRA）。</summary>
    public int OutputHeight => _outH;

    /// <summary>
    /// 打开视频并准备解码（输出降采样到 maxDimension 内的 BGRA）。
    /// 成功返回 true；任何错误返回 false（调用方降级）。
    /// </summary>
    public bool Open(string path, int maxDimension)
    {
        try
        {
            // open_input 需要「指针的指针」，字段不能取地址，用局部变量。
            AVFormatContext* fmtCtx = null;
            var hr = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
            _fmtCtx = fmtCtx;
            if (hr < 0)
            {
                Log($"avformat_open_input 失败 {hr}");
                return false;
            }

            hr = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
            if (hr < 0)
            {
                Log($"avformat_find_stream_info 失败 {hr}");
                return false;
            }

            AVCodec* codec = null;
            _streamIndex = ffmpeg.av_find_best_stream(_fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (_streamIndex < 0 || codec == null)
            {
                Log("未找到视频流 / 解码器");
                return false;
            }

            var name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "h264";
            var softCodec = codec;
            var hwName = HardwareDecoder;
            if (string.Equals(hwName, "auto", StringComparison.OrdinalIgnoreCase))
            {
                // 自动硬解：按流实际编码类型选 D3D11VA 解码器（h264_d3d11va / hevc_d3d11va 等）。
                // 包未编译硬解 / 驱动不支持时 avcodec_find_decoder_by_name 返回 null → 走下方软解回退。
                hwName = $"{name}_d3d11va";
            }

            if (!string.IsNullOrWhiteSpace(hwName))
            {
                // 硬件解码尝试：包内有同名解码器且流编码类型匹配时用之；hwdevice 创建失败则回退软解。
                var hwCodec = ffmpeg.avcodec_find_decoder_by_name(hwName);
                AVBufferRef* dev = null;
                if (hwCodec != null && hwCodec->id == codec->id &&
                    ffmpeg.av_hwdevice_ctx_create(&dev, HwDeviceTypeOf(hwName), null, null, 0) == 0 &&
                    dev != null)
                {
                    _hwDevice = dev;
                    _hwActive = true;
                    codec = hwCodec;
                    name = hwName;
                    Log($"尝试硬件解码器 {hwName}");
                }
                else
                {
                    if (dev != null)
                    {
                        ffmpeg.av_buffer_unref(&dev);
                    }

                    Log($"硬件解码器 {hwName} 不可用，回退软解");
                }
            }

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
            {
                Log("avcodec_alloc_context3 失败");
                return false;
            }

            var cp = _fmtCtx->streams[_streamIndex]->codecpar;
            hr = ffmpeg.avcodec_parameters_to_context(_codecCtx, cp);
            if (hr < 0)
            {
                Log($"avcodec_parameters_to_context 失败 {hr}");
                return false;
            }

            if (_hwActive && _hwDevice != null)
            {
                // 硬件解码必须把 hwdevice 交给解码器上下文。
                _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            }

            hr = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (hr < 0 && _hwActive)
            {
                // 硬件解码器打开失败（驱动/能力不足）：重建上下文回退软解。
                Log($"avcodec_open2 失败 {hr}（硬件解码器={name}），回退软解");
                _hwActive = false;
                var stale = _codecCtx;
                ffmpeg.avcodec_free_context(&stale);
                _codecCtx = null;
                codec = softCodec;
                name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "h264";
                _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecCtx == null)
                {
                    Log("回退软解时 avcodec_alloc_context3 失败");
                    return false;
                }

                hr = ffmpeg.avcodec_parameters_to_context(_codecCtx, cp);
                if (hr < 0)
                {
                    Log($"回退软解 avcodec_parameters_to_context 失败 {hr}");
                    return false;
                }

                hr = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            }

            if (hr < 0)
            {
                Log($"avcodec_open2 失败 {hr}（解码器={name}）");
                return false;
            }

            HardwareActive = _hwActive;

            var srcW = cp->width;
            var srcH = cp->height;
            SourceWidth = srcW;
            SourceHeight = srcH;
            _outW = srcW;
            _outH = srcH;
            if (maxDimension > 0 && Math.Max(srcW, srcH) > maxDimension)
            {
                var scale = (double)maxDimension / Math.Max(srcW, srcH);
                _outW = Math.Max(2, (int)(srcW * scale)) & ~1;
                _outH = Math.Max(2, (int)(srcH * scale)) & ~1;
            }

            // ⚠️ 输出尺寸必须向上对齐到 16（H.264 宏块）：解码器内部的 coded size 会按 16 对齐
            // （412x68 → 416x80），而显示尺寸不是 16 的倍数时，下游「WriteableBitmap + 主界面
            // Image 缩放」这条渲染路径会踩到 coreclr 级访问违规 —— 实测 412x68 必崩、416x80 正常。
            // 向上对齐最多放大 15 像素，视觉上无差别（最终显示尺寸由主界面 Image 缩放决定）。
            _outW = AlignUp16(_outW);
            _outH = AlignUp16(_outH);

            // 硬解时输出帧是 GPU 内存（QSV 等），回读后为 NV12；软解时为源像素格式。
            _swsInFormat = _hwActive ? AVPixelFormat.AV_PIX_FMT_NV12 : (AVPixelFormat)cp->format;
            _sws = ffmpeg.sws_getContext(srcW, srcH, _swsInFormat,
                _outW, _outH, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_sws == null)
            {
                Log("sws_getContext 失败");
                return false;
            }

            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _bgra = new byte[_outW * _outH * 4];
            // 源帧率：转码压缩时按它编码，保证时长一致。
            var fr = _fmtCtx->streams[_streamIndex]->avg_frame_rate;
            SourceFps = fr.num > 0 && fr.den > 0
                ? Math.Clamp((double)fr.num / fr.den, 1.0, 120.0)
                : 25;
            // 总时长：容器 duration → 视频流 duration → 帧数/帧率，逐级兜底。
            // 部分封装（网络下载的 mp4/flv 等）没有容器级时长，只有流级；都没有时用
            // 帧数估算——否则视频编辑器拖入片段探测不到原时长，只能落到 10 秒默认。
            Duration = _fmtCtx->duration > 0 ? _fmtCtx->duration / 1000000.0 : 0;
            var stream = _fmtCtx->streams[_streamIndex];
            if (Duration <= 0 && stream->duration > 0)
            {
                Duration = stream->duration * stream->time_base.num / (double)stream->time_base.den;
            }

            if (Duration <= 0 && stream->nb_frames > 0)
            {
                Duration = stream->nb_frames / SourceFps;
            }

            Log($"已打开 {path}: {srcW}x{srcH} → {_outW}x{_outH}（解码器={name}）");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Open 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读取一帧解码结果。返回 true 时 <paramref name="pixels"/> 为复用的 BGRA 缓冲
    /// （宽 = OutputWidth，高 = OutputHeight，stride = 宽 × 4）。真正的 EOF 返回 false。
    /// <para>
    /// ⚠️ **必须严格按 FFmpeg 的 send/receive 协议写，不能「喂一个包就取一帧、取不到就判 EOF」**。
    /// 这条链路上踩过一个很隐蔽的坑（2026-09-30，用户素材 5220 帧只解出 30 帧、表现成「底图反复播开头」）：
    /// </para>
    /// <list type="number">
    /// <item><c>avcodec_send_packet</c> 返回 <c>AVERROR(EAGAIN)</c> 的含义是「解码器输入队列满了，
    /// 先把已就绪的帧取走再回来喂」，**不是**致命错误。旧代码把它当致命错误直接 <c>return false</c>，
    /// 于是解码在开头几十帧处被误判成 EOF。</item>
    /// <item>多线程解码有重排缓冲：读到文件尾时解码器里还压着若干帧，必须送 NULL 包
    /// （<c>avcodec_send_packet(ctx, NULL)</c>）flush 一次才能取完。</item>
    /// <item>取帧要循环取到 <c>AVERROR(EAGAIN)</c> 为止 —— 一个包可能产出多帧。</item>
    /// </list>
    /// </summary>
    public bool ReadFrame(out byte[] pixels)
    {
        pixels = _bgra!;
        // 与 Dispose 互斥：Dispose 可能在另一线程（UI 线程停止预览）与解码线程并发。
        // 没有这道门就可能「进入本方法 → native 上下文被 free → 继续读」→
        // AccessViolationException(0xc0000005) 击穿进程（托管层抓不到，表现为静默崩溃）。
        lock (_gate)
        {
            return ReadFrameLocked(out pixels);
        }
    }

    private bool ReadFrameLocked(out byte[] pixels)
    {
        pixels = _bgra!;
        if (_disposed || _fmtCtx == null || _codecCtx == null || _sws == null || _pkt == null || _frame == null || _bgra == null)
        {
            return false;
        }

        while (true)
        {
            // ① 先看解码器里有没有已就绪的帧（上一包多产出的、或 flush 排空出来的）。
            if (TryReceiveFrame(out pixels))
            {
                return true;
            }

            // ② 已 flush 过还取不到帧 → 这才是真正的结尾（所有延迟帧都吐完了）。
            if (_draining)
            {
                return false;
            }

            // ③ 需要更多数据：读下一个包。
            var r = ffmpeg.av_read_frame(_fmtCtx, _pkt);
            if (r < 0)
            {
                // EOF / 读取错误：送 NULL 包 flush，把重排缓冲里的延迟帧排空（见方法注释第 2 条）。
                _draining = true;
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                continue;
            }

            if (_pkt->stream_index != _streamIndex)
            {
                // 非目标流（音频等）：丢弃继续读。**必须 unref**，否则引用计数泄漏。
                ffmpeg.av_packet_unref(_pkt);
                continue;
            }

            var sr = ffmpeg.avcodec_send_packet(_codecCtx, _pkt);
            ffmpeg.av_packet_unref(_pkt); // send 已持有自己的引用，这里可安全释放。
            if (sr < 0 && sr != Eagain)
            {
                // 真错误（EAGAIN 除外：它只表示「先取帧」，下一轮循环会先 receive）。
                Log($"avcodec_send_packet 失败 {sr}");
                return false;
            }

            // 回到循环顶部取帧。
        }
    }

    /// <summary><c>AVERROR(EAGAIN)</c>：解码器「再试一次」信号，不是错误。</summary>
    private static int Eagain => ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>
    /// 从解码器取一个输出帧并 sws_scale 到 <see cref="_bgra"/>。
    /// 无就绪帧（EAGAIN/EOF）返回 false；硬件帧回读失败会跳过该帧继续取。
    /// </summary>
    private unsafe bool TryReceiveFrame(out byte[] pixels)
    {
        pixels = _bgra!;
        while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
        {
            AVFrame* src;
            if (_hwActive)
            {
                // GPU 帧回读到系统内存（NV12），后续 sws 与软解同一管线。
                if (_hwFrame == null)
                {
                    _hwFrame = ffmpeg.av_frame_alloc();
                }

                if (ffmpeg.av_hwframe_transfer_data(_hwFrame, _frame, 0) < 0)
                {
                    // 单帧回读失败（GPU 瞬时故障）跳过该帧，不中断整条播放。
                    Log("av_hwframe_transfer_data 失败，跳过该帧");
                    continue;
                }

                src = _hwFrame;
            }
            else
            {
                src = _frame;
            }

            fixed (byte* dst = _bgra)
            {
                var dstData = new byte*[] { dst };
                var dstLinesize = new int[] { _outW * 4 };
                ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, src->height, dstData, dstLinesize);
            }

            return true;
        }

        return false;
    }

    /// <summary>在互斥门下执行（与 Dispose 互斥；native 调用期间绝不允许释放上下文）。</summary>
    private bool RunGated(Func<bool> action)
    {
        lock (_gate)
        {
            return _disposed ? false : action();
        }
    }

    /// <summary>向上对齐到 16 的倍数（H.264 宏块尺寸）。见 <see cref="Open"/> 里的说明。</summary>
    private static int AlignUp16(int value) => (value + 15) / 16 * 16;

    /// <summary>硬件解码器名 → hwdevice 类型。</summary>
    private static AVHWDeviceType HwDeviceTypeOf(string name) => name switch
    {
        var n when n.Contains("qsv") => AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
        var n when n.Contains("cuvid") || n.Contains("nvdec") => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
        _ => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
    };

    /// <summary>循环播放：seek 回开头并刷新解码器缓冲。</summary>
    public bool Restart() => RunGated(RestartLocked);

    private bool RestartLocked()
    {
        if (_disposed || _fmtCtx == null || _codecCtx == null)
        {
            return false;
        }

        try
        {
            var hr = ffmpeg.av_seek_frame(_fmtCtx, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (hr < 0)
            {
                Log($"av_seek_frame 失败 {hr}");
                return false;
            }

            ffmpeg.avcodec_flush_buffers(_codecCtx);
            _draining = false; // 复位后重新开始喂包，解除 flush 状态。
            return true;
        }
        catch (Exception ex)
        {
            Log($"Restart 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>跳到指定时间点（秒），供视频片段入点裁剪。用视频流的 time_base 换算 timestamp。</summary>
    public bool SeekTo(double seconds) => RunGated(() => SeekToLocked(seconds));

    private bool SeekToLocked(double seconds)
    {
        if (_disposed || _fmtCtx == null || _codecCtx == null || _streamIndex < 0)
        {
            return false;
        }

        try
        {
            if (seconds <= 0)
            {
                return RestartLocked();
            }

            var stream = _fmtCtx->streams[_streamIndex];
            var tb = stream->time_base;
            var tbSeconds = tb.num / (double)tb.den;
            if (tbSeconds <= 0)
            {
                return Restart();
            }

            var ts = (long)(seconds / tbSeconds);
            var hr = ffmpeg.av_seek_frame(_fmtCtx, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (hr < 0)
            {
                Log($"av_seek_frame 失败 {hr}（秒={seconds:0.###}）");
                return false;
            }

            ffmpeg.avcodec_flush_buffers(_codecCtx);
            _draining = false; // seek 后重新喂包，解除 flush 状态。
            return true;
        }
        catch (Exception ex)
        {
            Log($"SeekTo 异常: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        // 与 ReadFrame 互斥（见 ReadFrame 的说明）：持有门期间释放 native 上下文，
        // 就不会出现「解码线程正在用 → 这边 free 掉」的释放竞态。
        lock (_gate)
        {
            if (_disposed)
            {
                return; // 幂等：重复释放（玩家与帧源都可能调到）直接返回
            }

            _disposed = true;
            DisposeLocked();
        }
    }

    private void DisposeLocked()
    {
        try
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            // free 函数需要「指针的指针」，字段不能取地址，用局部变量传递。
            if (_frame != null)
            {
                var f = _frame;
                ffmpeg.av_frame_free(&f);
                _frame = null;
            }

            if (_pkt != null)
            {
                var p = _pkt;
                ffmpeg.av_packet_free(&p);
                _pkt = null;
            }

            if (_codecCtx != null)
            {
                var c = _codecCtx;
                ffmpeg.avcodec_free_context(&c);
                _codecCtx = null;
            }

            if (_hwFrame != null)
            {
                var f = _hwFrame;
                ffmpeg.av_frame_free(&f);
                _hwFrame = null;
            }

            if (_hwDevice != null)
            {
                var d = _hwDevice;
                ffmpeg.av_buffer_unref(&d);
                _hwDevice = null;
            }

            _hwActive = false;

            if (_fmtCtx != null)
            {
                var ctx = _fmtCtx;
                ffmpeg.avformat_close_input(&ctx);
                _fmtCtx = null;
            }
        }
        catch
        {
            // 释放错误忽略
        }
    }
}
