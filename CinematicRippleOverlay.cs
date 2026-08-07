using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClassIslandInjector;

/// <summary>
/// 高级强调 Ripple：抓取当前全屏画面，然后在全屏特效窗口里叠加
/// 「晃动 + 平滑缩放过渡 + 涟漪扩散 + 闪光（亮度扩散）+ 只模糊一次的交叉淡化还原」。
/// 模糊只在创建时对抓取帧做一次（生成预模糊位图），动画期间用透明度把模糊层淡出、
/// 露出下面的清晰帧，避免每帧重算模糊的 GPU 开销。由注入器 16ms 时钟推进，
/// 播完自动移除并释放抓取帧与模糊帧。
/// </summary>
internal sealed class CinematicRippleOverlay : Control, IRippleEffect, IDisposable
{
    private readonly Bitmap _frame;
    private readonly Bitmap? _blurredFrame;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly TimeSpan _duration;
    private readonly double _opacityScale;
    private readonly double _shakeAmount;
    private readonly double _flashAmount;
    private double _shakeX;
    private double _shakeY;
    private bool _disposed;

    public CinematicRippleOverlay(Bitmap frame, TimeSpan duration, double opacityScale,
        double shakeAmount, double blurRadius, double flashAmount)
    {
        _frame = frame;
        _duration = duration;
        _opacityScale = Math.Clamp(opacityScale, 0, 1);
        _shakeAmount = Math.Max(0, shakeAmount);
        _flashAmount = Math.Clamp(flashAmount, 0, 1);
        // 模糊只做一次：创建时预生成模糊帧，动画期间用透明度淡出还原清晰画面。
        _blurredFrame = blurRadius > 0 ? PreBlurFrame(frame, blurRadius) : null;
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public bool IsCompleted => DateTime.UtcNow - _startedAt >= _duration;

    public void Advance()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        // 晃动：衰减的正弦（两条不同频率/相位，避免呆板）。
        var decay = Math.Pow(1 - progress, 2);
        _shakeX = _shakeAmount * decay * Math.Sin(progress * Math.Tau * 5);
        _shakeY = _shakeAmount * 0.75 * decay * Math.Sin(progress * Math.Tau * 6 + 1.3);
        Opacity = _opacityScale;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var progress = Math.Clamp((DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        var fullRect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // 平滑缩放过渡：起始轻微放大、中途到峰值、末尾回落到原大小（正弦 in-out 曲线）。
        var zoom = 1 + 0.1 * Math.Sin(progress * Math.PI);
        // 模糊淡出：前 30% 时长内模糊层透明度从 1 → 0，露出清晰帧（模糊只算一次）。
        var blurOpacity = _blurredFrame == null ? 0 : 1 - Math.Clamp(progress / 0.3, 0, 1);

        // 晃动整体作用于画面与涟漪环，闪光在屏幕空间叠加。
        using (context.PushTransform(Matrix.CreateTranslation(_shakeX, _shakeY)))
        {
            // 基础画面：平滑缩放（放大 → 回落），轻微放大兼防晃动露边。
            var baseRect = ScaleRectAbout(center, fullRect, zoom);
            context.DrawImage(_frame, baseRect);
            // 模糊层：与清晰帧同变换对齐，透明度淡出即「由模糊过渡回清晰」。
            if (_blurredFrame is { } blurred && blurOpacity > 0.01)
            {
                using (context.PushOpacity(blurOpacity))
                {
                    context.DrawImage(blurred, baseRect);
                }
            }

            // 两圈错峰扩散的涟漪环（柔化亮边）。
            DrawRippleRing(context, progress, 0);
            DrawRippleRing(context, progress, 1);
        }

        DrawFlash(context, progress);
    }

    /// <summary>绘制一圈「水波」涟漪环：环内画面被放大（折射感），亮边柔和。</summary>
    private void DrawRippleRing(DrawingContext context, double progress, int index)
    {
        var p = Math.Clamp((progress - 0.12 * (index + 1)) / 0.62, 0, 1);
        if (p <= 0 || p >= 1)
        {
            return;
        }

        var maxDim = Math.Max(Bounds.Width, Bounds.Height);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = maxDim * (0.16 + p * 0.6);
        var thickness = Math.Max(10, maxDim * 0.08 * (1 - p * 0.55));

        // 环形裁剪：外圆减内圆（EvenOdd 挖洞），环内显示放大画面（水波折射感）。
        var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new EllipseGeometry(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2)));
        ring.Children.Add(new EllipseGeometry(new Rect(
            center.X - radius + thickness, center.Y - radius + thickness,
            (radius - thickness) * 2, (radius - thickness) * 2)));

        var zoom = 1 + 0.25 * (1 - p);
        using (context.PushGeometryClip(ring))
        {
            context.DrawImage(_frame, ScaleRectAbout(center, new Rect(0, 0, Bounds.Width, Bounds.Height), zoom));
        }

        DrawSoftRingGlow(context, center, radius, thickness, 1 - p);
    }

    /// <summary>
    /// 柔化水波亮边：用多圈同心白色细线叠加出中间亮、两侧柔和淡出的光晕带，
    /// 替代原来单条硬描边（alpha 按正弦包络衰减，与扫描线尾迹同款手法）。
    /// </summary>
    private void DrawSoftRingGlow(DrawingContext context, Point center, double radius, double thickness, double fade)
    {
        const int layers = 8;
        var half = thickness * 0.85;
        for (var i = 0; i <= layers; i++)
        {
            var t = (double)i / layers;                 // 0..1：从内沿到外沿
            var r = radius - half + t * half * 2;
            var falloff = Math.Pow(Math.Sin(t * Math.PI), 1.6);   // 中间亮、两端柔和淡出
            var alpha = (byte)(150 * fade * falloff);
            if (alpha <= 0)
            {
                continue;
            }

            var width = Math.Max(1.2, thickness * 0.15 * (1 + 0.5 * Math.Sin(t * Math.PI)));
            context.DrawEllipse(null,
                new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), width),
                new Rect(center.X - r, center.Y - r, r * 2, r * 2));
        }
    }

    /// <summary>闪光（亮度扩散）：从中心向外扩散的白色径向渐变，随进度衰减。</summary>
    private void DrawFlash(DrawingContext context, double progress)
    {
        if (_flashAmount <= 0)
        {
            return;
        }

        var alpha = (byte)Math.Clamp(255 * _flashAmount * Math.Pow(1 - progress, 1.8), 0, 255);
        if (alpha <= 0)
        {
            return;
        }

        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(alpha, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb((byte)(alpha * 0.5), 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
            }
        };
        context.DrawRectangle(brush, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    /// <summary>
    /// 对抓取帧预做一次模糊，生成模糊位图（仅在创建时执行一次）。
    /// 用临时 Image 控件（带 BlurEffect）渲染进 RenderTargetBitmap；失败返回 null（无模糊降级）。
    /// </summary>
    private static Bitmap? PreBlurFrame(Bitmap source, double radius)
    {
        try
        {
            var width = source.PixelSize.Width;
            var height = source.PixelSize.Height;
            var size = new Size(width, height);
            var image = new Image
            {
                Source = source,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = radius }
            };
            image.Measure(size);
            image.Arrange(new Rect(size));
            var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            target.Render(image);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static Rect ScaleRectAbout(Point center, Rect rect, double scale)
    {
        var width = rect.Width * scale;
        var height = rect.Height * scale;
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frame.Dispose();
        _blurredFrame?.Dispose();
    }
}
