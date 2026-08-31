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

            // 输出帧率 = 源帧率（取整），保证转码后时长与素材一致（±1 帧内）。
            var fps = Math.Clamp((int)Math.Round(decoder.SourceFps), 1, 60);
            using var encoder = new FFmpegVideoEncoder(outputPath, decoder.OutputWidth, decoder.OutputHeight, crf, fps);
            // 估算总帧数（时长 × 帧率）供进度显示；未知时长时按 30s 兜底。
            var totalFrames = Math.Max(1, (int)((decoder.Duration > 0.1 ? decoder.Duration : 30) * fps));
            var sw = Stopwatch.StartNew();
            var frames = 0;
            while (decoder.ReadFrame(out var pixels))
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>是否为可导入的视频素材扩展名。</summary>
    public static bool IsVideoFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".mp4" or ".wmv" or ".avi" or ".mkv" or ".mov" or ".webm" or ".m4v";
}
