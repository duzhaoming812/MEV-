using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MEVJiaolongControlCenter.Core.Hardware;
using MEVJiaolongControlCenter.Core.Wmi;
using NLog;

namespace MEVJiaolongControlCenter;

public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 单实例令牌。这是托盘常驻 + 开机自启的程序, 连开两份会同时写 EC/WMI、抢灯效状态,
    /// 后启动的那份还会把前一份的独占资源搅乱。
    /// </summary>
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常处理器必须最先注册: 主窗口由 StartupUri 在 base.OnStartup 里创建,
        // 构造阶段的异常也要能被记录并提示, 否则只会静默崩溃。
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Logger.Fatal(ex, "Fatal unhandled exception");
            ShowErrorDialog("Fatal error", ex);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Logger.Error(args.Exception, "UI thread unhandled exception");
            ShowErrorDialog("UI error", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Logger.Error(args.Exception, "Task unhandled exception");
            args.SetObserved();
        };

        // 安装程序会以 --probe 调用一次: 扫描本机可用功能并写入 ProgramData 后立即退出, 不建界面。
        // 必须在单实例检查之前 —— 安装/修复时用户可能正开着程序。
        if (e.Args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            RunCapabilityProbe();
            ExitImmediately();
        }

        // 安装/卸载程序调用: --autostart on|off (复用程序里已测试过的计划任务逻辑)
        if (e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)))
        {
            bool enable = !e.Args.Any(a => a.Equals("off", StringComparison.OrdinalIgnoreCase));
            bool ok = enable ? Services.AutostartService.Enable() : Services.AutostartService.Disable();
            Logger.Info($"命令行设置开机自启: {(enable ? "开" : "关")} -> {(ok ? "成功" : "失败")}");
            ExitImmediately();
        }
        // 单实例检查必须放在 base.OnStartup 之前: App.xaml 用 StartupUri 指定主窗口,
        // base 会连窗口、WMI 桥、灯效引擎一起创建, 等它返回再 Shutdown 已经太晚
        // (实测第二份会带着完整窗口活下来)。
        _singleInstance = new Mutex(true, @"Local\MEVJiaolongControlCenter.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // 静默退出: 常驻托盘 + 开机自启的程序, 第二份通常是误触, 弹模态框反而挡住用户。
            // 这里只能硬退出: OnStartup 阶段调用 Shutdown() 会被 WPF 忽略, 进程会带着一个
            // 无窗口的消息循环永久空转 (实测 ShutdownMode=OnMainWindowClose 下必然发生)。
            Logger.Info("检测到已有实例在运行, 本进程退出");
            ExitImmediately();
        }

        base.OnStartup(e);

        // .NET 8 默认只带 UTF-8/Latin1 等少数编码; 不注册这个提供程序, 子进程按 OEM 代码页
        // 输出的中文(powercfg/pnputil)就无法解码, 依赖文本匹配的回读会静默返回 null。
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        Logger.Info("=== MEVJiaolongControlCenter starting ===");
        Logger.Info($"Working dir: {Environment.CurrentDirectory}");
        Logger.Info($"Exe path: {Environment.ProcessPath}");
    }

    /// <summary>
    /// 安装程序调用的能力扫描(--probe): 探测本机支持哪些功能, 结果写到
    /// %ProgramData%\MEVJiaolongControlCenter\capabilities.json, 程序启动时据此禁用不支持的项。
    /// </summary>
    private static void RunCapabilityProbe()
    {
        try
        {
            using var wmi = new WmiInterface();
            bool ok = wmi.Initialize();
            Logger.Info($"能力扫描: MICommonInterface 初始化={ok}");

            string machine = "";
            try { machine = MachineProfile.Load().Model; } catch { }

            var support = CapabilityProbe.Probe(ok ? wmi : null, machine);
            bool saved = CapabilityStore.Save(support);
            Logger.Info($"PROBE_RESULT {(saved ? "已保存" : "保存失败")} | {support.Summary} | {CapabilityStore.FilePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "安装时能力扫描失败");
        }
    }

    /// <summary>
    /// 命令行模式(--probe / --autostart)的硬退出。Shutdown() 只是排队关闭: WPF 在
    /// OnStartup 返回后仍会按 StartupUri 创建主窗口, 实测卸载时的 --autostart off
    /// 会连带初始化 WMI 并写回设置/日志, 把刚清空的用户目录重新造出来。
    /// </summary>
    private static void ExitImmediately()
    {
        LogManager.Shutdown();
        Environment.Exit(0);
    }

    private static void ShowErrorDialog(string title, Exception? ex)
    {
        try
        {
            var msg = $"{title}\n\n{ex?.Message}\n\nStack:\n{ex?.StackTrace}";
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"MEVJiaolongControlCenter exiting with code {e.ApplicationExitCode}");
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        LogManager.Shutdown();
        base.OnExit(e);
    }
}
