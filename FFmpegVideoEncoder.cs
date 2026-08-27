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
    private readonly int _width;
    private readonly int _height;
    private long _frameIndex;
    private bool _finished;
    private bool _opened;

    public FFmpegVideoEncoder(string outputPath, int width, int height, int crf, int fps)
    {
        _width = width;
        _height = height;
        Open(outputPath, crf, fps);
    }

    private void Open(string path, int crf, int fps)
    {
        // 打开输出容器（mp4）。局部变量取地址，字段不能取地址。
        AVFormatContext* fmtCtx = null;
        var hr = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mp4", path);
        if (hr < 0 || fmtCtx == null)
        {
            throw new InvalidOperationException($"创建输出上下文失败（{hr}）");
        }

        _fmtCtx = fmtCtx;

        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
        {
            throw new InvalidOperationException("找不到 H.264 编码器（libx264），请检查 FFmpeg 库完整性");
        }

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
        {
            throw new InvalidOperationException("分配编码上下文失败");
        }

        _codecCtx->width = _width;
        _codecCtx->height = _height;
        _codecCtx->time_base = new AVRational { num = 1, den = fps };
        _codecCtx->framerate = new AVRational { num = fps, den = 1 };
        _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _codecCtx->gop_size = 12;
        _codecCtx->max_b_frames = 2;
        // 用 CRF 控制质量（数值越小越清晰、文件越大）。
        ffmpeg.av_opt_set(_codecCtx->priv_data, "preset", "medium", 0);
        ffmpeg.av_opt_set_int(_codecCtx->priv_data, "crf", crf, 0);

        hr = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (hr < 0)
        {
            throw new InvalidOperationException($"打开编码器失败（{hr}）");
        }

        var stream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
        if (stream == null)
        {
            throw new InvalidOperationException("创建视频流失败");
        }

        hr = ffmpeg.avcodec_parameters_from_context(stream->codecpar, _codecCtx);
        if (hr < 0)
        {
            throw new InvalidOperationException($"复制编码参数失败（{hr}）");
        }

        stream->time_base = new AVRational { num = 1, den = fps };

        hr = ffmpeg.avio_open(&_fmtCtx->pb, path, ffmpeg.AVIO_FLAG_WRITE);
        if (hr < 0)
        {
            throw new InvalidOperationException($"无法写入输出文件（{hr}）");
        }

        hr = ffmpeg.avformat_write_header(_fmtCtx, null);
        if (hr < 0)
        {
            throw new InvalidOperationException($"写入文件头失败（{hr}）");
        }

        _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            _width, _height, AVPixelFormat.AV_PIX_FMT_YUV420P, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null)
        {
            throw new InvalidOperationException("创建颜色转换器失败");
        }

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
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
