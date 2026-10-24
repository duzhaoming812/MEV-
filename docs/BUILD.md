# 构建环境与发布指南

本文档描述如何从源码构建 MEV蛟龙控制台 并生成正式安装包。项目只发布安装版，
不提供免安装/便携包。

## 已验证的构建环境

| 项目 | 版本 |
|------|------|
| 操作系统 | Windows 11 x64（本机 10.0.26300） |
| .NET SDK | 8.0.425 |
| Inno Setup | 6.7.3（Unicode 版） |
| PowerShell | 5.1（Windows 自带） |
| WMI 桥编译 | .NET Framework 4.x `csc.exe`（Windows 自带） |
| 目标框架 | `net8.0-windows` |
| 目标平台 | `win-x64`，framework-dependent |

Windows 10 1809+ 理论上同样可以构建；运行端需要 .NET 8 桌面运行时。

## 安装工具链

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
```

验证：

```powershell
dotnet --version          # 8.0.x
ISCC.exe /?               # 或在标准路径下确认 ISCC.exe 存在
```

`installer/build.ps1` 会依次在以下位置查找 `ISCC.exe`：`$env:ISCC`、
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`、`C:\Program Files (x86)\Inno Setup 6\ISCC.exe`、
`C:\Program Files\Inno Setup 6\ISCC.exe`。如果装在别处，先设置环境变量 `ISCC`。

Visual Studio 2022 不是必需的；WPF 项目用 `dotnet` CLI 即可构建。

## 依赖清单

主程序 `src/MEVJiaolongControlCenter/MEVJiaolongControlCenter.csproj`：

| 包 | 版本 | 用途 |
|----|------|------|
| CommunityToolkit.Mvvm | 8.2.2 | MVVM |
| MaterialDesignColors | 5.3.2 | 主题色 |
| MaterialDesignThemes | 5.3.2 | 控件与图标字体 |
| LibreHardwareMonitorLib | 0.9.3 | CPU/GPU 传感器与 MSR |
| NLog | 5.2.8 | 日志 |
| System.Text.Encoding.CodePages | 8.0.0 | 解码 OEM 代码页输出 |

核心库 `src/MEVJiaolongControlCenter.Core/MEVJiaolongControlCenter.Core.csproj`：

| 包 | 版本 | 用途 |
|----|------|------|
| LibreHardwareMonitorLib | 0.9.3 | 硬件监控 |
| NAudio.Core / Wasapi / WinMM | 2.2.1 | 音频回环（音乐律动） |
| NLog | 5.2.8 | 日志 |
| System.Management | 8.0.0 | WMI |

NuGet 还原需要联网。项目不包含也不下载任何便携驱动；LHM 的内核驱动由它在运行时
自行释放到安装目录（签名驱动），卸载器会停掉并删除对应服务。

## 从源码构建

```powershell
git clone <仓库地址>
cd MEVJiaolongControlCenter

dotnet restore MEVJiaolongControlCenter.sln
dotnet build -c Debug                      # 调试构建
dotnet publish src\MEVJiaolongControlCenter\MEVJiaolongControlCenter.csproj `
  -c Release -r win-x64 --self-contained false -o publish
```

`publish/` 约 16 MB、27 个文件；MaterialDesignThemes 的图标字体约占 9.6 MB。
`installer\build.ps1` 还会用 Windows 自带的 .NET Framework `csc.exe` 编译
`tools\WmiBridge\Bridge.cs` 为 `publish\WmiBridge.exe`（约 6KB，常驻约 20–30MB），
替代原先约 100MB 的常驻 PowerShell 桥。

## 生成安装包

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

脚本会：

1. 清空并重新 `dotnet publish` 到 `publish/`；
2. 用系统的 .NET Framework `csc.exe` 编译 `tools\WmiBridge\Bridge.cs` 为 `publish\WmiBridge.exe`；
3. 调用 `ISCC.exe` 编译 `installer\MEVJiaolongControlCenter.iss`；
4. 输出 `dist\MEV-Jiaolong-Control-Center-Setup-<版本>.exe`；
5. 生成 `dist\SHA256SUMS.txt`。

只想重新打包、不重新 publish 时：

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -SkipPublish
```

安装包约 5.2 MB（LZMA2 压缩），安装向导为简体中文。中文翻译文件
`installer/ChineseSimplified.isl` 来自第三方（MIT，见同目录 LICENSE），以 UTF-8 BOM 保存。

## .NET 8 运行时自动安装

安装器在 `PrepareToInstall`（点击“安装”之后、复制文件之前）检测
`C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`。缺失时：

1. 从微软官方构建服务器下载固定版本的 `windowsdesktop-runtime-8.0.32-win-x64.exe`；
2. 用内置的 `DownloadTemporaryFile` 按 SHA-256 校验；
3. 以 `/install /quiet /norestart` 静默安装（退出码 0 / 3010 / 1638 视为成功）；
4. 再次检测到运行时候继续复制主程序；失败则提示人工下载链接并中止。

界面上，下载阶段会在“准备安装”页显示百分比与 MB 进度；静默安装阶段不弹任何窗口。
整个过程写在安装日志里（`/LOG=` 可查看）。

### 更新运行时版本

