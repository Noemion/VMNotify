#ifndef AppVersion
  #error AppVersion must be supplied by scripts/package-windows.ps1
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
; Older tray builds can refuse graceful Restart Manager shutdown.
CloseApplications=force
RestartApplications=no
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UsePreviousLanguage=no
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
DisableReadyPage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
english.WelcomeLabel1=Welcome to VMNotify
chinesesimplified.WelcomeLabel1=欢迎安装 VMNotify
english.WelcomeLabel2=Receive message alerts from supported apps in your Linux virtual machine on Windows, without switching windows.%n%nConnect over SSH, discover apps, and choose which notifications to forward.%n%nSettings are saved automatically. VMNotify runs in the tray and supports reconnecting and scheduled connections.%n%nInstall the Linux agent before connecting. Windows requires .NET Desktop Runtime 10.0 and the OpenSSH client.%n%nNext, choose the installation folder and Start Menu folder, then confirm installation.
chinesesimplified.WelcomeLabel2=将 Linux 虚拟机中受支持应用的消息提醒转发到 Windows，无需切换窗口。%n%n通过 SSH 连接虚拟机，自动发现应用，自主选择要转发的通知。%n%n自动保存设置，支持托盘后台运行、断线重连和定时连接。%n%n使用前需安装 Linux 采集端；Windows 端需要 .NET Desktop Runtime 10.0 和 OpenSSH 客户端。%n%n接下来选择安装路径和开始菜单名称，再确认安装。

[CustomMessages]
english.ActionInstall=Install
chinesesimplified.ActionInstall=安装
english.ActionUpgrade=Upgrade
chinesesimplified.ActionUpgrade=升级
english.ActionReinstall=Reinstall
chinesesimplified.ActionReinstall=重装
english.ActionDowngrade=Downgrade
chinesesimplified.ActionDowngrade=降级
english.ActionUnknown=Replace
chinesesimplified.ActionUnknown=覆盖安装
english.ReadyDescription=Review the detected installation state and destination before continuing.
chinesesimplified.ReadyDescription=请核对检测到的安装状态和目标位置。
english.ReadyInstructions=Click %1 to continue, or Back to change settings.
chinesesimplified.ReadyInstructions=点击“%1”继续，或点击“上一步”修改设置。
english.ReadyAction=Ready to %1 VMNotify
chinesesimplified.ReadyAction=准备%1 VMNotify
english.ActionSummary=Operation: %1%nInstalled version: %2%nTarget version: %3
chinesesimplified.ActionSummary=操作类型：%1%n当前版本：%2%n目标版本：%3
english.NotInstalled=Not installed
chinesesimplified.NotInstalled=未安装
english.UnknownVersion=Unknown
chinesesimplified.UnknownVersion=未知
english.PreviousFolder=Current installation folder:
chinesesimplified.PreviousFolder=当前安装位置：
english.RetainSettings=Saved connection settings and app selections will be retained.
chinesesimplified.RetainSettings=已保存的连接配置和应用选择将予以保留。
english.DowngradeNote=This will replace the installed version with an older version.
chinesesimplified.DowngradeNote=本次将使用较旧版本替换当前版本。
english.MoveFailed=The previous installation could not be removed. Close VMNotify and try again. Your saved connection settings are retained.
chinesesimplified.MoveFailed=无法移除旧版安装。请退出 VMNotify 后重试。已保存的连接配置会保留。
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

const UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1';
var ReadyAction, InstalledVersion, InstalledFolder: String;

