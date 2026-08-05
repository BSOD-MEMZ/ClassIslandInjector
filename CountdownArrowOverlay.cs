using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// A lightweight, non-interactive chevron belt drawn above the built-in
/// prepare-for-class countdown. The chevrons slide horizontally as one stream
/// and use the full host height, so they remain flush with the island at any
/// scale or theme-provided height.
/// </summary>
internal sealed class CountdownArrowOverlay : PrepareOnClassOverlay
{
    public Color ArrowColor { get; set; } = Colors.White;
    /// <summary>屏幕上同时滑动的箭头组数量。</summary>
    public int ArrowCount { get; set; } = 6;
    /// <summary>每组内包含的箭头数量（2 即经典 &gt;&gt; 效果）。</summary>
    public int ArrowsPerGroup { get; set; } = 2;
    /// <summary>同一组内相邻箭头之间的间距（像素）。</summary>
    public double ArrowSpacing { get; set; } = 12;
    /// <summary>相邻箭头组之间的额外间距（像素）。</summary>
    public double ArrowGroupSpacing { get; set; } = 24;
    public double ArrowThickness { get; set; } = 8;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 12 || Bounds.Height < 8)
        {
            return;
        }

        var arrowWidth = Math.Clamp(Bounds.Height * 0.28, 7, 18);
        var groupCount = Math.Clamp(ArrowCount, 1, 24);
        var perGroup = Math.Clamp(ArrowsPerGroup, 1, 12);
        var innerStride = Math.Max(0, ArrowSpacing);
        // 组宽度 = 组内第一个箭头尖端到最后一个箭头尖端；组间距 = 组宽度 + 用户配置的组间隙。
        var groupWidth = arrowWidth + (perGroup - 1) * innerStride;
        var stride = Math.Max(groupWidth + Math.Max(0, ArrowGroupSpacing), Bounds.Width / Math.Max(groupCount, 1));
        var shift = (Phase - Math.Floor(Phase)) * stride;
        var top = 1d;
        var centerY = Bounds.Height / 2;
        var bottom = Bounds.Height - 1;

        // 每个箭头是一条连续折线（上端 → 尖端 → 下端）。必须把同一组内的箭头合并到
        // 同一个 StreamGeometry 里一次描边，否则用两条独立 DrawLine 时，两条半透明
        // 线条会在尖端处叠加导致颜色加深（\ 与 / 在尖端重合处变深的问题）。
        // 每组有独立的淡入淡出透明度，因此每组一个几何 + 一个画笔。
        var fadeDistance = Math.Max(groupWidth * 1.4, Bounds.Width * 0.12);
        for (var groupX = -stride + shift; groupX < Bounds.Width + groupWidth; groupX += stride)
        {
            var enterFade = Math.Clamp((groupX + groupWidth) / fadeDistance, 0, 1);
            var exitFade = Math.Clamp((Bounds.Width - groupX) / fadeDistance, 0, 1);
            var alpha = (byte)Math.Clamp(ArrowColor.A * 0.88 * Math.Min(enterFade, exitFade), 0, 255);
            if (alpha <= 0)
            {
                continue;
            }

            var color = new Color(alpha, ArrowColor.R, ArrowColor.G, ArrowColor.B);
            var pen = new Pen(new SolidColorBrush(color), ArrowThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var arrow = 0; arrow < perGroup; arrow++)
                {
                    var tipX = groupX + arrow * innerStride;
                    ctx.BeginFigure(new Point(tipX - arrowWidth, top), false);
                    ctx.LineTo(new Point(tipX, centerY));
                    ctx.LineTo(new Point(tipX - arrowWidth, bottom));
                    ctx.EndFigure(false);
                }
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }
}
