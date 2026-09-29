using VMNotify;

if (args.Length == 1 && args[0] == "--update-web") {
    await UpdateTests.Live();
    return;
}

if (args.Length == 2 && args[0] == "--bundle-files") {
    await BundleTests.VerifyFiles(args[1]);
    return;
}

if (args.Length == 5 && args[0] == "--ssh-bundle") {
    await BundleTests.Integration(args[1], args[2], args[3], args[4]);
    return;
}

if (args.Length == 4 && args[0] == "--ssh-rediscover") {
    await DiscoveryTests.Integration(args[1], args[2], args[3]);
    return;
}

if (args.Length == 5 && args[0] == "--ssh-sampling") {
    var liveSettings = new Settings { Host = args[1], User = args[2], AgentPath = args[3], Rules = new() {
        [args[4]] = new NotificationRule { Condition = RuleCondition.Grayscale, SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60 }
    } };
    var start = new System.Diagnostics.ProcessStartInfo(Receiver.SshPath()) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in liveSettings.SshArguments()) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start)!;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var times = new List<long>();
    try {
        await foreach (var line in Protocol.Lines(process.StandardOutput, deadline.Token)) {
            var sample = AgentEvent.Parse(line);
            if (sample.AppId != args[4] || sample.Icon == null) continue;
            using var data = System.Text.Json.JsonDocument.Parse(line);
            times.Add(data.RootElement.GetProperty("timestamp_ms").GetInt64());
            if (times.Count == 8) break;
        }
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        if (times.Count != 8 || gaps.Average() < 750 || gaps.Average() > 1500 || gaps.Any(g => g < 650 || g > 1800))
            throw new Exception("Unexpected live sampling cadence: " + string.Join(", ", gaps));
        Console.WriteLine("PASS: actual SSH per-app 1000ms sampling; observed intervals (ms): " + string.Join(", ", gaps));
    } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    return;
}

if (args.Length == 2 && args[0] == "--replay-icons") {
    var clock = new TimingTests.Clock();
    var receiver = new Receiver(clock);
    long? previous = null;
    var samples = new List<(long Time, AgentEvent Event)>();
    foreach (var line in File.ReadLines(args[1])) {
        var ev = AgentEvent.Parse(line);
        using var document = System.Text.Json.JsonDocument.Parse(line);
        if (ev.Icon != null) samples.Add((document.RootElement.GetProperty("timestamp_ms").GetInt64(), ev));
    }
    var id = samples.First().Event.AppId!;
    receiver.SetEnabledApps([id]);
    receiver.SetRules(new() { [id] = new NotificationRule { Condition = RuleCondition.Grayscale, SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60, Message = "aTrust 未连接" } });
    int notifications = 0; AgentEvent? notification = null;
    foreach (var (time, ev) in samples) {
        if (previous != null) clock.Advance(TimeSpan.FromMilliseconds(time - previous.Value));
        previous = time;
        receiver.ApplyObservation(ev); receiver.QueueReminders();
        while (receiver.Notifications.Reader.TryRead(out var replayAlert)) {
            if (!receiver.ShouldDisplay(replayAlert)) continue;
            notifications++; notification = replayAlert;
            Console.WriteLine($"NOTIFICATION at {time}: {replayAlert.Message}");
        }
    }
    if (!samples.Any(s => s.Event.Icon!.Colorful == true) || !samples.Any(s => s.Event.Icon!.Colorful == false)) throw new Exception("Need real color and gray samples");
    if (notifications != 1) throw new Exception($"Expected exactly one notification, got {notifications}");
    if (samples.Last().Event.Icon!.Colorful != true || receiver.ShouldDisplay(notification!)) throw new Exception("Need recovery to color and cleared notification");
    Console.WriteLine($"PASS: {samples.Count} real icon samples; grayscale hold triggers once and color recovery clears host notification.");
    return;
}

if (args.Length == 4 && args[0] == "--ssh-authorization") {
    await SshAuthorizationTests.Integration(args[1], args[2], int.Parse(args[3]));
    return;
}

if (args.Length == 4 && args[0] == "--ssh-names") {
    System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    Console.OutputEncoding = System.Text.Encoding.GetEncoding(936);
    var receiver = new Receiver();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var running = receiver.Run(new Settings { Host = args[1], User = args[2], AgentPath = args[3] }, stop.Token);
    bool correct = false;
    try {
        while (!stop.IsCancellationRequested) {
            var app = receiver.AvailableApps.FirstOrDefault(a => a.Id == "lanxin");
            if (app != null) {
                if (app.Name != "蓝信") throw new Exception("SSH UTF-8 application name was corrupted");
                correct = true;
                break;
            }
            await Task.Delay(100, stop.Token);
        }
    } finally { stop.Cancel(); await running; }
    if (!correct) throw new Exception("SSH name discovery failed");
    Console.WriteLine("PASS: actual SSH UTF-8 name with Windows code page 936.");
    return;
}

