using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassIslandInjector;

/// <summary>
/// 视频背景的音频输出：把 <see cref="FFmpegAudioDecoder"/> 解出的 PCM 经 NAudio（WASAPI 共享模式）
/// 送到默认输出设备，实现「视频背景播放声音」。
/// <para>
/// 采用 <see cref="IWaveProvider"/> 拉模型——声卡要多少 PCM 就现解多少，不在内存里堆缓冲：
/// 播放节奏由声卡决定，长时间播放不会累积漂移，也不需要单独的对齐线程。
/// 音频与视频各自解码、互不阻塞（视频那条链路的限帧抖动不会拖慢音频）。
/// </para>
/// <para>
/// 一切失败都静默降级为无声：没有音频轨、设备被独占、NAudio 不可用都只记日志，不影响视频画面。
/// </para>
/// </summary>
internal sealed class VideoAudioPlayer : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[video-audio] {message}");

    /// <summary>解码器访问锁：Read（音频线程）与 Start/Stop/Seek/Dispose（UI 线程）互斥。</summary>
    private readonly object _sync = new();

    private FFmpegAudioDecoder? _decoder;
    private WasapiOut? _output;
    private DecoderWaveProvider? _provider;
    private volatile bool _disposed;
    private volatile float _volume = 1f;
    private volatile bool _loop = true;

    /// <summary>音频输出当前是否在播。</summary>
    public bool IsRunning => _output != null;

    /// <summary>当前音量（0-1）。</summary>
    public double Volume => _volume;

    /// <summary>
    /// 启动音频输出。返回 false 表示该文件没有可用音频轨或设备不可用（调用方无需特别处理，画面照常）。
    /// </summary>
    public bool Start(string path, double volume, bool loop, double startSeconds = 0)
    {
        Stop();
        _disposed = false;

        try
        {
            var decoder = new FFmpegAudioDecoder();
            if (!decoder.Open(path))
            {
                decoder.Dispose();
                return false;
            }

            if (startSeconds > 0.05)
            {
                decoder.SeekTo(startSeconds);
            }

            _decoder = decoder;
            _volume = (float)Math.Clamp(volume, 0, 1);
            _loop = loop;
            _provider = new DecoderWaveProvider(decoder, this);

            // WASAPI 共享模式：不独占设备，与其它程序（音乐软件等）可共存。
            var output = new WasapiOut(AudioClientShareMode.Shared, 100);
            output.Init(_provider);
            output.Play();
            _output = output;

            Log($"音频输出已启动：音量={_volume:0.##} 循环={_loop} 起点={startSeconds:0.##}s 源={decoder.SourceCodecName} " +
                $"{decoder.SourceSampleRate}Hz/{decoder.SourceChannels}ch");
            return true;
        }
        catch (Exception ex)
        {
            Log($"音频输出启动失败：{ex.Message}");
            Stop();
            return false;
        }
    }

    /// <summary>停止并释放音频输出（可再次 Start）。</summary>
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
            // 设备已拔出 / 音频服务重启：停止失败无所谓，下面照样释放。
        }

        try
        {
            output?.Dispose();
        }
        catch
        {
            // 同上，释放失败不应影响视频侧。
        }

        lock (_sync)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }

    /// <summary>调整音量（即时生效，无需重启播放）。</summary>
    public void SetVolume(double volume) => _volume = (float)Math.Clamp(volume, 0, 1);

    /// <summary>更新循环标记。</summary>
    public void SetLoop(bool loop) => _loop = loop;

    /// <summary>把音频位置对到指定秒（不重启设备）。</summary>
    public void Seek(double seconds)
    {
        lock (_sync)
        {
            _decoder?.SeekTo(seconds);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// 拉模型提供器：声卡每次要 <c>count</c> 字节，就从解码器现拉。
    /// 到文件末尾时——循环则复位继续，不循环则补静音（返回 0 会让 WASAPI 直接判定流结束并停止输出）。
    /// </summary>
    private sealed class DecoderWaveProvider : IWaveProvider
    {
        private readonly FFmpegAudioDecoder _decoder;
        private readonly VideoAudioPlayer _owner;
        private readonly WaveFormat _format = new(FFmpegAudioDecoder.OutSampleRate,
            FFmpegAudioDecoder.OutBitsPerSample, FFmpegAudioDecoder.OutChannels);

        public DecoderWaveProvider(FFmpegAudioDecoder decoder, VideoAudioPlayer owner)
        {
            _decoder = decoder;
            _owner = owner;
        }

        public WaveFormat WaveFormat => _format;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_owner._disposed)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var got = 0;
            lock (_owner._sync)
            {
                if (_owner._disposed)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                got = _decoder.ReadPcm(buffer, offset, count);
                if (got == 0 && _owner._loop && _decoder.Restart())
                {
                    got = _decoder.ReadPcm(buffer, offset, count);
                }
            }

            if (got <= 0)
            {
                // 播完且不循环（或解码彻底失败）：补静音维持输出在线，避免 WASAPI 停流。
                Array.Clear(buffer, offset, count);
                got = count;
            }
            else if (got < count)
            {
                // 尾部不足：补静音，保证每次都返回完整块。
                Array.Clear(buffer, offset + got, count - got);
                got = count;
            }

            ApplyVolume(buffer, offset, got, _owner._volume);
            return got;
        }

        /// <summary>按音量缩放 16bit 交错采样（不改 WaveFormat，设备兼容性最好）。</summary>
        private static void ApplyVolume(byte[] buffer, int offset, int count, float volume)
        {
            if (volume >= 0.999f)
            {
                return;
            }

            var end = offset + (count & ~1);
            if (volume <= 0.0001f)
            {
                Array.Clear(buffer, offset, count & ~1);
                return;
            }

            for (var i = offset; i < end; i += 2)
            {
                var s = (short)(buffer[i] | (buffer[i + 1] << 8));
                s = (short)(s * volume);
                buffer[i] = (byte)(s & 0xFF);
                buffer[i + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
    }
}
