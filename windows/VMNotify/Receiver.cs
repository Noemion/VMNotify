using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace VMNotify;

internal sealed class Receiver
{
    public readonly Channel<AgentEvent> Notifications = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private string status = "未连接";
    private AvailableApp[] availableApps = [];
    private string[] enabledApps = [];
    public AvailableApp[] AvailableApps => Volatile.Read(ref availableApps);
    public void SetEnabledApps(string[] ids) => Volatile.Write(ref enabledApps, ids.ToArray());
    public bool IsEnabled(string id) => Volatile.Read(ref enabledApps).Contains(id);
    public string Status => Volatile.Read(ref status);
    private void SetStatus(string text) => Volatile.Write(ref status, text);

    public static string SshPath()
    {
        var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), system, "OpenSSH", "ssh.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("请先在 Windows 可选功能中安装 OpenSSH 客户端");
        return path;
    }

    public async Task Run(Settings settings, CancellationToken stop)
    {
        int retry = 1;
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            try { await Session(settings, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { SetStatus("连接中断：" + ex.Message[..Math.Min(300, ex.Message.Length)]); }
            if (stop.IsCancellationRequested) break;
            if (started.Elapsed > TimeSpan.FromMinutes(1)) retry = 1;
            SetStatus(Status + $"（{retry} 秒后重试）");
            try { await Task.Delay(TimeSpan.FromSeconds(retry), stop); }
            catch (OperationCanceledException) { break; }
            retry = Math.Min(30, retry * 2);
        }
        SetStatus("已断开");
        Volatile.Write(ref availableApps, []);
    }

    private async Task Session(Settings settings, CancellationToken stop)
    {
        SetStatus("正在通过 SSH 连接…");
        Volatile.Write(ref availableApps, []);
        var info = new ProcessStartInfo(SshPath()) { RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in settings.SshArguments()) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 SSH");
        process.StandardInput.Close();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        session.CancelAfter(TimeSpan.FromSeconds(45));
        string lastError = "SSH 已结束，请检查密钥登录、主机指纹和代理路径";
        var errors = Task.Run(async () => {
            var chars = new char[512];
            try {
                int n;
                while ((n = await process.StandardError.ReadAsync(chars, session.Token)) > 0)
                    Volatile.Write(ref lastError, new string(chars, 0, n).Trim());
            } catch (OperationCanceledException) { }
        }, CancellationToken.None);
        var active = new HashSet<string>();
        bool ready = false;
        try
        {
            await foreach (var line in Protocol.Lines(process.StandardOutput, session.Token))
            {
                var ev = AgentEvent.Parse(line);
                session.CancelAfter(TimeSpan.FromSeconds(45));
                if (ev.Kind == "ready") { ready = true; SetStatus("已连接 · 正在监听"); }
                if (ev.Kind == "degraded") { active.Clear(); SetStatus("已连接 · 桌面托盘接口暂不可用"); }
                if (ev.Kind == "apps") Volatile.Write(ref availableApps, ev.Apps!);
                if (ev.Kind == "cleared") active.Remove(ev.AppId!);
                if (ev.Kind == "attention" && ready && active.Add(ev.AppId!))
                {
                    if (active.Count > 64) throw new InvalidDataException("代理发送了过多应用");
                    if (IsEnabled(ev.AppId!)) Notifications.Writer.TryWrite(ev);
                }
            }
            throw new IOException(Volatile.Read(ref lastError));
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { throw new IOException("45 秒未收到代理心跳"); }
        finally
        {
            session.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }
}
