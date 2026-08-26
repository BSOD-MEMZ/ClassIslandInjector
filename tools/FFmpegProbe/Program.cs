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
        if (!File.Exists(path))
        {
            Console.WriteLine("文件不存在！");
            return 1;
        }

        // FFmpeg dll 目录：优先环境变量 FFMPEG_DIR，其次本程序目录下的 ffmpeg\
        var root = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        }

        if (!Directory.Exists(root))
        {
            Console.WriteLine($"未找到 FFmpeg dll 目录（设置 FFMPEG_DIR 或放到 {root}）");
            return 1;
        }

        ffmpeg.RootPath = root;
        Console.WriteLine($"ffmpeg.RootPath = {root}");
        Console.WriteLine($"avcodec version: {ffmpeg.avcodec_version()}");

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
                outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
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
}
