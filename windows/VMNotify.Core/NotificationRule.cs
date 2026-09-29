using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VMNotify;

[JsonConverter(typeof(JsonStringEnumConverter<RuleCondition>))]
public enum RuleCondition { Default, Flashing, NeedsAttention, Colorful, Grayscale, Color, IconEquals, IconDiffers, NameEquals, NameDiffers }

public sealed record NotificationRule
{
    public RuleCondition Condition { get; init; }
    public string Target { get; init; } = "";
    public int Tolerance { get; init; } = 40;
    public int HoldSeconds { get; init; }
    public int SampleMilliseconds { get; init; } = 250;
    public int RepeatSeconds { get; init; } = 180;
    public bool NotifyInitial { get; init; } = true;
    public string Message { get; init; } = "";
    public void Validate()
    {
        if (SampleMilliseconds is < 250 or > 60000 || SampleMilliseconds % 250 != 0)
            throw new ArgumentException("检测间隔应为 0.25–60 秒，按 0.25 秒递增。");
        if (Condition is RuleCondition.Default or RuleCondition.Flashing && SampleMilliseconds > 500)
            throw new ArgumentException("闪烁检测的间隔应为 0.25 或 0.5 秒，避免漏过快速图标变化。");
        if (!Enum.IsDefined(Condition) || HoldSeconds is < 0 or > 3600 || RepeatSeconds is < 0 or > 86400
            || (RepeatSeconds is > 0 and < 10) || Tolerance is < 0 or > 255)
            throw new ArgumentException("持续时间应为 0–3600 秒，重复间隔为 0（不重复）或 10–86400 秒，颜色容差为 0–255。");
        if (Target == null || Target.Length > 1024 || Target.Any(char.IsControl) || Message == null || Message.Length > 200 || Message.Any(char.IsControl))
            throw new ArgumentException("目标或通知内容无效，通知内容最多 200 字且不能换行。");
        if (Condition == RuleCondition.Color && !IsColor(Target)) throw new ArgumentException("颜色请填写 #RRGGBB，例如 #808080。");
        if (Condition is RuleCondition.IconEquals or RuleCondition.IconDiffers or RuleCondition.NameEquals or RuleCondition.NameDiffers && string.IsNullOrWhiteSpace(Target))
            throw new ArgumentException("请先记录图标，或填写图标名称。");
    }
    internal static bool IsColor(string value) => Regex.IsMatch(value, "\\A#[0-9a-fA-F]{6}\\z");
    public bool? Matches(IconObservation icon) => Condition switch {
        RuleCondition.Default => icon.Flashing == true || icon.Attention == true ? true : icon.Flashing == null && icon.Attention == null ? null : false,
        RuleCondition.Flashing => icon.Flashing,
        RuleCondition.NeedsAttention => icon.Attention,
        RuleCondition.Colorful => icon.Colorful,
        RuleCondition.Grayscale => icon.Colorful is bool colorful ? !colorful : null,
        RuleCondition.Color => icon.Colorful == null ? null : icon.Colors.Any(c => ColorDistance(c, Target) <= Tolerance),
        RuleCondition.IconEquals => SameIconSource(icon.Fingerprint) ? icon.Fingerprint == Target : null,
        RuleCondition.IconDiffers => SameIconSource(icon.Fingerprint) ? icon.Fingerprint != Target : null,
        RuleCondition.NameEquals => icon.IconName == null ? null : icon.IconName == Target,
        RuleCondition.NameDiffers => icon.IconName == null ? null : icon.IconName != Target,
        _ => null
    };
    private bool SameIconSource(string? fingerprint) => fingerprint != null &&
        ((fingerprint.StartsWith("pix-v1-") && Target.StartsWith("pix-v1-")) || (fingerprint.StartsWith("name-v1-") && Target.StartsWith("name-v1-")));
    private static int ColorDistance(string a, string b) => new[] { 1, 3, 5 }.Max(i => Math.Abs(Convert.ToInt32(a.Substring(i, 2), 16) - Convert.ToInt32(b.Substring(i, 2), 16)));
    public string Description => Condition switch {
        RuleCondition.Default => "闪烁或需要关注", RuleCondition.Flashing => "图标闪烁", RuleCondition.NeedsAttention => "需要关注",
        RuleCondition.Colorful => "变为彩色", RuleCondition.Grayscale => "变为灰色", RuleCondition.Color => "接近颜色 " + Target,
        RuleCondition.IconEquals => "变为记录的图标", RuleCondition.IconDiffers => "离开记录的图标",
        RuleCondition.NameEquals => "图标名称等于 " + Target, RuleCondition.NameDiffers => "图标名称不等于 " + Target, _ => "未知规则"
    };
    public string NotificationText(string appName, IconObservation icon)
    {
        if (!string.IsNullOrWhiteSpace(Message)) return Message;
        var state = icon.Colorful is bool colorful ? colorful ? "彩色" : "灰色" : "已选状态";
        var detail = Condition switch {
            RuleCondition.IconEquals => "图标变为" + state,
            RuleCondition.IconDiffers => "图标已离开所选状态" + (icon.Colorful == null ? "" : "，当前为" + state),
            RuleCondition.Colorful => "图标变为彩色",
            RuleCondition.Grayscale => "图标变为灰色",
            RuleCondition.Color => "图标颜色接近 " + Target,
            RuleCondition.Flashing => "图标正在闪烁",
            RuleCondition.NeedsAttention => "上报需要关注",
            RuleCondition.Default => icon.Attention == true ? "上报需要关注" : "图标正在闪烁",
            _ => Description
        };
        return appName + " " + detail;
    }
}

public sealed record IconObservation(string InstanceId, string? Fingerprint, string? IconName, bool? Colorful,
    string[] Colors, bool? Flashing, bool? Attention, bool Removed = false, TrayImage? Preview = null);

// Independent state for each instance; unknown readings interrupt the hold timer.
public sealed class RuleState
{
    private bool initialized, armed;
    private long? since;
    public bool Active { get; private set; }
    public bool Update(NotificationRule rule, IconObservation icon, TimeProvider clock)
    {
        var matches = rule.Matches(icon);
        if (matches == null) { since = null; Active = false; return false; }
        if (!initialized) { initialized = true; armed = rule.NotifyInitial || !matches.Value; }
        if (!matches.Value) { armed = true; since = null; Active = false; return false; }
        if (!armed) return false;
        since ??= clock.GetTimestamp();
        return Active = clock.GetElapsedTime(since.Value).TotalSeconds >= rule.HoldSeconds;
    }
}
