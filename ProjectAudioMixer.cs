using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassIslandInjector;

/// <summary>
/// 工程音频混音器：按工程时间轴调度所有出声片段（音频轨片段 + 视频片段自带原声），
/// 每个片段一个 FFmpeg 解码器，解码 → 应用片段音量与淡入/淡出包络 → 逐采样相加 →
/// 输出到 NAudio（WASAPI 共享模式）。
/// <para>
/// 时间基准由**已输出的采样数**推进（声卡时钟），与视频轨的实时时钟各自独立但同为实时速率，
/// 长期偏差在背景视频场景下可忽略；片段位置每次按「应有的素材时间」校验，偏差超过 150ms
/// 就 seek 纠正，因此拖动播放头（<see cref="Seek"/>）后不会累积跑偏。
/// </para>
/// <para>
/// 混音按 10ms 小块推进：每块重算一次活跃片段集合，片段边界的对齐误差不超过一个块长。
/// 累加用 int 中间量，最后钳制到 s16 范围，多轨叠加不会因为反复截断而失真。
/// </para>
/// </summary>
internal sealed class ProjectAudioMixer : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[project-audio] {message}");

    /// <summary>混音块长度（毫秒）。越小片段边界越准，越大开销越低。</summary>
    private const int ChunkMs = 10;

    /// <summary>素材位置偏差超过该秒数就 seek 纠正。</summary>
    private const double ResyncThreshold = 0.15;

    private readonly object _sync = new();

    private VideoProject? _project;
    private WasapiOut? _output;
    private MixProvider? _provider;

    /// <summary>采样计数起点对应的工程时间（秒）。</summary>
    private double _baseTime;
    /// <summary>自 <see cref="_baseTime"/> 起已输出的采样帧数。</summary>
    private long _frames;

    /// <summary>当前活跃的音频源（片段 → 解码器）。</summary>
    private readonly Dictionary<VideoClip, AudioSource> _active = [];

    /// <summary>累加缓冲（int 中间量，避免逐源截断造成失真）。</summary>
    private int[] _accum = new int[4096];

    /// <summary>
    /// 已释放标志。置位后混音线程会在下一块立刻返回静音，
    /// 释放流程才不会卡在「等音频线程退出」上（那是 UI 卡死的典型成因）。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>音频输出是否已建立。</summary>
    public bool IsRunning => _output != null;

    /// <summary>
    /// 当前**可听**的工程时间（秒）：以声卡实际播放位置为准。
    /// <para>
    /// 不能用「已渲染帧数」：输出总是领先可听位置一个 WASAPI 缓冲（约 100~200ms），
    /// 拿它跟视频的墙钟比会一直差着一个缓冲量。这里用 <see cref="IWavePosition.GetPosition"/>
    /// 拿声卡真实播放字节数，才是能用做音画同步校正的时钟。
    /// </para>
    /// </summary>
    public double AudibleTime
    {
        get
        {
            var output = _output;
            if (output == null)
            {
                return _baseTime + _frames / (double)SampleRate;
            }

            try
            {
                var bytesPerSecond = SampleRate * FFmpegAudioDecoder.OutBytesPerSample;
                return _baseTime + output.GetPosition() / (double)bytesPerSecond;
            }
            catch
            {
                // 设备拔出 / 音频服务重启：退回渲染进度（不精确但不会抛）。
                return _baseTime + _frames / (double)SampleRate;
            }
        }
    }

    /// <summary>输出采样率（固定 48kHz）。</summary>
    public static int SampleRate => FFmpegAudioDecoder.OutSampleRate;

    /// <summary>当前工程时间（秒，由已输出采样数推算）。</summary>
    public double CurrentTime
    {
        get
        {
            lock (_sync)
            {
                return _baseTime + _frames / (double)SampleRate;
            }
        }
    }

    /// <summary>
    /// 启动混音输出。工程不含任何可能出声的片段、或输出设备不可用时返回 false（调用方静默降级）。
    /// </summary>
    public bool Start(VideoProject project, double startTime)
    {
        Stop();
        _disposed = false;

        try
        {
            if (!project.HasAudioContent)
            {
                // 「没声音」最常见的原因就是这里：有音频/视频片段，但全都被静音或音量 0 筛掉了。
                // 把每个被筛掉的片段和原因写进日志，免得又要怀疑声卡。
                Log("工程不含**可出声**的音频内容，不建立音频输出"
                    + DescribeInaudible(project, out var anyAudioClip)
                    + (anyAudioClip ? "" : "（工程里也没有音频/视频片段）"));
                return false;
            }

            lock (_sync)
            {
                _project = project;
                _baseTime = Math.Max(0, startTime);
                _frames = 0;
                ReleaseAllSources();
            }

            var provider = new MixProvider(this);
            var output = new WasapiOut(AudioClientShareMode.Shared, 100);
            output.Init(provider);
            output.Play();

            _provider = provider;
            _output = output;
            Log($"工程音频已启动：起点={startTime:0.###}s，"
                + $"可出声片段={project.Clips.Count(c => c.ContributesAudio)}"
                + DescribeInaudible(project, out _));
            return true;
        }
        catch (Exception ex)
        {
            Log($"工程音频启动失败：{ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>
    /// 「有音轨但不出声」的片段清单（日志用）：静音 / 音量 0 的逐条列出，方便定位
    /// 「剪视频听不到声音」到底是哪一条被筛掉了。<paramref name="anyAudioCapable"/> 表示工程里
    /// 是否存在音频/视频片段（区分「根本没素材」与「素材都不出声」）。
    /// </summary>
    private static string DescribeInaudible(VideoProject project, out bool anyAudioCapable)
    {
        var parts = new List<string>();
        anyAudioCapable = false;
        foreach (var clip in project.Clips)
        {
            if (!clip.IsAudio && !string.Equals(clip.Kind, "Video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            anyAudioCapable = true;
            if (clip.ContributesAudio)
            {
                continue;
            }

            var reason = clip.Muted ? "已静音" : $"音量 {clip.Volume * 100:0.#}%";
            parts.Add(clip.IsAudio
                ? $"音频片段 {Path.GetFileName(clip.SourcePath)}@{clip.StartTime:0.##}s（{reason}）"
                : $"视频片段 {Path.GetFileName(clip.SourcePath)}@{clip.StartTime:0.##}s（{reason}）");
        }

        return parts.Count == 0 ? "" : "；不出声的片段：" + string.Join("、", parts);
    }

    /// <summary>暂停输出（保留位置，<see cref="Resume"/> 继续）。</summary>
    public void Pause()
    {
        try
        {
            _output?.Pause();
        }
        catch (Exception ex)
        {
            Log($"暂停失败：{ex.Message}");
        }
    }

    /// <summary>恢复输出。</summary>
    public void Resume()
    {
        try
        {
            _output?.Play();
        }
        catch (Exception ex)
        {
            Log($"恢复失败：{ex.Message}");
        }
    }

    /// <summary>跳转到指定工程时间（丢弃活跃源，下次混音时按新位置重建并 seek）。</summary>
    public void Seek(double time)
    {
        lock (_sync)
        {
            _baseTime = Math.Max(0, time);
            _frames = 0;
            ReleaseAllSources();
        }
    }

    /// <summary>更新工程引用（片段被编辑后调用，下一次混音自动生效）。</summary>
    public void UpdateProject(VideoProject project)
    {
        lock (_sync)
        {
            _project = project;
        }
    }

    /// <summary>
    /// 确保音频输出已建立。用于「起播时工程还没有音频内容、之后才加进来」的场景
    /// （编辑器里先按播放、再拖素材是常规操作顺序）：那时 <see cref="Start"/> 会发现
    /// 没有可出声内容而直接返回，输出一直为空 —— 之后无论加多少素材都是静音。
    /// 这里在每次工程刷新 / 恢复播放时补建一次。
    /// </summary>
    public void EnsureRunning(VideoProject project, double startTime)
    {
        lock (_sync)
        {
            if (_output != null)
            {
                return;
            }
        }

        if (project.HasAudioContent)
        {
            Log($"工程出现可出声内容，补建音频输出（起点={startTime:0.###}s）");
            Start(project, startTime);
        }
    }

    /// <summary>停止并释放输出（可再次 Start）。</summary>
    public void Stop()
    {
        var output = _output;
        _output = null;
        _provider = null;

        try
        {
            output?.Stop();
        }
        catch
        {
            // 设备被拔出 / 音频服务重启：停止失败无所谓，下面照样释放。
        }

        try
        {
            output?.Dispose();
        }
        catch
        {
            // 同上。
        }

        lock (_sync)
        {
            ReleaseAllSources();
            _project = null;
        }
    }

    public void Dispose()
    {
        // 先立标志：让混音线程的 Fill 立刻返回，Stop 里的 WasapiOut.Dispose 才等得到线程退出。
        _disposed = true;
        Stop();
    }

    private void ReleaseAllSources()
    {
        foreach (var source in _active.Values)
        {
            source.Dispose();
        }

        _active.Clear();
    }

    /// <summary>混音填充：始终返回 <paramref name="count"/>（补静音维持输出在线）。</summary>
    private int Fill(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        lock (_sync)
        {
            var project = _project;
            if (_disposed || project == null)
            {
                return count;
            }

            var chunkFrames = Math.Max(1, SampleRate * ChunkMs / 1000);
            var pos = offset;
            var remaining = count;

            while (remaining > 0)
            {
                var frames = Math.Min(chunkFrames, remaining / FFmpegAudioDecoder.OutBytesPerSample);
                if (frames <= 0)
                {
                    break;
                }

                var bytes = frames * FFmpegAudioDecoder.OutBytesPerSample;
                MixChunk(project, buffer, pos, frames);

                pos += bytes;
                remaining -= bytes;
                _frames += frames;
            }
        }

        return count;
    }

    /// <summary>混一个块：重算活跃集合 → 逐源累加 → 限幅写回 s16。</summary>
    private void MixChunk(VideoProject project, byte[] buffer, int offset, int frames)
    {
        var t = _baseTime + _frames / (double)SampleRate;
        var blockSeconds = frames / (double)SampleRate;
        // 以块中点判定活跃：片段首尾各差半个块长，听感上比整块偏移更自然。
        var probe = t + blockSeconds * 0.5;
        var alive = new HashSet<VideoClip>(project.AudioClipsAt(probe));

        // 关掉本块不再活跃的源。
        foreach (var clip in _active.Keys.Where(c => !alive.Contains(c)).ToArray())
        {
            _active[clip].Dispose();
            _active.Remove(clip);
        }

        var needed = frames * FFmpegAudioDecoder.OutChannels;
        if (_accum.Length < needed)
        {
            _accum = new int[Math.Max(needed, _accum.Length * 2)];
        }

        Array.Clear(_accum, 0, needed);
        var anySource = false;

        foreach (var clip in alive)
        {
            if (!_active.TryGetValue(clip, out var source))
            {
                source = AudioSource.TryCreate(clip);
                if (source == null)
                {
                    // 素材缺音频轨 / 文件不可读：这个片段就一直不出声（不反复重试）。
                    continue;
                }

                _active[clip] = source;

                // 片段已经在时间轴中途（seek 后/延迟进入）：把解码器对到应有的素材位置。
                var entrySource = clip.InPoint + Math.Max(0, t - clip.StartTime);
                if (entrySource > 0.02)
                {
                    source.Decoder.SeekTo(entrySource);
                }

                source.SourceBase = Math.Max(0, entrySource);
                source.Reader.Reset();
                Log($"音频源进入：{Path.GetFileName(clip.SourcePath)}（素材位置 {source.SourceBase:0.###}s" +
                    (Math.Abs(clip.EffectiveSpeed - 1) > 0.001
                        ? $"，速度 {clip.EffectiveSpeed:0.##}×{(clip.PreservePitch ? " 保持音调" : " 变调")}"
                        : "") + "）");
            }

            var localTime = t - clip.StartTime;
            // 按播放速度换算素材位置：速度 2× 时同样一段输出时间要吃掉两倍的素材。
            var expectedSource = clip.SourceTimeAt(localTime);
            var actualSource = source.SourceBase + source.ReadFrames / (double)SampleRate;
            if (Math.Abs(expectedSource - actualSource) > ResyncThreshold)
            {
                var target = Math.Max(0, expectedSource);
                source.Decoder.SeekTo(target);
                source.SourceBase = target;
                source.Reader.Reset();
            }

            var bytes = frames * FFmpegAudioDecoder.OutBytesPerSample;
            source.EnsureCapacity(bytes);
            // 变速读源：原速直接透传；变速按速度重采样（变调）或时间伸缩（保持音调）。
            var mixFrames = source.Reader.ReadFrames(source.Buffer!, frames, clip.EffectiveSpeed, clip.PreservePitch);
            if (mixFrames <= 0)
            {
                // 素材先播完（片段比素材长，或静音轨）：本块该源无输出，其余源照常。
                continue;
            }

            var trackGain = TrackGainOf(project, clip);
            if (trackGain <= 0.0001)
            {
                continue; // 轨静音：照常消耗（Reader 已记账），只是不混进去
            }

            var src = source.Buffer!;
            for (var f = 0; f < mixFrames; f++)
            {
                var gain = clip.AudioGainAt(localTime + f / (double)SampleRate) * trackGain;
                if (gain <= 0.0001)
                {
                    continue;
                }

                var baseIndex = f * FFmpegAudioDecoder.OutBytesPerSample;
                var left = (short)(src[baseIndex] | (src[baseIndex + 1] << 8));
                var right = (short)(src[baseIndex + 2] | (src[baseIndex + 3] << 8));
                _accum[f * 2] += (int)(left * gain);
                _accum[f * 2 + 1] += (int)(right * gain);
                anySource = true;
            }
            // 消费量由 AudioSpeedReader 记账（它才知道变速下实际吃掉多少素材帧）。
        }

        if (!anySource)
        {
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            WriteClamped(buffer, offset + f * FFmpegAudioDecoder.OutBytesPerSample, _accum[f * 2]);
            WriteClamped(buffer, offset + f * FFmpegAudioDecoder.OutBytesPerSample + 2, _accum[f * 2 + 1]);
        }
    }

    /// <summary>写回一个声道采样并钳制到 s16（多源叠加削波保护）。</summary>
    private static void WriteClamped(byte[] buffer, int index, int value)
    {
        if (value > 32767)
        {
            value = 32767;
        }
        else if (value < -32768)
        {
            value = -32768;
        }

        buffer[index] = (byte)(value & 0xFF);
        buffer[index + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>片段所在轨道的增益（音频轨的静音 / 音量；视频片段原声暂不叠轨道增益）。</summary>
    private static double TrackGainOf(VideoProject project, VideoClip clip)
    {
        if (!clip.IsAudio)
        {
            return 1;
        }

        var state = project.GetAudioTrackState(clip.AudioTrack);
        if (state == null)
        {
            return 1;
        }

        return state.Muted ? 0 : Math.Clamp(state.Volume, 0, 2);
    }

    /// <summary>一个活跃片段的解码源（每个片段各自解码，互不干扰）。</summary>
    private sealed class AudioSource : IDisposable
    {
        private AudioSource(FFmpegAudioDecoder decoder)
        {
            Decoder = decoder;
            Reader = new AudioSpeedReader(decoder.ReadPcm);
        }

        public FFmpegAudioDecoder Decoder { get; }

        /// <summary>变速读源：按片段的播放速度/是否保持音调取 PCM（原速时直接透传）。</summary>
        public AudioSpeedReader Reader { get; }

        /// <summary>拉取缓冲（复用；按需扩容到一块的最大字节数）。</summary>
        public byte[]? Buffer { get; private set; }

        /// <summary>解码器当前位置对应的素材时间基准（秒）。</summary>
        public double SourceBase { get; set; }

        /// <summary>自 <see cref="SourceBase"/> 起已消费的采样帧数（由 <see cref="Reader"/> 记账）。</summary>
        public long ReadFrames => Reader.SourceFramesConsumed;

        /// <summary>
        /// 尝试为该片段建立音频源。素材缺失 / 不含音频轨 / 解码器不可用时返回 null（该片段静默）。
        /// </summary>
        public static AudioSource? TryCreate(VideoClip clip)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(clip.SourcePath) || !File.Exists(clip.SourcePath))
                {
                    return null;
                }

                // 解码库没就绪时直接调 FFmpeg 会抛 DllNotFoundException（被 catch 吞掉、表现为静音）：
                // 这里显式检查并记日志，避免「明明加了解码库却听不到声音」这类难查的问题。
                if (!FFmpegRuntime.IsAvailable || !FFmpegRuntime.EnsureLoaded())
                {
                    Log("FFmpeg 解码库不可用，工程音频无法解码（表现就是没有声音）");
                    return null;
                }

                var decoder = new FFmpegAudioDecoder();
                if (!decoder.Open(clip.SourcePath))
                {
                    decoder.Dispose();
                    return null;
                }

                return new AudioSource(decoder);
            }
            catch (Exception ex)
            {
                Log($"建立音频源失败（{clip.SourcePath}）：{ex.Message}");
                return null;
            }
        }

        public void EnsureCapacity(int bytes)
        {
            if (Buffer == null || Buffer.Length < bytes)
            {
                Buffer = new byte[Math.Max(bytes, SampleRate * FFmpegAudioDecoder.OutBytesPerSample)];
            }
        }

        public void Dispose()
        {
            try
            {
                Decoder.Dispose();
            }
            catch
            {
                // 释放失败不影响其它源。
            }

            Buffer = null;
        }
    }

    /// <summary>拉模型提供器：声卡要多少就现场混多少。</summary>
    private sealed class MixProvider : IWaveProvider
    {
        private readonly ProjectAudioMixer _owner;
        private readonly WaveFormat _format = new(FFmpegAudioDecoder.OutSampleRate,
            FFmpegAudioDecoder.OutBitsPerSample, FFmpegAudioDecoder.OutChannels);

        public MixProvider(ProjectAudioMixer owner)
        {
            _owner = owner;
        }

        public WaveFormat WaveFormat => _format;

        public int Read(byte[] buffer, int offset, int count) => _owner.Fill(buffer, offset, count);
    }
}
