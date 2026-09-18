; CellPort 安装包脚本（Inno Setup 6）
; 编译: ISCC.exe installer\CellPort.iss
; 产物: 仓库同级目录 CellPort-v<版本>-setup.exe（per-user 免管理员安装）

#define MyAppName "CellPort"
#define MyAppVersion "1.0.1"
#define MyAppExeName "CellPort.exe"
#define MyAppPublisher "xmgzxmgz"
#define MyAppURL "https://github.com/xmgzxmgz/CellPort"

[Setup]
AppId={{7F4A2B8E-9C31-4D56-A8E2-51B0C3D9E7F4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/releases
; per-user 安装：无需管理员权限，不弹 UAC
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={localappdata}\Programs\CellPort
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\..
OutputBaseFilename=CellPort-v{#MyAppVersion}-setup
SetupIconFile=..\docs\cellport.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CellPort.exe
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "开机自动启动 {#MyAppName}"; \
    GroupDescription: "其他选项："; Flags: unchecked

[Files]
Source: "..\dist\framework-dependent\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Comment: "DJI 4G 模块管理器（短信 / 通话 / eSIM / AT 控制台）"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Tasks: desktopicon; Comment: "DJI 4G 模块管理器"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; \
    Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时保留用户数据（%APPDATA%\CellPort 设置与 %LOCALAPPDATA%\CellPort 日志），仅清理安装目录

[Code]
function HasDesktopRuntime9(): Boolean;
var
  FindRec: TFindRec;
  runtimeDir: String;
begin
  Result := False;
  runtimeDir := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(runtimeDir) then begin
    if FindFirst(runtimeDir + '\9.*', FindRec) then begin
      Result := True;
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not HasDesktopRuntime9() then begin
    Result := MsgBox(
      '未检测到 .NET 9 Desktop Runtime (x64)。' + #13#10 +
      '没有它 CellPort 可能无法启动。' + #13#10 + #13#10 +
      '可从 https://dotnet.microsoft.com/download/dotnet/9.0 免费下载。' + #13#10 + #13#10 +
      '是否仍要继续安装？',
      mbConfirmation, MB_YESNO) = IDYES;
  end;
end;
