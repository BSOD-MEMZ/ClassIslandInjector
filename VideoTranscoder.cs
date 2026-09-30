using System.Diagnostics;

namespace ClassIslandInjector;

/// <summary>
/// 素材转码压缩：把导入视频编辑器的源视频重新编码为降采样 H.264 mp4（丢音频），
/// 减小体积、加快剪辑预览与渲染。流程 = FFmpegVideoDecoder 逐帧解码 →
/// FFmpegVideoEncoder 按源帧率编码（输出帧率 = 源帧率，保证时长一致）。
/// 需要完整 FFmpeg 包（含编码器，<see cref="FFmpegRuntime.EncoderAvailable"/>），
/// 调用方应先检查并在缺失时引导升级。全部调用有 try/catch 兜底，失败返回 false。
/// </summary>
internal static class VideoTranscoder
{
    /// <summary>
    /// 转码产物的帧率上限。预览/底图的解码成本与源帧率成正比，而展示只要 24fps，
    /// 手机视频（54~60fps）照抄源帧率会白解近两倍的帧（3 轨同屏 15fps → 24fps 的差别）。
    /// 30fps 也保证产物是固定帧率，播放器的帧号换算严格成立。
    /// </summary>
    private const int ProxyMaxFps = 30;

    /// <summary>
    /// 压缩转码。<paramref name="maxDimension"/> 限制输出最长边（如 720）；
    /// <paramref name="crf"/> 越大文件越小（建议 26~30）；进度回调传 0..1。
    /// 取消（cancellationToken）时删除半成品并返回 false。
    /// </summary>
    public static bool Compress(string sourcePath, string outputPath, int maxDimension, int crf,
        Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // 解码/编码都会触发 FFmpeg 惰性加载，必须先确认库可用（缺失直接失败，调用方回退原文件）。
            if (!FFmpegRuntime.IsAvailable || !FFmpegRuntime.EnsureLoaded())
            {
                return false;
            }

            using var decoder = new FFmpegVideoDecoder();
            if (!decoder.Open(sourcePath, maxDimension))
            {
                return false;
            }

            // 输出帧率：**上限 30fps**（不再照抄源帧率）。
            // 播放/预览的解码成本几乎只由「源帧率 × 分辨率」决定：手机视频常见 54~60fps，
            // 而显示只要 24fps —— 照抄意味着每轨每秒白解 1.8~2.5 倍的帧
            // （实测同一台机器：54fps 源 7.4ms/帧、30fps CFR 源 5.2ms/帧，3 轨同屏 15fps vs 24fps）。
            // 压到 30fps 还有第二个好处：产物是**固定帧率**，播放器的帧号换算严格成立
            // （VFR 素材会让「落后多少帧」的判断失真，进而频繁 seek）。
            // 30fps > 渲染输出常用的 24fps，画质不受影响。
            var sourceFps = decoder.SourceFps > 0.5 ? decoder.SourceFps : ProxyMaxFps;
            var fps = Math.Clamp((int)Math.Round(Math.Min(sourceFps, ProxyMaxFps)), 5, ProxyMaxFps);
            using var encoder = new FFmpegVideoEncoder(outputPath, decoder.OutputWidth, decoder.OutputHeight, crf, fps);
            // 估算总帧数（时长 × 输出帧率）供进度显示；未知时长时按 30s 兜底。
            var totalFrames = Math.Max(1, (int)((decoder.Duration > 0.1 ? decoder.Duration : 30) * fps));
            var sw = Stopwatch.StartNew();
            var frames = 0;
            // 抽帧：源帧率高于输出帧率时按目标时间挑帧。顺序解码无法「不读」，但不编码即可 ——
            // 转码是一次性成本，换来的是每次播放都少解一半的帧。
            var ratio = sourceFps / fps > 1 ? sourceFps / fps : 1.0;
            long srcIndex = -1;
            long nextSrc = 0;
            long outIndex = 0;
            while (decoder.ReadFrame(out var pixels))
            {
                cancellationToken.ThrowIfCancellationRequested();
                srcIndex++;
                if (srcIndex < nextSrc)
                {
                    continue;
                }

                nextSrc = (long)Math.Round(++outIndex * ratio);
                encoder.EncodeFrame(pixels);
                frames++;
                if (sw.ElapsedMilliseconds >= 150)
                {
                    sw.Restart();
                    progress?.Invoke(Math.Clamp((double)frames / totalFrames, 0, 1));
                }
            }

            progress?.Invoke(1);
            encoder.Finish();
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // 半成品删除失败忽略
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>压缩输出路径（配置目录 video-cache 下，文件名含源大小防同目录同名冲突）。已存在时直接复用。</summary>
    public static string BuildOutputPath(string sourcePath, string cacheDirectory, int maxDimension, long sourceLength)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var safeName = string.Join('_', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safeName.Length > 48)
        {
            safeName = safeName[..48];
        }

        Directory.CreateDirectory(cacheDirectory);
        var output = Path.Combine(cacheDirectory, $"{safeName}-{sourceLength}-{maxDimension}p.mp4");
        if (File.Exists(output))
        {
            try
            {
                // 残留半成品（上次取消/崩溃）：长度异常小则删除重压。
                if (new FileInfo(output).Length > 4096)
                {
                    return output;
                }

                File.Delete(output);
            }
            catch
            {
                // 复用失败则走重新压缩。
            }
        }

        return output;
    }

