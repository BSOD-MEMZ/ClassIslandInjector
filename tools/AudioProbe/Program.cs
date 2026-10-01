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

        if (args.Contains("--loop"))
        {
            return RunLoopCheck(args);
        }

        if (args.Contains("--demux"))
        {
            return RunDemuxCheck(args);
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
        // 文件里仍保留 VideoTracks / AudioTracks 两个字段（读旧文件的兼容面），但轨号已经统一。
        Check(text.Contains("\"VideoTracks\"") && text.Contains("\"AudioTracks\""), "序列内保留 VideoTracks / AudioTracks 字段（兼容旧文件）");
        Check(text.Contains("\"Assets\""), "工程含素材库字段");

        var loaded = VideoProjectStore.Load(roundTripPath);
        var audio = loaded.Clips.FirstOrDefault(c => c.IsAudio);
        Check(loaded.Name == "探针序列", "序列名往返");
        Check(loaded.Clips.Count == 2, "片段数量往返");
        Check(audio != null && audio.AudioTrack == 1 && Math.Abs(audio.Volume - 1.5) < 0.001 &&
              Math.Abs(audio.AudioFadeIn - 1) < 0.001 && Math.Abs(audio.AudioFadeOut - 2) < 0.001,
            "音频片段音量 / 淡入 / 淡出往返");
        // 2026-10-01 起音频并入统一轨道号：旧的 Track = -1 + AudioTrack 独立编号会被 Normalize 迁移。
        // 迁移采用「音频锚定在底部」：旧 A(n) → 轨号 n，画面片段整体上移「音频轨数」。
        // 本工程的音频 AudioTrack = 1 → 音频轨数 2 → 音频落在轨号 1、视频从 0 上移到 2。
        Check(audio is { Track: 1 }, "旧格式音频片段迁移到统一轨道号（锚定在底部的第 2 条）");
        Check(loaded.Clips.Any(c => c.Kind == "Video" && c.Track == 2),
            "画面片段迁移时整体上移「音频轨数」");
        Check(loaded.TrackCount == 3, "轨道数统计全部片段（画面与音频共用轨道号）");
        Check(loaded.AudioTrackCount == 1, "AudioTrackCount = 含音频片段的轨道数");
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

    /// <summary>
    /// demux 层探针：把「读不到包」与「解不出帧」分开。
    /// <para>
    /// 背景：用户报「底图一直循环开头几秒」，插件解码器只解出 30 帧，但 PotPlayer 播放完全正常
    /// —— 说明素材没坏，问题在我们的读取/解码链路。本探针用裸 avformat API 统计：
    /// ① 容器有多少条流、各是什么编码；② 逐包读完全文件，每条流各读到多少个包，
    /// ③ 读完后 av_read_frame 的返回码（EOF 还是真错误）；④ 事后 seek 到中段能否正常继续解码。
    /// 用法：--demux &lt;视频&gt; &lt;FFmpeg库目录&gt;
    /// </para>
    /// </summary>
    private static unsafe int RunDemuxCheck(string[] args)
    {
        var idx = Array.IndexOf(args, "--demux");
        var video = args.Length > idx + 1 ? args[idx + 1] : @"D:\Downloads\injector-video.mp4";
        var libDir = args.Length > idx + 2
            ? args[idx + 2]
            : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";

        ffmpeg.RootPath = libDir;
        Console.WriteLine("=== demux 层探针（裸 avformat API） ===");
        Console.WriteLine($"文件: {video}");
        Console.WriteLine($"FFmpeg: {ffmpeg.av_version_info()}");

        AVFormatContext* fmt = null;
        var ret = ffmpeg.avformat_open_input(&fmt, video, null, null);
        if (ret < 0)
        {
            Console.WriteLine($"× avformat_open_input 失败 {ret}");
            return 1;
        }

        ret = ffmpeg.avformat_find_stream_info(fmt, null);
        Console.WriteLine($"avformat_find_stream_info ret={ret}");
        Console.WriteLine($"nb_streams={fmt->nb_streams}  duration={fmt->duration / (double)ffmpeg.AV_TIME_BASE:0.###}s  " +
                          $"start_time={fmt->start_time}");
        Console.WriteLine($"iformat={(fmt->iformat->long_name != null ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->long_name) : "?")}");

        for (var i = 0; i < fmt->nb_streams; i++)
        {
            var st = fmt->streams[i];
            var par = st->codecpar;
            var name = ffmpeg.avcodec_get_name(par->codec_id);
            var tb = st->time_base;
            Console.WriteLine($"  stream[{i}] {name} type={par->codec_type} " +
                              $"{par->width}x{par->height} time_base={tb.num}/{tb.den} " +
                              $"nb_frames={st->nb_frames} duration={st->duration} " +
                              $"avg_fps={ffmpeg.av_q2d(st->avg_frame_rate):0.##} " +
                              $"extradata={par->extradata_size}B " +
                              $"pix_fmt={(AVPixelFormat)par->format} profile={par->profile}");
        }

        // ---- 逐包读完全文件 ----
        var pkt = ffmpeg.av_packet_alloc();
        var counts = new long[fmt->nb_streams];
        long totalPackets = 0;
        long bytes = 0;
        var firstPts = new long[fmt->nb_streams];
        var lastPts = new long[fmt->nb_streams];
        for (var i = 0; i < fmt->nb_streams; i++)
        {
            firstPts[i] = long.MinValue;
            lastPts[i] = long.MinValue;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lastErr;
        while (true)
        {
            lastErr = ffmpeg.av_read_frame(fmt, pkt);
            if (lastErr < 0)
            {
                break;
            }

            var si = pkt->stream_index;
            counts[si]++;
            totalPackets++;
            bytes += pkt->size;
            if (firstPts[si] == long.MinValue)
            {
                firstPts[si] = pkt->pts;
            }

            if (pkt->pts != ffmpeg.AV_NOPTS_VALUE)
            {
                lastPts[si] = pkt->pts;
            }

            ffmpeg.av_packet_unref(pkt);
        }

        Console.WriteLine($"\n逐包读完：共 {totalPackets} 个包，{bytes / 1024.0 / 1024.0:0.##}MB，耗时 {sw.ElapsedMilliseconds}ms");
        for (var i = 0; i < fmt->nb_streams; i++)
        {
            Console.WriteLine($"  stream[{i}] 包数={counts[i]} firstPts={firstPts[i]} lastPts={lastPts[i]}");
        }

        var errBuf = stackalloc byte[256];
        ffmpeg.av_strerror(lastErr, errBuf, 256);
        var errText = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)errBuf) ?? "?";
        Console.WriteLine($"结束返回码 {lastErr} = {errText}" +
                          (lastErr == ffmpeg.AVERROR_EOF ? "（正常 EOF）" : "（★ 非 EOF，读取真出错）"));

        // ---- 事后 seek 到中段，看能否继续解码 ----
        ffmpeg.av_packet_free(&pkt);
        Console.WriteLine("\n--- seek 到 100s 后重新解码 5 帧（验证中段数据是否可解）---");
        var target = 100L * ffmpeg.AV_TIME_BASE;
        var seekRet = ffmpeg.av_seek_frame(fmt, -1, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
        Console.WriteLine($"av_seek_frame(100s) ret={seekRet}");
        if (seekRet >= 0)
        {
            // 找到视频流对应的解码器
            var vIdx = -1;
            for (var i = 0; i < fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    vIdx = i;
                    break;
                }
            }

            if (vIdx >= 0)
            {
                var codec = ffmpeg.avcodec_find_decoder(fmt->streams[vIdx]->codecpar->codec_id);
                var ctx = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(ctx, fmt->streams[vIdx]->codecpar);
                var openRet = ffmpeg.avcodec_open2(ctx, codec, null);
                Console.WriteLine($"avcodec_open2 ret={openRet}（{ffmpeg.avcodec_get_name(codec->id)}）");
                if (openRet >= 0)
                {
                    var p2 = ffmpeg.av_packet_alloc();
                    var f2 = ffmpeg.av_frame_alloc();
                    var got = 0;
                    var guard = 0;
                    while (got < 5 && guard++ < 5000)
                    {
                        if (ffmpeg.av_read_frame(fmt, p2) < 0)
                        {
                            Console.WriteLine("  读到 EOF 仍未凑够 5 帧");
                            break;
                        }

                        if (p2->stream_index == vIdx)
                        {
                            var sr = ffmpeg.avcodec_send_packet(ctx, p2);
                            if (sr < 0)
                            {
                                Console.WriteLine($"  ★ avcodec_send_packet 失败 ret={sr}（第 {got} 帧后）");
                                ffmpeg.av_packet_unref(p2);
                                break;
                            }

                            while (ffmpeg.avcodec_receive_frame(ctx, f2) >= 0)
                            {
                                got++;
                                if (got >= 5)
                                {
                                    break;
                                }
                            }
                        }

                        ffmpeg.av_packet_unref(p2);
                    }

                    Console.WriteLine($"  seek 后解出 {got} 帧" + (got >= 5 ? "（✓ 中段数据可解，问题在顺序读取被提前截断）" : ""));
                    ffmpeg.av_frame_free(&f2);
                    ffmpeg.av_packet_free(&p2);
                }

                ffmpeg.avcodec_free_context(&ctx);
            }
        }

        ffmpeg.avformat_close_input(&fmt);
        return 0;
    }

    /// <summary>
    /// 循环复位探针：验证「解到 EOF → Restart() → 继续解」真的回到了开头。
    /// 用户报「主界面底图循环卡在开头不动」——先解 N 帧记下首帧指纹，再解到 EOF，
    /// Restart 后解出的第一帧必须和首帧一致（同一个画面），否则就是没回到开头。
    /// 用法：--loop &lt;视频&gt; &lt;FFmpeg库目录&gt;
    /// </summary>
    private static int RunLoopCheck(string[] args)
    {
        var idx = Array.IndexOf(args, "--loop");
        var video = args.Length > idx + 1 ? args[idx + 1] : @"D:\Downloads\injector-video.mp4";
        var libDir = args.Length > idx + 2
            ? args[idx + 2]
            : @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";

        ffmpeg.RootPath = libDir;
        Console.WriteLine("=== 循环复位探针 ===");
        Console.WriteLine($"文件: {video}");

        if (!File.Exists(video))
        {
            Console.WriteLine("× 视频文件不存在");
            return 2;
        }

        using var decoder = new FFmpegVideoDecoder();
        decoder.HardwareDecoder = "auto";
        if (!decoder.Open(video, 1280))
        {
            Console.WriteLine("× 打开失败");
            return 1;
        }

        Console.WriteLine($"源 {decoder.SourceWidth}x{decoder.SourceHeight} → {decoder.OutputWidth}x{decoder.OutputHeight}，" +
                          $"帧率={decoder.SourceFps:0.##} 时长={decoder.Duration:0.###}s 硬解生效={decoder.HardwareActive}");

        // 首帧指纹：解码头几帧，用第 3 帧（避开可能的 B 帧乱序）的内容做基准。
        byte[]? reference = null;
        var pre = 0;
        while (pre < 3 && decoder.ReadFrame(out var p))
        {
            reference = (byte[])p.Clone();
            pre++;
        }

        if (reference == null)
        {
            Console.WriteLine("× 连首帧都解不出来");
            return 1;
        }

        Console.WriteLine($"首 3 帧已解，指纹长度 {reference.Length}");

        // 解到 EOF。
        long total = pre;
        while (decoder.ReadFrame(out _))
        {
            total++;
        }

        Console.WriteLine($"解到 EOF，共 {total} 帧");

        // 循环复位。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var restarted = decoder.Restart();
        sw.Stop();
        Console.WriteLine($"Restart() = {restarted}（{sw.Elapsed.TotalMilliseconds:0.0}ms）");

        if (!restarted)
        {
            Console.WriteLine("× 复位失败 —— 播放器会直接停止，画面卡在最后一帧");
            return 1;
        }

        // 复位后连续解若干帧，看能不能解出（卡在开头 = 解不出帧）。
        var after = new List<byte[]>();
        for (var i = 0; i < 5; i++)
        {
            if (!decoder.ReadFrame(out var p))
            {
                Console.WriteLine($"× 复位后第 {i + 1} 帧解不出来（-1 帧无法解出）");
                break;
            }

            after.Add((byte[])p.Clone());
        }

        Console.WriteLine($"复位后解出 {after.Count} 帧");
        if (after.Count == 0)
        {
            Console.WriteLine("× 复位后一帧都解不出 —— 这就是「循环卡在开头」");
            return 1;
        }

        // 指纹比对：复位后第一帧应当与首帧接近（同一画面；编码有损，比较均值差）。
        var diff = MeanAbsDiff(reference, after[0]);
        Console.WriteLine($"复位后首帧 vs 起始首帧的平均像素差 = {diff:0.0}（越小越像同一画面）");
        var ok = diff < 24;
        Console.WriteLine(ok ? "✓ 循环复位正常（回到了开头）" : "× 复位后画面与开头不是同一帧");
        return ok ? 0 : 1;
    }

    /// <summary>两张 BGRA 帧的平均通道差（长度不一致直接判为差异极大）。</summary>
    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return 255;
        }

        long sum = 0;
        var n = 0;
        // 抽样：每 37 字节取一个，够判画面是否相同。
        for (var i = 0; i < a.Length; i += 37)
        {
            sum += Math.Abs(a[i] - b[i]);
            n++;
        }

        return n == 0 ? 0 : (double)sum / n;
    }

    private static void WriteWav(string path, List<byte> pcm)    {
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
