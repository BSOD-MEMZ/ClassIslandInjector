namespace ClassIslandInjector;

/// <summary>
/// 滤镜逐像素变换（BGRA，直通色，alpha 通道保持）。供渲染器（VideoProjectRenderer）、
/// 编辑器舞台预览（VideoEditorWindow）与滤镜卡片预览共用。
/// 支持：Grayscale 灰度 / Sepia 棕褐 / Invert 反色 / Brighten 变亮 / Darken 变暗 / Mosaic 马赛克。
/// </summary>
internal static class FilterUtils
{
    /// <summary>对 BGRA 帧就地应用滤镜（alpha 通道保持）。intensity 0..1（0 = 无效果，1 = 完全应用）。</summary>
    public static void ApplyInPlace(byte[] p, int w, int h, string filter, double intensity = 1)
    {
        var t = Math.Clamp(intensity, 0, 1);
        if (t <= 0.001)
        {
            return;
        }

        switch (filter)
        {
            case "Grayscale":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    var g = (byte)((p[i + 2] * 299 + p[i + 1] * 587 + p[i] * 114) / 1000);
                    p[i] = (byte)(p[i] + (g - p[i]) * t);
                    p[i + 1] = (byte)(p[i + 1] + (g - p[i + 1]) * t);
                    p[i + 2] = (byte)(p[i + 2] + (g - p[i + 2]) * t);
                }

                break;
            case "Sepia":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    var r = p[i + 2];
                    var g = p[i + 1];
                    var b = p[i];
                    var sr = (byte)Math.Clamp((int)(r * 0.393 + g * 0.769 + b * 0.189), 0, 255);
                    var sg = (byte)Math.Clamp((int)(r * 0.349 + g * 0.686 + b * 0.168), 0, 255);
                    var sb = (byte)Math.Clamp((int)(r * 0.272 + g * 0.534 + b * 0.131), 0, 255);
                    p[i + 2] = (byte)(r + (sr - r) * t);
                    p[i + 1] = (byte)(g + (sg - g) * t);
                    p[i] = (byte)(b + (sb - b) * t);
                }

                break;
            case "Invert":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)(p[i] + ((255 - p[i]) - p[i]) * t);
                    p[i + 1] = (byte)(p[i + 1] + ((255 - p[i + 1]) - p[i + 1]) * t);
                    p[i + 2] = (byte)(p[i + 2] + ((255 - p[i + 2]) - p[i + 2]) * t);
                }

                break;
            case "Brighten":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)Math.Min(255, p[i] + 60 * t);
                    p[i + 1] = (byte)Math.Min(255, p[i + 1] + 60 * t);
                    p[i + 2] = (byte)Math.Min(255, p[i + 2] + 60 * t);
                }

                break;
            case "Darken":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)(p[i] * (1 - 0.45 * t));
                    p[i + 1] = (byte)(p[i + 1] * (1 - 0.45 * t));
                    p[i + 2] = (byte)(p[i + 2] * (1 - 0.45 * t));
                }

                break;
            case "Mosaic":
                ApplyMosaic(p, w, h, 8, t);
                break;
        }
    }

    /// <summary>马赛克：分块取平均色（块内所有像素用块平均色），按强度与原图混合。</summary>
    private static void ApplyMosaic(byte[] p, int w, int h, int block, double t)
    {
        if (w <= 0 || h <= 0 || block <= 0)
        {
            return;
        }

        for (var by = 0; by < h; by += block)
        {
            for (var bx = 0; bx < w; bx += block)
            {
                long sr = 0;
                long sg = 0;
                long sb = 0;
                var count = 0;
                var x2 = Math.Min(w, bx + block);
                var y2 = Math.Min(h, by + block);
                for (var y = by; y < y2; y++)
                {
                    for (var x = bx; x < x2; x++)
                    {
                        var i = (y * w + x) * 4;
                        sb += p[i];
                        sg += p[i + 1];
                        sr += p[i + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var rb = (byte)(sb / count);
                var rg = (byte)(sg / count);
                var rr = (byte)(sr / count);
                for (var y = by; y < y2; y++)
                {
                    for (var x = bx; x < x2; x++)
                    {
                        var i = (y * w + x) * 4;
                        p[i] = (byte)(p[i] + (rb - p[i]) * t);
                        p[i + 1] = (byte)(p[i + 1] + (rg - p[i + 1]) * t);
                        p[i + 2] = (byte)(p[i + 2] + (rr - p[i + 2]) * t);
                    }
                }
            }
        }
    }
}