三个常量在 `installer/MEVJiaolongControlCenter.iss` 的 `[Code]` 顶部：

```pascal
DotNetDesktopUrl    = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.32/windowsdesktop-runtime-8.0.32-win-x64.exe';
DotNetDesktopSha256 = '0f45815f9f032095910af092405c9c6d3844b7fe57252f854d65ed9379b45598';
DotNetDesktopFile   = 'windowsdesktop-runtime-8.0.32-win-x64.exe';
```

更新步骤：

1. 从 <https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json>
   找到 `windowsdesktop` → `win-x64` 的最新 `url`；
2. 下载该文件并计算 SHA-256（`Get-FileHash ... -Algorithm SHA256`）；
3. 同步替换上面三个常量，重新编译；
4. 在未安装 .NET 8 的干净机器上实测：应自动下载、安装并继续完成主程序安装。

> 下载链接必须使用 `builds.dotnet.microsoft.com`（官方构建服务器）。不要改成第三方网盘，
> 也不要为了“加速”去掉 SHA-256 校验。

## 修改版本号

发布新版本时同步修改三处：

| 文件 | 字段 |
|------|------|
| `installer/MEVJiaolongControlCenter.iss` | `#define AppVersion` / `#define AppVersion4` |
| `src/MEVJiaolongControlCenter/MEVJiaolongControlCenter.csproj` | `<Version>` |
| `src/MEVJiaolongControlCenter.Core/MEVJiaolongControlCenter.Core.csproj` | `<Version>` |

安装包的输出文件名、Windows“应用和功能”里的版本号、exe 文件属性都取自这些值。

## 本地调试

直接运行 `src\MEVJiaolongControlCenter\bin\Debug\net8.0-windows\MEVJiaolongControlCenter.exe`
（需要管理员权限）。日志位置：

```
%LOCALAPPDATA%\MEVJiaolongControlCenter\logs\YYYY-MM-DD.log
```

设置与 WMI 桥：

```
%APPDATA%\MEVJiaolongControlCenter\settings.json
<安装目录>\WmiBridge.exe            # 内置桥, 约 20–30MB 常驻
%APPDATA%\MEVJiaolongControlCenter\wmi_bridge.ps1   # 仅内置桥缺失时的兜底
```

命令行参数：

| 参数 | 作用 |
|------|------|
| `--probe` | 探测本机能力并写 `%ProgramData%\MEVJiaolongControlCenter\capabilities.json`，不建界面 |
| `--autostart on\|off` | 注册/注销开机自启计划任务，不建界面 |

日志默认只记 Info 及以上；排查问题时把 `NLog.config` 的 `minlevel` 改成 `Debug`。
`concurrentWrites="true"` 是为了让 `--probe` 与主程序同时写日志时不丢内容。

## 发布检查清单

- [ ] `installer\build.ps1` 编译通过，输出 `dist\...Setup-<版本>.exe`
- [ ] 安装到默认目录（`C:\Program Files\MEVJiaolongControlCenter`），程序能启动并读到硬件数据
- [ ] `WmiBridge.exe` 随安装包发布并被启动；任务管理器里没有常驻 PowerShell 桥
- [ ] 在未安装 .NET 8 的机器上验证：安装器自动下载运行时、SHA-256 校验、静默安装后继续
- [ ] 在无 EC 的机器（普通台式机）上验证：标题显示“未知模式 / 未连接”，EC 控件全部置灰，
      压力测试仍可运行且参数按本机内存/磁盘自适应，无 NVIDIA 时 GPU 探测被禁用
- [ ] “应用和功能”里能看到 `MEV蛟龙控制台`，发布者为 `TianMing`
- [ ] 卸载时选“是”：程序目录、注册表、计划任务、快捷方式、`AppData`、`LocalAppData`、
      `ProgramData`、`R0MEVJiaolongControlCenter` 驱动服务全部清除
- [ ] 卸载时选“否”：只保留 `%AppData%` 与 `%LocalAppData%` 下的个人配置与日志
- [ ] 静默卸载 `/VERYSILENT` 同样不留残留
- [ ] 上传安装包与 `SHA256SUMS.txt` 到 Releases，不要提交 `publish/`、`dist/`

校验安装包：

```powershell
certutil -hashfile .\MEV-Jiaolong-Control-Center-Setup-1.0.0.exe SHA256
```

## 常见问题

**`ISCC.exe not found`** — 安装 Inno Setup 6，或设置 `$env:ISCC` 指向 `ISCC.exe`。

**自动安装运行时失败** — 安装器会弹出人工下载链接并中止。检查网络/代理，或安全软件是否
拦截了下载与临时目录执行；手动安装
[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) 后重新运行安装程序。

**编译中文脚本报错** — `ChineseSimplified.isl` 必须保留 UTF-8 BOM，否则 Inno 会把中文读成乱码。

**程序启动但没有数据** — 确认以管理员身份运行；`root\WMI` 的
`ACPI\PNP0C14\MIFS_0` 不存在时程序会进入演示/只读状态，日志里有 `MICommonInterface 初始化=...`。

**杀软拦截** — 项目不加壳、不混淆、不修改安全软件设置。被误报时请提交样本复核，
详见 README 的“杀毒软件误报说明”。
