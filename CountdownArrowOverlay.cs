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
internal sealed class CountdownArrowOverlay : Control
{
    public double Phase { get; set; }
    public Color ArrowColor { get; set; } = Colors.White;
    public int ArrowCount { get; set; } = 6;
    public double ArrowThickness { get; set; } = 1.6;

    public CountdownArrowOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 12 || Bounds.Height < 8)
        {
            return;
        }

        var arrowWidth = Math.Clamp(Bounds.Height * 0.28, 7, 18);
        var groupCount = Math.Clamp(ArrowCount, 2, 24);
        var innerStride = arrowWidth * 0.92;
        var groupWidth = arrowWidth + innerStride;
        // ArrowCount represents groups. Each group is a compact ">>" pair,
        // while its stride deliberately leaves a larger empty gap before the
        // next group.
        var stride = Math.Max(groupWidth * 1.75, Bounds.Width / groupCount);
        var shift = (Phase - Math.Floor(Phase)) * stride;
        var top = 1d;
        var centerY = Bounds.Height / 2;
        var bottom = Bounds.Height - 1;

        // Start one group before the left edge and continue past the right
        // edge. The edge fade makes every group softly appear and disappear.
        for (var groupX = -stride + shift; groupX < Bounds.Width + groupWidth; groupX += stride)
        {
            var fadeDistance = Math.Max(groupWidth * 1.4, Bounds.Width * 0.12);
            var enterFade = Math.Clamp((groupX + groupWidth) / fadeDistance, 0, 1);
            var exitFade = Math.Clamp((Bounds.Width - groupX) / fadeDistance, 0, 1);
            var alpha = (byte)Math.Clamp(ArrowColor.A * 0.88 * Math.Min(enterFade, exitFade), 0, 255);
            var color = new Color(alpha, ArrowColor.R, ArrowColor.G, ArrowColor.B);
            var pen = new Pen(new SolidColorBrush(color), ArrowThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            for (var arrow = 0; arrow < 2; arrow++)
            {
                var tipX = groupX + arrow * innerStride;
                context.DrawLine(pen, new Point(tipX - arrowWidth, top), new Point(tipX, centerY));
                context.DrawLine(pen, new Point(tipX, centerY), new Point(tipX - arrowWidth, bottom));
            }
        }
    }
}
