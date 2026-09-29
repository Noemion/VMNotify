using VMNotify;

internal static class IconHistoryTests
{
    public static void Run()
    {
        var history = new IconHistory();
        var scope = IconHistory.Scope(new Settings { Host = "vm1", User = "desktop" });
        var other = IconHistory.Scope(new Settings { Host = "vm2", User = "desktop" });
        var gray = new IconObservation("instance", "pix-v1-gray", null, false, ["#808080"], false, false, Preview: new(1, 1, "ff808080"));
        history.Record(scope, "vpn", gray); history.Record(scope, "vpn", gray);
        if (history.ForApp(scope, "vpn").Length != 1 || history.Revision != 1) throw new Exception("Repeated samples must not duplicate history or force disk writes");
        history.Record(scope, "vpn", gray with { Fingerprint = "pix-v1-color", Colorful = true, Preview = new(1, 1, "ff00ff00") });
        if (history.ForApp(scope, "vpn").Length != 2 || history.ForApp(other, "vpn").Length != 0) throw new Exception("History must distinguish states and isolate virtual machines");
        var folder = Path.Combine(Path.GetTempPath(), "VMNotify-icon-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "history.json");
        try {
            IconHistory.Save(path, history.Snapshot());
            var restored = new IconHistory(); restored.Load(path);
            if (restored.ForApp(scope, "vpn").Length != 2 || restored.ForApp(scope, "vpn")[0].Image.ArgbHex != "ff808080") throw new Exception("History must survive restart with original pixels");
            for (int i = 0; i < 30; i++) restored.Record(scope, "vpn", gray with { Fingerprint = "pix-v1-" + i });
            if (restored.ForApp(scope, "vpn").Length != 16) throw new Exception("Animated icon history must stay bounded");
            restored.Record(scope, "vpn", gray with { Removed = true });
            if (restored.ForApp(scope, "vpn").Length != 16) throw new Exception("Removal must not add a historical state");
            File.WriteAllText(path, "invalid json");
            try { new IconHistory().Load(path); throw new Exception("Corrupt history accepted"); } catch (System.Text.Json.JsonException) { }
        } finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS: discovered icon deduplication, color/gray history, persistence, VM isolation and animation bounds.");
    }
}
