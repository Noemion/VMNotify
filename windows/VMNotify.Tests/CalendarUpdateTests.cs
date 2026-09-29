using System.Net;
using System.Text.Json;
using VMNotify;

internal static class CalendarUpdateTests
{
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static string Fixture(int year) => JsonSerializer.Serialize(new {
        year, papers = new[] { "https://www.gov.cn/test-notice" }, days = new[] {
            new { name = "测试元旦", date = $"{year}-01-01", isOffDay = true },
            new { name = "测试国庆", date = $"{year}-10-01", isOffDay = true },
            new { name = "测试调休", date = $"{year}-01-02", isOffDay = false },
            new { name = "测试跨年", date = $"{year - 1}-12-31", isOffDay = true }
        }
    });
    private static string Empty(int year) => $$"""{"year":{{year}},"papers":[],"days":[]}""";
    private static void Rejected(string json, int year, string why)
    {
        bool rejected = false;
        try { CalendarYear.Parse(json, year); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException) { rejected = true; }
        Check(rejected, why);
    }

    public static async Task Run()
    {
        var legacy = JsonSerializer.Deserialize<Settings>("{}")!;
        Check(!legacy.CalendarAutoUpdate && legacy.CalendarUpdateDay == 1 && legacy.CalendarUpdateTime == "09:00", "calendar legacy defaults");
        var settings = legacy with { CalendarAutoUpdate = true, CalendarUpdateDay = 15, CalendarUpdateTime = "09:30" };
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Check(restored.CalendarAutoUpdate && restored.CalendarUpdateDay == 15 && restored.CalendarUpdateTime == "09:30", "calendar settings roundtrip");
        DateTime today = new(2027, 9, 15, 9, 30, 0);
        var state = new CalendarUpdateState();
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddMinutes(-1)) == today, "before start time");
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddHours(3)) == today, "late startup attempts now");
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddDays(-1)) == today, "before selected day");
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddDays(1)) == today.AddMonths(1), "no catchup on another day");
        Check(CalendarUpdateSchedule.NextAttempt(legacy, state, today) == null, "disabled schedule");
        state = state with { LastAttempt = today.AddMinutes(7) };
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddMinutes(20)) == today.AddMinutes(67), "retry a full hour after failed attempt");
        state = state with { LastSuccess = today.AddMinutes(8) };
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.AddHours(2)) == today.AddMonths(1), "success stops this date");
        state = new() { LastAttempt = today.Date.AddHours(23.5) };
        Check(CalendarUpdateSchedule.NextAttempt(settings, state, today.Date.AddHours(23.8)) == today.AddMonths(1), "no retry past midnight");
        var monthEnd = settings with { CalendarUpdateDay = 31, CalendarUpdateTime = "23:00" };
        Check(CalendarUpdateSchedule.NextAttempt(monthEnd, new(), new(2027, 2, 1)) == new DateTime(2027, 2, 28, 23, 0, 0), "short month end");
        Check(CalendarUpdateSchedule.NextAttempt(monthEnd, new(), new(2028, 2, 1)) == new DateTime(2028, 2, 29, 23, 0, 0), "leap month end");
        Check(CalendarUpdateSchedule.NextAttempt(monthEnd, new() { LastSuccess = new(2027, 12, 31) }, new(2027, 12, 31)) == new DateTime(2028, 1, 31, 23, 0, 0), "next month year rollover");
        foreach (var (day, time) in new[] { (0, "09:00"), (32, "09:00"), (1, "24:00"), (1, "9:00") }) {
            bool rejected = false;
            try { CalendarUpdateSchedule.Validate(day, time); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "reject invalid schedule");
        }
        Check(CalendarYear.Parse(Empty(2028), 2028) == null, "next year placeholder is not coverage");
        Rejected(Fixture(2027), 2028, "wrong year");
        Rejected(Fixture(2027).Replace("2027-01-02", "2027-01-01"), 2027, "duplicate dates");
        Rejected(Fixture(2027).Replace("2027-01-02", "2027-02-30"), 2027, "invalid date");
        Rejected(Fixture(2027).Replace("2026-12-31", "2026-11-30"), 2027, "outside previous December");
        Rejected(Fixture(2027).Replace("www.gov.cn", "example.com"), 2027, "invalid paper source");
        Rejected(Fixture(2027).Replace("2027-10-01", "2027-09-30"), 2027, "incomplete year");
        Rejected(Fixture(2027).Replace("true", "\"true\""), 2027, "boolean type");

        var folder = Path.Combine(Path.GetTempPath(), "vmnotify-calendar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "calendar.json");
        var current = today;
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var updater = new CalendarUpdates(client, path, () => current);
        try {
            handler.Respond = year => new(HttpStatusCode.OK) { Content = new StringContent(year == 2027 ? Fixture(year) : Empty(year)) };
            await updater.Update();
            Check(updater.State.LastSuccess == today && handler.Calls == 2, "current year plus unpublished next succeeds");
            Check(updater.State.Message.Contains("尚未公布 2028") && !ChinaWorkCalendar.SupportedYears.Contains(2028), "placeholder status and coverage");
            Check(!ChinaWorkCalendar.IsWorkday(new(2027, 1, 1)) && ChinaWorkCalendar.IsWorkday(new(2027, 1, 2)), "downloaded holiday and weekend workday");
            Check(!ChinaWorkCalendar.IsWorkday(new(2026, 12, 31)), "following notice overrides previous December");
            Check(DailySchedule.Parse("22:00", "07:00").WindowStart(new(2027, 1, 1, 6, 0, 0), ScheduleDays.ChinaWorkdays) == null, "download affects overnight schedule");
            var restarted = new CalendarUpdates(client, path, () => current);
            restarted.Load();
            Check(restarted.State.LastSuccess == today && restarted.State.LastAttempt == today && ChinaWorkCalendar.SupportedYears.Contains(2027), "restart persists data and successful status");

            current = today.AddMonths(1);
            handler.Respond = _ => new(HttpStatusCode.ServiceUnavailable);
            await updater.Update();
            Check(updater.State.LastSuccess == today && updater.State.Message.Contains("更新失败") && ChinaWorkCalendar.SupportedYears.Contains(2027), "network failure keeps last good data");
            restarted.Load();
            Check(restarted.State.LastAttempt == current && CalendarUpdateSchedule.NextAttempt(settings, restarted.State, current) == current.AddHours(1), "restart suppresses immediate failed retry");

            handler.Respond = year => new(HttpStatusCode.OK) { Content = new StringContent(year == 2027 ? Fixture(year) : "invalid-json") };
            await updater.Update();
            Check(updater.State.LastSuccess == today && updater.State.Message.Contains("更新失败"), "malformed next year fails whole attempt");
            handler.Respond = year => year == 2028 ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent(Fixture(year)) };
            await updater.Update();
            Check(updater.State.LastSuccess == current && !updater.State.Documents.ContainsKey(2028), "404 next year is unpublished");
            handler.Respond = _ => new(HttpStatusCode.NotFound);
            current = current.AddDays(1);
            await updater.Update();
            Check(updater.State.LastSuccess != current && updater.State.Message.Contains("更新失败"), "404 current year is failure");
            handler.Respond = year => new(HttpStatusCode.OK) { Content = new StringContent(Empty(year)) };
            await updater.Update();
            Check(updater.State.LastSuccess != current, "empty current year is failure");
            handler.Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 300 * 1024)) };
            await updater.Update();
            Check(updater.State.LastSuccess != current, "oversized payload rejected");

            handler.Respond = year => new(HttpStatusCode.OK) { Content = new StringContent(Fixture(year)) };
            await updater.Update();
            Check(updater.State.LastSuccess == current && ChinaWorkCalendar.SupportedYears.Contains(2028), "new year activates without software upgrade");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Block = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
            var first = updater.Update();
            await entered.Task;
            int calls = handler.Calls;
            await updater.Update();
            Check(handler.Calls == calls, "manual and automatic requests do not overlap");
            release.SetResult(); await first; handler.Block = null;

            // Simulate shutdown cancellation after recording the attempt, then reload it.
            using var cancel = new CancellationTokenSource();
            handler.Block = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            current = current.AddDays(1);
            await updater.Update(cancel.Token);
            restarted.Load();
            Check(restarted.State.LastAttempt == current && restarted.State.LastSuccess != current, "cancelled attempt persists and does not succeed");
            handler.Block = null;

            // A failed final atomic write must not activate the downloaded calendar.
            string blockedPath = Path.Combine(folder, "blocked.json");
            var blocked = new CalendarUpdates(client, blockedPath, () => current);
            handler.Respond = year => {
                if (year == 2028) Directory.CreateDirectory(blockedPath + ".tmp");
                return new(HttpStatusCode.OK) { Content = new StringContent(Fixture(year).Replace("2027-01-02", "2027-01-03")) };
            };
            await blocked.Update();
            Check(blocked.State.LastSuccess == null && ChinaWorkCalendar.IsWorkday(new(2027, 1, 2)), "write failure preserves active snapshot");
            Directory.Delete(blockedPath + ".tmp");

            ChinaWorkCalendar.UseDownloaded(new Dictionary<int, CalendarYear>());
            File.WriteAllText(path, "{broken"); restarted.Load();
            Check(restarted.State.Message.Contains("缓存读取失败") && !ChinaWorkCalendar.SupportedYears.Contains(2027), "corrupt cache falls back without crashing");
            File.WriteAllText(path, "{\"Documents\":null}"); restarted.Load();
            Check(restarted.State.Message.Contains("缓存读取失败"), "null documents cache rejected");
        } finally {
            ChinaWorkCalendar.UseDownloaded(new Dictionary<int, CalendarYear>());
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine("PASS: calendar validation, offline cache, updates, publication status, atomic activation, failure/cancellation, concurrency, monthly retries and restart persistence.");
    }

    public static async Task Live()
    {
        // Read-only smoke check; independent of local settings/cache and not required by CI.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (int year in new[] { 2025, 2026 }) {
            var calendar = CalendarYear.Parse(await client.GetStringAsync(CalendarUpdates.Source + year + ".json"), year)!;
            for (var day = new DateOnly(year, 1, 1); day.Year == year; day = day.AddDays(1)) {
                bool actual = calendar.Days.TryGetValue(day, out var off) ? !off : day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
                Check(actual == ChinaWorkCalendar.IsWorkday(day), "live source disagrees with bundled date " + day);
            }
            Console.WriteLine($"PASS: live {year} source validates and agrees with bundled workday rules for every day.");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<int, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.NotFound);
        public Func<CancellationToken, Task>? Block { get; set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Block != null) await Block(cancellationToken);
            return Respond(int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath)));
        }
    }
}
