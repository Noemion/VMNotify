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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.marker"

[Icons]
Name: "{group}\VMNotify"; Filename: "{app}\VMNotify.exe"
Name: "{group}\卸载 VMNotify"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VMNotify.exe"; Description: "启动 VMNotify"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Value: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', Value) then
      if CompareText(Value, '"' + ExpandConstant('{app}\VMNotify.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify');
end;
