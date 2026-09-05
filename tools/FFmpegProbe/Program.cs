using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FFmpegProbe;

/// <summary>
/// FFmpeg 解码探针：验证用 FFmpeg（自带解码器）把 H.264 视频逐帧解码并转换为 BGRA。
/// 用法：FFmpegProbe.exe <视频路径> [帧数]
/// 完全绕开系统 Media Foundation（本机 MF 解码管道异常）。
/// </summary>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : @"D:\BSOD-MEMZ\为你的课表注入活力——ClassIslandInjector样式注入器发布！.mp4";
        var maxFrames = args.Length > 1 ? int.Parse(args[1]) : 10;
        Console.WriteLine($"=== FFmpeg 探针 路径={path} 帧数上限={maxFrames} ===");

        // FFmpeg dll 目录：优先环境变量 FFMPEG_DIR，其次本程序目录下的 ffmpeg\
        var root = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        }

        // 编码器检测：区分「仅解码精简包」（动态壁纸）与「解码+编码完整包」（渲染剪辑）。
        try
        {
            ffmpeg.RootPath = root;
            Console.WriteLine($"ffmpeg.RootPath = {root}");
            Console.WriteLine($"avcodec version: {ffmpeg.avcodec_version()}");
            foreach (var id in new[] { AVCodecID.AV_CODEC_ID_H264, AVCodecID.AV_CODEC_ID_HEVC, AVCodecID.AV_CODEC_ID_AAC })
            {
                var enc = ffmpeg.avcodec_find_encoder(id);
                Console.WriteLine($"编码器 {id}: {(enc == null ? "无" : Marshal.PtrToStringAnsi((IntPtr)enc->name))}");
            }

            if (args.Any(a => a is "--hw" or "hw"))
            {
                return ProbeHardware();
            }

            if (args.Any(a => a is "--enc" or "enc"))
            {
                return EncodeTest();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FFmpeg 加载失败：{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine("文件不存在！");
            return 1;
        }

        try
        {
            AVFormatContext* fmtCtx = null;
            var hr = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
            if (hr < 0)
            {
                Console.WriteLine($"avformat_open_input 失败 {hr}");
                return 1;
            }

            hr = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            Console.WriteLine($"avformat_find_stream_info = {hr}，流数量={fmtCtx->nb_streams}");

            // 容器元数据：时间基/帧率/帧数（诊断「渲染 fps 异常」用）。
            for (var i = 0; i < fmtCtx->nb_streams; i++)
            {
                var st = fmtCtx->streams[i];
                var tb = st->time_base;
                var afr = st->avg_frame_rate;
                var rfr = st->r_frame_rate;
                Console.WriteLine(
                    $"流{i}: time_base={tb.num}/{tb.den} avg_fps={(afr.den > 0 ? (double)afr.num / afr.den : 0):0.###} ({afr.num}/{afr.den}) " +
                    $"r_fps={(rfr.den > 0 ? (double)rfr.num / rfr.den : 0):0.###} ({rfr.num}/{rfr.den}) " +
                    $"帧数={st->nb_frames} 时长={st->duration * tb.num / (double)Math.Max(1, tb.den):0.###}s 起始={st->start_time}");
            }

            AVCodec* codec = null;
            var videoStream = ffmpeg.av_find_best_stream(fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            Console.WriteLine($"av_find_best_stream(video) = {videoStream} codec={(codec == null ? "null" : "ok")}");
            if (videoStream < 0 || codec == null)
            {
                Console.WriteLine("未找到视频流/解码器！");
                return 1;
            }

            Console.WriteLine($"解码器: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}（{Marshal.PtrToStringAnsi((IntPtr)codec->long_name)}）");
            var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null)
            {
                Console.WriteLine("avcodec_alloc_context3 失败");
                return 1;
            }

            var codecpar = fmtCtx->streams[videoStream]->codecpar;
            hr = ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar);
            Console.WriteLine($"avcodec_parameters_to_context = {hr} 尺寸={codecpar->width}x{codecpar->height} 像素格式={codecpar->format}");
            hr = ffmpeg.avcodec_open2(codecCtx, codec, null);
            Console.WriteLine($"avcodec_open2 = {hr}");
            if (hr < 0)
            {
                return 1;
            }

            // swscale：YUV → BGRA
            var outW = codecpar->width;
            var outH = codecpar->height;
            if (outW > 1280 && outH > 1280)
            {
                var scale = 1280.0 / Math.Max(outW, outH);
                outW = Math.Max(2, (int)(outW * scale) & ~1);
                outH = Math.Max(2, (int)(outH * scale) & ~1);
            }

            var sws = ffmpeg.sws_getContext(codecpar->width, codecpar->height, (AVPixelFormat)codecpar->format,
                outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (sws == null)
            {
                Console.WriteLine("sws_getContext 失败");
                return 1;
            }

            var pkt = ffmpeg.av_packet_alloc();
            var frame = ffmpeg.av_frame_alloc();
            var dst = new byte[outW * outH * 4];
            var decoded = 0;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var sw = new System.Diagnostics.Stopwatch();
            while (decoded < maxFrames)
            {
                var r = ffmpeg.av_read_frame(fmtCtx, pkt);
                if (r < 0)
                {
                    Console.WriteLine($"av_read_frame 结束 ({r})");
                    break;
                }

                if (pkt->stream_index == videoStream)
                {
                    sw.Restart();
                    var sr = ffmpeg.avcodec_send_packet(codecCtx, pkt);
                    if (sr < 0)
                    {
                        Console.WriteLine($"avcodec_send_packet 失败 {sr}");
                        break;
                    }

                    while (decoded < maxFrames && ffmpeg.avcodec_receive_frame(codecCtx, frame) >= 0)
                    {
                        fixed (byte* dstPtr = dst)
                        {
                            var dstStride = outW * 4;
                            var dstData = new byte*[] { dstPtr };
                            var dstLinesize = new int[] { dstStride };
                            ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, codecCtx->height, dstData, dstLinesize);
                        }

                        decoded++;
                        sw.Stop();
                        Console.WriteLine($"帧[{decoded}] {frame->width}x{frame->height} 格式={frame->format} 解码耗时={sw.ElapsedMilliseconds}ms 首像素=({dst[2]},{dst[1]},{dst[0]})");
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }

            elapsed.Stop();
            Console.WriteLine($"=== 共解码 {decoded} 帧，总耗时 {elapsed.ElapsedMilliseconds}ms，平均 {(decoded > 0 ? elapsed.ElapsedMilliseconds / (double)decoded : 0):F1}ms/帧 ===");

            ffmpeg.sws_freeContext(sws);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.avformat_close_input(&fmtCtx);
            return decoded > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"异常: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// 硬件能力探测（--hw）：枚举包内全部硬件编解码器 + 实测 hwdevice 创建 + 硬编解码器试开。
    /// 用于确认 BtbN 完整包在目标机器（学校核显/独显）上的 qsv/nvenc/amf/mf 可用性。
    /// </summary>
    private static unsafe int ProbeHardware()
    {
        Console.WriteLine();
        Console.WriteLine("== 硬件编解码器（包内注册的）==");
        void* opaque = null;
        AVCodec* it;
        while ((it = ffmpeg.av_codec_iterate(&opaque)) != null)
        {
            var name = Marshal.PtrToStringAnsi((IntPtr)it->name);
            if (name == null || !(name.Contains("_qsv") || name.Contains("_nvenc") || name.Contains("_nvdec") ||
                                  name.Contains("_cuvid") || name.Contains("_amf") || name.Contains("_mf") ||
                                  name.Contains("_d3d11va") || name.Contains("_dxva2")))
            {
                continue;
            }

            var kind = ffmpeg.av_codec_is_encoder(it) != 0 ? "编" : ffmpeg.av_codec_is_decoder(it) != 0 ? "解" : "?";
            Console.WriteLine($"  [{kind}] {name} ({(AVCodecID)it->id})");
        }

        Console.WriteLine();
        Console.WriteLine("== hwdevice 创建实测 ==");
        foreach (var type in new[]
                 {
                     AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                     AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                     AVHWDeviceType.AV_HWDEVICE_TYPE_OPENCL, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN
                 })
        {
            AVBufferRef* dev = null;
            var err = ffmpeg.av_hwdevice_ctx_create(&dev, type, null, null, 0);
            if (dev != null)
            {
                ffmpeg.av_buffer_unref(&dev);
            }

            Console.WriteLine($"  {type}: {(err == 0 ? "可用" : $"失败({err})")}");
        }

        Console.WriteLine();
        Console.WriteLine("== 硬件编码器试开（能 avcodec_open2 才算真可用）==");
        foreach (var name in new[] { "h264_qsv", "h264_nvenc", "h264_amf", "h264_mf" })
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null)
            {
                Console.WriteLine($"  {name}: 包内未注册");
                continue;
            }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null)
            {
                Console.WriteLine($"  {name}: 分配失败");
                continue;
            }

            ctx->width = 640;
            ctx->height = 360;
            ctx->time_base = new AVRational { num = 1, den = 30 };
            ctx->framerate = new AVRational { num = 30, den = 1 };
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            var err = ffmpeg.avcodec_open2(ctx, codec, null);
            Console.WriteLine($"  {name}: {(err == 0 ? "可打开 ✓（本机可用）" : $"打开失败({err})")}");
            ffmpeg.avcodec_free_context(&ctx);
        }

        Console.WriteLine();
        Console.WriteLine("== 硬件解码器试开（h264 系）==");
        foreach (var name in new[] { "h264_qsv", "h264_cuvid", "h264_mf" })
        {
            var codec = ffmpeg.avcodec_find_decoder_by_name(name);
            if (codec == null)
            {
                Console.WriteLine($"  {name}: 包内未注册");
                continue;
            }

            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            var err = ctx == null ? -1 : ffmpeg.avcodec_open2(ctx, codec, null);
            Console.WriteLine($"  {name}: {(err == 0 ? "可打开 ✓" : $"打开失败({err})")}");
            ffmpeg.avcodec_free_context(&ctx);
        }

        return 0;
    }

    /// <summary>
    /// 编码自测（--enc）：用与插件 FFmpegVideoEncoder 相同的逻辑（含 time_base 修复）
    /// 分别以硬件/软件编码器渲染一段 640x80@24fps 移动条纹测试视频，再回读元数据验证帧率。
    /// 蓝通道条纹移动，可肉眼检查红蓝是否互换。
    /// </summary>
    private static int EncodeTest()
    {
        const int w = 640, h = 80, fps = 24, frames = 96;
        var dir = Path.Combine(Path.GetTempPath(), "enc-test");
        Directory.CreateDirectory(dir);

        foreach (var hw in new[] { true, false })
        {
            var outPath = Path.Combine(dir, hw ? "enc-hw.mp4" : "enc-sw.mp4");
            Console.WriteLine($"\n=== 编码自测 {(hw ? "硬件（qsv→nvenc→amf→mf→软编回退）" : "软件 libx264")} ===");
            try
            {
                var used = EncodeClip(outPath, w, h, fps, frames, hw);
                Console.WriteLine($"实际编码器: {used}");
                ProbeFile(outPath);
                Console.WriteLine($"文件: {outPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"失败: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>按候选编码器列表逐个尝试，全部失败抛异常。</summary>
    private static string EncodeClip(string path, int w, int h, int fps, int frames, bool hw)
    {
        var candidates = hw
            ? new List<string?> { "h264_qsv", "h264_nvenc", "h264_amf", "h264_mf", null }
            : new List<string?> { null };
        Exception? last = null;
        foreach (var cand in candidates)
        {
            try
            {
                EncodeClipWith(cand, path, w, h, fps, frames);
                return cand ?? "libx264";
            }
            catch (Exception ex)
            {
                last = ex;
                Console.WriteLine($"  编码器 {cand ?? "libx264"} 不可用: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"无可用编码器: {last?.Message}");
    }

    /// <summary>与插件 FFmpegVideoEncoder 相同的编码流程（含 time_base 修复点）。</summary>
    private static unsafe void EncodeClipWith(string? hwName, string path, int w, int h, int fps, int frames)
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        SwsContext* sws = null;
        AVFrame* frame = null;
        AVPacket* pkt = null;
        try
        {
            var isHw = !string.IsNullOrWhiteSpace(hwName);
            var outFmt = isHw ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;

            var hr = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mp4", path);
            if (hr < 0 || fmtCtx == null)
            {
                throw new InvalidOperationException($"输出上下文失败({hr})");
            }

            AVCodec* codec;
            if (isHw)
            {
                codec = ffmpeg.avcodec_find_encoder_by_name(hwName);
                if (codec == null)
                {
                    throw new InvalidOperationException("包内未注册");
                }
            }
            else
            {
                codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
                if (codec == null)
                {
                    throw new InvalidOperationException("无 libx264");
                }
            }

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            codecCtx->width = w;
            codecCtx->height = h;
            codecCtx->time_base = new AVRational { num = 1, den = fps };
            codecCtx->framerate = new AVRational { num = fps, den = 1 };
            codecCtx->pix_fmt = outFmt;
            codecCtx->gop_size = 12;
            codecCtx->max_b_frames = 2;
            hr = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (hr < 0)
            {
                throw new InvalidOperationException($"打开失败({hr})");
            }

            // ★ 修复点 1：硬件编码器可能改写 time_base，open 后强制恢复 1/fps。
            codecCtx->time_base = new AVRational { num = 1, den = fps };

            var stream = ffmpeg.avformat_new_stream(fmtCtx, codec);
            if (stream == null)
            {
                throw new InvalidOperationException("建流失败");
            }

            stream->time_base = new AVRational { num = 1, den = fps };
            stream->avg_frame_rate = new AVRational { num = fps, den = 1 };
            stream->r_frame_rate = new AVRational { num = fps, den = 1 };
            hr = ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecCtx);
            if (hr < 0)
            {
                throw new InvalidOperationException($"参数失败({hr})");
            }

            hr = ffmpeg.avio_open(&fmtCtx->pb, path, ffmpeg.AVIO_FLAG_WRITE);
            if (hr < 0)
            {
                throw new InvalidOperationException($"打开输出失败({hr})");
            }

            hr = ffmpeg.avformat_write_header(fmtCtx, null);
            if (hr < 0)
            {
                throw new InvalidOperationException($"写头失败({hr})");
            }

            var tbStream = stream->time_base;

            sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA, w, h, outFmt,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)outFmt;
            frame->width = w;
            frame->height = h;
            ffmpeg.av_frame_get_buffer(frame, 32);
            pkt = ffmpeg.av_packet_alloc();

            // 生成移动条纹测试画面（B 通道条纹移动，肉眼可查红蓝互换）。
            var bgra = new byte[w * h * 4];
            for (var i = 0; i < frames; i++)
            {
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var o = (y * w + x) * 4;
                        bgra[o] = (byte)((x + i * 8) % 256);
                        bgra[o + 1] = (byte)(y % 256);
                        bgra[o + 2] = (byte)((x * 2 + i * 4) % 256);
                        bgra[o + 3] = 255;
                    }
                }

                fixed (byte* p = bgra)
                {
                    var srcData = new byte*[] { p };
                    var srcLinesize = new int[] { w * 4 };
                    ffmpeg.sws_scale(sws, srcData, srcLinesize, 0, h, frame->data, frame->linesize);
                }

                frame->pts = i;
                var sr = ffmpeg.avcodec_send_frame(codecCtx, frame);
                if (sr < 0)
                {
                    throw new InvalidOperationException($"送帧失败({sr})");
                }

                DrainTestPackets(codecCtx, pkt, fmtCtx, tbStream);
            }

            ffmpeg.avcodec_send_frame(codecCtx, null);
            DrainTestPackets(codecCtx, pkt, fmtCtx, tbStream);
            ffmpeg.av_write_trailer(fmtCtx);
        }
        finally
        {
            if (pkt != null)
            {
                ffmpeg.av_packet_free(&pkt);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
            }

            if (codecCtx != null)
            {
                ffmpeg.avcodec_free_context(&codecCtx);
            }

            if (fmtCtx != null)
            {
                ffmpeg.avformat_free_context(fmtCtx);
                fmtCtx = null;
            }
        }
    }

    /// <summary>取包并按修复后的时间基换算写入（★ 修复点 2）。</summary>
    private static unsafe void DrainTestPackets(AVCodecContext* codecCtx, AVPacket* pkt,
        AVFormatContext* fmtCtx, AVRational tbStream)
    {
        while (true)
        {
            var rr = ffmpeg.avcodec_receive_packet(codecCtx, pkt);
            if (rr == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rr == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (rr < 0)
            {
                throw new InvalidOperationException($"取包失败({rr})");
            }

            ffmpeg.av_packet_rescale_ts(pkt, codecCtx->time_base, tbStream);
            pkt->time_base = tbStream;
            ffmpeg.av_interleaved_write_frame(fmtCtx, pkt);
            ffmpeg.av_packet_unref(pkt);
        }
    }

    /// <summary>回读文件并打印流元数据（fps/time_base/帧数/时长）。</summary>
    private static unsafe void ProbeFile(string path)
    {
        AVFormatContext* fmtCtx = null;
        var hr = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
        if (hr < 0)
        {
            Console.WriteLine($"  回读失败({hr})");
            return;
        }

        ffmpeg.avformat_find_stream_info(fmtCtx, null);
        for (var i = 0; i < fmtCtx->nb_streams; i++)
        {
            var st = fmtCtx->streams[i];
            var afr = st->avg_frame_rate;
            var rfr = st->r_frame_rate;
            var tb = st->time_base;
            Console.WriteLine(
                $"  结果: time_base={tb.num}/{tb.den} avg_fps={(afr.den > 0 ? (double)afr.num / afr.den : 0):0.##} " +
                $"r_fps={(rfr.den > 0 ? (double)rfr.num / rfr.den : 0):0.##} 帧数={st->nb_frames} " +
                $"时长={st->duration * tb.num / (double)Math.Max(1, tb.den):0.###}s");
        }

        ffmpeg.avformat_free_context(fmtCtx);
    }
}
