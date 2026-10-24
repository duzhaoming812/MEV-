using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;

namespace MEVJiaolongControlCenter.Services;

/// <summary>
/// 当前显示器刷新率枚举/切换。只列当前分辨率下驱动报告的可用模式, 切换前先 CDS_TEST,
/// 失败则不落地, 避免黑屏。
/// </summary>
public sealed class DisplayModeService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int EnumCurrentSettings = -1;
    private const int DmDisplayFrequency = 0x400000;
    private const int CdsUpdateRegistry = 0x00000001;
    private const int CdsTest = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string? deviceName, int modeNum,
        ref DEVMODE devMode, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode,
        IntPtr hwnd, int flags, IntPtr param);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CurrentHz { get; private set; }
    public bool Available => CurrentHz > 0;

    public void Refresh()
    {
        try
        {
            var dm = NewDevMode();
            if (EnumDisplaySettingsEx(null, EnumCurrentSettings, ref dm, 0))
            {
                Width = dm.dmPelsWidth;
                Height = dm.dmPelsHeight;
                CurrentHz = dm.dmDisplayFrequency;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取刷新率失败"); }
    }

    /// <summary>当前分辨率/色深下驱动报告的刷新率(去重降序)。</summary>
    public List<int> GetRefreshRates()
    {
        var rates = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        try
        {
            var current = NewDevMode();
            if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0)) return rates.ToList();

            for (int i = 0; i < 512; i++)
            {
                var dm = NewDevMode();
                if (!EnumDisplaySettingsEx(null, i, ref dm, 0)) break;
                if (dm.dmPelsWidth != current.dmPelsWidth ||
                    dm.dmPelsHeight != current.dmPelsHeight ||
                    dm.dmBitsPerPel != current.dmBitsPerPel) continue;
                if (dm.dmDisplayFrequency > 1) rates.Add(dm.dmDisplayFrequency);
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "枚举刷新率失败"); }
        return rates.ToList();
    }

    public bool SetRefreshRate(int hz)
    {
        try
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref dm, 0)) return false;
            dm.dmDisplayFrequency = hz;
            dm.dmFields = DmDisplayFrequency;

            if (ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero) != 0)
            {
                Logger.Warn($"刷新率 {hz}Hz 未通过驱动测试, 不应用");
                return false;
            }
            int rc = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
            if (rc == 0)
            {
                CurrentHz = hz;
                Logger.Info($"刷新率已切换: {hz}Hz");
                return true;
            }
            Logger.Warn($"刷新率切换失败: rc={rc}");
        }
        catch (Exception ex) { Logger.Warn(ex, "切换刷新率异常"); }
        return false;
    }

    private static DEVMODE NewDevMode() => new DEVMODE
    {
        dmDeviceName = "",
        dmFormName = "",
        dmSize = (short)Marshal.SizeOf<DEVMODE>()
    };
}
