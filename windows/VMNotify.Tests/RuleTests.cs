using System.Text.Json;
using VMNotify;

internal static class RuleTests
{
    public static void Run()
    {
        static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("invalid rule accepted"); }
        var gray = new IconObservation(":1.5/item", "pix-v1-gray", "atrust-offline", false, ["#808080"], false, false);
        var colorful = gray with { Fingerprint = "pix-v1-color", IconName = "atrust-online", Colorful = true, Colors = ["#00AAFF"] };
        var missing = gray with { Colorful = null, Fingerprint = null, IconName = null, Colors = [], Flashing = null, Attention = null };
        var rule = new NotificationRule { Condition = RuleCondition.Grayscale, HoldSeconds = 5, RepeatSeconds = 10, Message = "aTrust 未连接" };
        Check(rule.Matches(gray) == true && rule.Matches(colorful) == false && rule.Matches(missing) == null, "gray and unknown must differ");
        Check((rule with { Condition = RuleCondition.Color, Target = "#0099F0", Tolerance = 20 }).Matches(colorful) == true, "color tolerance");
        Check((rule with { Condition = RuleCondition.Color, Target = "#FF0000" }).Matches(colorful) == false, "different color");
        Check((rule with { Condition = RuleCondition.NameEquals, Target = "atrust-offline" }).Matches(gray) == true, "name match");
        Check((rule with { Condition = RuleCondition.NameDiffers, Target = "atrust-online" }).Matches(missing) == null, "missing name is unknown");
        Check((rule with { Condition = RuleCondition.IconDiffers, Target = "pix-v1-color" }).Matches(gray) == true, "recorded icon differs");
        Check((rule with { Condition = RuleCondition.IconDiffers, Target = "pix-v1-color" }).Matches(missing) == null, "missing icon is unknown");
        Check((rule with { Condition = RuleCondition.IconDiffers, Target = "pix-v1-color" }).Matches(gray with { Fingerprint = "name-v1-fallback" }) == null, "pixel read failure with name fallback is not a changed icon");
        Check((rule with { Condition = RuleCondition.Flashing }).Matches(gray with { Flashing = true }) == true, "flashing independently selectable");
        Check((rule with { Condition = RuleCondition.NeedsAttention }).Matches(gray with { Attention = true }) == true, "attention independently selectable");
        var clock = new TimingTests.Clock(); var state = new RuleState();
        Check(!state.Update(rule, gray, clock), "hold starts silently"); clock.Advance(TimeSpan.FromSeconds(4));
        Check(!state.Update(rule, gray, clock), "hold not early");
        Check(!state.Update(rule, missing, clock), "unknown cancels hold"); clock.Advance(TimeSpan.FromSeconds(2));
        Check(!state.Update(rule, gray, clock), "unknown must restart hold"); clock.Advance(TimeSpan.FromSeconds(5));
        Check(state.Update(rule, gray, clock), "continuous hold triggers");
        Check(!state.Update(rule, colorful, clock), "normal clears");
        state = new(); var initialOff = rule with { NotifyInitial = false, HoldSeconds = 0 };
        Check(!state.Update(initialOff, gray, clock), "initial matching condition suppressed");
        Check(!state.Update(initialOff, missing, clock) && !state.Update(initialOff, gray, clock), "unknown does not rearm initial suppression");
        state.Update(initialOff, colorful, clock);
        Check(state.Update(initialOff, gray, clock), "observed normal arms later notification");

