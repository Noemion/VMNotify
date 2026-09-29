using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VMNotify;

public sealed record BundledAgent(string Architecture, string Version, string Sha256, long Size);

// Both Linux architectures travel with every Windows package. No network download is needed.
public sealed class AgentBundle(string directory)
{
    public bool Exists => File.Exists(Path.Combine(directory, "manifest.json"));
    public BundledAgent[] ReadManifest()
    {
        var path = Path.Combine(directory, "manifest.json");
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("内置采集端清单过大。");
        var entries = JsonSerializer.Deserialize<BundledAgent[]>(File.ReadAllText(path)) ?? [];
        if (entries.Length != 2 || !entries.Select(e => e.Architecture).Order().SequenceEqual(new[] { "aarch64", "x86_64" }))
            throw new InvalidDataException("内置采集端必须包含 Linux x64 和 ARM64。");
        foreach (var entry in entries) {
            _ = Updates.ParseVersion(entry.Version);
            if (!Regex.IsMatch(entry.Sha256, "\\A[a-fA-F0-9]{64}\\z") || entry.Size is <= 0 or > 67108864)
                throw new InvalidDataException("内置采集端校验信息无效。");
        }
        return entries;
    }

    public (BundledAgent Agent, bool Install) Select(string handshake)
    {
        var parts = handshake.Split(' ');
        if (parts.Length != 3 || parts[0] != "vmnotify-bootstrap") throw new InvalidDataException("采集端部署响应无效。");
        var entry = ReadManifest().SingleOrDefault(e => e.Architecture == parts[1])
            ?? throw new InvalidDataException("虚拟机架构不受支持：" + parts[1]);
        // Unknown/custom version output is rejected, and newer agents are never downgraded.
        return (entry, parts[2] == "missing" || Updates.ParseVersion(parts[2]) < Updates.ParseVersion(entry.Version));
    }

    public async Task<byte[]> ReadVerified(BundledAgent entry, CancellationToken token)
    {
        if (entry.Architecture is not ("x86_64" or "aarch64")) throw new InvalidDataException("无效的采集端架构。");
        var path = Path.Combine(directory, entry.Architecture, "vmnotify-agent");
        if (new FileInfo(path).Length != entry.Size) throw new InvalidDataException("内置 Linux 采集端大小校验失败，请重新安装 VMNotify。");
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置 Linux 采集端校验失败，请重新安装 VMNotify。");
        return bytes;
    }

    public string Command(Settings settings)
    {
        var entries = ReadManifest();
        var choices = string.Join("\n", entries.Select(e => $"{e.Architecture}) expected={Settings.ShellQuote(e.Sha256.ToLowerInvariant())}; expected_version={Settings.ShellQuote(e.Version)};;"));
        var target = settings.AgentPath.StartsWith('/') ? settings.AgentPath : "./" + settings.AgentPath;
        // read consumes only the decision line; cat receives the binary until stdin closes.
        // Stage beside the target so mv is atomic; failed/cancelled transfers leave the old agent intact.
        return $$"""
            set -eu
            target={{Settings.ShellQuote(target)}}
            arch=$(uname -m)
            case "$arch" in
            {{choices}}
            *) echo 'Unsupported Linux architecture' >&2; exit 1;;
            esac
            version=missing
            if [ -e "$target" ]; then
                version=$("$target" --version)
                case "$version" in 'vmnotify-agent '*) version=${version#vmnotify-agent };; *) echo 'Unrecognized agent version' >&2; exit 1;; esac
            fi
            printf 'vmnotify-bootstrap %s %s\n' "$arch" "$version"
            IFS= read -r action
            case "$action" in
            install)
                test ! -L "$target" || { echo 'Agent target must not be a symlink' >&2; exit 1; }
                mkdir -p -- "$(dirname -- "$target")"
                temporary=$(mktemp "${target}.update.XXXXXX")
                trap 'rm -f -- "$temporary"' EXIT
                trap 'exit 1' HUP INT TERM
                cat > "$temporary"
                actual=$(sha256sum -- "$temporary"); actual=${actual%% *}
                test "$actual" = "$expected" || { echo 'Agent transfer checksum mismatch' >&2; exit 1; }
                chmod 755 -- "$temporary"
                test "$("$temporary" --version)" = "vmnotify-agent $expected_version" || { echo 'Agent executable version mismatch' >&2; exit 1; }
                mv -f -- "$temporary" "$target"
                trap - EXIT HUP INT TERM
                ;;
            keep) ;;
            *) exit 1;;
            esac
            {{settings.SshArguments(includeVersion: true).Last()}}
            """.Replace("\r\n", "\n");
    }
}
