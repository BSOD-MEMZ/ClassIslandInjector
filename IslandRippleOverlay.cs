using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClassIslandInjector;

/// <summary>
/// A non-interactive ripple drawn above the main window. It deliberately lives in
/// the plugin so ripple styles are independent from ClassIsland's built-in effect.
/// </summary>
internal sealed class IslandRippleOverlay : Control
{
    private static readonly Lazy<Bitmap?> FireworkTexture = new(() => LoadMaimaiTexture("Firework.png"));
    private static readonly Lazy<Bitmap?> ColorBallTexture = new(() => LoadMaimaiTexture("ColorBall.png"));
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly TimeSpan _duration;
    private readonly RippleType _type;
    private readonly Color _color;
    private readonly double _thickness;
    private readonly Point _center;

    public IslandRippleOverlay(Point center, RippleType type, Color color, TimeSpan duration, double thickness)
    {
        _center = center;
        _type = type;
        _color = color;
        _duration = duration;
        _thickness = thickness;
        IsHitTestVisible = false;
    }

    public bool IsCompleted => DateTime.UtcNow - _startedAt >= _duration;

    public void Advance()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        Opacity = 1 - progress;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var progress = Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        var radius = Math.Max(Bounds.Width, Bounds.Height) * (0.12 + progress * 0.65);
        var brush = new SolidColorBrush(_color);
        var rect = new Rect(_center.X - radius, _center.Y - radius, radius * 2, radius * 2);

        switch (_type)
        {
            case RippleType.Ring:
                context.DrawEllipse(null, new Pen(brush, _thickness), rect);
                break;
            case RippleType.DoubleRing:
                context.DrawEllipse(null, new Pen(brush, _thickness), rect);
                var delayed = radius * Math.Max(0, progress - 0.22) / Math.Max(progress, 0.001);
                context.DrawEllipse(null, new Pen(brush, _thickness * 0.65),
                    new Rect(_center.X - delayed, _center.Y - delayed, delayed * 2, delayed * 2));
                break;
            case RippleType.Glow:
                context.DrawEllipse(brush, null, rect);
                break;
            case RippleType.Square:
                context.DrawRectangle(null, new Pen(brush, _thickness), rect);
                break;
            case RippleType.Hanabi:
                DrawHanabi(context, progress, radius);
                break;
        }
    }

    private void DrawHanabi(DrawingContext context, double progress, double radius)
    {
        // This follows MajdataView's colourful Firework sprite and yellow
        // ColorBall sprite. Hanabi deliberately ignores the ordinary ripple tint
        // so it preserves the maimai appearance.
        var opening = Math.Clamp(progress / 0.18, 0, 1);
        var burst = 1 - Math.Pow(1 - opening, 3);
        var fade = Math.Clamp(1 - Math.Pow(progress, 1.65), 0, 1) * 0.589;
        // The ColorBall sprite is a prominent part of the original touch burst,
        // not a tiny point. Let it occupy the same central proportion as the
        // Firework texture, then taper it as the rays take over.
        var coreRadius = Math.Max(16, radius * (0.26 - Math.Min(progress, 0.78) * 0.19));
        var yellowCore = Color.FromArgb((byte)(255 * fade), 255, 213, 38);
        var whiteCore = Color.FromArgb((byte)(255 * fade), 255, 255, 218);
        var fireworkWhite = Color.FromArgb((byte)(255 * fade), 255, 255, 255);
        var softWhite = Color.FromArgb((byte)(170 * fade), 255, 255, 255);

        // Use the original MajdataView sprites when they are present in the plugin
        // package. The procedural branch below is only a resilient fallback.
        if (DrawMaimaiTextures(context, radius, coreRadius, fade))
        {
            return;
        }

        context.DrawEllipse(new SolidColorBrush(yellowCore), null, _center, coreRadius, coreRadius);
        context.DrawEllipse(new SolidColorBrush(whiteCore), null, _center, coreRadius * 0.44, coreRadius * 0.44);

        // The original Firework texture is an irregular 16-spoke white bloom;
        // alternating radii recreate its petal-like silhouette rather than a ring.
        const int rays = 24;
        for (var i = 0; i < rays; i++)
        {
            var angle = i * Math.Tau / rays + 0.12 + (i % 2 == 0 ? 0.025 : -0.025);
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            var variation = 0.62 + ((i * 7) % 9) * 0.047;
            var outer = radius * burst * variation;
            var inner = outer * (0.08 + progress * 0.15);
            var start = _center + direction * inner;
            var end = _center + direction * outer;
            var rayBrush = new SolidColorBrush(i % 4 == 0 ? softWhite : fireworkWhite);
            context.DrawLine(new Pen(rayBrush, Math.Max(0.9, _thickness * (1.05 - progress * 0.42))), start, end);

            if (i % 2 == 0)
            {
                var sparkRadius = Math.Max(1.1, _thickness * (1.15 - progress * 0.55));
                context.DrawEllipse(rayBrush, null, end, sparkRadius, sparkRadius);
            }
        }
    }

    private bool DrawMaimaiTextures(DrawingContext context, double radius, double coreRadius, double opacity)
    {
        var firework = FireworkTexture.Value;
        var colorBall = ColorBallTexture.Value;
        if (firework == null || colorBall == null)
        {
            return false;
        }

        var fireworkRect = new Rect(_center.X - radius, _center.Y - radius, radius * 2, radius * 2);
        var coreRect = new Rect(_center.X - coreRadius, _center.Y - coreRadius, coreRadius * 2, coreRadius * 2);
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(firework, fireworkRect);
            context.DrawImage(colorBall, coreRect);
        }
        return true;
    }

    private static Bitmap? LoadMaimaiTexture(string fileName)
    {
        try
        {
            var assemblyPath = typeof(IslandRippleOverlay).Assembly.Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyPath);
            var texturePath = pluginDirectory == null ? null : Path.Combine(pluginDirectory, "Assets", fileName);
            return texturePath is { } path && File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
