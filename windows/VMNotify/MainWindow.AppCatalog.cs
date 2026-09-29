using System.Windows;
using System.Windows.Controls;

namespace VMNotify;

public partial class MainWindow
{
    private bool showingIgnored;
    private void AppSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (AppItems != null) RenderApps(displayed ?? []);
    }
    private void IgnoredAppsClicked(object sender, RoutedEventArgs e)
    {
        showingIgnored = !showingIgnored;
        AppSearchInput.Clear();
        RenderApps(displayed ?? []);
    }
    private void IgnoreAppClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: AppChoice app }) return;
        var previous = saved;
        try {
            saved = AppCatalog.ToggleIgnored(saved, app.Id, app.Name);
            if (!previewMode) SaveSettings();
        } catch (Exception ex) {
            saved = previous;
            System.Windows.MessageBox.Show(this, "保存忽略设置失败：" + ex.Message, "VMNotify", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        enabled = new(saved.EnabledApps);
        receiver.SetEnabledApps(enabled.ToArray());
        RenderApps(displayed ?? []);
        RefreshStatus();
    }
}
