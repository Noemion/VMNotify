param([Parameter(Mandatory)][string]$PublishDir, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $directory | Out-Null
Copy-Item "$PublishDir\*" $directory -Recurse
Set-Content "$directory\portable.marker" 'Startup test uses isolated settings.'
foreach ($silent in @($false, $true)) {
    @{ AutoConnect=$false; SilentStartup=$silent; ScheduleEnabled=$false; EnabledApps=@() } |
        ConvertTo-Json | Set-Content "$directory\settings.json" -Encoding utf8
    $process=Start-Process "$directory\VMNotify.exe" -PassThru
    try {
        $visible=$false
        for ($i=0; $i -lt 80; $i++) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
            if ($process.HasExited) { throw "Application exited unexpectedly (silent=$silent)" }
            if ($process.MainWindowHandle -ne 0) {
                $visible=$true
                if ($silent) { throw 'Silent startup displayed a main window' }
                break
            }
        }
        if (!$silent -and !$visible) { throw 'Normal startup did not display the main window' }
        Write-Output "PASS: startup SilentStartup=$silent, visible=$visible, process remains running"
    } finally {
        if (!$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit(3000) | Out-Null }
    }
}
