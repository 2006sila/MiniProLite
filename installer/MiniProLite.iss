; MiniProLite 安装脚本（Inno Setup 6）
; 编译方式：iscc installer\MiniProLite.iss
; 产物：dist\MiniProLite-Setup-1.0.0.exe

#define AppName "MiniProLite"
#define AppVersion "1.0.0"
#define AppPublisher "MiniProLite"
#define AppExeName "MiniProLite.exe"

[Setup]
; AppId 固定不变，保证后续版本可以覆盖安装 / 正确升级
AppId={{0C4B7E3A-2B7D-4C1E-9A6F-3D5C8B1E7F20}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\MiniProLite
DefaultGroupName=MiniProLite
DisableProgramGroupPage=yes
; 需要写 ACPI/EC 的 WMI 方法，必须管理员权限（与 app.manifest 里 requestedExecutionLevel 的级别一致）
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=MiniProLite-Setup-1.0.0
SetupIconFile=..\MiniProLite\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=1.0.0.0
VersionInfoDescription=MiniProLite Setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\publish\MiniProLite.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MiniProLite"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\MiniProLite"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,MiniProLite}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 删掉开机自启计划任务（不存在时 schtasks 返回非零，忽略即可）
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN MiniProLite /F"; Flags: runhidden; RunOnceId: deltask
