#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId=VMNotify
AppName=VMNotify
AppVersion={#AppVersion}
AppPublisher=Noemion
AppPublisherURL=https://github.com/Noemion/VMNotify
DefaultDirName={localappdata}\Programs\VMNotify
DefaultGroupName=VMNotify
PrivilegesRequired=lowest
SetupArchitecture=x86
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x86compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=VMNotify-{#AppVersion}-win-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\VMNotify.exe
SetupIconFile=..\windows\VMNotify\Assets\VMNotify.ico
CloseApplications=yes
RestartApplications=no

#include "legacy-runtime-cleanup.iss"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.marker"

[Icons]
Name: "{group}\VMNotify"; Filename: "{app}\VMNotify.exe"
Name: "{group}\卸载 VMNotify"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VMNotify.exe"; Description: "启动 VMNotify"; Flags: nowait postinstall skipifsilent

[Code]
function HasFramework(const Root, Framework, RequiredFile: String): Boolean;
var Entry: TFindRec; Base, Patch: String;
begin
  Result := False;
  Base := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Base + '10.0.*', Entry) then begin
    try
      repeat
        Patch := Copy(Entry.Name, 6, Length(Entry.Name));
        if (StrToIntDef(Patch, -1) >= 0) and
           FileExists(Base + Entry.Name + '\' + RequiredFile) then begin
          Result := True;
          Break;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function HasDesktopRuntime: Boolean;
var Root: String;
begin
  { Match apphost lookup: architecture-specific environment, generic environment,
    registered install location, then the platform default. }
  Root := GetEnv('DOTNET_ROOT_{#UpperCase(Arch)}');
#if Arch == "x86"
  if (Root = '') and IsWin64 then Root := GetEnv('DOTNET_ROOT(x86)');
#endif
  if Root = '' then Root := GetEnv('DOTNET_ROOT');
  if (Root = '') or not DirExists(Root) then begin
    Root := '';
    RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\{#Arch}', 'InstallLocation', Root);
  end;
  if Root = '' then begin
#if Arch == "x86"
    Root := ExpandConstant('{commonpf32}\dotnet');
#elif Arch == "x64"
    Root := ExpandConstant('{commonpf64}\dotnet');
    if IsArm64 then Root := Root + '\x64';
#else
    Root := ExpandConstant('{commonpf64}\dotnet');
#endif
  end;
  Result := HasFramework(Root, 'Microsoft.NETCore.App', 'coreclr.dll') and
            HasFramework(Root, 'Microsoft.WindowsDesktop.App', 'PresentationFramework.dll');
  Log('Desktop Runtime 10.0 ({#Arch}) check at ' + Root + ': ' + IntToStr(Ord(Result)));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not HasDesktopRuntime then
    Result := 'VMNotify requires .NET Desktop Runtime 10.0 ({#Arch}).' + #13#10 +
      'Install the latest 10.0 Desktop Runtime for this architecture, then click Install to retry.' + #13#10 +
      'Download: https://dotnet.microsoft.com/download/dotnet/10.0';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Value: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', Value) then
      if CompareText(Value, '"' + ExpandConstant('{app}\VMNotify.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify');
end;
