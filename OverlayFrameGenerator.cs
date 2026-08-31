using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClassIslandInjector;

/// <summary>
/// 文本/形状覆盖层帧生成器：把 Kind=Text/Shape 的片段渲染成 BGRA 帧（透明背景 + 预乘 alpha）。
/// 供编辑器舞台预览、<see cref="VideoProjectPlayer"/> 与 <see cref="VideoProjectRenderer"/> 共用
/// （System.Drawing，纯计算，可在后台线程调用）。输出为预乘 alpha：Avalonia 的 Bgra8888 Premul
/// 位图直接写入即可；渲染器按预乘合成。
/// </summary>
internal static class OverlayFrameGenerator
{
    /// <summary>解析 #AARRGGBB（缺 alpha 时按 255）。失败回退白色。</summary>
    public static Color ParseColor(string text)
    {
        try
        {
            var s = text.Trim();
            if (s.StartsWith('#'))
            {
                s = s[1..];
            }

            if (s.Length < 6)
            {
                return Color.White;
            }

            var a = s.Length >= 8 ? Convert.ToByte(s[..2], 16) : (byte)255;
            var r = Convert.ToByte(s.Substring(2, 2), 16);
            var g = Convert.ToByte(s.Substring(4, 2), 16);
            var b = Convert.ToByte(s.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return Color.White;
        }
    }

    /// <summary>图片覆盖层位图缓存（System.Drawing 解码一次复用；后台线程访问，全局锁保护）。</summary>
    private static readonly Dictionary<string, Bitmap> ImageCache = new();
    private static readonly object ImageCacheLock = new();

    /// <summary>取图片覆盖层的解码位图（缓存上限 12，超出清空重建——覆盖层图片数量有限）。</summary>
    private static Bitmap? GetCachedImage(string path)
    {
        lock (ImageCacheLock)
        {
            if (ImageCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                // File.ReadAllBytes 避免 Bitmap 锁定源文件（文件可能被外部替换/删除）。
                var bmp = new Bitmap(new MemoryStream(File.ReadAllBytes(path)));
                if (ImageCache.Count >= 12)
                {
                    foreach (var old in ImageCache.Values)
                    {
                        old.Dispose();
                    }

                    ImageCache.Clear();
                }

                ImageCache[path] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>生成覆盖层帧（BGRA + 预乘 alpha）。Text/Shape/Image 返回帧，其它返回 null。</summary>
    public static VideoFrame? Render(VideoClip clip, int w, int h)
    {
        if (clip.Kind is not ("Text" or "Shape" or "Image"))
        {
            return null;
        }

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        // 图片覆盖层：图片按原比例居中绘制到整帧（周围透明），与视频素材「原比例居中」
        // 的变换基准一致；图片比例与舞台相同时自然铺满（底图图层编辑器导入即此场景）。
        if (clip.Kind == "Image")
        {
            var image = GetCachedImage(clip.SourcePath);
            if (image == null)
            {
                return null;
            }

            var imgPixels = new byte[w * h * 4];
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    var scale = Math.Min(w / (double)image.Width, h / (double)image.Height);
                    var dw = Math.Max(1, (int)Math.Round(image.Width * scale));
                    var dh = Math.Max(1, (int)Math.Round(image.Height * scale));
                    g.DrawImage(image, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh));
                }

                var imgData = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(imgData.Scan0, y * imgData.Stride), imgPixels, y * w * 4, w * 4);
                    }
                }
                finally
                {
                    bmp.UnlockBits(imgData);
                }
            }

