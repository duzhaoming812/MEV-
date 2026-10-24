; MEV蛟龙控制台 (MEV Jiaolong Control Center) — Inno Setup 6 脚本
; 构建: 在仓库根目录运行 installer\build.ps1 (先 publish 再编译)
;
; 安装内容:
;   - 程序文件到 C:\Program Files\MEVJiaolongControlCenter (系统盘默认目录)
;   - 应用和功能 卸载项 + HKLM\SOFTWARE\MEVJiaolongControlCenter 记录安装路径/版本/能力文件
;   - 可选: 开机自启(计划任务 + 最高权限, 可绕过 UAC) / 桌面快捷方式
;   - 安装末尾调用 --probe 扫描本机可用功能, 写入 ProgramData\capabilities.json,
;     程序启动时据此禁用本机不支持的模块
;
; 为什么开机自启用计划任务而不是 Windows 服务:
;   本程序是带界面的托盘应用, 需要以管理员身份运行才能访问 root\WMI 和 MSR。
;   Windows 服务跑在 Session 0, 无法显示任何界面; 计划任务(ONLOGON + HIGHEST)
;   是这类桌面对硬件控制程序的通行做法。

#define AppName "MEV Jiaolong Control Center"
#define AppNameCn "MEV蛟龙控制台"
#define AppVersion "1.0.0"
#define AppVersion4 "1.0.0.0"
#define AppPublisher "TianMing"
#define AppExe "MEVJiaolongControlCenter.exe"

