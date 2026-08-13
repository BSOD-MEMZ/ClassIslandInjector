using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace ClassIslandInjector;

/// <summary>
/// 底图图层「效果」构建工具：把图层的模糊 / 投影设置翻译为 Avalonia 的 Effect，
/// 以及把裁剪 / 色相 / 饱和度 / 明度 / 亮度 / 对比度设置逐像素应用到位图。
/// 编辑器画布预览与运行时注入器共用，保证所见即所得。
/// 注意：Avalonia 11.3 的 Skia 渲染器（Effect 属性）只支持 BlurEffect（高斯模糊）与
/// DropShadowEffect（投影）两种内建效果；裁剪与色相 / 饱和度 / 明度 / 亮度 / 对比度
/// 需要逐像素重处理位图。
/// </summary>
public static class WallpaperLayerEffects
{
    /// <summary>构建高斯模糊效果；未启用（半径 ≤ 0）时返回 null。</summary>
    public static BlurEffect? BuildBlur(WallpaperLayerItem layer)
    {
        if (layer.Kind != WallpaperLayerKind.Image || layer.BlurRadius <= 0)
        {
            return null;
        }

        return new BlurEffect { Radius = Math.Clamp(layer.BlurRadius, 0, 200) };
    }

    /// <summary>构建投影效果；未启用时返回 null。</summary>
    public static DropShadowEffect? BuildShadow(WallpaperLayerItem layer)
    {
        if (layer.Kind != WallpaperLayerKind.Image || !layer.ShadowEnabled)
        {
            return null;
        }

        return new DropShadowEffect
        {
            BlurRadius = Math.Clamp(layer.ShadowBlurRadius, 0, 100),
            OffsetX = Math.Clamp(layer.ShadowOffsetX, -200, 200),
            OffsetY = Math.Clamp(layer.ShadowOffsetY, -200, 200),
            Color = ParseColor(layer.ShadowColor, Color.FromArgb(0x99, 0, 0, 0)),
            Opacity = Math.Clamp(layer.ShadowOpacity, 0, 1)
        };
    }

    /// <summary>容错解析颜色；格式非法时回退到默认。</summary>
    private static Color ParseColor(string text, Color fallback) => ColorUtil.Parse(text, fallback);

    // ============ 裁剪 + 色相 / 饱和度 / 明度 / 亮度 / 对比度（逐像素）============

    /// <summary>图层是否启用了色相 / 饱和度 / 明度调整。</summary>
    public static bool HasHsl(WallpaperLayerItem layer) =>
        layer.Kind == WallpaperLayerKind.Image &&
        (Math.Abs(layer.HueShift) > 0.001 ||
         Math.Abs(layer.SaturationAdjust) > 0.001 ||
         Math.Abs(layer.LightnessAdjust) > 0.001);

    /// <summary>图层是否启用了亮度 / 对比度调整。</summary>
    public static bool HasBrightnessContrast(WallpaperLayerItem layer) =>
        layer.Kind == WallpaperLayerKind.Image &&
        (Math.Abs(layer.Brightness) > 0.001 || Math.Abs(layer.Contrast) > 0.001);

    /// <summary>图层是否启用了逐像素颜色调整（HSL / 亮度 / 对比度）。</summary>
    public static bool HasAdjustment(WallpaperLayerItem layer) =>
        layer.Kind == WallpaperLayerKind.Image && (HasHsl(layer) || HasBrightnessContrast(layer));

    /// <summary>图层是否启用了裁剪（裁剪矩形非零且不是整图）。</summary>
    public static bool HasCrop(WallpaperLayerItem layer) =>
        layer.Kind == WallpaperLayerKind.Image &&
        (layer.CropWidth > 0.5 || layer.CropHeight > 0.5);

    /// <summary>
    /// 应用图层的全部逐像素处理（先裁剪，再颜色调整），返回处理后的新位图；
    /// 无任何处理时返回 null（调用方继续用原图）。
    /// </summary>
    public static Bitmap? Process(Bitmap source, WallpaperLayerItem layer)
    {
        if (layer.Kind != WallpaperLayerKind.Image)
        {
            return null;
        }

        var hasCrop = HasCrop(layer);
        var hasAdjust = HasAdjustment(layer);
        if (!hasCrop && !hasAdjust)
        {
            return null;
        }

        Bitmap? intermediate = null;
        var current = source;
        if (hasCrop)
        {
            intermediate = ApplyCrop(source, layer);
            if (intermediate != null)
            {
                current = intermediate;
            }
        }

        if (hasAdjust)
        {
            var processed = ApplyAdjustments(current, layer);
            if (processed != null)
            {
                intermediate?.Dispose();
                return processed;
            }
        }

        return intermediate;
    }

