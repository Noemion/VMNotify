using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string directory = args.Length == 1 ? args[0] : "windows/VMNotify/Assets";
        Directory.CreateDirectory(directory);
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes) {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
                var blue = new LinearGradientBrush(Color.FromRgb(20, 125, 225), Color.FromRgb(6, 87, 171), new Point(0, 0), new Point(1, 1));
                dc.DrawRoundedRectangle(blue, null, new Rect(8, 8, 240, 240), 52, 52);
                var white = new Pen(Brushes.White, 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawRoundedRectangle(null, white, new Rect(48, 66, 152, 108), 12, 12);
                dc.DrawLine(white, new Point(124, 178), new Point(124, 202));
                dc.DrawLine(white, new Point(94, 204), new Point(154, 204));
                dc.DrawLine(new Pen(Brushes.White, 7), new Point(54, 148), new Point(194, 148));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(84, 211, 255)), new Pen(new SolidColorBrush(Color.FromRgb(14, 105, 194)), 9), new Point(201, 61), 26, 26);
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); frames.Add(stream.ToArray());
        }
        File.WriteAllBytes(Path.Combine(directory, "VMNotify.png"), frames[^1]);
        using var writer = new BinaryWriter(File.Create(Path.Combine(directory, "VMNotify.ico")));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        uint offset = (uint)(6 + 16 * sizes.Length);
        for (int i = 0; i < sizes.Length; i++) {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)frames[i].Length); writer.Write(offset); offset += (uint)frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
    }
}
