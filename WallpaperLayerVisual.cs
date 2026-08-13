using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Theming;
using ClassIsland.Shared;
using System.Globalization;

namespace ClassIslandInjector;

/// <summary>
/// 矢量形状 / 文本框图层的渲染控件（编辑器画布与运行时底图宿主共用）。
/// 矩形坐标即图层矩形（0,0 → Width,Height），旋转由外层 RenderTransform 完成；
/// 不透明度由外层 Opacity 控制，此处只负责按图层内容绘制。
/// </summary>
public sealed class WallpaperLayerVisual : Control
{
    private WallpaperLayerItem? _layer;
    private string? _overrideText;
    private EventHandler<ThemeUpdatedEventArgs>? _themeHandler;
    /// <summary>文本换行缓存键（文本 + 字号 + 字体 + 粗细 + 宽度）。</summary>
    private string _wrapCacheKey = string.Empty;
    /// <summary>文本换行缓存结果：键变化时才重新测量，避免拖拽/重绘时每帧重复二分换行。</summary>
    private readonly List<string> _wrapCacheLines = [];

    /// <summary>临时覆盖的文本内容（运行时「显示媒体标题」用）；为 null 时使用图层 Text。</summary>
    public string? OverrideText
    {
        get => _overrideText;
        set
        {
            if (_overrideText == value)
            {
                return;
            }

            _overrideText = value;
            InvalidateVisual();
        }
    }

    public WallpaperLayerVisual()
    {
        ClipToBounds = true;
        // 「跟随主题色」的图层需要随 ClassIsland 主题色变化实时重绘。
        // 采用惰性订阅：仅挂到视觉树后订阅主题事件（OnAttachedToVisualTree）、分离时退订，
        // 这样 RasterizeLayer 等从不挂树的临时实例不会泄漏订阅，重挂载后也会重新订阅。
    }

    /// <summary>订阅宿主主题更新事件（服务不可用时静默忽略，主题色回退系统蓝）。</summary>
    private void SubscribeTheme()
    {
        if (_themeHandler != null)
        {
            return;
        }

        try
        {
            var theme = IAppHost.TryGetService<IThemeService>();
            if (theme != null)
            {
                _themeHandler = (_, _) => Dispatcher.UIThread.Post(InvalidateVisual);
                theme.ThemeUpdated += _themeHandler;
            }
        }
        catch
        {
            // 主题服务不可用时忽略。
        }
    }

    /// <summary>退订主题事件，避免被宿主单例持有的委托强引用而泄漏。</summary>
    private void UnsubscribeTheme()
    {
        if (_themeHandler == null)
        {
            return;
        }

        try
        {
            var theme = IAppHost.TryGetService<IThemeService>();
            if (theme != null)
            {
                theme.ThemeUpdated -= _themeHandler;
            }
        }
        catch
        {
            // 忽略退订失败。
        }

        _themeHandler = null;
    }

