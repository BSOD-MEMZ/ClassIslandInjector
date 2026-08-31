namespace ClassIslandInjector;

/// <summary>
/// 把视频工程渲染为 mp4：统一时间轴推进，逐帧对每个轨道拉取对应片段帧，
/// 按片段的显示变换（原比例基准 + 缩放/旋转/偏移/裁剪/不透明度）逐像素合成到
/// 输出画布，再交给 <see cref="FFmpegVideoEncoder"/> 编码为 H.264 mp4。
/// 与编辑器舞台预览（<see cref="Views.VideoEditorWindow"/>）用同一套变换语义：
/// 默认原比例居中（不拉伸），Scale=1 即原始画面比例。
/// 渲染在调用方线程执行（建议后台线程 + 进度回调），帧合成纯 CPU。
/// </summary>
internal sealed class VideoProjectRenderer
{
    private readonly VideoProject _project;
    private readonly string _outputPath;
    private readonly int _outW;
    private readonly int _outH;
    private readonly int _crf;
    /// <summary>x264 预设（画质档映射：高=medium / 中=faster / 低=veryfast，越快渲染越快）。</summary>
    private readonly string _preset;
    /// <summary>硬件编码器名（"auto" = 自动探测 qsv→nvenc→amf→mf；null = 软编）。</summary>
    private readonly string? _hwEncoder;
    /// <summary>硬件解码器名（null = 软解）。</summary>
    private readonly string? _hwDecoder;
    private readonly int _fps;
    private readonly int _maxDimension;
    private readonly Action<double, string>? _progress;

    private sealed class TrackState
    {
        public int Track;
        public VideoFrameSource? Source;
        public VideoClip? ActiveClip;
        /// <summary>覆盖层片段生成的静态帧（只生成一次，渲染输出分辨率）。</summary>
        public VideoFrame? OverlayBuffer;
        /// <summary>最近一帧的拷贝（EOF 后冻结最后画面，避免轨道中途变透明）。</summary>
        public byte[]? LastBuffer;
        public int LastWidth;
        public int LastHeight;
        /// <summary>该轨已消费到的媒体帧号（相对片段开头，按源帧率换算）；-1 = 尚未消费。每输出帧按帧号差消费多帧，速度与时间轴一致（旧版每输出帧只拉 1 帧 = 高帧率源被慢放）。</summary>
        public long LastFrameIndex = -1;
        /// <summary>该轨源的帧率（打开时记录）。</summary>
        public double SourceFps;
        /// <summary>该轨源已到 EOF（冻结最后画面）。</summary>
        public bool Eof;
    }

    public VideoProjectRenderer(VideoProject project, string outputPath, int outW, int outH, int crf, int fps,
        Action<double, string>? progress = null, string preset = "medium",
        string? hwEncoder = null, string? hwDecoder = null)
    {
        _project = project;
        _outputPath = outputPath;
        _outW = outW;
        _outH = outH;
        _crf = crf;
        _fps = Math.Max(1, fps);
        _maxDimension = Math.Max(outW, outH);
        _progress = progress;
        _preset = preset;
        _hwEncoder = hwEncoder;
        _hwDecoder = hwDecoder;
    }

