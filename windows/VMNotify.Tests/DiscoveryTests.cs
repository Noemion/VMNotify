using VMNotify;

internal static class DiscoveryTests
{
    public static void Run()
    {
        foreach (var version in new[] { "0.1.2", "0.1.4" }) {
            var ev = AgentEvent.Parse("vmnotify-agent " + version);
            if (ev.Kind != "agent_info" || ev.AgentVersion != version) throw new Exception("Agent version parsing failed");
        }
        foreach (var value in new[] { "", "0.1", "0.1.4.1", "0.1.4;echo bad" }) {
            try { AgentEvent.Parse("vmnotify-agent " + value); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Malformed version accepted");
        }
        var settings = new Settings { Host = "192.0.2.1", User = "user", AgentPath = "/tmp/a'b;$(touch nope)" };
        var command = settings.SshArguments(includeVersion: true).Last();
        if (command != "'/tmp/a'\"'\"'b;$(touch nope)' --version && " + settings.SshArguments().Last())
            throw new Exception("Version probe must quote the configured path");
        var receiver = new Receiver();
        if (receiver.CanRediscover || receiver.RequestRediscovery()) throw new Exception("Disconnected rediscovery accepted");
        Console.WriteLine("PASS: agent version compatibility, probe quoting and disconnected rediscovery.");
    }

    public static async Task Integration(string host, string user, string path)
    {
        const string id = "auto-fd987f5d56d09045";
        var receiver = new Receiver();
        receiver.SetEnabledApps([id]);
        var settings = new Settings { Host = host, User = user, AgentPath = path, Rules = new() {
            [id] = new NotificationRule { Condition = RuleCondition.Grayscale, SampleMilliseconds = 1000, HoldSeconds = 2, RepeatSeconds = 60 }
        } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var running = receiver.Run(settings, stop.Token);
        async Task WaitFor(Func<bool> condition) {
            while (!condition()) { if (running.IsCompleted) throw new Exception(receiver.Status); await Task.Delay(100, stop.Token); }
        }
        try {
            await WaitFor(() => receiver.CanRediscover && receiver.AvailableApps.Any(a => a.Id == id));
            var version = receiver.AgentVersion;
            if (version == null || Version.Parse(version) < new Version(0, 1, 4)) throw new Exception("Unexpected live agent version");
            if (Version.Parse(version) >= new Version(0, 2, 0))
                await WaitFor(() => receiver.GetIcons(id).Any(i => i.Preview != null) && receiver.GetIcons("lanxin").Any(i => i.Preview != null));
            var revision = receiver.DiscoverySessionRevision;
            if (!receiver.RequestRediscovery() || receiver.RequestRediscovery()) throw new Exception("Rediscovery request/debounce failed");
            await WaitFor(() => receiver.InventoryRevision > revision && receiver.CanRediscover);
            if (!receiver.IsEnabled(id) || !receiver.AvailableApps.Any(a => a.Id == id) || receiver.AgentVersion != version)
                throw new Exception("Rediscovery lost aTrust or app selection");
            Console.WriteLine($"PASS: live agent {version}; aTrust discovered before and after manual rediscovery; selection preserved; image telemetry verified for 0.2.0+. Apps: " + string.Join(", ", receiver.AvailableApps.Select(a => a.Name)));
        } finally { stop.Cancel(); await running; }
    }
}
