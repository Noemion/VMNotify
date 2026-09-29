using System.Text.Json;
using VMNotify;

internal static class CatalogTests
{
    public static void Run()
    {
        var rule = new NotificationRule { Condition = RuleCondition.Grayscale };
        var settings = new Settings { EnabledApps = ["vpn"], Rules = new() { ["vpn"] = rule } };
        AvailableApp[] apps = [new("vpn", "aTrustTray2", true, "", ""), new("chat", "蓝信", true, "", "")];
        if (AppCatalog.Filter(apps, settings, " ATRUST ", false).Single().Id != "vpn") throw new Exception("Name search failed");
        settings = AppCatalog.ToggleIgnored(settings, "vpn", "aTrustTray2");
        if (settings.EnabledApps.Length != 0 || AppCatalog.Filter(apps, settings, "", false).Single().Id != "chat") throw new Exception("Ignored app must hide and stop notifications");
        settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        if (AppCatalog.Filter([], settings, "atrust", true).Single().Name != "aTrustTray2") throw new Exception("Ignored list must persist while disconnected");
        settings = AppCatalog.ToggleIgnored(settings, "vpn", "aTrustTray2");
        if (settings.EnabledApps.Length != 0 || settings.IgnoredApps.Count != 0 || settings.Rules["vpn"].Condition != rule.Condition)
            throw new Exception("Restore must preserve rules without enabling notifications");
        if (AppCatalog.Filter(apps, settings, "missing", false).Length != 0) throw new Exception("Search empty state failed");
        Console.WriteLine("PASS: case-insensitive app search, ignore/restore, notification opt-out, persisted offline list and preserved rules.");
    }
}