[Setup]
AppId={{9C6E1B42-7A3D-4F58-9B21-3E5D8C1A4F70}
AppName={#AppNameCn}
AppVersion={#AppVersion}
AppVerName={#AppNameCn} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (c) 2026 {#AppPublisher}
DefaultDirName={autopf}\MEVJiaolongControlCenter
DisableProgramGroupPage=yes
DefaultGroupName={#AppNameCn}
PrivilegesRequired=admin
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=MEV-Jiaolong-Control-Center-Setup-{#AppVersion}
SetupIconFile=..\src\MEVJiaolongControlCenter\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppNameCn}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion4}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (c) 2026 {#AppPublisher}
VersionInfoDescription={#AppNameCn} 安装程序
VersionInfoProductName={#AppNameCn}

[Languages]
; 安装向导全中文(第三方翻译, MIT, 见 installer/ChineseSimplified.LICENSE.txt)
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动（最高权限）"; GroupDescription: "附加任务："

[Files]
; 发布包里的 pdb 只在调试时有用, 不随安装包分发(体积可省几百 KB)
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppNameCn}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppNameCn} (Uninstall)"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppNameCn}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\MEVJiaolongControlCenter"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\MEVJiaolongControlCenter"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKLM; Subkey: "SOFTWARE\MEVJiaolongControlCenter"; ValueType: string; ValueName: "Capabilities"; ValueData: "{commonappdata}\MEVJiaolongControlCenter\capabilities.json"

[Run]
; 能力扫描: 探测本机支持哪些功能, 写 capabilities.json
Filename: "{app}\{#AppExe}"; Parameters: "--probe"; StatusMsg: "正在扫描本机可用功能…"; Flags: runhidden waituntilterminated
; 开机自启(计划任务, 与程序内开关同一套逻辑, 保证 exe 路径指向安装目录)
Filename: "{app}\{#AppExe}"; Parameters: "--autostart on"; StatusMsg: "正在注册开机自启…"; Flags: runhidden waituntilterminated; Tasks: autostart
; 安装完成后启动
; runascurrentuser 必须加: 带 postinstall 的 [Run] 项默认以"最初的未提权用户"运行,
; 而本程序清单是 requireAdministrator —— 双击安装(用户提权安装)时最后一步会报
; CreateProcess 失败 740。加上后与安装器同权限(管理员)启动, 不再弹 UAC。
Filename: "{app}\{#AppExe}"; Description: "启动 {#AppNameCn}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; 运行中的实例由 [Code] 里的 StopInstalledApp 结束(否则 DLL 被占用, 文件删不掉), 再注销开机自启。
Filename: "{app}\{#AppExe}"; Parameters: "--autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[UninstallDelete]
Type: files; Name: "{commonappdata}\MEVJiaolongControlCenter\capabilities.json"
Type: files; Name: "{commonappdata}\MEVJiaolongControlCenter\platform.json"
Type: dirifempty; Name: "{commonappdata}\MEVJiaolongControlCenter"
; LibreHardwareMonitor 运行时会把自己的内核驱动释放到 exe 同目录(文件名跟随进程名), 卸载时清掉
Type: files; Name: "{app}\MEVJiaolongControlCenter.sys"

[Code]
const
  FileAttributeDirectory = $10;

procedure StopInstalledApp();
var
  ResultCode: Integer;
begin
  // 直接调用 taskkill, 不经过 cmd /c + ping —— "在 cmd 里拼命令、用 ping 充当 sleep"
  // 是杀软行为检测里的经典组合, 能省则省。
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
end;

procedure StopDriverService(const ServiceName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + ServiceName,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(600);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ' + ServiceName,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopKernelDriver();
begin
  // LibreHardwareMonitor 把驱动注册成 R0<进程名>。应用被 taskkill 强杀时来不及
  // 注销驱动: 服务保持 Running 并锁住 .sys, 卸载后 Program Files 里就会残留驱动
  // 文件和内核服务。必须先停服务再删注册项。
  StopDriverService('R0MEVJiaolongControlCenter');
  // 改名前的旧版本(JiaolongControlCenter)留下的服务, 从旧版升级上来的机器要一并清掉
  StopDriverService('R0JiaolongControlCenter');
  Sleep(300);
end;

procedure DeleteDriverFile();
var
  I: Integer;
  DriverPath: String;
begin
  DriverPath := ExpandConstant('{app}\MEVJiaolongControlCenter.sys');
  // 驱动卸载后文件句柄可能晚几百毫秒才释放, 重试到确实删除。
  for I := 1 to 10 do
  begin
    if not FileExists(DriverPath) then
      Break;
    DeleteFile(DriverPath);
    if not FileExists(DriverPath) then
      Break;
    Sleep(300);
  end;
  if FileExists(DriverPath) then
    Log('警告: 无法删除驱动文件 ' + DriverPath)
  else
    Log('驱动文件已清理: ' + DriverPath);
  // 文件删掉后目录可能只剩空壳, Inno 的删除阶段已经过去, 这里补一刀
  RemoveDir(ExpandConstant('{app}'));
end;

var
  RemoveUserData: Boolean;

procedure DeleteUserData();
var
  I: Integer;
begin
  // 刚退出的进程可能还持有文件句柄(日志 keepFileOpen / WMI 桥脚本), 重试到目录确实消失
  for I := 1 to 10 do
  begin
    DelTree(ExpandConstant('{userappdata}\MEVJiaolongControlCenter'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\MEVJiaolongControlCenter'), True, True, True);
    // 旧版目录名也一并清掉, 避免升级过的机器残留 JiaolongControlCenter 目录
    DelTree(ExpandConstant('{userappdata}\JiaolongControlCenter'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\JiaolongControlCenter'), True, True, True);

    if (not DirExists(ExpandConstant('{userappdata}\MEVJiaolongControlCenter'))) and
       (not DirExists(ExpandConstant('{localappdata}\MEVJiaolongControlCenter'))) and
       (not DirExists(ExpandConstant('{userappdata}\JiaolongControlCenter'))) and
       (not DirExists(ExpandConstant('{localappdata}\JiaolongControlCenter'))) then
      Break;
    Log('用户数据目录仍存在, 500ms 后重试 (第 ' + IntToStr(I) + ' 次)');
    Sleep(500);
  end;

  if DirExists(ExpandConstant('{userappdata}\MEVJiaolongControlCenter')) then
    Log('警告: 未能完全删除 ' + ExpandConstant('{userappdata}\MEVJiaolongControlCenter'))
  else
    Log('已删除 ' + ExpandConstant('{userappdata}\MEVJiaolongControlCenter'));

  if DirExists(ExpandConstant('{localappdata}\MEVJiaolongControlCenter')) then
    Log('警告: 未能完全删除 ' + ExpandConstant('{localappdata}\MEVJiaolongControlCenter'))
  else
    Log('已删除 ' + ExpandConstant('{localappdata}\MEVJiaolongControlCenter'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopInstalledApp();
    StopKernelDriver();

    if UninstallSilent then
    begin
      // 静默卸载没有人可以回答弹窗。按"彻底卸载"处理, 保证脚本化卸载也不留痕迹。
      RemoveUserData := True;
      Log('卸载(静默): 按彻底卸载处理, 删除个人配置与日志');
    end
    else
    begin
      // 卸载不留残留: 默认按钮是"是"(彻底清除); 想保留配置以便重装的用户点"否",
      // 这属于用户明确选择, 不再是残留。
      RemoveUserData := MsgBox(
        '是否同时删除个人配置与日志？' + #13#10 + #13#10 +
        '“是”：彻底卸载，删除 %APPDATA%\MEVJiaolongControlCenter（灯效/配色等设置）' + #13#10 +
        '        和 %LOCALAPPDATA%\MEVJiaolongControlCenter（日志）。' + #13#10 + #13#10 +
        '“否”：保留配置，重新安装后可直接恢复原来的灯效与配色。',
        mbConfirmation, MB_YESNO) = IDYES;
      if RemoveUserData then
        Log('卸载: 用户选择删除个人配置与日志')
      else
        Log('卸载: 用户选择保留个人配置与日志');
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // 驱动文件必须等 Inno 的删除阶段跑完再补删: sc stop 后句柄释放有延迟,
    // 而 usUninstall 阶段删不到它, 目录就会整个留下来。
    DeleteDriverFile();
    // 用户数据同理必须放在这里, 不能放在 usUninstall: 那时 [UninstallRun] 的
    // --autostart off 还没执行, 它会初始化 NLog 把日志目录重新写出来; 先删后写等于白删。
    if RemoveUserData then
      DeleteUserData();
  end;
end;

const
  // 直接链接微软官方构建服务器(固定版本); SHA-256 用于校验下载结果, 版本升级时同步替换
  // 这里的三个常量。更新方法见 docs/BUILD.md。
  DotNetDesktopUrl =
    'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.32/windowsdesktop-runtime-8.0.32-win-x64.exe';
  DotNetDesktopSha256 = '0f45815f9f032095910af092405c9c6d3844b7fe57252f854d65ed9379b45598';
  DotNetDesktopFile = 'windowsdesktop-runtime-8.0.32-win-x64.exe';

var
  DotNetNeedsRestart: Boolean;

// 发布包是 framework-dependent 的, 缺 .NET 8 桌面运行时程序根本起不来。
// 直接找 C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*
// (SDK/运行时安装器不一定写 InstalledVersions\...\sharedfx 注册表键, 不能只查注册表)
function IsDotNet8DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    repeat
      if (FindRec.Attributes and FileAttributeDirectory) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

function DotNetDownloadProgress(const Url, FileName: String;
  const Progress, ProgressMax: Int64): Boolean;
var
  Caption: String;
  Percent, CurrentMb, TotalMb: Integer;
begin
  // 返回 False 会取消下载; 这里始终继续
  Result := True;
  if WizardSilent then Exit;
  if ProgressMax > 0 then
  begin
    Percent := Round(Progress * 100 / ProgressMax);
    CurrentMb := Progress div 1048576;
    TotalMb := ProgressMax div 1048576;
    Caption := Format('正在下载 .NET 8 桌面运行时… %d%%（%d / %d MB）', [Percent, CurrentMb, TotalMb]);
  end
  else
    Caption := Format('正在下载 .NET 8 桌面运行时… %d MB', [Progress div 1048576]);
  WizardForm.StatusLabel.Caption := Caption;
  WizardForm.Update;
end;

function InstallDotNet8Desktop(): Boolean;
var
  DownloadPath: String;
  ResultCode: Integer;
begin
  Result := False;
  DownloadPath := ExpandConstant('{tmp}') + '\' + DotNetDesktopFile;

  if not WizardSilent then
  begin
    WizardForm.StatusLabel.Caption := '未检测到 .NET 8 桌面运行时，正在下载（约 56 MB）…';
    WizardForm.Update;
  end;
  Log('开始下载 .NET 8 桌面运行时: ' + DotNetDesktopUrl);
  try
    DownloadTemporaryFile(DotNetDesktopUrl, DotNetDesktopFile,
                          DotNetDesktopSha256, @DotNetDownloadProgress);
  except
    Log('下载 .NET 8 桌面运行时失败: ' + GetExceptionMessage);
    Exit;
  end;

  if not FileExists(DownloadPath) then
  begin
    Log('下载结束后未找到运行时安装程序: ' + DownloadPath);
    Exit;
  end;
  Log('下载完成, SHA-256 校验通过: ' + DownloadPath);

  if not WizardSilent then
  begin
    WizardForm.StatusLabel.Caption := '正在静默安装 .NET 8 桌面运行时…';
    WizardForm.Update;
  end;
  // /install /quiet /norestart 是微软 .NET 安装包的标准无人值守参数
  if not Exec(DownloadPath, '/install /quiet /norestart', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('无法启动 .NET 8 运行时安装程序: ' + DownloadPath);
    Exit;
  end;

  Log(Format('.NET 8 运行时安装程序退出码: %d', [ResultCode]));
  // 0=成功; 3010=成功但需要重启; 1638=系统里已有更高版本
  Result := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638);
  if ResultCode = 3010 then
    DotNetNeedsRestart := True;
end;

// 在点击“安装”之后、复制文件之前执行: 缺运行时先自动补齐, 失败了才中止并给出人工链接。
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  DotNetNeedsRestart := False;
  if IsDotNet8DesktopInstalled() then
    Exit;

  Log('未检测到 .NET 8 桌面运行时, 开始自动下载并安装');
  if not InstallDotNet8Desktop() then
  begin
    Result :=
      '未能自动安装 .NET 8 桌面运行时（可能没有网络连接，或下载被安全软件拦截）。' + #13#10 + #13#10 +
      '请手动安装 .NET 8 Desktop Runtime (x64) 后重新运行本安装程序：' + #13#10 +
      'https://dotnet.microsoft.com/download/dotnet/8.0';
    Exit;
  end;

  if not IsDotNet8DesktopInstalled() then
  begin
    Result :=
      '运行时安装程序已结束，但系统中仍未找到 .NET 8 桌面运行时。' + #13#10 + #13#10 +
      '请重启电脑后重新运行安装程序，或手动安装：' + #13#10 +
      'https://dotnet.microsoft.com/download/dotnet/8.0';
    Exit;
  end;

  if DotNetNeedsRestart then
    NeedsRestart := True;
  Log('.NET 8 桌面运行时已就绪, 继续安装主程序');
end;
