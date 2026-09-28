using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VMNotify;

// OpenSSH performs the handshake, displays its actual host key and writes known_hosts.
// The helper relays only host confirmation; passwords and key passphrases are never collected.
public sealed class SshAuthorization : IDisposable
{
    public const string PipeVariable = "VMNOTIFY_SSH_CONFIRM_PIPE";
    private readonly NamedPipeServerStream pipe;
    public bool Rejected { get; private set; }

    public SshAuthorization(ProcessStartInfo info, string helper)
    {
        var name = "VMNotify.Ssh." + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        info.Environment[PipeVariable] = name;
        info.Environment["SSH_ASKPASS"] = helper;
        info.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        info.Environment["DISPLAY"] = "vmnotify";
    }

    public static bool IsHostConfirmation(string prompt) => prompt.Length <= 8192
        && prompt.Contains("The authenticity of host '", StringComparison.Ordinal)
        && prompt.Contains("SHA256:", StringComparison.Ordinal)
        && prompt.Contains("Are you sure you want to continue connecting", StringComparison.Ordinal);

    public async Task Listen(Func<string, CancellationToken, Task<bool>> confirm, CancellationToken stop)
    {
        try {
            while (!stop.IsCancellationRequested) {
                await pipe.WaitForConnectionAsync(stop);
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true }) {
                    var line = await ReadBoundedLine(reader, stop);
                    var prompt = JsonSerializer.Deserialize<string>(line) ?? "";
                    bool accepted = IsHostConfirmation(prompt) && await confirm(prompt, stop);
                    stop.ThrowIfCancellationRequested();
                    if (!accepted) Rejected = true;
                    await writer.WriteLineAsync((accepted ? "yes" : "no").AsMemory(), stop);
                    // DisconnectNamedPipe discards unread data; wait for the helper to consume it.
                    if (await ReadBoundedLine(reader, stop) != "received") throw new IOException("SSH 授权响应未送达");
                }
                pipe.Disconnect();
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { Rejected = true; }
        catch (JsonException) { Rejected = true; }
    }

    private static async Task<string> ReadBoundedLine(StreamReader reader, CancellationToken stop)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (text.Length <= 16384 && await reader.ReadAsync(character, stop) != 0) {
            if (character[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(character[0]);
        }
        throw new IOException("无效的 SSH 授权请求");
    }

    public static async Task<int> RunHelper(string name, string prompt)
    {
        if (!name.StartsWith("VMNotify.Ssh.", StringComparison.Ordinal) || !IsHostConfirmation(prompt)) return 1;
        try {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await pipe.ConnectAsync(5000, timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(prompt).AsMemory(), timeout.Token);
            var answer = await ReadBoundedLine(reader, timeout.Token);
            await writer.WriteLineAsync("received".AsMemory(), timeout.Token);
            // WinExe has no console, but OpenSSH supplies an inherited stdout pipe.
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            await output.WriteLineAsync(answer == "yes" ? "yes" : "no");
            return answer == "yes" ? 0 : 1;
        } catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { return 1; }
    }

    public void Dispose() => pipe.Dispose();
}
