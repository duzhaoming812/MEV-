using System.Collections.Generic;
using NLog;

namespace MEVJiaolongControlCenter.Core.Power;

/// <summary>
/// 三档功耗配置: 只描述 CPU 功耗墙(SPL/SPPT), 由 ControlService 在切换性能档时下发。
/// 不做风扇曲线/GPU TGP: 本机风扇由 EC 温度门控、TGP 由固件管理, 那些字段只会造出"能调"的错觉。
/// </summary>
public class PowerProfileManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, PowerProfile> _profiles = new();

    public PowerProfileManager()
    {
        _profiles["Quiet"] = new PowerProfile { Name = "静音模式", CpuPl1 = 25, CpuPl2 = 35 };
        _profiles["Balanced"] = new PowerProfile { Name = "平衡模式", CpuPl1 = 45, CpuPl2 = 65 };
        _profiles["Turbo"] = new PowerProfile
        {
            Name = "狂暴模式",
            // 实测本机芯片是频率上限型而非功耗上限型: 80W 以上再抬 SPL, 满载吞吐不再变化
            // (120/160 与 255/255 差 0.08%), 但去掉上限后温度明显更高(近空载 thermal zone 67→78°C),
            // 持续负载反而更容易顶温度墙。EC 写入钳位仍开放到 255, 这里只是取实测最优值。
            CpuPl1 = 120,
            CpuPl2 = 160,
        };

        Logger.Info($"已初始化 {_profiles.Count} 个默认功耗配置");
    }

    public PowerProfile? GetProfile(string name)
        => _profiles.TryGetValue(name, out var profile) ? profile : null;
}

public class PowerProfile
{
    public string Name { get; set; } = "";

    /// <summary>CPU 持续功耗墙 SPL(瓦), 写 EC 0x17 sub 0x02</summary>
    public int CpuPl1 { get; set; }

    /// <summary>CPU 短时爆发功耗墙 SPPT(瓦), 写 EC 0x17 sub 0x03</summary>
    public int CpuPl2 { get; set; }
}
