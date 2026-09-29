using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 用 FFmpeg（libx264 + 原生 AAC）把 BGRA 帧与 PCM 编码为 mp4，供「渲染并应用」把视频工程
/// 逐帧合成后输出 mp4 使用。依赖 FFmpeg 原生库（由 <see cref="FFmpegRuntime"/>
/// 检测/下载；libx264 与 aac 编码器、swresample 都包含在标准共享构建里）。
/// 用法：构造（打开输出）→ 逐帧 <see cref="EncodeFrame"/> + <see cref="EncodeAudio"/> → <see cref="Finish"/>。
/// 所有调用都有 throw 兜底；调用方捕获异常提示用户。
/// </summary>
internal sealed unsafe class FFmpegVideoEncoder : IDisposable
{
    private AVFormatContext* _fmtCtx;
    private AVCodecContext* _codecCtx;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private AVStream* _stream;
    /// <summary>写头后复用器最终确定的流时间基（pkt 时间戳换算目标）。</summary>
    private AVRational _tbStream;
    private readonly int _width;
    private readonly int _height;
    private readonly string? _hwEncoder;
    /// <summary>编码器输入像素格式：软编 yuv420p，硬编 nv12。</summary>
    private AVPixelFormat _swsOutFormat;
    private long _frameIndex;
    private bool _finished;
    private bool _opened;

    // ---- 音频（可选；「包含音频」时输出第二条 AAC 音轨）----
    private readonly bool _withAudio;
    private readonly int _audioRate;
    private readonly int _audioChannels;
    private readonly int _audioBitrate;
    private AVCodecContext* _audioCtx;
    private AVStream* _audioStream;
    private AVRational _tbAudioStream;
    private AVFrame* _audioFrame;
    private AVPacket* _audioPkt;
    /// <summary>PCM 重采样/重排：s16 交错 → fltp 平面（AAC 只吃 fltp）。</summary>
    private SwrContext* _swr;
    /// <summary>AAC 帧长（1024；编码器要求每次送的样本数正好是一帧）。</summary>
    private int _audioFrameSize;
    /// <summary>已送入编码器的样本帧数（= 下一帧的 pts 基准，整数累加不漂）。</summary>
    private long _audioSamplesSubmitted;
    /// <summary>攒帧缓冲：凑满一整帧才送编码器（AAC 不接受任意帧长）。</summary>
    private byte[] _audioPending = [];
    private int _audioPendingBytes;
    /// <summary>诊断：还要打多少个包（&lt;0 = 已停止）。</summary>
    private int _tracePackets = 8;

    /// <summary>是否带音轨（构造时请求且成功建立）。</summary>
    public bool HasAudio => _audioStream != null;

    /// <summary>
    /// 诊断钩子（可选，默认关闭）：把流索引 / 时间基 / 每个包的时间戳打出来。
    /// 复用器的时间戳问题（dts 非单调、时间基错配）只在 FFmpeg 自己的 log 里露一行，
    /// 排查时打开它能看到「谁在什么时候送了哪个时间戳」。
    /// </summary>
    public static Action<string>? Trace { get; set; }

    private static void TraceLog(string message) => Trace?.Invoke(message);

    public FFmpegVideoEncoder(string outputPath, int width, int height, int crf, int fps, string preset = "medium",
        string? hwEncoder = null, bool withAudio = false, int audioSampleRate = 48_000, int audioChannels = 2,
        int audioBitrate = 128_000)
    {
        _width = width;
        _height = height;
        _hwEncoder = hwEncoder;
        _withAudio = withAudio;
        _audioRate = audioSampleRate;
        _audioChannels = audioChannels;
        _audioBitrate = audioBitrate;
        Open(outputPath, crf, fps, preset);
    }

