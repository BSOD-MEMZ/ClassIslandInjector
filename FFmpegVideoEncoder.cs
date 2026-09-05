using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 用 FFmpeg（libx264）把 BGRA 帧编码为 H.264 mp4，供「渲染并应用」把视频工程
/// 逐帧合成后输出 mp4 使用。依赖 FFmpeg 原生库（由 <see cref="FFmpegRuntime"/>
/// 检测/下载；libx264 包含在标准共享构建里）。
/// 用法：构造（打开输出）→ 逐帧 <see cref="EncodeFrame"/> → <see cref="Finish"/>。
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

    public FFmpegVideoEncoder(string outputPath, int width, int height, int crf, int fps, string preset = "medium",
        string? hwEncoder = null)
    {
        _width = width;
        _height = height;
        _hwEncoder = hwEncoder;
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

    private void DrainPackets()
    {
        while (true)
        {
            var rr = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
            if (rr == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rr == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (rr < 0)
            {
                throw new InvalidOperationException($"取回编码数据失败（{rr}）");
            }

            // 编码器输出的时间戳在其自身时间基上；显式换算到流时间基，并把
            // pkt.time_base 同步为流时间基（新版 muxer 据此跳过二次换算），
            // 修复硬件编码路径上 time_base 不一致导致的 mp4 帧率/时长异常。
            ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _tbStream);
            _pkt->time_base = _tbStream;
            ffmpeg.av_interleaved_write_frame(_fmtCtx, _pkt);
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    /// <summary>刷新编码器并写结尾，关闭输出文件。</summary>
    public void Finish()
    {
        if (!_opened || _finished)
        {
            return;
        }

        _finished = true;
        ffmpeg.avcodec_send_frame(_codecCtx, null); // 刷新编码器尾帧
        DrainPackets();
        ffmpeg.av_write_trailer(_fmtCtx);
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

        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
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
