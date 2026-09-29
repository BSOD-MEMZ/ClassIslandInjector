using System.Text;
using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 「渲染带音频」端到端回归探针（tools/ 下独立项目，不参与插件主项目编译）。
/// <para>
/// 守护的两件事：
/// </para>
/// <list type="number">
/// <item>离线混音（<see cref="ProjectAudioMixer.StartOffline"/>）+ AAC 双流 mp4 的时间轴正确性：
/// 音频时长 == 工程时长、片段起点前是静音、变速下频率符合预期（保持音调不变调、变调跟随速度）。</item>
/// <item>复用器的 stream_index：**编码器不会设置 `pkt->stream_index`**（默认 0）。
/// 带音轨时若忘了写，音频包会被塞进 0 号（视频）流 —— 复用器刷「non monotonically increasing
/// dts」并把视频轨写坏：产物只剩一两千字节、视频参数缺失（"unspecified pixel format"）、
/// 读它还会在 libswscale 里断言崩掉。这个坑只在**双流**时才暴露，单流（纯视频）永远正常。</item>
/// </list>
/// <para>
/// 用法：RaceProbe.exe --audio [视频路径] [FFmpeg库目录] [输出目录]
/// （音源是探针自己合成的 1kHz 正弦 wav，临时文件写在输出目录）
/// 退出码 0 = 全部断言通过。
/// </para>
/// </summary>
internal static class AudioRenderCheck
{
    private const int Rate = 48000;
    private const int Fps = 10;
    /// <summary>视频片段时长（工程里那条被静音的视频）。</summary>
    private const double VideoClipSeconds = 2.5;
    /// <summary>音频片段：素材 2s，起点 0.5s，变速后时间轴占 2/speed。</summary>
    private const double AudioClipStart = 0.5;
    private const double AudioClipSourceSeconds = 2.0;

    public static int Run(string videoPath, string ffmpegRoot, string outDir)
    {
        ffmpeg.RootPath = ffmpegRoot;
        _ = ffmpeg.avcodec_version();
        Directory.CreateDirectory(outDir);

        var tone = Path.Combine(outDir, "tone-1khz.wav");
        WriteToneWav(tone, seconds: 3.0, hz: 1000);
        Console.WriteLine($"音源：{Path.GetFileName(tone)}（1kHz 正弦，48kHz/2ch/s16）");
        Console.WriteLine($"视频：{Path.GetFileName(videoPath)}");
        Console.WriteLine();

        var ok = true;
        // 2× 保持音调 → 仍然 1kHz；2× 变调 → 2kHz；原速 → 1kHz；0.5× 保持音调 → 1kHz
        ok &= Case("2× 保持音调", videoPath, tone, outDir, 2.0, preservePitch: true, expectHz: 1000);
        ok &= Case("2× 变调", videoPath, tone, outDir, 2.0, preservePitch: false, expectHz: 2000);
        ok &= Case("1× 原速", videoPath, tone, outDir, 1.0, preservePitch: false, expectHz: 1000);
        ok &= Case("0.5× 保持音调", videoPath, tone, outDir, 0.5, preservePitch: true, expectHz: 1000);

        // 反向：关掉「包含音频」必须真的没有音轨（「仅画面」选项不能悄悄留下空音轨）。
        var noAudio = Render(videoPath, tone, Path.Combine(outDir, "noaudio.mp4"), 1.0, false, includeAudio: false);
        var hasTrack = HasAudioTrack(noAudio);
        var (nf, _, _, _) = CountVideoFrames(noAudio);
        Console.WriteLine($"[反向] 关闭「包含音频」→ 有音轨 = {hasTrack}（期望 False）；"
                          + $"视频解码帧数 = {nf}（与带音轨对照，说明尾部 2 帧读不出与音轨无关）");
        ok &= !hasTrack;

        Console.WriteLine();
        Console.WriteLine(ok ? "== 通过：带音频渲染的时长/静音/频率/流归属全部正确 ==" : "== 失败：见上面 FAIL 行 ==");
        return ok ? 0 : 1;
    }

