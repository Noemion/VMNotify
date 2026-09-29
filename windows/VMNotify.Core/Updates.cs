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
    public const string LatestUrl = "https://github.com/Noemion/VMNotify/releases/latest";
    private const long MaximumSize = 512L * 1024 * 1024;
    private static readonly Regex FilePattern = new(@"\AVMNotify-\d+\.\d+\.\d+-win-(x86|x64|arm64)-setup\.exe\z");
    public static Version ParseVersion(string text)
    {
        text = text.Split('+')[0].TrimStart('v');
        if (!Regex.IsMatch(text, @"\A\d+\.\d+\.\d+\z")) throw new InvalidDataException("更新版本号格式不受支持。");
        return Version.Parse(text);
    }

    public async Task<ReleaseUpdate?> Check(string currentVersion, string architecture, CancellationToken token)
    {
        if (architecture is not ("x86" or "x64" or "arm64")) throw new ArgumentException("不支持的架构");
        // GitHub's public latest redirect is independent of the REST API quota and excludes prereleases.
        using var latestRequest = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        latestRequest.Headers.UserAgent.ParseAdd("VMNotify");
        using var latest = await client.SendAsync(latestRequest, HttpCompletionOption.ResponseHeadersRead, token);
        if (latest.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("仓库暂无可访问的正式发布版本。");
        latest.EnsureSuccessStatusCode();
        var uri = latest.RequestMessage?.RequestUri;
        var tag = uri == null ? Match.Empty : Regex.Match(uri.AbsolutePath, @"\A/Noemion/VMNotify/releases/tag/v(\d+\.\d+\.\d+)\z");
        if (uri?.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || !tag.Success)
            throw new InvalidDataException("无法确认 GitHub 正式发布版本，请打开发布页面下载。");
        var version = ParseVersion(tag.Groups[1].Value);
        if (version <= ParseVersion(currentVersion)) return null;
        string name = $"VMNotify-{version}-win-{architecture}-setup.exe";
        string baseUrl = RepositoryUrl + "/releases/download/v" + version + "/";
        using var checksumResponse = await client.GetAsync(baseUrl + "SHA256SUMS-windows.txt", HttpCompletionOption.ResponseHeadersRead, token);
        checksumResponse.EnsureSuccessStatusCode();
        await checksumResponse.Content.LoadIntoBufferAsync(65536, token);
        var checksums = await checksumResponse.Content.ReadAsStringAsync(token);
        var hashes = Regex.Matches(checksums, @"(?m)^([a-fA-F0-9]{64}) [ *]" + Regex.Escape(name) + @"\r?$");
        if (hashes.Count != 1) throw new InvalidDataException("发布包缺少唯一的 SHA-256 校验信息。");
        using var sizeRequest = new HttpRequestMessage(HttpMethod.Head, baseUrl + name);
        using var sizeResponse = await client.SendAsync(sizeRequest, HttpCompletionOption.ResponseHeadersRead, token);
        sizeResponse.EnsureSuccessStatusCode();
        long size = sizeResponse.Content.Headers.ContentLength ?? 0;
        if (size <= 0 || size > MaximumSize) throw new InvalidDataException("安装包大小无效。");
        return new(version.ToString(), name, baseUrl + name, hashes[0].Groups[1].Value, size);
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
