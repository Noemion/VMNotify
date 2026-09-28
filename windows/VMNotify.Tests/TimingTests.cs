using VMNotify;

internal static class TimingTests
{
    internal sealed class Clock : TimeProvider {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
    public static void Run() {
        static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        var date = new DateTime(2026, 9, 28);
        var day = DailySchedule.Parse("09:00", "18:00");
        Check(day.WindowStart(date.AddHours(8)) == null, "before start");
        Check(day.WindowStart(date.AddHours(9)) == date.AddHours(9), "inclusive start");
        Check(day.WindowStart(date.AddHours(18)) == null, "exclusive end");
        var night = DailySchedule.Parse("22:00", "07:00");
        Check(night.WindowStart(date.AddHours(23)) == date.AddHours(22), "overnight evening");
        Check(night.WindowStart(date.AddDays(1).AddHours(6)) == date.AddHours(22), "overnight same window after midnight");
        Check(night.WindowStart(date.AddDays(1).AddHours(7)) == null, "overnight end");
        Check(night.WindowStart(date.AddDays(1).AddHours(22)) != date.AddHours(22), "next period releases manual pause");
        foreach (var pair in new[] { ("25:00", "18:00"), ("09:00", "09:00"), ("9am", "18:00") }) {
            bool rejected = false;
            try { DailySchedule.Parse(pair.Item1, pair.Item2); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid schedule accepted");
        }
        Check(!new Settings().ScheduleEnabled, "schedule must opt in");
        var clock = new Clock(); var r = new Receiver(clock); var ev = new AgentEvent("attention", "lanxin", "蓝信");
        r.SetEnabledApps(["lanxin"]); r.ApplyAttention(ev);
        Check(r.Notifications.Reader.TryRead(out var initial) && r.ShouldDisplay(initial), "initial notification");
        r.MarkDelivered(initial!);
        clock.Advance(TimeSpan.FromSeconds(299)); r.QueueReminders();
        Check(!r.Notifications.Reader.TryRead(out _), "no early repeat");
        clock.Advance(TimeSpan.FromSeconds(1)); r.QueueReminders();
        Check(r.Notifications.Reader.TryRead(out var repeat) && repeat.Kind == "reminder" && r.ShouldDisplay(repeat), "five minute repeat");
        r.MarkDelivered(repeat!); r.QueueReminders();
        Check(!r.Notifications.Reader.TryRead(out _), "no duplicate on next tick");
        clock.Advance(TimeSpan.FromMinutes(5)); r.QueueReminders();
        Check(r.Notifications.Reader.TryRead(out var stale), "second repeat");
        r.ApplyAttention(ev with { Kind = "cleared" });
        Check(!r.ShouldDisplay(stale!), "queued repeat invalid after cleared");
        clock.Advance(TimeSpan.FromMinutes(10)); r.QueueReminders();
        Check(!r.Notifications.Reader.TryRead(out _), "no repeats after cleared");
        r.ApplyAttention(ev); r.Notifications.Reader.TryRead(out _); r.SetEnabledApps([]);
        clock.Advance(TimeSpan.FromMinutes(5)); r.QueueReminders();
        Check(!r.Notifications.Reader.TryRead(out _), "disabled app no repeats");
        Console.WriteLine("PASS: daily/overnight schedules and five-minute reminder timing, cancellation and opt-out.");
    }
}
