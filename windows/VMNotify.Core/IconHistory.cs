using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VMNotify;

public sealed record RecordedIcon(string Scope, string AppId, string Fingerprint, string? IconName, bool? Colorful, TrayImage Image, DateTimeOffset FirstSeen);

public sealed class IconHistory
{
    private readonly List<RecordedIcon> entries = [];
    public long Revision { get; private set; }
    public static string Scope(Settings settings) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Host.ToLowerInvariant()}\n{settings.Port}\n{settings.User}")));
    public RecordedIcon[] ForApp(string scope, string id) => entries.Where(i => i.Scope == scope && i.AppId == id).ToArray();
    public RecordedIcon[] Snapshot() => entries.ToArray();
    public void Record(string scope, string id, IconObservation icon)
    {
        if (icon.Removed || icon.Preview == null || string.IsNullOrEmpty(icon.Fingerprint)) return;
        var index = entries.FindIndex(i => i.Scope == scope && i.AppId == id && i.Fingerprint == icon.Fingerprint);
        if (index >= 0) {
            var previous = entries[index];
            if (previous.Image == icon.Preview && previous.IconName == icon.IconName && previous.Colorful == icon.Colorful) return;
            entries[index] = previous with { Image = icon.Preview, IconName = icon.IconName, Colorful = icon.Colorful };
        } else {
            while (entries.Count(i => i.Scope == scope && i.AppId == id) >= 16)
                entries.RemoveAt(entries.FindIndex(i => i.Scope == scope && i.AppId == id));
            entries.Add(new(scope, id, icon.Fingerprint, icon.IconName, icon.Colorful, icon.Preview, DateTimeOffset.UtcNow));
            if (entries.Count > 512) entries.RemoveAt(0);
        }
        Revision++;
    }
    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("图标历史文件过大。");
        var loaded = JsonSerializer.Deserialize<RecordedIcon[]>(File.ReadAllText(path)) ?? [];
        if (loaded.Length > 512) throw new InvalidDataException("图标历史数量过多。");
        foreach (var item in loaded) {
            if (item.Scope.Length != 64 || !item.Scope.All(Uri.IsHexDigit) || item.AppId.Length is < 1 or > 64
                || item.Fingerprint.Length is < 1 or > 128 || item.Fingerprint.Any(char.IsControl)
                || item.IconName?.Length > 1024 || item.IconName?.Any(char.IsControl) == true)
                throw new InvalidDataException("无效的图标历史。");
            item.Image.Validate();
        }
        entries.Clear();
        foreach (var item in loaded.GroupBy(i => (i.Scope, i.AppId)).SelectMany(g => g.DistinctBy(i => i.Fingerprint).TakeLast(16))) entries.Add(item);
    }
    public static void Save(string path, RecordedIcon[] snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot));
        File.Move(path + ".tmp", path, true);
    }
}
