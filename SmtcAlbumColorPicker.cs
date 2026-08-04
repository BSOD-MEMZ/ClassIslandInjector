using System.Drawing;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Utils;

namespace ClassIslandInjector;

/// <summary>
/// 从专辑封面派生的一组 Material You 颜色：背景使用主导种子色，
/// 边框使用主色调（Primary tone 80），阴影使用深中性色（Neutral tone 10）。
/// </summary>
internal sealed record AlbumAccentColors(Avalonia.Media.Color Background, Avalonia.Media.Color Border, Avalonia.Media.Color Shadow);

/// <summary>
/// 纯函数式的专辑封面取色工具（不包含任何 WinRT 调用）。
/// WinRT 会话管理由事件驱动的 <see cref="SmtcWatcher"/> 负责。
/// </summary>
internal static class SmtcAlbumColorPicker
{
    private static string? _logPath;

    /// <summary>
    /// 设置诊断日志路径。置空则关闭日志。
    /// </summary>
    public static void SetLogPath(string? logPath) => _logPath = logPath;

    public static void LogDiagnostic(string message)
    {
        if (_logPath == null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响取色。
        }
    }

    public static AlbumAccentColors? ExtractAccentColors(byte[] bytes)
    {
        try
        {
            using var imageStream = new MemoryStream(bytes);
            using var source = new Bitmap(imageStream);
            var width = Math.Min(source.Width, 128);
            var height = Math.Min(source.Height, 128);
            using var bitmap = new Bitmap(source, new Size(width, height));
            var pixels = new uint[width * height];
            var index = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    pixels[index++] = ((uint)pixel.A << 24) | ((uint)pixel.R << 16) | ((uint)pixel.G << 8) | pixel.B;
                }
            }

            var seed = ImageUtils.ColorsFromImage(pixels).FirstOrDefault();
            if (seed == 0)
            {
                LogDiagnostic("调色板未提取到种子颜色");
                return null;
            }

            // Material You 派生：背景取主导种子色，边框取主色调 80，阴影取深中性色 10。
            var palette = CorePalette.Of(seed);
            var background = ToColor(seed);
            var border = ToColor(palette.Primary.Tone(80));
            var shadow = ToColor(palette.Neutral.Tone(10));
            LogDiagnostic($"提取调色板: 种子={seed:X8}, 背景={background}, 边框={border}, 阴影={shadow}");
            return new AlbumAccentColors(background, border, shadow);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"缩略图解析失败: {ex}");
            return null;
        }
    }

    private static Avalonia.Media.Color ToColor(uint argb) =>
        Avalonia.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