            Premultiply(imgPixels);
            return new VideoFrame(imgPixels, w, h);
        }

        using var frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var color = ParseColor(clip.Color);
            if (clip.Kind == "Text")
            {
                var fontSize = Math.Max(8f, h * 0.32f);
                using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(color);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(clip.Text, font, brush, new RectangleF(0, 0, w, h), format);
            }
            else
            {
                using var brush = new SolidBrush(color);
                var rect = new Rectangle(0, 0, Math.Max(1, w - 1), Math.Max(1, h - 1));
                using var path = BuildShapePath(clip.Shape, rect);
                g.FillPath(brush, path);
                // 形状描边：宽度相对输出高（0 = 无描边）。
                if (clip.StrokeWidth > 0)
                {
                    var penWidth = (float)Math.Max(1, clip.StrokeWidth * h);
                    using var pen = new Pen(ParseColor(clip.StrokeColor), penWidth);
                    g.DrawPath(pen, path);
                }
            }
        }

        var pixels = new byte[w * h * 4];
        var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Format32bppArgb 内存布局即 B,G,R,A（小端 ARGB），与 BGRA 一致。
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * w * 4, w * 4);
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        Premultiply(pixels);
        return new VideoFrame(pixels, w, h);
    }

    /// <summary>预乘 alpha（Avalonia 位图为 Premul；渲染器按预乘 over 合成）。</summary>
    private static void Premultiply(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 255)
            {
                continue;
            }

            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
            else
            {
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }
        }
    }

    /// <summary>构建形状路径（矩形/椭圆/三角形/菱形/五角星/心形/箭头/五边形/圆环/十字）。</summary>
    private static GraphicsPath BuildShapePath(string shape, Rectangle rect)
    {
        var path = new GraphicsPath();
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var w = rect.Width;
        var h = rect.Height;
        switch (shape)
        {
            case "Ellipse":
                path.AddEllipse(rect);
                break;
            case "Triangle":
                path.AddPolygon(new[]
                {
                    new PointF(cx, rect.Y),
                    new PointF(rect.Right, rect.Bottom),
                    new PointF(rect.X, rect.Bottom)
                });
                break;
            case "Diamond":
                path.AddPolygon(new[]
                {
                    new PointF(cx, rect.Y),
                    new PointF(rect.Right, cy),
                    new PointF(cx, rect.Bottom),
                    new PointF(rect.X, cy)
                });
                break;
            case "Star":
                path.AddPolygon(StarPoints(new PointF(cx, cy), w * 0.5f, w * 0.21f, 5));
                break;
            case "Heart":
                BuildHeart(path, rect);
                break;
            case "ArrowRight":
                path.AddPolygon(new[]
                {
                    new PointF(rect.X, rect.Y),
                    new PointF(rect.X + w * 0.65f, rect.Y),
                    new PointF(rect.X + w * 0.65f, rect.Y - h * 0.18f),
                    new PointF(rect.Right, cy),
                    new PointF(rect.X + w * 0.65f, rect.Bottom + h * 0.18f),
                    new PointF(rect.X + w * 0.65f, rect.Bottom),
                    new PointF(rect.X, rect.Bottom)
                });
                break;
            case "Pentagon":
                path.AddPolygon(RegularPolygon(new PointF(cx, cy), w * 0.5f, 5));
                break;
            case "Ring":
                path.FillMode = FillMode.Alternate;
                path.AddEllipse(rect);
                path.AddEllipse(rect.X + w * 0.18f, rect.Y + h * 0.18f, w * 0.64f, h * 0.64f);
                break;
            case "Cross":
                path.AddPolygon(new[]
                {
                    new PointF(rect.X + w * 0.36f, rect.Y),
                    new PointF(rect.X + w * 0.64f, rect.Y),
                    new PointF(rect.X + w * 0.64f, rect.Y + h * 0.36f),
                    new PointF(rect.Right, rect.Y + h * 0.36f),
                    new PointF(rect.Right, rect.Y + h * 0.64f),
                    new PointF(rect.X + w * 0.64f, rect.Y + h * 0.64f),
                    new PointF(rect.X + w * 0.64f, rect.Bottom),
                    new PointF(rect.X + w * 0.36f, rect.Bottom),
                    new PointF(rect.X + w * 0.36f, rect.Y + h * 0.64f),
                    new PointF(rect.X, rect.Y + h * 0.64f),
                    new PointF(rect.X, rect.Y + h * 0.36f),
                    new PointF(rect.X + w * 0.36f, rect.Y + h * 0.36f)
                });
                break;
            default: // Rect
                path.AddRectangle(rect);
                break;
        }

        return path;
    }

    /// <summary>生成多角星顶点（points=角数，内外半径交替）。</summary>
    private static PointF[] StarPoints(PointF center, float outerR, float innerR, int points)
    {
        var pts = new PointF[points * 2];
        for (var i = 0; i < points * 2; i++)
        {
            var r = i % 2 == 0 ? outerR : innerR;
            var angle = -90f + i * 180f / points;
            pts[i] = new PointF(
                center.X + r * MathF.Cos(angle * MathF.PI / 180f),
                center.Y + r * MathF.Sin(angle * MathF.PI / 180f));
        }

        return pts;
    }

    /// <summary>正多边形顶点。</summary>
    private static PointF[] RegularPolygon(PointF center, float radius, int sides)
    {
        var pts = new PointF[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = -90f + i * 360f / sides;
            pts[i] = new PointF(
                center.X + radius * MathF.Cos(angle * MathF.PI / 180f),
                center.Y + radius * MathF.Sin(angle * MathF.PI / 180f));
        }

        return pts;
    }

    /// <summary>心形：两个圆 + 三角（Winding 填充并集）。</summary>
    private static void BuildHeart(GraphicsPath path, Rectangle rect)
    {
        var w = rect.Width;
        var h = rect.Height;
        var r = w * 0.25f;
        var cy = rect.Y + h * 0.26f;
        path.FillMode = FillMode.Winding;
        path.AddEllipse(rect.X, cy - r, 2 * r, 2 * r);
        path.AddEllipse(rect.Right - 2 * r, cy - r, 2 * r, 2 * r);
        path.AddPolygon(new[]
        {
            new PointF(rect.X, cy + r * 0.75f),
            new PointF(rect.Right, cy + r * 0.75f),
            new PointF(rect.X + w / 2f, rect.Bottom)
        });
    }
}
