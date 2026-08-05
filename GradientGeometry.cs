using Avalonia;

namespace ClassIslandInjector;

/// <summary>
/// 渐变方向与 <see cref="LinearGradientBrush"/> 起点/终点的映射。
/// </summary>
internal static class GradientGeometry
{
    private static readonly RelativePoint TopLeft = new(0, 0, RelativeUnit.Relative);
    private static readonly RelativePoint Top = new(0.5, 0, RelativeUnit.Relative);
    private static readonly RelativePoint TopRight = new(1, 0, RelativeUnit.Relative);
    private static readonly RelativePoint Left = new(0, 0.5, RelativeUnit.Relative);
    private static readonly RelativePoint Right = new(1, 0.5, RelativeUnit.Relative);
    private static readonly RelativePoint BottomLeft = new(0, 1, RelativeUnit.Relative);
    private static readonly RelativePoint Bottom = new(0.5, 1, RelativeUnit.Relative);
    private static readonly RelativePoint BottomRight = new(1, 1, RelativeUnit.Relative);

    public static (RelativePoint Start, RelativePoint End) Points(GradientDirection direction) => direction switch
    {
        GradientDirection.TopToBottom => (Top, Bottom),
        GradientDirection.BottomToTop => (Bottom, Top),
        GradientDirection.LeftToRight => (Left, Right),
        GradientDirection.RightToLeft => (Right, Left),
        GradientDirection.TopLeftToBottomRight => (TopLeft, BottomRight),
        GradientDirection.BottomRightToTopLeft => (BottomRight, TopLeft),
        GradientDirection.TopRightToBottomLeft => (TopRight, BottomLeft),
        GradientDirection.BottomLeftToTopRight => (BottomLeft, TopRight),
        _ => (TopLeft, BottomRight)
    };
}
