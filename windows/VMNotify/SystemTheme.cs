using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VMNotify;

internal static class SystemTheme
{
    public static bool IsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public static void Apply(Window window, bool light)
    {
        var palette = new Dictionary<string, (string light, string dark)> {
            ["WindowBackground"] = ("#F3F3F3", "#202020"),
            ["CardBackground"] = ("#FFFFFF", "#2B2B2B"),
            ["CardBorder"] = ("#E8E8E8", "#2B2B2B"),
            ["MainText"] = ("#1A1A1A", "#F2F2F2"),
            ["Muted"] = ("#666666", "#C4C4C4"),
            ["Accent"] = ("#0067C0", "#4CC2FF"),
            ["AccentText"] = ("#FFFFFF", "#000000"),
            ["InputBackground"] = ("#FAFAFA", "#373737"),
            ["InputBorder"] = ("#CFCFCF", "#454545"),
            ["NavHover"] = ("#EBEBEB", "#292929"),
            ["NavSelected"] = ("#E7E7E7", "#2D2D2D"),
            ["TrackBorder"] = ("#888888", "#A0A0A0"),
            ["ThumbOff"] = ("#666666", "#CCCCCC"),
            ["ScrollThumb"] = ("#777777", "#999999"),
            ["InfoBackground"] = ("#EDF4FA", "#293139"),
        };
        foreach (var (name, pair) in palette) {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? pair.light : pair.dark));
            brush.Freeze(); window.Resources[name] = brush;
        }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) {
            int dark = light ? 0 : 1;
            // Unsupported Windows builds retain their normal title bar.
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
