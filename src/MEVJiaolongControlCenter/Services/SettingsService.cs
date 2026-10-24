using System;
using System.IO;
using System.Text.Json;
using NLog;

namespace MEVJiaolongControlCenter.Services;

public class SettingsService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "MEVJiaolongControlCenter");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "settings.json");

        MigrateLegacySettings(appData);

        _settings = LoadSettings();
    }

    /// <summary>
    /// 旧版目录名(JiaolongControlCenter)升级: 首次运行时把 settings.json 拷过来,
    /// 灯效/配色/窗口位置都不会丢。旧目录保留不动, 用户可自行删除。
    /// </summary>
    private void MigrateLegacySettings(string appData)
    {
        try
        {
            if (File.Exists(_settingsPath)) return;
            var legacy = Path.Combine(appData, "JiaolongControlCenter", "settings.json");
            if (!File.Exists(legacy)) return;
            File.Copy(legacy, _settingsPath, overwrite: false);
            Logger.Info("已从旧目录迁移设置: " + legacy);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "迁移旧设置失败(忽略, 使用默认设置)");
        }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return Normalize(loaded);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "加载设置失败, 使用默认设置");
        }

        return new AppSettings();
    }

    /// <summary>
    /// 反序列化后补齐空对象/空集合: 手改过的 settings.json 里出现 "Keyboard": null
    /// 时, 后续 s.Keyboard.Palette 会空引用, 主窗口直接起不来。
    /// </summary>
    private static AppSettings Normalize(AppSettings s)
    {
        s.Keyboard ??= new KeyboardSettings();
        s.Window ??= new WindowSettings();
        s.Keyboard.CustomColors ??= new System.Collections.Generic.List<string>();
        s.Keyboard.CustomGradients ??= new System.Collections.Generic.List<string>();
        s.Keyboard.Palette ??= new System.Collections.Generic.List<string>();
        return s;
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            // 先写临时文件再原子替换: 直接 WriteAllText 会先截断, 进程被杀/断电就留下半截 JSON,
            // 下次启动会静默丢光全部设置。
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsPath, overwrite: true);
            Logger.Info("设置已保存");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "保存设置失败");
        }
    }

    public void UpdateSettings(Action<AppSettings> updater)
    {
        updater(_settings);
        SaveSettings();
    }
}

public class AppSettings
{


    /// <summary>用户自定义 CPU 功耗墙 SPL(瓦); 0 = 跟随性能档位预设</summary>
    public int CpuSplWatts { get; set; } = 0;

    /// <summary>
    /// 极限档是否处于启用状态。独显锁频在重启/重启程序后必然失效, 所以必须在开机时重放一次。
    /// </summary>
    public bool ExtremeModeArmed { get; set; } = false;

    public KeyboardSettings Keyboard { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
}

public class KeyboardSettings
{
    public byte Brightness { get; set; } = 100;
    public string Effect { get; set; } = "Static";
    public byte ColorR { get; set; } = 255;
    public byte ColorG { get; set; } = 0;
    public byte ColorB { get; set; } = 128;
    public byte Color2R { get; set; } = 0;
    public byte Color2G { get; set; } = 0;
    public byte Color2B { get; set; } = 255;

    /// <summary>用户自定义单色预设, 格式 "r,g,b"</summary>
    public System.Collections.Generic.List<string> CustomColors { get; set; } = new();

    /// <summary>用户自定义渐变预设, 格式 "r,g,b|r,g,b"</summary>
    public System.Collections.Generic.List<string> CustomGradients { get; set; } = new();

    /// <summary>
    /// 当前生效的调色板(2-6 色, 格式 "r,g,b")。渐变/呼吸/流水等效果按它插值,
    /// 不存下来的话"随机渐变"重启后就只剩主副两色, 多出来的颜色丢失。
    /// </summary>
    public System.Collections.Generic.List<string> Palette { get; set; } = new();

    /// <summary>动态随机: 每隔几秒重新随机一次调色板, 灯效本身保持不变</summary>
    public bool RandomDynamic { get; set; } = false;

    /// <summary>上一次用的是哪种随机: "Single"=单色, "Gradient"=渐变</summary>
    public string RandomMode { get; set; } = "Single";

    /// <summary>律动亮度上限百分比 10-100, 默认100=满亮度脉冲</summary>
    public int PulseCap { get; set; } = 100;

    /// <summary>A 面 Logo 背光开关(EC 0x0F)。该硬件无独立颜色通道, 只能开关。</summary>
    public bool LogoLight { get; set; } = true;
}

public class WindowSettings
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 780;
    public bool Maximized { get; set; } = false;
}
