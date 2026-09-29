param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference = 'Stop'
$version = (Get-Content (Join-Path $RepositoryRoot 'windows/VERSION') -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid Windows release version' }
$changelog = Get-Content (Join-Path $RepositoryRoot 'CHANGELOG.md') -Raw
$pattern = '(?ms)^## Windows ' + [regex]::Escape($version) + ' / Linux (?<agent>\d+\.\d+\.\d+)\r?\n(?<body>.*?)(?=^## |\z)'
$sections = [regex]::Matches($changelog, $pattern)
if ($sections.Count -ne 1) { throw "CHANGELOG.md must contain exactly one section for Windows $version" }
$body = $sections[0].Groups['body'].Value.Trim()
if ($body -notmatch '(?m)^- \S') { throw 'Release notes must contain change entries' }
$agentVersion = $sections[0].Groups['agent'].Value
$notes = @"
# VMNotify v$version

## 更新内容

$body

## 下载与升级

- 按 Windows 架构选择 x64、x86 或 ARM64；普通安装使用 setup.exe，便携使用 portable.zip。
- 需要与包架构一致的 .NET Desktop Runtime 10.0 和 Windows OpenSSH 客户端。
- Windows 包内置 Linux 采集端 $agentVersion（x64 / ARM64），连接虚拟机时自动部署或升级，无需单独下载。
- 已安装用户可在“关于 → 检查更新 → 下载并升级”中更新；旧版遇到 API 限流时，请从本发布页手动下载安装一次。
- SHA256SUMS-windows.txt 提供下载完整性校验。

"@
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $notes + "`n", [Text.UTF8Encoding]::new($false))