procedure DetectInstallAction;
var RootKey: Integer; Existing: Boolean; OldVersion, NewVersion: Int64;
begin
  RootKey := HKCU32;
  if IsWin64 then
    if RegKeyExists(HKCU64, UninstallKey) then RootKey := HKCU64;
  Existing := RegKeyExists(RootKey, UninstallKey);
  InstalledVersion := '';
  InstalledFolder := '';
  if Existing then begin
    RegQueryStringValue(RootKey, UninstallKey, 'InstallLocation', InstalledFolder);
    RegQueryStringValue(RootKey, UninstallKey, 'DisplayVersion', InstalledVersion);
  end;
  { Also recognize files remaining in the selected folder without registration. }
  if not Existing then begin
    InstalledFolder := ExpandConstant('{app}');
    Existing := FileExists(AddBackslash(InstalledFolder) + 'VMNotify.exe');
  end;
  if not Existing then begin
    ReadyAction := 'ActionInstall';
    InstalledFolder := '';
    InstalledVersion := CustomMessage('NotInstalled');
    Exit;
  end;
  if (InstalledVersion = '') or not StrToVersion(InstalledVersion, OldVersion) then begin
    if InstalledFolder = '' then InstalledVersion := ''
    else if not GetVersionNumbersString(AddBackslash(InstalledFolder) + 'VMNotify.exe', InstalledVersion) then
      InstalledVersion := '';
  end;
  if (InstalledVersion = '') or not StrToVersion(InstalledVersion, OldVersion) then begin
    ReadyAction := 'ActionUnknown';
    InstalledVersion := CustomMessage('UnknownVersion');
    Exit;
  end;
  if not StrToVersion('{#AppVersion}', NewVersion) then
    RaiseException('Invalid installer version');
  case ComparePackedVersion(NewVersion, OldVersion) of
    -1: ReadyAction := 'ActionDowngrade';
     0: ReadyAction := 'ActionReinstall';
     1: ReadyAction := 'ActionUpgrade';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  DetectInstallAction;
  Result := FmtMessage(CustomMessage('ActionSummary'), [CustomMessage(ReadyAction), InstalledVersion, '{#AppVersion}']);
  if InstalledFolder <> '' then
    Result := Result + NewLine + NewLine + CustomMessage('PreviousFolder') +
      NewLine + Space + InstalledFolder;
  Result := Result + NewLine + NewLine + MemoDirInfo;
  if MemoGroupInfo <> '' then Result := Result + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
  if ReadyAction <> 'ActionInstall' then
    Result := Result + NewLine + NewLine + CustomMessage('RetainSettings');
  if ReadyAction = 'ActionDowngrade' then
    Result := Result + NewLine + CustomMessage('DowngradeNote');
  Log('Installation action: ' + ReadyAction + '; installed=' + InstalledVersion + '; target={#AppVersion}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then begin
    WizardForm.PageNameLabel.Caption := FmtMessage(CustomMessage('ReadyAction'), [CustomMessage(ReadyAction)]);
    WizardForm.NextButton.Caption := CustomMessage(ReadyAction);
    WizardForm.PageDescriptionLabel.Caption := CustomMessage('ReadyDescription');
    WizardForm.ReadyLabel.Caption := FmtMessage(CustomMessage('ReadyInstructions'), [CustomMessage(ReadyAction)]);
  end;
end;

procedure StopInstalledApp;
var Locator, Services, Processes, Process: Variant; I, Attempt: Integer;
    OldPath, TargetPath, ExePath: String; Found: Boolean;
begin
  TargetPath := AddBackslash(ExpandConstant('{app}')) + 'VMNotify.exe';
  OldPath := '';
  if not RegQueryStringValue(HKCU32, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1', 'InstallLocation', OldPath) then
    if IsWin64 then
      RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\VMNotify_is1', 'InstallLocation', OldPath);
  if OldPath <> '' then OldPath := AddBackslash(OldPath) + 'VMNotify.exe';
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('.', 'root\cimv2');
  for Attempt := 0 to 50 do begin
    Found := False;
    Processes := Services.ExecQuery('SELECT * FROM Win32_Process WHERE Name = ''VMNotify.exe''');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then begin
        ExePath := Process.ExecutablePath;
        if (CompareText(ExePath, TargetPath) = 0) or
           ((OldPath <> '') and (CompareText(ExePath, OldPath) = 0)) then begin
          Found := True;
          if Attempt = 0 then begin
            Log('Closing installed VMNotify: ' + ExePath);
            if Process.Terminate(0) <> 0 then RaiseException(CustomMessage('MoveFailed'));
          end;
        end;
      end;
    end;
    if not Found then Exit;
    Sleep(100);
  end;
  RaiseException(CustomMessage('MoveFailed'));
end;

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
  if CurStep = ssInstall then begin
    StopInstalledApp;
    if not RemovePreviousLocation then
      RaiseException(CustomMessage('MoveFailed'));
  end;
  if (CurStep = ssPostInstall) and RestoreStartupAfterMove then
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'VMNotify', '"' + ExpandConstant('{app}\VMNotify.exe') + '"');
end;

procedure InitializeWizard;
begin
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
