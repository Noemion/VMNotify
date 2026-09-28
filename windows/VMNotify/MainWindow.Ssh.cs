using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace VMNotify;

public partial class MainWindow
{
    private Task<bool> ConfirmSshHost(string prompt, CancellationToken stop) => Dispatcher.InvokeAsync(() => {
        if (stop.IsCancellationRequested || quitting) return false;
        var dialog = CreateSshHostDialog(prompt);
        using var registration = stop.Register(() => Dispatcher.BeginInvoke(() => dialog.Close()));
        return dialog.ShowDialog() == true && !stop.IsCancellationRequested;
    }).Task;

    private Window CreateSshHostDialog(string prompt)
    {
        var dialog = new Window {
            Title = "首次连接：确认 SSH 主机", Width = 640, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ShowInTaskbar = true,
            Background = (Brush)FindResource("WindowBackground"), Foreground = (Brush)FindResource("MainText")
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        if (IsVisible) { dialog.Owner = this; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "请核对虚拟机的主机指纹", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "确认后将保存信任并继续连接；后续连接会自动校验。请通过可信途径核对下方 SHA256 指纹。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBox { Text = prompt, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消连接", IsCancel = true, IsDefault = true, Padding = new Thickness(16, 8, 16, 8) };
        var accept = new Button { Content = "信任并连接", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(12, 0, 0, 0) };
        accept.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog;
    }

    private async Task PreviewSshHost(string directory, bool light)
    {
        const string prompt = "The authenticity of host '192.0.2.10 (192.0.2.10)' can't be established.\nED25519 key fingerprint is SHA256:0123456789abcdefghijklmnopqrstuvwxyzABCDEFG.\nThis key is not known by any other names.\nAre you sure you want to continue connecting (yes/no/[fingerprint])?";
        var dialog = CreateSshHostDialog(prompt);
        dialog.Loaded += async (_, _) => {
            await Task.Delay(150);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = System.IO.File.Create(System.IO.Path.Combine(directory, "ssh-confirm-" + (light ? "light" : "dark") + ".png"))) encoder.Save(output);
            dialog.DialogResult = false;
        };
        if (dialog.ShowDialog() == true) throw new InvalidOperationException("SSH preview must not authorize a host");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        if (await ConfirmSshHost(prompt, stop.Token)) throw new InvalidOperationException("Cancelled SSH dialog authorized a host");
    }
}
