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
    public string RuleSummary { get; init; } = "";
    public bool Enabled { get; set; }
    public bool CanEnable { get; init; } = true;
    public string IgnoreAction { get; init; } = "忽略";
}

public partial class MainWindow : Window
{
    private readonly Receiver receiver = new();
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
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
    private bool quitting, busy;
    private bool loaded;
    private DateTime? pausedWindow;
    private DateTime scheduleRetryAfter;
    private string scheduleStatus = "";

    public MainWindow(bool preview = false)
    {
        previewMode = preview;
        if (!preview) Diagnostics.Write("Application started");
        InitializeComponent();
        receiver.AuthorizationHelper = Environment.ProcessPath;
        receiver.ConfirmHost = ConfirmSshHost;
        var build = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
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
        if (!preview) {
            tray.BalloonTipShown += (_, _) => Diagnostics.Write("Windows balloon shown");
            tray.BalloonTipClosed += (_, _) => Diagnostics.Write("Windows balloon closed");
            tray.BalloonTipClicked += (_, _) => Diagnostics.Write("Windows balloon clicked");
        }
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
        ScheduleInput.IsChecked = saved.ScheduleEnabled;
        SilentStartupInput.IsChecked = saved.SilentStartup;
        EveryDayInput.IsChecked = saved.ScheduleDays == ScheduleDays.EveryDay;
        ChinaWorkdaysInput.IsChecked = saved.ScheduleDays == ScheduleDays.ChinaWorkdays;
        ConnectTimeInput.Text = saved.ConnectTime;
        DisconnectTimeInput.Text = saved.DisconnectTime;
        saved = saved with { Rules = saved.Rules ?? new(), IgnoredApps = saved.IgnoredApps ?? new() };
        saved = saved with { EnabledApps = saved.EnabledApps.Where(id => !saved.IgnoredApps.ContainsKey(id)).ToArray() };
        try { receiver.SetRules(saved.Rules); }
        catch (ArgumentException ex) { WpfMessageBox.Show("通知规则无效，请重新设置：" + ex.Message, "VMNotify"); saved = saved with { Rules = new() }; }
        enabled = new(saved.EnabledApps); receiver.SetEnabledApps(enabled.ToArray());
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            StartupInput.IsChecked = string.Equals(key?.GetValue("VMNotify") as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
        OverviewNav.IsChecked = true; RefreshStatus();
        timer.Tick += async (_, _) => {
            await EvaluateSchedule();
            if (busy || quitting) return;
            RefreshStatus();
            receiver.QueueReminders();
            if (!ReferenceEquals(displayed, receiver.AvailableApps)) RenderApps(receiver.AvailableApps);
            if (receiver.Notifications.Reader.TryRead(out var ev)) {
                if (receiver.ShouldDisplay(ev)) {
                    var message = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                        ? ev.Kind == "reminder" ? $"虚拟机【{saved.Host}】中的【{ev.AppName}】仍有待查看的消息。" : $"虚拟机【{saved.Host}】中的【{ev.AppName}】有新消息。"
                        : ev.Kind == "reminder" ? $"Messages still need attention in [{ev.AppName}] on virtual machine [{saved.Host}]." : $"New message from [{ev.AppName}] on virtual machine [{saved.Host}].";
                    if (ev.Message != null) message = $"虚拟机【{saved.Host}】中的【{ev.AppName}】：{ev.Message}";
                    LastNotification.Text = $"{message}  ·  {DateTime.Now:HH:mm}";
                    Diagnostics.Write("Notification dequeued: " + ev.AppId);
                    ShowNotification("VMNotify · " + ev.AppName, message);
                    receiver.MarkDelivered(ev);
                }
            }
        };
        if (!preview) timer.Start();
        Loaded += async (_, _) => await StartBackground();
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

    internal bool StartsInTray => !previewMode && saved.SilentStartup;
    internal async Task StartInTray()
    {
        // Create the native handle for shutdown messages without ever showing the window.
        new WindowInteropHelper(this).EnsureHandle();
        await StartBackground();
    }
    private async Task StartBackground()
    {
        if (loaded) return;
        loaded = true;
        if (previewMode) return;
        if (saved.ScheduleEnabled) await EvaluateSchedule();
        else if (saved.AutoConnect && saved.Host.Length > 0) await Connect();
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
        SettingsPage.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        SettingsFooter.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionFooter.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = new[] { "概览", "虚拟机配置", "应用管理", "关于", "设置" }[index];
        PageDescription.Text = new[] { "管理虚拟机连接与应用通知。", "设置与 Linux 虚拟机的连接方式。", "选择哪些应用可以在这台电脑上提醒你。", "版本与更新", "管理启动行为与定时连接。" }[index];
        if (index == 0) OverviewNav.IsChecked = true;
        else if (index == 1) ConnectionNav.IsChecked = true;
        else if (index == 2) AppsNav.IsChecked = true;
        else if (index == 3) AboutNav.IsChecked = true; else SettingsNav.IsChecked = true;
    }
    private void OverviewClicked(object sender, RoutedEventArgs e) => SelectPage(0);
    private void ConnectionClicked(object sender, RoutedEventArgs e) => SelectPage(1);
    private void SettingsClicked(object sender, RoutedEventArgs e) => SelectPage(4);
    private void AppsClicked(object sender, RoutedEventArgs e) => SelectPage(2);
    private void ShowNotification(string title, string text)
    {
        var result = SHQueryUserNotificationState(out var state);
        Diagnostics.Write($"Requesting Windows balloon; visible={tray.Visible}; shellState={state}; result={result}");
        tray.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
    private void TestClicked(object sender, RoutedEventArgs e) => ShowNotification("VMNotify", "这是一条本机测试通知。虚拟机连接需单独验证。");
    private void BrowseKeyClicked(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 SSH 私钥", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) IdentityInput.Text = dialog.FileName; }
    private async void ConnectClicked(object sender, RoutedEventArgs e) => await Connect();
    private async void DisconnectClicked(object sender, RoutedEventArgs e) {
        if (busy) return;
        busy = true;
        try {
            if (saved.ScheduleEnabled) pausedWindow = DailySchedule.Parse(saved.ConnectTime, saved.DisconnectTime).WindowStart(DateTime.Now);
            await Stop();
        } finally { busy = false; }
    }
    private async void QuickConnectClicked(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(HostInput.Text)) SelectPage(1); else await Connect(); }

    private void RenderApps(AvailableApp[] apps)
    {
        displayed = apps;
        var filtered = AppCatalog.Filter(apps, saved, AppSearchInput.Text, showingIgnored);
        AppItems.ItemsSource = filtered.Select(a => new AppChoice { Id = a.Id, Name = a.Name, Enabled = !showingIgnored && enabled.Contains(a.Id), Detail = showingIgnored ? "已忽略 · 不接收提醒，恢复后可重新开启" : a.Description,
            CanEnable = !showingIgnored, IgnoreAction = showingIgnored ? "恢复显示" : "忽略",
            RuleSummary = saved.Rules.TryGetValue(a.Id, out var rule) ? $"{rule.Description} · 检测 {rule.SampleMilliseconds / 1000d:0.##} 秒 · 持续 {rule.HoldSeconds} 秒 · " + (rule.RepeatSeconds == 0 ? "仅提醒一次" : $"每 {rule.RepeatSeconds} 秒提醒") : "闪烁或需要关注 · 检测 0.25 秒 · 每 3 分钟提醒" }).ToArray();
        EmptyApps.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyAppsTitle.Text = AppSearchInput.Text.Trim().Length > 0 ? "没有匹配的应用" : showingIgnored ? "没有已忽略的应用" : "尚未发现可显示的应用";
        EmptyAppsDetail.Text = AppSearchInput.Text.Trim().Length > 0 ? "试试其他应用名称，或清空搜索。" : showingIgnored ? "忽略的应用会显示在这里，可随时恢复。" : "连接虚拟机并重新探测，或在“已忽略的应用”中恢复应用。";
        AppListHeading.Text = $"{(showingIgnored ? "已忽略的应用" : "可转发的应用")}（{filtered.Length}）";
        IgnoredAppsButton.Content = showingIgnored ? "返回应用列表" : $"已忽略的应用（{saved.IgnoredApps.Count}）";
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
        RefreshDiscoveryStatus();
        bool connected = receiver.Status.StartsWith("已连接");
        var status = receiver.Status;
        bool retrying = status.Contains("后重试") || status.StartsWith("正在");
        ConnectionLabel.Text = connected ? status.Contains("不可用") ? "已连接 · 监听异常" : "已连接"
            : retrying ? "连接中" : saved.ScheduleEnabled ? scheduleStatus.Contains("配置有误") ? "定时设置有误" : pausedWindow != null && scheduleStatus.Contains("手动断开") ? "本时段已暂停" : "等待定时连接" : status == "未连接" ? "未连接" : "已断开";
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, connected ? "Accent" : "Muted");
        ConnectionBadge.ToolTip = (saved.Host.Length > 0 ? saved.Host + "\n" : "") + status + (saved.ScheduleEnabled ? "\n" + scheduleStatus : "");
        ScheduleStatusText.Text = scheduleStatus;
        StatusTitle.Text = connected ? "正在接收虚拟机提醒" : receiver.Status.StartsWith("正在") ? "正在连接虚拟机" : "连接你的虚拟机";
        StatusDetail.Text = receiver.Status is "未连接" or "已断开" ? saved.ScheduleEnabled ? scheduleStatus : "开始连接后，你选择的应用会在这台电脑上显示提醒。" : receiver.Status;
        StatusIcon.Text = connected ? "\uE73E" : "\uE8D7";
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
            var cfg = saved with { Host = HostInput.Text.Trim(), User = UserInput.Text.Trim(), Port = port,
                IdentityFile = IdentityInput.Text.Trim(), AgentPath = AgentInput.Text.Trim(), EnabledApps = enabled.ToArray() }; // General options are saved independently.

            cfg.Validate(); Receiver.SshPath(); saved = cfg; SaveSettings();
            await Stop(); if (quitting) return;
            pausedWindow = null; scheduleRetryAfter = DateTime.MinValue;
            if (!cfg.ScheduleEnabled) scheduleStatus = "";
            if (!cfg.ScheduleEnabled || DailySchedule.Parse(cfg.ConnectTime, cfg.DisconnectTime).WindowStart(DateTime.Now, saved.ScheduleDays) != null) {
                cancellation = new(); running = receiver.Run(cfg, cancellation.Token);
            } else scheduleStatus = $"等待{ScheduleDaysLabel} {cfg.ConnectTime} 自动连接（本机时间）";
            SelectPage(0);
        } catch (Exception ex) { WpfMessageBox.Show(this, ex.Message, "VMNotify", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; SaveConnect.IsEnabled = QuickConnect.IsEnabled = true; RefreshStatus(); }
    }

    private async void SaveGeneralClicked(object sender, RoutedEventArgs e)
    {
        if (busy || previewMode) return;
        busy = true; SaveGeneral.IsEnabled = false;
        try {
            var cfg = saved with { AutoConnect = AutoConnectInput.IsChecked == true, SilentStartup = SilentStartupInput.IsChecked == true,
                ScheduleEnabled = ScheduleInput.IsChecked == true, ScheduleDays = ChinaWorkdaysInput.IsChecked == true ? ScheduleDays.ChinaWorkdays : ScheduleDays.EveryDay,
                ConnectTime = ConnectTimeInput.Text.Trim(), DisconnectTime = DisconnectTimeInput.Text.Trim() };
            if (cfg.ScheduleEnabled) _ = DailySchedule.Parse(cfg.ConnectTime, cfg.DisconnectTime);
            var previous = saved;
            saved = cfg;
            try { SaveSettings(); } catch { saved = previous; throw; }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                if (StartupInput.IsChecked == true) key.SetValue("VMNotify", StartupCommand()); else key.DeleteValue("VMNotify", false);
            }
            if (previous.ScheduleDays != cfg.ScheduleDays || previous.ScheduleEnabled != cfg.ScheduleEnabled || previous.ConnectTime != cfg.ConnectTime || previous.DisconnectTime != cfg.DisconnectTime) {
                pausedWindow = null; scheduleRetryAfter = DateTime.MinValue;
            }
            if (!cfg.ScheduleEnabled) scheduleStatus = "";
            SettingsSaveStatus.Text = "设置已保存。";
        } catch (Exception ex) { WpfMessageBox.Show(this, ex.Message, "VMNotify", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { busy = false; SaveGeneral.IsEnabled = true; }
        if (saved.Host.Length > 0) await EvaluateSchedule();
        RefreshStatus();
    }

    private string ScheduleDaysLabel => saved.ScheduleDays == ScheduleDays.ChinaWorkdays ? "中国工作日" : "每天";
    private async Task EvaluateSchedule()
    {
        if (!loaded || previewMode || busy || quitting || !saved.ScheduleEnabled) return;
        if (saved.Host.Length == 0) { scheduleStatus = "请先配置虚拟机连接。"; return; }
        busy = true;
        try {
            var now = DateTime.Now;
            var window = DailySchedule.Parse(saved.ConnectTime, saved.DisconnectTime).WindowStart(now, saved.ScheduleDays);
            scheduleStatus = window == null ? $"等待{ScheduleDaysLabel} {saved.ConnectTime} 自动连接（本机时间）"
                : pausedWindow == window ? "本时段已手动断开，下个时段自动连接"
                : $"{ScheduleDaysLabel} {saved.ConnectTime}–{saved.DisconnectTime} 连接（本机时间）";
            if (window == null) { if (cancellation != null) await Stop(); return; }
            if (pausedWindow == window || cancellation != null || now < scheduleRetryAfter) return;
            saved.Validate(); Receiver.SshPath();
            cancellation = new(); running = receiver.Run(saved, cancellation.Token);
        } catch (Exception ex) {
            if (cancellation != null) await Stop();
            scheduleStatus = "定时连接配置有误：" + ex.Message;
            scheduleRetryAfter = DateTime.Now.AddMinutes(1);
        } finally { busy = false; }
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
        // Restart Manager uses session shutdown messages, even without logging off.
        if (msg == 0x0011) { // WM_QUERYENDSESSION: allow shutdown, but not yet exit.
            handled = true;
            return new IntPtr(1);
        }
        if (msg == 0x0016 && wParam != IntPtr.Zero) { // WM_ENDSESSION confirmed.
            quitting = true;
            cancellation?.Cancel();
            Dispatcher.BeginInvoke(async () => { await Stop(); Close(); });
            handled = true;
        }
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
        await PreviewUpdateCleanup(directory);
        RenderApps([new("lanxin", "蓝信", true, "status-notifier-flash", "attention-only", true),
            new("auto-preview", "自动发现的应用", true, "status-notifier-auto", "attention-only")]);
        StatusTitle.Text = "正在接收虚拟机提醒"; StatusDetail.Text = "已连接。应用提醒会自动转发到这台电脑。";
        StatusIcon.Text = "\uE73E"; HostSummary.Text = "192.0.2.10"; AppsSummary.Text = "1 个应用已开启转发"; QuickConnect.Content = "重新连接";
        ConnectionLabel.Text = "已连接"; ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        string[] names = ["overview", "connection", "apps", "about", "settings"];
        void Capture(string name) {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        foreach (bool light in new[] { false, true }) {
            SystemTheme.Apply(this, light);
            await PreviewSshHost(directory, light);
            await PreviewRules(directory, light);
            UpdateLayout();
            if (((SolidColorBrush)OverviewNav.Foreground).Color != ((SolidColorBrush)Resources["MainText"]).Color
                || ((SolidColorBrush)AutoConnectInput.Foreground).Color != ((SolidColorBrush)Resources["MainText"]).Color)
                throw new InvalidOperationException("Control text did not follow theme");
            for (int i = 0; i < names.Length; i++) {
                SelectPage(i); UpdateLayout(); await Task.Delay(150); HideScrollbarImmediately();
                Capture(names[i] + (light ? "-light" : "-dark"));
                if (i == 2) {
                    Width = 900; UpdateLayout();
                    if (AppListHeading.TranslatePoint(new System.Windows.Point(AppListHeading.ActualWidth, 0), Root).X > RediscoverButton.TranslatePoint(new System.Windows.Point(), Root).X
                        || AppSearchInput.ActualWidth < 150
                        || IgnoredAppsButton.TranslatePoint(new System.Windows.Point(IgnoredAppsButton.ActualWidth, 0), Root).X > AppsPage.TranslatePoint(new System.Windows.Point(AppsPage.ActualWidth, 0), Root).X + 1)
                        throw new InvalidOperationException("Application discovery/search actions must fit at minimum width");
                    Capture("apps-900" + (light ? "-light" : "-dark"));
                    var catalogSettings = saved;
                    var catalogApps = displayed ?? [];
                    saved = AppCatalog.ToggleIgnored(saved, "preview-atrust", "aTrustTray2");
                    showingIgnored = true; AppSearchInput.Text = "ATRUST"; RenderApps(catalogApps); UpdateLayout();
                    if (AppItems.Items.Count != 1) throw new InvalidOperationException("Ignored apps search must include disconnected apps");
                    Capture("apps-ignored-900" + (light ? "-light" : "-dark"));
                    saved = catalogSettings; showingIgnored = false; AppSearchInput.Clear(); RenderApps(catalogApps);
                    Width = 1024; UpdateLayout();
                }
                if (i == 3) {
                    foreach (var previewWidth in new[] { 1024d, 900d }) {
                        Width = previewWidth; UpdateLayout();
                        UpdateLayout(); await Task.Delay(150);
                        double Right(FrameworkElement element) => element.TranslatePoint(new System.Windows.Point(element.ActualWidth, 0), Root).X;
                        if (Right(DownloadUpdateButton) > Right(AboutPage)
                            || CheckUpdateButton.TranslatePoint(new System.Windows.Point(), Root).Y != DownloadUpdateButton.TranslatePoint(new System.Windows.Point(), Root).Y)
                            throw new InvalidOperationException("Update actions must fit on one row at minimum width");
                        if (FindName("DownloadedPackages") != null) throw new InvalidOperationException("Download history must not be shown");
                        Capture($"about-update-{previewWidth}" + (light ? "-light" : "-dark"));
                    }
                    Width = 1024; UpdateLayout();
                }
                if (i == 4) {
                    ScheduleInput.IsChecked = true;
                    PageScroll.ScrollToEnd(); UpdateLayout(); await Task.Delay(150); HideScrollbarImmediately();
                    Capture("schedule" + (light ? "-light" : "-dark"));
                    ScheduleInput.IsChecked = false;
                }
            }
        }
        SystemTheme.Apply(this, false); Height = 620; PageScroll.Height = 240; SelectPage(1); UpdateLayout();
        await Task.Delay(200); HideScrollbarImmediately();
        if (PageScroll.ScrollableHeight <= 0) throw new InvalidOperationException("Scroll test needs overflowing content");
        double width = ConnectionPage.ActualWidth;
        if (overlayBar!.Opacity != 0) throw new InvalidOperationException("Idle scrollbar must be hidden");
        Capture("scroll-idle");
        PageScroll.ScrollToVerticalOffset(80); UpdateLayout(); await Task.Delay(150);
        if (overlayBar.Opacity < .9 || PageScroll.VerticalOffset <= 0) throw new InvalidOperationException("Scrollbar did not appear during scroll");
        if (Math.Abs(ConnectionPage.ActualWidth - width) > .1) throw new InvalidOperationException("Overlay scrollbar shifted layout");
        var barRight = overlayBar.TranslatePoint(new System.Windows.Point(overlayBar.ActualWidth, 0), Root).X;
        var barLeft = overlayBar.TranslatePoint(new System.Windows.Point(), Root).X;
        if (Root.ActualWidth - barRight > 8 || barLeft <= ConnectionPage.TranslatePoint(new System.Windows.Point(ConnectionPage.ActualWidth, 0), Root).X)
            throw new InvalidOperationException("Scrollbar must stay near the window edge and outside page content");
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
