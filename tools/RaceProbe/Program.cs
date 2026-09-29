using System.Diagnostics;
using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>诊断日志桩（探针打到控制台）。</summary>
internal static class DiagnosticLog
{
    public static bool Enabled { get; set; } = true;
    public static string? CrashLogPath { get; set; }

    public static void Write(string? path, string message) => Console.WriteLine($"  [log] {message}");
}

/// <summary>FFmpeg 运行时桩：探针自己在 Main 里设置 ffmpeg.RootPath。</summary>
internal static class FFmpegRuntime
{
    public static bool IsAvailable => true;
    public static bool EnsureLoaded() => true;
}

/// <summary>
/// 「解码中释放解码器」竞态回归探针（tools/ 下独立项目，不参与插件主项目编译）。
/// <para>
/// 复现的真实事故：点编辑器「渲染并应用」→ 第一步 StopPreview → VideoProjectPlayer.Dispose
/// 释放各轨解码器，而播放线程此刻正卡在 FFmpegVideoDecoder.ReadFrame 里；
/// 旧实现「先 free native 上下文、再 Join 线程」且 ReadFrame 与 Dispose 无互斥 →
/// 继续读已释放的 AVCodecContext/SwsContext → System.AccessViolationException（0xc0000005）
/// **直接击穿进程**，托管层完全抓不到（宿主表现为「卡死 + 静默崩溃」，事件日志里
/// 才是 coreclr.dll / sws_scale / FFmpegVideoDecoder.ReadFrame）。
/// </para>
/// <para>
/// 用法：RaceProbe.exe [视频路径] [FFmpeg库目录]
/// 退出码 0 = 释放与解码正确互斥；139/其它 = 崩了（回归）。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // stress：[视频] [音频] [FFmpeg库目录] → 预览播放压力测试（把卡顿/音画不同步量成数字）。
        if (args.Length > 0 && args[0] == "--stress")
        {
            Console.WriteLine("=== 预览播放压力测试 ===");
            return Stress.Run(
                args.Length > 3 ? args[3] : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg",
                args.Length > 1 ? args[1] : @"D:\Downloads\injector-video.mp4",
                args.Length > 2 ? args[2] : @"D:\Downloads\bgm.mp3");
        }

        // --audio：[视频] [FFmpeg库目录] [输出目录] → 「渲染带音频」端到端回归。
        if (args.Length > 0 && args[0] == "--audio")
        {
            Console.WriteLine("=== 「渲染带音频」端到端回归探针 ===");
            return AudioRenderCheck.Run(
                args.Length > 1 ? args[1] : @"D:\Downloads\injector-video.mp4",
                args.Length > 2 ? args[2] : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg",
                args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "raceprobe-audio"));
        }

        var video = args.Length > 0 ? args[0] : @"D:\Downloads\injector-video.mp4";
        var libDir = args.Length > 1
            ? args[1]
            : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";

        Console.WriteLine("=== 解码 / 释放竞态回归探针 ===");
        if (!File.Exists(video))
        {
            Console.WriteLine($"× 视频文件不存在: {video}");
            return 2;
        }

        ffmpeg.RootPath = libDir;
        Console.WriteLine($"视频: {Path.GetFileName(video)}  FFmpeg: {ffmpeg.av_version_info()}");

        const int rounds = 5;
        for (var round = 1; round <= rounds; round++)
        {
            var decoder = new FFmpegVideoDecoder();
            if (!decoder.Open(video, 640))
            {
                Console.WriteLine("× 打开失败（无视频轨 / 解码器不可用）");
                return 2;
            }

            for (var i = 0; i < 5; i++)
            {
                decoder.ReadFrame(out _); // 预热
            }

            var stop = 0;
            long reads = 0;
            var readerEx = "无";
            var thread = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        if (!decoder.ReadFrame(out _))
                        {
                            break; // 已释放 / EOF：正常退出
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex)
                {
                    readerEx = ex.GetType().Name + ": " + ex.Message;
                }
            })
            {
                IsBackground = true
            };
            thread.Start();
            Thread.Sleep(120); // 让读线程正处在解码途中
            var sw = Stopwatch.StartNew();
            decoder.Dispose();
            sw.Stop();
            Volatile.Write(ref stop, 1);
            var joined = thread.Join(2000);
            var afterDisposeReturnsFalse = !decoder.ReadFrame(out _);

            Console.WriteLine($"第 {round} 轮: Dispose 阻塞 {sw.Elapsed.TotalMilliseconds:0.#}ms，" +
                              $"期间读帧 {Volatile.Read(ref reads)}，读线程异常={readerEx}，" +
                              $"已退出={joined}，释放后再读返回 false={afterDisposeReturnsFalse}");
            if (!joined || readerEx != "无" || !afterDisposeReturnsFalse)
            {
                Console.WriteLine("== 失败：释放与解码没有正确互斥 ==");
                return 1;
            }
        }

        Console.WriteLine($"== 通过：{rounds} 轮「解码中释放」均安全（Dispose 阻塞到在途帧读完、无访问违规）==");
        return 0;
    }
}
