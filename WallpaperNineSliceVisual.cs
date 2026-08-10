using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClassIslandInjector;

/// <summary>
/// 九宫格切图渲染控件（编辑器画布预览与运行时全屏底图宿主共用）：
/// 把一张位图按「上 / 下 / 左 / 右」四条切边分成 9 块，四角保持原尺寸不变形、
/// 四边沿单轴拉伸、中间双轴拉伸，铺满目标矩形；关闭切图时直接整体拉伸铺满。
/// </summary>
public sealed class WallpaperNineSliceVisual : Control
{
    private Bitmap? _bitmap;

    public WallpaperNineSliceVisual()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    /// <summary>要切图渲染的位图。</summary>
    public Bitmap? Bitmap
    {
        get => _bitmap;
        set
        {
            _bitmap = value;
            InvalidateVisual();
        }
    }

    /// <summary>是否启用九宫格切图；关闭时整体拉伸。</summary>
    public bool SliceEnabled { get; set; }

    public double SliceTop { get; set; }

    public double SliceBottom { get; set; }

    public double SliceLeft { get; set; }

    public double SliceRight { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bitmap = _bitmap;
        if (bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var bw = bitmap.PixelSize.Width;
        var bh = bitmap.PixelSize.Height;
        if (bw <= 0 || bh <= 0)
        {
            return;
        }

        var target = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (!SliceEnabled)
        {
            context.DrawImage(bitmap, new Rect(0, 0, bw, bh), target);
            return;
        }

        // 切边夹到合理范围（各边不小于 0，且不超过图片半宽/半高，避免交叉）。
        var sl = Math.Clamp(SliceLeft, 0, Math.Max(0, bw / 2.0 - 1));
        var st = Math.Clamp(SliceTop, 0, Math.Max(0, bh / 2.0 - 1));
        var sr = Math.Clamp(SliceRight, 0, Math.Max(0, bw / 2.0 - 1));
        var sb = Math.Clamp(SliceBottom, 0, Math.Max(0, bh / 2.0 - 1));

        DrawNineSlice(context, bitmap, bw, bh, sl, st, sr, sb, target);
    }

    /// <summary>按九宫格把源图 9 块绘制到目标矩形（角 = 源切边尺寸，边 = 单轴拉伸，中间 = 双轴拉伸）。</summary>
    private static void DrawNineSlice(DrawingContext context, Bitmap bitmap,
        double bw, double bh, double sl, double st, double sr, double sb, Rect target)
    {
        var w = target.Width;
        var h = target.Height;
        var midW = Math.Max(1, w - sl - sr);
        var midH = Math.Max(1, h - st - sb);
        var smidW = Math.Max(1, bw - sl - sr);
        var smidH = Math.Max(1, bh - st - sb);

        // 角（4）
        Draw(context, bitmap, 0, 0, sl, st, target.X, target.Y, sl, st);
        Draw(context, bitmap, bw - sr, 0, sr, st, target.X + w - sr, target.Y, sr, st);
        Draw(context, bitmap, 0, bh - sb, sl, sb, target.X, target.Y + h - sb, sl, sb);
        Draw(context, bitmap, bw - sr, bh - sb, sr, sb, target.X + w - sr, target.Y + h - sb, sr, sb);
        // 边（4）
        Draw(context, bitmap, sl, 0, smidW, st, target.X + sl, target.Y, midW, st);
        Draw(context, bitmap, sl, bh - sb, smidW, sb, target.X + sl, target.Y + h - sb, midW, sb);
        Draw(context, bitmap, 0, st, sl, smidH, target.X, target.Y + st, sl, midH);
        Draw(context, bitmap, bw - sr, st, sr, smidH, target.X + w - sr, target.Y + st, sr, midH);
        // 中间（1）
        Draw(context, bitmap, sl, st, smidW, smidH, target.X + sl, target.Y + st, midW, midH);
    }

    private static void Draw(DrawingContext context, Bitmap bitmap,
        double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0)
        {
            return;
        }

        context.DrawImage(bitmap, new Rect(sx, sy, sw, sh), new Rect(dx, dy, dw, dh));
    }
}
