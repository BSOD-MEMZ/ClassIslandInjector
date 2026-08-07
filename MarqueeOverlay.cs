using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 全屏内发光 + 边框跑马灯覆盖层：仿手机「智慧识屏」/ Gemini 等语音助手激活时的效果。
/// 屏幕内部保持透明，屏幕四周（边框处）是颜色较深的发光描边，彩虹色沿矩形边框
/// 旋转流动（跑马灯），形成「流光溢彩」的视觉效果。独立于 <see cref="RippleType"/>，
/// 可与任意 Ripple 类型叠加播放，由注入器 16ms 时钟统一推进，播放完毕后自动移除。
/// </summary>
internal sealed class MarqueeOverlay : Control, IRippleEffect
{
    private readonly double _duration;
    private readonly double _speed;              // 彩虹每秒绕边框旋转的圈数
    private readonly double _opacityScale;
    private readonly double _frameThicknessFraction; // 边框厚度相对屏幕短边的比例
    private readonly Color _tint;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    /// <summary>已应用的模糊半径（避免每帧重复赋值 Effect 触发重绘）。</summary>
    private double _appliedBlurRadius = -1;

    public MarqueeOverlay(double durationSeconds, double speed, double opacityScale,
        double frameThicknessFraction, Color tint)
    {
        _duration = Math.Max(0.1, durationSeconds);
        _speed = Math.Clamp(speed, 0.1, 8);
        _opacityScale = Math.Clamp(opacityScale, 0, 1);
        _frameThicknessFraction = Math.Clamp(frameThicknessFraction, 0.01, 0.15);
        _tint = tint;
        IsHitTestVisible = false;
        // 内发光限定在特效窗口/主界面内部。
        ClipToBounds = true;
        // 构造时先给一个默认模糊（运行时按实际尺寸在 Advance 里细化）；
        // 不能在 Render 里设置 Effect，否则抛「Visual was invalidated during the render pass」。
        Effect = new BlurEffect { Radius = 16 };
    }

    public bool IsCompleted => (DateTime.UtcNow - _startedAt).TotalSeconds >= _duration;

    public void Advance()
    {
        // 高斯模糊半径依赖实际尺寸，需在渲染期之外（动画时钟回调）设置 Effect。
        EnsureBlurEffect();
        InvalidateVisual();
    }

    /// <summary>
    /// 按当前尺寸设置高斯模糊半径（构造后先用了默认值，这里在布局完成/尺寸变化时细化）。
    /// 模糊半径随边框厚度联动；外层超出屏幕的部分被裁掉，向内的一侧形成柔和的「向内发光」。
    /// </summary>
    private void EnsureBlurEffect()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var thickness = Math.Max(2, Math.Min(width, height) * _frameThicknessFraction);
        var blurRadius = Math.Clamp(thickness * 1.0, 6, 72);
        if (Math.Abs(_appliedBlurRadius - blurRadius) <= 0.5)
        {
            return;
        }

        _appliedBlurRadius = blurRadius;
        Effect = new BlurEffect { Radius = blurRadius };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
        var progress = Math.Clamp(elapsed / _duration, 0, 1);
        // 淡入 0→0.2，淡出 0.75→1，中间保持全亮。
        var fade = SmoothStep(progress, 0, 0.2) * (1 - SmoothStep(progress, 0.75, 1));
        var opacity = _opacityScale * fade;
        if (opacity <= 0.01)
        {
            return;
        }

