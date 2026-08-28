namespace ClassIslandInjector;

/// <summary>
/// 滤镜逐像素变换（BGRA，直通色，alpha 通道保持）。供渲染器（VideoProjectRenderer）、
/// 编辑器舞台预览（VideoEditorWindow）与滤镜卡片预览共用。
/// 支持：Grayscale 灰度 / Sepia 棕褐 / Invert 反色 / Brighten 变亮 / Darken 变暗 / Mosaic 马赛克。
/// </summary>
internal static class FilterUtils
{
    /// <summary>对 BGRA 帧就地应用滤镜（alpha 通道保持）。</summary>
    public static void ApplyInPlace(byte[] p, int w, int h, string filter)
    {
        switch (filter)
        {
            case "Grayscale":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    var g = (byte)((p[i + 2] * 299 + p[i + 1] * 587 + p[i] * 114) / 1000);
                    p[i] = g;
                    p[i + 1] = g;
                    p[i + 2] = g;
                }

                break;
            case "Sepia":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    var r = p[i + 2];
                    var g = p[i + 1];
                    var b = p[i];
                    p[i + 2] = (byte)Math.Clamp((int)(r * 0.393 + g * 0.769 + b * 0.189), 0, 255);
                    p[i + 1] = (byte)Math.Clamp((int)(r * 0.349 + g * 0.686 + b * 0.168), 0, 255);
                    p[i] = (byte)Math.Clamp((int)(r * 0.272 + g * 0.534 + b * 0.131), 0, 255);
                }

                break;
            case "Invert":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)(255 - p[i]);
                    p[i + 1] = (byte)(255 - p[i + 1]);
                    p[i + 2] = (byte)(255 - p[i + 2]);
                }

                break;
            case "Brighten":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)Math.Min(255, p[i] + 60);
                    p[i + 1] = (byte)Math.Min(255, p[i + 1] + 60);
                    p[i + 2] = (byte)Math.Min(255, p[i + 2] + 60);
                }

                break;
            case "Darken":
                for (var i = 0; i + 3 < p.Length; i += 4)
                {
                    p[i] = (byte)(p[i] * 0.55);
                    p[i + 1] = (byte)(p[i + 1] * 0.55);
                    p[i + 2] = (byte)(p[i + 2] * 0.55);
                }

                break;
            case "Mosaic":
                ApplyMosaic(p, w, h, 8);
                break;
        }
    }

    /// <summary>马赛克：分块取平均色（块内所有像素用块平均色）。</summary>
    private static void ApplyMosaic(byte[] p, int w, int h, int block)
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
                        p[i] = rb;
                        p[i + 1] = rg;
                        p[i + 2] = rr;
                    }
                }
            }
        }
    }
}
