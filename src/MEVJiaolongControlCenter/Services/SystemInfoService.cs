using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NLog;

namespace MEVJiaolongControlCenter.Services;

/// <summary>
/// 展示用的系统信息(只读): 电池循环次数、显示器型号/分辨率/刷新率、操作系统版本。
/// 与实时传感器无关, 启动时读取一次即可。
/// </summary>
public sealed class SystemInfoService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public int? BatteryCycles { get; private set; }
    public string BatteryName { get; private set; } = "";
    public string BatteryManufacturer { get; private set; } = "";
    public string BatteryChemistry { get; private set; } = "";
    public double BatteryDesignVoltageV { get; private set; }
    public int BatteryStatus { get; private set; } = -1;
    public int BatteryEstimatedRunMinutes { get; private set; } = -1;
    public int BatteryMaxRechargeMinutes { get; private set; } = -1;
    public string DisplayPanel { get; private set; } = "";
    public string DisplayMode { get; private set; } = "";
    public string OsText { get; private set; } = "";

    // CPU / 内存 / 硬盘元数据(静态)
    public string MachineText { get; private set; } = "";
    public string CpuName { get; private set; } = "";
    public int CpuCores { get; private set; }
    public int CpuThreads { get; private set; }
    public double CpuL2Mb { get; private set; }
    public double CpuL3Mb { get; private set; }
    public string CpuSocket { get; private set; } = "";
    public int CpuMaxClockMhz { get; private set; }
    public string CpuArch { get; private set; } = "";
    public List<string> MemoryModules { get; private set; } = new();
    public string StorageModel { get; private set; } = "";
    public double StorageSizeGb { get; private set; }
    public string StorageBus { get; private set; } = "";
    public string MonitorSerial { get; private set; } = "";
    public int MonitorYear { get; private set; }
    public List<string> DisplayAdapters { get; private set; } = new();
    public string NetworkText { get; private set; } = "";
    public string OsInstallDate { get; private set; } = "";
    public DateTime LastBoot { get; private set; }
    /// <summary>Windows 报告的可视物理内存总量(GB), 用于替代 LHM 的"已用+可用"近似值</summary>
    public double MemoryTotalGb { get; private set; }

    // 电源/充电状态(GetSystemPowerStatus)
    public bool AcOnline { get; private set; }
    public bool BatteryCharging { get; private set; }
    public bool BatteryNoBattery { get; private set; }
    public int BatteryLifeSeconds { get; private set; } = -1;
    public int BatteryPercent { get; private set; } = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public void RefreshPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return;
            AcOnline = s.ACLineStatus == 1;
            BatteryCharging = (s.BatteryFlag & 8) != 0;
            BatteryNoBattery = (s.BatteryFlag & 128) != 0;
            BatteryPercent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
            BatteryLifeSeconds = s.BatteryLifeTime == uint.MaxValue ? -1 : (int)s.BatteryLifeTime;
        }
        catch (Exception ex) { Logger.Debug(ex, "读取电源状态失败"); }
    }

    /// <summary>
    /// 刷新会变化的那部分: 电源/电池状态与循环次数、分辨率与显示适配器、网卡连接状态。
    /// 静态信息(型号/序列号/安装日期)不在重复查询范围内。调用方放到后台线程执行。
    /// </summary>
    public void RefreshDynamic()
    {
        try
        {
            RefreshPowerStatus();
            BatteryCycles = ReadBatteryCycles();
            ReadBatteryInfo();
            ReadDisplay();
            ReadNetwork();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "刷新系统动态信息失败");
        }
    }

    public void Load()
    {
        BatteryCycles = ReadBatteryCycles();
        ReadBatteryInfo();
        ReadDisplay();
        ReadOs();
        MemoryTotalGb = ReadMemoryTotalGb();
        ReadMachineInfo();
        ReadCpuInfo();
        ReadMemoryModules();
        ReadStorageInfo();
        ReadNetwork();
        RefreshPowerStatus();
        Logger.Info($"系统信息: 电池循环={BatteryCycles?.ToString() ?? "?"}, 显示={DisplayPanel} {DisplayMode}, " +
                    $"CPU={CpuName} {CpuCores}C/{CpuThreads}T, 内存={MemoryTotalGb:F1}GB, 硬盘={StorageModel}, OS={OsText}");
    }

    private void ReadCpuInfo()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, L2CacheSize, L3CacheSize, " +
                "SocketDesignation, MaxClockSpeed, AddressWidth FROM Win32_Processor");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string name = (o["Name"]?.ToString() ?? "").Replace("(R)", "").Replace("(TM)", "");
                    int cut = name.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                    if (cut > 0) name = name[..cut];
                    CpuName = name.Trim();
                    CpuCores = o["NumberOfCores"] != null ? Convert.ToInt32(o["NumberOfCores"]) : 0;
                    CpuThreads = o["NumberOfLogicalProcessors"] != null ? Convert.ToInt32(o["NumberOfLogicalProcessors"]) : 0;
                    CpuL2Mb = o["L2CacheSize"] != null ? Convert.ToDouble(o["L2CacheSize"]) / 1024 : 0;
                    CpuL3Mb = o["L3CacheSize"] != null ? Convert.ToDouble(o["L3CacheSize"]) / 1024 : 0;
                    CpuSocket = o["SocketDesignation"]?.ToString()?.Trim() ?? "";
                    CpuMaxClockMhz = o["MaxClockSpeed"] != null ? Convert.ToInt32(o["MaxClockSpeed"]) : 0;
                    int bits = o["AddressWidth"] != null ? Convert.ToInt32(o["AddressWidth"]) : 0;
                    CpuArch = bits > 0 ? $"{bits}-bit" : "";
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取 CPU 元数据失败"); }
    }

    private void ReadMemoryModules()
    {
        var list = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT BankLabel, DeviceLocator, Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    double gb = o["Capacity"] != null ? Convert.ToDouble(o["Capacity"]) / 1024.0 / 1024.0 / 1024.0 : 0;
                    // 两条内存的 DeviceLocator 都可能叫 "DIMM 0", 用 BankLabel 区分通道
                    string bank = o["BankLabel"]?.ToString()?.Trim() ?? "";
                    string loc = o["DeviceLocator"]?.ToString()?.Trim() ?? "";
                    string where = bank.Length > 0 ? $"{bank} / {loc}" : loc;
                    string speed = o["Speed"]?.ToString() ?? "";
                    string maker = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                    string part = o["PartNumber"]?.ToString()?.Trim() ?? "";
                    // 分两行, 避免在窄内存列里把型号/料号拦腰截断
                    list.Add($"{where} · {gb:F1}GB {speed}MHz");
                    list.Add($"  {maker} {part}".TrimEnd());
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取内存条信息失败"); }
        MemoryModules = list;
    }

    private void ReadStorageInfo()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Model, Size, MediaType FROM Win32_DiskDrive WHERE Size > 0");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string media = o["MediaType"]?.ToString() ?? "";
                    if (!media.Contains("Fixed", StringComparison.OrdinalIgnoreCase) &&
                        !media.Contains("fixed", StringComparison.OrdinalIgnoreCase))
                        continue;
                    StorageModel = o["Model"]?.ToString()?.Trim() ?? "";
                    StorageSizeGb = o["Size"] != null
                        ? Convert.ToDouble(o["Size"]) / 1024.0 / 1024.0 / 1024.0 : 0;
                    break;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取硬盘信息失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string name = o["FriendlyName"]?.ToString() ?? "";
                    if (StorageModel.Length > 0 &&
                        !name.Equals(StorageModel, StringComparison.OrdinalIgnoreCase)) continue;
                    int bus = o["BusType"] != null ? Convert.ToInt32(o["BusType"]) : -1;
                    StorageBus = bus switch { 17 => "NVMe", 11 => "SATA", 8 => "RAID", 7 => "USB", _ => "" };
                    if (StorageModel.Length == 0) StorageModel = name;
                    break;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取硬盘总线类型失败"); }
    }

    private static double ReadMemoryTotalGb()
    {
        // 优先用已安装内存条容量(用户在任务管理器/设置里看到的就是 16GB),
        // 而不是"可见物理内存"(约 15.2GB) —— 后者会让界面显得和实际配置对不上。
        try
        {
            double installed = 0;
            using var s = new ManagementObjectSearcher(
                "SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (o["Capacity"] != null)
                        installed += Convert.ToDouble(o["Capacity"]) / 1024.0 / 1024.0 / 1024.0;
                }
            }
            if (installed > 0) return installed;
        }
        catch (Exception ex) { Logger.Debug(ex, "读取内存条容量失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (o["TotalVisibleMemorySize"] != null)
                        return Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1024.0 / 1024.0;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取内存总量失败"); }
        return 0;
    }

    private void ReadBatteryInfo()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Chemistry, DesignVoltage, EstimatedRunTime, BatteryStatus, MaxRechargeTime " +
                "FROM Win32_Battery");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    BatteryName = o["Name"]?.ToString()?.Trim() ?? "";
                    if (o["Chemistry"] != null)
                    {
                        BatteryChemistry = Convert.ToInt32(o["Chemistry"]) switch
                        {
                            3 => "铅酸",
                            4 => "镍镉",
                            5 => "镍氢",
                            6 => "锂离子",
                            7 => "锌空气",
                            8 => "锂聚合物",
                            _ => "未知"
                        };
                    }
                    if (o["DesignVoltage"] != null)
                        BatteryDesignVoltageV = Convert.ToDouble(o["DesignVoltage"]) / 1000.0;
                    if (o["BatteryStatus"] != null) BatteryStatus = Convert.ToInt32(o["BatteryStatus"]);
                    if (o["EstimatedRunTime"] != null)
                    {
                        int m = Convert.ToInt32(o["EstimatedRunTime"]);
                        BatteryEstimatedRunMinutes = m is > 0 and < 100000 ? m : -1;
                    }
                    if (o["MaxRechargeTime"] != null)
                    {
                        int m = Convert.ToInt32(o["MaxRechargeTime"]);
                        BatteryMaxRechargeMinutes = m is > 0 and < 100000 ? m : -1;
                    }
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取电池详细信息失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DeviceName, ManufactureName FROM BatteryStaticData");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (BatteryName.Length == 0)
                        BatteryName = o["DeviceName"]?.ToString()?.Trim() ?? "";
                    BatteryManufacturer = o["ManufactureName"]?.ToString()?.Trim() ?? "";
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取电池静态信息失败"); }
    }

    private static int? ReadBatteryCycles()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (o["CycleCount"] != null) return Convert.ToInt32(o["CycleCount"]);
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取电池循环次数失败"); }
        return null;
    }

    private void ReadDisplay()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string maker = Decode(o["ManufacturerName"] as ushort[]);
                    string name = Decode(o["UserFriendlyName"] as ushort[]);
                    DisplayPanel = name.Length > 0 ? name : maker;
                    MonitorSerial = Decode(o["SerialNumberID"] as ushort[]);
                    MonitorYear = o["YearOfManufacture"] != null ? Convert.ToInt32(o["YearOfManufacture"]) : 0;
                    if (DisplayPanel.Length > 0) break;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取显示器型号失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate " +
                "FROM Win32_VideoController");
            int bestArea = 0, bestHz = 0;
            var adapters = new List<string>();
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string name = o["Name"]?.ToString() ?? "";
                    if (name.Length > 0 && !adapters.Contains(name)) adapters.Add(name);

                    // 排除虚拟显示/远程桌面/基础显示适配器, 否则会读到 MuMu/Remote 的假分辨率
                    if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Indirect", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int w = o["CurrentHorizontalResolution"] != null ? Convert.ToInt32(o["CurrentHorizontalResolution"]) : 0;
                    int h = o["CurrentVerticalResolution"] != null ? Convert.ToInt32(o["CurrentVerticalResolution"]) : 0;
                    int hz = o["CurrentRefreshRate"] != null ? Convert.ToInt32(o["CurrentRefreshRate"]) : 0;
                    if (w <= 0 || h <= 0) continue;

                    int area = w * h;
                    if (area > bestArea || (area == bestArea && hz > bestHz))
                    {
                        bestArea = area;
                        bestHz = hz;
                        DisplayMode = hz > 0 ? $"{w}×{h} @ {hz}Hz" : $"{w}×{h}";
                    }
                }
            }
            DisplayAdapters = adapters;
        }
        catch (Exception ex) { Logger.Debug(ex, "读取显示分辨率失败"); }
    }

    private void ReadOs()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    string caption = (o["Caption"]?.ToString() ?? "").Replace("Microsoft ", "").Trim();
                    string build = o["BuildNumber"]?.ToString() ?? "";
                    string arch = o["OSArchitecture"]?.ToString() ?? "";
                    OsText = string.Join(" · ", new[] { caption, arch, build.Length > 0 ? $"build {build}" : "" }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                    DateTime? install = ParseCimDate(o["InstallDate"]);
                    if (install.HasValue) OsInstallDate = install.Value.ToString("yyyy-MM-dd");
                    DateTime? boot = ParseCimDate(o["LastBootUpTime"]);
                    if (boot.HasValue) LastBoot = boot.Value;
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取系统版本失败"); }
    }

    private void ReadMachineInfo()
    {
        string vendor = "", name = "", serial = "";
        string board = "", boardVer = "", boardSerial = "";
        string biosMfr = "", biosVer = "", biosDate = "";
        string chassis = "";

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Vendor, Name, IdentifyingNumber FROM Win32_ComputerSystemProduct");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    vendor = o["Vendor"]?.ToString()?.Trim() ?? "";
                    name = o["Name"]?.ToString()?.Trim() ?? "";
                    serial = o["IdentifyingNumber"]?.ToString()?.Trim() ?? "";
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取整机序列号失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    board = $"{o["Manufacturer"]?.ToString()?.Trim()} {o["Product"]?.ToString()?.Trim()}".Trim();
                    boardVer = o["Version"]?.ToString()?.Trim() ?? "";
                    boardSerial = o["SerialNumber"]?.ToString()?.Trim() ?? "";
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取主板信息失败"); }

        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    biosMfr = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                    biosVer = o["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                    DateTime? released = ParseAnyDate(o["ReleaseDate"]);
                    if (released.HasValue) biosDate = released.Value.ToString("yyyy-MM-dd");
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取 BIOS 信息失败"); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    int code = 0;
                    if (o["ChassisTypes"] is ushort[] types && types.Length > 0) code = types[0];
                    else if (o["ChassisTypes"] is Array arr && arr.Length > 0) code = Convert.ToInt32(arr.GetValue(0));
                    chassis = code switch
                    {
                        8 or 9 or 10 or 14 => $"笔记本({code})",
                        3 or 4 or 5 or 6 or 7 => $"台式机({code})",
                        _ => code > 0 ? $"机箱({code})" : ""
                    };
                }
                break;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取机箱类型失败"); }

        var lines = new List<string>();
        if (vendor.Length > 0 || name.Length > 0)
            lines.Add($"{vendor} {name}".Trim() + (serial.Length > 0 ? $" · SN {serial}" : ""));
        if (board.Length > 0)
            lines.Add($"主板 {board} {boardVer}".Trim() + (boardSerial.Length > 0 ? $" · SN {boardSerial}" : ""));
        if (biosVer.Length > 0)
            lines.Add($"BIOS {biosMfr} {biosVer}".Trim() + (biosDate.Length > 0 ? $" ({biosDate})" : ""));
        if (chassis.Length > 0) lines.Add($"机箱 {chassis}");
        MachineText = string.Join("\n", lines);
    }

    private void ReadNetwork()
    {
        var lines = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                string type = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "以太网",
                    _ => ni.NetworkInterfaceType.ToString()
                };
                string status = ni.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接";
                string speed = ni.Speed > 0
                    ? (ni.Speed >= 1_000_000_000 ? $"{ni.Speed / 1_000_000_000.0:F1}Gbps"
                        : ni.Speed >= 1_000_000 ? $"{ni.Speed / 1_000_000:F0}Mbps"
                        : $"{ni.Speed / 1000:F0}Kbps")
                    : "--";
                string ip = "--";
                try
                {
                    var v4 = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (v4 != null) ip = v4.Address.ToString();
                }
                catch { }
                string mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                lines.Add($"{type} {ni.Name} · {status} · {speed} · {ip} · {mac}");
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取网络适配器失败"); }
        NetworkText = lines.Count > 0 ? string.Join("\n", lines) : "未检测到网络适配器";
    }

    private static DateTime? ParseCimDate(object? value)
    {
        try
        {
            string text = value?.ToString() ?? "";
            if (text.Length == 0) return null;
            return ManagementDateTimeConverter.ToDateTime(text);
        }
        catch { return null; }
    }

    private static DateTime? ParseAnyDate(object? value)
    {
        if (value is DateTime dt) return dt;
        DateTime? cim = ParseCimDate(value);
        if (cim.HasValue) return cim;
        if (value != null && DateTime.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string Decode(ushort[]? data)
    {
        if (data == null || data.Length == 0) return "";
        return new string(data.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim();
    }
}
