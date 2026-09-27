using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 用 FFmpeg 解出视频文件里的音频轨，统一重采样为 48kHz / 立体声 / 16bit PCM，
/// 供 <see cref="VideoAudioPlayer"/> 经 NAudio 输出（供「视频背景播放声音」使用）。
/// <para>
/// 与 <see cref="FFmpegVideoDecoder"/> 相互独立：视频是「拉帧」模型（每帧一张位图），
/// 音频是「拉 PCM」模型（声卡要多少给多少）。两条解码链各自读同一个文件的不同流，
/// 互不阻塞——视频那边「限帧 + 等 UI 消费」的抖动不会传导到音频，音频才不会卡。
/// </para>
/// <para>
/// 全部原生调用 try/catch + 日志兜底：任何一步失败只会导致静音，绝不影响视频播放。
/// </para>
/// </summary>
internal sealed unsafe class FFmpegAudioDecoder : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[video-audio] {message}");

    /// <summary>统一输出采样率（NAudio 播放侧固定用它）。</summary>
    public const int OutSampleRate = 48000;

    /// <summary>统一输出声道数（源多声道由 swr 下混）。</summary>
    public const int OutChannels = 2;

    /// <summary>输出位深（16bit 有符号）。</summary>
    public const int OutBitsPerSample = 16;

    /// <summary>每采样字节数（2 声道 × 2 字节）。</summary>
    public const int OutBytesPerSample = OutChannels * OutBitsPerSample / 8;

    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _codecCtx;
    private SwrContext* _swr;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private int _streamIndex = -1;

    /// <summary>重采样输出缓冲（复用；_outOffset 起 _outBytes 为待消费数据）。</summary>
    private byte[]? _outBuf;
    private int _outBytes;
    private int _outOffset;

    /// <summary>解码器是否已收到 null packet 冲刷（输入 EOF 后只做一次）。</summary>
    private bool _decoderDrained;

    /// <summary>重采样器是否已冲刷完毕（此后 ReadPcm 返回 0）。</summary>
    private bool _swrDrained;

    /// <summary>文件里是否存在可用音频轨（打开后有效）。</summary>
    public bool HasAudio => _codecCtx != null;

    /// <summary>容器总时长（秒；0 = 未知）。</summary>
    public double Duration { get; private set; }

    /// <summary>源采样率（媒体信息展示用）。</summary>
    public int SourceSampleRate { get; private set; }

    /// <summary>源声道数。</summary>
    public int SourceChannels { get; private set; }

    /// <summary>源音频编码名（如 aac / mp3）。</summary>
    public string SourceCodecName { get; private set; } = string.Empty;

    /// <summary>
    /// 打开文件的音频流。没有音频轨 / 解码器不可用时返回 false（调用方静默降级为无声）。
    /// </summary>
    public bool Open(string path)
    {
        try
        {
            AVFormatContext* fmtCtx = null;
            var hr = ffmpeg.avformat_open_input(&fmtCtx, path, null, null);
            _fmtCtx = fmtCtx;
            if (hr < 0)
            {
                Log($"avformat_open_input 失败 {hr}");
                return false;
            }

            hr = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
            if (hr < 0)
            {
                Log($"avformat_find_stream_info 失败 {hr}");
                return false;
            }

            if (_fmtCtx->duration > 0)
            {
                Duration = _fmtCtx->duration / 1000000.0;
            }

            AVCodec* codec = null;
            _streamIndex = ffmpeg.av_find_best_stream(_fmtCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (_streamIndex < 0 || codec == null)
            {
                // 视频没声音是很常见的情况，不是错误。
                Log("文件不含音频流");
                return false;
            }

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null)
            {
                Log("avcodec_alloc_context3 失败");
                return false;
            }

            var cp = _fmtCtx->streams[_streamIndex]->codecpar;
            hr = ffmpeg.avcodec_parameters_to_context(_codecCtx, cp);
            if (hr < 0)
            {
                Log($"avcodec_parameters_to_context 失败 {hr}");
                return false;
            }

            hr = ffmpeg.avcodec_open2(_codecCtx, codec, null);
            if (hr < 0)
            {
                Log($"avcodec_open2 失败 {hr}（解码器={Marshal.PtrToStringAnsi((IntPtr)codec->name)}）");
                return false;
            }

            SourceSampleRate = _codecCtx->sample_rate;
            SourceCodecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "audio";

            // 输入声道布局：优先用解码器给出的；缺失/未指定时按声道数补一个默认布局，
            // 否则 swr 会因为「布局未知」直接失败（部分 mp4 的 aac 轨就是这样）。
            var inLayout = _codecCtx->ch_layout;
            if (inLayout.nb_channels <= 0 ||
                inLayout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                var channels = inLayout.nb_channels > 0 ? inLayout.nb_channels : OutChannels;
                ffmpeg.av_channel_layout_default(&inLayout, channels);
            }

            SourceChannels = inLayout.nb_channels;

            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, OutChannels);

            SwrContext* swr = null;
            var inFmt = (AVSampleFormat)_codecCtx->sample_fmt;
            hr = ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutSampleRate,
                &inLayout, inFmt, _codecCtx->sample_rate, 0, null);
            _swr = swr;
            if (hr < 0 || _swr == null)
            {
                Log($"swr_alloc_set_opts2 失败 {hr}");
                return false;
            }

            hr = ffmpeg.swr_init(_swr);
            if (hr < 0)
            {
                Log($"swr_init 失败 {hr}");
                return false;
            }

            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_pkt == null || _frame == null)
            {
                Log("av_packet_alloc / av_frame_alloc 失败");
                return false;
            }

            // 输出缓冲：按「一帧最大可能产出」预留，重采样后有富余（44.1k→48k 约 1.09 倍）。
            var maxOutSamples = Math.Max(4096, ffmpeg.swr_get_out_samples(_swr, 8192)) + 1024;
            _outBuf = new byte[maxOutSamples * OutBytesPerSample];
            _outBytes = 0;
            _outOffset = 0;

            Log($"已打开音频流: {SourceCodecName} {SourceSampleRate}Hz {SourceChannels}ch → {OutSampleRate}Hz {OutChannels}ch s16");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Open 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 拉取 PCM 数据：把解码+重采样后的 16bit 立体声交错字节写入 <paramref name="buffer"/>。
    /// 返回实际写入字节数；0 表示已到文件末尾（调用方决定是否循环复位）。
    /// </summary>
    public int ReadPcm(byte[] buffer, int offset, int count)
    {
        if (_fmtCtx == null || _codecCtx == null || _swr == null || _pkt == null || _frame == null)
        {
            return 0;
        }

        var written = 0;
        while (written < count)
        {
            // 先吐上一步重采样留在缓冲里的数据。
            if (_outOffset < _outBytes)
            {
                var n = Math.Min(count - written, _outBytes - _outOffset);
                Buffer.BlockCopy(_outBuf!, _outOffset, buffer, offset + written, n);
                _outOffset += n;
                written += n;
                continue;
            }

            if (!PumpNext())
            {
                break;
            }
        }

        return written;
    }

    /// <summary>解出下一批 PCM 到 <see cref="_outBuf"/>；已到末尾返回 false。</summary>
    private bool PumpNext()
    {
        while (true)
        {
            if (_swrDrained)
            {
                return false;
            }

            var r = ffmpeg.av_read_frame(_fmtCtx, _pkt);
            if (r < 0)
            {
                return Drain();
            }

            if (_pkt->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_pkt);
                continue;
            }

            var sr = ffmpeg.avcodec_send_packet(_codecCtx, _pkt);
            ffmpeg.av_packet_unref(_pkt);
            if (sr < 0)
            {
                continue;
            }

            while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
            {
                if (ConvertFrame())
                {
                    return true;
                }
            }
        }
    }

    /// <summary>输入 EOF：先冲刷解码器缓冲，再冲刷重采样器残留。</summary>
    private bool Drain()
    {
        if (!_decoderDrained)
        {
            _decoderDrained = true;
            ffmpeg.avcodec_send_packet(_codecCtx, null);
            while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
            {
                if (ConvertFrame())
                {
                    return true;
                }
            }
        }

        // 冲刷 swr：in = null / in_count = 0 表示把内部残留样本吐出来。
        try
        {
            fixed (byte* dst = _outBuf)
            {
                var outData = stackalloc byte*[1];
                outData[0] = dst;
                var maxSamples = _outBuf!.Length / OutBytesPerSample;
                var n = ffmpeg.swr_convert(_swr, outData, maxSamples, null, 0);
                if (n > 0)
                {
                    _outBytes = n * OutBytesPerSample;
                    _outOffset = 0;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"swr 冲刷异常: {ex.Message}");
        }

        _swrDrained = true;
        return false;
    }

    /// <summary>把当前解码帧重采样进输出缓冲；产出非空返回 true。</summary>
    private bool ConvertFrame()
    {
        try
        {
            if (_frame->nb_samples <= 0)
            {
                return false;
            }

            var maxSamples = _outBuf!.Length / OutBytesPerSample;
            fixed (byte* dst = _outBuf)
            {
                var outData = stackalloc byte*[1];
                outData[0] = dst;
                var n = ffmpeg.swr_convert(_swr, outData, maxSamples,
                    _frame->extended_data, _frame->nb_samples);
                if (n > 0)
                {
                    _outBytes = n * OutBytesPerSample;
                    _outOffset = 0;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"swr_convert 异常: {ex.Message}");
        }

        return false;
    }

    /// <summary>循环播放复位：seek 回开头并重置解码器 / 重采样器状态。</summary>
    public bool Restart()
    {
        if (_fmtCtx == null || _codecCtx == null || _swr == null)
        {
            return false;
        }

        try
        {
            var hr = ffmpeg.av_seek_frame(_fmtCtx, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (hr < 0)
            {
                Log($"av_seek_frame 失败 {hr}");
                return false;
            }

            ResetAfterSeek();
            return true;
        }
        catch (Exception ex)
        {
            Log($"Restart 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>跳到指定时间点（秒），供片段入点裁剪。</summary>
    public bool SeekTo(double seconds)
    {
        if (_fmtCtx == null || _codecCtx == null || _swr == null || _streamIndex < 0)
        {
            return false;
        }

        try
        {
            if (seconds <= 0)
            {
                return Restart();
            }

            var stream = _fmtCtx->streams[_streamIndex];
            var tb = stream->time_base;
            var tbSeconds = tb.num / (double)tb.den;
            if (tbSeconds <= 0)
            {
                return Restart();
            }

            var ts = (long)(seconds / tbSeconds);
            var hr = ffmpeg.av_seek_frame(_fmtCtx, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (hr < 0)
            {
                Log($"av_seek_frame 失败 {hr}（秒={seconds:0.###}）");
                return false;
            }

            ResetAfterSeek();
            return true;
        }
        catch (Exception ex)
        {
            Log($"SeekTo 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>seek 之后统一清理：解码器缓冲、重采样器内部状态、待吐数据。</summary>
    private void ResetAfterSeek()
    {
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        // swr 内部可能还留着上一段的位置，close + init 是最干净的复位方式。
        ffmpeg.swr_close(_swr);
        ffmpeg.swr_init(_swr);
        _outBytes = 0;
        _outOffset = 0;
        _decoderDrained = false;
        _swrDrained = false;
    }

    public void Dispose()
    {
        try
        {
            if (_frame != null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_pkt != null)
            {
                var pkt = _pkt;
                ffmpeg.av_packet_free(&pkt);
                _pkt = null;
            }

            if (_swr != null)
            {
                var swr = _swr;
                ffmpeg.swr_free(&swr);
                _swr = null;
            }

            if (_codecCtx != null)
            {
                var ctx = _codecCtx;
                ffmpeg.avcodec_free_context(&ctx);
                _codecCtx = null;
            }

            if (_fmtCtx != null)
            {
                var fmt = _fmtCtx;
                ffmpeg.avformat_close_input(&fmt);
                _fmtCtx = null;
            }
        }
        catch (Exception ex)
        {
            Log($"Dispose 异常: {ex.Message}");
        }

        _outBuf = null;
        _outBytes = 0;
        _outOffset = 0;
    }
}
