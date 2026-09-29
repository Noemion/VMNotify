using System.Net;
using System.Security.Cryptography;
using System.Text;
using VMNotify;

internal static class UpdateTests
{
    public static async Task Live()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var folder = Path.Combine(Path.GetTempPath(), "VMNotify-web-update-" + Guid.NewGuid().ToString("N"));
        var updates = new Updates(client, folder);
        foreach (var architecture in new[] { "x86", "x64", "arm64" }) {
            var release = await updates.Check("0.0.0", architecture, default) ?? throw new Exception("Missing live release");
            Console.WriteLine($"PASS: public web release {release.Version}, {architecture}, {release.Size} bytes");
            Check(await updates.Check(release.Version, architecture, default) == null);
            if (architecture == "x64") {
                try {
                    var downloaded = await updates.Download(release, new Progress<int>(), default);
                    _ = await updates.Verify(downloaded, default);
                    updates.Delete(downloaded);
                    Console.WriteLine("PASS: public installer download and SHA-256 verification (not executed)");
                } finally { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            }
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static void Check(bool value) { if (!value) throw new Exception("Update regression failed"); }
    public static async Task Run()
    {
        byte[] content = Encoding.UTF8.GetBytes("installer test payload");
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string name = "VMNotify-0.2.0-win-x64-setup.exe";
        {
            int calls = 0;
            using var fallbackClient = new HttpClient(new Handler(request => {
                calls++;
                Check(request.RequestUri!.Host == "github.com" && request.Headers.Authorization == null);
                if (request.RequestUri.AbsolutePath.EndsWith("/releases/latest")) return new(HttpStatusCode.OK) {
                    RequestMessage = new(HttpMethod.Get, Updates.RepositoryUrl + "/releases/tag/v0.2.0"), Content = new StringContent("") };
                if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS-windows.txt")) return new(HttpStatusCode.OK) { Content = new StringContent(hash + "  " + name + "\r\n") };
                Check(request.Method == HttpMethod.Head && request.RequestUri.AbsolutePath.EndsWith(name));
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            }));
            var fallback = new Updates(fallbackClient, "unused");
            var found = await fallback.Check("0.1.1", "x64", default);
            Check(found?.Sha256 == hash && found.Size == content.Length && calls == 3);
            calls = 0;
            Check(await fallback.Check("0.2.0", "x64", default) == null && calls == 1);
        }
        foreach (var broken in new[] { "foreign", "prerelease", "missing-hash", "duplicate-hash", "missing-size", "oversized" }) {
            using var badClient = new HttpClient(new Handler(request => {
                Check(request.RequestUri!.Host == "github.com");
                if (request.RequestUri.AbsolutePath.EndsWith("/releases/latest")) return new(HttpStatusCode.OK) {
                    RequestMessage = new(HttpMethod.Get, broken == "foreign" ? "https://example.com/Noemion/VMNotify/releases/tag/v0.2.0"
                        : Updates.RepositoryUrl + "/releases/tag/v0.2.0" + (broken == "prerelease" ? "-beta" : "")), Content = new StringContent("") };
                if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS-windows.txt")) return new(HttpStatusCode.OK) {
                    Content = new StringContent(broken == "oversized" ? new string('x', 65537) : broken == "missing-hash" ? ""
                        : hash + "  " + name + "\n" + (broken == "duplicate-hash" ? hash + "  " + name + "\n" : "")) };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            }));
            bool failed = false;
            try { await new Updates(badClient, "unused").Check("0.1.1", "x64", default); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { failed = true; }
            Check(failed);
        }
        var release = new ReleaseUpdate("0.2.0", name, Updates.RepositoryUrl + "/releases/download/v0.2.0/" + name, hash, content.Length);
        string folder = Path.Combine(Path.GetTempPath(), "VMNotify-update-test-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        var service = new Updates(client, folder);
        try
        {
            var item = await service.Download(release, new Progress<int>(), default);
            Check(service.Downloads().Count == 1);
            Check(File.Exists(await service.Verify(item, default)));
            await File.WriteAllTextAsync(Path.Combine(folder, name), "tampered");
            bool rejected = false;
            try { await service.Verify(item, default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            service.Delete(item);
            Check(service.Downloads().Count == 0);
            rejected = false;
            try { await service.Download(release with { Sha256 = new string('0', 64) }, new Progress<int>(), default); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(folder, name + ".partial")));
            using var missing = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            rejected = false;
            try { await new Updates(missing, folder).Check("0.1.1", "x64", default); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            await service.Download(release, new Progress<int>(), default);
            await File.WriteAllTextAsync(Path.Combine(folder, "VMNotify-0.1.0-win-x86-setup.exe.partial"), "partial");
            await File.WriteAllTextAsync(Path.Combine(folder, "VMNotify-0.1.0-win-arm64-setup.exe.json"), "broken metadata");
            await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "keep");
            var cleaned = service.DeleteAll();
            Check(cleaned.Deleted == 4 && cleaned.Failed == 0 && service.Downloads().Count == 0);
            Check(File.Exists(Path.Combine(folder, "settings.json")));
            Check(service.DeleteAll() == (0, 0));
            await service.Download(release, new Progress<int>(), default);
            using (var locked = new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.None)) {
                cleaned = service.DeleteAll();
                Check(cleaned.Failed == 1 && File.Exists(Path.Combine(folder, name)));
            }
            Check(service.DeleteAll() == (1, 0));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
        Console.WriteLine("PASS: version selection, source validation, download integrity, tamper rejection, cleanup and missing release.");
    }
}