if (args.Length == 4 && args[0] is "--ssh" or "--ssh-muted" or "--ssh-reminder") {
    bool muted = args[0] == "--ssh-muted";
    var clock = new TimingTests.Clock();
    var receiver = new Receiver(clock);
    receiver.SetEnabledApps(muted ? [] : ["lanxin"]);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var running = receiver.Run(new Settings { Host = args[1], User = args[2], AgentPath = args[3] }, stop.Token);
    bool found = false, attention = false;
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    try {
        while (!stop.IsCancellationRequested) {
            if (!found && receiver.AvailableApps.Length > 0) {
                Console.WriteLine("DISCOVERY: " + string.Join(", ", receiver.AvailableApps.Select(a => a.Id + ":" + a.Running)));
                found = true;
            }
            while (receiver.Notifications.Reader.TryRead(out var ev)) {
                Console.WriteLine("NOTIFICATION: " + ev.AppId); attention = true;
            }
            if (found && (muted ? elapsed.Elapsed.TotalSeconds >= 7 : attention)) {
                if (args[0] == "--ssh-reminder") {
                    clock.Advance(TimeSpan.FromMinutes(3)); receiver.QueueReminders();
                    if (!receiver.Notifications.Reader.TryRead(out var reminder) || reminder.Kind != "reminder" || !receiver.ShouldDisplay(reminder))
                        throw new Exception("Actual SSH attention did not produce timed reminder");
                    Console.WriteLine("PASS: live SSH attention produces three-minute reminder with simulated elapsed time.");
                }
                break;
            }
            await Task.Delay(100, stop.Token);
        }
    } catch (OperationCanceledException) { }
    finally { stop.Cancel(); await running; }
    if (!found || (muted ? attention : !attention)) throw new Exception("SSH integration discovery/selection failed: " + receiver.Status);
    Console.WriteLine("SSH discovery / notification / cancellation integration passed.");
    return;
}

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("invalid input accepted"); }
Assert(new Settings().EnabledApps.Length == 0, "new settings must opt out of all apps");
var defaults = System.Text.Json.JsonSerializer.Deserialize<Settings>("{}");
Assert(defaults!.EnabledApps.Length == 0, "missing app selection must opt out");
var selection = new Receiver();
Assert(!selection.IsEnabled("lanxin"), "receiver must opt out initially");
selection.SetEnabledApps(["lanxin"]);
Assert(selection.IsEnabled("lanxin"), "explicit selection must enable forwarding");
var evt = AgentEvent.Parse("{\"v\":1,\"kind\":\"attention\",\"app_id\":\"lanxin\",\"app_name\":\"蓝信\"}");
Assert(evt.AppName == "蓝信", "UTF-8 name");
var optIn = new Receiver();
optIn.ApplyAttention(evt);
Assert(!optIn.Notifications.Reader.TryRead(out _), "disabled app must remain silent");
optIn.SetEnabledApps(["lanxin"]);
Assert(optIn.Notifications.Reader.TryRead(out var pending) && pending.AppName == "蓝信", "enabling must deliver current attention");
optIn.SetEnabledApps(["lanxin"]);
optIn.ApplyAttention(evt);
Assert(!optIn.Notifications.Reader.TryRead(out _), "same attention must not repeat");
optIn.ApplyAttention(evt with { Kind = "cleared" });
optIn.ApplyAttention(evt);
Assert(optIn.Notifications.Reader.TryRead(out var nextRound) && nextRound.AppId == "lanxin", "a new attention round on the same connection must notify again");
optIn.SetEnabledApps([]);
optIn.ApplyAttention(evt with { Kind = "cleared" });
optIn.SetEnabledApps(["lanxin"]);
Assert(!optIn.Notifications.Reader.TryRead(out _), "cleared attention must not replay");
Reject(() => AgentEvent.Parse("{\"v\":2,\"kind\":\"ready\"}"));
Reject(() => AgentEvent.Parse("{\"v\":1,\"kind\":\"attention\",\"app_id\":\"x\",\"app_name\":\"bad\\nname\"}"));
Reject(() => new Settings { Host = "-oProxyCommand=bad", User = "user" }.Validate());
Reject(() => new Settings { Host = "host", User = "user", Port = 0 }.Validate());
var cfg = new Settings { Host = "192.0.2.1", User = "user", AgentPath = "/tmp/a'b;$(touch nope)" };
Assert(cfg.SshArguments().Last() == "exec '/tmp/a'\"'\"'b;$(touch nope)'", "remote shell quoting");
var lines = new List<string>();
await foreach (var line in Protocol.Lines(new StringReader("one\r\ntwo\n"), default)) lines.Add(line);
Assert(lines.SequenceEqual(["one", "two"]), "line framing");
var inventory = AgentEvent.Parse("{\"v\":1,\"kind\":\"apps\",\"apps\":[{\"id\":\"lanxin\",\"name\":\"蓝信\",\"running\":true,\"adapter\":\"status-notifier-flash\",\"capability\":\"attention-only\"}]}");
Assert(inventory.Apps is { Length: 1 } && inventory.Apps[0].Running, "application discovery");
foreach (var input in new[] { new string('x', 32769), "truncated" }) {
    bool rejected = false;
    try { await foreach (var _ in Protocol.Lines(new StringReader(input), default)) { } }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "unbounded or truncated input accepted");
}
Console.WriteLine("All protocol, framing and SSH argument tests passed.");
await UpdateTests.Run();
TimingTests.Run();
RuleTests.Run();
CalendarTests.Run();
SshAuthorizationTests.Run();
DiscoveryTests.Run();
await BundleTests.Run();
CatalogTests.Run();
TrayImageTests.Run();
IconHistoryTests.Run();
