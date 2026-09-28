using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Microsoft.Win32;
using WpfMessageBox = System.Windows.MessageBox;

namespace VMNotify;

internal sealed class AppChoice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public bool Enabled { get; set; }
}

public partial class MainWindow : Window
{
    private readonly Receiver receiver = new();
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer scrollIdle = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private ScrollBar? overlayBar;
    private DateTime suppressScrollUntil;
    private HwndSource? windowSource;
    private readonly string settingsFile;
    private readonly bool previewMode;
    private Settings saved = new();
    private HashSet<string> enabled = [];
    private AvailableApp[]? displayed;
    private CancellationTokenSource? cancellation;
    private Task running = Task.CompletedTask;
    private DateTime lastNotification = DateTime.MinValue;
    private bool quitting, busy;

    public MainWindow(bool preview = false)
    {
        previewMode = preview;
        if (!preview) Diagnostics.Write("Application started");
        InitializeComponent();
        var build = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        VersionLabel.Text = "版本 " + build.Split('+')[0];
        VersionLabel.ToolTip = build;
        InitializeAbout(build);
        SystemTheme.Apply(this, SystemTheme.IsLight());
        SourceInitialized += (_, _) => {
            windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            windowSource?.AddHook(WindowMessage);
            SystemTheme.Apply(this, SystemTheme.IsLight());
        };
        if (!preview) SystemEvents.UserPreferenceChanged += ThemeChanged;
        scrollIdle.Tick += (_, _) => {
            if (overlayBar?.IsMouseOver == true || overlayBar?.IsMouseCaptureWithin == true) return;
            scrollIdle.Stop();
            overlayBar?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };
        bool portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
        settingsFile = Path.Combine(portable ? AppContext.BaseDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMNotify"), "settings.json");
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/VMNotify.ico")).Stream)
        using (var loadedIcon = new System.Drawing.Icon(stream, 32, 32)) trayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        tray = new() { Icon = trayIcon, Text = "VMNotify", Visible = !preview };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 VMNotify", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.InvokeAsync(async () => { quitting = true; await Stop(); Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        if (!preview) {
            try { if (File.Exists(settingsFile)) saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) ?? new(); }
            catch (Exception ex) { WpfMessageBox.Show("读取设置失败：" + ex.Message, "VMNotify"); }
        }
        saved = saved with { Host = saved.Host ?? "", User = saved.User ?? "", IdentityFile = saved.IdentityFile ?? "", AgentPath = saved.AgentPath ?? ".local/bin/vmnotify-agent", EnabledApps = saved.EnabledApps ?? [] };
        HostInput.Text = saved.Host; UserInput.Text = saved.User; PortInput.Text = saved.Port.ToString();
        IdentityInput.Text = saved.IdentityFile; AgentInput.Text = saved.AgentPath; AutoConnectInput.IsChecked = saved.AutoConnect;
        enabled = new(saved.EnabledApps); receiver.SetEnabledApps(enabled.ToArray());
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            StartupInput.IsChecked = string.Equals(key?.GetValue("VMNotify") as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
        OverviewNav.IsChecked = true; RefreshStatus();
        timer.Tick += (_, _) => {
            RefreshStatus();
            if (!ReferenceEquals(displayed, receiver.AvailableApps)) RenderApps(receiver.AvailableApps);
            if ((DateTime.UtcNow - lastNotification).TotalSeconds >= 5 && receiver.Notifications.Reader.TryRead(out var ev)) {
                if (receiver.IsEnabled(ev.AppId!)) {
                    LastNotification.Text = $"{ev.AppName} 有待查看的消息  ·  {DateTime.Now:HH:mm}";
                    Diagnostics.Write("Notification dequeued: " + ev.AppId);
                    ShowNotification("VMNotify · " + ev.AppName, "虚拟机中的应用正在提醒你查看消息。");
                }
                lastNotification = DateTime.UtcNow;
            }
        };
        if (!preview) timer.Start();
        Loaded += async (_, _) => { if (!preview && saved.AutoConnect && saved.Host.Length > 0) await Connect(); };
        Closing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => {
            updateLifetime.Cancel(); updateClient.Dispose();
            timer.Stop(); scrollIdle.Stop(); cancellation?.Cancel(); tray.Dispose(); trayIcon.Dispose();
            if (!preview) SystemEvents.UserPreferenceChanged -= ThemeChanged;
            windowSource?.RemoveHook(WindowMessage);
        };
        if (preview) { Opacity = 0; ShowInTaskbar = false; }
        StateChanged += (_, _) => {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        };
    }

    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void MinimizeClicked(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClicked(object sender, RoutedEventArgs e) {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    private void SelectPage(int index)
    {
        if (OverviewPage == null) return;
        suppressScrollUntil = DateTime.UtcNow.AddMilliseconds(120);
        PageScroll.ScrollToTop();
        HideScrollbarImmediately();
        OverviewPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionFooter.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = new[] { "概览", "连接设置", "应用管理", "关于" }[index];
        PageDescription.Text = new[] { "管理虚拟机连接与应用通知。", "设置与 Linux 虚拟机的连接方式。", "选择哪些应用可以在这台电脑上提醒你。", "版本与更新" }[index];
        if (index == 0) OverviewNav.IsChecked = true;
        else if (index == 1) ConnectionNav.IsChecked = true;
        else if (index == 2) AppsNav.IsChecked = true;
        else AboutNav.IsChecked = true;
    }
    private void OverviewClicked(object sender, RoutedEventArgs e) => SelectPage(0);
    private void ConnectionClicked(object sender, RoutedEventArgs e) => SelectPage(1);
    private void AppsClicked(object sender, RoutedEventArgs e) => SelectPage(2);
    private void ShowNotification(string title, string text)
    {
        Diagnostics.Write("Requesting Windows balloon; visible=" + tray.Visible);
        tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }
    private void TestClicked(object sender, RoutedEventArgs e) => ShowNotification("VMNotify", "这是一条本机测试通知。虚拟机连接需单独验证。");
    private void BrowseKeyClicked(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 SSH 私钥", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) IdentityInput.Text = dialog.FileName; }
    private async void ConnectClicked(object sender, RoutedEventArgs e) => await Connect();
    private async void DisconnectClicked(object sender, RoutedEventArgs e) => await Stop();
    private async void QuickConnectClicked(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(HostInput.Text)) SelectPage(1); else await Connect(); }

    private void RenderApps(AvailableApp[] apps)
    {
        displayed = apps;
        AppItems.ItemsSource = apps.Select(a => new AppChoice { Id = a.Id, Name = a.Name, Enabled = enabled.Contains(a.Id), Detail = a.Running ? "正在监听  ·  仅转发提醒" : "已安装，等待应用启动" }).ToArray();
        EmptyApps.Visibility = apps.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppListHeading.Text = apps.Length == 0 ? "可转发的应用" : $"可转发的应用（{apps.Length}）";
    }
    private void AppToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: AppChoice app } checkbox) return;
        if (checkbox.IsChecked == true) enabled.Add(app.Id); else enabled.Remove(app.Id);
        receiver.SetEnabledApps(enabled.ToArray()); saved = saved with { EnabledApps = enabled.ToArray() };
        if (!previewMode) try { SaveSettings(); } catch (Exception ex) { WpfMessageBox.Show("保存应用选择失败：" + ex.Message, "VMNotify"); }
        RefreshStatus();
    }
    private void RefreshStatus()
    {
        bool connected = receiver.Status.StartsWith("已连接");
        StatusTitle.Text = connected ? "正在接收虚拟机提醒" : receiver.Status.StartsWith("正在") ? "正在连接虚拟机" : "连接你的虚拟机";
        StatusDetail.Text = receiver.Status is "未连接" or "已断开" ? "开始连接后，你选择的应用会在这台电脑上显示提醒。" : receiver.Status;
        StatusIcon.Text = connected ? "\uE73E" : "\uE8D7";
        SidebarStatus.Text = connected ? "●  已连接" : "●  未连接";
        SidebarStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, connected ? "Accent" : "Muted");
        tray.Text = "VMNotify · " + (connected ? "已连接" : "未连接");
        HostSummary.Text = saved.Host.Length == 0 ? "尚未配置" : saved.Host;
        AppsSummary.Text = receiver.AvailableApps.Length == 0 ? "连接后发现受支持的应用" : $"{receiver.AvailableApps.Count(a => enabled.Contains(a.Id))} 个应用已开启转发";
        QuickConnect.Content = HostInput.Text.Length == 0 ? "配置连接" : connected ? "重新连接" : "连接虚拟机";
    }
    private static string StartupCommand() => "\"" + Environment.ProcessPath + "\"";
    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(settingsFile + ".tmp", settingsFile, overwrite: true);
    }
    private async Task Connect()
    {
        if (busy) return;
        busy = true; SaveConnect.IsEnabled = QuickConnect.IsEnabled = false;
        try {
            if (!int.TryParse(PortInput.Text, out var port)) throw new ArgumentException("SSH 端口必须是数字");
            var cfg = new Settings { Host = HostInput.Text.Trim(), User = UserInput.Text.Trim(), Port = port,
                IdentityFile = IdentityInput.Text.Trim(), AgentPath = AgentInput.Text.Trim(), AutoConnect = AutoConnectInput.IsChecked == true, EnabledApps = enabled.ToArray() };
            cfg.Validate(); Receiver.SshPath(); saved = cfg; SaveSettings();
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                if (StartupInput.IsChecked == true) key.SetValue("VMNotify", StartupCommand()); else key.DeleteValue("VMNotify", false);
            }
            await Stop(); if (quitting) return;
            cancellation = new(); running = receiver.Run(cfg, cancellation.Token); SelectPage(0);
        } catch (Exception ex) { WpfMessageBox.Show(this, ex.Message, "VMNotify", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; SaveConnect.IsEnabled = QuickConnect.IsEnabled = true; RefreshStatus(); }
    }
    private async Task Stop()
    {
        cancellation?.Cancel(); await running; cancellation?.Dispose(); cancellation = null;
        while (receiver.Notifications.Reader.TryRead(out _)) { }
        RefreshStatus();
    }

    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => SystemTheme.Apply(this, SystemTheme.IsLight()));
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x001A && !previewMode) SystemTheme.Apply(this, SystemTheme.IsLight());
        return IntPtr.Zero;
    }
    private void EnsureScrollbar()
    {
        PageScroll.ApplyTemplate();
        overlayBar ??= PageScroll.Template.FindName("PART_VerticalScrollBar", PageScroll) as ScrollBar;
    }
    private void ShowScrollbar()
    {
        EnsureScrollbar();
        if (overlayBar == null || PageScroll.ScrollableHeight <= 0) return;
        overlayBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
        scrollIdle.Stop(); scrollIdle.Start();
    }
    private void HideScrollbarImmediately()
    {
        scrollIdle.Stop(); EnsureScrollbar();
        overlayBar?.BeginAnimation(OpacityProperty, null);
        if (overlayBar != null) overlayBar.Opacity = 0;
    }
    private void PageScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && DateTime.UtcNow >= suppressScrollUntil) ShowScrollbar();
    }
    private void PageWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) => ShowScrollbar();

    internal async Task ExportPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        // Preview data is illustrative, never saved or connected to a real machine.
        HostInput.Text = "192.0.2.10"; UserInput.Text = "desktop-user";
        RenderApps([new("lanxin", "蓝信", true, "status-notifier-flash", "attention-only")]);
        StatusTitle.Text = "正在接收虚拟机提醒"; StatusDetail.Text = "已连接。应用提醒会自动转发到这台电脑。";
        StatusIcon.Text = "\uE73E"; SidebarStatus.Text = "●  已连接"; HostSummary.Text = "192.0.2.10"; AppsSummary.Text = "1 个应用已开启转发"; QuickConnect.Content = "重新连接";
        string[] names = ["overview", "connection", "apps", "about"];
        void Capture(string name) {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        foreach (bool light in new[] { false, true }) {
            SystemTheme.Apply(this, light);
            UpdateLayout();
            if (((SolidColorBrush)OverviewNav.Foreground).Color != ((SolidColorBrush)Resources["MainText"]).Color
                || ((SolidColorBrush)AutoConnectInput.Foreground).Color != ((SolidColorBrush)Resources["MainText"]).Color)
                throw new InvalidOperationException("Control text did not follow theme");
            for (int i = 0; i < names.Length; i++) {
                SelectPage(i); UpdateLayout(); await Task.Delay(150); HideScrollbarImmediately();
                Capture(names[i] + (light ? "-light" : "-dark"));
            }
        }
        SystemTheme.Apply(this, false); Height = 620; SelectPage(1); UpdateLayout();
        await Task.Delay(200); HideScrollbarImmediately();
        if (PageScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Scroll test needs overflowing content");
        double width = ConnectionPage.ActualWidth;
        if (overlayBar!.Opacity != 0) throw new InvalidOperationException("Idle scrollbar must be hidden");
        Capture("scroll-idle");
        PageScroll.ScrollToVerticalOffset(80); UpdateLayout(); await Task.Delay(150);
        if (overlayBar.Opacity < .9 || PageScroll.VerticalOffset <= 0) throw new InvalidOperationException("Scrollbar did not appear during scroll");
        if (Math.Abs(ConnectionPage.ActualWidth - width) > .1) throw new InvalidOperationException("Overlay scrollbar shifted layout");
        Capture("scroll-active");
        await Task.Delay(1400);
        if (overlayBar.Opacity > .01) throw new InvalidOperationException("Scrollbar did not fade after scrolling");
        Capture("scroll-faded");
        // Exercise the actual caption button handlers and native chrome hit testing.
        void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        int HitTest(double x, double y) {
            var point = Root.PointToScreen(new System.Windows.Point(x, y));
            long packed = (ushort)(int)point.X | ((long)(ushort)(int)point.Y << 16);
            return (int)SendMessage(new WindowInteropHelper(this).Handle, 0x0084, IntPtr.Zero, new IntPtr(unchecked((int)packed)));
        }
        if (HitTest(500, 20) != 2) throw new InvalidOperationException("Title bar is not draggable");
        if (HitTest(Root.ActualWidth - 1, 100) != 11) throw new InvalidOperationException("Window edge is not resizable");
        Click(MaximizeButton); await Task.Delay(150);
        if (WindowState != WindowState.Maximized || (string)MaximizeButton.Content != "\uE923") throw new InvalidOperationException("Maximize button failed");
        Click(MaximizeButton); await Task.Delay(150);
        if (WindowState != WindowState.Normal) throw new InvalidOperationException("Restore button failed");
        Click(MinimizeButton); await Task.Delay(150);
        if (WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize button failed");
        SystemCommands.RestoreWindow(this); await Task.Delay(150);
        Click(CloseButton); await Task.Delay(150);
        if (IsVisible) throw new InvalidOperationException("Close button did not hide window to tray");
        Show();
        File.WriteAllText(Path.Combine(directory, "ui-checks.txt"), "PASS: light/dark palettes, page rendering, hidden idle scrollbar, visible while scrolling, fade after idle, no layout shift, caption drag hit test, edge resize hit test, maximize/restore/minimize/close buttons.");
    }
    internal void QuitForPreview() { quitting = true; Close(); }
}
