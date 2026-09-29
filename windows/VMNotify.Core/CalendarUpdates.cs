using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace VMNotify;

public sealed record CalendarYear(int Year, IReadOnlyDictionary<DateOnly, bool> Days)
{
    public static CalendarYear? Parse(string json, int expectedYear)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (expectedYear is < 2000 or > 9998 || root.GetProperty("year").GetInt32() != expectedYear)
            throw new InvalidDataException("日历年份不匹配。");
        var papers = root.GetProperty("papers");
        var entries = root.GetProperty("days");
        if (papers.ValueKind != JsonValueKind.Array || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 400)
            throw new InvalidDataException("日历格式无效。");
        // The upstream publishes an empty next-year placeholder before the annual notice.
        if (papers.GetArrayLength() == 0 && entries.GetArrayLength() == 0) return null;
        if (papers.GetArrayLength() == 0 || entries.GetArrayLength() == 0)
            throw new InvalidDataException("日历缺少公告来源或日期。");
        foreach (var paper in papers.EnumerateArray()) {
            if (!Uri.TryCreate(paper.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || !(uri.Host == "gov.cn" || uri.Host.EndsWith(".gov.cn", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("日历公告来源无效。");
        }
        var days = new Dictionary<DateOnly, bool>();
        foreach (var entry in entries.EnumerateArray()) {
            if (!DateOnly.TryParseExact(entry.GetProperty("date").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !(date.Year == expectedYear || (date.Year == expectedYear - 1 && date.Month == 12))
                || string.IsNullOrWhiteSpace(entry.GetProperty("name").GetString())
                || !days.TryAdd(date, entry.GetProperty("isOffDay").GetBoolean()))
                throw new InvalidDataException("日历包含无效、重复或越界日期。");
        }
        // Reject obviously incomplete annual documents, even when the JSON is valid.
        if (!days.Any(d => d.Key.Year == expectedYear && d.Key.Month == 1 && d.Value)
            || !days.Any(d => d.Key.Year == expectedYear && d.Key.Month == 10 && d.Value))
            throw new InvalidDataException("日历缺少完整年度安排。");
        return new(expectedYear, new ReadOnlyDictionary<DateOnly, bool>(days));
    }
}

public sealed record CalendarUpdateState
{
    public Dictionary<int, string> Documents { get; init; } = new();
    public DateTime? LastAttempt { get; init; }
    public DateTime? LastSuccess { get; init; }
    public string Message { get; init; } = "尚未更新，正在使用内置日历。";
}

public static class CalendarUpdateSchedule
{
    public static TimeOnly Validate(int day, string time)
    {
        if (day is < 1 or > 31) throw new ArgumentException("每月更新日期必须在 1–31 日之间。");
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new ArgumentException("日历更新开始时间请使用 HH:mm 格式，例如 09:00。");
        return parsed;
    }

    public static DateTime? NextAttempt(Settings settings, CalendarUpdateState state, DateTime now)
    {
        if (!settings.CalendarAutoUpdate) return null;
        var time = Validate(settings.CalendarUpdateDay, settings.CalendarUpdateTime);
        DateTime InMonth(DateTime month) => new DateTime(month.Year, month.Month,
            Math.Min(settings.CalendarUpdateDay, DateTime.DaysInMonth(month.Year, month.Month))) + time.ToTimeSpan();
        var start = InMonth(now);
        if (now.Date > start.Date || state.LastSuccess?.Date == start.Date) return InMonth(now.AddMonths(1));
        if (now.Date < start.Date) return start;
        var next = state.LastAttempt is { } attempt && attempt.Date == now.Date && attempt.AddHours(1) > start ? attempt.AddHours(1) : start;
        return next.Date == start.Date ? next : InMonth(now.AddMonths(1));
    }
}

public sealed class CalendarUpdates(HttpClient client, string cacheFile, Func<DateTime>? clock = null)
{
    public const string Source = "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/";
    private const int MaximumBytes = 256 * 1024;
    private readonly Func<DateTime> now = clock ?? (() => DateTime.Now);
    private readonly SemaphoreSlim gate = new(1, 1);
    public CalendarUpdateState State { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(cacheFile)) return;
        try {
            if (new FileInfo(cacheFile).Length > 4 * 1024 * 1024) throw new InvalidDataException("缓存过大。");
            var state = JsonSerializer.Deserialize<CalendarUpdateState>(File.ReadAllText(cacheFile)) ?? throw new InvalidDataException("缓存为空。");
            var parsed = ParseDocuments(state.Documents);
            State = state;
            ChinaWorkCalendar.UseDownloaded(parsed);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) {
            State = new() { Message = "日历缓存读取失败，使用内置日历：" + ex.Message };
        }
    }

    private static Dictionary<int, CalendarYear> ParseDocuments(Dictionary<int, string> documents)
    {
        if (documents == null || documents.Count > 20) throw new InvalidDataException("日历缓存格式无效。");
        return documents.ToDictionary(p => p.Key, p => CalendarYear.Parse(p.Value, p.Key) ?? throw new InvalidDataException("缓存包含未公布的年份。"));
    }

    private void Save(CalendarUpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
        File.WriteAllText(cacheFile + ".tmp", JsonSerializer.Serialize(state));
        File.Move(cacheFile + ".tmp", cacheFile, overwrite: true);
    }

    private async Task<string?> Fetch(int year, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, Source + year + ".json");
        request.Headers.UserAgent.ParseAdd("VMNotify/1.0");
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("日历下载超过大小限制。");
        await response.Content.LoadIntoBufferAsync(MaximumBytes, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return CalendarYear.Parse(json, year) == null ? null : json;
    }

    public async Task Update(CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0, cancellation)) return;
        try {
            var started = now();
            State = State with { LastAttempt = started, Message = "上次更新未完成，将按计划重试。" };
            Save(State); // Persist before networking so a restart cannot cause an immediate retry loop.
            var current = await Fetch(started.Year, cancellation) ?? throw new InvalidDataException($"数据源尚未提供 {started.Year} 年完整日历。");
            var next = await Fetch(started.Year + 1, cancellation);
            var documents = new Dictionary<int, string>(State.Documents) { [started.Year] = current };
            if (next != null) documents[started.Year + 1] = next;
            // Keep a bounded rolling cache, including the previous year for overnight windows.
            documents = documents.Where(p => p.Key >= started.Year - 2 && p.Key <= started.Year + 1).ToDictionary();
            var parsed = ParseDocuments(documents);
            var completed = State with { Documents = documents, LastSuccess = now(), Message = next != null
                ? $"更新成功，已检查 {started.Year}、{started.Year + 1} 年日历。"
                : $"更新成功，已检查 {started.Year} 年；数据源尚未公布 {started.Year + 1} 年安排" + (documents.ContainsKey(started.Year + 1) ? "，保留该年已有缓存。" : "。") };
            Save(completed);
            State = completed;
            ChinaWorkCalendar.UseDownloaded(parsed);
        } catch (Exception ex) {
            State = State with { Message = "更新失败，保留已有日历：" + (ex is OperationCanceledException ? "请求超时或已取消。" : ex.Message) };
            try { Save(State); }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) {
                State = State with { Message = State.Message + " 更新状态无法保存：" + saveError.Message };
            }
        } finally { gate.Release(); }
    }
}
