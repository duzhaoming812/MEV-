using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using NLog;

namespace MEVJiaolongControlCenter.Services;

/// <summary>
/// 开机自启管理。优先使用任务计划程序(ONLOGON + /RL HIGHEST)实现"最高权限"自启,
/// 可绕过 UAC 提示以管理员身份启动(LHM Ring0 需要); 失败时回退到 HKCU Run 注册表项。
/// </summary>
public static class AutostartService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const string TaskName = "MEVJiaolongControlCenterAutostart";
    private const string RunValueName = "MEVJiaolongControlCenter";
    // 旧版标识: 升级后必须清掉, 否则登录时旧任务会把旧 exe 也拉起来
    private const string LegacyTaskName = "JiaolongControlCenterAutostart";
    private const string LegacyRunValueName = "JiaolongControlCenter";

    private static string ExePath
    {
        get
        {
            try
            {
                return Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>删除旧版本留下的自启项(计划任务 + HKCU Run); 没有时静默返回</summary>
    public static void CleanupLegacy()
    {
        try { RunSchTasks($"/Delete /TN \"{LegacyTaskName}\" /F"); } catch { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(LegacyRunValueName, false);
        }
        catch { }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key?.GetValue(RunValueName) != null) return true;
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /NH")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;

            // 先异步读再等退出, 否则超时无效(schtasks 卡住会永久阻塞)
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            // 用退出码判断, 不匹配输出文本: schtasks 的提示是本地化的, 而且失败信息里
            // 也可能带任务名(不同语言版本行为不一致), 用文本匹配迟早会误判。
            return p.ExitCode == 0 && output.Contains(TaskName);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "查询自启任务失败");
            return false;
        }
    }

    public static bool Enable()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Logger.Warn($"无法启用自启: 找不到可执行文件 {exe}");
            return false;
        }

        // 首选: 任务计划程序, 登录时触发, 最高权限运行(绕过UAC)
        try
        {
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F";
            if (RunSchTasks(args))
            {
                CleanupLegacy();   // 顺手清掉旧版任务, 避免双启动
                // 清掉可能残留的 HKCU Run 入口: 两份同时存在会导致登录时拉起两个进程
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.DeleteValue(RunValueName, false);
                }
                catch { }
                Logger.Info("已通过任务计划程序启用最高权限开机自启");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "创建自启任务失败, 回退注册表");
        }

        // 回退: HKCU Run 注册表
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue(RunValueName, $"\"{exe}\"");
            Logger.Info("已通过注册表 Run 项启用开机自启");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启用开机自启失败");
            return false;
        }
    }

    public static bool Disable()
    {
        // 两个入口都尝试删除, 然后用真实状态作为返回值 —— 原来两个分支都无条件 ok=true,
        // 即使计划任务因权限不足删不掉也会提示"已关闭"。
        CleanupLegacy();
        try { RunSchTasks($"/Delete /TN \"{TaskName}\" /F"); }
        catch (Exception ex) { Logger.Debug(ex, "删除自启任务失败"); }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(RunValueName, false);
        }
        catch (Exception ex) { Logger.Debug(ex, "删除注册表自启项失败"); }

        bool stillEnabled = IsEnabled();
        Logger.Info(stillEnabled ? "关闭开机自启失败: 仍有入口存在" : "已关闭开机自启");
        return !stillEnabled;
    }

    private static bool RunSchTasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        // 先异步读空两个管道再等退出: 否则输出超过管道缓冲时子进程会阻塞, WaitForExit 等到超时
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(); } catch { }
            return false;
        }
        _ = outTask.GetAwaiter().GetResult();
        _ = errTask.GetAwaiter().GetResult();
        return p.ExitCode == 0;
    }
}
