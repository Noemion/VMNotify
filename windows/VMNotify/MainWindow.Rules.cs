using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace VMNotify;

public partial class MainWindow
{
    private async Task PreviewRules(string directory, bool light)
    {
        var icon = new IconObservation(":1.42/StatusNotifierItem", "pix-v1-example", "atrust-disconnected", false, ["#808080", "#FFFFFF"], false, false);
        var dialog = new RuleWindow(this, "aTrustTray2", new NotificationRule { Condition = RuleCondition.Grayscale, SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60,
            Message = "aTrust 未连接" }, () => [icon]) { Opacity = 0 };
        SystemTheme.Apply(dialog, light);
        dialog.Show();
        try {
            foreach (var condition in new[] { RuleCondition.Grayscale, RuleCondition.NameDiffers, RuleCondition.Color, RuleCondition.IconDiffers }) {
                dialog.ConditionInput.SelectedIndex = (int)condition;
                if (condition != RuleCondition.Grayscale) dialog.RecordButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (condition == RuleCondition.NameDiffers && dialog.TargetInput.Text != icon.IconName) throw new InvalidOperationException("Record icon name failed");
                if (condition == RuleCondition.IconDiffers && dialog.TargetInput.Text != icon.Fingerprint) throw new InvalidOperationException("Record icon state failed");
                dialog.Width = condition == RuleCondition.Color ? 560 : 660;
                dialog.UpdateLayout(); await Task.Delay(100);
                var root = (FrameworkElement)dialog.Content;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(directory, $"rules-{condition}-{(light ? "light" : "dark")}.png"));
                encoder.Save(output);
                if (dialog.RuleScroll.ScrollableHeight > 0) {
                    dialog.RuleScroll.ScrollToEnd(); dialog.UpdateLayout();
                    if (dialog.RuleScroll.VerticalOffset <= 0) throw new InvalidOperationException("Rule form must scroll to notification settings");
                    dialog.RuleScroll.ScrollToTop();
                }
            }
        } finally { dialog.Close(); }
    }

    private void RuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppChoice app }) return;
        var dialog = new RuleWindow(this, app.Name, saved.Rules.GetValueOrDefault(app.Id), () => receiver.GetIcons(app.Id));
        if (dialog.ShowDialog() != true) return;
        var previous = saved;
        try {
            var rules = new Dictionary<string, NotificationRule>(saved.Rules);
            if (dialog.Result == null) rules.Remove(app.Id); else rules[app.Id] = dialog.Result;
            if (rules.Count > 64) throw new ArgumentException("通知规则最多支持 64 个应用。");
            saved = saved with { Rules = rules };
            if (!previewMode) SaveSettings();
            receiver.SetRules(rules);
            RenderApps(displayed ?? []);
        } catch (Exception ex) {
            saved = previous;
            System.Windows.MessageBox.Show(this, "保存通知规则失败：" + ex.Message, "VMNotify", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
