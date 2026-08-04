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
    private const double HanabiClipDuration = 1.3333334;
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
        // Hanabi's curves are authored for a 1.333 s clip. Finishing the host
        // control sooner makes its centre bloom look as though it was cut off.
        _duration = type == RippleType.Hanabi && duration < TimeSpan.FromSeconds(HanabiClipDuration)
            ? TimeSpan.FromSeconds(HanabiClipDuration)
            : duration;
        _thickness = thickness;
        IsHitTestVisible = false;
        ClipToBounds = false;
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
        // Ported from MajdataView's Firework.prefab and fire.anim. The original
        // uses three SpriteRenderers driven by 60 FPS Unity Hermite curves.
        var extent = Math.Max(Bounds.Width, Bounds.Height);
        var time = progress * HanabiClipDuration;
        var fireworkScale = SampleFireworkScale(time);
        var fireworkRadius = extent * 0.098 * fireworkScale;
        var rotation = SampleFireworkRotation(time);
        var fireworkOpacity = SampleFireworkOpacity(time);

        // Preserve the original expansion cadence while increasing the final
        // size of both centre balls. Opacity is controlled separately below.
        var smallBallScale = SampleAccelerating(time, 0, 0.2, 0.1, 0.82);
        var smallBallRadius = extent * 0.0465 * smallBallScale;
        var smallBallOpacity = SampleSmallBallOpacity(time);

        var bigBallScale = SampleAccelerating(time, 0, 1, 0.3, 1.65);
        var bigBallRadius = extent * 0.0465 * bigBallScale;
        var bigBallOpacity = SampleBigBallOpacity(time);
        var bigBallTint = SampleBigBallTint(time);

        if (DrawMaimaiTextures(context, fireworkRadius, rotation, fireworkOpacity,
            bigBallRadius, bigBallOpacity, bigBallTint, smallBallRadius, smallBallOpacity))
        {
            return;
        }

        context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * bigBallOpacity), 255, 191, 191)),
            null, _center, bigBallRadius, bigBallRadius);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * smallBallOpacity), 255, 213, 38)),
            null, _center, smallBallRadius, smallBallRadius);

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
                var outer = fireworkRadius * variation;
                var inner = outer * (0.08 + progress * 0.15);
                var start = _center + direction * inner;
                var end = _center + direction * outer;
                var rayAlpha = (byte)((i % 4 == 0 ? 170 : 255) * fireworkOpacity);
                var rayBrush = new SolidColorBrush(Color.FromArgb(rayAlpha, 255, 255, 255));
                context.DrawLine(new Pen(rayBrush, Math.Max(0.9, _thickness * (1.05 - progress * 0.42))), start, end);

                if (i % 2 == 0)
                {
                    var sparkRadius = Math.Max(1.1, _thickness * (1.15 - progress * 0.55));
                    context.DrawEllipse(rayBrush, null, end, sparkRadius, sparkRadius);
                }
            }
        }
    }

    private bool DrawMaimaiTextures(DrawingContext context, double fireworkRadius, double rotation,
        double fireworkOpacity, double bigBallRadius, double bigBallOpacity, double bigBallTint,
        double smallBallRadius, double smallBallOpacity)
    {
        var firework = FireworkTexture.Value;
        var colorBall = ColorBallTexture.Value;
        if (firework == null || colorBall == null)
        {
            return false;
        }

        var fireworkRect = CenteredRect(fireworkRadius);
        var bigBallRect = CenteredRect(bigBallRadius);
        var smallBallRect = CenteredRect(smallBallRadius);
        using (context.PushOpacity(fireworkOpacity))
        using (context.PushTransform(CreateRotationMatrix(rotation)))
        {
            context.DrawImage(firework, fireworkRect);
        }
        using (context.PushOpacity(bigBallOpacity))
        {
            context.DrawImage(colorBall, bigBallRect);
            if (bigBallTint > 0)
            {
                var mask = new ImageBrush(colorBall) { Stretch = Stretch.Fill };
                using (context.PushOpacityMask(mask, bigBallRect))
                {
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(
                        (byte)(64 * bigBallTint), 255, 0, 0)), null, bigBallRect);
                }
            }
        }
        using (context.PushOpacity(smallBallOpacity))
        {
            context.DrawImage(colorBall, smallBallRect);
        }
        return true;
    }

    private Rect CenteredRect(double radius) =>
        new(_center.X - radius, _center.Y - radius, radius * 2, radius * 2);

    private static double SampleFireworkScale(double time)
    {
        if (time <= 0.1)
        {
            return 0;
        }
        if (time <= 0.13333334)
        {
            return Hermite(time, 0.1, 0, 0, 0.13333334, 0.6, 9.375001);
        }
        if (time <= 0.23333333)
        {
            return Hermite(time, 0.13333334, 0.6, 9.375001, 0.23333333, 1.25, 2.1666665);
        }
        return Hermite(time, 0.23333333, 1.25, 2.1666665, HanabiClipDuration, 5, 0);
    }

    private static double SampleFireworkRotation(double time)
    {
        if (time <= 0.13333334)
        {
            return 0;
        }
        if (time <= 1.2166667)
        {
            return Hermite(time, 0.13333334, 0, -66.46153, 1.2166667, -72, -66.46153);
        }
        return Hermite(time, 1.2166667, -72, -58.775543,
            HanabiClipDuration, -78.85715, -102.857155);
    }

    private static double SampleFireworkOpacity(double time) =>
        time <= 0.5 ? 0.589 : Hermite(time, 0.5, 0.589, 0, HanabiClipDuration, 0, 0);

    private static double SampleSmallBallOpacity(double time)
    {
        // Keep the two centre light balls visible long enough to read as an
        // actual bloom at 30 FPS. The previous imported curve dropped the
        // small ball to 10% by frame 6 and made the whole core disappear
        // around frame 8.
        if (time <= 0.18)
        {
            return 1;
        }
        if (time <= 0.42)
        {
            return Hermite(time, 0.18, 1, 0, 0.42, 0.16, -1.2);
        }
        if (time <= 0.62)
        {
            return Hermite(time, 0.42, 0.16, -1.2, 0.62, 0, 0);
        }
        return 0;
    }

    private static double SampleBigBallOpacity(double time)
    {
        if (time <= 0.16)
        {
            return 1;
        }
        if (time <= 0.46)
        {
            return Hermite(time, 0.16, 1, 0, 0.46, 0.32, -1.1);
        }
        if (time <= 0.95)
        {
            return Hermite(time, 0.46, 0.32, -1.1, 0.95, 0, 0);
        }
        return 0;
    }

    private static double SampleBigBallTint(double time)
    {
        if (time <= 0.06666667)
        {
            return SampleSmooth(time, 0, 0, 0.06666667, 1);
        }
        if (time <= 0.3)
        {
            return SampleSmooth(time, 0.06666667, 1, 0.3, 0);
        }
        return 0;
    }

    private static double SampleSmooth(double time, double startTime, double startValue,
        double endTime, double endValue)
    {
        if (time <= startTime)
        {
            return startValue;
        }
        if (time >= endTime)
        {
            return endValue;
        }
        return Hermite(time, startTime, startValue, 0, endTime, endValue, 0);
    }

    /// <summary>
    /// A monotonic ease-in curve for the centre balls: its velocity continues
    /// to increase until the existing expansion deadline, without changing the
    /// start value, end value, or duration of the animation.
    /// </summary>
    private static double SampleAccelerating(double time, double startTime, double startValue,
        double endTime, double endValue)
    {
        if (time <= startTime)
        {
            return startValue;
        }
        if (time >= endTime)
        {
            return endValue;
        }

        var t = (time - startTime) / (endTime - startTime);
        return startValue + (endValue - startValue) * t * t;
    }

    private static double Hermite(double time, double startTime, double startValue, double startSlope,
        double endTime, double endValue, double endSlope)
    {
        var duration = endTime - startTime;
        var t = Math.Clamp((time - startTime) / duration, 0, 1);
        var t2 = t * t;
        var t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * startValue +
               (t3 - 2 * t2 + t) * duration * startSlope +
               (-2 * t3 + 3 * t2) * endValue +
               (t3 - t2) * duration * endSlope;
    }

    private Matrix CreateRotationMatrix(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return Matrix.CreateTranslation(-_center.X, -_center.Y) *
               Matrix.CreateRotation(radians) *
               Matrix.CreateTranslation(_center.X, _center.Y);
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
