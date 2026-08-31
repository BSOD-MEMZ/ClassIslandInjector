using FFmpeg.AutoGen;

namespace ScheduleProbe;

/// <summary>
/// 播放调度离线探针：用真实视频 + 虚拟时钟（无 UI/无睡眠，等价于解码无限快的理想环境）
/// 分别模拟「新帧号调度」与「旧每拍一帧调度」，输出各自消费到的媒体时间与墙钟之比。
/// 新调度速度比应 ≈ 1.0（不慢放不快放）；旧调度在高帧率素材上会显著小于 1（慢放）。
/// 用法：ScheduleProbe.exe <视频路径> [模拟秒数=10]
/// 环境变量 FFMPEG_DIR 指向 FFmpeg DLL 目录（与 FFmpegProbe 相同）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: ScheduleProbe.exe <视频路径> [模拟秒数=10]");
            return 1;
        }

        var path = args[0];
        var seconds = args.Length > 1 ? double.Parse(args[1]) : 10.0;

        var root = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.WriteLine("请设置 FFMPEG_DIR 环境变量指向 FFmpeg DLL 目录");
            return 1;
        }

        ffmpeg.RootPath = root;

        // ---- 打开解码器（与 VideoFrameSource 同参：降采样 800） ----
        var decoder = new ClassIslandInjector.FFmpegVideoDecoder();
        if (!decoder.Open(path, 800))
        {
            Console.WriteLine("解码器打开失败");
            return 1;
        }

        var fps = decoder.SourceFps;
        Console.WriteLine($"源: {decoder.OutputWidth}x{decoder.OutputHeight} fps={fps:0.###} 时长={decoder.Duration:0.##}s");

        Console.WriteLine();
        Console.WriteLine("== 新调度（帧号消费，每拍消费 targetN-last 帧） ==");
        RunSchedule(decoder, seconds, fps, newStyle: true);

        // 重新打开解码器（重置帧指针）。
        decoder.Dispose();
        decoder = new ClassIslandInjector.FFmpegVideoDecoder();
        decoder.Open(path, 800);
        Console.WriteLine();
        Console.WriteLine("== 旧调度（每拍固定消费 1 帧） ==");
        RunSchedule(decoder, seconds, fps, newStyle: false);
        decoder.Dispose();
        return 0;
    }

    private static void RunSchedule(ClassIslandInjector.FFmpegVideoDecoder decoder, double seconds, double fps, bool newStyle)
    {
        const double tick = 1.0 / 24; // 编辑器预览目标帧率 24fps
        long lastFrameIndex = -1;
        var clock = 0.0;
        long shown = 0;
        long skipped = 0;
        var eof = false;

        while (clock < seconds && !eof)
        {
            var targetN = (long)(clock * fps);
            var behind = targetN - lastFrameIndex;
            if (behind > 0)
            {
                if (newStyle)
                {
                    // 新调度：前 behind-1 帧丢弃、最后一帧显示。
                    var skip = (int)Math.Min(behind - 1, 32);
                    for (var i = 0; i < skip; i++)
                    {
                        if (!decoder.ReadFrame(out _))
                        {
                            eof = true;
                            break;
                        }

                        skipped++;
                    }

                    if (!eof)
                    {
                        if (!decoder.ReadFrame(out _))
                        {
                            eof = true;
                        }
                        else
                        {
                            lastFrameIndex = Math.Min(lastFrameIndex + skip + 1, targetN);
                            shown++;
                        }
                    }
                }
                else
                {
                    // 旧调度：每拍最多消费 1 帧（上上版的模型）。
                    if (!decoder.ReadFrame(out _))
                    {
                        eof = true;
                    }
                    else
                    {
                        lastFrameIndex++;
                        shown++;
                    }
                }
            }
            // 虚拟墙钟：每拍固定推进一个拍间隔（理想解码环境，解码/显示耗时为 0）。
            clock += tick;
        }

        var mediaTime = (lastFrameIndex + 1) / fps;
        Console.WriteLine(
            $"模拟墙钟 {clock:0.##}s → 已消费 {lastFrameIndex + 1} 帧 = 媒体时间 {mediaTime:0.##}s | " +
            $"速度比 {mediaTime / clock:0.###}（1=不慢放不快放）| 显示 {shown} 丢弃 {skipped}");
    }
}