    private static bool Case(string label, string videoPath, string tone, string outDir,
        double speed, bool preservePitch, int expectHz)
    {
        var path = Render(videoPath, tone, Path.Combine(outDir, $"audio-{speed}-{preservePitch}.mp4"),
            speed, preservePitch, includeAudio: true);

        // 期望工程时长 = max(视频片段, 音频片段起点 + 素材 ÷ 速度)
        var expectSeconds = Math.Max(VideoClipSeconds, AudioClipStart + AudioClipSourceSeconds / speed);

        // 1) 视频轨：容器时长 + 解码帧数
        //    帧数允许少 2 帧：FFmpegVideoDecoder 不在 EOF 冲解码器，B 帧重排缓冲里的最后 2 帧
        //    读不出来（既有行为，与音轨无关，由上面的 noaudio 对照证明）。
        var (frames, fw, fh, videoSeconds) = CountVideoFrames(path);
        var expectFrames = (int)Math.Ceiling(expectSeconds * Fps);
        var videoOk = Math.Abs(videoSeconds - expectSeconds) < 0.06 && frames >= expectFrames - 2;

        // 2) 音频轨：总时长 + 片段前静音 + 片段中段频率/响度
        var pcm = ReadAllAudio(path, out var audioSeconds);
        var durOk = Math.Abs(audioSeconds - expectSeconds) < 0.05;
        var clipEnd = AudioClipStart + AudioClipSourceSeconds / speed;
        var before = Rms(pcm, 0.05 * Rate, 0.45 * Rate);
        var mid = (AudioClipStart + clipEnd) / 2;
        var toneHz = ZeroCrossHz(pcm, (mid - 0.3) * Rate, (mid + 0.3) * Rate);
        var toneRms = Rms(pcm, (mid - 0.3) * Rate, (mid + 0.3) * Rate);
        var hzOk = Math.Abs(toneHz - expectHz) <= expectHz * 0.03;
        var silentOk = before < 0.01 && toneRms > 0.1;

        var ok = videoOk && durOk && hzOk && silentOk;
        Console.WriteLine(
            $"[{(ok ? "OK " : "FAIL")}] {label,-12} 视频 {fw}x{fh} {videoSeconds:0.###}s/{frames}帧"
            + $"（期望 {expectSeconds:0.###}s/{expectFrames}帧）| 音频 {audioSeconds:0.###}s | "
            + $"片段前静音={before:0.0000} 段内 RMS={toneRms:0.###} 频率={toneHz:0}Hz（期望 {expectHz}）");

        if (!videoOk)
        {
            Console.WriteLine($"       ↳ 视频不符：{videoSeconds:0.###}s/{frames} 帧（期望 {expectSeconds:0.###}s/{expectFrames} 帧）");
        }

        if (!durOk)
        {
            Console.WriteLine($"       ↳ 音频时长不符：{audioSeconds:0.###}s（期望 {expectSeconds:0.###}s）");
        }

        if (!hzOk)
        {
            Console.WriteLine($"       ↳ 频率不符：{toneHz:0}Hz（期望 {expectHz}Hz）");
        }

        if (!silentOk)
        {
            Console.WriteLine("       ↳ 静音/响度不符（片段起点前应为静音、片段区间内应有声）");
        }

        return ok;
    }

    /// <summary>构造工程（1 条静音视频片段 + 1 条正弦音频片段）并用真实渲染器渲染。</summary>
    private static string Render(string videoPath, string tone, string outPath, double speed,
        bool preservePitch, bool includeAudio)
    {
        var project = new VideoProject
        {
            OutputWidth = 160,
            OutputHeight = 90,
            Clips =
            [
                new VideoClip
                {
                    Kind = "Video",
                    SourcePath = videoPath,
                    Track = 0,
                    StartTime = 0,
                    InPoint = 0,
                    OutPoint = VideoClipSeconds,
                    Muted = true // 原声关掉，产物里只该有测试音
                },
                new VideoClip
                {
                    Kind = "Audio",
                    SourcePath = tone,
                    AudioTrack = 0,
                    StartTime = AudioClipStart,
                    InPoint = 0,
                    OutPoint = AudioClipSourceSeconds,
                    Volume = 1,
                    Speed = speed,
                    PreservePitch = preservePitch
                }
            ]
        };

        if (File.Exists(outPath))
        {
            File.Delete(outPath);
        }

        new VideoProjectRenderer(project, outPath, 160, 90, 28, Fps,
            progress: null, preset: "veryfast", hwEncoder: null, hwDecoder: null,
            token: default, includeAudio: includeAudio).Render();
        return outPath;
    }

