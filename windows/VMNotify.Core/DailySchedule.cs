using System.Globalization;

namespace VMNotify;

public sealed record DailySchedule(TimeOnly Start, TimeOnly End)
{
    public static DailySchedule Parse(string start, string end)
    {
        if (!TimeOnly.TryParseExact(start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !TimeOnly.TryParseExact(end, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            throw new ArgumentException("连接和断开时间请使用 HH:mm 格式，例如 09:00。");
        if (from == to) throw new ArgumentException("连接时间与断开时间不能相同。");
        return new(from, to);
    }

    // A window identifier also lets manual disconnect pause only the current period.
    public DateTime? WindowStart(DateTime localNow)
    {
        var time = TimeOnly.FromDateTime(localNow);
        if (Start < End) return time >= Start && time < End ? localNow.Date + Start.ToTimeSpan() : null;
        if (time >= Start) return localNow.Date + Start.ToTimeSpan();
        return time < End ? localNow.Date.AddDays(-1) + Start.ToTimeSpan() : null;
    }
}
