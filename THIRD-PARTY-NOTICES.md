# 第三方组件与许可证 / Third-Party Notices

本项目自身以 **MIT** 许可发布（见 [LICENSE](LICENSE)）。但发布目录中会随附下列第三方 DLL，
它们各自保留原有许可证。**MIT 只覆盖本仓库自己的代码**，不改变这些组件的授权条件。

发布命令：`dotnet publish -c Release -r win-x64 --self-contained false`
（本清单按该命令的实际输出核对，共 17 个 DLL，其中 15 个为第三方组件。）

> 托盘图标已改为直接调用 `Shell_NotifyIcon`（自写实现），不再依赖
> Hardcodet.NotifyIcon.Wpf（CPOL 1.02，非 OSI 认证），因此当前发布物中**没有 CPOL 组件**。

## 分发的第三方程序集

| 程序集 | 项目 | 许可证 | 备注 |
|---|---|---|---|
| CommunityToolkit.Mvvm.dll | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) | MIT | |
| MaterialDesignColors.dll | [MaterialDesignInXamlToolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | MIT | |
| MaterialDesignThemes.Wpf.dll | [MaterialDesignInXamlToolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | MIT | 含图标字体，占发布体积约 60% |
| Microsoft.Xaml.Behaviors.dll | [XamlBehaviorsWpf](https://github.com/Microsoft/XamlBehaviorsWpf) | MIT | 由 MaterialDesignThemes 传递引入 |
| NAudio.Core.dll / NAudio.Wasapi.dll / NAudio.WinMM.dll | [naudio/NAudio](https://github.com/naudio/NAudio) | MIT | 仅使用 Core / Wasapi / WinMM 三个子包 |
| System.Management.dll | .NET Runtime | MIT | |
| System.IO.Ports.dll | .NET Runtime | MIT | 由 HidSharp 传递引入 |
| Mono.Posix.NETStandard.dll / MonoPosixHelper.dll / libMonoPosixHelper.dll | .NET Foundation | MIT | 由 HidSharp 传递引入 |
| NLog.dll | [NLog](https://nlog-project.org/) | **BSD-3-Clause** | |
| HidSharp.dll | [HIDSharp](http://www.zer7.com/software/hidsharp) | **Apache-2.0** | 由 LibreHardwareMonitorLib 传递引入 |
| LibreHardwareMonitorLib.dll | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | **MPL-2.0** | 读取 CPU/主板/电池传感器 |

## 需要特别注意的两项

### MPL-2.0 — LibreHardwareMonitorLib

MPL-2.0 是**文件级**弱著佐权：可以把它和采用其他许可证的代码组合分发，前提是
**对 MPL 覆盖文件的修改必须以 MPL 公开**。

本项目**未修改** LibreHardwareMonitor 的任何源码，仅以 NuGet 二进制形式引用。
因此只需保留其许可证声明；如需获取对应源码，见上方项目链接（版本 0.9.3）。
若你将来修改了该库，必须公开修改后的文件。

### Apache-2.0 — HidSharp

Apache-2.0 允许商用与再分发，但要求**保留版权与许可声明**。上游声明为：

> HIDSharp Copyright 2010-2025 James F. Bellinger — Licensed under the Apache License, Version 2.0.

本项目同样未修改该库，仅通过 LibreHardwareMonitor 间接使用（USB 设备枚举，
本程序实际只读 CPU/主板传感器时不会走到这条路径）。

### 已移除：Hardcodet.NotifyIcon.Wpf（CPOL 1.02）

原系统托盘依赖该库，其许可证为 **CPOL 1.02（非 OSI 认证）**。为了做到"发布物只含
OSI 认可的许可证"，托盘已改为自写实现（`Services/TrayService.cs`，
直接调用 `Shell_NotifyIcon` / `LoadImage`，约 300 行），该依赖已从项目中删除。

因此当前发布目录中**不存在 CPOL 组件**，全部第三方组件均为 MIT / BSD-3-Clause /
Apache-2.0 / MPL-2.0 —— 都是 OSI 认证、且都允许与 MIT 项目组合分发。

## 安装程序使用的第三方资源

- **Inno Setup 简体中文翻译**（`installer/ChineseSimplified.isl`）
  - 作者：Zhenghan Yang (Kira)，https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
  - 许可证：MIT（全文见 `installer/ChineseSimplified.LICENSE.txt`）
  - 用途：安装向导显示简体中文；编译时嵌入 `Setup.exe`

## 未随包分发、仅构建期使用的包

以下包出现在 `project.assets.json` 的还原图里，但不会进入发布目录，
因此不构成对最终用户的再分发：

- Newtonsoft.Json（MIT，LibreHardwareMonitor 的编译期依赖）
- Microsoft.NETCore.Platforms、Microsoft.Win32.Registry、System.Text.Encoding.CodePages、
  runtime.*.System.IO.Ports 等运行时支撑包

## 保持本清单准确

改动 `PackageReference` 或发布参数后，请重新执行一次发布并核对发布目录中的 DLL 列表，
必要时更新本文件。
