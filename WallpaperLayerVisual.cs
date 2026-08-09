using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    public WallpaperLayerVisual()
    {
        ClipToBounds = true;
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
        var fill = ParseColor(layer.FillColor, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        var stroke = ParseColor(layer.StrokeColor, Colors.White);
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
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(w / 2, 0), true);
                    g.LineTo(new Point(w, h));
                    g.LineTo(new Point(0, h));
                    g.EndFigure(true);
                }

                context.DrawGeometry(fillBrush, pen, geo);
                break;
        }
    }

    private void DrawText(DrawingContext context, WallpaperLayerItem layer)
    {
        var text = string.IsNullOrEmpty(layer.Text) ? " " : layer.Text;
        var size = Math.Max(4, layer.TextFontSize);
        var brush = new SolidColorBrush(ParseColor(layer.TextColor, Colors.White));
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
        var lines = new List<string>();
        foreach (var hardLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines.AddRange(WrapLine(hardLine, maxWidth, typeface, size));
        }

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
            context.DrawText(formatted, new Point(x, y0 + i * lineHeight));
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

    private static Color ParseColor(string text, Color fallback)
    {
        try
        {
            return Color.Parse(text);
        }
        catch (FormatException)
        {
            return fallback;
        }
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
