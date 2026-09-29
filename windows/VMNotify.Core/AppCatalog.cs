namespace VMNotify;

public static class AppCatalog
{
    public static AvailableApp[] Filter(AvailableApp[] apps, Settings settings, string search, bool ignored)
    {
        IEnumerable<AvailableApp> source = ignored
            ? settings.IgnoredApps.Select(p => apps.FirstOrDefault(a => a.Id == p.Key) ?? new(p.Key, p.Value, false, "", ""))
            : apps.Where(a => !settings.IgnoredApps.ContainsKey(a.Id));
        search = search.Trim();
        return source.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static Settings ToggleIgnored(Settings settings, string id, string name)
    {
        var ignored = new Dictionary<string, string>(settings.IgnoredApps);
        if (!ignored.Remove(id)) ignored[id] = name;
        // Restoring leaves forwarding off. Rules remain available for the next explicit opt-in.
        return settings with { IgnoredApps = ignored, EnabledApps = settings.EnabledApps.Where(a => a != id).ToArray() };
    }
}
