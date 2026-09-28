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
DisableWelcomePage=yes
DisableDirPage=no
DisableProgramGroupPage=no
DisableReadyPage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.MoveFailed=The previous installation could not be removed. Close VMNotify and try again. Your saved connection settings are retained.
chinesesimplified.MoveFailed=无法移除旧版安装。请退出 VMNotify 后重试。已保存的连接配置会保留。
english.IntroductionTitle=Welcome to VMNotify
chinesesimplified.IntroductionTitle=欢迎使用 VMNotify
english.IntroductionDescription=Message alerts from your virtual machine, on your Windows desktop.
chinesesimplified.IntroductionDescription=虚拟机里的消息，Windows 上及时获知。
english.AppIntroduction=Receive alerts from your Linux virtual machine on Windows,%nwithout switching windows.%n%nFEATURES%n  - Discover supported apps and choose which can notify you.%n  - Save settings automatically and reconnect when needed.%n  - Keep running in the tray; follow the Windows theme.%n%nGET STARTED%nInstall the Linux agent, connect over SSH, then select apps.%n%nREQUIREMENTS%n.NET Desktop Runtime 10.0 and Windows OpenSSH client.
chinesesimplified.AppIntroduction=将 Linux 虚拟机中受支持应用的消息提醒转发到 Windows，%n无需切换窗口，即可及时获知新消息。%n%n主要功能%n  · 自动发现受支持应用，按需开启消息提醒。%n  · 自动保存设置，支持断线重连。%n  · 托盘后台运行，跟随系统浅色或深色主题。%n%n开始使用%n安装 Linux 采集端 → 配置 SSH 连接 → 选择提醒应用。%n%n运行环境%n.NET Desktop Runtime 10.0 和 Windows OpenSSH 客户端。
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
var RestoreStartupAfterMove: Boolean;

function RemovePreviousLocation: Boolean;
var OldPath, Uninstaller, StartupValue: String; RootKey: Integer; ExitCode: Integer;
begin
  Result := True;
  RootKey := HKCU32;
  if IsWin64 then
    if RegKeyExists(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1') then
      RootKey := HKCU64;
  if not RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1', 'InstallLocation', OldPath) then Exit;
  if OldPath = '' then Exit;
  OldPath := RemoveBackslashUnlessRoot(ExpandFileName(OldPath));
  if CompareText(OldPath, RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')))) = 0 then Exit;
  Result := False;
  if not RegQueryStringValue(RootKey, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1', 'UninstallString', Uninstaller) then Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  { Only execute the registered uninstaller inside the registered old location. }
  if CompareText(RemoveBackslashUnlessRoot(ExtractFileDir(ExpandFileName(Uninstaller))), OldPath) <> 0 then Exit;
  if not FileExists(Uninstaller) then Exit;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', StartupValue) then
    RestoreStartupAfterMove := CompareText(StartupValue, '"' + OldPath + '\VMNotify.exe"') = 0;
  Log('Moving installation from ' + OldPath + ' to ' + ExpandConstant('{app}'));
  if not Exec(Uninstaller, '/SILENT /NORESTART', OldPath, SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then Exit;
  Result := (ExitCode = 0) and not FileExists(OldPath + '\VMNotify.exe');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    if not RemovePreviousLocation then
      RaiseException(CustomMessage('MoveFailed'));
  if (CurStep = ssPostInstall) and RestoreStartupAfterMove then
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', '"' + ExpandConstant('{app}\VMNotify.exe') + '"');
end;

procedure InitializeWizard;
var IntroductionPage: TWizardPage; IntroductionText: TNewStaticText; IntroductionFrame: TBevel;
begin
  IntroductionPage := CreateCustomPage(wpWelcome,
    CustomMessage('IntroductionTitle'), CustomMessage('IntroductionDescription'));
  IntroductionFrame := TBevel.Create(WizardForm);
  IntroductionFrame.Parent := IntroductionPage.Surface;
  IntroductionFrame.Shape := bsFrame;
  IntroductionFrame.SetBounds(0, 0, IntroductionPage.SurfaceWidth, IntroductionPage.SurfaceHeight);
  IntroductionText := TNewStaticText.Create(WizardForm);
  IntroductionText.Parent := IntroductionPage.Surface;
  IntroductionText.AutoSize := False;
  IntroductionText.SetBounds(ScaleX(12), ScaleY(16), IntroductionPage.SurfaceWidth - ScaleX(24), IntroductionPage.SurfaceHeight - ScaleY(28));
  IntroductionText.WordWrap := True;
  IntroductionText.Caption := CustomMessage('AppIntroduction');
  WizardForm.ReadyMemo.Color := clWhite;
  WizardForm.ReadyMemo.BorderStyle := bsNone;
  WizardForm.ReadyMemo.ScrollBars := ssNone;
  WizardForm.ReadyMemo.WordWrap := True;
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
