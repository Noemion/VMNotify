param(
    [ValidateSet('x86','x64','arm64')][string[]]$Architectures = @('x86','x64','arm64'),
    [string]$Version = '',
    [string]$Iscc = 'ISCC.exe',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = (Get-Content (Join-Path $repoRoot 'windows\VERSION') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid Windows version' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
foreach ($arch in $Architectures) {
    # A fresh staging directory prevents old self-contained runtime files leaking into packages.
    $publishDir = Join-Path $repoRoot "artifacts\publish\win-$arch-$Version-$([Guid]::NewGuid().ToString('N'))"
    & dotnet publish (Join-Path $repoRoot 'windows\VMNotify\VMNotify.csproj') -c Release -r "win-$arch" --self-contained false -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE) { throw "Publish failed for $arch" }
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
