using Avalonia;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 底图图层「锚点 + 偏移」相对定位的共享数学工具。
/// 运行时注入器（MainWindowStyleInjector）与图层编辑器（WallpaperLayerEditor）
/// 使用同一套公式计算图层矩形，保证所见即所得。
/// </summary>
public static class WallpaperLayerLayout
{
    /// <summary>岛屿坐标系内水平锚点参考位置（岛屿左/中/右）。</summary>
    public static double AnchorXReference(WallpaperLayerAnchorX anchor, double islandWidth) => anchor switch
    {
        WallpaperLayerAnchorX.Left => 0,
        WallpaperLayerAnchorX.Center => islandWidth / 2,
        WallpaperLayerAnchorX.Right => islandWidth,
        _ => 0
    };

    /// <summary>岛屿坐标系内垂直锚点参考位置（岛屿上/中/下）。</summary>
    public static double AnchorYReference(WallpaperLayerAnchorY anchor, double islandHeight) => anchor switch
    {
        WallpaperLayerAnchorY.Top => 0,
        WallpaperLayerAnchorY.Center => islandHeight / 2,
        WallpaperLayerAnchorY.Bottom => islandHeight,
        _ => 0
    };

    /// <summary>图片矩形内水平参考点（左/中/右）。</summary>
    public static double ImageXReference(WallpaperLayerAnchorX anchor, double width) => anchor switch
    {
        WallpaperLayerAnchorX.Left => 0,
        WallpaperLayerAnchorX.Center => width / 2,
        WallpaperLayerAnchorX.Right => width,
        _ => 0
    };

    /// <summary>图片矩形内垂直参考点（上/中/下）。</summary>
    public static double ImageYReference(WallpaperLayerAnchorY anchor, double height) => anchor switch
    {
        WallpaperLayerAnchorY.Top => 0,
        WallpaperLayerAnchorY.Center => height / 2,
        WallpaperLayerAnchorY.Bottom => height,
        _ => 0
    };

    /// <summary>
    /// 计算图层在岛屿坐标系内的矩形（原点为岛屿左上角）。
    /// <paramref name="aspect"/> 为图片宽高比（宽/高），用于只指定单边尺寸时推导另一边。
    /// </summary>
    public static Rect ComputeRect(WallpaperLayerItem layer, double islandWidth, double islandHeight, double? aspect)
    {
        if (layer.SizeMode == WallpaperLayerSizeMode.FillIsland)
        {
            return new Rect(0, 0, islandWidth, islandHeight);
        }

        var width = layer.Width > 0 ? layer.Width : islandWidth * 0.6;
        var height = layer.Height > 0 ? layer.Height : islandHeight * 0.6;
        if (aspect is > 0)
        {
            if (layer.Width > 0 && layer.Height <= 0)
            {
                height = width / aspect.Value;
            }
            else if (layer.Height > 0 && layer.Width <= 0)
            {
                width = height * aspect.Value;
            }
        }

        var x = AnchorXReference(layer.AnchorX, islandWidth) + layer.OffsetX - ImageXReference(layer.AnchorX, width);
        var y = AnchorYReference(layer.AnchorY, islandHeight) + layer.OffsetY - ImageYReference(layer.AnchorY, height);
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// 由矩形反推锚点偏移（保持当前锚点不变，把矩形位置表达为相对锚点的偏移）。
    /// </summary>
    public static (double OffsetX, double OffsetY) ToOffsets(WallpaperLayerItem layer, Rect rect, double islandWidth, double islandHeight)
    {
        var ox = rect.X - AnchorXReference(layer.AnchorX, islandWidth) + ImageXReference(layer.AnchorX, rect.Width);
        var oy = rect.Y - AnchorYReference(layer.AnchorY, islandHeight) + ImageYReference(layer.AnchorY, rect.Height);
        return (ox, oy);
    }

    /// <summary>图层显示模式的 Stretch 映射（Tile 以原始像素尺寸居中显示）。</summary>
    public static Stretch ToStretch(WallpaperDisplayMode mode) => mode switch
    {
        WallpaperDisplayMode.Stretch => Stretch.Fill,
        WallpaperDisplayMode.Fill => Stretch.UniformToFill,
        WallpaperDisplayMode.Fit => Stretch.Uniform,
        WallpaperDisplayMode.Tile => Stretch.None,
        _ => Stretch.UniformToFill
    };

    /// <summary>把矩形坐标转换到以岛屿左上角为原点的局部坐标。</summary>
    public static Rect InIslandSpace(Rect rect, double islandX, double islandY) =>
        new(rect.X - islandX, rect.Y - islandY, rect.Width, rect.Height);
}
