using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NLog;

namespace MEVJiaolongControlCenter.Services;

[Flags]
public enum StressTarget
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Disk = 4,
    Gpu = 8
}

/// <summary>
/// 多目标压力测试: CPU满载线程 / 内存大块分配写入 / 硬盘顺序写读 / GPU(尽力而为, 混合模式下独显可能休眠)。
/// 全部使用专用后台线程, 不占线程池(避免饿死 UI 与倒计时)。限时自动停止。
/// </summary>
public partial class StressTestService : ObservableObject, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _remaining;

    [ObservableProperty]
    private string _statusText = "空闲";

    // 实时指标
    [ObservableProperty]
    private string _memoryMetric = "--";

    [ObservableProperty]
    private string _diskMetric = "--";

    [ObservableProperty]
    private string _gpuMetric = "--";

    private CancellationTokenSource? _cts;
    private readonly List<Thread> _workers = new();
    /// <summary>磁盘线程登记的临时文件路径, 只在 Stop 的后台清理里做兜底删除(进程被结束时 finally 跑不到)。</summary>
    private string? _diskTempFile;
    /// <summary>正在停止(工作线程还没全部退出) —— 此时禁止再次启动, 否则新旧线程会同时吃内存/磁盘。</summary>
    private volatile bool _stopping;

    public event Action? Finished;

    /// <summary>启动压力测试。durationSeconds 到点自动停止并恢复。</summary>
    public bool Start(StressTarget targets, int durationSeconds)
    {
        if (IsRunning || _stopping || targets == StressTarget.None) return false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        Remaining = durationSeconds;
        StatusText = "运行中";

        if (targets.HasFlag(StressTarget.Cpu)) StartCpuWorkers(token);
        if (targets.HasFlag(StressTarget.Memory)) StartMemoryWorkers(token);
        if (targets.HasFlag(StressTarget.Disk)) StartDiskWorker(token);
        if (targets.HasFlag(StressTarget.Gpu)) StartGpuProbe(token);

        Logger.Warn($"压力测试启动: {targets} 共 {durationSeconds} 秒, { _workers.Count } 个工作线程");

        // 专用计时线程保证倒计时准确
        var timer = new Thread(() =>
        {
            try
            {
                for (int s = durationSeconds; s > 0; s--)
                {
                    if (token.IsCancellationRequested) return;
                    Remaining = s;
                    Thread.Sleep(1000);
                }
                if (!token.IsCancellationRequested)
                {
                    // 窗口已关闭时 Dispatcher 可能已停机, Invoke 会抛异常到计时线程(无人 catch)
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                    {
                        try { dispatcher.Invoke(Stop); } catch { }
                    }
                }
            }
            catch { }
        })
        { IsBackground = true };
        timer.Start();
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _stopping = true;
        var cts = _cts;
        try { cts?.Cancel(); } catch { }

        var workers = _workers.ToArray();
        _workers.Clear();
        if (ReferenceEquals(_cts, cts)) _cts = null;
        var pendingTempFile = _diskTempFile;

        IsRunning = false;
        Remaining = 0;
        StatusText = "已停止";
        MemoryMetric = "--";
        DiskMetric = "--";
        GpuMetric = "--";

        // Join 不能放在 UI 线程: CPU 压测线程数 = 逻辑处理器数(本机 32), 磁盘线程可能正卡在一次
        // WriteThrough 写入里, 原实现逐个 Join(2000) 最坏能把界面冻住几十秒。
        // 放到后台等待; 期间 _stopping 保持 true, 新压测要等旧线程真退出才能开始,
        // 否则两组内存 worker 会同时吃内存。
        Task.Run(() =>
        {
            bool allStopped = true;
            foreach (var t in workers)
            {
                try { if (!t.Join(TimeSpan.FromMinutes(2))) allStopped = false; }
                catch { allStopped = false; }
            }
            try { cts?.Dispose(); } catch { }
            // 线程都退出之后再删: 磁盘线程可能还握着这个文件句柄
            try
            {
                if (pendingTempFile != null && File.Exists(pendingTempFile)) File.Delete(pendingTempFile);
            }
            catch { }
            _stopping = false;
            if (allStopped) Logger.Info("压测工作线程已全部退出");
            else Logger.Warn("个别压测线程 2 分钟内未退出, 已按停止处理并放开重新开始");
        });

        Logger.Info("压力测试停止请求已下发");
        Finished?.Invoke();
    }

    // ===== CPU: 全核浮点满载 =====
    private void StartCpuWorkers(CancellationToken token)
    {
        int cores = Environment.ProcessorCount;
        for (int i = 0; i < cores; i++)
        {
            int seed = i;
            var w = new Thread(() =>
            {
                try
                {
                    // 四条互相独立的依赖链。原来只有一条 x = sqrt(...) 的串行链, 每轮都要等 sqrt
                    // 的延迟(约 15-20 周期), FP 流水线填不满 —— 实测 32 线程只有 42W。
                    // 独立链让分母(吞吐)而不是延迟成为瓶颈, 才是真正的"全核满载"。
                    double a = 1.1 + seed * 0.1;
                    double b = 2.2 + seed * 0.1;
                    double c = 3.3 + seed * 0.1;
                    double d = 4.4 + seed * 0.1;
                    while (!token.IsCancellationRequested)
                    {
                        a = Math.Sqrt(a * 1.000001 + 12345.678);
                        b = Math.Sqrt(b * 1.000002 + 23456.789);
                        c = Math.Sqrt(c * 1.000003 + 34567.890);
                        d = Math.Sqrt(d * 1.000004 + 45678.901);
                    }
                    if (a + b + c + d == double.MaxValue) Logger.Debug("unreachable");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "CPU压力线程异常");
                }
            }) { IsBackground = true };
            w.Start();
            _workers.Add(w);
        }
    }

    // ===== 内存: 多个写线程按本机内存动态分配, 制造内存控制器负载 =====
    private void StartMemoryWorkers(CancellationToken token)
    {
        int targetMb = SuggestMemoryMb();
        int workers = SuggestMemoryWorkers(targetMb);
        int perWorkerMb = Math.Max(256, targetMb / workers);
        int actualTotalMb = perWorkerMb * workers;
        Logger.Warn($"内存压测目标 {actualTotalMb} MB ({workers} 个线程, 每线程 {perWorkerMb} MB)");

        for (int i = 0; i < workers; i++)
        {
            int seed = i;
            var w = new Thread(() => MemoryWorkerLoop(token, perWorkerMb, actualTotalMb, workers, seed))
            { IsBackground = true, Name = $"StressMemory#{i + 1}" };
            w.Start();
            _workers.Add(w);
        }
    }

    private unsafe void MemoryWorkerLoop(CancellationToken token, int targetMb, int totalMb, int workers, int seed)
    {
        // 用非托管内存而不是 byte[]: 几 GB 的托管大对象即使被 GC 回收, .NET 也不会把已提交的段还给系统。
        // 实测 4GB 压测 stop 后私有内存仍停在 4.1GB(强制 LOH 压缩也只降到 3.6GB), 常驻托盘程序不能这样。
        // NativeMemory.Alloc/Free 直接对应 VirtualAlloc/VirtualFree, 停止即归还(实测残留 0MB)。
        const int blockMb = 256;
        int blockBytes = blockMb * 1024 * 1024;
        int blockCount = Math.Max(1, targetMb / blockMb);
        var blocks = new IntPtr[blockCount];
        int allocated = 0;
        try
        {
            for (int i = 0; i < blockCount; i++)
            {
                var p = (IntPtr)NativeMemory.Alloc((nuint)blockBytes);
                if (p == IntPtr.Zero) break;
                blocks[allocated++] = p;
            }

            if (allocated == 0)
            {
                MemoryMetric = "内存不足, 已跳过";
                Logger.Warn("内存压力测试分配失败");
                return;
            }

            long rounds = 0;
            byte value = (byte)(seed * 37 + 1);
            while (!token.IsCancellationRequested)
            {
                for (int b = 0; b < allocated; b++)
                {
                    if (token.IsCancellationRequested) break;
                    var span = new Span<byte>((void*)blocks[b], blockBytes);
                    for (int i = 0; i < blockBytes; i += 4096)
                    {
                        if (token.IsCancellationRequested) break;
                        span.Slice(i, Math.Min(4096, blockBytes - i)).Fill(value);
                    }
                }
                rounds++;
                MemoryMetric = $"{totalMb}MB · {workers} 线程 · 第 {rounds} 轮";
            }
        }
        catch (OutOfMemoryException)
        {
            MemoryMetric = "内存不足, 已跳过";
            Logger.Warn("内存压力测试分配失败");
        }
        catch (Exception ex)
        {
            MemoryMetric = "内存测试异常";
            Logger.Error(ex, "内存压力测试异常");
        }
        finally
        {
            for (int i = 0; i < allocated; i++)
            {
                NativeMemory.Free((void*)blocks[i]);
                blocks[i] = IntPtr.Zero;
            }
        }
    }

    // ===== 硬盘: 系统盘写 512MB 临时文件(WriteThrough)再顺序读回 =====
    private void StartDiskWorker(CancellationToken token)
    {
        var w = new Thread(() =>
        {
            string? tempFile = null;
            try
            {
                var dir = Path.GetTempPath();
                // 路径只用这个线程自己的局部变量: 临时文件名带 GUID, 即使上一轮还没退干净
                // 也不会和新一轮共用同一个文件。
                tempFile = Path.Combine(dir, $"jlc_stress_{Guid.NewGuid():N}.tmp");
                Interlocked.Exchange(ref _diskTempFile, tempFile);
                // 按临时目录所在磁盘的剩余空间动态定量: 10%, 最少 256MB(极端紧张时 128MB), 最多 4GB,
                // 并给系统盘留 2GB 余量 —— 固定 512MB 会让小容量/几乎写满的机器直接失败。
                int targetMb = SuggestDiskMb();
                const int chunkSize = 4 * 1024 * 1024;   // 4MB
                int totalChunks = Math.Max(16, targetMb / 4);
                Logger.Warn($"磁盘压测目标 {targetMb} MB (临时文件 {tempFile})");
                var rng = new Random(42);
                // 读写共用一块缓冲区: 原实现每轮新建两个 4MB 数组, 长时间压测会持续制造大对象垃圾
                var ioBuf = new byte[chunkSize];

                while (!token.IsCancellationRequested)
                {
                    // 写阶段
                    var sw = Stopwatch.StartNew();
                    long written = 0;
                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write,
                               FileShare.None, chunkSize, FileOptions.WriteThrough))
                    {
                        for (int c = 0; c < totalChunks; c++)
                        {
                            if (token.IsCancellationRequested) break;
                            rng.NextBytes(ioBuf);
                            fs.Write(ioBuf, 0, ioBuf.Length);
                            written += ioBuf.Length;
                            // 大文件一轮可能十几秒, 中途给个进度, 否则界面一直显示 "--"
                            if ((c & 31) == 0)
                                DiskMetric = $"写入中 {written / 1048576} / {totalChunks * 4} MB";
                        }
                        fs.Flush(true);
                    }
                    // 取消时可能只写了几块, 必须按实际写入量算吞吐, 否则会虚高
                    double writeMBs = written / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    DiskMetric = $"写入 {writeMBs:F0} MB/s";

                    // 读阶段
                    sw.Restart();
                    long read = 0;
                    using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read,
                               FileShare.Read, chunkSize, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = fs.Read(ioBuf, 0, ioBuf.Length)) > 0)
                        {
                            if (token.IsCancellationRequested) break;
                            read += n;
                        }
                    }
                    double readMBs = read / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
                    DiskMetric = $"读 {readMBs:F0} / 写 {writeMBs:F0} MB/s";

                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                DiskMetric = "磁盘测试失败";
                Logger.Error(ex, "磁盘压力测试异常");
            }
            finally
            {
                // 取消/异常/正常结束都由这个线程删掉自己那份临时文件;
                // 进程被直接结束时来不及跑 finally, 才有 Stop() 里的兜底删除。
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                if (tempFile != null) Interlocked.CompareExchange(ref _diskTempFile, null, tempFile);
            }
        }) { IsBackground = true, Name = "StressDisk" };
        w.Start();
        _workers.Add(w);
    }

    // ===== GPU: 混合模式下独显通常休眠, nvidia-smi 无法唤醒。做能力探测并如实报告 =====
    private void StartGpuProbe(CancellationToken token)
    {
        var w = new Thread(() =>
        {
            try
            {
                int cycles = 0;
                while (!token.IsCancellationRequested && cycles < 3)
                {
                    var (ok, text) = QueryNvidiaSmi();
                    GpuMetric = ok ? text : "独显休眠(混合模式)·需独显直连";
                    cycles++;
                    for (int i = 0; i < 5 && !token.IsCancellationRequested; i++) Thread.Sleep(1000);
                }
            }
            catch
            {
                GpuMetric = "GPU 不可探测";
            }
        }) { IsBackground = true, Name = "StressGpuProbe" };
        w.Start();
        _workers.Add(w);
    }

    private static (bool ok, string text) QueryNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=temperature.gpu,power.draw,utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "");

            // 先异步读再等退出, 否则 5 秒超时无效(nvidia-smi 卡住会永久阻塞压测线程)
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(1500); } catch { }
                return (false, "nvidia-smi 超时");
            }

            string outp = outTask.GetAwaiter().GetResult().Trim();
            string errp = errTask.GetAwaiter().GetResult().Trim();
            if (!string.IsNullOrEmpty(outp) && outp.Contains(","))
            {
                var parts = outp.Split(',');
                return (true, $"{parts[0].Trim()}°C · {parts[1].Trim()}W · {parts[2].Trim()}%");
            }
            return (false, errp);
        }
        catch
        {
            return (false, "");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// 按物理内存与当前可用内存给出压测目标: 物理总量的 35% 且不超过当前可用的 60%,
    /// 再给系统留 2GB; 512MB–16GB。低内存机器不会被拉爆, 大内存机器也能压得动。
    /// </summary>
    public static int SuggestMemoryMb()
    {
        long totalMb = 0, availMb = 0;
        try
        {
            var ms = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref ms))
            {
                totalMb = (long)(ms.ullTotalPhys / 1048576);
                availMb = (long)(ms.ullAvailPhys / 1048576);
            }
        }
        catch { }

        if (totalMb <= 0 || availMb <= 0)
        {
            long fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;
            totalMb = fallback;
            availMb = fallback;
        }

        long target = Math.Min(totalMb * 35 / 100, availMb * 60 / 100);
        target = Math.Min(target, Math.Max(512, availMb - 2048));
        return (int)Math.Clamp(target, 512, 16384);
    }

    /// <summary>内存写线程数: 每 8 个逻辑处理器 1 个, 1–4 个; 目标不足 1GB 时只用 1 个。</summary>
    public static int SuggestMemoryWorkers(int targetMb)
    {
        int byCores = Math.Max(1, Environment.ProcessorCount / 8);
        int byMem = Math.Max(1, targetMb / 1024);
        return Math.Clamp(Math.Min(byCores, byMem), 1, 4);
    }

    /// <summary>按临时目录所在磁盘剩余空间给出磁盘压测目标: 10%, 256MB–4GB, 至少留 2GB 余量。</summary>
    public static int SuggestDiskMb()
    {
        try
        {
            string dir = Path.GetTempPath();
            var drive = new DriveInfo(Path.GetPathRoot(dir) ?? "C:\\");
            long freeMb = drive.AvailableFreeSpace / (1024 * 1024);
            long target = Math.Clamp(freeMb / 10, 256, 4096);
            if (freeMb < target + 2048)
                target = Math.Max(256, freeMb - 2048);
            return (int)Math.Max(128, target);
        }
        catch { return 512; }
    }

    /// <summary>本机是否有可用的 nvidia-smi(没有则 GPU 探测目标应禁用)。</summary>
    public static bool NvidiaSmiAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(1500); } catch { }
                return false;
            }
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(outTask.GetAwaiter().GetResult());
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        GC.SuppressFinalize(this);
    }
}
