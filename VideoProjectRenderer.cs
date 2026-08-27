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
    private readonly int _fps;
    private readonly int _maxDimension;
    private readonly Action<double, string>? _progress;

    private sealed class TrackState
    {
        public int Track;
        public VideoFrameSource? Source;
        public VideoClip? ActiveClip;
        /// <summary>最近一帧的拷贝（EOF 后冻结最后画面，避免轨道中途变透明）。</summary>
        public byte[]? LastBuffer;
        public int LastWidth;
        public int LastHeight;
    }

    public VideoProjectRenderer(VideoProject project, string outputPath, int outW, int outH, int crf, int fps,
        Action<double, string>? progress = null)
    {
        _project = project;
        _outputPath = outputPath;
        _outW = outW;
        _outH = outH;
        _crf = crf;
        _fps = Math.Max(1, fps);
        _maxDimension = Math.Max(outW, outH);
        _progress = progress;
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

        using var encoder = new FFmpegVideoEncoder(_outputPath, _outW, _outH, _crf, _fps);
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
                    }

                    st.ActiveClip = clip;
                }
            }

            // 逐轨合成（轨道号越大越靠上层）。
            Array.Clear(output, 0, output.Length);
            for (var t = 0; t < trackCount; t++)
            {
                var st = states[t];
                if (st.Source == null || st.ActiveClip == null)
                {
                    continue;
                }

                if (st.Source.TryReadFrame(out var fr) && fr != null)
                {
                    if (st.LastBuffer == null || st.LastBuffer.Length != fr.Pixels.Length)
                    {
                        st.LastBuffer = new byte[fr.Pixels.Length];
                    }

                    Buffer.BlockCopy(fr.Pixels, 0, st.LastBuffer, 0, fr.Pixels.Length);
                    st.LastWidth = fr.Width;
                    st.LastHeight = fr.Height;
                }

                if (st.LastBuffer != null)
                {
                    Composite(st.ActiveClip, st.LastBuffer, st.LastWidth, st.LastHeight, output);
                }
            }

            encoder.EncodeFrame(output);
            _progress?.Invoke((double)(frame + 1) / totalFrames, $"渲染 {frame + 1}/{totalFrames} 帧");
        }

        encoder.Finish();
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
        var scale = Math.Max(0.01, clip.Scale);
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
        var useAlpha = opacity < 0.999;

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

                // 逆变换：输出像素 → 源像素。
                var dx = x - cx - offX;
                var dy = y - cy - offY;
                var rx = dx * cos + dy * sin;
                var ry = -dx * sin + dy * cos;
                var sx = rx / scale + cx;
                var sy = ry / scale + cy;
                var u = (sx - baseX) / baseW * bw;
                var v = (sy - baseY) / baseH * bh;
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
                if (!useAlpha)
                {
                    output[oi] = sb;
                    output[oi + 1] = sg;
                    output[oi + 2] = sr;
                    output[oi + 3] = 255;
                }
                else
                {
                    var a = opacity;
                    output[oi] = (byte)(output[oi] + (sb - output[oi]) * a);
                    output[oi + 1] = (byte)(output[oi + 1] + (sg - output[oi + 1]) * a);
                    output[oi + 2] = (byte)(output[oi + 2] + (sr - output[oi + 2]) * a);
                    output[oi + 3] = 255;
                }
            }
        }
    }
}
