using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VMNotify;

public record AvailableApp(string Id, string Name, bool Running, string Adapter, string Capability)
{
    public override string ToString() => $"{Name} — {(Running ? "正在监听" : "已安装，未运行")}（仅提醒）";
}

public record AgentEvent(string Kind, string? AppId, string? AppName, AvailableApp[]? Apps = null)
{
    public static AgentEvent Parse(string line)
    {
        if (line.Length > 32768) throw new InvalidDataException("事件过长");
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.GetProperty("v").GetInt32() != 1) throw new InvalidDataException("不支持的协议版本");
        var kind = root.GetProperty("kind").GetString() ?? "";
        if (kind is not ("ready" or "heartbeat" or "attention" or "cleared" or "degraded" or "apps"))
            throw new InvalidDataException("未知事件类型");
        string? id = null, name = null;
        if (kind is "attention" or "cleared")
        {
            id = root.GetProperty("app_id").GetString();
            name = root.GetProperty("app_name").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || "-_.".Contains(c))
                || string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl))
                throw new InvalidDataException("无效的应用信息");
        }
        AvailableApp[]? apps = null;
        if (kind == "apps") {
            var array = root.GetProperty("apps");
            if (array.GetArrayLength() > 64) throw new InvalidDataException("应用列表过长");
            apps = array.EnumerateArray().Select(item => {
                var appId = item.GetProperty("id").GetString() ?? "";
                var appName = item.GetProperty("name").GetString() ?? "";
                var adapter = item.GetProperty("adapter").GetString() ?? "";
                var capability = item.GetProperty("capability").GetString() ?? "";
                if (appId.Length is < 1 or > 64 || !appId.All(c => char.IsAsciiLetterOrDigit(c) || "-_.".Contains(c))
                    || appName.Length is < 1 or > 128 || appName.Any(char.IsControl)
                    || adapter.Length > 64 || capability != "attention-only") throw new InvalidDataException("无效的应用列表");
                return new AvailableApp(appId, appName, item.GetProperty("running").GetBoolean(), adapter, capability);
            }).ToArray();
            if (apps.Select(a => a.Id).Distinct().Count() != apps.Length) throw new InvalidDataException("重复的应用 ID");
        }
        return new(kind, id, name, apps);
    }
}

public static class Protocol
{
    public static async IAsyncEnumerable<string> Lines(TextReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new char[512];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else { if (line.Length >= 32768) throw new InvalidDataException("事件流超过长度限制"); line.Append(buffer[i]); }
            }
        if (line.Length != 0) throw new InvalidDataException("事件流在一行中间断开");
    }
}
