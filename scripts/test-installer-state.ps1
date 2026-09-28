param([Parameter(Mandatory)][string]$Iscc, [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = (Get-Content "$repo\windows\VERSION" -Raw).Trim()
$testId = 'VMNotifyTest' + [Guid]::NewGuid().ToString('N')
$testKey = "Software\VMNotifyTests\$testId"
$work = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $testId
New-Item -ItemType Directory -Force $work | Out-Null
# Compile the real script against an isolated registry key and AppId. Never install it.
$source = Get-Content "$repo\packaging\windows.iss" -Raw
$source = $source.Replace('AppId=VMNotify', "AppId=$testId")
$source = $source -replace '(?m)^OutputBaseFilename=.*', "OutputBaseFilename=$testId-setup"
$source = $source.Replace("const UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1';", "const UninstallKey = '$testKey';")
$source = $source.Replace('..\windows\VMNotify\Assets\VMNotify.ico', "$repo\windows\VMNotify\Assets\VMNotify.ico")
$source = $source.Replace('"legacy-runtime-cleanup.iss"', '"' + "$repo\packaging\legacy-runtime-cleanup.iss" + '"')
$source | Set-Content "$work\test.iss" -Encoding utf8
& $Iscc /Qp "/DAppVersion=$version" /DArch=x64 "/DPublishDir=$PublishDir" "/DOutputDir=$work" "$work\test.iss"
if ($LASTEXITCODE) { throw 'Test installer compilation failed' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class ReadyTestNative { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l); }'
$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
try {
    foreach ($case in @(
        @{ Name='install'; Version=$null; Action='ActionInstall'; Button='Install' },
        @{ Name='upgrade'; Version='0.1.9'; Action='ActionUpgrade'; Button='Upgrade' },
        @{ Name='reinstall'; Version=$version; Action='ActionReinstall'; Button='Reinstall' },
        @{ Name='downgrade'; Version='0.10.0'; Action='ActionDowngrade'; Button='Downgrade' },
        @{ Name='unknown'; Version='invalid'; Action='ActionUnknown'; Button='Replace' }
    )) {
        $base.DeleteSubKeyTree($testKey, $false)
        if ($null -ne $case.Version) {
            $key=$base.CreateSubKey($testKey)
            $key.SetValue('DisplayVersion', $case.Version)
            $key.SetValue('InstallLocation', "$work\old")
            $key.Dispose()
        }
        $log = "$work\$($case.Name).log"
        $launcher=Start-Process "$work\$testId-setup.exe" -ArgumentList '/LANG=english',"/DIR=`"$work\target`"","/LOG=`"$log`"" -PassThru
        $window=$null
        try {
            for ($i=0; $i -lt 60; $i++) {
                $window=Get-Process | Where-Object { $_.ProcessName -eq "$testId-setup.tmp" -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
                if ($window) { break }; Start-Sleep -Milliseconds 100
            }
            if (!$window) { throw 'Test window not found' }
            $root=[System.Windows.Automation.AutomationElement]::FromHandle($window.MainWindowHandle)
            for ($page=0; $page -lt 3; $page++) {
                $next=$null
                for ($attempt=0; $attempt -lt 30; $attempt++) {
                    Start-Sleep -Milliseconds 100
                    $next=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Next'))
                    if ($next) { break }
                }
                if (!$next) { throw "Missing Next on page $page" }
                [ReadyTestNative]::SendMessage([IntPtr]$next.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
            }
            Start-Sleep -Milliseconds 300
            $names=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }
            if ($case.Button -notin $names) { throw "Wrong confirmation action for $($case.Name): $names" }
            if (!(Get-Content $log -Raw).Contains("Installation action: $($case.Action);")) { throw "Wrong detected state: $($case.Name)" }
            Write-Output "PASS: $($case.Name) ($($case.Version) -> $version)"
        } finally {
            if ($window -and !$window.HasExited) { Stop-Process -Id $window.Id }
            $launcher.WaitForExit(3000) | Out-Null
        }
    }
} finally { $base.DeleteSubKeyTree($testKey,$false); $base.Dispose() }
