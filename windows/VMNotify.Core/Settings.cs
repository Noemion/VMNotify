using System.Text.RegularExpressions;

namespace VMNotify;

public sealed record Settings
{
    public string Host { get; init; } = "";
    public string User { get; init; } = "";
    public int Port { get; init; } = 22;
    public string IdentityFile { get; init; } = "";
    public string AgentPath { get; init; } = ".local/bin/vmnotify-agent";
    public bool AutoConnect { get; init; } = true;
    public bool SilentStartup { get; init; }
    public string[] EnabledApps { get; init; } = [];
    public bool ScheduleEnabled { get; init; }
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ScheduleDays>))]
    public ScheduleDays ScheduleDays { get; init; } = ScheduleDays.EveryDay;
    public string ConnectTime { get; init; } = "09:00";
    public string DisconnectTime { get; init; } = "18:00";

    public void Validate()
    {
        if (!Enum.IsDefined(ScheduleDays)) throw new ArgumentException("无效的定时连接生效日期模式。");
        if (ScheduleEnabled) _ = DailySchedule.Parse(ConnectTime, DisconnectTime);
        if (!Regex.IsMatch(Host, @"\A[a-zA-Z0-9][a-zA-Z0-9.:%_-]{0,252}\z") || Host.Contains('\n'))
            throw new ArgumentException("主机填写 IP 或主机名，不包含用户名或空格");
        if (!Regex.IsMatch(User, @"\A[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\z")) throw new ArgumentException("无效的 SSH 用户名");
        if (Port is < 1 or > 65535) throw new ArgumentException("端口必须在 1–65535 之间");
        if (string.IsNullOrWhiteSpace(AgentPath) || AgentPath.Length > 1024 || AgentPath.Any(char.IsControl))
            throw new ArgumentException("无效的代理路径");
        if (IdentityFile.Length > 0 && !File.Exists(IdentityFile)) throw new ArgumentException("SSH 私钥文件不存在");
    }

    // OpenSSH passes the remote command to a POSIX shell, even with ArgumentList.
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public string[] SshArguments()
    {
        Validate();
        var args = new List<string> { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
            "-p", Port.ToString(), "-l", User };
        if (IdentityFile.Length > 0) args.AddRange(["-i", IdentityFile]);
        args.AddRange(["--", Host, "exec " + ShellQuote(AgentPath)]);
        return args.ToArray();
    }
}
