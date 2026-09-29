using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace VMNotify;

public partial class MainWindow
{
    private RulePage OpenRules(string id, string name, Func<IconObservation[]> readIcons)
    {
        SelectPage(2);
        var page = new RulePage(name, saved.Rules.GetValueOrDefault(id), readIcons, () => ReadIconHistory(id, readIcons));
        page.BackRequested += () => SelectPage(2);
        page.SaveRequested += rule => {
            var previous = saved;
            try {
                var rules = new Dictionary<string, NotificationRule>(saved.Rules);
                if (rule == null) rules.Remove(id); else rules[id] = rule;
                if (rules.Count > 64) throw new ArgumentException("通知规则最多支持 64 个应用。");
                saved = saved with { Rules = rules };
                if (!previewMode) SaveSettings();
                receiver.SetRules(rules);
                RenderApps(displayed ?? []);
                SelectPage(2);
            } catch (Exception ex) {
                saved = previous;
                page.ValidationMessage.Text = "保存通知规则失败：" + ex.Message;
            }
        };
        RulesHost.Content = page;
        SelectPage(5);
        return page;
    }

    private void RuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppChoice app }) OpenRules(app.Id, app.Name, () => ReadTrayIcons(app.Id));
    }

    private async Task PreviewRules(string directory, bool light)
    {
        var before = saved;
        var previewId = previewIconEvents.FirstOrDefault(e => e.AppName == "aTrustTray2")?.AppId ?? "preview-vpn";
        var icon = new IconObservation(":1.42/StatusNotifierItem", "pix-v1-example", null, false, ["#808080", "#FFFFFF"], false, false,
            Preview: new TrayImage(2, 2, "00808080ff808080ff808080ffffffff"));
        icon = previewIconEvents.FirstOrDefault(e => e.AppName == "aTrustTray2" && e.Icon?.Preview != null)?.Icon ?? icon;
        saved = saved with { Rules = new(saved.Rules) { [previewId] = new NotificationRule { Condition = RuleCondition.Grayscale,
            SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60, Message = "aTrust 未连接" } } };
        var windowCount = System.Windows.Application.Current.Windows.Count;
        var page = OpenRules(previewId, "aTrustTray2", () => [icon]);
        try {
            if (page.IconStates.Children.Count == 0) throw new InvalidOperationException("Current image must appear in the discovered states list");
            ((Button)page.IconStates.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (page.ConditionInput.SelectedIndex != (int)RuleCondition.IconEquals || page.TargetInput.Text.Length == 0)
                throw new InvalidOperationException("Clicking a discovered image must select it as notification target");
            foreach (var condition in new[] { RuleCondition.Grayscale, RuleCondition.NameDiffers, RuleCondition.Color, RuleCondition.IconDiffers }) {
                page.ConditionInput.SelectedIndex = (int)condition;
                if (condition == RuleCondition.NameDiffers) page.TargetInput.Text = "atrust-connected";
                if (condition is RuleCondition.Color or RuleCondition.IconDiffers)
                    page.RecordButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (condition == RuleCondition.IconDiffers && page.TargetInput.Text != icon.Fingerprint) throw new InvalidOperationException("Record icon state failed");
                Width = condition == RuleCondition.Color ? 900 : 1024;
                UpdateLayout(); await Task.Delay(100);
                if (System.Windows.Application.Current.Windows.Count != windowCount || RulesHost.Visibility != Visibility.Visible || PageScroll.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("Rules must stay inside the main window");
                if (page.LiveImage.Source == null || page.MissingImage.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Unnamed icons must show their image");
                if (page.ColorSwatches.Visibility != (condition == RuleCondition.Color ? Visibility.Visible : Visibility.Collapsed))
                    throw new InvalidOperationException("Color swatches belong only to the color matching rule");
                var source = (System.Windows.Media.Imaging.BitmapSource)page.LiveImage.Source;
                var actualPixels = new byte[icon.Preview!.Width * icon.Preview.Height * 4];
                source.CopyPixels(actualPixels, icon.Preview.Width * 4, 0);
                if (!actualPixels.SequenceEqual(icon.Preview.BgraPixels()) || page.LiveImage.Opacity != 1
                    || page.LiveImage.StretchDirection != System.Windows.Controls.StretchDirection.DownOnly || page.LiveImage.Width > 24)
                    throw new InvalidOperationException("Tray images must keep original color and alpha without upscaling");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(Root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(directory, $"rules-{condition}-{(light ? "light" : "dark")}.png"));
                encoder.Save(output);
                if (page.RuleScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Rule form must scroll within the page");
                page.RuleScroll.ScrollToEnd(); UpdateLayout();
                if (page.RuleScroll.VerticalOffset <= 0 || page.SaveButton.TranslatePoint(new System.Windows.Point(0, page.SaveButton.ActualHeight), Root).Y > Root.ActualHeight)
                    throw new InvalidOperationException("Rule save controls must remain visible");
                page.RuleScroll.ScrollToTop();
            }
            page.SampleInput.Text = "invalid";
            page.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (RulesHost.Content != page || page.ValidationMessage.Text.Length == 0) throw new InvalidOperationException("Invalid input must remain on the rule page");
            page.ConditionInput.SelectedIndex = (int)RuleCondition.Grayscale; page.SampleInput.Text = "1";
            page.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (RulesHost.Content != null || saved.Rules[previewId].SampleMilliseconds != 1000 || AppsPage.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Saving must apply the rule and return to apps");
            page = OpenRules(previewId, "aTrustTray2", () => [icon]); page.HoldInput.Text = "999";
            page.BackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (saved.Rules[previewId].HoldSeconds == 999 || RulesHost.Content != null) throw new InvalidOperationException("Back must discard unsaved input");
            page = OpenRules(previewId, "aTrustTray2", () => [icon]);
            page.ResetButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (saved.Rules.ContainsKey(previewId) || RulesHost.Content != null) throw new InvalidOperationException("Reset must remove the saved rule");
        } finally { saved = before; receiver.SetRules(saved.Rules); Width = 1024; SelectPage(2); }
    }
}
