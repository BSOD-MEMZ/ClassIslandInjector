using System.Drawing;
using MaterialColorUtilities.Utils;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClassIslandInjector;

/// <summary>
/// Obtains the active SMTC album thumbnail and delegates palette extraction to
/// Google's Material color utilities .NET port rather than maintaining a custom
/// image quantizer in the plugin.
/// </summary>
internal static class SmtcAlbumColorPicker
{
    public static async Task<Avalonia.Media.Color?> TryGetAccentColorAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (session == null)
            {
                return null;
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            if (mediaProperties.Thumbnail == null)
            {
                return null;
            }

            using var randomAccessStream = await mediaProperties.Thumbnail.OpenReadAsync();
            using var reader = new DataReader(randomAccessStream);
            await reader.LoadAsync((uint)randomAccessStream.Size);
            var bytes = new byte[(int)randomAccessStream.Size];
            reader.ReadBytes(bytes);
            return ExtractAccentColor(bytes);
        }
        catch
        {
            // SMTC is optional: media apps may not expose a session or thumbnail.
            return null;
        }
    }

    private static Avalonia.Media.Color? ExtractAccentColor(byte[] bytes)
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
            return seed == 0
                ? null
                : Avalonia.Media.Color.FromArgb((byte)(seed >> 24), (byte)(seed >> 16), (byte)(seed >> 8), (byte)seed);
        }
        catch
        {
            return null;
        }
    }
}
