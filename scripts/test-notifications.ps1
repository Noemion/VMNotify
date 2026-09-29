param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$LiveNotificationCenter
)
$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $directory | Out-Null
Copy-Item "$PublishDir\*" $directory -Recurse
function Invoke-NotificationCheck([string]$Mode) {
    $report = Join-Path $directory "$Mode.txt"
    $process = Start-Process (Join-Path $directory 'VMNotify.exe') -ArgumentList @('--notification-check', $Mode, ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id
        throw "Notification check timed out: $Mode"
    }
    if (!(Test-Path $report)) { throw "Notification check did not produce a report: $Mode, exit=$($process.ExitCode)" }
    $result = Get-Content $report -Raw
    if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS:')) { throw $result }
    Write-Output $result
}
Invoke-NotificationCheck 'payload'
if ($LiveNotificationCenter) {
    try {
        Invoke-NotificationCheck 'send'
        Start-Sleep -Seconds 8
        Invoke-NotificationCheck 'verify'
        # Exercise the documented COM callback through the registered local server.
        # This checks delivery to AppNotificationManager.NotificationInvoked, not
        # just direct invocation of our own event handler.
        Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IToastCheckCallback {
    void Activate([MarshalAs(UnmanagedType.LPWStr)] string appId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, IntPtr input, uint count);
}
public static class ToastCheckActivation {
    public static void Invoke(string clsid) {
        object callback = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid)));
        try { ((IToastCheckCallback)callback).Activate("", "action=open", IntPtr.Zero, 0); }
        finally { Marshal.ReleaseComObject(callback); }
    }
}
'@
        $report = Join-Path $directory 'activate.txt'
        $process = Start-Process (Join-Path $directory 'VMNotify.exe') -ArgumentList @('--notification-check', 'activate', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
        try {
            for ($i = 0; $i -lt 100 -and !(Test-Path ($report + '.ready')); $i++) {
                if ($process.HasExited) { throw 'Activation host exited before registration' }
                Start-Sleep -Milliseconds 100
            }
            if (!(Test-Path ($report + '.ready'))) { throw 'Activation host did not register' }
            $clsid = Get-ChildItem 'HKCU:\Software\Classes\CLSID' | Where-Object {
                $server = $_.OpenSubKey('LocalServer32')
                if ($null -ne $server) {
                    try { ([string]$server.GetValue('')).Contains((Join-Path $directory 'VMNotify.exe')) }
                    finally { $server.Dispose() }
                }
            } | Select-Object -ExpandProperty PSChildName
            if (@($clsid).Count -ne 1) { throw 'Expected exactly one COM server for isolated notification host' }
            [ToastCheckActivation]::Invoke($clsid)
            if (!$process.WaitForExit(10000)) { throw 'Notification activation was not delivered' }
            $result = Get-Content $report -Raw
            if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS:')) { throw $result }
            Write-Output $result
        } finally { if (!$process.HasExited) { Stop-Process -Id $process.Id } }
    } finally { Invoke-NotificationCheck 'cleanup' }
}