    public void Render()
    {
        var clips = _project.Clips.ToList();
        var duration = _project.Duration;
        if (duration <= 0 || clips.Count == 0)
        {
            return;
        }

        var totalFrames = Math.Max(1, (int)Math.Ceiling(duration * _fps));
        var trackCount = _project.TrackCount;
        var states = new TrackState[trackCount];
        for (var t = 0; t < trackCount; t++)
        {
            states[t] = new TrackState { Track = t };
        }

        using var encoder = TryCreateEncoder();
        var output = new byte[_outW * _outH * 4];

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var time = frame / (double)_fps;

            // 每轨：确保当前时刻的活跃片段已打开解码器。
            for (var t = 0; t < trackCount; t++)
            {
                var st = states[t];
                var clip = FindActiveClip(clips, t, time);
                if (!ReferenceEquals(clip, st.ActiveClip))
                {
                    Close(st);
                    if (clip != null)
                    {
                        st.Source = Open(clip);
                        if (st.Source != null)
                        {
                            st.Source.HardwareDecoder = _hwDecoder;
                        }

                        st.SourceFps = st.Source?.SourceFps ?? 0;
                    }

                    st.ActiveClip = clip;
                }
            }

            // 逐轨合成（轨道号越大越靠上层）。
            Array.Clear(output, 0, output.Length);
            for (var t = 0; t < trackCount; t++)
            {
                var st = states[t];
                if (st.ActiveClip == null)
                {
                    continue;
                }

                if (st.ActiveClip.Kind == "Video")
                {
                    if (st.Source == null)
                    {
                        continue;
                    }

                    // 帧号消费：每输出帧消费 targetN-LastFrameIndex 帧（前面丢弃、最后一帧保留），
                    // 60fps 源在 24fps 输出下每帧消费 2~3 帧，速度与时间轴一致（旧版每输出帧只拉 1 帧 = 慢放）。
                    var mediaTime = st.ActiveClip.InPoint + Math.Max(0, time - st.ActiveClip.StartTime);
                    var fps = st.SourceFps > 0 ? st.SourceFps : 25.0;
                    var targetN = (long)(mediaTime * fps);
                    var behind = targetN - st.LastFrameIndex;
                    if (!st.Eof && behind > 0)
                    {
                        for (var i = 0; i < behind; i++)
                        {
                            if (!st.Source.TryReadFrame(out var fr) || fr == null)
                            {
                                st.Eof = true; // EOF：后续输出帧冻结最后画面。
                                break;
                            }

                            st.LastFrameIndex++;
                            if (st.LastBuffer == null || st.LastBuffer.Length != fr.Pixels.Length)
                            {
                                st.LastBuffer = new byte[fr.Pixels.Length];
                            }

                            Buffer.BlockCopy(fr.Pixels, 0, st.LastBuffer, 0, fr.Pixels.Length);
                            st.LastWidth = fr.Width;
                            st.LastHeight = fr.Height;
                        }
                    }
                }
                else
                {
                    // 文本/形状覆盖层：生成静态帧（透明背景 + 预乘 alpha）。
                    st.OverlayBuffer ??= OverlayFrameGenerator.Render(st.ActiveClip, _outW, _outH);
                    if (st.OverlayBuffer != null)
                    {
                        st.LastBuffer = st.OverlayBuffer.Pixels;
                        st.LastWidth = _outW;
                        st.LastHeight = _outH;
                    }
                }

                if (st.LastBuffer != null)
                {
                    Composite(st.ActiveClip, st.LastBuffer, st.LastWidth, st.LastHeight, output);
                }
            }

            // 预乘 → 直通 + 不透明背景（黑底），供编码器使用。
            for (var i = 0; i + 3 < output.Length; i += 4)
            {
                var a = output[i + 3];
                if (a == 0)
                {
                    output[i] = 0;
                    output[i + 1] = 0;
                    output[i + 2] = 0;
                }
                else if (a < 255)
                {
                    output[i] = (byte)(output[i] * 255 / a);
                    output[i + 1] = (byte)(output[i + 1] * 255 / a);
                    output[i + 2] = (byte)(output[i + 2] * 255 / a);
                }

                output[i + 3] = 255;
            }

            // 滤镜片段：对整帧应用当前时刻最上层的滤镜（被滤镜覆盖的画面显示该效果）。
            var filter = FindActiveFilter(clips, time);
            if (filter != null && !string.IsNullOrEmpty(filter.Filter))
            {
                FilterUtils.ApplyInPlace(output, _outW, _outH, filter.Filter);
            }

            encoder.EncodeFrame(output);
            _progress?.Invoke((double)(frame + 1) / totalFrames, $"渲染 {frame + 1}/{totalFrames} 帧");
        }

