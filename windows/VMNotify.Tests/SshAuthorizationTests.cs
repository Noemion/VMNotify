using System.Diagnostics;
using VMNotify;

internal static class SshAuthorizationTests
{
    public const string Prompt = "The authenticity of host 'test (127.0.0.1)' can't be established.\nED25519 key fingerprint is SHA256:example.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ";

    public static void Run()
    {
        if (!SshAuthorization.IsHostConfirmation(Prompt)
            || SshAuthorization.IsHostConfirmation("Password: ")
            || SshAuthorization.IsHostConfirmation("Enter passphrase for key: ")
            || SshAuthorization.IsHostConfirmation("REMOTE HOST IDENTIFICATION HAS CHANGED!"))
            throw new Exception("SSH confirmation filtering failed");
        var cfg = new Settings { Host = "127.0.0.1", User = "test" };
        if (!cfg.SshArguments(true).Contains("StrictHostKeyChecking=ask")
            || !cfg.SshArguments(true).Contains("PasswordAuthentication=no")
            || !cfg.SshArguments().Contains("StrictHostKeyChecking=yes"))
            throw new Exception("SSH host verification policy failed");
        var automatic = AgentEvent.Parse("""{"v":1,"kind":"apps","apps":[{"id":"auto-123","name":"Chat","running":true,"adapter":"status-notifier-auto","capability":"attention-only","verified":false}]}""");
        if (automatic.Apps![0].Verified || !automatic.Apps[0].Description.Contains("待验证"))
            throw new Exception("Automatic capability must not imply verified message detection");
        Console.WriteLine("PASS: SSH prompt filtering, host verification policy and automatic discovery metadata.");
    }

    // Use only a disposable local sshd and known_hosts file, never the user's trust store.
    public static async Task Integration(string helper, string directory, int port)
    {
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
            var info = new ProcessStartInfo(helper) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Prompt);
            using var auth = new SshAuthorization(info, helper);
            var listening = auth.Listen((_, _) => Task.FromResult(true), stop.Token);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(stop.Token);
            stop.Cancel(); await listening;
            if (process.ExitCode != 0 || (await output).Trim() != "yes") throw new Exception($"Helper stdout failed: exit={process.ExitCode} output={await output} errors={await errors}");
        }
        var knownHosts = Path.Combine(directory, "known_hosts");
        async Task<(int Exit, int Prompts)> Connect(bool accept, bool cancel = false)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = new ProcessStartInfo(Receiver.SshPath()) { RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-F"); info.ArgumentList.Add("none");
            info.ArgumentList.Add("-o"); info.ArgumentList.Add("UserKnownHostsFile=" + knownHosts.Replace('\\', '/'));
            info.ArgumentList.Add("-o"); info.ArgumentList.Add("GlobalKnownHostsFile=NUL");
            foreach (var arg in new Settings { Host = "127.0.0.1", User = "root", Port = port,
                IdentityFile = Path.Combine(directory, "client"), AgentPath = "/bin/true" }.SshArguments(true)) info.ArgumentList.Add(arg);
            using var auth = new SshAuthorization(info, helper);
            int prompts = 0;
            var listening = auth.Listen((prompt, ct) => {
                prompts++;
                if (!prompt.Contains("SHA256:")) throw new Exception("Missing actual host fingerprint");
                if (cancel) { stop.Cancel(); return Task.FromResult(false); }
                return Task.FromResult(accept);
            }, stop.Token);
            using var ssh = Process.Start(info)!;
            ssh.StandardInput.Close();
            var output = ssh.StandardOutput.ReadToEndAsync();
            var errors = ssh.StandardError.ReadToEndAsync();
            try { await ssh.WaitForExitAsync(stop.Token); }
            catch (OperationCanceledException) when (cancel && prompts == 1) { ssh.Kill(true); await ssh.WaitForExitAsync(); }
            finally { stop.Cancel(); if (!ssh.HasExited) { ssh.Kill(true); await ssh.WaitForExitAsync(); } await listening; }
            await output;
            var error = await errors;
            if (!cancel && accept && ssh.ExitCode != 0) throw new Exception($"SSH accepted connection failed: prompts={prompts} rejected={auth.Rejected} " + error);
            return (ssh.ExitCode, prompts);
        }
        File.Delete(knownHosts);
        var denied = await Connect(false);
        if (denied.Exit == 0 || denied.Prompts != 1 || File.Exists(knownHosts)) throw new Exception("Declined host persisted or connected");
        var cancelled = await Connect(false, true);
        if (cancelled.Exit == 0 || File.Exists(knownHosts)) throw new Exception("Cancelled host persisted or connected");
        var accepted = await Connect(true);
        if (accepted.Exit != 0 || accepted.Prompts != 1 || !File.Exists(knownHosts)) throw new Exception("Approved host not persisted");
        var remembered = await Connect(true);
        if (remembered.Exit != 0 || remembered.Prompts != 0) throw new Exception("Known host prompted again");
        var otherKey = File.ReadAllText(Path.Combine(directory, "other.pub")).Trim().Split(' ');
        File.WriteAllText(knownHosts, $"[127.0.0.1]:{port} {otherKey[0]} {otherKey[1]}\n");
        var changed = await Connect(false);
        if (changed.Exit == 0 || changed.Prompts != 0) throw new Exception("Changed host key was accepted or treated as first use");
        Console.WriteLine("PASS: actual OpenSSH + WinExe helper; reject, cancel, accept, remembered host and changed-key rejection.");
    }
}
