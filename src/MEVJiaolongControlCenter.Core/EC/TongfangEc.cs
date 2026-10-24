using System;
using MEVJiaolongControlCenter.Core.Wmi;
using NLog;

namespace MEVJiaolongControlCenter.Core.EC;

/// <summary>
/// 同方模具 EC 控制层。全部控制都走 MICommonInterface WMI Provider。
/// 裸 0x62/0x66 端口访问在本机实测不可用(连 i8042 自检命令都不应答), 详见 docs/EC_REGISTERS.md。
/// </summary>
public class TongfangEc : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly WmiInterface? _wmi;

    public TongfangEc(WmiInterface? wmi)
    {
        _wmi = wmi;
    }

    #region 性能模式 (WMI: 0=平衡, 1=狂暴, 2=静音)

    public PerformanceMode GetPerformanceMode()
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn("WMI不可用,无法读取性能模式");
            return PerformanceMode.Unknown;
        }

        var value = _wmi.GetPerformanceMode();
        if (value < 0) return PerformanceMode.Unknown;

        return value switch
        {
            0 => PerformanceMode.Balanced,
            1 => PerformanceMode.Turbo,
            2 => PerformanceMode.Quiet,
            _ => PerformanceMode.Unknown
        };
    }

    /// <summary>写性能档。返回 EC/桥是否确认收到; false 时调用方不能报"已切换"。</summary>
    public bool SetPerformanceMode(PerformanceMode mode)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Error($"无法设置性能模式: {mode} (WMI不可用)");
            return false;
        }

        // 绝不把 Unknown 映射成 0: 那是"没读到", 写下去等于静默把档位改成 Balanced
        if (mode == PerformanceMode.Unknown)
        {
            Logger.Warn("性能模式未知(读取失败), 放弃写入");
            return false;
        }

        var value = mode switch
        {
            PerformanceMode.Balanced => 0,
            PerformanceMode.Turbo => 1,
            PerformanceMode.Quiet => 2,
            _ => 0
        };

        Logger.Info($"通过WMI设置性能模式: {mode} (WMI值={value})");
        if (_wmi.SetPerformanceMode(value))
        {
            Logger.Info($"WMI设置性能模式成功: {mode}");
            return true;
        }
        Logger.Warn("WMI设置性能模式失败(桥无应答/EC 拒绝)");
        return false;
    }

    #endregion

    #region 风扇 (WMI cmd 0x0D/0x14/0x15)

    public (int cpuRpm, int gpuRpm) GetFanRpm()
    {
        if (_wmi != null && _wmi.IsAvailable)
        {
            return _wmi.GetFanRpm();
        }

        Logger.Warn("WMI不可用,EC风扇寄存器地址未确认");
        return (-1, -1);
    }

    public void SetFanAuto(FanTarget target)
    {
        if (_wmi != null && _wmi.IsAvailable)
        {
            if (_wmi.SetMaxFanSwitch(false))
            {
                Logger.Info("已通过WMI关闭最大风扇模式(自动)");
                return;
            }
        }
        Logger.Warn($"风扇自动模式切换失败: target={target}");
    }

    public void SetMaxFanSpeed()
    {
        if (_wmi != null && _wmi.IsAvailable)
        {
            if (_wmi.SetMaxFanSwitch(true))
            {
                Logger.Info("已通过WMI开启最大风扇模式");
                return;
            }
        }
        Logger.Warn("最大风扇转速切换失败");
    }

    /// <summary>
    /// 实测该EC固件不暴露任何软件手动转速接口: 0x15(最大转速)只存储不生效,
    /// 0x14(最大风扇开关)对RPM无可测影响, 温度寄存器0x16只读(写入被物理传感器覆盖),
    /// EC RAM占空比寄存器只读, 0x18-0x40无隐藏风扇命令。风扇转速完全由物理温度门控,
    /// 因此这里只保留"开关最大风扇模式", 不提供手动转速(避免做出无效的假控件)。
    /// </summary>
    #endregion

    #region 显卡模式 (WMI: 0=混合, 1=独显直连)

    public GpuMode GetGpuMode()
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn("WMI不可用,无法读取显卡模式");
            return GpuMode.Unknown;
        }

        var value = _wmi.GetGpuMode();
        return value switch
        {
            0 => GpuMode.Hybrid,
            1 => GpuMode.Discrete,
            _ => GpuMode.Unknown
        };
    }

    /// <summary>写显卡模式(MUX)。返回 EC/桥是否确认收到; false 时调用方不能报"已切换"。</summary>
    public bool SetGpuMode(GpuMode mode)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Error($"无法设置显卡模式: {mode} (WMI不可用)");
            return false;
        }

        // 同上: Unknown 不能当成 Hybrid 写进 0x09(会动 MUX)
        if (mode == GpuMode.Unknown)
        {
            Logger.Warn("显卡模式未知(读取失败), 放弃写入");
            return false;
        }

        var value = mode switch
        {
            GpuMode.Hybrid => 0,
            GpuMode.Discrete => 1,
            _ => 0
        };

        Logger.Info($"通过WMI设置显卡模式: {mode} (WMI值={value})");
        if (_wmi.SetGpuMode(value))
        {
            Logger.Info($"WMI设置显卡模式成功: {mode}");
            return true;
        }
        Logger.Warn("WMI设置显卡模式失败(桥无应答/EC 拒绝)");
        return false;
    }

    #endregion

    #region 功耗控制 (WMI cmd 0x17)

    /// <summary>
    /// 写 CPU 功耗墙 (EC 0x17: sub 0x02=SPL 持续, 0x03=SPPT 短时爆发, 单位瓦)。
    /// 该寄存器只写, EC 不回读, 是否生效需靠满载实测功耗/频率验证。
    /// </summary>
    public bool SetCpuPowerLimit(int pl1, int pl2)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn($"WMI不可用,无法设置CPU功耗: PL1={pl1}W, PL2={pl2}W");
            return false;
        }

        bool ok1 = _wmi.SetCpuPower(0x02, (byte)Math.Clamp(pl1, 1, 255));
        bool ok2 = _wmi.SetCpuPower(0x03, (byte)Math.Clamp(pl2, 1, 255));
        Logger.Info($"通过WMI设置CPU功耗: SPL={pl1}W, SPPT={pl2}W, EC应答={ok1 && ok2}");
        return ok1 && ok2;
    }

    #endregion

    #region 键盘灯效 (WMI cmd 0x10/0x11/0x12)

    public void SetKeyboardLighting(byte r, byte g, byte b, LightingMode mode = LightingMode.Static)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn($"WMI不可用,无法设置键盘灯效");
            return;
        }

        _wmi.SetKeyboardMode((int)mode);
        _wmi.SetKeyboardColor(r, g, b);
        Logger.Info($"通过WMI设置键盘灯效: mode={mode}, RGB=({r},{g},{b})");
    }

    public void SetKeyboardBrightness(byte brightness)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn($"WMI不可用,无法设置键盘亮度");
            return;
        }

        if (brightness > 3) brightness = 3;
        _wmi.SetKeyboardBrightness(brightness);
        Logger.Info($"通过WMI设置键盘亮度: {brightness}");
    }

    #endregion

    #region A 面 Logo 背光 (WMI cmd 0x0F)

    /// <summary>A 面 Logo 背光: 1=亮 0=灭 -1=读取失败/不支持。该命令没有颜色通道, 只能开关。</summary>
    public int GetLogoLight()
    {
        if (_wmi == null || !_wmi.IsAvailable) return -1;
        return _wmi.GetLogoLight();
    }

    public bool SetLogoLight(bool on)
    {
        if (_wmi == null || !_wmi.IsAvailable)
        {
            Logger.Warn("WMI不可用, 无法设置 Logo 背光");
            return false;
        }

        bool ok = _wmi.SetLogoLight(on);
        Logger.Info($"Logo 背光 -> {(on ? "开" : "关")}, EC应答={ok}");
        return ok;
    }

    #endregion

    public bool IsWmiAvailable => _wmi?.IsAvailable ?? false;
    public WmiInterface? Wmi => _wmi;

    public int GetAcType()
    {
        if (_wmi != null && _wmi.IsAvailable)
        {
            return _wmi.GetAcType();
        }
        return 0;
    }

    public void Dispose()
    {
        _wmi?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public enum PerformanceMode
{
    Unknown = -1,
    Balanced = 0,
    Turbo = 1,
    Quiet = 2
}

public enum FanTarget
{
    Cpu,
    Gpu,
    Both
}

public enum GpuMode
{
    Unknown = -1,
    Hybrid = 0,
    Discrete = 1
}

public enum LightingMode
{
    Off = 0,
    Static = 1,
    Breathing = 2,
    Cycle = 3,
    Wave = 4,
    Reactive = 5
}
