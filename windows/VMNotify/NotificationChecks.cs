using System.IO;
using System.Xml.Linq;
using Microsoft.Windows.AppNotifications;

namespace VMNotify;

internal static class NotificationChecks
{
    // Called from an isolated copy by scripts/test-notifications.ps1. The live
    // check deliberately sends no popup and removes only its own test group.
    internal static async Task Run(string mode, string output)
    {
        const string group = "vmnotify-selftest";
        if (mode == "payload") {
            var resource = System.Runtime.InteropServices.NativeLibrary.Load(
                Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Insights.Resource.dll"));
            System.Runtime.InteropServices.NativeLibrary.Free(resource);
            var first = DesktopNotifications.Create("通知 <&> 中文", "正文 <tag> & \"quoted\"");
            var second = DesktopNotifications.Create("second", "message");
            var xml = XDocument.Parse(first.Payload);
            var texts = xml.Descendants("text").Select(e => e.Value).ToArray();
            if (!texts.SequenceEqual(new[] { "通知 <&> 中文", "正文 <tag> & \"quoted\"" })
                || (string?)xml.Root?.Attribute("launch") != "action=open"
                || first.Tag == second.Tag || first.SuppressDisplay
                || first.Expiration < DateTimeOffset.Now.AddDays(6))
                throw new InvalidOperationException("Invalid notification content or retention settings.");
        } else {
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var notifications = new DesktopNotifications(() => activated.TrySetResult());
            if (!notifications.IsRegistered) throw new InvalidOperationException("App notification COM registration failed; see diagnostics.log.");
            var manager = AppNotificationManager.Default;
            if (mode == "activate") {
                File.WriteAllText(output + ".ready", "Registered");
                await activated.Task.WaitAsync(TimeSpan.FromSeconds(25));
            } else if (mode == "send") {
                await manager.RemoveByGroupAsync(group);
                for (int i = 0; i < 2; i++) {
                    var notification = DesktopNotifications.Create("VMNotify 通知留存测试", "验证后将自动清理。", "check" + i, silent: true);
                    notification.Group = group;
                    manager.Show(notification);
                    if (notification.Id == 0) throw new InvalidOperationException("Windows rejected test notification.");
                }
            } else if (mode == "verify") {
                bool found = false;
                for (int i = 0; i < 30; i++) {
                    var history = await manager.GetAllAsync();
                    if (history.Count(n => n.Group == group) == 2) { found = true; break; }
                    await Task.Delay(200);
                }
                if (!found) throw new InvalidOperationException("Two notifications did not survive process exit in Notification Center.");
                await manager.RemoveByGroupAsync(group);
            } else if (mode == "cleanup") {
                await manager.RemoveByGroupAsync(group);
                await notifications.Uninstall();
            } else throw new ArgumentException("Unknown notification check.");
        }
        File.WriteAllText(output, "PASS: notifications " + mode);
    }
}
