using System;
using System.Management;
using NLog;

namespace MEVJiaolongControlCenter.Core.Wmi;

public class WmiInterface : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private ManagementScope? _scope;
    private ManagementObject? _wmiObj;
    private PowerShellWmiBridge? _psBridge;
    private bool _disposed;
    private bool _directWmiFailed;
    private readonly object _directLock = new();

    private const byte VERB_GET = 0xFA;
    private const byte VERB_SET = 0xFB;

    private const byte CMD_PERF_MODE = 0x08;
    private const byte CMD_GPU_MODE = 0x09;
    private const byte CMD_FAN_SPEED = 0x0D;
    /// <summary>A 面 Logo 背光开关 (0/1)。0x00-0xFF 全量扫描中发现的未记录命令, 实测可写可回读,
    /// 且不随键盘灯亮度变化。没有配套的颜色命令, 因此仅支持开关。</summary>
    private const byte CMD_LOGO_LIGHT = 0x0F;
    private const byte CMD_AC_TYPE = 0x13;
    private const byte CMD_MAX_FAN_SWITCH = 0x14;
    private const byte CMD_CPU_TEMP = 0x16;
    private const byte CMD_CPU_POWER = 0x17;
    private const byte CMD_KB_MODE = 0x10;
    private const byte CMD_KB_COLOR = 0x11;
    private const byte CMD_KB_BRIGHTNESS = 0x12;

    public bool IsAvailable { get; private set; }

    private const string WmiRelativePath = @"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'";

    public bool Initialize()
    {
        // 先做一次轻量的设备实例检查: 台式机/非蛟龙机型上没有这个 ACPI 设备,
        // 后面启动 PowerShell 桥、连 root\WMI 都是白费, 还会留下常驻子进程。
        if (!EcInstanceExists())
        {
            Logger.Warn("未找到 MICommonInterface 设备实例, 判定 EC 不可用");
            IsAvailable = false;
            return false;
        }

        // 桥接仍是主通道: 本机 System.Management/CIM 直连 InvokeMethod 都会报"无效的对象",
        // 只有 Windows PowerShell 的 WMI 调用能稳定执行厂商方法。
        bool bridgeOk = false;
        try
        {
            _psBridge = new PowerShellWmiBridge();
            bridgeOk = _psBridge.Start();
            if (bridgeOk) Logger.Info("PowerShell WMI桥接已启动 (主通道)");
            else
            {
                Logger.Warn("PowerShell WMI桥接启动失败, 将尝试直接WMI");
                // 置 null 前必须 Dispose: 桥对象可能已经拉起了一个子进程, 只丢引用就是永久残留
                try { _psBridge.Dispose(); } catch { }
                _psBridge = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "PowerShell WMI桥接初始化异常");
            try { _psBridge?.Dispose(); } catch { }
            _psBridge = null;
        }

        bool directOk = false;
        try
        {
            _scope = new ManagementScope(@"root\WMI");
            _scope.Connect();

            if (_scope.IsConnected)
            {
                var path = new ManagementPath(WmiRelativePath);
                _wmiObj = new ManagementObject(_scope, path, null);
                var instanceName = _wmiObj["InstanceName"]?.ToString() ?? "";
                var active = _wmiObj["Active"];
                Logger.Info($"MICommonInterface 已连接: InstanceName={instanceName}, Active={active}");
                directOk = true;
            }
            else
            {
                Logger.Warn("WMI root\\WMI 连接失败, 仅使用 PowerShell 桥");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "直连 WMI 初始化失败, 仅使用 PowerShell 桥");
            _scope = null;
            _wmiObj = null;
        }

        IsAvailable = bridgeOk || directOk;
        if (!IsAvailable)
            Logger.Error("MICommonInterface 不可用: 设备存在但桥接与直连 WMI 均初始化失败");
        return IsAvailable;
    }

    /// <summary>本机是否真的暴露 MICommonInterface ACPI 设备(台式机/非蛟龙机型通常没有)。</summary>
    private static bool EcInstanceExists()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName FROM MICommonInterface");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                if (item != null) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "查询 MICommonInterface 实例失败");
            return false;
        }
    }

    private byte[]? SendCommand(byte verb, byte commandId, byte[]? payload = null)
    {
        var packet = new byte[32];
        packet[0] = 32;
        packet[1] = verb;
        packet[3] = commandId;

        if (payload != null)
        {
            Array.Copy(payload, 0, packet, 4, Math.Min(payload.Length, 28));
        }

        // 桥接优先(已验证); 桥接失败时才尝试直连作为兜底。
        if (_psBridge != null)
        {
            byte[]? viaBridge = SendViaBridge(verb, commandId, payload);
            if (viaBridge != null) return viaBridge;
            Logger.Warn($"PS桥接失败, 尝试直接WMI: cmd=0x{commandId:X2}");
        }

        if (_directWmiFailed) return null;
        return SendCommandDirect(packet, verb, commandId);
    }

    private byte[]? SendViaBridge(byte verb, byte commandId, byte[]? payload)
    {
        if (_psBridge == null) return null;
        try
        {
            byte[]? result = verb == VERB_GET
                ? _psBridge.SendGet(commandId)
                : _psBridge.SendSet(commandId, payload ?? Array.Empty<byte>());

            if (result != null)
                Logger.Debug($"PS桥接成功: cmd=0x{commandId:X2}, 响应长度={result.Length}");
            else
                Logger.Warn($"PS桥接失败: cmd=0x{commandId:X2}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"PS桥接异常: cmd=0x{commandId:X2}");
            return null;
        }
    }

    private byte[]? SendCommandDirect(byte[] packet, byte verb, byte commandId)
    {
        if (_scope == null) return null;

        // _scope 会被 UI 线程和 EC 轮询的后台 Task 同时用到, 直连路径必须串行。
        // 每次新建 ManagementObject: 复用初始化时那个会报"无效的对象"(WMI 实例上下文过期)。
        lock (_directLock)
        {
            try
            {
                // 从查询结果拿实例再调用: 直接 new ManagementObject(path) 在这台机器上
                // InvokeMethod 会报"无效的对象", 只有 searcher.Get() 返回的实例可调用。
                using var searcher = new ManagementObjectSearcher(@"root\WMI",
                    "SELECT * FROM MICommonInterface");
                ManagementObject? instance = null;
                foreach (ManagementObject found in searcher.Get())
                {
                    instance = found;
                    break;
                }
                if (instance == null) return null;

                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("MiInterface");
                    inParams["InData"] = (byte[])packet;

                    using var res = instance.InvokeMethod("MiInterface",
                        (ManagementBaseObject)inParams, new InvokeMethodOptions());
                if (res == null)
                {
                    Logger.Warn("直接WMI未返回结果");
                    return null;
                }

                foreach (PropertyData p in res.Properties)
                {
                    if (p.Name == "OutData" && p.Value is byte[] outBytes)
                        return outBytes;
                }

                Logger.Warn("直接WMI未返回OutData");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"直接WMI调用失败, 切换到桥接: cmd=0x{commandId:X2}");
                _directWmiFailed = true;
                return null;
            }
        }
    }

    #region 性能模式

    public int GetPerformanceMode()
    {
        var response = SendCommand(VERB_GET, CMD_PERF_MODE);
        if (response == null || response.Length < 5) return -1;

        var mode = response[4];
        Logger.Debug($"WMI读取性能模式: {mode}");
        return mode;
    }

    public bool SetPerformanceMode(int mode)
    {
        Logger.Info($"WMI设置性能模式: {mode}");
        var response = SendCommand(VERB_SET, CMD_PERF_MODE, new[] { (byte)mode });
        return response != null;
    }

    #endregion

    #region 显卡模式

    public int GetGpuMode()
    {
        var response = SendCommand(VERB_GET, CMD_GPU_MODE);
        if (response == null || response.Length < 5) return -1;

        var mode = response[4];
        Logger.Debug($"WMI读取显卡模式: {mode}");
        return mode;
    }

    public bool SetGpuMode(int mode)
    {
        Logger.Info($"WMI设置显卡模式: {mode}");
        var response = SendCommand(VERB_SET, CMD_GPU_MODE, new[] { (byte)mode });
        return response != null;
    }

    #endregion

    #region 风扇

    public (int cpuRpm, int gpuRpm) GetFanRpm()
    {
        var response = SendCommand(VERB_GET, CMD_FAN_SPEED);
        // 读失败返回 -1 而不是 0: 0 是"风扇真的停转"的合法读数,
        // 用 0 表示失败会让界面在桥抖动时把停转当成真实状态。
        if (response == null || response.Length < 8) return (-1, -1);

        var cpuRpm = response[4] | (response[5] << 8);
        var gpuRpm = response[6] | (response[7] << 8);
        return (cpuRpm, gpuRpm);
    }

    public bool SetMaxFanSwitch(bool enabled)
    {
        var response = SendCommand(VERB_SET, CMD_MAX_FAN_SWITCH, new[] { (byte)(enabled ? 1 : 0) });
        return response != null;
    }

    public int GetMaxFanSwitch()
    {
        var response = SendCommand(VERB_GET, CMD_MAX_FAN_SWITCH);
        if (response == null || response.Length < 5) return -1;
        return response[4];
    }

    #endregion

    #region 温度与功耗

    public int GetCpuTemp()
    {
        var response = SendCommand(VERB_GET, CMD_CPU_TEMP);
        if (response == null || response.Length < 5) return 0;
        return response[4];
    }

    public int GetAcType()
    {
        var response = SendCommand(VERB_GET, CMD_AC_TYPE);
        if (response == null || response.Length < 5) return 0;
        return response[4];
    }

    /// <summary>
    /// 一次性读取所有 EC 支持的遥测数据 (基于 0x00-0xFF 全命令扫描确认的受支持命令集)。
    /// 功耗限制(0x17)为只写命令, EC 不回读, 故不包含。
    /// </summary>
    public EcStatus ReadEcStatus()
    {
        var s = new EcStatus();
        if (!IsAvailable) return s;

        try
        {
            s.PerfMode = GetPerformanceMode();
            s.GpuMode = GetGpuMode();
            var (cpu, gpu) = GetFanRpm();
            s.CpuFanRpm = cpu;
            s.GpuFanRpm = gpu;
            s.CpuTemp = GetCpuTemp();
            s.AcType = GetAcType();
            s.MaxFanOn = GetMaxFanSwitch();
            s.LogoLight = GetLogoLight();
            s.KbMode = GetKeyboardMode();
            s.KbBrightness = GetKeyboardBrightness();
            var (r, g, b) = GetKeyboardColor();
            s.KbR = r; s.KbG = g; s.KbB = b;

            // 只有关键命令真的读到了才标有效: 桥超时/WMI 挂掉时各字段会是 -1,
            // 若仍标 Valid, 上层会把"未知"当成真实状态覆盖界面。
            s.Valid = s.PerfMode >= 0 && s.GpuMode >= 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取EC状态失败");
        }

        return s;
    }

    #endregion

    #region 键盘灯效

    public int GetKeyboardMode()
    {
        var response = SendCommand(VERB_GET, CMD_KB_MODE);
        if (response == null || response.Length < 5) return -1;
        return response[4];
    }

    public bool SetKeyboardMode(int mode)
    {
        var response = SendCommand(VERB_SET, CMD_KB_MODE, new[] { (byte)mode });
        return response != null;
    }

    public (byte r, byte g, byte b) GetKeyboardColor()
    {
        var response = SendCommand(VERB_GET, CMD_KB_COLOR);
        if (response == null || response.Length < 7) return (0, 0, 0);
        return (response[4], response[5], response[6]);
    }

    public bool SetKeyboardColor(byte r, byte g, byte b)
    {
        var response = SendCommand(VERB_SET, CMD_KB_COLOR, new[] { r, g, b });
        return response != null;
    }

    public int GetKeyboardBrightness()
    {
        var response = SendCommand(VERB_GET, CMD_KB_BRIGHTNESS);
        if (response == null || response.Length < 5) return -1;
        return response[4];
    }

    public bool SetKeyboardBrightness(int brightness)
    {
        if (brightness < 0) brightness = 0;
        if (brightness > 3) brightness = 3;
        var response = SendCommand(VERB_SET, CMD_KB_BRIGHTNESS, new[] { (byte)brightness });
        return response != null;
    }

    /// <summary>A 面 Logo 背光状态: 0=灭 1=亮, -1=读取失败/不支持</summary>
    public int GetLogoLight()
    {
        var response = SendCommand(VERB_GET, CMD_LOGO_LIGHT);
        if (response == null || response.Length < 5) return -1;
        return response[4];
    }

    public bool SetLogoLight(bool on)
    {
        var response = SendCommand(VERB_SET, CMD_LOGO_LIGHT, new[] { (byte)(on ? 1 : 0) });
        return response != null;
    }

    #endregion

    #region CPU功耗

    public bool SetCpuPower(byte subCommand, byte value)
    {
        var response = SendCommand(VERB_SET, CMD_CPU_POWER, new[] { subCommand, value });
        return response != null;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 先摘掉可用标志: 还在飞的轮询/灯效回调会立刻短路, 不再碰正在释放的桥
        IsAvailable = false;
        _psBridge?.Dispose();
        _wmiObj = null;
        _scope = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// EC 全命令扫描后确认的可读遥测快照。
/// </summary>
public class EcStatus
{
    public bool Valid { get; set; }
    public int PerfMode { get; set; } = -1;      // 0x08: 0=平衡 1=狂暴 2=静音
    public int GpuMode { get; set; } = -1;       // 0x09: 0=混合 1=独显直连
    public int CpuFanRpm { get; set; }           // 0x0D
    public int GpuFanRpm { get; set; }           // 0x0D
    public int CpuTemp { get; set; }             // 0x16
    public int AcType { get; set; }              // 0x13
    public int MaxFanOn { get; set; } = -1;      // 0x14: 1=最大风速开
    public int LogoLight { get; set; } = -1;     // 0x0F: 1=A面Logo背光亮
    public int KbMode { get; set; } = -1;        // 0x10
    public int KbBrightness { get; set; } = -1;  // 0x12: 0-3
    public byte KbR { get; set; }                // 0x11
    public byte KbG { get; set; }
    public byte KbB { get; set; }

    public string PerfModeText => PerfMode switch
    {
        0 => "游戏", 1 => "狂暴", 2 => "办公", _ => "未知"
    };
    public string GpuModeText => GpuMode switch
    {
        0 => "混合", 1 => "独显直连", _ => "未知"
    };
    public string AcTypeText => AcType switch
    {
        0 => "电池", 1 => "标准适配器", 2 => "原装适配器", _ => $"未知({AcType})"
    };
}
