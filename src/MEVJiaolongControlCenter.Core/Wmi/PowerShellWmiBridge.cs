using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NLog;

namespace MEVJiaolongControlCenter.Core.Wmi;

public class PowerShellWmiBridge : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private Process? _process;
    private bool _disposed;
    private readonly object _lock = new();

    public bool IsReady { get; private set; }

    public bool Start()
    {
        try
        {
            Logger.Info("正在启动PowerShell WMI桥接进程...");

            // 重建前先收掉旧进程, 避免累积僵尸 powershell
            try
            {
                if (_process != null && !_process.HasExited) _process.Kill();
                _process?.Dispose();
            }
            catch { }
            _process = null;
            IsReady = false;

            // 首选内置 WmiBridge.exe(.NET Framework WMI 桥, 约 20MB)。
            // 它和主程序同目录发布; 缺失时回退到 PowerShell 脚本桥。
            var helper = Path.Combine(AppContext.BaseDirectory, "WmiBridge.exe");
            if (File.Exists(helper))
            {
                try
                {
                    var hpsi = new ProcessStartInfo
                    {
                        FileName = helper,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.ASCII,
                        StandardErrorEncoding = Encoding.ASCII,
                    };
                    var hproc = new Process { StartInfo = hpsi };
                    hproc.Start();
                    // 阻塞 ReadLine() 会让启动这一步没有上限: 桥进程卡在 ACPI WMI 调用上时,
                    // 主程序在 UI 线程里等 READY, 窗口永远出不来。
                    string? hello = null;
                    try
                    {
                        var helloTask = hproc.StandardOutput.ReadLineAsync();
                        if (!helloTask.Wait(StartTimeoutMs))
                            Logger.Warn($"内置 WmiBridge.exe {StartTimeoutMs}ms 内未应答, 回退 PowerShell 桥");
                        else
                            hello = helloTask.Result;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "读取内置 WmiBridge.exe 首行失败");
                    }

                    if (hello != null && hello.Contains("READY"))
                    {
                        _process = hproc;
                        IsReady = true;
                        Logger.Info("内置 WmiBridge.exe 已就绪");
                        return true;
                    }

                    Logger.Warn($"内置 WmiBridge.exe 未就绪, 回退 PowerShell: {hello}");
                    // 只 Kill 不 Dispose 会留下还在跑的常驻子进程(约 25MB), 回退前必须收干净
                    try
                    {
                        if (!hproc.HasExited) hproc.Kill();
                    }
                    catch { }
                    try { hproc.Dispose(); } catch { }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "启动内置 WmiBridge.exe 失败, 回退 PowerShell 桥");
                }
            }

            // 脚本落盘后用 -File 启动: 避免超长内联 -Command 触发杀软行为检测。
            var script = BuildInlineScript();
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MEVJiaolongControlCenter");
            Directory.CreateDirectory(folder);
            var scriptPath = Path.Combine(folder, "wmi_bridge.ps1");

            // 内容没变就不重写: 减少"运行时往 AppData 丢脚本"的磁盘行为
            try
            {
                if (!File.Exists(scriptPath) || File.ReadAllText(scriptPath) != script)
                    File.WriteAllText(scriptPath, script);
            }
            catch
            {
                File.WriteAllText(scriptPath, script);
            }

            // 用完整路径启动, 不依赖 PATH; 执行策略用 RemoteSigned 而不是 Bypass ——
            // 本地(无网络标记)脚本在 RemoteSigned 下同样可运行, 而 Bypass 是杀软的高危特征词。
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"{scriptPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.ASCII,
                StandardErrorEncoding = Encoding.ASCII,
            };

            _process = new Process { StartInfo = psi };
            _process.Start();

            var firstLine = ReadLine();
            if (firstLine != null && firstLine.Contains("READY"))
            {
                IsReady = true;
                Logger.Info("PowerShell WMI桥接进程已就绪");
                return true;
            }

            Logger.Warn($"PowerShell WMI桥接未就绪, 首行: {firstLine}");
            // 桥没起来也把子进程收掉: 否则这个 powershell 会带着两条管道一直挂在后台(约 100MB),
            // 而调用方(WmiInterface)接下来会把桥对象丢掉, 再也没人管它。
            CleanupFailedProcess();
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "启动PowerShell WMI桥接失败");
            CleanupFailedProcess();
            return false;
        }
    }

    private void CleanupFailedProcess()
    {
        var proc = _process;
        _process = null;
        IsReady = false;
        if (proc == null) return;
        try
        {
            if (!proc.HasExited) proc.Kill();
        }
        catch { }
        try { proc.Dispose(); } catch { }
    }

    private string BuildInlineScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$NS = 'root\\WMI'");
        sb.AppendLine("$INST = \"MICommonInterface.InstanceName='ACPI\\PNP0C14\\MIFS_0'\"");
        sb.AppendLine("$METH = 'MiInterface'");
        sb.AppendLine("$GET = [byte]0xFA");
        sb.AppendLine("$SET = [byte]0xFB");
        sb.AppendLine("function Send-Packet([byte]$type, [byte]$cmd, [byte[]]$payload) {");
        sb.AppendLine("    $pkt = New-Object byte[] 32");
        sb.AppendLine("    $pkt[1] = $type");
        sb.AppendLine("    $pkt[3] = $cmd");
        sb.AppendLine("    if ($payload) { for ($i = 0; $i -lt $payload.Length -and $i -lt 28; $i++) { $pkt[4 + $i] = $payload[$i] } }");
        sb.AppendLine("    $o = New-Object System.Management.ManagementObject($NS, $INST, $null)");
        sb.AppendLine("    $inParams = $o.GetMethodParameters($METH)");
        sb.AppendLine("    $inParams['InData'] = [byte[]]$pkt");
        sb.AppendLine("    $opts = New-Object System.Management.InvokeMethodOptions");
        sb.AppendLine("    $res = $o.InvokeMethod($METH, [System.Management.ManagementBaseObject]$inParams, $opts)");
        sb.AppendLine("    foreach ($p in $res.Properties) { if ($p.Name -eq 'OutData' -and $p.Value -is [byte[]]) { return [byte[]]$p.Value } }");
        sb.AppendLine("    return $null");
        sb.AppendLine("}");
        sb.AppendLine("function Reply([string]$s) { [Console]::Out.WriteLine($s); [Console]::Out.Flush() }");
        sb.AppendLine("Reply 'OK READY'");
        sb.AppendLine("$reader = [Console]::In");
        sb.AppendLine("while (($line = $reader.ReadLine()) -ne $null) {");
        sb.AppendLine("    $line = $line.Trim()");
        sb.AppendLine("    if ($line.Length -eq 0) { continue }");
        sb.AppendLine("    $parts = $line.Split(' ')");
        sb.AppendLine("    $verb = $parts[0].ToUpperInvariant()");
        sb.AppendLine("    try {");
        sb.AppendLine("        switch ($verb) {");
        sb.AppendLine("            'PING' { Reply 'OK PONG' }");
        sb.AppendLine("            'QUIT' { Reply 'OK BYE'; exit 0 }");
        sb.AppendLine("            'GET' {");
        sb.AppendLine("                $cmd = [Convert]::ToByte($parts[1], 16)");
        sb.AppendLine("                $data = Send-Packet $GET $cmd $null");
        sb.AppendLine("                if (-not $data) { throw 'no OutData' }");
        sb.AppendLine("                Reply ('OK ' + (($data | ForEach-Object { $_.ToString('X2') }) -join ''))");
        sb.AppendLine("            }");
        sb.AppendLine("            'SET' {");
        sb.AppendLine("                $cmd = [Convert]::ToByte($parts[1], 16)");
        sb.AppendLine("                $pay = @()");
        sb.AppendLine("                for ($i = 2; $i -lt $parts.Length; $i++) { $pay += [Convert]::ToByte($parts[$i], 16) }");
        sb.AppendLine("                $data = Send-Packet $SET $cmd ([byte[]]$pay)");
        sb.AppendLine("                if (-not $data) { throw 'no OutData' }");
        sb.AppendLine("                Reply ('OK ' + (($data | ForEach-Object { $_.ToString('X2') }) -join ''))");
        sb.AppendLine("            }");
        sb.AppendLine("            default { Reply ('ERR unknown verb ' + $verb) }");
        sb.AppendLine("        }");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        $m = ($_.Exception.Message -replace '[\\r\\n]+', ' ').Trim()");
        sb.AppendLine("        Reply ('ERR ' + $m)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public byte[]? SendGet(byte commandId)
    {
        return SendCommand($"GET {commandId:X2}");
    }

    public byte[]? SendSet(byte commandId, byte[] payload)
    {
        var payloadHex = "";
        if (payload != null && payload.Length > 0)
        {
            payloadHex = " " + string.Join(" ", Array.ConvertAll(payload, b => b.ToString("X2")));
        }
        return SendCommand($"SET {commandId:X2}{payloadHex}");
    }

    /// <summary>单条命令的最长等待时间。WMI 偶发卡死时不能让调用方无限期挂住。</summary>
    private const int CommandTimeoutMs = 2500;   // EC 单命令正常 <50ms, 超时只是给桥抖动兜底; 调小可减少 UI 冻结
    private const int StartTimeoutMs = 8000;
    /// <summary>重建失败后的退避时间, 避免每条命令都白等一次 8 秒启动</summary>
    private const int RestartBackoffMs = 20000;
    private long _lastStartAttemptTicks = long.MinValue / 2;

    private byte[]? SendCommand(string command)
    {
        lock (_lock)
        {
            // 已释放就不再拉起新进程(否则关窗过程中灯效定时器还会造出一个永不回收的 powershell)
            if (_disposed) return null;

            // 桥接可能已经崩掉/被超时逻辑杀掉。惰性重建, 否则一次失败会让之后所有 EC 操作
            // 静默失效, 直到用户重启整个程序(实测过这条: 只能靠重启恢复)。
            if (!IsReady || _process == null || _process.HasExited)
            {
                long now = Environment.TickCount64;
                if (now - _lastStartAttemptTicks < RestartBackoffMs)
                {
                    Logger.Warn("PowerShell 桥接不可用(重建退避中), 本次直接失败");
                    return null;
                }
                _lastStartAttemptTicks = now;
                Logger.Warn("PowerShell 桥接不可用, 尝试重建");
                if (!Start()) return null;
            }

            try
            {
                Logger.Debug($"PS桥接发送: {command}");

                _process!.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();

                var response = ReadLine(CommandTimeoutMs);
                if (response == null)
                {
                    Logger.Warn("PowerShell桥接返回null");
                    IsReady = false;
                    return null;
                }

                Logger.Debug($"PS桥接收到: {response.Substring(0, Math.Min(response.Length, 80))}");

                if (response.StartsWith("OK "))
                {
                    var hex = response.Substring(3);
                    return HexToBytes(hex);
                }

                if (response.StartsWith("ERR "))
                {
                    Logger.Warn($"PowerShell桥接错误: {response}");
                    return null;
                }

                Logger.Warn($"PowerShell桥接意外响应: {response}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"PowerShell桥接通信失败: {command}");
                IsReady = false;
                return null;
            }
        }
    }

    /// <summary>
    /// 带超时读一行。原实现直接阻塞 ReadLine(): 只要桌面的 WMI 调用卡住, 调用线程(可能是 UI)
    /// 就永久冻结。超时后主动杀掉子进程, 让下次调用走重建路径。
    /// </summary>
    private string? ReadLine(int timeoutMs = StartTimeoutMs)
    {
        if (_process == null) return null;
        try
        {
            var task = _process.StandardOutput.ReadLineAsync();
            if (!task.Wait(timeoutMs))
            {
                Logger.Warn($"PowerShell 桥接读取超时 {timeoutMs}ms, 结束桥接进程以便重建 (高负载时属正常)");
                IsReady = false;
                TryKill();
                return null;
            }

            var line = task.Result;
            if (line == null)
            {
                var err = "";
                try { err = _process.StandardError.ReadToEnd(); } catch { }
                if (!string.IsNullOrEmpty(err))
                    Logger.Error($"PowerShell stderr: {err}");
            }
            return line;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取PowerShell输出失败");
            TryKill();
            return null;
        }
    }

    private void TryKill()
    {
        try
        {
            if (_process != null && !_process.HasExited) _process.Kill();
        }
        catch { }
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        }
        return bytes;
    }

    public void Dispose()
    {
        // _disposed 必须在锁内判定+置位, 否则与 SendCommand 的"锁内检查"之间仍有窗口:
        // 关窗时灯效定时器可能刚好在锁外看到旧的 false, 随后拿到锁又拉起一个新 powershell。
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            IsReady = false;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    try { _process.StandardInput.WriteLine("QUIT"); } catch { }
                    if (!_process.WaitForExit(2000))
                    {
                        _process.Kill();
                    }
                }
            }
            catch { }

            _process = null;
        }
        GC.SuppressFinalize(this);
    }
}