    /// <summary>
    /// 按图层的裁剪矩形截取源位图，返回新位图；裁剪无效 / 覆盖整图时返回 null。
    /// 用 GCHandle 钉住托管缓冲读回裁剪区域像素（全程安全代码，无需 unsafe）。
    /// </summary>
    public static Bitmap? ApplyCrop(Bitmap source, WallpaperLayerItem layer)
    {
        if (source.Format != PixelFormat.Bgra8888)
        {
            return null;
        }

        var bw = source.PixelSize.Width;
        var bh = source.PixelSize.Height;
        var x = Math.Clamp((int)Math.Round(layer.CropX), 0, Math.Max(0, bw - 1));
        var y = Math.Clamp((int)Math.Round(layer.CropY), 0, Math.Max(0, bh - 1));
        var w = (int)Math.Min(Math.Round(layer.CropWidth), bw - x);
        var h = (int)Math.Min(Math.Round(layer.CropHeight), bh - y);
        if (w <= 0 || h <= 0 || (x == 0 && y == 0 && w == bw && h == bh))
        {
            return null;
        }

        var stride = w * 4;
        var bytes = new byte[h * stride];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(x, y, w, h), handle.AddrOfPinnedObject(), bytes.Length, stride);
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(),
                new PixelSize(w, h), source.Dpi, stride);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 对位图逐像素应用色相 / 饱和度 / 明度 / 亮度 / 对比度调整（Photoshop 式），
    /// 返回处理后的新位图；无调整或位图格式不支持时返回 null（调用方继续用原图）。
    /// 用 GCHandle 钉住托管缓冲取地址读回源像素（全程安全代码，无需 unsafe）。
    /// </summary>
    public static Bitmap? ApplyAdjustments(Bitmap source, WallpaperLayerItem layer)
    {
        if (!HasAdjustment(layer))
        {
            return null;
        }

        var w = source.PixelSize.Width;
        var h = source.PixelSize.Height;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        // Windows/Skia 解码默认 Bgra8888；个别平台可能是 Rgba8888，两种都支持。
        var format = source.Format;
        var bgra = format == PixelFormat.Bgra8888;
        if (!bgra && format != PixelFormat.Rgba8888)
        {
            return null;
        }

        var stride = w * 4;
        var bytes = new byte[h * stride];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var hue = (layer.HueShift % 360) / 360.0;
        var sat = 1 + layer.SaturationAdjust / 100.0;
        var light = layer.LightnessAdjust / 100.0;
        AdjustPixels(bytes, stride, w, h, bgra, hue, sat, light,
            layer.Brightness, layer.Contrast,
            source.AlphaFormat == AlphaFormat.Premul);

        var output = new WriteableBitmap(new PixelSize(w, h), source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var ofb = output.Lock())
        {
            var outStride = ofb.RowBytes;
            if (outStride == stride)
            {
                Marshal.Copy(bytes, 0, ofb.Address, bytes.Length);
            }
            else
            {
                var row = w * 4;
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(bytes, y * stride, ofb.Address + y * outStride, row);
                }
            }
        }

        return output;
    }

    /// <summary>就地逐像素调整（Bgra8888 / Rgba8888，正确处理预乘 alpha）。
    /// <paramref name="mask"/> 非空时只处理掩码内（255）像素，用于选区内的图像变换。</summary>
    public static void AdjustPixels(byte[] bytes, int stride, int w, int h, bool bgra,
        double hue, double sat, double light, double brightness, double contrast, bool premul,
        byte[]? mask = null)
    {
        var rowPixels = w * 4;
        var needLight = Math.Abs(light) > 0.001;
        var needBc = Math.Abs(brightness) > 0.001 || Math.Abs(contrast) > 0.001;
        var contrastFactor = contrast != 0
            ? (259.0 * (contrast + 255.0)) / (255.0 * (259.0 - contrast))
            : 1.0;
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            for (var x = 0; x < rowPixels; x += 4)
            {
                if (mask != null && mask[y * w + (x >> 2)] == 0)
                {
                    continue;
                }

                var i = row + x;
                var a = bytes[i + 3];
                if (a == 0)
                {
                    // 全透明像素无需处理。
                    continue;
                }

                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                if (!bgra)
                {
                    // Rgba8888：字节序为 R,G,B,A。
                    (r, b) = (b, r);
                }

                // 预乘 alpha 先反预乘，避免颜色失真。
                if (premul && a != 255)
                {
                    r = (byte)(r * 255 / a);
                    g = (byte)(g * 255 / a);
                    b = (byte)(b * 255 / a);
                }

                // 先做亮度 / 对比度，再做色相 / 饱和度 / 明度。
                if (needBc)
                {
                    r = ApplyBc(r, brightness, contrastFactor);
                    g = ApplyBc(g, brightness, contrastFactor);
                    b = ApplyBc(b, brightness, contrastFactor);
                }

                if (r != g || g != b)
                {
                    // 彩色像素：完整 RGB -> HSL -> 调整 -> RGB。
                    double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
                    var max = Math.Max(rn, Math.Max(gn, bn));
                    var min = Math.Min(rn, Math.Min(gn, bn));
                    var l = (max + min) / 2.0;
                    double hh, s;
                    if (max == min)
                    {
                        hh = 0;
                        s = 0;
                    }
                    else
                    {
                        var d = max - min;
                        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                        hh = max == rn
                            ? (gn - bn) / d + (gn < bn ? 6 : 0)
                            : max == gn
                                ? (bn - rn) / d + 2
                                : (rn - gn) / d + 4;
                        hh /= 6;
                    }

                    hh = (hh + hue) % 1.0;
                    if (hh < 0)
                    {
                        hh += 1;
                    }

                    s = Math.Clamp(s * sat, 0, 1);
                    l = Math.Clamp(l + light, 0, 1);
                    var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                    var p = 2 * l - q;
                    r = (byte)(HueToRgb(p, q, hh + 1.0 / 3.0) * 255);
                    g = (byte)(HueToRgb(p, q, hh) * 255);
                    b = (byte)(HueToRgb(p, q, hh - 1.0 / 3.0) * 255);
                }
                else if (needLight)
                {
                    // 灰色像素：仅受明度影响。
                    var v = (byte)(Math.Clamp(r / 255.0 + light, 0, 1) * 255);
                    r = g = b = v;
                }

                if (premul && a != 255)
                {
                    r = (byte)(r * a / 255);
                    g = (byte)(g * a / 255);
                    b = (byte)(b * a / 255);
                }

                if (!bgra)
                {
                    (r, b) = (b, r);
                }

                bytes[i] = b;
                bytes[i + 1] = g;
                bytes[i + 2] = r;
                bytes[i + 3] = a;
            }
        }
    }

    /// <summary>亮度 / 对比度单通道换算（0-255）。</summary>
    private static byte ApplyBc(byte value, double brightness, double contrastFactor)
    {
        var v = value + brightness * 2.55;
        v = (v - 128) * contrastFactor + 128;
        return (byte)Math.Clamp(v, 0, 255);
    }

    /// <summary>
    /// 对掩码内（255）的像素做三趟盒式模糊（近似高斯模糊），掩码外像素不动。
    /// 模糊核采样包含掩码外的相邻像素（与 Photoshop 选区模糊行为一致）。
    /// 输入为直通 alpha 的 Bgra8888 缓冲。
    /// </summary>
    public static void BlurPixelsMasked(byte[] bytes, int stride, int w, int h, double radius, byte[] mask)
    {
        if (radius < 1 || w <= 0 || h <= 0 || bytes.Length < h * stride || mask.Length < w * h)
        {
            return;
        }

        var r = (int)Math.Ceiling(radius);
        var a = new byte[bytes.Length];
        var b = new byte[bytes.Length];
        Array.Copy(bytes, a, bytes.Length);
        BoxBlurPass(a, b, stride, w, h, r, true);
        BoxBlurPass(b, a, stride, w, h, r, false);
        BoxBlurPass(a, b, stride, w, h, r, true);
        // 只把模糊结果写回掩码内像素。
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                if (mask[row + x] == 0)
                {
                    continue;
                }

                var src = (row + x) * 4;
                bytes[src] = b[src];
                bytes[src + 1] = b[src + 1];
                bytes[src + 2] = b[src + 2];
                bytes[src + 3] = b[src + 3];
            }
        }
    }

    /// <summary>单趟盒式模糊（RGBA 逐通道滑动窗口均值；边界按最近像素补齐）。</summary>
    private static void BoxBlurPass(byte[] src, byte[] dst, int stride, int w, int h, int r, bool horizontal)
    {
        var window = r * 2 + 1;
        if (horizontal)
        {
            for (var y = 0; y < h; y++)
            {
                var row = y * stride;
                for (var c = 0; c < 4; c++)
                {
                    long sum = 0;
                    for (var k = -r; k <= r; k++)
                    {
                        sum += src[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                    }

                    for (var x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / window);
                        sum += src[row + Math.Clamp(x + r + 1, 0, w - 1) * 4 + c]
                             - src[row + Math.Clamp(x - r, 0, w - 1) * 4 + c];
                    }
                }
            }
        }
        else
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    long sum = 0;
                    for (var k = -r; k <= r; k++)
                    {
                        sum += src[Math.Clamp(k, 0, h - 1) * stride + x * 4 + c];
                    }

                    for (var y = 0; y < h; y++)
                    {
                        dst[y * stride + x * 4 + c] = (byte)(sum / window);
                        sum += src[Math.Clamp(y + r + 1, 0, h - 1) * stride + x * 4 + c]
                             - src[Math.Clamp(y - r, 0, h - 1) * stride + x * 4 + c];
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在直通 alpha 的 Bgra8888 缓冲中绘制一条圆头线段：
    /// 画笔按源色 alpha 混合到现有像素；橡皮擦把覆盖区域清为全透明。
    /// 沿线采样多个圆盘保证连续平滑（零长度 = 画一个点）。
    /// </summary>
    public static void DrawStroke(byte[] bytes, int stride, int w, int h,
        double x0, double y0, double x1, double y1, double radius, Color color, bool erase)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1, radius)));
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (double)steps;
            DrawDisc(bytes, stride, w, h, x0 + dx * t, y0 + dy * t, radius, color, erase);
        }
    }

    /// <summary>
    /// 在一个圆盘区域内落笔（画笔 alpha 混合 / 橡皮清空），边缘 1px 抗锯齿软过渡。
    /// 以像素中心到圆心的距离计算覆盖度：半径内覆盖度 1，向外 1px 线性降到 0，
    /// 消除原来的锯齿硬边（这也是「矢量对象看着不抗锯齿」的主要来源——笔刷边缘）。
    /// </summary>
    private static void DrawDisc(byte[] bytes, int stride, int w, int h,
        double cx, double cy, double radius, Color color, bool erase)
    {
        // 外扩 1px 覆盖抗锯齿过渡带。
        var x0 = Math.Clamp((int)Math.Floor(cx - radius - 1), 0, w - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(cx + radius + 1), 0, w - 1);
        var y0 = Math.Clamp((int)Math.Floor(cy - radius - 1), 0, h - 1);
        var y1 = Math.Clamp((int)Math.Ceiling(cy + radius + 1), 0, h - 1);
        for (var y = y0; y <= y1; y++)
        {
            var row = y * stride;
            for (var x = x0; x <= x1; x++)
            {
                var ddx = x - cx;
                var ddy = y - cy;
                var d = Math.Sqrt(ddx * ddx + ddy * ddy);
                var coverage = Math.Clamp(radius + 0.5 - d, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }

                var i = row + x * 4;
                if (erase)
                {
                    // 橡皮：按覆盖度把现有 alpha 降到 0（直通 alpha 空间，RGB 不变，保存时再预乘）。
                    var a = bytes[i + 3];
                    bytes[i + 3] = (byte)(a * (1 - coverage));
                    continue;
                }

                var da = bytes[i + 3] / 255.0;
                var sa = color.A / 255.0 * coverage;
                var outA = sa + da * (1 - sa);
                if (outA <= 0)
                {
                    bytes[i] = 0;
                    bytes[i + 1] = 0;
                    bytes[i + 2] = 0;
                    bytes[i + 3] = 0;
                    continue;
                }

                var dr = bytes[i + 2] / 255.0;
                var dg = bytes[i + 1] / 255.0;
                var db = bytes[i] / 255.0;
                var sr = color.R / 255.0;
                var sg = color.G / 255.0;
                var sb = color.B / 255.0;
                bytes[i] = (byte)Math.Clamp((sb * sa + db * da * (1 - sa)) / outA * 255, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp((sg * sa + dg * da * (1 - sa)) / outA * 255, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp((sr * sa + dr * da * (1 - sa)) / outA * 255, 0, 255);
                bytes[i + 3] = (byte)Math.Clamp(outA * 255, 0, 255);
            }
        }
    }

    /// <summary>把预乘 alpha 缓冲转为直通 alpha（用于画笔工作缓冲）。</summary>
    public static void Unpremultiply(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            if (a == 0 || a == 255)
            {
                continue;
            }

            bytes[i] = (byte)(bytes[i] * 255 / a);
            bytes[i + 1] = (byte)(bytes[i + 1] * 255 / a);
            bytes[i + 2] = (byte)(bytes[i + 2] * 255 / a);
        }
    }

    /// <summary>HSL -> RGB 色相分量换算。</summary>
    private static double HueToRgb(double p, double q, double t)    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6.0)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1.0 / 2.0)
        {
            return q;
        }

        if (t < 2.0 / 3.0)
        {
            return p + (q - p) * (2.0 / 3.0 - t) * 6;
        }

        return p;
    }
}
