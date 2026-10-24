using System;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MEVJiaolongControlCenter.Core.EC;
using MEVJiaolongControlCenter.Core.Hardware;
using MEVJiaolongControlCenter.Core.Power;
using MEVJiaolongControlCenter.Core.Wmi;
using NLog;

namespace MEVJiaolongControlCenter.Services;

public partial class ControlService : ObservableObject, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly TongfangEc? _ec;
    private readonly HardwareMonitor _hardwareMonitor;
    private readonly PowerProfileManager _powerProfileManager;

    public bool EcConnected { get; private set; }
    public string EcErrorMessage { get; private set; } = "";
    public TongfangEc? Ec => _ec;

    private bool _isInitialized;

    // 初始必须是 Unknown: EC 不可用(台式机/非蛟龙机型)时不能默认成"游戏/混合",
    // 否则界面会在没读到真实档位的情况下显示一个看似确定的状态。
    [ObservableProperty]
    private PerformanceMode _currentPerformanceMode = PerformanceMode.Unknown;

    [ObservableProperty]
    private GpuMode _currentGpuMode = GpuMode.Unknown;

    [ObservableProperty]
    private float _cpuTemp;

    [ObservableProperty]
    private float _gpuTemp;

    [ObservableProperty]
    private int _cpuFanRpm;

    [ObservableProperty]
    private int _gpuFanRpm;

    [ObservableProperty]
    private float? _cpuPower;

    [ObservableProperty]
    private float? _gpuPower;

    [ObservableProperty]
    private float? _cpuLoad;

    [ObservableProperty]
    private float? _gpuLoad;

    [ObservableProperty]
    private float? _batteryLevel;

    [ObservableProperty]
    private bool _isMaxFan;

    // 注: 风扇"强制最高速/一键最大"独立按钮已移除。
    // 现在切狂暴模式时由 SetPerformanceMode 自动开启0x14满速开关, 离开自动恢复。

    // === EC 实时遥测 (来自 MICommonInterface 全命令扫描确认的只读数据) ===
    public EcStatus? LatestEcStatus { get; private set; }

    [ObservableProperty]
    private string _ecPerfModeText = "--";

    [ObservableProperty]
    private string _ecGpuModeText = "--";

    [ObservableProperty]
    private string _ecAcTypeText = "--";

    [ObservableProperty]
    private int _ecCpuTemp;

    [ObservableProperty]
    private bool _ecMaxFanOn;

    [ObservableProperty]
    private int _ecKbBrightness = -1;

    [ObservableProperty]
    private string _ecKbColorHex = "#000000";

    public ControlService(TongfangEc? ec, HardwareMonitor hardwareMonitor)
    {
        _ec = ec;
        _hardwareMonitor = hardwareMonitor;
        _powerProfileManager = new PowerProfileManager();

        if (ec != null)
        {
            EcConnected = true;
        }

        _hardwareMonitor.DataUpdated += OnHardwareDataUpdated;
    }

    public Task InitializeAsync()
    {
        if (_isInitialized) return Task.CompletedTask;

        try
        {
            Logger.Info("正在初始化控制服务...");

            if (EcConnected && _ec != null)
            {
                CurrentPerformanceMode = _ec.GetPerformanceMode();
                CurrentGpuMode = _ec.GetGpuMode();
            }

            _hardwareMonitor.StartMonitoring(2000);

            _isInitialized = true;
            Logger.Info("控制服务初始化完成");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "控制服务初始化失败");
            throw;
        }

        // EC 读取是同步的(走常驻 PowerShell 桥), 这里不切线程: 属性更新必须留在 UI 线程
        return Task.CompletedTask;
    }

    private long _lastFanRpmPoll;
    private const int FAN_POLL_INTERVAL_MS = 3000;

    /// <summary>最近一次硬件快照(用于状态导出)</summary>
    public HardwareData? LatestHardwareData { get; private set; }

    private void OnHardwareDataUpdated(object? sender, HardwareData data)
    {
        LatestHardwareData = data;
        // CPU温度: 优先LibreHardwareMonitor的Package温度, 回退到核心最高温
        CpuTemp = data.CpuPackageTemp ?? data.MaxCpuTemp;
        GpuTemp = data.GpuTemp ?? 0;
        CpuPower = data.CpuPower;
        GpuPower = data.GpuPower;
        BatteryLevel = data.BatteryLevel;

        if (data.CpuLoads.TryGetValue("CPU Total", out var cpuLoad))
        {
            CpuLoad = cpuLoad;
        }

        GpuLoad = data.GpuLoad;

        // 优先使用LibreHardwareMonitor读取到的风扇转速(两条通道分别判断, 单风扇机型不会被写成 0)
        TryGetLhmFan(data, out int lhmCpuFan, out int lhmGpuFan, out bool hasCpuFan, out bool hasGpuFan);
        if (hasCpuFan) CpuFanRpm = lhmCpuFan;
        if (hasGpuFan) GpuFanRpm = lhmGpuFan;

        // 无论 LHM 有没有风扇数据, 都定期刷新 EC 状态面板(功耗/模式/AC等), 内部自有 3s 节流
        PollEcStatus();
    }

    private static void TryGetLhmFan(HardwareData data,
        out int cpuFan, out int gpuFan, out bool hasCpu, out bool hasGpu)
    {
        cpuFan = 0; gpuFan = 0; hasCpu = false; hasGpu = false;
        foreach (var kv in data.FanSpeeds)
        {
            if (!kv.Value.HasValue || kv.Value.Value <= 0) continue;
            var name = kv.Key.ToLowerInvariant();
            if (name.Contains("gpu"))
            {
                gpuFan = (int)kv.Value.Value;
                hasGpu = true;
            }
            else if (name.Contains("cpu"))
            {
                cpuFan = (int)kv.Value.Value;
                hasCpu = true;
            }
            // 既不含 cpu 也不含 gpu 的风扇(如 System Fan)不强行归到 CPU, 避免覆盖真实值
        }
    }

    private int _ecPollRunning;
    private long _lastEcOkTicks;

    /// <summary>最近 15 秒内是否成功读到过一轮 EC。界面用它判断"连接是否还活着", 掉线时降级为只读。</summary>
    public bool EcHealthy => _lastEcOkTicks != 0 && Environment.TickCount64 - _lastEcOkTicks < 15000;

    private void PollEcStatus()
    {
        // 不看窗口是否可见: EC 档位/风扇/温度是界面与托盘共用的实时数据源, 隐藏时停掉轮询
        // 会让托盘提示和重新显示后的头几秒停在旧值。节流靠下面的 FAN_POLL_INTERVAL_MS。
        long now = Environment.TickCount64;
        if (now - _lastFanRpmPoll < FAN_POLL_INTERVAL_MS) return;
        _lastFanRpmPoll = now;

        // 关键: 不能在监控线程上同步读。ReadEcStatus 慢的时候(桥超时/重建)最长要几十秒,
        // 会把整个硬件监控循环卡住, 温度和风扇数据也跟着停止更新。改成后台执行 + 同一时刻只允许一轮。
        if (Interlocked.Exchange(ref _ecPollRunning, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { ReadEcStatusOnce(); }
            catch (Exception ex) { Logger.Debug(ex, "EC 状态轮询异常"); }
            finally { Interlocked.Exchange(ref _ecPollRunning, 0); }
        });
    }

    /// <summary>绕过节流, 立即读取一次EC状态(用于"刷新"按钮)</summary>
    public bool RefreshEcStatusNow()
    {
        _lastFanRpmPoll = Environment.TickCount64;
        return ReadEcStatusOnce();
    }

    private bool ReadEcStatusOnce()
    {
        var wmi = _ec?.Wmi;
        if (wmi == null || !wmi.IsAvailable) return false;

        try
        {
            var s = wmi.ReadEcStatus();
            if (!s.Valid) return false;

            LatestEcStatus = s;
            _lastEcOkTicks = Environment.TickCount64;
            // -1 = 这一项没读到(桥超时), 保持上一次的值; 0 是合法读数(风扇确实停转), 必须照实显示
            if (s.CpuFanRpm >= 0) CpuFanRpm = s.CpuFanRpm;
            if (s.GpuFanRpm >= 0) GpuFanRpm = s.GpuFanRpm;
            if (s.CpuTemp >= 0) EcCpuTemp = s.CpuTemp;

            EcPerfModeText = s.PerfModeText;
            EcGpuModeText = s.GpuModeText;
            EcAcTypeText = s.AcTypeText;
            EcMaxFanOn = s.MaxFanOn == 1;
            EcKbBrightness = s.KbBrightness;
            EcKbColorHex = $"#{s.KbR:X2}{s.KbG:X2}{s.KbB:X2}";

            // 厂商 Fn 快捷键也能改档位/显卡模式: 回读到与界面不一致时以 EC 为准,
            // 否则控制台会一直显示用户没在用的档位。VM 侧有 _suppressModeWrite, 不会反向再写 EC。
            if (s.PerfMode >= 0)
            {
                var pm = s.PerfMode switch
                {
                    0 => PerformanceMode.Balanced,
                    1 => PerformanceMode.Turbo,
                    2 => PerformanceMode.Quiet,
                    _ => PerformanceMode.Unknown
                };
                if (pm != PerformanceMode.Unknown && pm != CurrentPerformanceMode)
                {
                    CurrentPerformanceMode = pm;
                    IsMaxFan = s.MaxFanOn == 1;
                    Logger.Info($"EC 档位被外部改变, 已同步: {pm}");
                }
            }
            if (s.GpuMode >= 0)
            {
                var gm = s.GpuMode == 1 ? GpuMode.Discrete : GpuMode.Hybrid;
                if (gm != CurrentGpuMode)
                {
                    CurrentGpuMode = gm;
                    Logger.Info($"EC 显卡模式被外部改变, 已同步: {gm}");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "WMI读取EC状态失败");
            return false;
        }
    }

    /// <summary>导出当前EC状态与硬件快照为文本(用于"导出"按钮)</summary>
    public string ExportStatusSnapshot()
    {
        RefreshEcStatusNow();
        var data = LatestHardwareData;
        var s = LatestEcStatus;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("MEV蛟龙控制台 - 状态快照");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine("【EC 实时状态】");
        if (s != null && s.Valid)
        {
            sb.AppendLine($"  性能模式   : {s.PerfModeText} ({s.PerfMode})");
            sb.AppendLine($"  显卡模式   : {s.GpuModeText} ({s.GpuMode})");
            sb.AppendLine($"  电源类型   : {s.AcTypeText} ({s.AcType})");
            sb.AppendLine($"  CPU温度    : {s.CpuTemp} °C");
            sb.AppendLine($"  CPU风扇    : {s.CpuFanRpm} RPM");
            sb.AppendLine($"  GPU风扇    : {s.GpuFanRpm} RPM");
            sb.AppendLine($"  最大风扇   : {(s.MaxFanOn == 1 ? "开启" : "关闭")}");
            sb.AppendLine($"  键盘模式   : {s.KbMode}");
            sb.AppendLine($"  键盘亮度   : {s.KbBrightness} / 3");
            sb.AppendLine($"  键盘颜色   : #{s.KbR:X2}{s.KbG:X2}{s.KbB:X2}");
        }
        else
        {
            sb.AppendLine("  (EC状态不可用)");
        }

        sb.AppendLine();
        sb.AppendLine("【硬件监控】");
        if (data != null)
        {
            sb.AppendLine($"  CPU包温度  : {data.CpuPackageTemp?.ToString("0.#") ?? "-"} °C");
            sb.AppendLine($"  CPU最高温度: {data.MaxCpuTemp:0.#} °C");
            sb.AppendLine($"  CPU功耗    : {data.CpuPower?.ToString("0.#") ?? "-"} W");
            sb.AppendLine($"  GPU名称    : {(string.IsNullOrEmpty(data.GpuName) ? "-" : data.GpuName)}");
            sb.AppendLine($"  GPU类型    : {(data.GpuIsDiscrete ? "独立显卡 (RTX 4060)" : "核显 (Radeon)")}");
            sb.AppendLine($"  GPU温度    : {data.GpuTemp?.ToString("0.#") ?? "-"} °C");
            sb.AppendLine($"  GPU功耗    : {data.GpuPower?.ToString("0.#") ?? "-"} W");
            sb.AppendLine($"  GPU负载    : {data.GpuLoad?.ToString("0.#") ?? "-"} %");
            sb.AppendLine($"  内存占用   : {data.MemoryLoad?.ToString("0.#") ?? "-"} %");
            sb.AppendLine($"  电池电量   : {data.BatteryLevel?.ToString("0.#") ?? "-"} %");
            sb.AppendLine($"  电池损耗   : {data.BatteryDegradation?.ToString("0.#") ?? "-"} %");
            sb.AppendLine($"  电池电压   : {data.BatteryVoltage?.ToString("0.###") ?? "-"} V");
            sb.AppendLine($"  CPU风扇    : {CpuFanRpm} RPM");
            sb.AppendLine($"  GPU风扇    : {GpuFanRpm} RPM");
        }
        else
        {
            sb.AppendLine("  (硬件数据不可用)");
        }

        sb.AppendLine();
        sb.AppendLine(new string('=', 48));
        var text = sb.ToString();
        Logger.Info("已生成状态快照导出");
        return text;
    }

    /// <summary>
    /// 切换性能档并回读校验。返回 false 表示 EC 没接受(或读不回来), 调用方必须把界面回滚到真实档位,
    /// 否则用户以为已经切到狂暴档, 实际功耗墙与风扇曲线一动没动。
    /// </summary>
    public bool SetPerformanceMode(PerformanceMode mode)
    {
        try
        {
            if (_ec != null)
            {
                // 写入没被 EC/桥确认时直接判失败: 否则桥超时那一轮会显示"已切换狂暴",
                // 而满速风扇和 0x17 功耗墙其实一条都没下发。
                if (!_ec.SetPerformanceMode(mode))
                {
                    EcErrorMessage = "EC 未确认性能档写入(桥无应答或被拒绝)";
                    Logger.Warn(EcErrorMessage);
                    return false;
                }

                // 写后回读: 回读到别的档位说明 EC 没接受。
                // 回读 Unknown 表示这台机器只能写不能读(部分机型如此), 写入已确认, 不算失败。
                var echo = _ec.GetPerformanceMode();
                if (echo != PerformanceMode.Unknown && echo != mode)
                {
                    EcErrorMessage = $"EC 未确认性能档切换(回读={echo})";
                    Logger.Warn(EcErrorMessage);
                    return false;
                }
            }

            CurrentPerformanceMode = mode;

            // 狂暴档自动拉满风扇(0x14满速开关), 离开狂暴档自动恢复EC温控曲线
            if (mode == PerformanceMode.Turbo)
            {
                _ec?.SetMaxFanSpeed();
                IsMaxFan = true;
            }
            else
            {
                _ec?.SetFanAuto(FanTarget.Both);
                IsMaxFan = false;
            }

            var profile = ProfileFor(mode);
            if (profile != null)
            {
                ApplyPowerProfile(profile);
            }

            Logger.Info($"性能模式已切换: {mode}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"切换性能模式失败: {mode}");
            return false;
        }
    }

    private PowerProfile? ProfileFor(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Quiet => _powerProfileManager.GetProfile("Quiet"),
        PerformanceMode.Balanced => _powerProfileManager.GetProfile("Balanced"),
        PerformanceMode.Turbo => _powerProfileManager.GetProfile("Turbo"),
        _ => null
    };

    /// <summary>按当前性能档位重新下发功耗配置 (用户清除功耗墙覆盖后调用)</summary>
    public void ReapplyCurrentPowerProfile()
    {
        var profile = ProfileFor(CurrentPerformanceMode);
        if (profile != null) ApplyPowerProfile(profile);
    }

    /// <summary>
    /// 切换显卡模式(MUX)并回读校验。返回 false 表示 EC 没有接受写入 —— 这里绝不能假装切换成功,
    /// 用户会按提示重启电脑, 而 MUX 实际一动没动。
    /// </summary>
    public bool SetGpuMode(GpuMode mode)
    {
        try
        {
            if (_ec != null)
            {
                if (!_ec.SetGpuMode(mode))
                {
                    EcErrorMessage = "EC 未确认显卡模式写入(桥无应答或被拒绝)";
                    Logger.Warn(EcErrorMessage);
                    return false;
                }

                // 0x09 可回读: 回读不一致就是没切过去, 绝不能让用户按提示重启电脑
                var echo = _ec.GetGpuMode();
                if (echo != mode)
                {
                    EcErrorMessage = $"EC 未确认显卡模式切换(回读={echo})";
                    Logger.Warn(EcErrorMessage);
                    return false;
                }
            }

            CurrentGpuMode = mode;
            Logger.Info($"显卡模式已切换: {mode}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"切换显卡模式失败: {mode}");
            return false;
        }
    }

    /// <summary>
    /// 用户手动指定的 CPU 功耗墙(SPL, 瓦)。>0 时优先于档位预设, 且切换性能模式不会被冲掉;
    /// 0 = 跟随档位预设。EC 0x17 是只写寄存器, 无法回读, 生效情况靠满载频率实测验证。
    /// </summary>
    public int CpuPowerOverrideWatts { get; set; }

    private void ApplyPowerProfile(PowerProfile profile)
    {
        try
        {
            int spl = CpuPowerOverrideWatts > 0 ? CpuPowerOverrideWatts : profile.CpuPl1;
            int sppt = CpuPowerOverrideWatts > 0
                ? Math.Max(CpuPowerOverrideWatts, profile.CpuPl2)
                : profile.CpuPl2;
            _ec?.SetCpuPowerLimit(spl, sppt);

            Logger.Info($"已应用功耗配置: {profile.Name}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "应用功耗配置失败");
        }
    }


    public void Dispose()
    {
        _hardwareMonitor.DataUpdated -= OnHardwareDataUpdated;
        _hardwareMonitor.Dispose();
        _ec?.Dispose();
        GC.SuppressFinalize(this);
    }
}
