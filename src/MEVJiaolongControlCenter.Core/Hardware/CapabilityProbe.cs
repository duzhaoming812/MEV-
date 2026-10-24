using System;
using System.IO;
using System.Text.Json;
using NLog;

namespace MEVJiaolongControlCenter.Core.Hardware;

/// <summary>
/// 本机功能支持情况。安装时(<c>--probe</c>)扫描一次并缓存到 ProgramData,
/// 程序启动时据此禁用本机不支持的功能 —— 换一台机器不会显示一堆点了没反应的东西。
/// </summary>
public sealed record FeatureSupport
{
    public bool EcWmi { get; init; }
    public bool PerfMode { get; init; }
    public bool GpuMode { get; init; }
    public bool KeyboardLight { get; init; }
    public bool LogoLight { get; init; }
    public bool DiscreteGpu { get; init; }
    /// <summary>EC 0x17 功耗墙: 只写不回读, 无法真正验证, 只要 EC 通就当支持</summary>
    public bool CpuPowerWall { get; init; }
    public string Machine { get; init; } = "";
    public string ProbedAt { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary =>
        $"EC={Flag(EcWmi)} 性能档={Flag(PerfMode)} 显卡模式={Flag(GpuMode)} 键盘灯={Flag(KeyboardLight)} " +
        $"Logo灯={Flag(LogoLight)} 独显={Flag(DiscreteGpu)} 功耗墙={Flag(CpuPowerWall)}";

    private static string Flag(bool b) => b ? "✓" : "✗";
}

/// <summary>
/// 逐项探测功能是否可用。探测方式都是"写回当前值再回读"——不改变机器状态, 只确认命令被接受。
/// Logo 灯例外: 它只有开关没有当前值可写, 因此写反值→回读→再写回原值, 全过程可逆。
/// </summary>
public static class CapabilityProbe
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static FeatureSupport Probe(Wmi.WmiInterface? wmi, string machine = "")
    {
        bool ec = wmi?.IsAvailable == true;
        bool perf = false, gpuMode = false, kbLight = false, logo = false, powerWall = false;

        if (ec)
        {
            // 性能档: 写回当前值, 回读一致即支持(不会真的切档)
            try
            {
                int cur = wmi!.GetPerformanceMode();
                if (cur >= 0)
                {
                    wmi.SetPerformanceMode(cur);
                    perf = wmi.GetPerformanceMode() == cur;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "探测性能档失败"); }

            // 显卡模式: 只读。写 0x09 会真的切 MUX, 探测阶段绝不能动
            try { gpuMode = wmi!.GetGpuMode() >= 0; }
            catch (Exception ex) { Logger.Debug(ex, "探测显卡模式失败"); }

            // 键盘灯: 写回当前亮度, 不改变观感
            try
            {
                int b = wmi!.GetKeyboardBrightness();
                if (b >= 0)
                {
                    wmi.SetKeyboardBrightness(b);
                    kbLight = wmi.GetKeyboardBrightness() == b;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "探测键盘灯失败"); }

            // Logo 灯: 写反值→回读→写回原值, 全程可逆
            try
            {
                int cur = wmi!.GetLogoLight();
                if (cur >= 0)
                {
                    int flipped = cur == 1 ? 0 : 1;
                    logo = wmi.SetLogoLight(flipped == 1) && wmi.GetLogoLight() == flipped;
                    wmi.SetLogoLight(cur == 1);          // 恢复原状
                    logo = logo && wmi.GetLogoLight() == cur;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "探测 Logo 灯失败"); }

            // 功耗墙 0x17 只写不可回读, 只能在 EC 可用时"当作支持"
            powerWall = ec;
        }

        var support = new FeatureSupport
        {
            EcWmi = ec,
            PerfMode = perf,
            GpuMode = gpuMode,
            KeyboardLight = kbLight,
            LogoLight = logo,
            CpuPowerWall = powerWall,
            DiscreteGpu = MachineProfile.HasDiscreteGpu(),
            Machine = machine,
            ProbedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        Logger.Info("功能探测: " + support.Summary);
        return support;
    }
}

/// <summary>
/// 能力扫描结果的持久化(机器级, ProgramData), 安装时写入、程序启动时读取。
/// 读不到时调用方应现场探测一次。
/// </summary>
public static class CapabilityStore
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MEVJiaolongControlCenter");

    public static string FilePath => Path.Combine(Folder, "capabilities.json");

    /// <summary>旧版目录名(JiaolongControlCenter)升级: 首次读取时把能力文件搬过来</summary>
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "JiaolongControlCenter", "capabilities.json");

    private static void MigrateLegacyOnce()
    {
        try
        {
            if (File.Exists(FilePath) || !File.Exists(LegacyFilePath)) return;
            Directory.CreateDirectory(Folder);
            File.Copy(LegacyFilePath, FilePath, overwrite: false);
            Logger.Info("已从旧目录迁移能力扫描结果");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "迁移旧能力文件失败(忽略)");
        }
    }

    public static FeatureSupport? Load()
    {
        try
        {
            MigrateLegacyOnce();
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<FeatureSupport>(json);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "读取能力扫描结果失败");
            return null;
        }
    }

    public static bool Save(FeatureSupport support)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(support,
                new JsonSerializerOptions { WriteIndented = true }));
            Logger.Info("能力扫描结果已保存: " + FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "保存能力扫描结果失败");
            return false;
        }
    }
}
