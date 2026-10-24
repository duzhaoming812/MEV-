using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using NLog;

namespace MEVJiaolongControlCenter.Core.Hardware;

/// <summary>
/// 本机硬件配置摘要, 用于界面底栏。全部从 WMI 实时读取, 不写死任何机型信息,
/// 这样别人用同一份程序也能看到自己的配置。任何一段读取失败都只是留空, 不影响其余部分。
/// </summary>
public sealed record MachineProfile(string Model, string Cpu, string Gpu, string Memory, string Disk)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static MachineProfile Load()
    {
        string model = ReadModel();
        string cpu = ReadCpu();
        string gpu = ReadGpu();
        string memory = ReadMemory();
        string disk = ReadDisk();
        return new MachineProfile(model, cpu, gpu, memory, disk);
    }

    /// <summary>拼成底栏一行; 空字段自动省略, 不会出现 " ·  · "</summary>
    public string ToFooterLine(string appName, string appVersion)
    {
        var parts = new[] { Model, Cpu, Gpu, Memory, Disk, $"{appName} v{appVersion}" }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 是否存在「可用」的独显: 以 nvidia-smi 能否通信为准。
    /// 设备在但驱动没绑定同样算不可用 —— 那正是我们要在界面上禁用的场景。
    /// </summary>
    public static bool HasDiscreteGpu()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("nvidia-smi", "-L")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return false;
            }

            string stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 && stdout.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "探测 nvidia-smi 失败");
            return false;
        }
    }

    private static string ReadModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var o in s.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    string maker = Text(o["Manufacturer"]);
                    string model = Text(o["Model"]);
                    if (model.Length == 0) return maker;
                    // 厂商名已经包含在型号里就不用重复(有些机型 Model 会写成完整品牌名)
                    return maker.Length > 0 && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)
                        ? $"{maker} {model}"
                        : model;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取机型失败"); }
        return "";
    }

    private static string ReadCpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    string name = Text(o["Name"]);
                    if (name.Length == 0) continue;
                    // 去掉冗长的尾巴: "AMD Ryzen 9 7945HX with Radeon Graphics" / "... 16-Core Processor"
                    name = name.Replace("(R)", "").Replace("(TM)", "").Replace("CPU ", "");
                    int cut = name.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                    if (cut > 0) name = name[..cut];
                    cut = name.IndexOf(" Processor", StringComparison.OrdinalIgnoreCase);
                    if (cut > 0) name = name[..cut];
                    return name.Trim();
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取 CPU 型号失败"); }
        return "";
    }

    private static string ReadGpu()
    {
        try
        {
            var names = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var o in s.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    string name = Text(o["Name"]);
                    if (name.Length == 0) continue;
                    // 跳过虚拟显示器/远程桌面这类适配器, 它们对"配置"没有意义
                    if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                        continue;
                    names.Add(name);
                }
            }

            if (names.Count == 0) return "";

            // 优先展示独显(NVIDIA/AMD 独显), 其次核显
            string? discrete = names.FirstOrDefault(n =>
                n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase));
            string chosen = discrete ?? names[0];
            return chosen
                .Replace("NVIDIA GeForce ", "")
                .Replace("NVIDIA ", "")
                .Replace("AMD ", "")
                .Trim();
        }
        catch (Exception ex) { Logger.Debug(ex, "读取显卡型号失败"); }
        return "";
    }

    private static string ReadMemory()
    {
        try
        {
            var capacities = new List<ulong>();
            int speed = 0;
            int smbiosType = 0;
            using (var s = new ManagementObjectSearcher(
                       "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                foreach (var o in s.Get().Cast<ManagementBaseObject>())
                {
                    using (o)
                    {
                        if (ulong.TryParse(o["Capacity"]?.ToString(), out var cap) && cap > 0)
                            capacities.Add(cap);
                        int sp = Number(o["Speed"]);
                        if (sp > speed) speed = sp;
                        int t = Number(o["SMBIOSMemoryType"]);
                        if (t > 0) smbiosType = t;
                    }
                }
            }

            if (capacities.Count == 0)
            {
                // 退路: 用系统报告的总物理内存(会略小于标称值, 但总比没有强)
                using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var o in s.Get().Cast<ManagementBaseObject>())
                {
                    using (o)
                    {
                        if (ulong.TryParse(o["TotalPhysicalMemory"]?.ToString(), out var total) && total > 0)
                            return $"{Gb(total, 0)} GB";
                    }
                }
                return "";
            }

            ulong totalBytes = (ulong)capacities.Sum(c => (decimal)c);
            string type = DdrTypeName(smbiosType);
            string speedText = speed > 0 ? $"-{speed}" : "";
            string sticks = capacities.Count > 1 && capacities.Distinct().Count() == 1
                ? $" ({Gb(capacities[0], 0)} GB × {capacities.Count})"
                : "";
            return $"{Gb(totalBytes, 0)} GB {type}{speedText}{sticks}".Trim();
        }
        catch (Exception ex) { Logger.Debug(ex, "读取内存信息失败"); }
        return "";
    }

    private static string ReadDisk()
    {
        // 首选 Storage 命名空间: MediaType 能区分 SSD/HDD, BusType 能区分 NVMe/SATA
        try
        {
            ulong total = 0;
            bool nvme = false, ssd = false, known = false;

            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT Size, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var o in s.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    if (!ulong.TryParse(o["Size"]?.ToString(), out var size) || size == 0) continue;
                    total += size;
                    known = true;
                    int media = Number(o["MediaType"]);   // 3=HDD 4=SSD
                    int bus = Number(o["BusType"]);       // 17=NVMe
                    if (media == 4) ssd = true;
                    if (bus == 17) nvme = true;
                }
            }

            if (known && total > 0)
            {
                string kind = nvme ? "NVMe" : ssd ? "SSD" : "HDD";
                return $"{Gb(total, 0)} GB {kind}";
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取磁盘(Storage)失败, 回退 Win32_DiskDrive"); }

        try
        {
            ulong total = 0;
            using var s = new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive");
            foreach (var o in s.Get().Cast<ManagementBaseObject>())
            {
                using (o)
                {
                    if (ulong.TryParse(o["Size"]?.ToString(), out var size)) total += size;
                }
            }
            return total > 0 ? $"{Gb(total, 0)} GB" : "";
        }
        catch (Exception ex) { Logger.Debug(ex, "读取磁盘总容量失败"); }
        return "";
    }

    /// <summary>SMBIOS 内存类型编号 → 可读名称</summary>
    private static string DdrTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 or 28 or 29 or 30 => "LPDDR",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "内存"
    };

    private static string Gb(ulong bytes)
        => (bytes / 1024.0 / 1024 / 1024).ToString("F0");

    private static string Gb(ulong bytes, int digits)
        => digits == 0 ? Gb(bytes) : (bytes / 1024.0 / 1024 / 1024).ToString($"F{digits}");

    private static string Text(object? v) => v?.ToString()?.Trim() ?? "";

    private static int Number(object? v) => int.TryParse(v?.ToString(), out var n) ? n : 0;
}
