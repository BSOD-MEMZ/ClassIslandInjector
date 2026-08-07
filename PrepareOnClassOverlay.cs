using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 「即将上课」倒计时覆盖层的公共基类。
/// 统一由注入器的 16ms 动画时钟推进 <see cref="Phase"/>，各子类在 <see cref="Loop"/>
/// （0..1 循环进度）基础上自绘自己的样式。
/// </summary>
internal abstract class PrepareOnClassOverlay : Control
{
    /// <summary>淡入 / 淡出时长（秒）。</summary>
    private const double FadeSeconds = 0.3;

    /// <summary>由注入器每帧推进的相位（秒 × 速度）。</summary>
    public double Phase { get; set; }

    /// <summary>该样式的播放速度（每秒循环次数，由设置注入）。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>0..1 的循环进度（Phase 的小数部分）。</summary>
    protected double Loop => Phase - Math.Floor(Phase);

    /// <summary>覆盖层创建时间（用于淡入淡出计时）。</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>是否正在淡出（即将被移除）。</summary>
    public bool IsFadingOut { get; private set; }

    /// <summary>淡出是否已完成，可安全移除。</summary>
    public bool IsFadeComplete => IsFadingOut && (DateTime.UtcNow - CreatedAt).TotalSeconds >= FadeSeconds;

    /// <summary>请求淡出（完成后由注入器移除）。</summary>
    public void BeginFadeOut() => IsFadingOut = true;

    /// <summary>取消淡出（重新进入即将上课状态时恢复）。</summary>
    public void CancelFadeOut() => IsFadingOut = false;

    /// <summary>当前应显示的透明度（进入主界面时淡入、离开时淡出）。</summary>
    public double FadeOpacity
    {
        get
        {
            var elapsed = (DateTime.UtcNow - CreatedAt).TotalSeconds;
            return IsFadingOut
                ? Math.Clamp((FadeSeconds - elapsed) / FadeSeconds, 0, 1)
                : Math.Clamp(elapsed / FadeSeconds, 0, 1);
        }
    }

    protected PrepareOnClassOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Opacity = 0;
    }
}

/// <summary>
/// 扩散光环：从主界面中心向外扩散并淡出的圆环。
/// </summary>
internal sealed class CountdownPulseRingOverlay : PrepareOnClassOverlay
{
    public Color Color { get; set; } = Colors.White;

    public double Thickness { get; set; } = 3;

    /// <summary>光环最大半径占主界面高度（取宽高较小者）的比例。</summary>
    public double MaxRadius { get; set; } = 0.5;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 12 || Bounds.Height < 8)
        {
            return;
        }

        var t = Loop;
        var maxRadius = Math.Max(1, MaxRadius * Math.Min(Bounds.Width, Bounds.Height) / 2);
        var radius = t * maxRadius;
        var alpha = (byte)Math.Clamp(Color.A * (1 - t) * 1.2, 0, 255);
        var brush = new SolidColorBrush(new Color(alpha, Color.R, Color.G, Color.B));
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(brush, Thickness),
            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
    }
}

/// <summary>
/// 扫描线：一道带渐变尾迹的光线扫过主界面（横向上下扫 / 纵向左右扫）。
/// 尾迹由多条逐条变淡变细的平行线组成，避免单色线条导致尾迹不可见。
/// </summary>
internal sealed class CountdownScanlineOverlay : PrepareOnClassOverlay
{
    /// <summary>尾迹长度占控件长边的比例。</summary>
    private const double TailRatio = 0.22;

    /// <summary>尾迹平行线数量（含主线），间距较密以形成连续尾迹。</summary>
    private const int TailLines = 8;

    public Color Color { get; set; } = Colors.White;

    public double Thickness { get; set; } = 2;

    /// <summary>扫描方向：横向（水平线上下扫）或纵向（竖直线左右扫）。</summary>
    public ScanlineDirection Direction { get; set; } = ScanlineDirection.Horizontal;