    /// <summary>挂到视觉树时订阅主题事件（重挂载后重新订阅，保持「跟随主题色」始终生效）。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeTheme();
    }

    /// <summary>从视觉树分离时退订主题事件。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnsubscribeTheme();
    }

    /// <summary>当前渲染的图层（形状/文本内容）。</summary>
    public WallpaperLayerItem? Layer
    {
        get => _layer;
        set
        {
            _layer = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var layer = _layer;
        if (layer == null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        if (layer.Kind == WallpaperLayerKind.Text)
        {
            DrawText(context, layer);
        }
        else
        {
            DrawShape(context, layer);
        }
    }

    private void DrawShape(DrawingContext context, WallpaperLayerItem layer)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var fill = ResolveColor(layer.FillColor, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), layer.FillUsesThemeColor);
        var stroke = ResolveColor(layer.StrokeColor, Colors.White, layer.StrokeUsesThemeColor);
        var thickness = Math.Clamp(layer.StrokeThickness, 0, 200);
        var fillBrush = fill.A > 0 ? new SolidColorBrush(fill) : null;
        var pen = thickness > 0 && stroke.A > 0
            ? new Pen(new SolidColorBrush(stroke), thickness,
                lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

        switch (layer.ShapeType)
        {
            case WallpaperShapeType.Rectangle:
                context.DrawRectangle(fillBrush, pen, new Rect(0, 0, w, h), 0, 0, default);
                break;
            case WallpaperShapeType.RoundedRectangle:
                var cornerRadius = Math.Clamp(layer.ShapeCornerRadius, 0, Math.Min(w, h) / 2);
                context.DrawRectangle(fillBrush, pen, new Rect(0, 0, w, h), cornerRadius, cornerRadius, default);
                break;
            case WallpaperShapeType.Ellipse:
                context.DrawEllipse(fillBrush, pen, new Point(w / 2, h / 2), w / 2, h / 2);
                break;
            case WallpaperShapeType.Line:
                if (pen != null)
                {
                    context.DrawLine(pen, new Point(0, 0), new Point(w, h));
                }

                break;
            case WallpaperShapeType.Triangle:
                DrawGeometry(context, fillBrush, pen, BuildRegularPolygon(w, h, 3));
                break;
            case WallpaperShapeType.Diamond:
                DrawGeometry(context, fillBrush, pen, BuildRegularPolygon(w, h, 4));
                break;
            case WallpaperShapeType.Pentagon:
                DrawGeometry(context, fillBrush, pen, BuildRegularPolygon(w, h, 5));
                break;
            case WallpaperShapeType.Hexagon:
                DrawGeometry(context, fillBrush, pen, BuildRegularPolygon(w, h, 6));
                break;
            case WallpaperShapeType.Star:
                DrawGeometry(context, fillBrush, pen, BuildStarGeometry(w, h, layer.ShapeStarPoints, layer.ShapeStarInset));
                break;
            case WallpaperShapeType.Heart:
                DrawGeometry(context, fillBrush, pen, BuildHeartGeometry(w, h));
                break;
            case WallpaperShapeType.Parallelogram:
                var para = new StreamGeometry();
                using (var g = para.Open())
                {
                    g.BeginFigure(new Point(w * 0.25, 0), true);
                    g.LineTo(new Point(w, 0));
                    g.LineTo(new Point(w * 0.75, h));
                    g.LineTo(new Point(0, h));
                    g.EndFigure(true);
                }

                DrawGeometry(context, fillBrush, pen, para);
                break;
        }
    }

    private static void DrawGeometry(DrawingContext context, IBrush? fill, Pen? pen, StreamGeometry geometry)
        => context.DrawGeometry(fill, pen, geometry);

    /// <summary>构建正多边形路径（边数 ≥ 3，第一个顶点朝上）。</summary>
    private static StreamGeometry BuildRegularPolygon(double w, double h, int sides)
    {
        sides = Math.Max(3, sides);
        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Max(1, Math.Min(w, h) / 2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < sides; i++)
            {
                var angle = -Math.PI / 2 + i * 2 * Math.PI / sides;
                var x = cx + radius * Math.Cos(angle);
                var y = cy + radius * Math.Sin(angle);
                if (i == 0)
                {
                    g.BeginFigure(new Point(x, y), true);
                }
                else
                {
                    g.LineTo(new Point(x, y));
                }
            }

            g.EndFigure(true);
        }

        return geo;
    }

    /// <summary>构建星形路径（外顶点 + 内凹顶点交替，外半径 = 短边一半，内半径 = 外半径 × 内凹比例）。</summary>
    private static StreamGeometry BuildStarGeometry(double w, double h, int points, double inset)
    {
        points = Math.Clamp(points, 3, 16);
        inset = Math.Clamp(inset, 0.1, 0.95);
        var cx = w / 2;
        var cy = h / 2;
        var outer = Math.Max(1, Math.Min(w, h) / 2);
        var inner = outer * inset;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < points * 2; i++)
            {
                var radius = i % 2 == 0 ? outer : inner;
                var angle = -Math.PI / 2 + i * Math.PI / points;
                var x = cx + radius * Math.Cos(angle);
                var y = cy + radius * Math.Sin(angle);
                if (i == 0)
                {
                    g.BeginFigure(new Point(x, y), true);
                }
                else
                {
                    g.LineTo(new Point(x, y));
                }
            }

            g.EndFigure(true);
        }

        return geo;
    }

    /// <summary>构建心形路径（底部尖角 + 两个三次贝塞尔凸瓣）。</summary>
    private static StreamGeometry BuildHeartGeometry(double w, double h)
    {
        var cx = w / 2;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(cx, h * 0.96), true);
            // 左瓣
            g.CubicBezierTo(new Point(w * 0.02, h * 0.58), new Point(w * 0.2, h * 0.12), new Point(cx, h * 0.38));
            // 右瓣
            g.CubicBezierTo(new Point(w * 0.8, h * 0.12), new Point(w * 0.98, h * 0.58), new Point(cx, h * 0.96));
            g.EndFigure(true);
        }

        return geo;
    }

    private void DrawText(DrawingContext context, WallpaperLayerItem layer)
    {
        var text = string.IsNullOrEmpty(OverrideText) ? (layer.Text ?? string.Empty) : OverrideText!;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var size = Math.Max(4, layer.TextFontSize);
        var brush = new SolidColorBrush(ResolveColor(layer.TextColor, Colors.White, layer.TextUsesThemeColor));
        var typeface = new Typeface(ParseFontFamily(layer.TextFontFamily), FontStyle.Normal,
            layer.TextBold ? FontWeight.Bold : FontWeight.Normal);
        var alignment = layer.TextAlign switch
        {
            WallpaperTextAlign.Left => TextAlignment.Left,
            WallpaperTextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        var maxWidth = Bounds.Width;
        // 该 Avalonia 版本 FormattedText 无约束属性，这里按字符宽度手动换行。
        // 换行结果按「文本 + 字号 + 字体 + 粗细 + 宽度」缓存：拖拽 / 重绘时避免每帧
        // 二分测量构造大量 FormattedText（长文本下成本极高）。
        var wrapKey = $"{text}\u0001{size}\u0001{layer.TextFontFamily ?? string.Empty}\u0001{layer.TextBold}\u0001{maxWidth}";
        if (wrapKey != _wrapCacheKey)
        {
            _wrapCacheKey = wrapKey;
            _wrapCacheLines.Clear();
            foreach (var hardLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                _wrapCacheLines.AddRange(WrapLine(hardLine, maxWidth, typeface, size));
            }
        }

        var lines = _wrapCacheLines;

        var lineHeight = size * 1.25;
        var total = lines.Count * lineHeight;
        var y0 = Math.Max(0, (Bounds.Height - total) / 2);
        for (var i = 0; i < lines.Count; i++)
        {
            var formatted = new FormattedText(lines[i], CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, size, brush);
            var x = alignment == TextAlignment.Left ? 0
                : alignment == TextAlignment.Right ? Math.Max(0, maxWidth - formatted.Width)
                : Math.Max(0, (maxWidth - formatted.Width) / 2);
            var origin = new Point(x, y0 + i * lineHeight);
            if (layer.TextStrokeEnabled && layer.TextStrokeThickness > 0)
            {
                // 文字描边：把文本转为几何，先描边再填充（保持原填充色）。
                var strokeBrush = new SolidColorBrush(ParseColor(layer.TextStrokeColor, Colors.Black));
                var pen = new Pen(strokeBrush, Math.Clamp(layer.TextStrokeThickness, 0.5, 20))
                {
                    LineJoin = PenLineJoin.Round
                };
                var geometry = formatted.BuildGeometry(origin);
                if (geometry != null)
                {
                    // 只描边不填充（brush=null），再按文字填充色填充——镂空（透明填充）时
                    // 内部即露出背景，形成真正的空心字；否则描边色会填满字内形成「底色」。
                    context.DrawGeometry(null, pen, geometry);
                    context.DrawGeometry(brush, null, geometry);
                }
            }
            else
            {
                context.DrawText(formatted, origin);
            }
        }
    }

    /// <summary>把一个硬行按最大宽度拆成多行（用 FormattedText 实测宽度二分）。</summary>
    private static List<string> WrapLine(string text, double maxWidth, Typeface typeface, double size)
    {
        var result = new List<string>();
        if (maxWidth <= 0)
        {
            result.Add(text.Length == 0 ? " " : text);
            return result;
        }

        var remaining = text;
        while (remaining.Length > 0)
        {
            if (MeasureText(remaining, typeface, size) <= maxWidth)
            {
                result.Add(remaining);
                break;
            }

            var lo = 1;
            var hi = remaining.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (MeasureText(remaining.Substring(0, mid), typeface, size) <= maxWidth)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            result.Add(remaining.Substring(0, lo));
            remaining = remaining.Substring(lo);
        }

        if (result.Count == 0)
        {
            result.Add(" ");
        }

        return result;
    }

    private static double MeasureText(string text, Typeface typeface, double size)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black);
        return ft.Width;
    }

    private static Color ParseColor(string text, Color fallback) => ColorUtil.Parse(text, fallback);

    /// <summary>按图层设置取固定颜色或当前主题色，同时保留配置颜色的透明度。</summary>
    private static Color ResolveColor(string text, Color fallback, bool useThemeColor)
    {
        var color = ParseColor(text, fallback);
        if (!useThemeColor)
        {
            return color;
        }

        var accent = ThemePalette.AccentColor();
        return Color.FromArgb(color.A, accent.R, accent.G, accent.B);
    }

    /// <summary>解析用户选择的系统字体，缺失或卸载后平稳回退到默认字体。</summary>
    private static FontFamily ParseFontFamily(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return FontFamily.Default;
        }

        try
        {
            return FontFamily.Parse(text);
        }
        catch (ArgumentException)
        {
            return FontFamily.Default;
        }
    }
}