        encoder.Finish();
    }

    /// <summary>
    /// 创建编码器：硬件编码器（qsv→nvenc→amf→mf）优先，打开失败（无对应硬件/驱动）时
    /// 逐个回退，最后落到软编 libx264。渲染慢的主因是 x264 编码，核显机器硬编可提速数倍。
    /// </summary>
    private FFmpegVideoEncoder TryCreateEncoder()
    {
        var candidates = new List<string?>();
        if (string.IsNullOrEmpty(_hwEncoder))
        {
            candidates.Add(null); // 未启用硬件加速：直接软编。
        }
        else if (_hwEncoder == "auto")
        {
            candidates.AddRange(["h264_qsv", "h264_nvenc", "h264_amf", "h264_mf", null]);
        }
        else
        {
            candidates.AddRange([_hwEncoder, null]); // 指定但失败也回退软编。
        }

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var encoder = candidate == null
                    ? new FFmpegVideoEncoder(_outputPath, _outW, _outH, _crf, _fps, _preset)
                    : new FFmpegVideoEncoder(_outputPath, _outW, _outH, _crf, _fps, _preset, candidate);
                if (candidate != null)
                {
                    _progress?.Invoke(0, $"使用硬件编码器 {candidate}（失败自动回退软件）");
                }

                return encoder;
            }
            catch (Exception ex)
            {
                last = ex;
                _progress?.Invoke(0, $"编码器 {candidate ?? "libx264"} 不可用：{ex.Message}");
            }
        }

        throw new InvalidOperationException($"无法创建编码器：{last?.Message}");
    }

    /// <summary>当前时刻最上层的滤镜片段（轨道号最大；同轨取起始最晚）。</summary>
    private static VideoClip? FindActiveFilter(List<VideoClip> clips, double time)
    {
        VideoClip? best = null;
        foreach (var clip in clips)
        {
            if (clip.Kind != "Filter")
            {
                continue;
            }

            if (time >= clip.StartTime && time < clip.StartTime + clip.Duration)
            {
                if (best == null || clip.Track > best.Track ||
                    (clip.Track == best.Track && clip.StartTime >= best.StartTime))
                {
                    best = clip;
                }
            }
        }

        return best;
    }

    /// <summary>当前时刻指定轨道上的活跃片段（同轨重叠时取起始时间最晚的）。</summary>
    private static VideoClip? FindActiveClip(List<VideoClip> clips, int track, double time)
    {
        VideoClip? best = null;
        foreach (var clip in clips)
        {
            if (clip.Track != track)
            {
                continue;
            }

            if (time >= clip.StartTime && time < clip.StartTime + clip.Duration)
            {
                if (best == null || clip.StartTime >= best.StartTime)
                {
                    best = clip;
                }
            }
        }

        return best;
    }

    private VideoFrameSource? Open(VideoClip clip)
    {
        if (clip.Kind != "Video")
        {
            return null;
        }

        try
        {
            var source = new VideoFrameSource();
            if (!source.Open(clip.SourcePath, _maxDimension))
            {
                source.Dispose();
                return null;
            }

            if (clip.InPoint > 0)
            {
                source.SeekTo(clip.InPoint);
            }

            return source;
        }
        catch
        {
            return null;
        }
    }

    private static void Close(TrackState state)
    {
        state.Source?.Dispose();
        state.Source = null;
        state.OverlayBuffer = null;
        state.LastFrameIndex = -1;
        state.Eof = false;
    }

    /// <summary>把一轨画面按变换逐像素合成到输出画布（BGRA，alpha 混合）。</summary>
    private void Composite(VideoClip clip, byte[] src, int bw, int bh, byte[] output)
    {
        var W = _outW;
        var H = _outH;
        if (bw <= 0 || bh <= 0 || W <= 0 || H <= 0)
        {
            return;
        }

        var opacity = Math.Clamp(clip.Opacity, 0, 1);
        if (opacity <= 0)
        {
            return;
        }

        // 原比例基础矩形（Uniform 居中，与编辑器舞台一致）。
        var aspect = bw / (double)bh;
        var stageAspect = W / (double)H;
        double baseW, baseH;
        if (aspect >= stageAspect)
        {
            baseW = W;
            baseH = W / aspect;
        }
        else
        {
            baseH = H;
            baseW = H * aspect;
        }

        var baseX = (W - baseW) / 2.0;
        var baseY = (H - baseH) / 2.0;
        var scaleX = Math.Max(0.01, clip.Scale * clip.ScaleX);
        var scaleY = Math.Max(0.01, clip.Scale * clip.ScaleY);
        var offX = clip.OffsetX * W;
        var offY = clip.OffsetY * H;
        var rot = clip.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(rot);
        var sin = Math.Sin(rot);
        var cx = W / 2.0;
        var cy = H / 2.0;
        var cropL = clip.CropLeft * W;
        var cropT = clip.CropTop * H;
        var cropR = clip.CropRight * W;
        var cropB = clip.CropBottom * H;
        var flipH = clip.FlipH;
        var flipV = clip.FlipV;
        var gray = clip.Grayscale > 0.001;
        var grayA = Math.Clamp(clip.Grayscale, 0, 1);

        for (var y = 0; y < H; y++)
        {
            if (y < cropT || y > cropB)
            {
                continue;
            }

            var row = y * W;
            for (var x = 0; x < W; x++)
            {
                if (x < cropL || x > cropR)
                {
                    continue;
                }

                // 逆变换：输出像素 → 源像素（含翻转：翻转时对源坐标取镜像）。
                var dx = x - cx - offX;
                var dy = y - cy - offY;
                var rx = dx * cos + dy * sin;
                var ry = -dx * sin + dy * cos;
                var sx = rx / scaleX + cx;
                var sy = ry / scaleY + cy;
                var u = (sx - baseX) / baseW * bw;
                var v = (sy - baseY) / baseH * bh;
                if (flipH)
                {
                    u = bw - u;
                }

                if (flipV)
                {
                    v = bh - v;
                }

                if (u < 0 || v < 0 || u >= bw || v >= bh)
                {
                    continue;
                }

                var px = (int)u;
                var py = (int)v;
                var si = (py * bw + px) * 4;
                var sr = src[si];
                var sg = src[si + 1];
                var sb = src[si + 2];
                var oi = (row + x) * 4;

                // 预乘 over 合成：a = 源像素 alpha × 片段不透明度（视频帧 alpha=255，退化为原逻辑）。
                var srcA = src[si + 3] * opacity / 255.0;
                if (srcA <= 0.001)
                {
                    continue;
                }

                if (gray)
                {
                    var g = (byte)((sr * 299 + sg * 587 + sb * 114) / 1000);
                    sr = (byte)(g * grayA + sr * (1 - grayA));
                    sg = (byte)(g * grayA + sg * (1 - grayA));
                    sb = (byte)(g * grayA + sb * (1 - grayA));
                }

                var inv = 1 - srcA;
                output[oi] = (byte)(sb + output[oi] * inv);
                output[oi + 1] = (byte)(sg + output[oi + 1] * inv);
                output[oi + 2] = (byte)(sr + output[oi + 2] * inv);
                output[oi + 3] = (byte)((srcA + output[oi + 3] / 255.0 * inv) * 255);
            }
        }
    }
}
