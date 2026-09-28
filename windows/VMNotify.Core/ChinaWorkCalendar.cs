namespace VMNotify;

public enum ScheduleDays { EveryDay, ChinaWorkdays }

public static class ChinaWorkCalendar
{
    // State Council annual notices; see docs/china-work-calendar.md for sources.
    private static readonly Dictionary<int, (HashSet<DateOnly> Holidays, HashSet<DateOnly> Workdays)> Years = new() {
        [2025] = (Ranges(2025, "01-01/01-01", "01-28/02-04", "04-04/04-06", "05-01/05-05", "05-31/06-02", "10-01/10-08"),
            Dates(2025, "01-26", "02-08", "04-27", "09-28", "10-11")),
        [2026] = (Ranges(2026, "01-01/01-03", "02-15/02-23", "04-04/04-06", "05-01/05-05", "06-19/06-21", "09-25/09-27", "10-01/10-07"),
            Dates(2026, "01-04", "02-14", "02-28", "05-09", "09-20", "10-10"))
    };

    private static HashSet<DateOnly> Dates(int year, params string[] dates) =>
        dates.Select(d => DateOnly.ParseExact($"{year}-{d}", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private static HashSet<DateOnly> Ranges(int year, params string[] ranges)
    {
        var dates = new HashSet<DateOnly>();
        foreach (var range in ranges) {
            var ends = range.Split('/');
            var start = Dates(year, ends[0]).Single();
            var end = Dates(year, ends[1]).Single();
            for (var day = start; day <= end; day = day.AddDays(1)) dates.Add(day);
        }
        return dates;
    }

    public static bool IsWorkday(DateOnly date)
    {
        if (!Years.TryGetValue(date.Year, out var year))
            throw new InvalidOperationException($"缺少 {date.Year} 年中国节假日数据，请更新 VMNotify 或改用每天模式。");
        if (year.Workdays.Contains(date)) return true;
        if (year.Holidays.Contains(date)) return false;
        return date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
    }
}
