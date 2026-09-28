using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace VMNotify;

internal sealed class Receiver
{
    public readonly Channel<AgentEvent> Notifications = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly object selectionLock = new();
    private sealed class Alert(AgentEvent ev, long now) {
        public AgentEvent Original = ev;
        public AgentEvent Pending = ev;
        public long LastQueued = now;
    }
    private readonly Dictionary<string, Alert> activeAlerts = new();
    private readonly TimeProvider clock;
    public Receiver(TimeProvider? clock = null) { this.clock = clock ?? TimeProvider.System; }
    public void QueueReminders() {
        lock (selectionLock) {
            foreach (var alert in activeAlerts.Values) {
                if (!IsEnabled(alert.Original.AppId!) || clock.GetElapsedTime(alert.LastQueued) < TimeSpan.FromMinutes(5)) continue;
                alert.Pending = alert.Original with { Kind = "reminder" };
                alert.LastQueued = clock.GetTimestamp();
                Notifications.Writer.TryWrite(alert.Pending);
            }
        }
    }
    public bool ShouldDisplay(AgentEvent ev) {
        lock (selectionLock) return IsEnabled(ev.AppId!) && activeAlerts.TryGetValue(ev.AppId!, out var alert) && ReferenceEquals(alert.Pending, ev);
    }
    public void MarkDelivered(AgentEvent ev) {
        lock (selectionLock) if (activeAlerts.TryGetValue(ev.AppId!, out var alert) && ReferenceEquals(alert.Pending, ev)) alert.LastQueued = clock.GetTimestamp();
    }
    private string status = "未连接";
    private AvailableApp[] availableApps = [];
    private string[] enabledApps = [];
    public AvailableApp[] AvailableApps => Volatile.Read(ref availableApps);
    public void SetEnabledApps(string[] ids)
    {
        lock (selectionLock)
        {
            var previous = enabledApps;
            Volatile.Write(ref enabledApps, ids.Distinct().ToArray());
            foreach (var id in enabledApps.Except(previous))
                if (activeAlerts.TryGetValue(id, out var alert)) {
                    alert.Pending = alert.Original with { };
                    alert.LastQueued = clock.GetTimestamp();
                    Notifications.Writer.TryWrite(alert.Pending);
                }
        }
    }
    internal void ApplyAttention(AgentEvent ev)
    {
        lock (selectionLock)
        {
            if (ev.Kind == "cleared") { activeAlerts.Remove(ev.AppId!); return; }
            if (!activeAlerts.TryAdd(ev.AppId!, new Alert(ev, clock.GetTimestamp()))) return;
            if (activeAlerts.Count > 64) throw new InvalidDataException("代理发送了过多应用");
            if (IsEnabled(ev.AppId!)) Notifications.Writer.TryWrite(ev);
        }
    }
    private void ClearAttention() { lock (selectionLock) activeAlerts.Clear(); }
    public bool IsEnabled(string id) => Volatile.Read(ref enabledApps).Contains(id);
    public string Status => Volatile.Read(ref status);
    private void SetStatus(string text) { Volatile.Write(ref status, text); Diagnostics.Write("Status: " + text); }

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
        ClearAttention();
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
        bool ready = false;
        try
        {
            await foreach (var line in Protocol.Lines(process.StandardOutput, session.Token))
            {
                var ev = AgentEvent.Parse(line);
                if (ev.Kind != "heartbeat" && ev.Kind != "apps") Diagnostics.Write("Agent event: " + ev.Kind + " app=" + ev.AppId + " enabled=" + (ev.AppId != null && IsEnabled(ev.AppId)));
                session.CancelAfter(TimeSpan.FromSeconds(45));
                if (ev.Kind == "ready") { ready = true; SetStatus("已连接 · 正在监听"); }
                if (ev.Kind == "degraded") { ClearAttention(); SetStatus("已连接 · 桌面托盘接口暂不可用"); }
                if (ev.Kind == "apps") Volatile.Write(ref availableApps, ev.Apps!);
                if (ev.Kind == "cleared" || (ev.Kind == "attention" && ready)) ApplyAttention(ev);
            }
            throw new IOException(Volatile.Read(ref lastError));
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { throw new IOException("45 秒未收到代理心跳"); }
        finally
        {
            ClearAttention();
            session.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }
}
