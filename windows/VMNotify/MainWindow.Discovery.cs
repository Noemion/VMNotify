using System.Windows;

namespace VMNotify;

public partial class MainWindow
{
    private bool rediscovering;
    private void RefreshDiscoveryStatus()
    {
        RediscoverButton.IsEnabled = !busy && !rediscovering && (previewMode || receiver.CanRediscover);
        RediscoverButton.Content = rediscovering ? "正在重新探测…" : "重新探测托盘图标";
        var version = receiver.AgentVersion;
        AgentVersionStatus.Visibility = version == null ? Visibility.Collapsed : Visibility.Visible;
        AgentVersionStatus.Text = version == null ? "" : Version.Parse(version) < new Version(0, 1, 4)
            ? $"Linux 采集端 {version} 较旧，可能缺少应用。请安装完整的新版 VMNotify，连接时会自动部署内置采集端。"
            : $"Linux 采集端 {version} · 未显示的应用可尝试重新探测。";
    }

    private async void RediscoverClicked(object sender, RoutedEventArgs e)
    {
        if (busy || rediscovering || previewMode) return;
        var revision = receiver.DiscoverySessionRevision;
        if (!receiver.RequestRediscovery()) return;
        rediscovering = true;
        RediscoverStatus.Visibility = Visibility.Visible;
        RediscoverStatus.Text = "正在重新连接并探测托盘图标，应用选择和通知规则将保留…";
        RefreshDiscoveryStatus();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try {
            while (receiver.InventoryRevision <= revision) {
                if (running.IsCompleted) { RediscoverStatus.Text = "连接已断开，请先连接虚拟机再重新探测。"; return; }
                await Task.Delay(100, timeout.Token);
            }
            RenderApps(receiver.AvailableApps);
            RediscoverStatus.Text = $"已重新探测，发现 {receiver.AvailableApps.Length} 个应用。";
        } catch (OperationCanceledException) {
            if (!quitting) RediscoverStatus.Text = "本次探测尚未完成，请检查连接状态及 Linux 采集端版本后重试。";
        } finally { rediscovering = false; RefreshDiscoveryStatus(); }
    }
}
