using System;
using System.Linq;
using System.Management;
using NLog;

namespace MEVJiaolongControlCenter.Services;

/// <summary>
/// 内置屏幕亮度控制(WMI WmiMonitorBrightnessMethods)。外接显示器/不支持的机器上会返回不可用。
/// 只改当前显示器的亮度, 不写注册表、不改电源计划。
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private ManagementObject? _methods;

    public bool Available { get; private set; }
    public int Current { get; private set; } = 50;

    public BrightnessService()
    {
        Reload();
    }

    public void Reload()
    {
        try
        {
            _methods?.Dispose();
            _methods = null;
            Available = false;

            using (var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            {
                foreach (ManagementBaseObject o in s.Get())
                {
                    using (o)
                    {
                        if (o["CurrentBrightness"] != null)
                            Current = Math.Clamp(Convert.ToInt32(o["CurrentBrightness"]), 0, 100);
                    }
                    break;
                }
            }

            using (var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                foreach (ManagementBaseObject o in s.Get())
                {
                    _methods = (ManagementObject)o;
                    Available = true;
                    break;
                }
            }

            Logger.Info($"屏幕亮度: 可用={Available}, 当前={Current}%");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "屏幕亮度接口不可用");
            Available = false;
        }
    }

    /// <summary>读取当前亮度(系统热键改动后同步)。</summary>
    public void Refresh()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (o["CurrentBrightness"] != null)
                        Current = Math.Clamp(Convert.ToInt32(o["CurrentBrightness"]), 0, 100);
                }
                break;
            }
        }
        catch { }
    }

    public bool Set(int percent)
    {
        if (!Available || _methods == null) return false;
        try
        {
            int p = Math.Clamp(percent, 0, 100);
            using var inParams = _methods.GetMethodParameters("WmiSetBrightness");
            inParams["Timeout"] = (uint)0;
            inParams["Brightness"] = (byte)p;
            _methods.InvokeMethod("WmiSetBrightness", inParams, null);
            Current = p;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "设置屏幕亮度失败");
            return false;
        }
    }

    public void Dispose()
    {
        try { _methods?.Dispose(); } catch { }
        _methods = null;
        Available = false;
        GC.SuppressFinalize(this);
    }
}