        var receiver = new Receiver(clock); receiver.SetEnabledApps(["atrust"]);
        receiver.SetRules(new() { ["atrust"] = rule });
        AgentEvent Sample(IconObservation icon) => new("heartbeat", "atrust", "aTrust", Icon: icon);
        receiver.ApplyObservation(Sample(gray)); clock.Advance(TimeSpan.FromSeconds(5)); receiver.ApplyObservation(Sample(gray));
        Check(receiver.Notifications.Reader.TryRead(out var ev) && receiver.ShouldDisplay(ev) && ev.Message == "aTrust 未连接", "rule notification");
        receiver.ApplyObservation(Sample(colorful with { InstanceId = "second" }));
        Check(receiver.ShouldDisplay(ev!), "normal second instance cannot clear first");
        clock.Advance(TimeSpan.FromSeconds(5)); receiver.ApplyObservation(Sample(gray));
        clock.Advance(TimeSpan.FromSeconds(5)); receiver.ApplyObservation(Sample(gray)); receiver.QueueReminders();
        Check(receiver.Notifications.Reader.TryRead(out var repeat) && repeat.Kind == "reminder", "custom reminder interval");
        receiver.ApplyObservation(Sample(gray with { Removed = true }));
        Check(!receiver.ShouldDisplay(repeat!), "removed instance cancels pending notification");
        receiver.SetRules(new() { ["atrust"] = rule with { HoldSeconds = 0, RepeatSeconds = 0 } });
        receiver.ApplyObservation(Sample(gray)); receiver.Notifications.Reader.TryRead(out ev);
        for (int i = 0; i < 30; i++) { clock.Advance(TimeSpan.FromSeconds(1)); receiver.ApplyObservation(Sample(gray)); receiver.QueueReminders(); }
        Check(!receiver.Notifications.Reader.TryRead(out _), "one-shot does not repeat");
        clock.Advance(TimeSpan.FromSeconds(8)); receiver.QueueReminders();
        Check(!receiver.ShouldDisplay(ev!) && receiver.GetIcons("atrust").Length == 0, "stale telemetry clears alerts and UI");
        receiver.ApplyLegacyAttention(new("attention", "atrust", "aTrust"));
        Check(!receiver.Notifications.Reader.TryRead(out _), "custom rule suppresses legacy flash event");
        receiver.SetRules(new());
        Check(receiver.Notifications.Reader.TryRead(out ev) && receiver.ShouldDisplay(ev), "restore default replays current legacy attention");
        var saved = new Settings { Rules = new() { ["atrust"] = rule } };
        Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(saved))!.Rules["atrust"] == rule, "rule settings round trip");
        Reject(() => (rule with { RepeatSeconds = 1 }).Validate());
        Reject(() => (rule with { Condition = RuleCondition.NameEquals }).Validate());
        Reject(() => (rule with { Condition = RuleCondition.Color, Target = "red" }).Validate());
        Reject(() => (rule with { SampleMilliseconds = 100 }).Validate());
        Reject(() => (rule with { SampleMilliseconds = 1200 }).Validate());
        Reject(() => (rule with { Condition = RuleCondition.Flashing, SampleMilliseconds = 1000 }).Validate());
        var atrustRule = rule with { SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60 };
        atrustRule.Validate();
        var ssh = new Settings { Host = "test-host", User = "desktop", Rules = new() { ["atrust"] = atrustRule, ["chat"] = new() } };
        Check(ssh.SshArguments().Last().EndsWith("--sample-intervals '{\"atrust\":1000}'"), "per-app sampling passed to remote agent with shell quoting");
        var slowClock = new TimingTests.Clock(); var slow = new Receiver(slowClock);
        slow.SetEnabledApps(["atrust"]); slow.SetRules(new() { ["atrust"] = atrustRule });
        slow.ApplyObservation(Sample(gray)); slowClock.Advance(TimeSpan.FromSeconds(1)); slow.ApplyObservation(Sample(gray));
        Check(!slow.Notifications.Reader.TryRead(out _), "two-second hold not early");
        slowClock.Advance(TimeSpan.FromSeconds(1)); slow.ApplyObservation(Sample(gray));
        Check(slow.Notifications.Reader.TryRead(out var timedAlert), "atrust first reminder after two seconds");
        slow.MarkDelivered(timedAlert!);
        for (int i = 0; i < 59; i++) { slowClock.Advance(TimeSpan.FromSeconds(1)); slow.ApplyObservation(Sample(gray)); slow.QueueReminders(); }
        Check(!slow.Notifications.Reader.TryRead(out _), "atrust repeat not before sixty seconds");
        slowClock.Advance(TimeSpan.FromSeconds(1)); slow.ApplyObservation(Sample(gray)); slow.QueueReminders();
        Check(slow.Notifications.Reader.TryRead(out var timedRepeat) && timedRepeat.Kind == "reminder", "atrust repeats at sixty seconds");
        slow.ApplyObservation(Sample(colorful)); Check(!slow.ShouldDisplay(timedRepeat!), "atrust color recovery clears repeat");
        slow.SetRules(new() { ["atrust"] = atrustRule with { SampleMilliseconds = 60000, HoldSeconds = 0 } });
        slow.ApplyObservation(Sample(gray)); slow.Notifications.Reader.TryRead(out timedAlert);
        slowClock.Advance(TimeSpan.FromSeconds(60)); slow.QueueReminders();
        Check(slow.GetIcons("atrust").Length == 1, "slow sampling does not expire after eight seconds");
        slowClock.Advance(TimeSpan.FromSeconds(66)); slow.QueueReminders();
        Check(slow.GetIcons("atrust").Length == 0 && !slow.ShouldDisplay(timedAlert!), "slow sampling still expires when updates stop");
        var parsed = AgentEvent.Parse("""{"v":1,"kind":"heartbeat","app_id":"atrust","app_name":"aTrust","icon":{"instance_id":":1.5/item","icon_name":"atrust-offline","fingerprint":"pix-v1-gray","colorful":false,"colors":["#808080"],"flashing":false,"attention":null,"removed":false}}""");
        Check(parsed.Icon?.IconName == "atrust-offline" && parsed.Icon.Colorful == false, "additive heartbeat parsing");
        Reject(() => AgentEvent.Parse("""{"v":1,"kind":"heartbeat","app_id":"a","app_name":"A","icon":{"instance_id":"item","colors":["invalid"]}}"""));
        Check(AgentEvent.Parse("{\"v\":1,\"kind\":\"heartbeat\"}").Icon == null, "old heartbeat compatibility");
        Console.WriteLine("PASS: icon/color/name rules, hold, unknown, initial suppression, multiple instances, expiry, reminders, persistence and protocol.");
    }
}
