param(
    [ValidateSet('x86','x64','arm64')][string[]]$Architectures = @('x86','x64','arm64'),
    [string]$Version = '',
    [string]$Iscc = 'ISCC.exe',
    [string]$OutputDirectory = '',
    [string]$AgentDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = (Get-Content (Join-Path $repoRoot 'windows\VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid Windows version' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
if (!$AgentDirectory) { $AgentDirectory = Join-Path $repoRoot 'artifacts\bundled-agents' }
$agentVersion = [regex]::Match((Get-Content (Join-Path $repoRoot 'agent\Cargo.toml') -Raw), '(?m)^version\s*=\s*"(\d+\.\d+\.\d+)"').Groups[1].Value
if (!$agentVersion) { throw 'Missing Linux agent version' }
$agentManifest = foreach ($linuxArch in @('x86_64', 'aarch64')) {
    $binary = Join-Path $AgentDirectory "bundled-agent-$linuxArch-unknown-linux-musl\vmnotify-agent"
    if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing bundled Linux agent: $binary" }
    @{ Architecture = $linuxArch; Version = $agentVersion; Sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLower(); Size = (Get-Item -LiteralPath $binary).Length }
}
foreach ($arch in $Architectures) {
    # A fresh staging directory prevents old self-contained runtime files leaking into packages.
    $publishDir = Join-Path $repoRoot "artifacts\publish\win-$arch-$Version-$([Guid]::NewGuid().ToString('N'))"
    & dotnet publish (Join-Path $repoRoot 'windows\VMNotify\VMNotify.csproj') -c Release -r "win-$arch" --self-contained false -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE) { throw "Publish failed for $arch" }
    foreach ($entry in $agentManifest) {
        $destination = Join-Path $publishDir "linux-agent\$($entry.Architecture)"
        New-Item -ItemType Directory -Force $destination | Out-Null
        Copy-Item -LiteralPath (Join-Path $AgentDirectory "bundled-agent-$($entry.Architecture)-unknown-linux-musl\vmnotify-agent") -Destination $destination
    }
    ConvertTo-Json -InputObject @($agentManifest) | Set-Content (Join-Path $publishDir 'linux-agent\manifest.json') -Encoding utf8NoBOM
    if (Test-Path (Join-Path $publishDir 'coreclr.dll')) { throw 'Runtime unexpectedly bundled' }
    $runtime = Get-Content (Join-Path $publishDir 'VMNotify.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ('Microsoft.WindowsDesktop.App' -notin $runtime.runtimeOptions.frameworks.name) { throw 'Desktop runtime dependency missing' }
    Copy-Item (Join-Path $repoRoot 'README.md'),(Join-Path $repoRoot 'LICENSE') -Destination $publishDir
    Set-Content (Join-Path $publishDir 'portable.marker') 'Settings are saved beside VMNotify.exe.' -Encoding ascii
    $zip = Join-Path $OutputDirectory "VMNotify-$Version-win-$arch-portable.zip"
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zip -Force
    & $Iscc /Qp "/DAppVersion=$Version" "/DArch=$arch" "/DPublishDir=$publishDir" "/DOutputDir=$OutputDirectory" (Join-Path $repoRoot 'packaging\windows.iss')
    if ($LASTEXITCODE) { throw "Installer compilation failed for $arch" }
}
Get-ChildItem $OutputDirectory -File | Where-Object Name -Match '^VMNotify-.*\.(zip|exe)$' |
    Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content (Join-Path $OutputDirectory 'SHA256SUMS-windows.txt') -Encoding ascii
