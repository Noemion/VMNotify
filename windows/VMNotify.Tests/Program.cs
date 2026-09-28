using VMNotify;

if (args.Length == 4 && args[0] is "--ssh" or "--ssh-muted") {
    bool muted = args[0] == "--ssh-muted";
    var receiver = new Receiver();
    if (muted) receiver.SetEnabledApps([]);
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
            if (found && (muted ? elapsed.Elapsed.TotalSeconds >= 7 : attention)) break;
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
var evt = AgentEvent.Parse("{\"v\":1,\"kind\":\"attention\",\"app_id\":\"lanxin\",\"app_name\":\"蓝信\"}");
Assert(evt.AppName == "蓝信", "UTF-8 name");
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
