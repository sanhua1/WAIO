; WAIO 安装包(Inno Setup 6,ADR-0031)。不要直接编译,用 scripts/build-installer.ps1(它先发布,再传入 SourceDir / AppVersion)。
; 装到 C:\Program Files\WAIO(只有管理员能写):开机自启任务以最高权限运行 exe,所以 exe 必须在普通进程改不了的位置(ADR-0004)。
; 设置和数据在 %LOCALAPPDATA%\WAIO,卸载时保留。

#ifndef SourceDir
  #error 请用 scripts/build-installer.ps1 构建(需要 /DSourceDir=发布目录)
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{4F1F4D04-9873-47AF-B9AD-E285D952C811}
AppName=WAIO
AppVersion={#AppVersion}
AppVerName=WAIO {#AppVersion}
AppPublisher=sanhua1
AppPublisherURL=https://github.com/sanhua1/WAIO
AppSupportURL=https://github.com/sanhua1/WAIO/issues
AppUpdatesURL=https://github.com/sanhua1/WAIO/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\WAIO
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=WAIO-Setup-{#AppVersion}
SetupIconFile=..\src\Waio.App\Assets\waio.ico
UninstallDisplayIcon={app}\Waio.exe
UninstallDisplayName=WAIO
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 运行中的 WAIO 由 PrepareToInstall 先结束;装完由 [Run] 重新启动
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机自动启动 WAIO(以管理员权限运行,免 UAC 弹窗)"
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WAIO"; Filename: "{app}\Waio.exe"
Name: "{autodesktop}\WAIO"; Filename: "{app}\Waio.exe"; Tasks: desktopicon

[Run]
; 自启任务由 WAIO 自己建(和设置页开关同一套代码:只认 Program Files 路径、防任务 XML 被替换)
Filename: "{app}\Waio.exe"; Parameters: "--register-autostart"; Flags: runhidden waituntilterminated; Tasks: autostart; StatusMsg: "正在创建开机自启任务..."
Filename: "{app}\Waio.exe"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\Waio.exe"; Description: "启动 WAIO"; Flags: nowait postinstall skipifsilent
; 静默安装(以后的自动更新)装完直接在托盘启动
Filename: "{app}\Waio.exe"; Parameters: "--autostart"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{app}\Waio.exe"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAutostart"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Waio.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopWaio"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // 结束正在运行的 WAIO(它常驻托盘、占着文件);没有在运行时 taskkill 返回非 0,忽略
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Waio.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
