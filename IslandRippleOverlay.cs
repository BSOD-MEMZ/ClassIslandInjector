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
        // Hanabi owns separate opacity curves for its core and burst. Applying the
        // ordinary ripple fade here would multiply those curves and hide it early.
        Opacity = _type == RippleType.Hanabi ? 1 : 1 - progress;
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
                DrawHanabi(context, progress);
                break;
        }
    }

    private void DrawHanabi(DrawingContext context, double progress)
    {
        // The colour ball grows for the whole animation while the white bloom
        // appears shortly afterwards, expands, and rotates around it.
        var extent = Math.Max(Bounds.Width, Bounds.Height);
        var burstProgress = Math.Clamp((progress - 0.14) / 0.86, 0, 1);
        var burstExpansion = EaseOutCubic(burstProgress);
        var radius = extent * (0.045 + 0.44 * burstExpansion);
        var coreExpansion = EaseOutCubic(progress);
        var coreRadius = Math.Max(12, extent * (0.018 + 0.105 * coreExpansion));
        var rotation = 105 * burstProgress;
        var fireworkOpacity = SmoothStep(0.14, 0.22, progress) * FadeOut(0.58, progress);
        var coreOpacity = FadeOut(0.62, progress);
        var yellowCore = Color.FromArgb((byte)(255 * coreOpacity), 255, 213, 38);
        var whiteCore = Color.FromArgb((byte)(255 * coreOpacity), 255, 255, 218);
        var fireworkWhite = Color.FromArgb((byte)(255 * fireworkOpacity), 255, 255, 255);
        var softWhite = Color.FromArgb((byte)(170 * fireworkOpacity), 255, 255, 255);

        if (DrawMaimaiTextures(context, radius, coreRadius, rotation, fireworkOpacity, coreOpacity))
        {
            return;
        }

        context.DrawEllipse(new SolidColorBrush(yellowCore), null, _center, coreRadius, coreRadius);
        context.DrawEllipse(new SolidColorBrush(whiteCore), null, _center, coreRadius * 0.44, coreRadius * 0.44);

        using (context.PushTransform(CreateRotationMatrix(rotation)))
        {
            // The original Firework texture is an irregular white bloom;
            // alternating radii recreate its silhouette when the asset is absent.
            const int rays = 24;
            for (var i = 0; i < rays; i++)
            {
                var angle = i * Math.Tau / rays + 0.12 + (i % 2 == 0 ? 0.025 : -0.025);
                var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                var variation = 0.62 + ((i * 7) % 9) * 0.047;
                var outer = radius * variation;
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
    }

    private bool DrawMaimaiTextures(DrawingContext context, double radius, double coreRadius,
        double rotation, double fireworkOpacity, double coreOpacity)
    {
        var firework = FireworkTexture.Value;
        var colorBall = ColorBallTexture.Value;
        if (firework == null || colorBall == null)
        {
            return false;
        }

        var fireworkRect = new Rect(_center.X - radius, _center.Y - radius, radius * 2, radius * 2);
        var coreRect = new Rect(_center.X - coreRadius, _center.Y - coreRadius, coreRadius * 2, coreRadius * 2);
        using (context.PushOpacity(fireworkOpacity))
        using (context.PushTransform(CreateRotationMatrix(rotation)))
        {
            context.DrawImage(firework, fireworkRect);
        }
        using (context.PushOpacity(coreOpacity))
        {
            context.DrawImage(colorBall, coreRect);
        }
        return true;
    }

    private static double EaseOutCubic(double value) => 1 - Math.Pow(1 - value, 3);

    private static double FadeOut(double start, double progress) =>
        1 - Math.Clamp((progress - start) / (1 - start), 0, 1);

    private Matrix CreateRotationMatrix(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return Matrix.CreateTranslation(-_center.X, -_center.Y) *
               Matrix.CreateRotation(radians) *
               Matrix.CreateTranslation(_center.X, _center.Y);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var normalized = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return normalized * normalized * (3 - 2 * normalized);
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