    /// <summary>是否为视频编辑器可导入的图片素材（图片走 Kind=Image 覆盖层，无需压缩）。</summary>
    public static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    /// <summary>
    /// GDI+（System.Drawing）能否解码该图片。<see cref="OverlayFrameGenerator"/> 与
    /// <see cref="VideoProjectRenderer"/> 都走 GDI+ 画图，而 **GDI+ 没有 WebP 解码器**
    /// —— Windows 那个「WebP 图像扩展」只注册 WIC 编解码器，而 GDI+ 不走 WIC，
    /// 所以 <c>new Bitmap(stream)</c> 对 WebP 直接抛 ArgumentException（实测「Parameter is not valid.」）。
    /// 后果：该图片片段在预览里从头到尾不动（播放器每拍重试渲染都拿不到帧）、
    /// 渲染成片里图片**静默消失** —— 而素材缩略图 / 主界面底图走 Avalonia(Skia)，WebP 正常，
    /// 于是表现为「有的地方能看有的地方坏」。探针成本极低（失败在解析头部就返回）。
    /// </summary>
    public static bool IsGdiDecodable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var probe = new System.Drawing.Bitmap(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 图片格式归一化：把 GDI+ 解不了的图片（典型 WebP）用 Avalonia/Skia 解码后另存为 PNG，
    /// 返回可用的路径。已经在解码器支持范围内、文件缺失或转换失败时**原样返回**（调用方按无法解码处理）。
    /// <para>
    /// 转换产物落在 <paramref name="cacheDirectory"/>，文件名带源大小（源文件换了就是新产物，
    /// 与 <see cref="BuildOutputPath"/> 同一套防同名冲突口径），重复导入直接复用。
    /// Skia 侧用的是与素材缩略图完全相同的 <c>new Avalonia.Media.Imaging.Bitmap(stream)</c>（原生支持 WebP），
    /// 可在后台线程调用。
    /// </para>
    /// </summary>
    public static string NormalizeImage(string path, string cacheDirectory)
    {
        try
        {
            if (!File.Exists(path) || IsGdiDecodable(path))
            {
                return path;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var safeName = string.Join('_', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safeName.Length > 48)
            {
                safeName = safeName[..48];
            }

            Directory.CreateDirectory(cacheDirectory);
            var output = Path.Combine(cacheDirectory, $"{safeName}-{new FileInfo(path).Length}.png");
            if (File.Exists(output))
            {
                return output;
            }

            using (var stream = File.OpenRead(path))
            using (var bitmap = new Avalonia.Media.Imaging.Bitmap(stream))
            using (var fs = File.Create(output))
            {
                bitmap.Save(fs);
            }

            return output;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>是否为可导入的视频素材扩展名。</summary>
    public static bool IsVideoFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".mp4" or ".wmv" or ".avi" or ".mkv" or ".mov" or ".webm" or ".m4v";

    /// <summary>是否为可导入的音频素材扩展名（放到音频轨的片段）。</summary>
    public static bool IsAudioFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".wma" or ".opus" or ".aiff" or ".ape";
}