    /// <summary>合成 48kHz/2ch/s16 的正弦 wav（避开「拿现成音乐量频率」的不确定性）。</summary>
    private static void WriteToneWav(string path, double seconds, double hz)
    {
        var frames = (int)(Rate * seconds);
        var data = new byte[frames * 4];
        for (var n = 0; n < frames; n++)
        {
            var v = (short)(32767 * 0.5 * Math.Sin(2 * Math.PI * hz * n / Rate));
            data[n * 4] = (byte)(v & 0xFF);
            data[n * 4 + 1] = (byte)((v >> 8) & 0xFF);
            data[n * 4 + 2] = data[n * 4];
            data[n * 4 + 3] = data[n * 4 + 1];
        }

        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);                     // fmt 块长度
        w.Write((short)1);               // PCM
        w.Write((short)2);               // 声道数
        w.Write(Rate);                   // 采样率
        w.Write(Rate * 4);               // 字节率
        w.Write((short)4);               // 块对齐
        w.Write((short)16);              // 位深
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
    }

    private static bool HasAudioTrack(string path)
    {
        var decoder = new FFmpegAudioDecoder();
        try
        {
            return decoder.Open(path) && decoder.HasAudio;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>读出整条音轨（s16 交错），返回单声道混合后的浮点样点用于测量。</summary>
    private static float[] ReadAllAudio(string path, out double seconds)
    {
        var decoder = new FFmpegAudioDecoder();
        seconds = 0;
        try
        {
            if (!decoder.Open(path))
            {
                Console.WriteLine($"       ↳ 打不开音频：{path}");
                return [];
            }

            var block = new byte[4096 * FFmpegAudioDecoder.OutBytesPerSample];
            var list = new List<float>();
            while (true)
            {
                var got = decoder.ReadPcm(block, 0, block.Length);
                if (got <= 0)
                {
                    break;
                }

                var n = got / FFmpegAudioDecoder.OutBytesPerSample;
                for (var i = 0; i < n; i++)
                {
                    var o = i * 4;
                    var l = (short)(block[o] | (block[o + 1] << 8));
                    var r = (short)(block[o + 2] | (block[o + 3] << 8));
                    list.Add((l + r) / 2f / 32768f);
                }
            }

            seconds = list.Count / (double)Rate;
            return [.. list];
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private static (int Frames, int W, int H, double Seconds) CountVideoFrames(string path)
    {
        var decoder = new FFmpegVideoDecoder();
        try
        {
            if (!decoder.Open(path, 320))
            {
                return (0, 0, 0, 0);
            }

            var n = 0;
            while (decoder.ReadFrame(out _))
            {
                n++;
                if (n > 100_000)
                {
                    break;
                }
            }

            return (n, decoder.OutputWidth, decoder.OutputHeight, decoder.Duration);
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private static double Rms(float[] pcm, double fromSample, double toSample)
    {
        var from = Math.Max(0, (int)fromSample);
        var to = Math.Min(pcm.Length, (int)toSample);
        if (to <= from)
        {
            return 0;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += pcm[i] * (double)pcm[i];
        }

        return Math.Sqrt(sum / (to - from));
    }

    /// <summary>过零率测频（纯正弦足够准）：频率 = 过零次数 / 2 / 时长。</summary>
    private static double ZeroCrossHz(float[] pcm, double fromSample, double toSample)
    {
        var from = Math.Max(0, (int)fromSample);
        var to = Math.Min(pcm.Length, (int)toSample);
        if (to - from < 100)
        {
            return 0;
        }

        var crossings = 0;
        var prev = pcm[from];
        for (var i = from + 1; i < to; i++)
        {
            var cur = pcm[i];
            if ((prev <= 0 && cur > 0) || (prev >= 0 && cur < 0))
            {
                crossings++;
            }

            prev = cur;
        }

        return crossings / 2.0 / ((to - from) / (double)Rate);
    }
}
