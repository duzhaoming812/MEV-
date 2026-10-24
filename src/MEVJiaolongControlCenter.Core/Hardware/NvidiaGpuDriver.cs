using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLog;

namespace MEVJiaolongControlCenter.Core.Hardware;

/// <summary>
/// NVIDIA 关键设备的驱动诊断与修复。
/// 本机实测: 独显和 NVPCF(Dynamic Boost 控制器)都曾出现"设备在、驱动库里有匹配 INF、
/// 但就是没绑定"的状态 —— 系统不报任何错(ProblemCode=0, Status=OK), 只是 Class 为空、无 Service,
/// 表现分别是 nvidia-smi 直接 Driver Not Loaded、以及 GPU 死卡 80W 基础 TGP。
/// 这里把这种静默失效识别出来, 并支持从 DriverStore 匹配 INF 用 newdev!UpdateDriverForPlugAndPlayDevices 重新绑定。
/// </summary>
public static class NvidiaGpuDriver
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const string VENDOR = "VEN_10DE";
    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private static readonly Version ZeroVersion = new(0, 0);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool needReboot);

    /// <summary>NVIDIA 设备状态(独显本体 / Dynamic Boost 控制器)</summary>
    public sealed class GpuDevice
    {
        /// <summary>完整设备实例ID, 形如 PCI\VEN_10DE&DEV_28A0&SUBSYS_13021D05&REV_A1\4&...&0&0009</summary>
        public string DeviceId { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>绑定的驱动服务名(如 nvlddmkm / nvpcf); 空=未绑定驱动</summary>
        public string? Service { get; init; }
        public string? DriverVersion { get; init; }
        /// <summary>匹配到的 INF 完整路径</summary>
        public string? InfPath { get; init; }
        /// <summary>设备角色, 用于界面显示</summary>
        public string Role { get; init; } = "";

        public bool DriverBound => !string.IsNullOrEmpty(Service);

        /// <summary>
        /// 传给驱动安装 API 的硬件ID: 设备实例ID去掉最后的实例路径段。
        /// 注意 PCI 实例ID里有两处反斜杠 —— PCI\VEN_xxxx&amp;DEV_yyyy&amp;...\4&amp;xxxx&amp;0&amp;0009,
        /// 硬件ID是最后一个反斜杠之前的全部, 不能按第一个反斜杠切。
        /// </summary>
        public string HardwareId => HardwareIdOf(DeviceId);
    }

    /// <summary>
    /// 从设备实例ID取出硬件ID: 去掉最后的实例路径段。
    /// 形如 PCI\VEN_10DE&amp;DEV_28A0&amp;SUBSYS_13021D05&amp;REV_A1\4&amp;D0BDF66&amp;0&amp;0009
    /// → PCI\VEN_10DE&amp;DEV_28A0&amp;SUBSYS_13021D05&amp;REV_A1 (有两处反斜杠, 必须按最后一个切)
    /// </summary>
    private static string HardwareIdOf(string deviceId)
    {
        int cut = deviceId.LastIndexOf('\\');
        return cut > 0 ? deviceId[..cut] : deviceId;
    }

    /// <summary>
    /// 找出所有"驱动库里有匹配 INF"的 NVIDIA 关键设备。
    /// 本机实测有两个会静默失去驱动绑定, 都会让性能/功能残废而系统不报任何错:
    ///   1. 独显本体 (PCI\VEN_10DE&amp;DEV_28A0...) — 未绑定时 nvidia-smi 直接 Driver Not Loaded
    ///   2. NVPCF Dynamic Boost 控制器 (ACPI\NVDA0820) — 未绑定时 GPU 死卡基础 TGP(80W), 拿不到额外功耗
    /// </summary>
    public static List<GpuDevice> FindNvidiaDevices()
    {
        var found = new List<GpuDevice>();
        try
        {
            var infEntries = ScanInfs();
            if (infEntries.Count == 0)
            {
                Logger.Warn("驱动库里没有找到匹配的 NVIDIA INF");
                return found;
            }

            using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT DeviceID, Name, Service FROM Win32_PnPEntity " +
                $"WHERE DeviceID LIKE '%{VENDOR}%' OR DeviceID LIKE 'ACPI\\\\NVDA%'");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                string deviceId = o["DeviceID"]?.ToString() ?? "";
                if (deviceId.Length == 0) continue;

                string hwid = HardwareIdOf(deviceId);
                // 只认能在 INF 里找到归属的设备: 这样能自然排除独显的 HDMI 音频功能
                // (那属于 MEDIA 类 INF, 未绑定也不影响性能)
                var match = infEntries.FirstOrDefault(e =>
                    hwid.StartsWith(e.HardwareIdPrefix, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                found.Add(new GpuDevice
                {
                    DeviceId = deviceId,
                    Name = o["Name"]?.ToString() ?? "",
                    Service = o["Service"]?.ToString(),
                    DriverVersion = ReadDriverVersion(deviceId),
                    InfPath = match.InfPath,
                    Role = deviceId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase)
                        ? "Dynamic Boost 控制器"
                        : "独显"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "枚举 NVIDIA 设备失败");
        }
        return found;
    }

    /// <summary>只取独显本体(供 nvidia-smi 相关诊断使用)</summary>
    public static GpuDevice? FindNvidiaDisplayDevice() =>
        FindNvidiaDevices().FirstOrDefault(d => d.Role == "独显");

    private static string? ReadDriverVersion(string deviceId)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                $"SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID = '{deviceId.Replace("'", "''")}'");
            foreach (ManagementBaseObject o in s.Get())
                return o["DriverVersion"]?.ToString();
        }
        catch { }
        return null;
    }

    private sealed record InfEntry(string InfPath, string HardwareIdPrefix, Version DriverVer);

    /// <summary>
    /// 扫描 DriverStore 里所有 NVIDIA INF(显示类 + 软件设备类), 取出它们支持的硬件ID与驱动版本。
    /// 显示类对应独显本体(nvtfi.inf), 软件设备类对应 NVPCF(nvpcf.inf)。
    /// </summary>
    private static List<InfEntry> ScanInfs()
    {
        var result = new List<InfEntry>();
        string root = System.IO.Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root, "nv*"))
        {
            foreach (var inf in Directory.EnumerateFiles(dir, "*.inf"))
            {
                try
                {
                    string text = File.ReadAllText(inf);
                    if (!Regex.IsMatch(text, @"^\s*Class\s*=\s*(Display|SoftwareDevice)",
                            RegexOptions.Multiline | RegexOptions.IgnoreCase))
                        continue;

                    Version ver = ZeroVersion;
                    var mv = Regex.Match(text, @"^\s*DriverVer\s*=\s*[^,]+,\s*([0-9.]+)", RegexOptions.Multiline);
                    if (mv.Success && Version.TryParse(mv.Groups[1].Value, out var parsed) && parsed != null)
                        ver = parsed;

                    foreach (Match m in Regex.Matches(text, @"PCI\\VEN_10DE&DEV_[0-9A-Fa-f]{4}&SUBSYS_[0-9A-Fa-f]{8}"))
                        result.Add(new InfEntry(inf, m.Value, ver));
                    foreach (Match m in Regex.Matches(text, @"ACPI\\NVDA[0-9A-Fa-f]{4}"))
                        result.Add(new InfEntry(inf, m.Value, ver));
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, $"解析 INF 失败: {inf}");
                }
            }
        }

        // 同一硬件ID保留版本最新的 INF
        return result
            .GroupBy(e => e.HardwareIdPrefix, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.DriverVer).First())
            .ToList();
    }

    /// <summary>
    /// 把匹配到的 INF 强制绑定到独显设备上。
    /// 显示由核显驱动时这一步很安全: 失败只会让独显继续不可用, 不会影响显示输出。
    /// </summary>
    public static bool BindDriver(GpuDevice device, out bool needReboot, out string message)
    {
        needReboot = false;
        if (device.InfPath == null)
        {
            message = "驱动库里没有匹配本机独显的 INF, 需要重新安装 GeForce 驱动";
            return false;
        }

        try
        {
            bool ok = UpdateDriverForPlugAndPlayDevices(
                IntPtr.Zero, device.HardwareId, device.InfPath, INSTALLFLAG_FORCE, out needReboot);
            int err = Marshal.GetLastWin32Error();
            message = ok
                ? $"已绑定 {System.IO.Path.GetFileName(device.InfPath)}" + (needReboot ? " (需重启)" : "")
                : $"绑定失败 (Win32 错误 {err})";
            Logger.Info($"独显驱动绑定: hwid={device.HardwareId} inf={device.InfPath} ok={ok} err={err} reboot={needReboot}");
            return ok;
        }
        catch (Exception ex)
        {
            message = "绑定异常: " + ex.Message;
            Logger.Error(ex, "独显驱动绑定异常");
            return false;
        }
    }

    /// <summary>把 nvidia-smi 的报错文本归类成可读原因</summary>
    public static string ClassifySmiFailure(string output)
    {
        if (output.Contains("Driver Not Loaded", StringComparison.OrdinalIgnoreCase)
            || output.Contains("couldn't communicate", StringComparison.OrdinalIgnoreCase))
            return "驱动未加载";
        if (output.Contains("Insufficient Permissions", StringComparison.OrdinalIgnoreCase))
            return "权限不足(需管理员)";
        if (output.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            return "该 GPU 不支持此操作";
        return "nvidia-smi 通信失败";
    }
}
