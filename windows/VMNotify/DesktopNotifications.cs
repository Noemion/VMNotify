using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace VMNotify;

// Windows App SDK registers the unpackaged executable's identity and COM activator.
// Notifications are owned by Windows and survive both banner timeout and app exit.
internal sealed class DesktopNotifications : IDisposable
{
    private readonly Action openWindow;
    private readonly AppNotificationManager manager = AppNotificationManager.Default;
    private bool registered;
    internal bool IsRegistered => registered;
    public DesktopNotifications(Action openWindow)
    {
        this.openWindow = openWindow;
        manager.NotificationInvoked += Activated;
        try { manager.Register(); registered = true; }
        catch (Exception ex) { Diagnostics.Write("Windows notification registration failed: " + ex); }
    }

    private void Activated(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && action == "open") {
            Diagnostics.Write("Windows notification activated");
            openWindow();
        }
    }

    internal static AppNotification Create(string title, string message, string? tag = null, bool silent = false)
    {
        var notification = new AppNotificationBuilder().AddArgument("action", "open")
            .AddText(title).AddText(message).BuildNotification();
        notification.Tag = tag ?? Guid.NewGuid().ToString("N")[..16];
        notification.Group = "alerts";
        notification.Expiration = DateTimeOffset.Now.AddDays(7);
        notification.SuppressDisplay = silent;
        return notification;
    }

    public bool TryShow(string title, string message, out string? error)
    {
        try {
            if (!registered) { manager.Register(); registered = true; }
            if (manager.Setting != AppNotificationSetting.Enabled)
                throw new InvalidOperationException("Windows 通知已禁用（" + manager.Setting + "）。请检查系统通知设置。");
            var notification = Create(title, message);
            manager.Show(notification);
            if (notification.Id == 0) throw new InvalidOperationException("Windows 未接受通知。");
            Diagnostics.Write("Windows toast submitted: " + notification.Tag);
            error = null;
            return true;
        } catch (Exception ex) {
            Diagnostics.Write("Windows toast submission failed: " + ex);
            error = "Windows 通知发送失败：" + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        manager.NotificationInvoked -= Activated;
        if (registered) try { manager.Unregister(); }
            catch (Exception ex) { Diagnostics.Write("Windows notification unregister failed: " + ex); }
        // Do not clear history or uninstall the identity during ordinary shutdown.
    }

    internal async Task Uninstall()
    {
        await manager.RemoveAllAsync();
        manager.UnregisterAll();
        registered = false;
    }
}
