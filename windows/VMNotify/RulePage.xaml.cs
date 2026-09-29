using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace VMNotify;

public partial class RulePage : System.Windows.Controls.UserControl
{
    private readonly Func<IconObservation[]> readIcons;
    private readonly Func<RecordedIcon[]> readHistory;
    private string historySignature = "";
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IconObservation[] icons = [];
    private string[] displayedColors = [];
    private bool initialized;
    private sealed record InstanceChoice(string Id, string Label) { public override string ToString() => Label; }
    internal event Action<NotificationRule?>? SaveRequested;
    internal event Action? BackRequested;
    private TrayImage? displayedPreview;
    private sealed record ConditionChoice(RuleCondition Value, string Label) { public override string ToString() => Label; }
    private RuleCondition Condition => ConditionInput.SelectedValue is RuleCondition value ? value : RuleCondition.Default;
    private IconObservation? Current => icons.FirstOrDefault(i => i.InstanceId == InstanceInput.SelectedValue as string);
    internal RulePage(string name, NotificationRule? rule, Func<IconObservation[]> readIcons, Func<RecordedIcon[]> readHistory)
    {
        this.readIcons = readIcons;
        this.readHistory = readHistory;
        InitializeComponent();
        AppTitle.Text = name + " · 通知规则";
        rule ??= new();
        ConditionInput.DisplayMemberPath = "Label";
        ConditionInput.SelectedValuePath = "Value";
        ConditionInput.ItemsSource = Enum.GetValues<RuleCondition>()
            .Where(c => c is not (RuleCondition.Colorful or RuleCondition.Grayscale) || c == rule.Condition)
            .Select(c => new ConditionChoice(c, new NotificationRule { Condition = c }.Description
                + (c is RuleCondition.Colorful or RuleCondition.Grayscale ? "（旧版规则）" : ""))).ToArray();
        ConditionInput.SelectedValue = rule.Condition;
        TargetInput.Text = rule.Target; ToleranceInput.Text = rule.Tolerance.ToString();
        HoldInput.Text = rule.HoldSeconds.ToString(); RepeatInput.Text = rule.RepeatSeconds.ToString();
        SampleInput.Text = (rule.SampleMilliseconds / 1000m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InitialInput.IsChecked = rule.NotifyInitial; MessageInput.Text = rule.Message;
        initialized = true; UpdateCondition(); Refresh();
        refresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => refresh.Start(); Unloaded += (_, _) => refresh.Stop();
    }
    private void Refresh()
    {
        icons = readIcons();
        var ids = icons.Select(i => i.InstanceId).ToArray();
        if (!InstanceInput.Items.Cast<InstanceChoice>().Select(i => i.Id).SequenceEqual(ids)) {
            var selected = InstanceInput.SelectedValue as string;
            InstanceInput.ItemsSource = ids.Select((id, i) => new InstanceChoice(id, $"实例 {i + 1}")).ToArray();
            InstanceInput.SelectedValue = ids.Contains(selected) ? selected : ids.FirstOrDefault();
        }
        InstanceInput.Visibility = ids.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowCurrent();
        ShowHistory();
    }
    private void ShowHistory()
    {
        var history = readHistory();
        var signature = string.Join("|", history.Select(i => i.Fingerprint + i.Image.ArgbHex)) + "|" + Current?.Fingerprint + "|" + TargetInput.Text + Condition;
        if (signature == historySignature) return;
        historySignature = signature;
        IconStates.Children.Clear();
        HistoryEmpty.Visibility = history.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int index = 0; index < history.Length; index++) {
            var state = history[index];
            var content = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            content.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), Background = TrayImages.Backdrop(state.Image),
                Child = new System.Windows.Controls.Image { Source = TrayImages.Create(state.Image), Width = 24, Height = 24, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                    SnapsToDevicePixels = true } });
            RenderOptions.SetBitmapScalingMode(((Border)content.Children[0]).Child, BitmapScalingMode.NearestNeighbor);
            bool current = Current?.Fingerprint == state.Fingerprint;
            content.Children.Add(new TextBlock { Text = (state.Colorful is bool color ? color ? "彩色" : "灰色" : $"图标 {index + 1}") + (current ? " · 当前" : ""),
                FontSize = 12, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            var button = new Button { Content = content, MinWidth = 86, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 8),
                ToolTip = "点击设为通知图标" + (state.IconName == null ? "" : "\n" + state.IconName),
                BorderThickness = new Thickness(Condition == RuleCondition.IconEquals && TargetInput.Text == state.Fingerprint ? 2 : 1) };
            if (Condition == RuleCondition.IconEquals && TargetInput.Text == state.Fingerprint) button.SetResourceReference(BorderBrushProperty, "Accent");
            button.Click += (_, _) => { ConditionInput.SelectedValue = RuleCondition.IconEquals; TargetInput.Text = state.Fingerprint; ShowHistory(); };
            IconStates.Children.Add(button);
        }
    }
    private void InstanceChanged(object sender, SelectionChangedEventArgs e) { if (initialized) { ShowCurrent(); ShowHistory(); } }
    private void ShowCurrent()
    {
        var icon = Current;
        if (displayedPreview != icon?.Preview) {
            displayedPreview = icon?.Preview;
            LiveImage.Source = TrayImages.Create(displayedPreview);
            ImageBackdrop.Background = TrayImages.Backdrop(displayedPreview);
        }
        MissingImage.Visibility = LiveImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        LiveName.Text = icon?.IconName ?? "（未提供图标名称）";
        LiveStatus.Text = icon == null ? "暂无实时数据。请连接虚拟机并运行应用；旧版 Linux 采集端需要更新。"
            : $"颜色：{(icon.Colorful is bool c ? c ? "彩色" : "灰色" : "无法判断")}  ·  闪烁：{Flag(icon.Flashing)}  ·  需要关注：{Flag(icon.Attention)}";
        if (!displayedColors.SequenceEqual(icon?.Colors ?? [])) {
            displayedColors = icon?.Colors ?? [];
            ColorSwatches.Children.Clear();
            foreach (var color in displayedColors) {
                var button = new Button { Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 4),
                    Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(color)), ToolTip = color + " · 点击用于指定颜色" };
                button.Click += (_, _) => { ConditionInput.SelectedValue = RuleCondition.Color; TargetInput.Text = color; };
                ColorSwatches.Children.Add(button);
            }
        }
        RecordButton.IsEnabled = Condition switch {
            RuleCondition.IconEquals or RuleCondition.IconDiffers => icon?.Fingerprint != null,
            RuleCondition.NameEquals or RuleCondition.NameDiffers => icon?.IconName != null,
            RuleCondition.Color => icon?.Colors.Length > 0, _ => false
        };
    }
    private static string Flag(bool? flag) => flag is bool value ? value ? "是" : "否" : "无法判断";
    private void ConditionChanged(object sender, SelectionChangedEventArgs e) { if (initialized) { TargetInput.Text = ""; UpdateCondition(); ShowCurrent(); ShowHistory(); } }
    private void UpdateCondition()
    {
        bool color = Condition == RuleCondition.Color;
        bool name = Condition is RuleCondition.NameEquals or RuleCondition.NameDiffers;
        bool icon = Condition is RuleCondition.IconEquals or RuleCondition.IconDiffers;
        TargetPanel.Visibility = color || name || icon ? Visibility.Visible : Visibility.Collapsed;
        TolerancePanel.Visibility = PickColorButton.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        ColorSwatches.Visibility = ColorSwatchesLabel.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        TargetInput.IsReadOnly = icon;
        TargetLabel.Text = color ? "目标颜色（#RRGGBB）" : name ? "目标图标名称（完整名称，区分大小写）" : "已记录的图标标识";
        RecordButton.Content = icon ? "记录当前图标" : name ? "使用当前名称" : "使用当前主要颜色";
        ConditionHelp.Text = color ? "匹配图标中的主要颜色，忽略透明背景；可直接点击上方色块。"
            : name ? "名称为空或读取失败时无法判断，不会当作名称不等于。"
            : icon ? "按像素特征匹配；只有名称可读时按名称匹配。主题、尺寸或动画变化可能影响结果。"
            : "同一应用任一实例符合条件即提醒；读取失败会中断持续时间，恢复正常后停止重复。";
    }
    private void RecordClicked(object sender, RoutedEventArgs e)
    {
        var icon = Current;
        TargetInput.Text = Condition switch {
            RuleCondition.IconEquals or RuleCondition.IconDiffers => icon?.Fingerprint ?? "",
            RuleCondition.NameEquals or RuleCondition.NameDiffers => icon?.IconName ?? "",
            RuleCondition.Color => icon?.Colors.FirstOrDefault() ?? "", _ => ""
        };
    }
    private void PickColorClicked(object sender, RoutedEventArgs e)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) TargetInput.Text = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try {
            if (!int.TryParse(HoldInput.Text, out var hold) || !int.TryParse(RepeatInput.Text, out var repeat) || !int.TryParse(ToleranceInput.Text, out var tolerance))
                throw new ArgumentException("持续时间、重复间隔和颜色容差请填写整数。");
            if (!decimal.TryParse(SampleInput.Text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var sample)
                || sample < .25m || sample > 60 || sample * 1000 % 250 != 0)
                throw new ArgumentException("检测间隔应为 0.25–60 秒，按 0.25 秒递增，例如 0.25、0.5、2、5。");
            var rule = new NotificationRule { Condition = Condition, Target = TargetInput.Text.Trim(), HoldSeconds = hold,
                RepeatSeconds = repeat, SampleMilliseconds = (int)(sample * 1000), Tolerance = tolerance, NotifyInitial = InitialInput.IsChecked == true, Message = MessageInput.Text.Trim() };
            rule.Validate(); SaveRequested?.Invoke(rule);
        } catch (ArgumentException ex) { ValidationMessage.Text = ex.Message; }
    }
    private void ResetClicked(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(null);
    private void BackClicked(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
}
