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
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.AppIntroduction=YOUR VIRTUAL MACHINE'S ALERTS, ON WINDOWS%n%nVMNotify forwards message alerts from supported Linux applications to your Windows desktop, so you can stay informed without switching windows.%n%nFEATURES%n  - Discover supported apps in the connected virtual machine.%n  - Choose which apps can notify you; changes are saved automatically.%n  - Reconnect automatically and keep receiving alerts in the system tray.%n  - Follow the Windows light or dark theme.%n%nGET STARTED%nInstall the companion agent in your Linux virtual machine, configure an SSH connection, then select the apps you want to receive alerts from.%n%nCURRENT SUPPORT%nThe built-in adapter supports Lanxin attention alerts. It does not read chat messages or report unread counts. Additional applications require adapters.%n%nREQUIREMENTS%n.NET Desktop Runtime 10.0 for this package's architecture and the Windows OpenSSH client.
chinesesimplified.AppIntroduction=虚拟机里的消息，Windows 上及时获知%n%nVMNotify 将 Linux 虚拟机中受支持应用的消息提醒转发到 Windows 桌面，无需频繁切换窗口。%n%n主要功能%n  · 自动发现已连接虚拟机中受支持的应用。%n  · 按应用选择是否接收提醒，修改后自动保存。%n  · 支持断线重连，关闭窗口后继续在系统托盘中运行。%n  · 跟随 Windows 的浅色或深色主题。%n%n开始使用%n在 Linux 虚拟机中安装配套采集端，配置 SSH 连接，再选择需要接收提醒的应用。%n%n当前支持%n内置蓝信适配器，转发“有消息待查看”的提醒，不读取聊天正文或统计未读数量；其他应用需要相应适配器。%n%n运行环境%n需要与安装包架构对应的 .NET Desktop Runtime 10.0，以及 Windows OpenSSH 客户端。
english.UninstallApp=Uninstall VMNotify
chinesesimplified.UninstallApp=卸载 VMNotify
english.LaunchApp=Launch VMNotify
chinesesimplified.LaunchApp=启动 VMNotify
english.RuntimeRequired=VMNotify requires .NET Desktop Runtime 10.0 ({#Arch}).%nInstall the latest 10.0 Desktop Runtime for this architecture, then click Install to retry.%nDownload: https://dotnet.microsoft.com/download/dotnet/10.0
chinesesimplified.RuntimeRequired=VMNotify 需要 .NET Desktop Runtime 10.0（{#Arch}）。%n请安装此架构对应的最新 10.0 桌面运行时，然后点击“安装”重试。%n下载地址：https://dotnet.microsoft.com/download/dotnet/10.0

#include "legacy-runtime-cleanup.iss"

[Files]
; Do not change installer identity merely because an unchanged input was recopied.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs notimestamp; Excludes: "portable.marker"

[Icons]
Name: "{group}\VMNotify"; Filename: "{app}\VMNotify.exe"
Name: "{group}\{cm:UninstallApp}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VMNotify.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := CustomMessage('AppIntroduction');
  if MemoUserInfoInfo <> '' then Result := Result + NewLine + NewLine + MemoUserInfoInfo;
  if MemoDirInfo <> '' then Result := Result + NewLine + NewLine + MemoDirInfo;
  if MemoTypeInfo <> '' then Result := Result + NewLine + NewLine + MemoTypeInfo;
  if MemoComponentsInfo <> '' then Result := Result + NewLine + NewLine + MemoComponentsInfo;
  if MemoGroupInfo <> '' then Result := Result + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

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
    Result := CustomMessage('RuntimeRequired');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Value: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', Value) then
      if CompareText(Value, '"' + ExpandConstant('{app}\VMNotify.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify');
end;
