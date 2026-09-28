# Measures wizard window creation, not first visible paint. Never installs the application.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$OutputDirectory = (Join-Path $env:TEMP 'VMNotify-startup'),
    [ValidateRange(1,10)][int]$Runs = 3
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$Label = [IO.Path]::GetFileNameWithoutExtension($Exe)
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class SetupWindows {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 public static int[] Find() {var ids=new List<int>(); EnumWindows((h,p)=>{var s=new StringBuilder(256);GetClassName(h,s,256); if(s.ToString()=="TWizardForm") {uint id;GetWindowThreadProcessId(h,out id);ids.Add((int)id);}return true;},IntPtr.Zero);return ids.ToArray();}
}
"@
for($i=1;$i -le $Runs;$i++) {
 $before=@([SetupWindows]::Find())
 $log=(Join-Path $OutputDirectory "$Label-$i.log")
 $started=Get-Date
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $process=Start-Process $Exe -ArgumentList ("/LOG=`"" + $log + "`"") -WindowStyle Hidden -PassThru
 $found=@()
 while($watch.ElapsedMilliseconds -lt 15000) {
  $found=@([SetupWindows]::Find() | Where-Object {$_ -notin $before})
    $found=@($found | Where-Object {
   $candidate=Get-CimInstance Win32_Process -Filter "ProcessId = $_"
   ($_ -eq $process.Id) -or ($candidate.ParentProcessId -eq $process.Id)
  })
  if($found.Count) {break}
  Start-Sleep -Milliseconds 20
 }
 [pscustomobject]@{Label=$Label;Run=$i;Start=$started.ToString('HH:mm:ss.fff');WizardCreatedMs=$watch.ElapsedMilliseconds;WizardFound=($found.Count -gt 0)}
 foreach($childId in $found) {Stop-Process -Id $childId -ErrorAction SilentlyContinue}
 if(!$process.HasExited) {$process.Kill()}
}
