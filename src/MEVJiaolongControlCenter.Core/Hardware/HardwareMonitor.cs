using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using NLog;

namespace MEVJiaolongControlCenter.Core.Hardware;

public class HardwareMonitor : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private Computer _computer;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _diagnosticDumped;
    private bool _opened;

    public event EventHandler<HardwareData>? DataUpdated;
    public HardwareData LatestData { get; private set; } = new();

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsBatteryEnabled = true
        };

        // LHM 打开失败(驱动被占用/权限不足)不应该拖垮整个程序: 降级为"无硬件数据",
        // 之后由 UpdateData 每 30 秒重试, 恢复后自动继续采集。
        TryOpen();
    }

    private long _nextOpenRetry;

    private void TryOpen()
    {
        try
        {
            _computer.Open();
            _opened = true;
            Logger.Info("硬件监控已启动");
        }
        catch (Exception ex)
        {
            _opened = false;
            Logger.Error(ex, "LibreHardwareMonitor 打开失败, 硬件监控不可用(将每 30 秒重试)");
        }
    }

    public void StartMonitoring(int intervalMs = 1000)
    {
        // 防止重复启动: 第二次调用会新建一个循环并把旧的 _cts 顶掉, 旧循环再也停不下来
        if (_monitorTask is { IsCompleted: false })
        {
            Logger.Warn("硬件监控已在运行, 忽略重复启动");
            return;
        }

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    UpdateData();
                    await Task.Delay(intervalMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "监控数据更新出错");
                }
            }
        }, _cts.Token);

        Logger.Info($"硬件监控任务已启动，间隔: {intervalMs}ms");
    }

    private void UpdateData()
    {
        if (!_opened)
        {
            // 启动那一刻打开失败(驱动被别的程序占用等)不能让硬件面板永久空白: 每 30 秒重试一次
            long now = Environment.TickCount64;
            if (now < _nextOpenRetry) return;
            _nextOpenRetry = now + 30000;
            TryOpen();
            if (!_opened) return;
        }

        lock (_lock)
        {
            var data = new HardwareData();

            foreach (var hardware in _computer.Hardware)
            {
                // 绝不能让 LHM 调用 NVIDIA 的 NVML: LHM 0.9.3 的 NvmlDeviceGetPowerUsage
                // 互操作在本机新驱动上抛 AccessViolationException, 而 .NET 8 的 catch 抓不到
                // 损坏状态异常, 会直接终止整个进程(实测连续崩溃)。独显遥测改走 nvidia-smi。
                if (hardware.HardwareType == HardwareType.GpuNvidia) continue;

                try
                {
                    hardware.Update();
                }
                catch (Exception ex)
                {
                    // 单个硬件(如失效的CPU传感器)更新失败不应中断整轮监控
                    Logger.Debug(ex, $"硬件更新失败, 已跳过: {hardware.Name}");
                    continue;
                }

                if (!_diagnosticDumped)
                {
                    DumpHardwareSensors(hardware);
                }

                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        ProcessCpu(hardware, data);
                        break;
                    case HardwareType.GpuAmd:
                        ProcessGpu(hardware, data);
                        break;
                    case HardwareType.Memory:
                        ProcessMemory(hardware, data);
                        break;
                    case HardwareType.Battery:
                        ProcessBattery(hardware, data);
                        break;
                    case HardwareType.Storage:
                        ProcessStorage(hardware, data);
                        break;
                }

                ProcessSubHardware(hardware, data);
            }

            ApplyNvidiaTelemetry(data);

            if (!_diagnosticDumped)
            {
                _diagnosticDumped = true;
                Logger.Info($"=== 硬件诊断快照: CPU温度={data.CpuPackageTemp?.ToString("F1") ?? "null"}°C(核心最高={data.MaxCpuTemp:F1}), GPU={data.GpuName}{(data.GpuIsDiscrete ? "[独显]" : "[核显]")} 温度={data.GpuTemp?.ToString("F1") ?? "null"}°C, CPU功耗={data.CpuPower?.ToString("F1") ?? "null"}W, GPU功耗={data.GpuPower?.ToString("F1") ?? "null"}W, 内存={data.MemoryLoad?.ToString("F0") ?? "null"}%, 电池={data.BatteryLevel?.ToString("F0") ?? "null"}%(损耗{data.BatteryDegradation?.ToString("F0") ?? "?"}%), 风扇数={data.FanSpeeds.Count} ===");
            }

            LatestData = data;
            DataUpdated?.Invoke(this, data);
        }
    }

    private void DumpHardwareSensors(IHardware hardware)
    {
        Logger.Info($"[诊断] 硬件: {hardware.Name} ({hardware.HardwareType})");
        foreach (var sensor in hardware.Sensors)
        {
            Logger.Info($"[诊断]   {sensor.SensorType,-12} | {sensor.Name,-28} = {sensor.Value?.ToString("F2") ?? "null"}");
        }
        foreach (var sub in hardware.SubHardware)
        {
            Logger.Info($"[诊断]   子硬件: {sub.Name} ({sub.HardwareType})");
            foreach (var sensor in sub.Sensors)
            {
                Logger.Info($"[诊断]     {sensor.SensorType,-12} | {sensor.Name,-28} = {sensor.Value?.ToString("F2") ?? "null"}");
            }
        }
    }

    private void ProcessSubHardware(IHardware hardware, HardwareData data)
    {
        foreach (var sub in hardware.SubHardware)
        {
            // 与主循环里的 NVML 保护保持一致: 嵌套的 NVIDIA 子硬件也可能走 NVML
            if (sub.HardwareType == HardwareType.GpuNvidia) continue;

            // 单个子硬件失败不能中断整轮遥测, 否则 UI 数值会永久冻结在上一帧
            try { sub.Update(); }
            catch (Exception ex)
            {
                Logger.Debug(ex, $"子硬件更新失败, 已跳过: {sub.Name}");
                continue;
            }

            switch (sub.HardwareType)
            {
                case HardwareType.Cpu:
                    ProcessCpu(sub, data);
                    break;
                case HardwareType.GpuAmd:
                    ProcessGpu(sub, data);
                    break;
            }
        }
    }

    private void ProcessCpu(IHardware cpu, HardwareData data)
    {
        foreach (var sensor in cpu.Sensors)
        {
            switch (sensor.SensorType)
            {
                // AMD: 权威单值温度是 "Core (Tctl/Tdie)"; Intel: "Package"
                // 只匹配 Tctl/Package, 避免被 "CCDs Average (Tdie)" 等覆盖
                case SensorType.Temperature when sensor.Name.Contains("Tctl") ||
                                                 sensor.Name.Contains("Package"):
                    if (sensor.Value.HasValue)
                    {
                        data.CpuPackageTemp = sensor.Value;
                    }
                    break;
                case SensorType.Temperature when sensor.Name.Contains("Core") && !sensor.Name.Contains("Average"):
                    if (sensor.Value.HasValue)
                    {
                        data.CpuTemperatures[sensor.Name] = sensor.Value.Value;
                    }
                    break;
                case SensorType.Clock:
                    if (sensor.Name.Contains("#"))
                    {
                        data.CpuClocks[sensor.Name] = sensor.Value ?? 0;
                    }
                    break;
                case SensorType.Load:
                    if (sensor.Name.Contains("Core") || sensor.Name.Contains("Total"))
                    {
                        data.CpuLoads[sensor.Name] = sensor.Value ?? 0;
                    }
                    break;
                // 只取整封装功耗, 忽略每核心 (SMU) 功耗, 否则被 0.2W 单核值覆盖
                case SensorType.Power when sensor.Name.Contains("Package"):
                    data.CpuPower = sensor.Value;
                    break;
                // 每核功耗单独存, 用于"CPU 核心"面板
                case SensorType.Power when sensor.Name.Contains("Core #"):
                    data.CpuCorePowers[sensor.Name] = sensor.Value ?? 0;
                    break;
                // 每核 VID 电压
                case SensorType.Voltage when sensor.Name.Contains("VID"):
                    data.CpuVoltages[sensor.Name] = sensor.Value ?? 0;
                    break;
                case SensorType.Fan:
                    data.FanSpeeds[sensor.Name] = sensor.Value;
                    break;
            }
        }

        // AMD 无独立 Package 功耗传感器时, 回退到 "Package" 命名的功耗; 已处理
    }

    private void ProcessGpu(IHardware gpu, HardwareData data)
    {
        bool isNvidia = gpu.HardwareType == HardwareType.GpuNvidia;

        // 优先独显(NVIDIA); 一旦记录独显, 不让核显覆盖
        if (data.GpuIsDiscrete && !isNvidia) return;
        data.GpuIsDiscrete = isNvidia;
        data.GpuName = gpu.Name;

        foreach (var sensor in gpu.Sensors)
        {
            switch (sensor.SensorType)
            {
                // 核心温度优先; 核显只有 "GPU VR SoC" 一个温度传感器, 作为回退
                // 排除 Hotspot / Memory (次要且偏高读数)
                case SensorType.Temperature when !sensor.Name.Contains("Hotspot") &&
                                                 !sensor.Name.Contains("Memory"):
                    if (sensor.Name.Contains("Core"))
                    {
                        data.GpuTemp = sensor.Value;
                    }
                    else if (data.GpuTemp == null)
                    {
                        data.GpuTemp = sensor.Value;
                    }
                    break;
                case SensorType.Clock when sensor.Name.Contains("Core"):
                    data.GpuCoreClock = sensor.Value;
                    break;
                case SensorType.Clock when sensor.Name.Contains("Memory"):
                    data.GpuMemoryClock = sensor.Value;
                    break;
                case SensorType.Load when sensor.Name.Contains("GPU") || sensor.Name.Contains("Core"):
                    data.GpuLoad = sensor.Value;
                    break;
                case SensorType.Load when sensor.Name.Contains("Memory"):
                    data.GpuMemoryLoad = sensor.Value;
                    break;
                case SensorType.Power when sensor.Name.Contains("Package") || data.GpuPower == null:
                    data.GpuPower = sensor.Value;
                    break;
                case SensorType.Fan:
                    data.FanSpeeds[$"GPU {sensor.Name}"] = sensor.Value;
                    break;
                case SensorType.SmallData:
                    if (sensor.Name.Contains("Memory"))
                    {
                        data.GpuMemoryUsed = sensor.Value;
                    }
                    break;
            }
        }
    }

    private void ProcessMemory(IHardware memory, HardwareData data)
    {
        foreach (var sensor in memory.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Load:
                    data.MemoryLoad = sensor.Value;
                    break;
                // 必须精确匹配: "Virtual Memory Used/Available" 也包含这些字样, 会把总量算成 39GB
                case SensorType.Data when sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase):
                    data.MemoryUsedGb = sensor.Value;
                    break;
                case SensorType.Data when sensor.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase):
                    data.MemoryAvailableGb = sensor.Value;
                    break;
                case SensorType.Data when sensor.Name.Equals("Virtual Memory Used", StringComparison.OrdinalIgnoreCase):
                    data.MemoryVirtualUsedGb = sensor.Value;
                    break;
                case SensorType.Data when sensor.Name.Equals("Virtual Memory Available", StringComparison.OrdinalIgnoreCase):
                    data.MemoryVirtualAvailableGb = sensor.Value;
                    break;
            }
        }
    }

    private void ProcessStorage(IHardware storage, HardwareData data)
    {
        data.StorageName = storage.Name;
        foreach (var sensor in storage.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Temperature when sensor.Name == "Temperature":
                    data.StorageTemp = sensor.Value;
                    break;
                case SensorType.Temperature when sensor.Name == "Temperature 1":
                    data.StorageTemp1 = sensor.Value;
                    break;
                case SensorType.Temperature when sensor.Name == "Temperature 2":
                    data.StorageTemp2 = sensor.Value;
                    break;
                // 精确匹配: "Available Spare Threshold" 是 10% 的阈值, 不是剩余备用块
                case SensorType.Level when sensor.Name.Equals("Available Spare", StringComparison.OrdinalIgnoreCase):
                    data.StorageSpare = sensor.Value;
                    break;
                case SensorType.Level when sensor.Name.Equals("Available Spare Threshold", StringComparison.OrdinalIgnoreCase):
                    data.StorageSpareThreshold = sensor.Value;
                    break;
                case SensorType.Level when sensor.Name.Equals("Percentage Used", StringComparison.OrdinalIgnoreCase):
                    data.StorageWear = sensor.Value;
                    break;
                case SensorType.Load when sensor.Name.Contains("Used Space"):
                    data.StorageUsed = sensor.Value;
                    break;
                case SensorType.Load when sensor.Name.Equals("Read Activity", StringComparison.OrdinalIgnoreCase):
                    data.StorageReadActivity = sensor.Value;
                    break;
                case SensorType.Load when sensor.Name.Equals("Write Activity", StringComparison.OrdinalIgnoreCase):
                    data.StorageWriteActivity = sensor.Value;
                    break;
                case SensorType.Data when sensor.Name.Contains("Data Read"):
                    data.StorageReadGb = sensor.Value;
                    break;
                case SensorType.Data when sensor.Name.Contains("Data Written"):
                    data.StorageWriteGb = sensor.Value;
                    break;
            }
        }
    }

    private void ProcessBattery(IHardware battery, HardwareData data)
    {
        foreach (var sensor in battery.Sensors)
        {
            switch (sensor.SensorType)
            {
                // 电量只取 "Charge Level", 排除 "Degradation Level"(电池损耗)
                case SensorType.Level when sensor.Name.Contains("Charge Level") ||
                                           (!sensor.Name.Contains("Degradation") && data.BatteryLevel == null):
                    data.BatteryLevel = sensor.Value;
                    break;
                case SensorType.Level when sensor.Name.Contains("Degradation"):
                    data.BatteryDegradation = sensor.Value;
                    break;
                case SensorType.Power:
                    data.BatteryPower = sensor.Value;
                    break;
                case SensorType.Voltage:
                    data.BatteryVoltage = sensor.Value;
                    break;
                case SensorType.Temperature:
                    data.BatteryTemp = sensor.Value;
                    break;
                case SensorType.Current:
                    data.BatteryCurrent = sensor.Value;
                    break;
                case SensorType.Energy when sensor.Name.Contains("Designed"):
                    data.BatteryDesignMwh = sensor.Value;
                    break;
                case SensorType.Energy when sensor.Name.Contains("Full Charged"):
                    data.BatteryFullMwh = sensor.Value;
                    break;
                case SensorType.Energy when sensor.Name.Contains("Remaining"):
                    data.BatteryRemainingMwh = sensor.Value;
                    break;
            }
        }
    }

    // ===== 独显遥测 (nvidia-smi) =====
    // 不用 LHM/NVML 的原因见 UpdateData 里的注释: 会抛捕获不到的 AccessViolationException。
    // nvidia-smi 每次要拉起一个进程, 所以按 N 轮刷新一次并缓存; 连续失败则退避,
    // 避免混合模式下独显休眠时反复做无用查询(查询本身还会把独显唤醒, 白耗电)。
    private const int SMI_REFRESH_EVERY = 6;
    private const int SMI_BACKOFF_EVERY = 40;
    private const int SMI_MAX_FAILURES = 3;
    /// <summary>连续失败多少轮后把独显实时值判为过期(清空成 --)</summary>
    private const int SMI_STALE_AFTER = 2;
    // 初值设为刷新周期, 让启动后第一轮就探测独显, 避免头几秒 GPU 面板先显示核显再跳变
    private int _smiCounter = SMI_REFRESH_EVERY;
    private int _smiFailures;
    private string? _smiName;
    private string _smiPState = "";
    private string _smiDriver = "";
    private string _smiPcie = "";
    private float? _smiTemp, _smiPower, _smiPowerLimit, _smiClock, _smiMemClock;
    private float? _smiMaxCore, _smiMaxMem, _smiFan;
    private float? _smiLoad, _smiMemLoad, _smiMemUsed, _smiMemTotal, _smiMemFree;

    private void ApplyNvidiaTelemetry(HardwareData data)
    {
        int period = _smiFailures >= SMI_MAX_FAILURES ? SMI_BACKOFF_EVERY : SMI_REFRESH_EVERY;
        if (++_smiCounter >= period)
        {
            _smiCounter = 0;
            RefreshNvidiaTelemetry();
        }

        if (_smiName == null) return;

        // 有独显就以独显为准(UI 一直按这个语义展示 GPU)
        data.GpuIsDiscrete = true;
        data.GpuName = _smiName;
        data.GpuTemp = _smiTemp;
        data.GpuPower = _smiPower;
        data.GpuPowerLimit = _smiPowerLimit;
        data.GpuCoreClock = _smiClock;
        data.GpuMemoryClock = _smiMemClock;
        data.GpuLoad = _smiLoad;
        data.GpuMemoryLoad = _smiMemLoad;
        data.GpuMemoryUsed = _smiMemUsed;
        data.GpuMemoryTotal = _smiMemTotal;
        data.GpuMemoryFree = _smiMemFree;
        data.GpuMaxCoreClock = _smiMaxCore;
        data.GpuMaxMemoryClock = _smiMaxMem;
        data.GpuFanPercent = _smiFan;
        data.GpuPState = _smiPState;
        data.GpuDriver = _smiDriver;
        data.GpuPcie = _smiPcie;
    }

    private void RefreshNvidiaTelemetry()
    {
        var (code, output) = RunProcess("nvidia-smi",
            "--query-gpu=name,driver_version,temperature.gpu,power.draw,power.limit," +
            "clocks.gr,clocks.mem,clocks.max.gr,clocks.max.mem,utilization.gpu,utilization.memory," +
            "memory.used,memory.total,memory.free,fan.speed,pstate," +
            "pcie.link.gen.current,pcie.link.width.current " +
            "--format=csv,noheader,nounits", 8000);

        if (code != 0 || !output.Contains(','))
        {
            _smiFailures++;
            if (_smiFailures == SMI_MAX_FAILURES)
                Logger.Info("nvidia-smi 连续失败, 独显遥测转入退避轮询(独显休眠/混合模式属正常)");

            // 连续两轮读不到就把实时值清成 null: 独显休眠/MUX 切走/驱动掉线之后,
            // 界面必须显示 "--" 而不是冻结在最后一次读数上(旧温度比没有数据更误导)。
            if (_smiFailures >= SMI_STALE_AFTER && _smiName != null && _smiTemp != null)
            {
                ClearNvidiaLiveMetrics();
                Logger.Info($"独显遥测已过期({_smiFailures} 次失败), 实时值清空, 保留型号/驱动");
            }
            return;
        }

        var p = output.Split(',');
        if (p.Length < 18) return;

        bool first = _smiName == null;
        _smiFailures = 0;
        _smiName = p[0].Trim();
        _smiDriver = p[1].Trim();
        _smiTemp = ParseMetric(p[2]);
        _smiPower = ParseMetric(p[3]);
        _smiPowerLimit = ParseMetric(p[4]);
        _smiClock = ParseMetric(p[5]);
        _smiMemClock = ParseMetric(p[6]);
        _smiMaxCore = ParseMetric(p[7]);
        _smiMaxMem = ParseMetric(p[8]);
        _smiLoad = ParseMetric(p[9]);
        _smiMemLoad = ParseMetric(p[10]);
        _smiMemUsed = ParseMetric(p[11]);
        _smiMemTotal = ParseMetric(p[12]);
        _smiMemFree = ParseMetric(p[13]);
        _smiFan = ParseMetric(p[14]);
        _smiPState = p[15].Trim();
        string gen = p[16].Trim(), width = p[17].Trim();
        _smiPcie = (gen.Contains("N/A") || width.Contains("N/A")) ? "" : $"PCIe {gen} x{width}";
        if (first) Logger.Info($"独显遥测已接管(nvidia-smi): {_smiName}");
    }

    /// <summary>把独显的实时读数清空(型号/驱动是静态信息, 保留), 界面据此显示 "--"。</summary>
    private void ClearNvidiaLiveMetrics()
    {
        _smiTemp = _smiPower = _smiPowerLimit = _smiClock = _smiMemClock = null;
        _smiMaxCore = _smiMaxMem = _smiFan = null;
        _smiLoad = _smiMemLoad = _smiMemUsed = _smiMemTotal = _smiMemFree = null;
        _smiPState = "";
        _smiPcie = "";
    }

    /// <summary>nvidia-smi 对不支持的项返回 [N/A], 解析成 null 而不是 0</summary>
    private static float? ParseMetric(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        // nvidia-smi 的输出永远是 "45.00" 这种不变区域格式。用当前区域解析时, 逗号小数点
        // 的语言(如 de-DE)会把 "45.00" 读成 4500, 并可能把 "1,234" 误判成 1.234。
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    private static (int code, string output) RunProcess(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (-1, "");

            // 必须先异步读、再等退出。原来写的是 ReadToEnd() 之后再 WaitForExit(timeout),
            // 那个超时形同虚设: 子进程一旦卡住, ReadToEnd 会永久阻塞, 监控线程就死在那里。
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                // Kill 之后必须等它真的退出: 否则子进程和两条管道句柄会留在系统里,
                // 独显休眠时 nvidia-smi 卡住的情况每 12 秒来一次, 很快就积一堆僵尸进程。
                try { proc.Kill(true); } catch { }
                try { proc.WaitForExit(2000); } catch { }
                try { outTask.Wait(500); } catch { }
                try { errTask.Wait(500); } catch { }
                return (-1, "timeout");
            }

            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            return (proc.ExitCode, stdout.Length > 0 ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public void Dispose()
    {
        var cts = _cts;
        var task = _monitorTask;
        cts?.Cancel();

        // 这里是关窗路径, 跑在 UI 线程上: 监控线程可能正卡在 nvidia-smi(最长 8s)里并且持着 _lock,
        // 同步 Wait + Close 会让窗口点关闭后卡住十秒才消失。收尾搬到独立线程, 界面立刻关闭。
        // 用前台线程(IsBackground=false)是为了保证 LHM 的 Close 一定跑完 —— 否则进程先退出,
        // 监控驱动服务会一直挂在系统里, 变成卸载前的残留状态。
        // (Close 必须与 UpdateData 互斥: 关驱动句柄的同时另一线程还在 hardware.Update()
        //  会抛损坏状态异常, .NET 8 抓不到, 直接终结进程。)
        var cleanup = new Thread(() =>
        {
            try { task?.Wait(TimeSpan.FromSeconds(10)); }
            catch { }
            finally
            {
                lock (_lock)
                {
                    if (_opened)
                    {
                        try { _computer.Close(); } catch (Exception ex) { Logger.Warn(ex, "关闭硬件监控失败"); }
                        _opened = false;
                    }
                }
                try { cts?.Dispose(); } catch { }
                Logger.Info("硬件监控已停止并关闭");
            }
        })
        { IsBackground = false, Name = "HardwareMonitorClose" };
        cleanup.Start();

        GC.SuppressFinalize(this);
    }
}

public class HardwareData
{
    public string GpuName { get; set; } = "";
    public bool GpuIsDiscrete { get; set; }

    public float? CpuPackageTemp { get; set; }
    public Dictionary<string, float> CpuTemperatures { get; set; } = new();
    public Dictionary<string, float> CpuClocks { get; set; } = new();
    public Dictionary<string, float> CpuLoads { get; set; } = new();
    public Dictionary<string, float> CpuVoltages { get; set; } = new();
    public Dictionary<string, float> CpuCorePowers { get; set; } = new();
    public float? CpuPower { get; set; }

    public float? GpuTemp { get; set; }
    public float? GpuCoreClock { get; set; }
    public float? GpuMemoryClock { get; set; }
    public float? GpuLoad { get; set; }
    public float? GpuMemoryLoad { get; set; }
    public float? GpuPower { get; set; }
    public float? GpuMemoryUsed { get; set; }

    public float? MemoryLoad { get; set; }
    public float? MemoryUsedGb { get; set; }
    public float? MemoryAvailableGb { get; set; }
    public float? MemoryVirtualUsedGb { get; set; }
    public float? MemoryVirtualAvailableGb { get; set; }

    // NVMe/SSD 健康(LibreHardwareMonitor 的 Storage 传感器)
    public string StorageName { get; set; } = "";
    public float? StorageTemp { get; set; }
    public float? StorageTemp1 { get; set; }
    public float? StorageTemp2 { get; set; }
    public float? StorageSpare { get; set; }    // Available Spare %
    public float? StorageSpareThreshold { get; set; }
    public float? StorageWear { get; set; }     // Percentage Used %
    public float? StorageUsed { get; set; }     // Used Space %
    public float? StorageReadGb { get; set; }   // 累计读取(GB)
    public float? StorageWriteGb { get; set; }  // 累计写入(GB)
    public float? StorageReadActivity { get; set; }
    public float? StorageWriteActivity { get; set; }

    public Dictionary<string, float?> FanSpeeds { get; set; } = new();

    public float? BatteryLevel { get; set; }
    public float? BatteryPower { get; set; }
    public float? BatteryVoltage { get; set; }
    public float? BatteryDegradation { get; set; }
    public float? BatteryTemp { get; set; }
    public float? BatteryCurrent { get; set; }
    public float? BatteryDesignMwh { get; set; }
    public float? BatteryFullMwh { get; set; }
    public float? BatteryRemainingMwh { get; set; }
    public int? BatteryCycles { get; set; }      // WMI BatteryCycleCount, 一次性读取

    // 独显扩展遥测(nvidia-smi)
    public string GpuDriver { get; set; } = "";
    public float? GpuMemoryTotal { get; set; }
    public float? GpuMemoryFree { get; set; }
    public float? GpuMaxCoreClock { get; set; }
    public float? GpuMaxMemoryClock { get; set; }
    public float? GpuFanPercent { get; set; }
    public float? GpuPowerLimit { get; set; }
    public string GpuPState { get; set; } = "";
    public string GpuPcie { get; set; } = "";

    // 优先权威单值(Package/Tctl), 回退到核心最高温
    public float MaxCpuTemp => CpuPackageTemp
        ?? (CpuTemperatures.Count > 0 ? CpuTemperatures.Values.Max() : 0);
}