        using (context.PushOpacity(opacity))
        {
            DrawInnerGlow(context, width, height);
            DrawRotatingFrame(context, width, height, elapsed);
        }
    }

    /// <summary>
    /// 内发光：屏幕内部保持透明，越靠近边缘颜色越深，营造从边框向内泛光的柔和氛围。
    /// </summary>
    private void DrawInnerGlow(DrawingContext context, double width, double height)
    {
        var edgeAlpha = (byte)(38 * TintFactor());
        if (edgeAlpha <= 0)
        {
            return;
        }

        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, _tint.R, _tint.G, _tint.B), 0.62),
                new GradientStop(new Color(edgeAlpha, _tint.R, _tint.G, _tint.B), 1)
            }
        };
        context.DrawRectangle(brush, null, new Rect(0, 0, width, height), 0, 0, default(BoxShadows));
    }

    /// <summary>
    /// 边框跑马灯：紧贴屏幕边缘的发光描边，直角让四条边与四个边角全部铺满。
    /// 底层是一圈缓慢旋转的柔和彩虹（边框始终有深色），上层是一条明亮的彩虹弧段
    /// 沿边框快速旋转（跑马灯主光）。
    /// </summary>
    private void DrawRotatingFrame(DrawingContext context, double width, double height, double elapsed)
    {
        var minDim = Math.Min(width, height);
        var thickness = Math.Max(2, minDim * _frameThicknessFraction);

        // 边框外沿与屏幕边缘齐平（内缩半个线宽让描边完整可见），圆角 0 → 四角铺满。
        var inset = thickness / 2;
        var rect = new Rect(inset, inset, width - inset * 2, height - inset * 2);
        var rounded = new RoundedRect(rect, 0, 0);
        var rotation = elapsed * _speed * 360.0;

        // ① 底层：柔和全圈彩虹（边框始终有深色）。
        var baseBrush = BuildConic(rotation * 0.25, 80);
        DrawFrameGlow(context, rounded, baseBrush, thickness, 2);

        // ② 上层：明亮彩虹弧段沿边框旋转（跑马灯主光）。
        var cometBrush = BuildComet(rotation, 235);
        DrawFrameGlow(context, rounded, cometBrush, thickness * 1.2, 2);
    }

    /// <summary>构造绕中心旋转的锥形彩虹画刷（全圈，指定最大不透明度）。</summary>
    private ConicGradientBrush BuildConic(double rotation, byte maxAlpha)
    {
        var stops = new GradientStops();
        const int count = 12;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)(count - 1);
            var hue = (t * 360.0) % 360.0;
            stops.Add(new GradientStop(Tint(HsvToColor(hue, 0.9, 1.0, maxAlpha)), t));
        }

        return new ConicGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Angle = rotation,
            GradientStops = stops
        };
    }

    /// <summary>
    /// 构造带软头软尾的明亮彩虹弧（覆盖约 42% 圆周），其余透明；
    /// 旋转该画刷即得沿边框跑的「跑马灯」主光，头尾透明保证无缝循环。
    /// </summary>
    private ConicGradientBrush BuildComet(double rotation, byte maxAlpha)
    {
        var stops = new GradientStops();
        const int count = 48;
        for (var i = 0; i <= count; i++)
        {
            var t = i / (double)count;
            var envelope = t <= 0.42 ? Math.Pow(Math.Sin(t / 0.42 * Math.PI), 0.9) : 0;
            var hue = (t * 300.0) % 360.0;
            var alpha = (byte)(maxAlpha * envelope);
            stops.Add(new GradientStop(Tint(HsvToColor(hue, 0.95, 1.0, alpha)), t));
        }

        return new ConicGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Angle = rotation,
            GradientStops = stops
        };
    }

    /// <summary>
    /// 沿屏幕边缘绘制描边：配合控件级 <see cref="BlurEffect"/> 高斯模糊，
    /// 两层描边（外层更宽更淡）补充光晕深度，最终呈现柔和的向内辉光。
    /// </summary>
    private static void DrawFrameGlow(DrawingContext context, RoundedRect rounded, Brush brush,
        double thickness, int passes)
    {
        for (var i = 0; i < passes; i++)
        {
            var spread = i / (double)(passes - 1);
            var stroke = thickness * (1 + spread * 1.3);
            var alphaScale = Math.Pow(1 - spread, 1.5);
            var pen = new Pen(brush, stroke);
            using (context.PushOpacity(alphaScale))
            {
                context.DrawRectangle(null, pen, rounded, default(BoxShadows));
            }
        }
    }

    /// <summary>色调 alpha 越高越偏向该颜色（0 为纯彩虹）。</summary>
    private double TintFactor() => _tint.A / 255.0;

    /// <summary>按设置的色调给彩虹着色：色调 alpha 越高越偏向该颜色（0 为纯彩虹）。</summary>
    private Color Tint(Color color)
    {
        if (_tint.A == 0)
        {
            return color;
        }

        var w = _tint.A / 255.0;
        return new Color(color.A,
            (byte)(color.R * (1 - w) + _tint.R * w),
            (byte)(color.G * (1 - w) + _tint.G * w),
            (byte)(color.B * (1 - w) + _tint.B * w));
    }

    private static double SmoothStep(double t, double a, double b)
    {
        var x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>HSV → ARGB（用于生成流动彩虹）。</summary>
    private static Color HsvToColor(double hue, double saturation, double value, byte alpha)
    {
        var h = ((hue % 360.0) + 360.0) % 360.0;
        var c = value * saturation;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = value - c;
        double r, g, b;
        if (h < 60)
        {
            r = c; g = x; b = 0;
        }
        else if (h < 120)
        {
            r = x; g = c; b = 0;
        }
        else if (h < 180)
        {
            r = 0; g = c; b = x;
        }
        else if (h < 240)
        {
            r = 0; g = x; b = c;
        }
        else if (h < 300)
        {
            r = x; g = 0; b = c;
        }
        else
        {
            r = c; g = 0; b = x;
        }

        return new Color(alpha,
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}
