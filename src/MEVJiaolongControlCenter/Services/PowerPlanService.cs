using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;

namespace MEVJiaolongControlCenter.Services;

public sealed record PowerPlan(Guid Id, string Name)
{
    // ComboBox/UIA 直接显示名称, 而不是 record 的默认 ToString
    public override string ToString() => Name;
}

/// <summary>
/// Windows 电源方案枚举/切换。调用系统 powrprof API, 不直接改注册表。
/// 切换是可逆的系统设置, 只影响当前用户。
/// </summary>
public sealed class PowerPlanService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const uint AccessScheme = 16;

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup,
        uint accessFlags, uint index, out Guid buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup,
        IntPtr setting, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public List<PowerPlan> Enumerate()
    {
        var list = new List<PowerPlan>();
        for (uint i = 0; i < 64; i++)
        {
            uint size = (uint)Marshal.SizeOf<Guid>();
            uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                AccessScheme, i, out Guid guid, ref size);
            if (rc != 0) break;
            list.Add(new PowerPlan(guid, FriendlyName(guid)));
        }
        // PowerEnumerate 在某些系统上会把同一方案返回两次, 按 GUID 去重
        return list.GroupBy(p => p.Id).Select(g => g.First()).ToList();
    }

    public Guid? GetActive()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                try { return Marshal.PtrToStructure<Guid>(p); }
                finally { LocalFree(p); }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "读取当前电源方案失败"); }
        return null;
    }

    public bool SetActive(Guid guid)
    {
        try
        {
            uint rc = PowerSetActiveScheme(IntPtr.Zero, ref guid);
            if (rc == 0)
            {
                Logger.Info($"电源方案已切换: {guid}");
                return true;
            }
            Logger.Warn($"切换电源方案失败: rc={rc}");
        }
        catch (Exception ex) { Logger.Warn(ex, "切换电源方案异常"); }
        return false;
    }

    private static string FriendlyName(Guid guid)
    {
        uint size = 2048;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero,
                    buf, ref size) == 0)
            {
                string? name = Marshal.PtrToStringUni(buf)?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return guid.ToString();
    }
}