    private void Open(string path, int crf, int fps, string preset)
    {
        // 打开输出容器（mp4）。局部变量取地址，字段不能取地址。
        AVFormatContext* fmtCtx = null;
        var hr = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mp4", path);
        if (hr < 0 || fmtCtx == null)
        {
            throw new InvalidOperationException($"创建输出上下文失败（{hr}）");
        }

        _fmtCtx = fmtCtx;

        // 编码器：指定硬件编码器（h264_qsv/nvenc/amf/mf）时优先用之（渲染提速），失败抛异常由调用方回退软编。
        // 注意：本机 MF 解码管线异常，但 h264_mf 编码器走的是系统编码 MFT，不受影响。
        var hwName = _hwEncoder;
        AVCodec* codec = null;
        if (!string.IsNullOrWhiteSpace(hwName))
        {
            codec = ffmpeg.avcodec_find_encoder_by_name(hwName);
            if (codec == null)
            {
                throw new InvalidOperationException($"硬件编码器 {hwName} 在当前 FFmpeg 包中不可用");
            }
        }
        else
        {
            codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                throw new InvalidOperationException("找不到 H.264 编码器（libx264），请检查 FFmpeg 库完整性");
            }
        }

        var isHardware = !string.IsNullOrWhiteSpace(hwName);
        _swsOutFormat = isHardware ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
        {
            throw new InvalidOperationException("分配编码上下文失败");
        }

        _codecCtx->width = _width;
        _codecCtx->height = _height;
        _codecCtx->time_base = new AVRational { num = 1, den = fps };
        _codecCtx->framerate = new AVRational { num = fps, den = 1 };
        _codecCtx->pix_fmt = _swsOutFormat;
        _codecCtx->gop_size = 12;
        _codecCtx->max_b_frames = 2;
        if (isHardware)
        {
            // 硬编统一走码率模式（各硬件编码器对 CRF/preset 私有选项支持不一，码率最通用）。
            // 码率按分辨率/帧率估算（约 0.07 bit/像素/帧），范围 1~20 Mbps。
            _codecCtx->bit_rate = Math.Clamp((long)(_width * (long)_height * fps * 0.07), 1_000_000, 20_000_000);
        }
        else
        {
            // 用 CRF 控制质量（数值越小越清晰、文件越大）；preset 控制编码速度
            // （veryfast 约比 medium 快 3~5 倍，同 CRF 下体积略大，壁纸用途可接受）。
            ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", preset, 0);
            ffmpeg.av_opt_set_int(_codecCtx->priv_data, "crf", crf, 0);
        }

        hr = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (hr < 0)
        {
            // 打开失败（如机器无对应硬件）：释放本方法已分配的资源后抛出，由调用方回退软编。
            var stale = _codecCtx;
            ffmpeg.avcodec_free_context(&stale);
            _codecCtx = null;
            ffmpeg.avformat_free_context(_fmtCtx);
            _fmtCtx = null;
            throw new InvalidOperationException($"打开编码器失败（{hr}）");
        }

        // ★ 硬件编码器（QSV 等）可能在 open 时改写 time_base（实测 QSV 改为 1/12288 之类），
        // 复用器随后采纳它 → mp4 帧率元数据异常（播放器显示 12300fps、时长缩水）。
        // open 后强制恢复 1/fps，帧时间戳按真实帧率推进。
        _codecCtx->time_base = new AVRational { num = 1, den = fps };

        var stream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
        if (stream == null)
        {
            Dispose();
            throw new InvalidOperationException("创建视频流失败");
        }

        hr = ffmpeg.avcodec_parameters_from_context(stream->codecpar, _codecCtx);
        if (hr < 0)
        {
            Dispose();
            throw new InvalidOperationException($"复制编码参数失败（{hr}）");
        }

        stream->time_base = new AVRational { num = 1, den = fps };
        // 显式声明帧率元数据（部分播放器/探测工具直接按它显示）。
        stream->avg_frame_rate = new AVRational { num = fps, den = 1 };
        stream->r_frame_rate = new AVRational { num = fps, den = 1 };

        if (_withAudio)
        {
            try
            {
                OpenAudioStream();
            }
            catch
            {
                // 音频流建立失败（缺 AAC 编码器 / 上下文分配失败）：
                // 把已分配的视频上下文一并释放，避免 native 泄漏，异常由调用方处理。
                Dispose();
                throw;
            }
        }

        hr = ffmpeg.avio_open(&_fmtCtx->pb, path, ffmpeg.AVIO_FLAG_WRITE);
        if (hr < 0)
        {
            Dispose();
            throw new InvalidOperationException($"无法写入输出文件（{hr}）");
        }

