using System.Drawing;
using System.Runtime.CompilerServices;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Utils;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClassIslandInjector;

/// <summary>
/// 从专辑封面派生的一组 Material You 颜色：背景使用主导种子色，
/// 边框使用主色调（Primary tone 80），阴影使用深中性色（Neutral tone 10）。
/// </summary>
internal sealed record AlbumAccentColors(Avalonia.Media.Color Background, Avalonia.Media.Color Border, Avalonia.Media.Color Shadow);

/// <summary>
/// Obtains the active SMTC album thumbnail and delegates palette extraction to
/// Google's Material color utilities .NET port rather than maintaining a custom
/// image quantizer in the plugin.
/// </summary>
internal static class SmtcAlbumColorPicker
{
    private static string? _logPath;

    /// <summary>
    /// 设置诊断日志路径。置空则关闭日志。
    /// </summary>
    public static void SetLogPath(string? logPath) => _logPath = logPath;

    private static void Log(string message)
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

    public static async Task<AlbumAccentColors?> TryGetAccentColorsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Log("非 Windows，跳过取色");
            return null;
        }

        try
        {
            var result = await TryGetAccentColorsCoreAsync();
            Log(result is { } c ? $"取色成功: 背景={c.Background}, 边框={c.Border}, 阴影={c.Shadow}" : "取色返回 null");
            return result;
        }
        catch (Exception ex)
        {
            // SMTC is optional: media apps may not expose a session or thumbnail.
            // 该 catch 同时兜底 WinRT 投影程序集（Microsoft.Windows.SDK.NET）加载失败的情况，
            // 例如宿主 ClassIsland 自带的 Windows SDK 投影版本与插件编译时不一致。
            Log($"取色异常: {ex}");
            return null;
        }
    }

    // 将 WinRT 相关调用隔离到独立方法中：若投影程序集无法加载，JIT 会在调用该方法时抛出异常，
    // 该异常可被上方 TryGetAccentColorsAsync 的 try/catch 捕获，而不会让 async void 回调崩溃。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AlbumAccentColors?> TryGetAccentColorsCoreAsync()
    {
        Log("RequestAsync() 开始");
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        Log("RequestAsync() 完成");

        var session = manager.GetCurrentSession();
        if (session == null)
        {
            Log("GetCurrentSession() 返回 null（没有活动的 SMTC 会话）");
            return null;
        }

        Log($"获取到 SMTC 会话: {session.SourceAppUserModelId}");
        var mediaProperties = await session.TryGetMediaPropertiesAsync();
        if (mediaProperties.Thumbnail == null)
        {
            Log("缩略图为 null（媒体未提供专辑封面）");
            return null;
        }

        using var randomAccessStream = await mediaProperties.Thumbnail.OpenReadAsync();
        Log($"缩略图流大小: {randomAccessStream.Size}");
        using var reader = new DataReader(randomAccessStream);
        await reader.LoadAsync((uint)randomAccessStream.Size);
        var bytes = new byte[(int)randomAccessStream.Size];
        reader.ReadBytes(bytes);
        Log($"已读取缩略图字节数: {bytes.Length}");
        return ExtractAccentColors(bytes);
    }

    private static AlbumAccentColors? ExtractAccentColors(byte[] bytes)
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
                Log("调色板未提取到种子颜色");
                return null;
            }

            // Material You 派生：背景取主导种子色，边框取主色调 80，
            // 阴影取深中性色 10，三者天然和谐。
            var palette = CorePalette.Of(seed);
            var background = ToColor(seed);
            var border = ToColor(palette.Primary.Tone(80));
            var shadow = ToColor(palette.Neutral.Tone(10));
            Log($"提取调色板: 种子={seed:X8}, 背景={background}, 边框={border}, 阴影={shadow}");
            return new AlbumAccentColors(background, border, shadow);
        }
        catch (Exception ex)
        {
            Log($"缩略图解析失败: {ex}");
            return null;
        }
    }

    private static Avalonia.Media.Color ToColor(uint argb) =>
        Avalonia.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
