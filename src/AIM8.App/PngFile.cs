using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIM8.App;

internal static class PngFile
{
    /// <summary>Writes RGBA pixels (the capture format) as a PNG.</summary>
    public static void Save(byte[] rgba, int width, int height, string path)
    {
        // WPF has no RGBA format; drop alpha into packed RGB.
        var rgb = new byte[width * height * 3];
        for (int i = 0, j = 0; i < width * height; i++, j += 3)
        {
            rgb[j] = rgba[i * 4];
            rgb[j + 1] = rgba[i * 4 + 1];
            rgb[j + 2] = rgba[i * 4 + 2];
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
