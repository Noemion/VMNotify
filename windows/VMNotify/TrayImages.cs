using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VMNotify;

internal static class TrayImages
{
    public static BitmapSource? Create(TrayImage? image)
    {
        if (image == null) return null;
        var pixels = image.BgraPixels();
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, image.Width * 4);
        source.Freeze();
        return source;
    }
    public static SolidColorBrush? Backdrop(TrayImage? image)
    {
        if (image == null) return null;
        var pixels = image.BgraPixels();
        long light = 0, alpha = 0;
        for (int i = 0; i < pixels.Length; i += 4) {
            light += (pixels[i] + pixels[i + 1] + pixels[i + 2]) * pixels[i + 3];
            alpha += pixels[i + 3];
        }
        // Change only the neutral backdrop, never the source icon's RGB or opacity.
        byte shade = alpha > 0 && light / (3 * alpha) < 128 ? (byte)224 : (byte)48;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(shade, shade, shade)); brush.Freeze(); return brush;
    }
}