    /// <summary>是否绘制渐变尾迹；关闭时只画主线。</summary>
    public bool TailEnabled { get; set; } = true;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 12 || Bounds.Height < 8)
        {
            return;
        }

        var t = Loop;
        if (Direction == ScanlineDirection.Horizontal)
        {
            var y = t * Bounds.Height;
            DrawTail(context, new Point(0, y), new Point(Bounds.Width, y), Bounds.Height);
        }
        else
        {
            var x = t * Bounds.Width;
            DrawTail(context, new Point(x, 0), new Point(x, Bounds.Height), Bounds.Width);
        }
    }

    /// <summary>
    /// 从扫描线向后退方向绘制渐变尾迹：主线最亮最粗，越远越淡越细。
    /// </summary>
    private void DrawTail(DrawingContext context, Point headStart, Point headEnd, double extent)
    {
        var lineCount = TailEnabled ? TailLines : 1;
        var spacing = Math.Max(1.5, extent * TailRatio / (TailLines - 1));
        for (var i = 0; i < lineCount; i++)
        {
            var offset = i * spacing;
            Point lineStart;
            Point lineEnd;
            if (Direction == ScanlineDirection.Horizontal)
            {
                lineStart = headStart.WithY(headStart.Y - offset);
                lineEnd = headEnd.WithY(headEnd.Y - offset);
            }
            else
            {
                lineStart = headStart.WithX(headStart.X - offset);
                lineEnd = headEnd.WithX(headEnd.X - offset);
            }

            if (lineStart.Y < 0 || lineStart.Y > Bounds.Height ||
                lineStart.X < 0 || lineStart.X > Bounds.Width)
            {
                continue;
            }

            var factor = 1 - (double)i / TailLines;
            var alpha = (byte)Math.Clamp(Color.A * factor * factor, 0, 255);
            if (alpha <= 0)
            {
                continue;
            }

            var brush = new SolidColorBrush(new Color(alpha, Color.R, Color.G, Color.B));
            var thickness = Math.Max(0.5, Thickness * (1 - 0.6 * i / TailLines));
            context.DrawLine(new Pen(brush, thickness), lineStart, lineEnd);
        }
    }
}

/// <summary>
/// 「即将上课」红色警告：全屏内发光 + 边框光晕，并随 <see cref="PrepareOnClassOverlay.Loop"/>
/// 周期性闪动。仿照流光（跑马灯）的内发光绘制方式，但使用纯色警示（默认红色），
/// 宿主在专用全屏覆盖窗口（<see cref="MarqueeOverlayWindow"/>）里，覆盖整块屏幕。
/// </summary>
internal sealed class PrepareOnClassWarningOverlay : PrepareOnClassOverlay
{
    public Color Color { get; set; } = Color.FromArgb(0x66, 0xFF, 0, 0);

    /// <summary>每秒闪动次数（与 <see cref="Speed"/> 保持同一数值）。</summary>
    public double FlashSpeed { get; set; } = 3;

    /// <summary>闪动幅度：0 常亮，1 完全熄灭的闪烁。</summary>
    public double FlashAmount { get; set; } = 0.55;

    /// <summary>边框厚度（相对屏幕短边的比例）。</summary>
    public double FrameThickness { get; set; } = 0.02;

    /// <summary>整体透明度（0..1，在颜色自带 alpha 之上再叠加）。</summary>
    public double OpacityScale { get; set; } = 1;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 12 || Bounds.Height < 8)
        {
            return;
        }

        // 闪动：亮度在 [1-FlashAmount, 1] 之间按正弦起伏（Loop 每秒循环 FlashSpeed 次）。
        var flash = 1 - FlashAmount * (0.5 + 0.5 * Math.Sin(Loop * Math.Tau));
        var maxDim = Math.Min(Bounds.Width, Bounds.Height);
        var edgeAlpha = (byte)Math.Clamp(Color.A * OpacityScale * flash, 0, 255);
        if (edgeAlpha <= 0)
        {
            return;
        }

        // 全屏内发光：中心透明、边缘红色，向内柔和过渡（与流光的内发光同款画法）。
        var edge = new Color(edgeAlpha, Color.R, Color.G, Color.B);
        var transparent = Color.FromArgb(0, Color.R, Color.G, Color.B);
        var glowBrush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(transparent, 0.5),
                new GradientStop(edge, 1)
            }
        };
        context.DrawRectangle(glowBrush, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

        // 边框光晕：沿屏幕边缘的红色描边（比内发光更深更锐利），随闪动一起起伏。
        var frameAlpha = (byte)Math.Clamp(Color.A * 1.5 * OpacityScale * flash, 0, 255);
        var frameThickness = Math.Max(2, maxDim * FrameThickness);
        var framePen = new Pen(new SolidColorBrush(new Color(frameAlpha, Color.R, Color.G, Color.B)), frameThickness);
        var inset = frameThickness / 2;
        context.DrawRectangle(null, framePen,
            new Rect(inset, inset, Bounds.Width - frameThickness, Bounds.Height - frameThickness));
    }
}
