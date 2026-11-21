#define AppName       "Osmium"
#define AppVersion    "1.0.0"
#define AppVersionNum "1.0.0"
#define AppPublisher  "Osmium"
#define AppExe        "Osmium.exe"
#define AppId         "{{D84B93F2-6C1E-4A95-8B27-5E3C1094F6A2}"
#define SrcDir        "..\src\Osmium.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
#define IconFile      "..\assets\osmium.ico"
#define FontName      "Microsoft YaHei UI"
#define OutDir        "installer"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoVersion={#AppVersionNum}
VersionInfoProductVersion={#AppVersionNum}
VersionInfoDescription={#AppName} Installer

DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
DisableReadyPage=no
ShowLanguageDialog=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.19041

OutputDir={#OutDir}
OutputBaseFilename={#AppName}_Setup_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern dynamic includetitlebar
WizardSizePercent=110
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

CloseApplications=force
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chs.DownloadingWebView2=正在获取必要组件 (Microsoft Edge WebView2 运行时)...
chs.InstallingWebView2=正在部署必要组件 (Microsoft Edge WebView2 运行时)，请稍候...
chs.InstallFailedWebView2=必要组件 (Microsoft Edge WebView2 运行时) 部署未成功。请稍后手动安装环境，主程序将继续安装。
chs.DownloadFailedWebView2=无法获取必要组件 (Microsoft Edge WebView2 运行时)。请检查网络连接状态，或稍后手动完成安装。主程序将继续安装。

chs.RuntimeExecFailed=必要组件安装程序无法执行。请稍后手动安装环境，主程序将继续安装。
chs.InstallingDrivers=正在部署虚拟外设与手柄驱动，请稍候...

en.DownloadingWebView2=Retrieving prerequisite (Microsoft Edge WebView2 Runtime)...
en.InstallingWebView2=Deploying prerequisite (Microsoft Edge WebView2 Runtime), please wait...
en.InstallFailedWebView2=The deployment of the prerequisite (Microsoft Edge WebView2 Runtime) was unsuccessful. Please install it manually later. The main installation will now continue.
en.DownloadFailedWebView2=Unable to retrieve the prerequisite (Microsoft Edge WebView2 Runtime). Please verify your network connection or install it manually later. The main installation will now continue.

en.RuntimeExecFailed=The prerequisite installer failed to execute. Please install it manually later. The main installation will now continue.
en.InstallingDrivers=Deploying virtual USB and gamepad drivers, please wait...

[Tasks]
Name: "desktopicon";    Description: "创建桌面快捷方式";       GroupDescription: "附加快捷方式:"
Name: "startmenu";      Description: "创建开始菜单快捷方式";   GroupDescription: "附加快捷方式:"
Name: "installdrivers"; Description: "安装虚拟外设与手柄驱动"; GroupDescription: "驱动与组件:"
Name: "autostart";      Description: "开机自启动";             GroupDescription: "其他:"; Flags: unchecked

[Files]
Source: "{#SrcDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\*";          DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.lib,*.exp,*.xml,BuildHost-*,obj\*"

[Icons]
Name: "{group}\{#AppName}";          Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{group}\卸载 {#AppName}";     Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#AppName}";    Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --startup"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--install-drivers"; StatusMsg: "{cm:InstallingDrivers}"; Flags: runhidden waituntilterminated; Tasks: installdrivers
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-drivers"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "UninstallOsmiumDrivers"

[InstallDelete]
Type: filesandordirs; Name: "{app}\Cache"
Type: files; Name: "{app}\resources.pri"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Cache"
Type: filesandordirs; Name: "{app}\Logs"
Type: dirifempty;     Name: "{app}"

[Code]

var
  GDesktopIconExists: Boolean;
  DownloadPage: TDownloadWizardPage;

function GetWebView2Version(var Version: string): Boolean;
var
  RegKey: string;
begin
  RegKey := 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := RegQueryStringValue(HKLM, RegKey, 'pv', Version);
  if not Result then
    Result := RegQueryStringValue(HKCU, RegKey, 'pv', Version);

  if not Result then
  begin
    RegKey := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
    Result := RegQueryStringValue(HKLM, RegKey, 'pv', Version);
    if not Result then
      Result := RegQueryStringValue(HKCU, RegKey, 'pv', Version);
  end;
end;

function LeadingDigits(S: string): string;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
  begin
    if (S[I] >= '0') and (S[I] <= '9') then
      Result := Result + S[I]
    else
      Break;
  end;
end;

function CompareVersionStr(V1, V2: string): Integer;
var
  P1, P2: Integer;
  Num1, Num2: Integer;
begin
  Result := 0;
  while (V1 <> '') or (V2 <> '') do
  begin
    P1 := Pos('.', V1);
    if P1 = 0 then P1 := Length(V1) + 1;
    P2 := Pos('.', V2);
    if P2 = 0 then P2 := Length(V2) + 1;

    if P1 > 1 then Num1 := StrToIntDef(LeadingDigits(Copy(V1, 1, P1 - 1)), 0) else Num1 := 0;
    if P2 > 1 then Num2 := StrToIntDef(LeadingDigits(Copy(V2, 1, P2 - 1)), 0) else Num2 := 0;

    if Num1 < Num2 then
    begin
      Result := -1;
      Exit;
    end
    else if Num1 > Num2 then
    begin
      Result := 1;
      Exit;
    end;

    if P1 <= Length(V1) then V1 := Copy(V1, P1 + 1, Length(V1)) else V1 := '';
    if P2 <= Length(V2) then V2 := Copy(V2, P2 + 1, Length(V2)) else V2 := '';
  end;
end;

function NeedWebView2Update: Boolean;
var
  Version: string;
begin
  if GetWebView2Version(Version) then
  begin
    if CompareVersionStr(Version, '100.0.1185.39') <= 0 then
      Result := True
    else
      Result := False;
  end
  else
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CurPageID = wpReady then
  begin
    if NeedWebView2Update() then
    begin
      DownloadPage.Show;
      try
        DownloadPage.Clear;
        DownloadPage.Add('https://go.microsoft.com/fwlink/?linkid=2124701', 'WebView2Setup.exe', '');
        try
          DownloadPage.SetText(ExpandConstant('{cm:DownloadingWebView2}'), '');
          DownloadPage.Download;
          DownloadPage.SetText(ExpandConstant('{cm:InstallingWebView2}'), '');

          if Exec(ExpandConstant('{tmp}\WebView2Setup.exe'), '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
          begin
            if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) then
            begin
               MsgBox(ExpandConstant('{cm:InstallFailedWebView2}'), mbError, MB_OK);
            end;
          end
          else
          begin
            MsgBox(ExpandConstant('{cm:RuntimeExecFailed}'), mbError, MB_OK);
          end;
        except
          if not DownloadPage.AbortedByUser then
            MsgBox(ExpandConstant('{cm:DownloadFailedWebView2}'), mbError, MB_OK);
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  UninstStr: string;
  UninstKey: string;
  ResultCode: Integer;
  DesktopPath: string;
  ShortcutPath: string;
begin
  Result := '';
  DesktopPath := ExpandConstant('{autodesktop}');
  ShortcutPath := AddBackslash(DesktopPath) + ExpandConstant('{#AppName}') + '.lnk';
  GDesktopIconExists := FileExists(ShortcutPath);

  if GDesktopIconExists and (IsTaskSelected('desktopicon') = False) then
    WizardSelectTasks('desktopicon');

  UninstKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
               ExpandConstant('{#SetupSetting("AppId")}') + '_is1';

  if not RegQueryStringValue(HKCU, UninstKey, 'UninstallString', UninstStr) then
    RegQueryStringValue(HKLM, UninstKey, 'UninstallString', UninstStr);

  if UninstStr <> '' then
  begin
    UninstStr := RemoveQuotes(UninstStr);
    Exec(UninstStr, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure ApplyCustomFontToControl(C: TControl);
var
  I: Integer;
  F: string;
begin
  F := '{#FontName}';
  if C is TLabel             then TLabel(C).Font.Name := F
  else if C is TNewStaticText then TNewStaticText(C).Font.Name := F
  else if C is TNewCheckListBox then TNewCheckListBox(C).Font.Name := F
  else if C is TNewListBox    then TNewListBox(C).Font.Name := F
  else if C is TNewMemo       then TNewMemo(C).Font.Name := F
  else if C is TNewEdit       then TNewEdit(C).Font.Name := F
  else if C is TNewComboBox   then TNewComboBox(C).Font.Name := F
  else if C is TNewCheckBox   then TNewCheckBox(C).Font.Name := F
  else if C is TNewRadioButton then TNewRadioButton(C).Font.Name := F
  else if C is TNewButton     then TNewButton(C).Font.Name := F
  else if C is TButton        then TButton(C).Font.Name := F
  else if C is TNewProgressBar then
  else if C is TForm          then TForm(C).Font.Name := F;

  if C is TWinControl then
    for I := 0 to TWinControl(C).ControlCount - 1 do
      ApplyCustomFontToControl(TWinControl(C).Controls[I]);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  ApplyCustomFontToControl(WizardForm);
end;

procedure InitializeUninstallProgressForm;
begin
  ApplyCustomFontToControl(UninstallProgressForm);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if IsTaskSelected('desktopicon') then
    SetPreviousData(PreviousDataKey, 'desktopicon', '1')
  else
    SetPreviousData(PreviousDataKey, 'desktopicon', '0');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;
```[cite: 1]