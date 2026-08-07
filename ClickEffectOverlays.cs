using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 点击特效 · 软边扩散圆环：从点击位置向外扩散并淡出的柔边圆环。
/// 插件自绘实现（不复用提醒 Ripple 的渲染），由注入器 16ms 时钟统一推进，播完自动移除。
/// </summary>
internal sealed class ClickRingOverlay : Control, IRippleEffect
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly TimeSpan _duration;
    private readonly Point _center;
    private readonly Color _color;
    private readonly double _maxRadius;

    public ClickRingOverlay(Point center, Color color, TimeSpan duration, double maxRadius)
    {
        _center = center;
        _color = color;
        _duration = duration;
        _maxRadius = Math.Max(1, maxRadius);
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public bool IsCompleted => DateTime.UtcNow - _startedAt >= _duration;

    public void Advance() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var p = Math.Clamp((DateTime.UtcNow - _startedAt).TotalSeconds / _duration.TotalSeconds, 0, 1);
        if (p >= 1)
        {
            return;
        }

        // 缓出扩散：前期快、后期慢；随进度淡出、变细。
        var eased = 1 - Math.Pow(1 - p, 3);
        var radius = Math.Max(1, eased * _maxRadius);
        var alpha = (byte)Math.Clamp(_color.A * (1 - eased) * 1.2, 0, 255);
        if (alpha <= 0)
        {
            return;
        }

        var brush = new SolidColorBrush(new Color(alpha, _color.R, _color.G, _color.B));
        var thickness = Math.Max(0.5, 3 * (1 - eased) * 2);
        context.DrawEllipse(null, new Pen(brush, thickness),
            new Rect(_center.X - radius, _center.Y - radius, radius * 2, radius * 2));
    }
}
