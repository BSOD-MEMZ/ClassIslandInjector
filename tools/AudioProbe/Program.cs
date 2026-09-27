using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>诊断日志门面桩：探针直接打到控制台（不编译真正的 DiagnosticLog，避免拖入 Avalonia）。</summary>
internal static class DiagnosticLog
{
    public static bool Enabled { get; set; } = true;
    public static string? CrashLogPath { get; set; }

    public static void Write(string? path, string message)
    {
        if (Enabled)
        {
            Console.WriteLine($"  [log] {message}");
        }
    }
}

/// <summary>
/// 音频链路回归探针（tools/ 下独立项目，不参与插件主项目编译）。
/// 用途：在没有宿主 GUI 的情况下验证「视频文件 → FFmpeg 解音频 → 重采样为 48kHz 立体声 s16」
/// 这条链路是否真的产出有效波形，并顺手验证循环复位与片段跳转。
/// 用法：AudioProbe.exe [视频路径] [FFmpeg库目录] [导出WAV路径]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 部分终端不支持切换编码，忽略即可。
        }

        var video = args.Length > 0 ? args[0] : @"D:\Downloads\injector-video.mp4";
        var libDir = args.Length > 1
            ? args[1]
            : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";
        var wavPath = args.Length > 2
            ? args[2]
            : @"D:\Dev\ClassIslandInjector\.workbuddy\tmp\audio-probe.wav";

        Console.WriteLine("=== 视频背景音频链路探针 ===");
        Console.WriteLine($"视频文件: {video}");

        if (!File.Exists(video))
        {
            Console.WriteLine("× 视频文件不存在");
            return 2;
        }

        if (!Directory.Exists(libDir))
        {
            Console.WriteLine($"× FFmpeg 库目录不存在: {libDir}");
            return 2;
        }

        ffmpeg.RootPath = libDir;
        Console.WriteLine($"FFmpeg 库目录: {libDir}");
        Console.WriteLine($"FFmpeg 版本: {ffmpeg.av_version_info()}");

        using var decoder = new FFmpegAudioDecoder();
        if (!decoder.Open(video))
        {
            Console.WriteLine("× 打开失败：无音频轨 / 解码器不可用（详见上方日志）");
            return 1;
        }

        Console.WriteLine($"源音频: {decoder.SourceCodecName} {decoder.SourceSampleRate}Hz {decoder.SourceChannels}ch  容器时长={decoder.Duration:0.###}s");
        Console.WriteLine($"目标格式: {FFmpegAudioDecoder.OutSampleRate}Hz {FFmpegAudioDecoder.OutChannels}ch {FFmpegAudioDecoder.OutBitsPerSample}bit");

        var chunk = new byte[FFmpegAudioDecoder.OutSampleRate * FFmpegAudioDecoder.OutBytesPerSample];
        long totalBytes = 0, totalSamples = 0, loudSamples = 0;
        var peak = 0;
        var wav = new List<byte>();
        const int maxWavBytes = FFmpegAudioDecoder.OutSampleRate * FFmpegAudioDecoder.OutBytesPerSample * 10;

        var guard = 0;
        int read;
        while ((read = decoder.ReadPcm(chunk, 0, chunk.Length)) > 0 && guard++ < 20000)
        {
            totalBytes += read;
            for (var i = 0; i + 1 < read; i += 2)
            {
                var sample = (short)(chunk[i] | (chunk[i + 1] << 8));
                var magnitude = Math.Abs((int)sample);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }

                if (magnitude > 32)
                {
                    loudSamples++;
                }

                totalSamples++;
            }

            if (wav.Count < maxWavBytes)
            {
                var take = Math.Min(read, maxWavBytes - wav.Count);
                for (var i = 0; i < take; i++)
                {
                    wav.Add(chunk[i]);
                }
            }
        }

        var seconds = totalBytes / (double)(FFmpegAudioDecoder.OutSampleRate * FFmpegAudioDecoder.OutBytesPerSample);
        var loudRatio = totalSamples == 0 ? 0 : 100.0 * loudSamples / totalSamples;

        Console.WriteLine();
        Console.WriteLine($"解出 PCM: {totalBytes} 字节 ≈ {seconds:0.###}s / {totalSamples} 采样");
        Console.WriteLine($"峰值 = {peak}/32767（0 表示整段静音）");
        Console.WriteLine($"有效采样占比 = {loudRatio:0.##}%");

        var durationOk = decoder.Duration <= 0 || Math.Abs(seconds - decoder.Duration) < Math.Max(1.0, decoder.Duration * 0.1);
        Console.WriteLine($"时长核对: 解出 {seconds:0.###}s vs 容器 {decoder.Duration:0.###}s → {(durationOk ? "一致" : "偏差过大")}");

        if (decoder.Restart())
        {
            var again = decoder.ReadPcm(chunk, 0, chunk.Length);
            Console.WriteLine($"循环复位: Restart 后重读 = {again} 字节（期望 {chunk.Length}）");
        }
        else
        {
            Console.WriteLine("循环复位: Restart 失败");
        }

        if (decoder.SeekTo(5))
        {
            var afterSeek = decoder.ReadPcm(chunk, 0, chunk.Length);
            Console.WriteLine($"片段跳转: SeekTo(5s) 后读 = {afterSeek} 字节（期望 {chunk.Length}）");
        }
        else
        {
            Console.WriteLine("片段跳转: SeekTo 失败");
        }

        if (wav.Count > 0)
        {
            WriteWav(wavPath, wav);
            Console.WriteLine($"试听文件: {wavPath}（前 10 秒）");
        }

        Console.WriteLine();
        var pass = peak > 0 && totalSamples > 0 && durationOk;

        // 设备输出测试：验证 NAudio/WASAPI 在本机能真正打开输出设备（唯一无法离线验证的一环）。
        if (args.Contains("--play"))
        {
            Console.WriteLine();
            Console.WriteLine("=== 设备输出测试（音量 15%，约 2 秒，会实际出声）===");
            using var player = new VideoAudioPlayer();
            if (player.Start(video, 0.15, false))
            {
                Console.WriteLine("音频输出已启动，播放 2 秒…");
                Thread.Sleep(2000);
                player.Stop();
                Console.WriteLine("音频输出已停止，未抛异常。");
            }
            else
            {
                Console.WriteLine("× 音频输出启动失败（输出设备不可用 / 被独占 / NAudio 缺失）");
                pass = false;
            }
        }

        Console.WriteLine();
        Console.WriteLine(pass
            ? "结论: 通过 —— 音频解码 → 重采样链路产出有效波形，且时长与容器一致。"
            : "结论: 失败 —— 请检查上方日志（峰值 0 说明解出的是静音）。");
        return pass ? 0 : 1;
    }

    private static void WriteWav(string path, List<byte> pcm)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var data = pcm.ToArray();
        var byteRate = FFmpegAudioDecoder.OutSampleRate * FFmpegAudioDecoder.OutBytesPerSample;

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)FFmpegAudioDecoder.OutChannels);
        writer.Write(FFmpegAudioDecoder.OutSampleRate);
        writer.Write(byteRate);
        writer.Write((short)FFmpegAudioDecoder.OutBytesPerSample);
        writer.Write((short)FFmpegAudioDecoder.OutBitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);
    }
}
