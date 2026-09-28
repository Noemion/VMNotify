namespace VMNotify;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool preview = args.Length == 2 && args[0] == "--preview";
        using var mutex = new Mutex(true, preview ? @"Local\VMNotify.Preview" : @"Local\VMNotify.Desktop", out bool first);
        if (!first) return;
        var app = new System.Windows.Application();
        var window = new MainWindow(args.Length == 2 && args[0] == "--preview");
        if (args.Length == 2 && args[0] == "--preview") {
            window.Loaded += async (_, _) => {
                await System.Threading.Tasks.Task.Delay(250);
                await window.ExportPreviews(args[1]);
                window.QuitForPreview();
            };
        }
        app.Run(window);
    }
}
