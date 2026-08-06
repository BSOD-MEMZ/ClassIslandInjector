using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using GifImage = System.Drawing.Image;

namespace ClassIslandInjector;

/// <summary>
/// 用 System.Drawing 解码 GIF 的每一帧并换算成 Avalonia 位图，供 <see cref="ExplosionOverlay"/>
/// 手动逐帧播放。Avalonia 11.3.17 无内置 GIF 动画支持，故由插件自行驱动帧。
/// </summary>
internal static class GifFrameLoader
{
    private static readonly object Gate = new();
    private static List<AvaloniaBitmap>? _frames;
    private static List<int>? _delaysMs;

    /// <summary>加载插件 Assets 目录下的 GIF 帧（静态缓存，首次成功后不再重读）。</summary>
    public static (AvaloniaBitmap[] Frames, int[] DelaysMs) Load(string fileName)
    {
        lock (Gate)
        {
            if (_frames != null)
            {
                return (_frames.ToArray(), _delaysMs!.ToArray());
            }

            _frames = [];
            _delaysMs = [];
            try
            {
                var pluginDirectory = Path.GetDirectoryName(typeof(GifFrameLoader).Assembly.Location);
                var path = pluginDirectory == null ? null : Path.Combine(pluginDirectory, "Assets", fileName);
                if (path is not { } gifPath || !File.Exists(gifPath))
                {
                    return (_frames.ToArray(), _delaysMs.ToArray());
                }

                using var stream = File.OpenRead(gifPath);
                using var image = GifImage.FromStream(stream);
                var dimension = new FrameDimension(image.FrameDimensionsList[0]);
                var count = image.GetFrameCount(dimension);
                var delays = ReadFrameDelays(image);
                for (var i = 0; i < count; i++)
                {
                    image.SelectActiveFrame(dimension, i);
                    using var ms = new MemoryStream();
                    image.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    _frames.Add(new AvaloniaBitmap(ms));
                    _delaysMs.Add(delays is { Length: > 0 } && i < delays.Length ? delays[i] : 100);
                }
            }
            catch
            {
                // 解码失败时按空帧处理，爆炸效果静默降级为不可见。
            }

            return (_frames.ToArray(), _delaysMs!.ToArray());
        }
    }

    /// <summary>读取 GIF 帧延迟（PropertyTagFrameDelay，单位 1/100 秒），换算为毫秒。</summary>
    private static int[]? ReadFrameDelays(GifImage image)
    {
        try
        {
            foreach (var property in image.PropertyItems)
            {
                if (property.Id != 0x5100 || property.Value == null)
                {
                    continue;
                }

                var raw = property.Value;
                var delays = new int[raw.Length / 4];
                for (var i = 0; i < delays.Length; i++)
                {
                    delays[i] = BitConverter.ToInt32(raw, i * 4) * 10;
                }

                return delays;
            }
        }
        catch
        {
            // 读取失败时使用默认延迟。
        }

        return null;
    }
}

/// <summary>
/// 强调时在 Ripple 中心播放一次爆炸 GIF 的覆盖层。由注入器 16ms 时钟推进，
/// 播放完最后一帧（含末尾淡出）后由注入器移除。使用 GIF 自带配色，不依赖线宽/颜色设置。
/// </summary>
internal sealed class ExplosionOverlay : Control, IRippleEffect
{
    private readonly AvaloniaBitmap[] _frames;
    private readonly int[] _delaysMs;
    private readonly int _totalMs;
    private readonly Point _center;
    private readonly double _size;
    private readonly double _opacityScale;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public ExplosionOverlay(Point center, double size, double opacityScale = 1)
    {
        (_frames, _delaysMs) = GifFrameLoader.Load("explode.gif");
        _totalMs = _delaysMs.Sum();
        _center = center;
        _size = size;
        _opacityScale = Math.Clamp(opacityScale, 0, 1);
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public bool IsCompleted => (DateTime.UtcNow - _startedAt).TotalMilliseconds >= _totalMs;

    public void Advance() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_frames.Length == 0 || _size <= 0)
        {
            return;
        }

        var elapsedMs = (DateTime.UtcNow - _startedAt).TotalMilliseconds;
        var (frame, _) = FindFrame(elapsedMs);
        if (frame < 0)
        {
            return;
        }

        // 末尾 20% 淡出，让爆炸结束更自然。
        var progress = Math.Clamp(elapsedMs / _totalMs, 0, 1);
        var opacity = _opacityScale * Math.Clamp((1 - progress) / 0.2, 0, 1);
        if (opacity <= 0)
        {
            return;
        }

        var half = _size / 2;
        var dest = new Rect(_center.X - half, _center.Y - half, _size, _size);
        var source = new Rect(_frames[frame].Size);
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(_frames[frame], source, dest);
        }
    }

    private (int Index, double StartMs) FindFrame(double elapsedMs)
    {
        var acc = 0.0;
        for (var i = 0; i < _delaysMs.Length; i++)
        {
            if (elapsedMs < acc + _delaysMs[i])
            {
                return (i, acc);
            }

            acc += _delaysMs[i];
        }

        return (-1, acc);
    }
}
