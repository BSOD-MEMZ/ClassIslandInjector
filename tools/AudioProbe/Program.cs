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

/// <summary>FFmpeg 运行时桩：探针自己在 Main 里设置 ffmpeg.RootPath，这里恒报就绪。</summary>
internal static class FFmpegRuntime
{
    public static bool IsAvailable => true;

    public static bool EnsureLoaded() => true;
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

        if (args.Contains("--project"))
        {
            return RunProjectChecks();
        }

        if (args.Contains("--vdec"))
        {
            return RunVideoDecodeCheck(args);
        }
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

    /// <summary>
    /// 工程文件（v2 分层格式）与音频包络的离线校验：保存 / 读取往返、v1 旧格式迁移、
    /// 音量与淡入淡出包络、活跃片段判定。不依赖宿主 GUI。
    /// </summary>
    private static int RunProjectChecks()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ClassIslandInjectorProbe");
        Directory.CreateDirectory(tmp);
        var roundTripPath = Path.Combine(tmp, "roundtrip.ciproj");
        var legacyPath = Path.Combine(tmp, "legacy-video-project.json");

        var failures = new List<string>();

        void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "通过" : "失败")}: {what}");
            if (!ok)
            {
                failures.Add(what);
            }
        }

        Console.WriteLine("=== 工程文件与音频包络校验 ===");

        var project = new VideoProject { Name = "探针序列", OutputWidth = 400, OutputHeight = 90 };
        project.Clips.Add(new VideoClip
        {
            Kind = "Video", SourcePath = "v.mp4", Track = 0, StartTime = 0, InPoint = 0, OutPoint = 5
        });
        project.Clips.Add(new VideoClip
        {
            Kind = "Audio", SourcePath = "a.mp3", Track = -1, AudioTrack = 1, StartTime = 2,
            InPoint = 0.5, OutPoint = 8.5, Volume = 1.5, AudioFadeIn = 1, AudioFadeOut = 2,
            SourceDuration = 20
        });
        VideoProjectStore.Save(project, roundTripPath);

        var text = File.ReadAllText(roundTripPath);
        Check(text.Contains("\"Version\": 2") && text.Contains("\"Sequence\""), "写出 v2 分层结构（Version / Sequence）");
        Check(text.Contains("\"VideoTracks\"") && text.Contains("\"AudioTracks\""), "序列内区分视频轨与音频轨");
        Check(text.Contains("\"Assets\""), "工程含素材库字段");

        var loaded = VideoProjectStore.Load(roundTripPath);
        var audio = loaded.Clips.FirstOrDefault(c => c.IsAudio);
        Check(loaded.Name == "探针序列", "序列名往返");
        Check(loaded.Clips.Count == 2, "片段数量往返");
        Check(audio != null && audio.AudioTrack == 1 && Math.Abs(audio.Volume - 1.5) < 0.001 &&
              Math.Abs(audio.AudioFadeIn - 1) < 0.001 && Math.Abs(audio.AudioFadeOut - 2) < 0.001,
            "音频片段音量 / 淡入 / 淡出往返");
        Check(audio is { Track: -1 }, "音频片段 Track = -1（不进视频轨）");
        Check(loaded.TrackCount == 1, "视频轨数只数画面片段");
        Check(loaded.AudioTrackCount == 2, "音频轨数按 AudioTrack 计算");
        Check(loaded.HasAudioContent, "识别到可出声内容");

        if (audio != null)
        {
            Check(Math.Abs(audio.AudioGainAt(0)) < 0.001, "淡入起点增益 = 0");
            Check(Math.Abs(audio.AudioGainAt(0.5) - 0.75) < 0.01, "淡入中点增益 = 0.5 × 1.5");
            Check(Math.Abs(audio.AudioGainAt(3) - 1.5) < 0.001, "稳态增益 = 音量倍率");
            Check(Math.Abs(audio.AudioGainAt(7) - 0.75) < 0.01, "淡出中点增益 = 0.5 × 1.5");
            Check(Math.Abs(audio.AudioGainAt(8)) < 0.001, "淡出终点增益 = 0");
        }

        Check(loaded.AudioClipsAt(3).Count() == 2, "时刻 3s：视频原声与音频片段同时活跃");
        Check(loaded.AudioClipsAt(6).Count() == 1, "时刻 6s：视频片段已结束，只剩音频");
        Check(!loaded.AudioClipsAt(10.5).Any(), "时刻 10.5s：全部结束");

        var legacyJson = @"{
  ""OutputWidth"": 400,
  ""OutputHeight"": 90,
  ""Clips"": [
    { ""Kind"": ""Video"", ""SourcePath"": ""old1.mp4"", ""Track"": 0, ""StartTime"": 0, ""InPoint"": 0, ""OutPoint"": 5 },
    { ""Kind"": ""Video"", ""SourcePath"": ""old2.mp4"", ""Track"": 0, ""StartTime"": 0, ""InPoint"": 0, ""OutPoint"": 6 },
    { ""Kind"": ""Video"", ""SourcePath"": ""old3.mp4"", ""Track"": 0, ""StartTime"": 0, ""InPoint"": 0, ""OutPoint"": 4 }
  ]
}";
        File.WriteAllText(legacyPath, legacyJson);
        var migrated = VideoProjectStore.Load(legacyPath);
        Check(migrated.Clips.Count == 3, "旧格式片段全部保留");
        Check(migrated.Clips.All(c => c.Muted), "旧格式视频片段迁移后显式静音（升级不会突然出声）");
        Check(Math.Abs(migrated.Clips[0].StartTime) < 0.001 &&
              Math.Abs(migrated.Clips[1].StartTime - 5) < 0.001 &&
              Math.Abs(migrated.Clips[2].StartTime - 11) < 0.001,
            "旧版单轨拼接迁移为顺序时间轴（0 / 5 / 11）");
        Check(!migrated.HasAudioContent, "迁移后工程无可出声内容（全部静音）");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("结论: 通过 —— 工程格式（v2 分层 / v1 迁移）与音频包络均符合预期。");
            return 0;
        }

        Console.WriteLine($"结论: 失败 {failures.Count} 项");
        return 1;
    }

    /// <summary>
    /// 用插件自己的 <see cref="FFmpegVideoDecoder"/> 完整解码指定视频（复刻插件的解码路径：
    /// HardwareDecoder="auto" + maxDimension=1280），并在每帧做缓冲区边界抽查。
    /// 用法：--vdec &lt;视频&gt; &lt;FFmpeg库目录&gt; [最大帧数]
    /// </summary>
    private static int RunVideoDecodeCheck(string[] args)
    {
        var idx = Array.IndexOf(args, "--vdec");
        var video = args.Length > idx + 1 ? args[idx + 1] : @"D:\Downloads\injector-video.mp4";
        var libDir = args.Length > idx + 2
            ? args[idx + 2]
            : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";
        var maxFrames = args.Length > idx + 3 && int.TryParse(args[idx + 3], out var mf) ? mf : 0;

        ffmpeg.RootPath = libDir;
        Console.WriteLine("=== 插件解码器完整解码测试 ===");
        Console.WriteLine($"文件: {video}");
        Console.WriteLine($"FFmpeg: {ffmpeg.av_version_info()}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frames = 0;

        using (var decoder = new FFmpegVideoDecoder())
        {
            decoder.HardwareDecoder = "auto";
            if (!decoder.Open(video, 1280))
            {
                Console.WriteLine("× 打开失败");
                return 1;
            }

            Console.WriteLine($"源 {decoder.SourceWidth}x{decoder.SourceHeight} → 输出 {decoder.OutputWidth}x{decoder.OutputHeight}，" +
                              $"帧率={decoder.SourceFps:0.##} 时长={decoder.Duration:0.###}s 硬解生效={decoder.HardwareActive}");

            while (true)
            {
                if (!decoder.ReadFrame(out var pixels))
                {
                    break;
                }

                frames++;
                _ = pixels[^1];   // 边界抽查：缓冲区越界会直接崩在这行
                if (maxFrames > 0 && frames >= maxFrames)
                {
                    break;
                }

                if (frames % 1000 == 0)
                {
                    Console.WriteLine($"  已解码 {frames} 帧（本批 {sw.ElapsedMilliseconds}ms）");
                    sw.Restart();
                }
            }
        }

        Console.WriteLine($"完成：共解码 {frames} 帧，全程无异常。");
        return 0;
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
