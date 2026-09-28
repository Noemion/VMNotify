using System.Text.Json;
using VMNotify;

internal static class CalendarTests
{
    public static void Run()
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var schedule = DailySchedule.Parse("09:00", "18:00");
        foreach (var (date, work) in new[] {
            ("2026-09-28", true), ("2026-09-19", false), ("2026-09-20", true),
            ("2026-10-01", false), ("2026-10-07", false), ("2026-10-08", true), ("2026-10-10", true),
            ("2026-01-01", false), ("2026-01-04", true), ("2026-02-14", true),
            ("2026-02-15", false), ("2026-02-23", false), ("2026-02-28", true),
            ("2026-04-06", false), ("2026-05-05", false), ("2026-05-09", true),
            ("2026-06-19", false), ("2026-09-25", false), ("2025-09-28", true)
        }) {
            var day = DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Check(ChinaWorkCalendar.IsWorkday(day) == work, date);
            Check((schedule.WindowStart(day.ToDateTime(new(12, 0)), ScheduleDays.ChinaWorkdays) != null) == work, "filter " + date);
            Check(schedule.WindowStart(day.ToDateTime(new(12, 0)), ScheduleDays.EveryDay) != null, "every day " + date);
        }
        var night = DailySchedule.Parse("22:00", "07:00");
        Check(night.WindowStart(new(2026, 10, 1, 6, 0, 0), ScheduleDays.ChinaWorkdays) == new DateTime(2026, 9, 30, 22, 0, 0), "workday overnight into holiday");
        Check(night.WindowStart(new(2026, 10, 8, 6, 0, 0), ScheduleDays.ChinaWorkdays) == null, "holiday overnight into workday");
        Check(night.WindowStart(new(2026, 1, 1, 6, 0, 0), ScheduleDays.ChinaWorkdays) == new DateTime(2025, 12, 31, 22, 0, 0), "year boundary");
        Check(night.WindowStart(new(2026, 10, 1, 7, 0, 0), ScheduleDays.ChinaWorkdays) == null, "end is exclusive");
        Check(schedule.WindowStart(new(2027, 1, 4, 12, 0, 0), ScheduleDays.EveryDay) != null, "every day needs no calendar");
        bool unknown = false;
        try { schedule.WindowStart(new(2027, 1, 4, 12, 0, 0), ScheduleDays.ChinaWorkdays); } catch (InvalidOperationException) { unknown = true; }
        Check(unknown, "unknown year must not guess weekdays");
        var legacy = JsonSerializer.Deserialize<Settings>("{}")!;
        Check(legacy.ScheduleDays == ScheduleDays.EveryDay && !legacy.SilentStartup, "legacy defaults");
        var config = legacy with { ScheduleDays = ScheduleDays.ChinaWorkdays, SilentStartup = true };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(config))!;
        Check(restored.ScheduleDays == ScheduleDays.ChinaWorkdays && restored.SilentStartup, "settings persistence");
        Console.WriteLine("PASS: China holidays, weekend make-up workdays, overnight/year boundaries, missing data, and silent-start settings.");
    }
}