        hr = ffmpeg.avformat_write_header(_fmtCtx, null);
        if (hr < 0)
        {
            Dispose();
            throw new InvalidOperationException($"写入文件头失败（{hr}）");
        }

        // 复用器可能调整流时间基；记录最终值，pkt 时间戳统一换算到它。
        _stream = stream;
        _tbStream = stream->time_base;
        if (_audioStream != null)
        {
            _tbAudioStream = _audioStream->time_base;
        }

        TraceLog($"写头完成：视频流#{stream->index} tb={_tbStream.num}/{_tbStream.den} "
                 + $"codecpar={stream->codecpar->format} extradata={stream->codecpar->extradata_size}B"
                 + (_audioStream != null
                     ? $"；音频流#{_audioStream->index} tb={_tbAudioStream.num}/{_tbAudioStream.den} "
                       + $"extradata={_audioStream->codecpar->extradata_size}B frameSize={_audioFrameSize}"
                     : "；无音频流"));

        _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            _width, _height, _swsOutFormat, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null)
        {
            throw new InvalidOperationException("创建颜色转换器失败");
        }

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)_swsOutFormat;
        _frame->width = _width;
        _frame->height = _height;
        ffmpeg.av_frame_get_buffer(_frame, 32);

        _pkt = ffmpeg.av_packet_alloc();
        _opened = true;
    }

    /// <summary>
    /// 建立音频流：原生 AAC 编码器 + s16 交错 → fltp 平面的重采样上下文。
    /// 只在 <see cref="_withAudio"/> 时调用（须在 avformat_write_header 之前）。
    /// </summary>
    private void OpenAudioStream()
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
        {
            throw new InvalidOperationException("找不到 AAC 编码器，请检查 FFmpeg 包完整性");
        }

        _audioCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_audioCtx == null)
        {
            throw new InvalidOperationException("分配音频编码上下文失败");
        }

        _audioCtx->bit_rate = _audioBitrate;
        _audioCtx->sample_rate = _audioRate;
        ffmpeg.av_channel_layout_default(&_audioCtx->ch_layout, _audioChannels);
        _audioCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        // 音频流时间基 = 1/采样率（采样点位置），与视频流各自的 time_base 独立换算。
        _audioCtx->time_base = new AVRational { num = 1, den = _audioRate };

        var hr = ffmpeg.avcodec_open2(_audioCtx, codec, null);
        if (hr < 0)
        {
            throw new InvalidOperationException($"打开 AAC 编码器失败（{hr}）");
        }

        _audioFrameSize = _audioCtx->frame_size > 0 ? _audioCtx->frame_size : 1024;
        _audioPending = new byte[_audioFrameSize * FFmpegAudioDecoder.OutBytesPerSample * 2];

        _audioStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
        if (_audioStream == null)
        {
            throw new InvalidOperationException("创建音频流失败");
        }

        hr = ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCtx);
        if (hr < 0)
        {
            throw new InvalidOperationException($"复制音频编码参数失败（{hr}）");
        }

        _audioStream->time_base = new AVRational { num = 1, den = _audioRate };

        // s16 交错（解码器统一输出格式）→ fltp 平面（AAC 唯一支持）。同采样率同声道数，
        // 纯格式重排、无重采样延迟，所以离线渲染不会引入额外时移。
        SwrContext* swr = null;
        AVChannelLayout inLayout = default;
        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&inLayout, _audioChannels);
        ffmpeg.av_channel_layout_default(&outLayout, _audioChannels);
        hr = ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, _audioRate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, _audioRate, 0, null);
        if (hr < 0 || swr == null || ffmpeg.swr_init(swr) < 0)
        {
            throw new InvalidOperationException("创建音频重采样上下文失败");
        }

        _swr = swr;
        _audioFrame = ffmpeg.av_frame_alloc();
        _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioFrame->nb_samples = _audioFrameSize;
        ffmpeg.av_channel_layout_default(&_audioFrame->ch_layout, _audioChannels);
        _audioFrame->sample_rate = _audioRate;
        hr = ffmpeg.av_frame_get_buffer(_audioFrame, 0);
        if (hr < 0)
        {
            throw new InvalidOperationException($"分配音频帧缓冲失败（{hr}）");
        }

        _audioPkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>编码一帧 BGRA（宽高 = 构造时尺寸）。</summary>
    public void EncodeFrame(byte[] bgra)
    {
        if (!_opened || _finished)
        {
            return;
        }

        fixed (byte* p = bgra)
        {
            var srcData = new byte*[] { p };
            var srcLinesize = new int[] { _width * 4 };
            ffmpeg.sws_scale(_sws, srcData, srcLinesize, 0, _height, _frame->data, _frame->linesize);
        }

        _frame->pts = _frameIndex++;
        var sr = ffmpeg.avcodec_send_frame(_codecCtx, _frame);
        if (sr < 0)
        {
            throw new InvalidOperationException($"送入编码器失败（{sr}）");
        }

        DrainPackets();
    }

    /// <summary>取回某路编码器（视频或音频）已产出的包并写入复用器。</summary>
    private void DrainPackets(AVCodecContext* ctx, AVPacket* pkt, AVRational streamTimeBase, int streamIndex)
    {
        while (true)
        {
            var rr = ffmpeg.avcodec_receive_packet(ctx, pkt);
            if (rr == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rr == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (rr < 0)
            {
                throw new InvalidOperationException($"取回编码数据失败（{rr}）");
            }

            // ⚠️ 编码器不会设置 stream_index（默认 0）。必须自己按所属流写上，
            // 否则音频包会被当成视频包塞进 0 号流：复用器报「dts 非单调」并把视频轨写坏
            // （产物只剩几百字节、视频参数缺失）。
            pkt->stream_index = streamIndex;

            // 编码器输出的时间戳在其自身时间基上；显式换算到流时间基，并把
            // pkt.time_base 同步为流时间基（新版 muxer 据此跳过二次换算），
            // 修复硬件编码路径上 time_base 不一致导致的 mp4 帧率/时长异常。
            var rawPts = pkt->pts;
            var rawDts = pkt->dts;
            ffmpeg.av_packet_rescale_ts(pkt, ctx->time_base, streamTimeBase);
            pkt->time_base = streamTimeBase;
            if (_tracePackets > 0)
            {
                _tracePackets--;
                TraceLog($"  包 stream={pkt->stream_index} 编码器tb={ctx->time_base.num}/{ctx->time_base.den} "
                         + $"raw(pts={rawPts},dts={rawDts}) → 换算(pts={pkt->pts},dts={pkt->dts}) "
                         + $"streamTb={streamTimeBase.num}/{streamTimeBase.den} size={pkt->size}");
            }

            var wret = ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt);
            if (wret < 0 && _tracePackets >= 0)
            {
                _tracePackets = -1; // 只报第一次写失败
                TraceLog($"  ⚠ 写包失败 stream={pkt->stream_index} pts={pkt->pts} dts={pkt->dts} → {wret}");
            }

            ffmpeg.av_packet_unref(pkt);
        }
    }

    /// <summary>取回视频编码器产出的包（写入复用器）。</summary>
    private void DrainPackets() => DrainPackets(_codecCtx, _pkt, _tbStream, _stream->index);

    /// <summary>
    /// 送入一段 PCM 参与编码。<paramref name="frames"/> 为采样帧数（每帧含全部声道）。
    /// 参数与 <see cref="FFmpegAudioDecoder"/> 的输出格式一致（48kHz / 2ch / s16 交错）。
    /// 内部按 AAC 帧长攒帧，调用方按输出帧长任意切分即可。
    /// </summary>
    public void EncodeAudio(byte[] pcm, int offset, int frames)
    {
        if (!_opened || _finished || _audioCtx == null || frames <= 0)
        {
            return;
        }

        var bpf = FFmpegAudioDecoder.OutBytesPerSample;
        var bytes = frames * bpf;
        var frameBytes = _audioFrameSize * bpf;

        // 先把自己缓冲里不足一帧的尾巴补上，凑满就编一帧。
        var pos = offset;
        var remaining = bytes;
        while (remaining > 0)
        {
            var take = Math.Min(frameBytes - _audioPendingBytes, remaining);
            Buffer.BlockCopy(pcm, pos, _audioPending, _audioPendingBytes, take);
            _audioPendingBytes += take;
            pos += take;
            remaining -= take;

            if (_audioPendingBytes == frameBytes)
            {
                EncodeAudioFrame(_audioPending, _audioFrameSize);
                _audioPendingBytes = 0;
            }
        }
    }

    /// <summary>把正好一帧的 s16 交错 PCM 编成 AAC 包（fltp 转换 + 时间戳推进）。</summary>
    private void EncodeAudioFrame(byte[] pcm, int frames)
    {
        fixed (byte* p = pcm)
        {
            byte*[] inData = [p];
            var outData = new byte*[_audioChannels];
            for (var c = 0; c < _audioChannels; c++)
            {
                outData[c] = _audioFrame->data[(uint)c];
            }

            int got;
            fixed (byte** inPtr = inData)
            fixed (byte** outPtr = outData)
            {
                got = ffmpeg.swr_convert(_swr, outPtr, frames, inPtr, frames);
            }

            if (got < 0)
            {
                throw new InvalidOperationException($"音频重采样失败（{got}）");
            }

            if (got < frames)
            {
                // 同采样率同声道数不应发生；真发生则补静音，避免送出半帧。
                for (var c = 0; c < _audioChannels; c++)
                {
                    var plane = (float*)_audioFrame->data[(uint)c];
                    for (var i = got; i < frames; i++)
                    {
                        plane[i] = 0f;
                    }
                }
            }
        }

        _audioFrame->nb_samples = frames;
        _audioFrame->pts = _audioSamplesSubmitted;
        _audioSamplesSubmitted += frames;

        var sr = ffmpeg.avcodec_send_frame(_audioCtx, _audioFrame);
        if (sr < 0)
        {
            throw new InvalidOperationException($"送入音频编码器失败（{sr}）");
        }

        DrainPackets(_audioCtx, _audioPkt, _tbAudioStream, _audioStream->index);
    }

    /// <summary>刷新编码器并写结尾，关闭输出文件。</summary>
    public void Finish()
    {
        if (!_opened || _finished)
        {
            return;
        }

        _finished = true;
        ffmpeg.avcodec_send_frame(_codecCtx, null); // 刷新视频编码器尾帧
        DrainPackets();

        if (_audioCtx != null)
        {
            // 尾巴不足一帧：补静音凑满再送（最多 21ms 静音，播放器感知不到）。
            if (_audioPendingBytes > 0)
            {
                var frameBytes = _audioFrameSize * FFmpegAudioDecoder.OutBytesPerSample;
                Array.Clear(_audioPending, _audioPendingBytes, frameBytes - _audioPendingBytes);
                EncodeAudioFrame(_audioPending, _audioFrameSize);
                _audioPendingBytes = 0;
            }

            ffmpeg.avcodec_send_frame(_audioCtx, null);
            DrainPackets(_audioCtx, _audioPkt, _tbAudioStream, _audioStream->index);
        }

        ffmpeg.av_write_trailer(_fmtCtx);
        TraceLog($"写尾完成：音轨已输出样本帧数={_audioSamplesSubmitted}"
                 + (_audioStream != null ? $"（{_audioSamplesSubmitted / (double)_audioRate:0.###}s）" : ""));
    }

    public void Dispose()
    {
        try
        {
            if (_opened && !_finished)
            {
                Finish();
            }
        }
        catch
        {
            // 清理阶段异常忽略（文件可能不完整，但资源必须释放）。
        }

        if (_pkt != null)
        {
            var p = _pkt;
            ffmpeg.av_packet_free(&p);
            _pkt = null;
        }

        if (_audioPkt != null)
        {
            var ap = _audioPkt;
            ffmpeg.av_packet_free(&ap);
            _audioPkt = null;
        }

        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_audioFrame != null)
        {
            var af = _audioFrame;
            ffmpeg.av_frame_free(&af);
            _audioFrame = null;
        }

        if (_swr != null)
        {
            var swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }

        if (_audioCtx != null)
        {
            var ac = _audioCtx;
            ffmpeg.avcodec_free_context(&ac);
            _audioCtx = null;
            _audioStream = null;
        }

        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_codecCtx != null)
        {
            var c = _codecCtx;
            ffmpeg.avcodec_free_context(&c);
            _codecCtx = null;
        }

        if (_fmtCtx != null)
        {
            ffmpeg.avformat_free_context(_fmtCtx);
            _fmtCtx = null;
        }
    }
}
