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
    private Dictionary<string, NotificationRule> rules = new();
    private CancellationTokenSource? currentSession;
    private string? agentVersion;
    private long inventoryRevision;
    private long discoverySessionRevision;
    public AgentBundle Bundle { get; init; } = new(Path.Combine(AppContext.BaseDirectory, "linux-agent"));
    public string? AgentVersion => Volatile.Read(ref agentVersion);
    public long InventoryRevision => Interlocked.Read(ref inventoryRevision);
    public long DiscoverySessionRevision => Interlocked.Read(ref discoverySessionRevision);
    public bool CanRediscover { get { lock (selectionLock) return currentSession is { IsCancellationRequested: false } && Status.StartsWith("已连接"); } }
    public bool RequestRediscovery() {
        lock (selectionLock) {
            if (!CanRediscover) return false;
            SetStatus("正在重新探测托盘图标…");
            currentSession!.Cancel();
            return true;
        }
    }
    private double SampleExpirySeconds(string id) => Math.Max(8, (rules.GetValueOrDefault(id)?.SampleMilliseconds ?? 250) / 1000d * 2 + 6);
    private readonly Dictionary<string, AgentEvent> legacyAttention = new();
    private sealed class Observed(AgentEvent ev, long now) {
        public AgentEvent Event = ev;
        public long Seen = now;
        public RuleState State = new();
    }
    private readonly Dictionary<(string App, string Instance), Observed> observations = new();
    public void SetRules(Dictionary<string, NotificationRule> values) {
        if (values.Count > 64) throw new ArgumentException("通知规则最多支持 64 个应用。");
        foreach (var value in values.Values) { if (value == null) throw new ArgumentException("通知规则不能为空。"); value.Validate(); }
        lock (selectionLock) {
            var changed = rules.Keys.Union(values.Keys).Where(id => rules.GetValueOrDefault(id) != values.GetValueOrDefault(id)).ToArray();
            bool samplingChanged = changed.Any(id => (rules.GetValueOrDefault(id)?.SampleMilliseconds ?? 250) != (values.GetValueOrDefault(id)?.SampleMilliseconds ?? 250));
            rules = new(values);
            foreach (var id in changed) {
                activeAlerts.Remove(id);
                foreach (var pair in observations.Where(p => p.Key.App == id)) {
                    pair.Value.State = new();
                    if (rules.TryGetValue(id, out var rule) && clock.GetElapsedTime(pair.Value.Seen).TotalSeconds < SampleExpirySeconds(id))
                        pair.Value.State.Update(rule, pair.Value.Event.Icon!, clock);
                }
                RefreshRuleAlert(id);
                if (!rules.ContainsKey(id) && legacyAttention.TryGetValue(id, out var original)) ApplyAttention(original);
            }
            if (samplingChanged) currentSession?.Cancel();
        }
    }
    public IconObservation[] GetIcons(string appId) {
        lock (selectionLock) return observations.Where(p => p.Key.App == appId && clock.GetElapsedTime(p.Value.Seen).TotalSeconds < SampleExpirySeconds(appId))
            .Select(p => p.Value.Event.Icon!).ToArray();
    }
    internal void ApplyObservation(AgentEvent ev) {
        if (ev.Icon == null || ev.AppId == null) return;
        lock (selectionLock) {
            var key = (ev.AppId, ev.Icon.InstanceId);
            if (ev.Icon.Removed) observations.Remove(key);
            else {
                if (!observations.TryGetValue(key, out var observed)) {
                    if (observations.Count >= 64) throw new InvalidDataException("代理发送了过多托盘实例");
                    observations[key] = observed = new(ev, clock.GetTimestamp());
                }
                // A stale sample must not count toward the continuous hold period.
                if (clock.GetElapsedTime(observed.Seen).TotalSeconds >= SampleExpirySeconds(ev.AppId)) observed.State = new();
                observed.Event = ev; observed.Seen = clock.GetTimestamp();
                if (rules.TryGetValue(ev.AppId, out var rule)) observed.State.Update(rule, ev.Icon, clock);
            }
            RefreshRuleAlert(ev.AppId);
        }
    }
    private void RefreshRuleAlert(string appId) {
        if (!rules.TryGetValue(appId, out var rule)) return;
        var active = observations.Where(p => p.Key.App == appId).Select(p => p.Value).FirstOrDefault(o => o.State.Active);
        if (active == null) { activeAlerts.Remove(appId); return; }
        ApplyAttention(active.Event with { Kind = "attention", Icon = null,
            Message = string.IsNullOrWhiteSpace(rule.Message) ? "符合通知条件：" + rule.Description + "。" : rule.Message });
    }
    private readonly TimeProvider clock;
    public Func<string, CancellationToken, Task<bool>>? ConfirmHost { get; set; }
    public string? AuthorizationHelper { get; set; }
    private sealed class AuthorizationRejectedException : Exception;
    public Receiver(TimeProvider? clock = null) { this.clock = clock ?? TimeProvider.System; }
    public void QueueReminders() {
        lock (selectionLock) {
            foreach (var key in observations.Where(p => clock.GetElapsedTime(p.Value.Seen).TotalSeconds >= SampleExpirySeconds(p.Key.App)).Select(p => p.Key).ToArray()) {
                observations.Remove(key); RefreshRuleAlert(key.App);
            }
            foreach (var alert in activeAlerts.Values) {
                var repeat = rules.GetValueOrDefault(alert.Original.AppId!)?.RepeatSeconds ?? 180;
                if (repeat == 0 || !IsEnabled(alert.Original.AppId!) || clock.GetElapsedTime(alert.LastQueued) < TimeSpan.FromSeconds(repeat)) continue;
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
    private void ClearAttention() { lock (selectionLock) { activeAlerts.Clear(); observations.Clear(); legacyAttention.Clear(); } }
    internal void ApplyLegacyAttention(AgentEvent ev) { lock (selectionLock) {
        if (ev.Kind == "attention") {
            var icons = GetIcons(ev.AppId!);
            string reason = icons.Any(i => i.Attention == true) ? "应用上报需要关注" : icons.Any(i => i.Flashing == true) ? "检测到托盘图标持续闪烁" : "采集端上报托盘提醒";
            ev = ev with { Message = reason + "。" };
            Diagnostics.Write($"Tray trigger: app={ev.AppId}; reason={reason}; instances=" + string.Join(";", icons.Select(i => $"{i.InstanceId}: flashing={i.Flashing}, attention={i.Attention}, fingerprint={i.Fingerprint}")));
        }
        if (ev.Kind == "cleared") legacyAttention.Remove(ev.AppId!);
        else { legacyAttention[ev.AppId!] = ev; if (legacyAttention.Count > 64) throw new InvalidDataException("代理发送了过多应用"); }
        if (!rules.ContainsKey(ev.AppId!)) ApplyAttention(ev);
    } }
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
        SetRules(settings.Rules);
        int retry = 1;
        bool rejected = false;
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.StartNew();
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Settings sessionSettings;
            lock (selectionLock) { currentSession = restart; sessionSettings = settings with { Rules = new(rules) }; }
            try { await Session(sessionSettings, restart.Token); }
            catch (AuthorizationRejectedException) { rejected = true; break; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (restart.IsCancellationRequested) { continue; }
            catch (Exception ex) { SetStatus("连接中断：" + ex.Message[..Math.Min(300, ex.Message.Length)]); }
            finally { lock (selectionLock) { if (ReferenceEquals(currentSession, restart)) currentSession = null; } }
            if (stop.IsCancellationRequested) break;
            if (started.Elapsed > TimeSpan.FromMinutes(1)) retry = 1;
            SetStatus(Status + $"（{retry} 秒后重试）");
            try { await Task.Delay(TimeSpan.FromSeconds(retry), stop); }
            catch (OperationCanceledException) { break; }
            retry = Math.Min(30, retry * 2);
        }
        SetStatus(rejected ? "SSH 授权已取消；点击“保存并连接”重试" : "已断开");
        Volatile.Write(ref availableApps, []);
    }

    private async Task Session(Settings settings, CancellationToken stop)
    {
        var discoveryRevision = Interlocked.Increment(ref discoverySessionRevision);
        SetStatus("正在通过 SSH 连接…");
        Volatile.Write(ref agentVersion, null);
        ClearAttention();
        Volatile.Write(ref availableApps, []);
        var info = new ProcessStartInfo(SshPath()) { RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8 };
        bool authorize = ConfirmHost != null && AuthorizationHelper != null;
        var arguments = settings.SshArguments(authorize, includeVersion: true);
        bool bundled = Bundle.Exists;
        if (bundled) arguments[^1] = Bundle.Command(settings);
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var authorization = authorize ? new SshAuthorization(info, AuthorizationHelper!) : null;
        using var process = Process.Start(info) ?? throw new IOException("无法启动 SSH");
        if (!bundled) process.StandardInput.Close();
        session.CancelAfter(TimeSpan.FromSeconds(45));
        var authorizationTask = authorization?.Listen(async (prompt, ct) => {
            session.CancelAfter(Timeout.InfiniteTimeSpan);
            SetStatus("等待确认 SSH 主机指纹…");
            try { return await ConfirmHost!(prompt, ct); }
            finally { if (!ct.IsCancellationRequested) session.CancelAfter(TimeSpan.FromSeconds(45)); }
        }, session.Token) ?? Task.CompletedTask;
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
                if (bundled && line.StartsWith("vmnotify-bootstrap ", StringComparison.Ordinal)) {
                    var (agent, install) = Bundle.Select(line);
                    if (install) {
                        SetStatus($"正在安装或升级 Linux 采集端至 {agent.Version}…");
                        session.CancelAfter(TimeSpan.FromMinutes(2));
                        var bytes = await Bundle.ReadVerified(agent, session.Token);
                        await process.StandardInput.WriteAsync("install\n".AsMemory(), session.Token);
                        await process.StandardInput.FlushAsync(session.Token);
                        await process.StandardInput.BaseStream.WriteAsync(bytes, session.Token);
                    } else {
                        await process.StandardInput.WriteAsync("keep\n".AsMemory(), session.Token);
                    }
                    process.StandardInput.Close();
                    bundled = false;
                    continue;
                }
                var ev = AgentEvent.Parse(line);
                if (ev.Kind != "heartbeat" && ev.Kind != "apps") Diagnostics.Write("Agent event: " + ev.Kind + " app=" + ev.AppId + " enabled=" + (ev.AppId != null && IsEnabled(ev.AppId)));
                session.CancelAfter(TimeSpan.FromSeconds(45));
                if (ev.Kind == "agent_info") Volatile.Write(ref agentVersion, ev.AgentVersion);
                if (ev.Kind == "ready") { ready = true; SetStatus("已连接 · 正在监听"); }
                if (ev.Kind == "degraded") { ClearAttention(); SetStatus("已连接 · 桌面托盘接口暂不可用"); }
                if (ev.Kind == "apps") {
                    Volatile.Write(ref availableApps, ev.Apps!);
                    Interlocked.Exchange(ref inventoryRevision, discoveryRevision);
                    lock (selectionLock) {
                        foreach (var key in observations.Keys.Where(k => !ev.Apps!.Any(a => a.Id == k.App && a.Running)).ToArray()) {
                            observations.Remove(key); RefreshRuleAlert(key.App);
                        }
                    }
                }
                if (ev.Icon != null && ready) ApplyObservation(ev);
                if (ev.Kind == "cleared" || (ev.Kind == "attention" && ready)) ApplyLegacyAttention(ev);
            }
            if (authorization?.Rejected == true) throw new AuthorizationRejectedException();
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
            await authorizationTask;
        }
    }
}
