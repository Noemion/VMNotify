using System.Text.Json;
using VMNotify;

internal static class TrayImageTests
{
    public static void Run()
    {
        AgentEvent Parse(object? preview) => AgentEvent.Parse(JsonSerializer.Serialize(new { v = 1, kind = "heartbeat", app_id = "vpn", app_name = "aTrustTray2",
            icon = new { instance_id = "instance", fingerprint = "pix-v1-test", icon_name = (string?)null, colorful = false, colors = Array.Empty<string>(), preview } }));
        var ev = Parse(new { width = 2, height = 1, argb_hex = "FFFF00008000FF00" });
        if (!ev.Icon!.Preview!.BgraPixels().SequenceEqual(new byte[] { 0, 0, 255, 255, 0, 255, 0, 128 })) throw new Exception("ARGB conversion must preserve colors and alpha");
        if (Parse(null).Icon!.Preview != null) throw new Exception("Old agents must remain compatible");
        foreach (var bad in new[] { new { width = 33, height = 1, argb_hex = new string('0', 264) }, new { width = 0, height = 1, argb_hex = "" },
            new { width = 1, height = 1, argb_hex = "ffff00" }, new { width = 1, height = 1, argb_hex = "zzzzzzzz" } }) {
            try { Parse(bad); } catch (InvalidDataException) { continue; }
            throw new Exception("Malformed image accepted");
        }
        var clock = new TimingTests.Clock(); var receiver = new Receiver(clock);
        receiver.ApplyObservation(ev);
        if (receiver.GetIcons("vpn").Single().Preview == null) throw new Exception("Disabled applications still need images");
        var changed = ev with { Icon = ev.Icon with { Preview = new(1, 1, "ff808080") } };
        receiver.ApplyObservation(changed);
        if (receiver.GetIcons("vpn").Single().Preview!.ArgbHex != "ff808080") throw new Exception("Image changes must reach the UI");
        clock.Advance(TimeSpan.FromSeconds(9));
        if (receiver.GetIcons("vpn").Length != 0) throw new Exception("Stale images must expire");
        receiver.ApplyObservation(ev); receiver.ApplyObservation(ev with { Icon = ev.Icon with { Removed = true } });
        if (receiver.GetIcons("vpn").Length != 0) throw new Exception("Removed instance image survived");
        Console.WriteLine("PASS: real tray image protocol, color/alpha conversion, malformed rejection, updates, stale/removal and old-agent fallback.");
    }
}
