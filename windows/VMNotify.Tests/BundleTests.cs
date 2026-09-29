using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VMNotify;

internal static class BundleTests
{
    public static async Task VerifyFiles(string directory)
    {
        var bundle = new AgentBundle(directory);
        foreach (var entry in bundle.ReadManifest()) {
            var bytes = await bundle.ReadVerified(entry, default);
            int machine = entry.Architecture == "x86_64" ? 62 : 183;
            if (bytes.Length < 64 || bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F'
                || bytes[4] != 2 || bytes[5] != 1 || BitConverter.ToUInt16(bytes, 18) != machine)
                throw new Exception("Bundled binary does not match its Linux architecture");
        }
        Console.WriteLine("PASS: both packaged Linux architectures, sizes and SHA-256 digests.");
    }
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VMNotify-bundle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "x86_64"));
        byte[] content = Encoding.UTF8.GetBytes("fixture");
        var entry = new BundledAgent("x86_64", "0.1.4", Convert.ToHexString(SHA256.HashData(content)), content.Length);
        await File.WriteAllBytesAsync(Path.Combine(directory, "x86_64", "vmnotify-agent"), content);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new[] { entry, entry with { Architecture = "aarch64" } }));
        var bundle = new AgentBundle(directory);
        try {
            if (!bundle.Select("vmnotify-bootstrap x86_64 missing").Install || !bundle.Select("vmnotify-bootstrap x86_64 0.1.2").Install
                || bundle.Select("vmnotify-bootstrap x86_64 0.1.4").Install || bundle.Select("vmnotify-bootstrap x86_64 0.2.0").Install
                || bundle.Select("vmnotify-bootstrap aarch64 0.1.2").Agent.Architecture != "aarch64") throw new Exception("Bundle version/architecture selection failed");
            foreach (var input in new[] { "vmnotify-bootstrap i686 missing", "vmnotify-bootstrap x86_64 unknown" }) {
                try { bundle.Select(input); } catch (InvalidDataException) { continue; }
                throw new Exception("Invalid bootstrap accepted");
            }
            if (!(await bundle.ReadVerified(entry, default)).SequenceEqual(content)) throw new Exception("Bundle read failed");
            await File.WriteAllBytesAsync(Path.Combine(directory, "x86_64", "vmnotify-agent"), Encoding.UTF8.GetBytes("changed"));
            try { await bundle.ReadVerified(entry, default); throw new Exception("Corrupt bundle accepted"); } catch (InvalidDataException) { }
            var command = bundle.Command(new Settings { Host = "host", User = "user", AgentPath = "/tmp/a'b;$(touch nope)" });
            if (!command.Contains("target='/tmp/a'\"'\"'b;$(touch nope)'") || command.Contains('\r')) throw new Exception("Bootstrap shell quoting/line endings failed");
        } finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: bundled Linux architecture/version selection, no downgrade, checksum rejection and shell quoting.");
    }

    public static async Task Integration(string host, string user, string directory, string target)
    {
        var settings = new Settings { Host = host, User = user, AgentPath = target };
        var bundle = new AgentBundle(directory);
        async Task<string> Remote(string command) {
            var info = new ProcessStartInfo(Receiver.SshPath()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var arguments = settings.SshArguments(); arguments[^1] = command;
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await error);
            return await output;
        }
        // Caller supplies an isolated fixture path, never the installed monitoring agent.
        if (!target.StartsWith(".local/bin/vmnotify-bundle-test-", StringComparison.Ordinal)) throw new Exception("Need an isolated bundle test target");
        await Remote("test ! -e " + Settings.ShellQuote(target));
        async Task ConnectAndVerify() {
            var receiver = new Receiver { Bundle = bundle };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = receiver.Run(settings, stop.Token);
            try {
                while (!receiver.AvailableApps.Any(a => a.Name == "aTrustTray2")) {
                    await Task.Delay(100, stop.Token);
                }
                if (receiver.AgentVersion != "0.1.4") throw new Exception("Bundle installation version mismatch");
            } finally { stop.Cancel(); await running; }
        }
        try {
            await ConnectAndVerify(); // first install
            var first = await Remote("stat -c %i " + Settings.ShellQuote(target));
            await ConnectAndVerify(); // same version: no replacement
            if (first != await Remote("stat -c %i " + Settings.ShellQuote(target))) throw new Exception("Same version was unnecessarily replaced");
            await Remote("printf '%s\\n' '#!/bin/sh' 'echo vmnotify-agent 0.1.2' > " + Settings.ShellQuote(target + ".fixture") + " && chmod 755 " + Settings.ShellQuote(target + ".fixture") + " && mv -f -- " + Settings.ShellQuote(target + ".fixture") + " " + Settings.ShellQuote(target));
            var corruptInfo = new ProcessStartInfo(Receiver.SshPath()) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var corruptArgs = settings.SshArguments(); corruptArgs[^1] = bundle.Command(settings);
            foreach (var argument in corruptArgs) corruptInfo.ArgumentList.Add(argument);
            using (var corrupt = Process.Start(corruptInfo)!) {
                var error = corrupt.StandardError.ReadToEndAsync();
                var hello = await corrupt.StandardOutput.ReadLineAsync();
                if (hello == null || !bundle.Select(hello).Install) throw new Exception("Need old fixture before corrupt transfer");
                await corrupt.StandardInput.WriteAsync("install\ntruncated binary"); corrupt.StandardInput.Close();
                await corrupt.WaitForExitAsync();
                if (corrupt.ExitCode == 0 || !(await error).Contains("checksum mismatch")) throw new Exception("Corrupt transfer accepted");
            }
            if ((await Remote(Settings.ShellQuote(target) + " --version")).Trim() != "vmnotify-agent 0.1.2") throw new Exception("Failed transfer replaced old agent");
            await ConnectAndVerify(); // automatic old -> bundled
            Console.WriteLine("PASS: live SSH bundled first install, same-version reuse, corrupt transfer preserves old file, old-version upgrade; aTrust discovered in every session.");
        } finally { await Remote("rm -f -- " + Settings.ShellQuote(target)); }
    }
}
