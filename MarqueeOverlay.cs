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
    }

    public bool IsCompleted => (DateTime.UtcNow - _startedAt).TotalSeconds >= _duration;

    public void Advance() => InvalidateVisual();

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
            DrawInnerGlow(context, width, height, elapsed);
            DrawSoftFrame(context, width, height, elapsed);
        }
    }

    /// <summary>
    /// 内发光：屏幕内部保持透明，越靠边缘颜色越深、越往内越柔和地淡出（向内扩散变模糊）。
    /// 用径向渐变天然形成平滑柔和的过渡，不模糊边框本身，外沿保持清晰。
    /// </summary>
    private void DrawInnerGlow(DrawingContext context, double width, double height, double elapsed)
    {
        var edgeAlpha = (byte)(72 * TintFactor());
        if (edgeAlpha <= 0)
        {
            return;
        }

        var hue = (elapsed * _speed * 90.0 + 360.0) % 360.0;
        var edge = Tint(HsvToColor(hue, 0.85, 1.0, edgeAlpha));
        var transparent = Color.FromArgb(0, edge.R, edge.G, edge.B);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(transparent, 0.52),
                new GradientStop(edge, 1)
            }
        };
        context.DrawRectangle(brush, null, new Rect(0, 0, width, height), 0, 0, default(BoxShadows));
    }

    /// <summary>
    /// 边框跑马灯：外沿紧贴屏幕边缘、颜色最深且锐利；向内用逐边线性渐变遮罩
    /// 平滑淡出（柔和扩散）。四条边与四个角采用互不重叠的分区（角拆成两个三角），
    /// 避免重复绘制导致角落颜色格外深。彩虹沿边框旋转流动。
    /// </summary>
    private void DrawSoftFrame(DrawingContext context, double width, double height, double elapsed)
    {
        var minDim = Math.Min(width, height);
        var thickness = Math.Max(2, minDim * _frameThicknessFraction);
        // 内侧光晕扩散深度（外沿不透明 → 向内淡出）。
        var glowDepth = Math.Max(thickness * 2.4, 14);
        var fullRect = new Rect(0, 0, width, height);
        var rotation = elapsed * _speed * 360.0;

        // 旋转彩虹内容：底层全圈彩虹 + 跑马灯主光弧段。
        var baseBrush = BuildConic(rotation * 0.25, 170);
        var cometBrush = BuildComet(rotation, 245);

        void DrawContent()
        {
            context.DrawRectangle(baseBrush, null, fullRect, 0, 0, default(BoxShadows));
            context.DrawRectangle(cometBrush, null, fullRect, 0, 0, default(BoxShadows));
        }

        // 各边的「距边渐变」遮罩（外沿不透明 → 向内淡出）。
        var topMask = EdgeMask(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative));
        var bottomMask = EdgeMask(new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));
        var leftMask = EdgeMask(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));
        var rightMask = EdgeMask(new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));

        // 四条边（不含角，与角分区互不重叠）。
        var gd = glowDepth;
        DrawMaskedRegion(context, new Rect(gd, 0, width - 2 * gd, gd), topMask, DrawContent);
        DrawMaskedRegion(context, new Rect(gd, height - gd, width - 2 * gd, gd), bottomMask, DrawContent);
        DrawMaskedRegion(context, new Rect(0, gd, gd, height - 2 * gd), leftMask, DrawContent);
        DrawMaskedRegion(context, new Rect(width - gd, gd, gd, height - 2 * gd), rightMask, DrawContent);

        // 四个角：各拆成两个三角，分别用相邻边的「距边渐变」遮罩，
        // 与相邻边在边界处无缝衔接（min(距边)），且互不重叠、不会格外深。
        DrawCorner(context, new Rect(0, 0, gd, gd),
            new Point(0, 0), new Point(gd, gd), new Point(0, gd), leftMask, new Point(gd, 0), topMask, DrawContent);
        DrawCorner(context, new Rect(width - gd, 0, gd, gd),
            new Point(width, 0), new Point(width - gd, gd), new Point(width, gd), rightMask, new Point(width - gd, 0), topMask, DrawContent);
        DrawCorner(context, new Rect(0, height - gd, gd, gd),
            new Point(0, height), new Point(gd, height - gd), new Point(0, height - gd), leftMask, new Point(gd, height), bottomMask, DrawContent);
        DrawCorner(context, new Rect(width - gd, height - gd, gd, gd),
            new Point(width, height), new Point(width - gd, height - gd), new Point(width, height - gd), rightMask, new Point(width - gd, height), bottomMask, DrawContent);
    }

    /// <summary>在指定区域内用「外沿不透明 → 向内淡出」的渐变遮罩绘制内容。</summary>
    private static void DrawMaskedRegion(DrawingContext context, Rect region, LinearGradientBrush mask,
        Action drawContent)
    {
        using (context.PushClip(region))
        using (context.PushOpacityMask(mask, region))
        {
            drawContent();
        }
    }

    /// <summary>
    /// 绘制一个角：沿对角线拆成两个三角，各用相邻边的「距边渐变」遮罩，
    /// 使角与相邻边在边界处无缝衔接（相当于 min(到相邻两条边的距离)）。
    /// </summary>
    private static void DrawCorner(DrawingContext context, Rect corner,
        Point diagA, Point diagB, Point tri1, LinearGradientBrush tri1Mask,
        Point tri2, LinearGradientBrush tri2Mask, Action drawContent)
    {
        DrawTriangle(context, corner, diagA, diagB, tri1, tri1Mask, drawContent);
        DrawTriangle(context, corner, diagA, diagB, tri2, tri2Mask, drawContent);
    }

    /// <summary>用三角形几何裁剪 + 遮罩绘制内容（遮罩相对整个角区域映射，保证与相邻边连续）。</summary>
    private static void DrawTriangle(DrawingContext context, Rect maskBounds, Point a, Point b, Point c,
        LinearGradientBrush mask, Action drawContent)
    {
        var geometry = new StreamGeometry();
        using (var gc = geometry.Open())
        {
            gc.BeginFigure(a, true);
            gc.LineTo(b);
            gc.LineTo(c);
            gc.EndFigure(true);
        }

        using (context.PushGeometryClip(geometry))
        using (context.PushOpacityMask(mask, maskBounds))
        {
            drawContent();
        }
    }

    /// <summary>构建「先保持不透明、再柔和淡出到透明」的遮罩渐变（软边）。</summary>
    private static LinearGradientBrush EdgeMask(RelativePoint start, RelativePoint end) => new()
    {
        StartPoint = start,
        EndPoint = end,
        GradientStops =
        {
            new GradientStop(Colors.White, 0),
            new GradientStop(Colors.White, 0.3),
            new GradientStop(Color.FromArgb(190, 255, 255, 255), 0.55),
            new GradientStop(Color.FromArgb(100, 255, 255, 255), 0.78),
            new GradientStop(Colors.Transparent, 1)
        }
    };

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
