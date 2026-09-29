namespace VMNotify;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable(SshAuthorization.PipeVariable) is { Length: > 0 } pipe) {
            Environment.ExitCode = args.Length == 1 ? SshAuthorization.RunHelper(pipe, args[0]).GetAwaiter().GetResult() : 1;
            return;
        }
        bool preview = args.Length == 2 && args[0] == "--preview";
        if (args.Length == 3 && args[0] == "--notification-check") {
            try { NotificationChecks.Run(args[1], args[2]).GetAwaiter().GetResult(); }
            catch (Exception ex) { System.IO.File.WriteAllText(args[2], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 1 && args[0] == "--unregister-notifications") {
            try {
                using var notifications = new DesktopNotifications(() => { });
                notifications.Uninstall().GetAwaiter().GetResult();
            }
            catch (Exception ex) { Diagnostics.Write("Notification uninstall failed: " + ex); Environment.ExitCode = 1; }
            return;
        }
        using var mutex = new Mutex(true, preview ? @"Local\VMNotify.Preview" : @"Local\VMNotify.Desktop", out bool first);
        if (!first) return;
        var app = new System.Windows.Application();
        var window = new MainWindow(args.Length == 2 && args[0] == "--preview");
        if (!preview) window.InitializeNotifications();
        if (args.Length == 2 && args[0] == "--preview") {
            window.Loaded += async (_, _) => {
                await System.Threading.Tasks.Task.Delay(250);
                await window.ExportPreviews(args[1]);
                window.QuitForPreview();
            };
        }
        if (window.StartsInTray && !args.Any(a => a.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal))) {
            app.MainWindow = window;
            app.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            app.Startup += async (_, _) => await window.StartInTray();
            app.Run();
        } else app.Run(window);
    }
}
