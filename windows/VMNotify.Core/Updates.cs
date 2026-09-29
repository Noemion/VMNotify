using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VMNotify;

public sealed record ReleaseUpdate(string Version, string FileName, string Url, string Sha256, long Size);
public sealed record DownloadedUpdate(string Version, string FileName, string Sha256, long Size);

public sealed class Updates(HttpClient client, string directory)
{
    public const string RepositoryUrl = "https://github.com/Noemion/VMNotify";
    public const string LatestUrl = "https://api.github.com/repos/Noemion/VMNotify/releases/latest";
    private const long MaximumSize = 512L * 1024 * 1024;
    private static readonly Regex FilePattern = new(@"\AVMNotify-\d+\.\d+\.\d+-win-(x86|x64|arm64)-setup\.exe\z");
    public static Version ParseVersion(string text)
    {
        text = text.Split('+')[0].TrimStart('v');
        if (!Regex.IsMatch(text, @"\A\d+\.\d+\.\d+\z")) throw new InvalidDataException("更新版本号格式不受支持。");
        return Version.Parse(text);
    }

    public static ReleaseUpdate? ParseRelease(string json, string currentVersion, string architecture)
    {
        if (architecture is not ("x86" or "x64" or "arm64")) throw new ArgumentException("不支持的架构");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = ParseVersion(root.GetProperty("tag_name").GetString()!);
        if (version <= ParseVersion(currentVersion)) return null;
        string name = $"VMNotify-{version}-win-{architecture}-setup.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var url = asset.GetProperty("browser_download_url").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
                || !uri.AbsolutePath.StartsWith("/Noemion/VMNotify/releases/download/", StringComparison.Ordinal)
                || uri.UserInfo.Length != 0) throw new InvalidDataException("安装包下载来源无效。");
            string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            if (!Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z")) throw new InvalidDataException("发布包缺少 SHA-256 校验信息，请在 GitHub 查看发布详情。");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumSize) throw new InvalidDataException("安装包大小无效。");
            return new(version.ToString(), name, url, digest[7..], size);
        }
        throw new InvalidDataException($"发现版本 {version}，但尚未上传 {architecture} 安装包。");
    }

    public async Task<ReleaseUpdate?> Check(string currentVersion, string architecture, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.ParseAdd("VMNotify/" + currentVersion.Split('+')[0]);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("仓库暂无可访问的正式发布版本。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub 暂时限制请求，请稍后重试。");
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(token), currentVersion, architecture);
    }

    private string LocalPath(string name)
    {
        if (!FilePattern.IsMatch(name)) throw new InvalidDataException("无效的安装包文件名。");
        var folder = Path.GetFullPath(directory);
        if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("更新目录不能是链接。");
        var path = Path.Combine(folder, name);
        foreach (var candidate in new[] { path, path + ".json", path + ".partial" })
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新文件不能是链接。");
        return path;
    }

    public IReadOnlyList<DownloadedUpdate> Downloads()
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<DownloadedUpdate>();
        foreach (var file in Directory.EnumerateFiles(directory, "VMNotify-*-setup.exe.json"))
        {
            try
            {
                string name = Path.GetFileName(file)[..^5];
                var path = LocalPath(name);
                var item = JsonSerializer.Deserialize<DownloadedUpdate>(File.ReadAllText(path + ".json"));
                if (item != null && item.FileName == name && File.Exists(path)) result.Add(item);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return result.OrderByDescending(x => x.Version, StringComparer.Ordinal).ToArray();
    }

    public async Task<DownloadedUpdate> Download(ReleaseUpdate release, IProgress<int> progress, CancellationToken token)
    {
        string path = LocalPath(release.FileName);
        Directory.CreateDirectory(directory);
        string temporary = path + ".partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.Url);
            request.Headers.UserAgent.ParseAdd("VMNotify");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > release.Size || total > MaximumSize) throw new InvalidDataException("安装包大小超出预期。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress.Report((int)(total * 100 / release.Size));
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，已丢弃下载文件。");
            }
            var item = new DownloadedUpdate(release.Version, release.FileName, release.Sha256, release.Size);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(item), token);
            return item;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> Verify(DownloadedUpdate item, CancellationToken token)
    {
        string path = LocalPath(item.FileName);
        await using var stream = File.OpenRead(path);
        if (stream.Length != item.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包校验失败，请重试“下载并升级”。");
        return path;
    }

    public void Delete(DownloadedUpdate item)
    {
        string path = LocalPath(item.FileName);
        File.Delete(path);
        File.Delete(path + ".json");
    }

    public (int Deleted, int Failed) DeleteAll()
    {
        if (!Directory.Exists(directory)) return (0, 0);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("更新目录不能是链接。");
        int deleted = 0, failed = 0;
        foreach (var file in Directory.GetFiles(directory)) {
            var name = Path.GetFileName(file);
            var baseName = name.EndsWith(".partial", StringComparison.Ordinal) ? name[..^8]
                : name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name;
            if (!FilePattern.IsMatch(baseName)) continue;
            try {
                _ = LocalPath(baseName);
                File.Delete(file);
                deleted++;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return (deleted, failed);
    }
}
