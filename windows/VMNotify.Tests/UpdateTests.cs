using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VMNotify;

internal static class UpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static void Check(bool value) { if (!value) throw new Exception("Update regression failed"); }
    public static async Task Run()
    {
        byte[] content = Encoding.UTF8.GetBytes("installer test payload");
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string Json(string name, string digest, string url = "https://github.com/Noemion/VMNotify/releases/download/v0.2.0/installer.exe") => JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.0", draft = false, prerelease = false,
            assets = new[] { new { name, digest, size = content.Length, browser_download_url = url } }
        });
        string name = "VMNotify-0.2.0-win-x64-setup.exe";
        string json = Json(name, "sha256:" + hash);
        var release = Updates.ParseRelease(json, "0.1.1", "x64")!;
        Check(release.FileName == name);
        Check(Updates.ParseRelease(json, "0.2.0", "x64") == null);
        Check(Updates.ParseRelease(json, "0.3.0", "x64") == null);
        foreach (var invalid in new[] { Json(name, ""), Json(name, "sha256:" + hash, "https://example.com/installer.exe"), Json("../bad.exe", "sha256:" + hash) })
        {
            bool rejected = false;
            try { Updates.ParseRelease(invalid, "0.1.1", "x64"); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
        }
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
