using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MEVJiaolongControlCenter.Core.Audio;
using MEVJiaolongControlCenter.Core.EC;
using MEVJiaolongControlCenter.Core.Hardware;
using MEVJiaolongControlCenter.Core.Wmi;
using MEVJiaolongControlCenter.Services;
using MEVJiaolongControlCenter.ViewModels;
using MaterialDesignThemes.Wpf;
using NLog;

namespace MEVJiaolongControlCenter.Views;

public partial class MainWindow : Window
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly MainViewModel _viewModel;
    private readonly ControlService _controlService;
    private readonly SettingsService _settingsService;
    private readonly StressTestService _stressService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _saveDebounce;
    private readonly TrayService _trayService;
    private readonly BrightnessService _brightness;
    private readonly AudioVolumeControl _audio;
    private readonly SystemInfoService _sysInfo;
    private readonly PowerPlanService _powerPlans = new();
    private readonly DisplayModeService _displayModes = new();
    private readonly DispatcherTimer _brightnessDebounce;
    private bool _brightnessReady;
    private bool _volumeReady;
    private bool _muteLock;
    private bool _suppressQuickWrite;
    private bool _suppressPowerPlan;
    private bool _suppressRefresh;
    private int _audioPollCounter;
    private int _brightnessPollCounter;
    private int _powerPlanPollCounter;
    private int _refreshPollCounter;
    private int _sysInfoTick;
    private int _audioDeviceTick;
    private int _ecHealthTick;
    private volatile bool _brightnessRefreshBusy;
    private int? _batteryCycles;
    private int _detailTick;
    private string? _lastSwatchHex;
    private SolidColorBrush? _swatchBrush;

    // 这几支画刷每秒都会被赋值一次(连接状态/压测按钮), 用冻结的单例避免每秒新建对象
    private static readonly SolidColorBrush BrushOk = Frozen(Color.FromRgb(0x69, 0xF0, 0xAE));
    private static readonly SolidColorBrush BrushMuted = Frozen(Color.FromRgb(0x8A, 0x8F, 0x9C));
    private static readonly SolidColorBrush BrushStressRun = Frozen(Color.FromRgb(0xD3, 0x2F, 0x2F));
    private static readonly SolidColorBrush BrushStressRunText = Frozen(Color.FromRgb(0xFF, 0xAB, 0x91));
    private static readonly SolidColorBrush BrushStressIdle = Frozen(Color.FromRgb(0xFF, 0x6E, 0x40));
    private static readonly SolidColorBrush BrushStressIdleText = Frozen(Color.FromRgb(0xA6, 0xAB, 0xB8));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
    private readonly List<(TextBlock Block, List<int> Cores)> _coreColumns = new();
    private bool _coreAnyTemp;
    private readonly LightingEffects? _lightingEffects;
    private bool _isDemoMode;
    /// <summary>本机 EC 是否真的可用(设备存在且命令有响应); 决定模式徽章、连接状态与控件禁用</summary>
    private bool _ecUsable;
    private PerformanceMode _liveMode = PerformanceMode.Unknown;
    private GpuMode _liveGpuMode = GpuMode.Unknown;

    private IntPtr _keyboardHook = IntPtr.Zero;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private readonly LowLevelKeyboardProc _keyboardProc;

    // HSV 取色器状态
    private double _pickerH = 340, _pickerS = 1, _pickerV = 1;
    private bool _pickerDown;

    // 压力测试时长进度
    private int _stressTotalSeconds;

    // 压力测试统计(最小/平均/最大)
    private float _statTempMin = float.MaxValue, _statTempMax = float.MinValue, _statTempSum;
    private float _statPowerMin = float.MaxValue, _statPowerMax = float.MinValue, _statPowerSum;
    private int _statFanMin = int.MaxValue, _statFanMax = int.MinValue;
    private long _statFanSum;
    private int _statCount;

    // 音频电平显示
    private readonly DispatcherTimer _audioTimer;
    // 极限档自检(漂移自动补发)
    private readonly DispatcherTimer _configWatch;
    private double _pulseHold;
    private Core.Audio.AudioSourceKind _preferredAudioSource = Core.Audio.AudioSourceKind.SystemLoopback;

    public MainWindow()
    {
        try
        {
            Logger.Info("MainWindow constructor start");

            _keyboardProc = KeyboardHookCallback;
            InitializeComponent();

            // Button 的类处理器会把 MouseLeftButtonDown/Up 标记为已处理, 取色器需 handledEventsToo 才能收到
            SvHit.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(Sv_MouseDown), true);
            SvHit.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(Sv_MouseMove), true);
            SvHit.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(Picker_MouseUp), true);
            HueHit.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(Hue_MouseDown), true);
            HueHit.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(Hue_MouseMove), true);
            HueHit.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(Picker_MouseUp), true);

            Logger.Info("InitializeComponent done");

            _settingsService = new SettingsService();

            // 窗口位置/尺寸: 以前只在关闭时写进设置, 从来没有读回来 —— 每次启动都回到屏幕中央。
            // 这里在显示之前应用, 并做一次"是否还在可见屏幕范围内"的校验, 避免拔掉外接屏后窗口跑到屏幕外。
            var win = _settingsService.Settings.Window;
            if (win.Width >= 600 && win.Height >= 450)
            {
                double vx = SystemParameters.VirtualScreenLeft;
                double vy = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth;
                double vh = SystemParameters.VirtualScreenHeight;
                bool visible = win.Left > vx - 80 && win.Top > vy - 80
                            && win.Left < vx + vw - 160 && win.Top < vy + vh - 120;
                if (visible)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = win.Left;
                    Top = win.Top;
                    Width = win.Width;
                    Height = win.Height;
                    if (win.Maximized) WindowState = WindowState.Maximized;
                    Logger.Info($"已恢复窗口位置: {win.Left:F0},{win.Top:F0} {win.Width:F0}x{win.Height:F0}{(win.Maximized ? " (最大化)" : "")}");
                }
            }

            TongfangEc? ec = null;
            WmiInterface? wmi = null;

            try
            {
                wmi = new WmiInterface();
                if (wmi.Initialize())
                {
                    Logger.Info("MICommonInterface WMI接口已就绪");
                    ec = new TongfangEc(wmi);
                }
                else
                {
                    // WMI 不可用时不再造一个"看起来能用"的 EC 对象: 否则界面显示已连接,
                    // 所有读写却静默失败。直接进演示模式, EC 控件置灰。
                    Logger.Warn("WMI接口不可用, 进入演示模式(EC 控制禁用)");
                    _isDemoMode = true;
                }

                _lightingEffects = new LightingEffects(wmi);
                Logger.Info("纯WMI模式 (不加载 Ring0 驱动: 裸 EC 端口在本机不应答, 且会抢走 Fn 快捷键的 EC 事件字节)");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WMI init exception");
                _isDemoMode = true;
            }

            var monitor = new HardwareMonitor();
            _controlService = new ControlService(ec, monitor);
            _ecUsable = ec != null && !_isDemoMode;
            _stressService = new StressTestService();
            _brightness = new BrightnessService();
            _audio = new AudioVolumeControl();
            _sysInfo = new SystemInfoService();

            _viewModel = new MainViewModel(_controlService, _settingsService, _stressService);
            DataContext = _viewModel;

            _trayService = new TrayService();
            _trayService.ShowWindowRequested += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
            _trayService.ExitRequested += (s, e) =>
            {
                Close();
            };
            _trayService.ModeRequested += m =>
            {
                Dispatcher.BeginInvoke(() => _controlService.SetPerformanceMode(m));
            };
            _trayService.Initialize();

            Logger.Info("TrayService initialized");

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            _brightnessDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _brightnessDebounce.Tick += (s, e) =>
            {
                _brightnessDebounce.Stop();
                if (_brightnessReady && !_suppressQuickWrite && ScreenBrightnessSlider != null)
                    _brightness.Set((int)ScreenBrightnessSlider.Value);
            };

            _saveDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                SaveCurrentState();
            };

            // 极限档自检: 配置会被别的程序悄悄改掉, 武装期间每 5 分钟回读一次并自动补发
            _configWatch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _configWatch.Tick += async (s, e) => await VerifyAndHealExtremeAsync();
            _configWatch.Start();

            _audioTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioTimer.Tick += (s, e) =>
            {
                if (AudioLevelText == null || _lightingEffects == null) return;
                var audio = _lightingEffects.Audio;
                if (audio.IsActive && audio.Source == Core.Audio.AudioSourceKind.SystemLoopback)
                {
                    // 节拍模式不显示音量: 只反馈脉冲强度与已识别的拍数/速度
                    double p = audio.BeatPulse;
                    _pulseHold = p > _pulseHold ? p : _pulseHold * 0.75;
                    string bpm = audio.EstimatedBpm > 0 ? $" · {audio.EstimatedBpm:F0}BPM" : "";
                    AudioLevelText.Text = $"节拍 {_pulseHold:P0}{bpm} · 已识别{audio.BeatCount}拍";
                }
                else if (audio.IsActive)
                {
                    AudioLevelText.Text = $"音量 {audio.Level:P0}";
                }
                else
                {
                    double sim = _lightingEffects.CurrentEffect == LightingEffect.Music
                        ? _lightingEffects.SimulatedLevel
                        : 0;
                    AudioLevelText.Text = $"模拟 {sim:P0}";
                }
            };

            _stressService.PropertyChanged += (s, e) =>
            {
                PostUi(UpdateStressUi);
            };
            _stressService.Finished += () =>
            {
                PostUi(() =>
                {
                    ShowStressResults();
                    ShowSnackbar("压力测试已完成并自动停止");
                });
            };

            _controlService.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ControlService.CurrentPerformanceMode))
                {
                    PostUi(() => ApplyModeTheme(_controlService.CurrentPerformanceMode));
                }
                else if (e.PropertyName == nameof(ControlService.CurrentGpuMode))
                {
                    PostUi(UpdateGpuModeStatus);
                }
            };

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;

            IsVisibleChanged += (_, _) =>
            {
                // 收进托盘后把可换页的物理内存交还给系统: 常驻程序没人看界面时不该占着 200 多 MB。
                // 只减工作集(私有内存不变), 重新显示时按需换入, 首帧略慢。
                if (IsLoaded && !IsVisible) TrimWorkingSet();
            };

            Logger.Info("MainWindow constructor done, demo={0}", _isDemoMode);

            if (_isDemoMode)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ShowSnackbar("演示模式：EC驱动未连接，硬件控制不可用", true);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "MainWindow constructor FAILED");
            MessageBox.Show(
                $"初始化失败:\n{ex.Message}\n\n{ex.StackTrace}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Logger.Info("MainWindow Loaded");
        try
        {
            // 初始化当前分区: 首页(其余分区在 InitializeComponent 时 Checked 事件早于字段赋值, 未记录)
            SwitchZone(ZoneHome);

            await _viewModel.InitializeCommand.ExecuteAsync(null);
            Logger.Info("MainWindow InitializeAsync done");
            RestoreSettings();
            SyncKeyboardBrightness();
            InitPickerFromCurrentColors();
            RenderCustomPresets();
            EnsureAudioCaptureForMusic();
            ApplyModeTheme(_controlService.CurrentPerformanceMode);
            UpdateGpuModeStatus();
            EnableRoundedCorners();
            _ = InitQuickControlsAsync();
            ShowSnackbar("MEV蛟龙控制台已就绪");

            // 极限满血是"武装"状态: GPU 锁频一重启就没了, 而它又决定 CPU 能冲多高, 所以自动重放
            if (_settingsService.Settings.ExtremeModeArmed)
            {
                Logger.Info("检测到极限满血已武装, 开机自动重放配置");
                await ApplyExtremeAsync("开机自动重放");
            }

            // 底栏显示本机真实配置(后台读 WMI, 不阻塞界面)
            _ = LoadMachineProfileAsync();

            // 按实时 EC 结果禁用不支持的功能
            _ = LoadCapabilitiesAsync();

            // 压测参数按本机 CPU/内存/磁盘/显卡自适应
            _ = SetupStressAdaptiveAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MainWindow_Loaded error");
            ShowSnackbar("初始化失败: " + ex.Message, true);
        }
    }

    /// <summary>
    /// 读取本机硬件配置并填入底栏(机型/CPU/显卡/内存/磁盘)。
    /// 全部来自 WMI 实时读取, 换一台机器就是那台机器的配置; 任一字段失败只会省略该段。
    /// </summary>
    private async Task LoadMachineProfileAsync()
    {
        try
        {
            var profile = await Task.Run(() => MachineProfile.Load());
            string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            string line = profile.ToFooterLine("MEV蛟龙控制台", version);

            if (FooterText != null && !string.IsNullOrWhiteSpace(line))
                FooterText.Text = line;

            Logger.Info("底栏本机配置: " + line);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "读取本机配置失败, 底栏保持占位文本");
        }
    }


    /// <summary>
    /// 读取安装时(--probe)写入的能力扫描结果; 若没有(直接运行 exe), 现场扫一次并缓存。
    /// </summary>
    private async Task LoadCapabilitiesAsync()
    {
        try
        {
            // 安装时写的能力文件可能来自另一台机器, 也可能是旧版本把"桥能启动"误判成
            // EC 可用时写下的。先做一次实时确认, 再决定这份文件能不能信。
            bool ecDevicePresent = _controlService?.Ec != null;
            bool liveEc = ecDevicePresent && await Task.Run(CheckEcResponsive);
            _ecUsable = liveEc && !_isDemoMode;

            // 启动时第一次读 EC 可能超时, 导致 VM 还没拿到档位(模式单选全为空)。
            // 这里后台重试成功后把档位补回, 避免界面一直停在"未知"。
            if (_ecUsable)
            {
                if (_controlService!.CurrentPerformanceMode == PerformanceMode.Unknown &&
                    _liveMode != PerformanceMode.Unknown)
                    _controlService.CurrentPerformanceMode = _liveMode;
                if (_controlService.CurrentGpuMode == GpuMode.Unknown &&
                    _liveGpuMode != GpuMode.Unknown)
                    _controlService.CurrentGpuMode = _liveGpuMode;
            }

            var support = CapabilityStore.Load() ?? new FeatureSupport();
            if (!liveEc)
            {
                // 没有可用的 EC: 所有依赖 EC 的能力一律按不支持处理, 不看缓存怎么写
                bool stale = support.EcWmi;
                support = support with
                {
                    EcWmi = false,
                    PerfMode = false,
                    GpuMode = false,
                    KeyboardLight = false,
                    LogoLight = false,
                    CpuPowerWall = false
                };
                // 只有"本机确实没有这个设备"时才回写缓存; 设备在但命令临时无响应时不覆盖,
                // 避免一次桥超时把好的能力文件永久改成不可用。
                if (stale && !ecDevicePresent)
                    CapabilityStore.Save(support);
            }
            else if (!support.EcWmi)
            {
                // 文件缺失或来自别的机器: 现场探测一次并覆盖
                support = await Task.Run(() =>
                {
                    var probed = CapabilityProbe.Probe(_controlService?.Ec?.Wmi, "");
                    CapabilityStore.Save(probed);
                    return probed;
                });
            }

            ApplyCapabilities(support);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "读取能力扫描结果失败, 按 EC 不可用处理");
            _ecUsable = false;
            ApplyCapabilities(new FeatureSupport());
        }
    }

    // ================= 快捷控制: 屏幕亮度 / 系统音量 =================

    private async Task InitQuickControlsAsync()
    {
        try
        {
            _suppressQuickWrite = true;

            if (ScreenBrightnessSlider != null)
            {
                ScreenBrightnessSlider.IsEnabled = _brightness.Available;
                if (_brightness.Available)
                {
                    ScreenBrightnessSlider.Value = _brightness.Current;
                    if (ScreenBrightnessPercent != null)
                        ScreenBrightnessPercent.Text = $"{_brightness.Current}%";
                    _brightnessReady = true;
                }
                else if (ScreenBrightnessPercent != null)
                {
                    ScreenBrightnessPercent.Text = "不可用";
                }
            }

            bool audioOk = _audio.Initialize();
            if (VolumeSlider != null)
            {
                VolumeSlider.IsEnabled = audioOk;
                if (MuteToggle != null) MuteToggle.IsEnabled = audioOk;
                if (audioOk)
                {
                    VolumeSlider.Value = _audio.VolumePercent;
                    if (VolumePercentLabel != null)
                        VolumePercentLabel.Text = $"{_audio.VolumePercent}%";
                    if (MuteToggle != null) MuteToggle.IsChecked = _audio.Mute;
                    _volumeReady = true;
                }
                else if (VolumePercentLabel != null)
                {
                    VolumePercentLabel.Text = "不可用";
                }
            }

            // 电源方案
            try
            {
                var plans = _powerPlans.Enumerate();
                if (PowerPlanCombo != null)
                {
                    _suppressPowerPlan = true;
                    PowerPlanCombo.ItemsSource = plans;
                    var active = _powerPlans.GetActive();
                    if (active.HasValue)
                        PowerPlanCombo.SelectedItem = plans.FirstOrDefault(p => p.Id == active.Value);
                    PowerPlanCombo.IsEnabled = plans.Count > 0;
                    _suppressPowerPlan = false;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "枚举电源方案失败"); }

            // 刷新率
            try
            {
                _displayModes.Refresh();
                var rates = _displayModes.GetRefreshRates();
                if (RefreshRateCombo != null)
                {
                    _suppressRefresh = true;
                    RefreshRateCombo.ItemsSource = rates;
                    if (rates.Contains(_displayModes.CurrentHz))
                        RefreshRateCombo.SelectedItem = _displayModes.CurrentHz;
                    RefreshRateCombo.IsEnabled = rates.Count > 1;
                    _suppressRefresh = false;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "枚举刷新率失败"); }

            _suppressQuickWrite = false;

            await Task.Run(_sysInfo.Load);
            _batteryCycles = _sysInfo.BatteryCycles;
            UpdateSystemInfoLabels();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "快捷控制初始化失败");
        }
    }

    private void PollQuickControls()
    {
        try
        {
            // 静音锁定: 在控制台打开“静音”后, Windows 音量面板/媒体键/其它程序把静音关掉,
            // 这里每秒重新压回去, 直到用户在本控制台里显式关闭静音。
            if (_muteLock && _volumeReady)
            {
                _audio.Refresh();
                if (!_audio.Mute) _audio.SetMute(true);
                if (MuteToggle != null && MuteToggle.IsChecked != true)
                {
                    _suppressQuickWrite = true;
                    MuteToggle.IsChecked = true;
                    _suppressQuickWrite = false;
                }
            }

            // 音量/静音: 每秒回读一次, 外部(系统音量面板/媒体键/其它程序)改动 1 秒内就能看到
            if (++_audioPollCounter >= 1)
            {
                _audioPollCounter = 0;
                if (_volumeReady)
                {
                    _audio.Refresh();
                    if (VolumeSlider != null && !VolumeSlider.IsMouseCaptureWithin)
                    {
                        _suppressQuickWrite = true;
                        VolumeSlider.Value = _audio.VolumePercent;
                        if (VolumePercentLabel != null)
                            VolumePercentLabel.Text = $"{_audio.VolumePercent}%";
                        if (MuteToggle != null) MuteToggle.IsChecked = _audio.Mute;
                        _suppressQuickWrite = false;
                        UpdateSystemInfoLabels();
                    }
                }
            }

            // 亮度: WMI 查询放后台(桥/WMI 抖动时不会拖住 UI), 2 秒一轮
            if (++_brightnessPollCounter >= 2)
            {
                _brightnessPollCounter = 0;
                if (_brightnessReady && !_brightnessRefreshBusy)
                {
                    _brightnessRefreshBusy = true;
                    _ = Task.Run(() =>
                    {
                        try { _brightness.Refresh(); } catch { }
                        PostUi(() =>
                        {
                            try
                            {
                                if (ScreenBrightnessSlider != null && !ScreenBrightnessSlider.IsMouseCaptureWithin)
                                {
                                    _suppressQuickWrite = true;
                                    ScreenBrightnessSlider.Value = _brightness.Current;
                                    if (ScreenBrightnessPercent != null)
                                        ScreenBrightnessPercent.Text = $"{_brightness.Current}%";
                                    _suppressQuickWrite = false;
                                    UpdateSystemInfoLabels();
                                }
                            }
                            finally { _brightnessRefreshBusy = false; }
                        });
                    });
                }
            }

            // 外部(系统托盘/其它程序)切换电源方案后同步下拉框
            if (++_powerPlanPollCounter >= 3)
            {
                _powerPlanPollCounter = 0;
                if (PowerPlanCombo?.SelectedItem is PowerPlan selected &&
                    _powerPlans.GetActive() is Guid active && active != selected.Id)
                {
                    var match = (PowerPlanCombo.ItemsSource as IEnumerable<PowerPlan>)?
                        .FirstOrDefault(p => p.Id == active);
                    if (match != null)
                    {
                        _suppressPowerPlan = true;
                        PowerPlanCombo.SelectedItem = match;
                        _suppressPowerPlan = false;
                    }
                }
            }

            // 外部改变刷新率(显示设置/合盖唤醒)后同步: 2 秒一轮, EnumDisplaySettings 本身开销极低
            if (++_refreshPollCounter >= 2)
            {
                _refreshPollCounter = 0;
                _displayModes.Refresh();
                if (RefreshRateCombo?.SelectedItem is int hz && hz != _displayModes.CurrentHz)
                {
                    _suppressRefresh = true;
                    RefreshRateCombo.SelectedItem = _displayModes.CurrentHz;
                    _suppressRefresh = false;
                    UpdateSystemInfoLabels();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "快捷控制轮询失败");
        }
    }

    private void ScreenBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScreenBrightnessPercent != null)
            ScreenBrightnessPercent.Text = $"{(int)e.NewValue}%";
        if (!_brightnessReady || _suppressQuickWrite) return;
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumePercentLabel != null)
            VolumePercentLabel.Text = $"{(int)e.NewValue}%";
        if (!_volumeReady || _suppressQuickWrite) return;
        // 静音状态下拖动音量只调整数值, 保持静音; 要恢复声音必须显式点“静音”开关。
        _audio.SetVolume((int)e.NewValue);
        UpdateSystemInfoLabels();
    }

    private void MuteToggle_Changed(object sender, RoutedEventArgs e)
    {
        // 用 Checked/Unchecked 而不是 Click: 键盘、UIA、鼠标点击都能触发,
        // 初始化时由 _suppressQuickWrite / !_volumeReady 挡住。
        if (!_volumeReady || _suppressQuickWrite) return;
        _muteLock = MuteToggle.IsChecked == true;
        _audio.SetMute(_muteLock);
        UpdateSystemInfoLabels();
    }

    private void PowerPlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPowerPlan) return;
        if (PowerPlanCombo?.SelectedItem is PowerPlan plan)
            _powerPlans.SetActive(plan.Id);
    }

    private void RefreshRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        if (RefreshRateCombo?.SelectedItem is int hz && hz != _displayModes.CurrentHz)
        {
            if (_displayModes.SetRefreshRate(hz))
            {
                _displayModes.Refresh();
                UpdateSystemInfoLabels();
            }
            else
            {
                // 失败时把下拉框拨回当前值, 避免界面显示一个没生效的刷新率
                _suppressRefresh = true;
                RefreshRateCombo.SelectedItem = _displayModes.CurrentHz;
                _suppressRefresh = false;
            }
        }
    }

    private void UpdateSystemInfoLabels()
    {
        if (DisplayInfoText != null)
        {
            var bits = new List<string>();
            if (_sysInfo.DisplayPanel.Length > 0)
            {
                string extra = "";
                if (_sysInfo.MonitorYear > 0) extra += $" {_sysInfo.MonitorYear}";
                if (_sysInfo.MonitorSerial.Length > 0) extra += $" SN {_sysInfo.MonitorSerial}";
                bits.Add(_sysInfo.DisplayPanel + extra);
            }
            string mode = _displayModes.Available
                ? $"{_displayModes.Width}×{_displayModes.Height} @ {_displayModes.CurrentHz}Hz"
                : _sysInfo.DisplayMode;
            if (mode.Length > 0) bits.Add(mode);
            bits.Add($"亮度 {(_brightness.Available ? _brightness.Current + "%" : "不可用")}");
            string line = string.Join(" · ", bits);
            if (_sysInfo.DisplayAdapters.Count > 0)
                line += "\n适配器 " + string.Join(" / ", _sysInfo.DisplayAdapters);
            DisplayInfoText.Text = line;
        }
        if (AudioInfoText != null)
        {
            if (_audio.Available)
            {
                string fmt = _audio.FormatText.Length > 0 ? $" · {_audio.FormatText}" : "";
                AudioInfoText.Text =
                    $"{_audio.DeviceName}{fmt} · 音量 {_audio.VolumePercent}% · {(_audio.Mute ? "已静音" : "未静音")}\n" +
                    $"ID {_audio.DeviceId}";
            }
            else
            {
                AudioInfoText.Text = "未检测到播放设备";
            }
        }
        if (OsInfoText != null)
        {
            string os = _sysInfo.OsText;
            if (_sysInfo.OsInstallDate.Length > 0) os += $" · 安装 {_sysInfo.OsInstallDate}";
            if (_sysInfo.LastBoot != default)
                os += $" · 已运行 {FormatUptime(DateTime.Now - _sysInfo.LastBoot)}";
            OsInfoText.Text = os;
        }
    }

    private void UpdateHardwareDetailLabels(HardwareData d)
    {
        var clocks = d.CpuClocks.Values.Where(v => v > 0).ToList();
        float load = d.CpuLoads.TryGetValue("CPU Total", out var total) ? total
            : (d.CpuLoads.Count > 0 ? d.CpuLoads.Values.Average() : 0);

        if (MachineInfoText != null) MachineInfoText.Text = _sysInfo.MachineText;
        if (CpuInfoText != null)
        {
            string name = _sysInfo.CpuName.Length > 0 ? _sysInfo.CpuName : "CPU";
            string socket = _sysInfo.CpuSocket.Length > 0 ? $" · {_sysInfo.CpuSocket}" : "";
            string arch = _sysInfo.CpuArch.Length > 0 ? $" · {_sysInfo.CpuArch}" : "";
            string maxClock = _sysInfo.CpuMaxClockMhz > 0
                ? $" · 标称 {_sysInfo.CpuMaxClockMhz / 1000.0:F1}GHz" : "";
            string cache = "";
            if (_sysInfo.CpuL2Mb > 0) cache += $" · L2 {_sysInfo.CpuL2Mb:F0}MB";
            if (_sysInfo.CpuL3Mb > 0) cache += $" · L3 {_sysInfo.CpuL3Mb:F0}MB";
            string pkg = d.CpuPackageTemp.HasValue ? $"{d.CpuPackageTemp:F0}°C" : "--";
            string range = clocks.Count > 0 ? $"{clocks.Min() / 1000:F1}–{clocks.Max() / 1000:F1}GHz" : "--";
            CpuInfoText.Text =
                $"{name} · {_sysInfo.CpuCores}C/{_sysInfo.CpuThreads}T{socket}{cache}{maxClock}{arch}\n" +
                $"封装 {pkg} · 功耗 {(d.CpuPower?.ToString("F1") ?? "--")}W · 全核 {range} · 负载 {load:F0}%";
        }
        if (DeviceMemoryText != null) DeviceMemoryText.Text = BuildMemoryBlock(d);
        if (DeviceStorageText != null) DeviceStorageText.Text = BuildStorageBlock(d);
        if (NetworkInfoText != null) NetworkInfoText.Text = _sysInfo.NetworkText;

        if (CpuClockValue != null)
        {
            CpuClockValue.Text = clocks.Count > 0
                ? $"{clocks.Min() / 1000:F1}–{clocks.Max() / 1000:F1} GHz · 负载 {load:F0}%"
                : "--";
        }

        if (GpuDetailValue != null)
        {
            var bits = new List<string>();
            if (d.GpuCoreClock.HasValue)
            {
                string mem = d.GpuMemoryClock.HasValue ? $"{d.GpuMemoryClock:F0}" : "--";
                bits.Add($"{d.GpuCoreClock:F0}/{mem} MHz");
            }
            if (d.GpuLoad.HasValue) bits.Add($"{d.GpuLoad:F0}%");
            if (d.GpuMemoryUsed.HasValue && d.GpuMemoryTotal.HasValue)
                bits.Add($"{d.GpuMemoryUsed / 1024:F1}/{d.GpuMemoryTotal / 1024:F1} GB");
            if (d.GpuPState.Length > 0) bits.Add(d.GpuPState);
            GpuDetailValue.Text = bits.Count > 0 ? string.Join(" · ", bits) : "--";
        }

        if (GpuDetailText != null)
        {
            var lines = new List<string>();
            var l1 = new List<string>();
            if (d.GpuName.Length > 0) l1.Add(d.GpuName);
            if (d.GpuDriver.Length > 0) l1.Add($"驱动 {d.GpuDriver}");
            if (d.GpuPState.Length > 0) l1.Add(d.GpuPState);
            l1.Add(d.GpuTemp.HasValue ? $"{d.GpuTemp:F0}°C" : "--");
            l1.Add(d.GpuPower.HasValue ? $"{d.GpuPower:F1}W" : "--");
            lines.Add(string.Join(" · ", l1));

            lines.Add(
                $"核心 {d.GpuCoreClock?.ToString("F0") ?? "--"}/{d.GpuMaxCoreClock?.ToString("F0") ?? "--"} MHz · " +
                $"显存 {d.GpuMemoryClock?.ToString("F0") ?? "--"}/{d.GpuMaxMemoryClock?.ToString("F0") ?? "--"} MHz · " +
                $"GPU {d.GpuLoad?.ToString("F0") ?? "--"}% / 显存 {d.GpuMemoryLoad?.ToString("F0") ?? "--"}%");

            // nvidia-smi 的 memory.* 单位是 MiB, 这里换算成 GB 再展示
            string MemGb(float? v) => v.HasValue ? (v.Value / 1024).ToString("F1") : "--";
            var l3 = new List<string>();
            l3.Add($"显存 {MemGb(d.GpuMemoryUsed)}/{MemGb(d.GpuMemoryTotal)} GB（空闲 {MemGb(d.GpuMemoryFree)}）");
            l3.Add($"功耗 {d.GpuPower?.ToString("F1") ?? "--"}/{d.GpuPowerLimit?.ToString("F1") ?? "--"} W");
            l3.Add($"风扇 {d.GpuFanPercent?.ToString("F0") ?? "--"}%");
            if (d.GpuPcie.Length > 0) l3.Add(d.GpuPcie);
            lines.Add(string.Join(" · ", l3));
            GpuDetailText.Text = string.Join("\n", lines);
        }

        if (BatteryHealthText != null)
        {
            _sysInfo.RefreshPowerStatus();
            var lines = new List<string>();

            string status = _sysInfo.BatteryNoBattery ? "无电池"
                : _sysInfo.BatteryCharging ? "充电中"
                : !_sysInfo.AcOnline ? "放电中"
                : _sysInfo.BatteryPercent >= 100 ? "已充满" : "已接通电源";
            var l1 = new List<string> { status };
            if (d.BatteryLevel.HasValue) l1.Add($"电量 {d.BatteryLevel:F0}%");
            if (d.BatteryPower.HasValue) l1.Add($"充放电功率 {d.BatteryPower:F1}W");
            if (d.BatteryVoltage.HasValue) l1.Add($"电压 {d.BatteryVoltage:F1}V");
            if (d.BatteryCurrent.HasValue) l1.Add($"电流 {d.BatteryCurrent:F2}A");
            lines.Add(string.Join(" · ", l1));

            // 容量健康度: 优先满充/设计, 否则用 LHM 的损耗反推
            double? health = null;
            if (d.BatteryFullMwh is > 0 && d.BatteryDesignMwh is > 0)
                health = d.BatteryFullMwh.Value / d.BatteryDesignMwh.Value * 100;
            else if (d.BatteryDegradation.HasValue)
                health = 100 - d.BatteryDegradation.Value;

            var l2 = new List<string>();
            if (health.HasValue) l2.Add($"容量健康 {health:F0}%");
            if (d.BatteryFullMwh.HasValue) l2.Add($"满充 {d.BatteryFullMwh / 1000:F1}Wh");
            if (d.BatteryDesignMwh is > 0) l2.Add($"设计 {d.BatteryDesignMwh / 1000:F1}Wh");
            if (d.BatteryDegradation.HasValue) l2.Add($"损耗 {d.BatteryDegradation:F0}%");
            if (_batteryCycles.HasValue) l2.Add($"循环 {_batteryCycles} 次");
            if (l2.Count > 0) lines.Add(string.Join(" · ", l2));

            var l3 = new List<string>();
            string maker = $"{_sysInfo.BatteryManufacturer} {_sysInfo.BatteryName}".Trim();
            if (maker.Length > 0) l3.Add($"电池 {maker}");
            if (_sysInfo.BatteryChemistry.Length > 0) l3.Add(_sysInfo.BatteryChemistry);
            if (_sysInfo.BatteryDesignVoltageV > 0) l3.Add($"设计电压 {_sysInfo.BatteryDesignVoltageV:F1}V");
            if (d.BatteryRemainingMwh.HasValue) l3.Add($"剩余 {d.BatteryRemainingMwh / 1000:F2}Wh");
            if (d.BatteryTemp.HasValue) l3.Add($"温度 {d.BatteryTemp:F0}°C");
            else l3.Add("温度 --");
            if (l3.Count > 0) lines.Add(string.Join(" · ", l3));

            string runtime = "--";
            if (!_sysInfo.AcOnline && _sysInfo.BatteryLifeSeconds > 0)
                runtime = FormatDuration(_sysInfo.BatteryLifeSeconds / 3600.0);
            else if (!_sysInfo.AcOnline && d.BatteryPower is < 0 && d.BatteryRemainingMwh.HasValue)
                runtime = FormatDuration(d.BatteryRemainingMwh.Value / 1000.0 / Math.Abs(d.BatteryPower.Value));

            string toFull = "--";
            if (_sysInfo.BatteryCharging && d.BatteryPower is > 0 &&
                d.BatteryFullMwh.HasValue && d.BatteryRemainingMwh.HasValue)
            {
                double needWh = (d.BatteryFullMwh.Value - d.BatteryRemainingMwh.Value) / 1000.0;
                toFull = FormatDuration(needWh / d.BatteryPower.Value);
            }

            string acType = _controlService.EcAcTypeText;
            if (string.IsNullOrWhiteSpace(acType) || acType == "--") acType = "未知";
            lines.Add($"适配器 {acType} · 交流 {(_sysInfo.AcOnline ? "已接入" : "未接入")} · " +
                      $"预计可用 {runtime} · 预计充满 {toFull}");

            BatteryHealthText.Text = string.Join("\n", lines);
        }

        UpdateHardwareDetailText(d);
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return "--";
        return $"{(int)t.TotalDays}天{t.Hours:D2}小时{t.Minutes:D2}分";
    }

    private static string FormatDuration(double hours)
    {
        if (hours <= 0 || double.IsNaN(hours) || double.IsInfinity(hours)) return "--";
        int total = (int)Math.Round(hours * 3600);
        int h = total / 3600, m = (total % 3600) / 60;
        return h > 0 ? $"{h}h {m:D2}m" : $"{m}m";
    }

    /// <summary>CPU 概要 + 每核表 + 内存 + 硬盘, 等宽文本输出。</summary>
    private void UpdateHardwareDetailText(HardwareData d)
    {
        var cores = d.CpuClocks.Keys
            .Select(ParseCoreNumber)
            .Where(n => n > 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        var clocks = d.CpuClocks.Values.Where(v => v > 0).ToList();
        float totalLoad = d.CpuLoads.TryGetValue("CPU Total", out var t) ? t
            : (d.CpuLoads.Count > 0 ? d.CpuLoads.Values.Average() : 0);

        // ===== CPU 概要 =====
        if (CpuSummaryText != null)
        {
            string cpuName = _sysInfo.CpuName.Length > 0 ? _sysInfo.CpuName : "CPU";
            string cache = "";
            if (_sysInfo.CpuL2Mb > 0) cache += $" · L2 {_sysInfo.CpuL2Mb:F0}MB";
            if (_sysInfo.CpuL3Mb > 0) cache += $" · L3 {_sysInfo.CpuL3Mb:F0}MB";
            string pkg = d.CpuPackageTemp.HasValue ? $"{d.CpuPackageTemp:F0}°C" : "--";
            string range = clocks.Count > 0 ? $"{clocks.Min() / 1000:F1}–{clocks.Max() / 1000:F1}GHz" : "--";
            CpuSummaryText.Text =
                $"CPU  {cpuName} · {_sysInfo.CpuCores}C/{_sysInfo.CpuThreads}T{cache}\n" +
                $"封装 {pkg} · 全核 {range} · 总负载 {totalLoad:F0}%";
        }

        // ===== 每核表(按核心数自适应列数) =====
        bool anyCoreTemp = cores.Count > 0 && cores.Any(n => CoreValue(d.CpuTemperatures, n) > 0);
        RenderCoreColumns(cores, d, anyCoreTemp);

        if (MemoryDetailText != null) MemoryDetailText.Text = BuildMemoryBlock(d);
        if (StorageDetailText != null) StorageDetailText.Text = BuildStorageBlock(d);
    }

    private string BuildMemoryBlock(HardwareData d)
    {
        var sb = new StringBuilder();
        if (d.MemoryUsedGb.HasValue)
        {
            double memTotal = _sysInfo.MemoryTotalGb > 0
                ? _sysInfo.MemoryTotalGb
                : (d.MemoryAvailableGb.HasValue ? d.MemoryUsedGb.Value + d.MemoryAvailableGb.Value : 0);
            sb.AppendLine(memTotal > 0
                ? $"已用 {d.MemoryUsedGb:F1} / {memTotal:F1} GB · {d.MemoryLoad:F0}%"
                : $"已用 {d.MemoryUsedGb:F1} GB · {d.MemoryLoad:F0}%");
            if (d.MemoryAvailableGb.HasValue)
                sb.AppendLine($"可用 {d.MemoryAvailableGb:F1} GB");
            if (d.MemoryVirtualUsedGb.HasValue && d.MemoryVirtualAvailableGb.HasValue)
            {
                double vTotal = d.MemoryVirtualUsedGb.Value + d.MemoryVirtualAvailableGb.Value;
                double vPct = vTotal > 0 ? d.MemoryVirtualUsedGb.Value / vTotal * 100 : 0;
                sb.AppendLine($"虚拟 {d.MemoryVirtualUsedGb:F1} / {vTotal:F1} GB · {vPct:F0}%");
            }
        }
        else
        {
            sb.AppendLine("数据不可用");
        }
        foreach (string module in _sysInfo.MemoryModules)
            sb.AppendLine(module);
        return sb.ToString().TrimEnd();
    }

    private string BuildStorageBlock(HardwareData d)
    {
        var sb = new StringBuilder();
        string model = _sysInfo.StorageModel.Length > 0 ? _sysInfo.StorageModel : d.StorageName;
        string size = _sysInfo.StorageSizeGb > 0 ? $" · {_sysInfo.StorageSizeGb:F0}GB" : "";
        string bus = _sysInfo.StorageBus.Length > 0 ? $" · {_sysInfo.StorageBus}" : "";
        sb.AppendLine(model.Length > 0 ? $"{model}{size}{bus}" : "未检测到 NVMe/存储传感器");

        var l1 = new List<string>();
        if (d.StorageTemp1.HasValue) l1.Add($"{d.StorageTemp1:F0}°C");
        else if (d.StorageTemp.HasValue) l1.Add($"{d.StorageTemp:F0}°C");
        if (d.StorageTemp2.HasValue) l1.Add($"传感器2 {d.StorageTemp2:F0}°C");
        if (d.StorageWear.HasValue)
        {
            l1.Add($"健康 {Math.Clamp(100 - d.StorageWear.Value, 0, 100):F0}%");
            l1.Add($"损耗 {d.StorageWear:F0}%");
        }
        if (d.StorageSpare.HasValue) l1.Add($"备用 {d.StorageSpare:F0}%");
        if (d.StorageSpareThreshold.HasValue) l1.Add($"阈值 {d.StorageSpareThreshold:F0}%");
        if (d.StorageUsed.HasValue) l1.Add($"已用 {d.StorageUsed:F0}%");
        if (l1.Count > 0) sb.AppendLine(string.Join(" · ", l1));

        var l2 = new List<string>();
        if (d.StorageReadGb.HasValue) l2.Add($"累计读 {d.StorageReadGb / 1024:F2}TB");
        if (d.StorageWriteGb.HasValue) l2.Add($"写 {d.StorageWriteGb / 1024:F2}TB");
        if (d.StorageReadActivity.HasValue) l2.Add($"读活动 {d.StorageReadActivity:F0}%");
        if (d.StorageWriteActivity.HasValue) l2.Add($"写活动 {d.StorageWriteActivity:F0}%");
        if (l2.Count > 0)
        {
            l2.Add("来源 LHM");
            sb.AppendLine(string.Join(" · ", l2));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 每核表按核心数量自适应列数: 每列约 8 行, 2–4 列。线程更多的机器不会把卡片撑得无限长,
    /// 核心少的机器也不会出现大片空白。
    /// </summary>
    private void RenderCoreColumns(List<int> cores, HardwareData d, bool anyTemp)
    {
        if (CpuCoresGrid == null) return;

        if (cores.Count == 0)
        {
            if (_coreColumns.Count != 0)
            {
                CpuCoresGrid.Children.Clear();
                CpuCoresGrid.Columns = 1;
                _coreColumns.Clear();
            }
            if (CpuCoresGrid.Children.Count == 0)
            {
                CpuCoresGrid.Children.Add(new TextBlock
                {
                    Text = "核心传感器数据不可用",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0))
                });
            }
            return;
        }

        int cols = Math.Clamp((int)Math.Ceiling(cores.Count / 8.0), 2, 4);
        if (_coreColumns.Count != cols || _coreAnyTemp != anyTemp || CpuCoresGrid.Columns != cols)
        {
            CpuCoresGrid.Children.Clear();
            _coreColumns.Clear();
            CpuCoresGrid.Columns = cols;

            int per = (int)Math.Ceiling(cores.Count / (double)cols);
            for (int c = 0; c < cols; c++)
            {
                var block = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5,
                    LineHeight = 18,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0)),
                    Margin = new Thickness(c == 0 ? 0 : 12, 0, 0, 0)
                };
                CpuCoresGrid.Children.Add(block);
                _coreColumns.Add((block, cores.Skip(c * per).Take(per).ToList()));
            }
            _coreAnyTemp = anyTemp;
        }

        string header = anyTemp
            ? "核心  频率     VID    负载  功耗  温度"
            : "核心  频率     VID    负载  功耗";

        foreach (var (block, list) in _coreColumns)
        {
            if (list.Count == 0)
            {
                block.Text = "";
                continue;
            }
            var sb = new StringBuilder(header);
            foreach (int n in list)
            {
                float clock = CoreValue(d.CpuClocks, n);
                float load = CoreValue(d.CpuLoads, n);
                float volt = CoreValue(d.CpuVoltages, n);
                float power = CoreValue(d.CpuCorePowers, n);
                string vs = volt > 0.05f ? $"{volt:F2}V" : "--";
                sb.Append('\n');
                if (anyTemp)
                {
                    float temp = CoreValue(d.CpuTemperatures, n);
                    string ts = temp > 0 ? $"{temp:F0}°C" : "--";
                    sb.Append($"#{n,-3} {clock / 1000,5:F1}GHz {vs,6} {load,4:F0}% {power,5:F1}W {ts,5}");
                }
                else
                {
                    sb.Append($"#{n,-3} {clock / 1000,5:F1}GHz {vs,6} {load,4:F0}% {power,5:F1}W");
                }
            }
            block.Text = sb.ToString();
        }
    }

    private static int ParseCoreNumber(string name)
    {
        var m = Regex.Match(name, @"#(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    private static float CoreValue(Dictionary<string, float> dict, int core)
    {
        foreach (var kv in dict)
        {
            if (ParseCoreNumber(kv.Key) == core) return kv.Value;
        }
        return 0;
    }

    /// <summary>带重试的 EC 响应探测: 桥刚启动时第一条命令可能超时。</summary>
    private bool CheckEcResponsive()
    {
        var ec = _controlService?.Ec;
        if (ec == null) return false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var mode = ec.GetPerformanceMode();
                if (mode != PerformanceMode.Unknown)
                {
                    _liveMode = mode;
                    try { _liveGpuMode = ec.GetGpuMode(); } catch { }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "EC 响应探测失败");
            }
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>顶部连接状态: 只有 EC 真正可用时才显示"已连接"。</summary>
    private void UpdateConnectionStatus()
    {
        if (StatusText == null || StatusDot == null) return;
        if (_isDemoMode || !_ecUsable)
        {
            StatusText.Text = "未连接";
            StatusText.ToolTip = "未检测到厂商 EC（MICommonInterface），硬件控制已禁用；温度/功耗监控仍可用";
            StatusDot.Fill = BrushMuted;
        }
        else
        {
            StatusText.Text = "已连接";
            StatusText.ToolTip = "EC 已连接";
            StatusDot.Fill = BrushOk;
        }
    }

    /// <summary>
    /// 按能力扫描结果禁用本机不支持的入口。
    /// 只做"禁用", 不改布局 —— 用户仍能看到功能存在, 但不会点出一个假失败。
    /// </summary>
    private void ApplyCapabilities(FeatureSupport f)
    {
        try
        {
            // 既要能力文件说支持, 也要本次运行实测 EC 有响应;
            // 文件可能来自别的机器或旧版本的误判。
            bool ecUsable = f.EcWmi && _ecUsable;

            if (PerfModeGrid != null && (!f.PerfMode || !ecUsable))
                PerfModeGrid.IsEnabled = false;

            // 整个显卡卡片一起禁用: 只灰单选按钮的话, "切独显直连并重启" 仍可点,
            // 会写 0x09 并执行 shutdown /r
            if (GpuModeCard != null && (!f.GpuMode || !ecUsable))
                GpuModeCard.IsEnabled = false;

            if (MaxPerfBtn != null && (!f.CpuPowerWall || !ecUsable))
                MaxPerfBtn.IsEnabled = false;
            if (MaxPerfResetBtn != null && !ecUsable)
                MaxPerfResetBtn.IsEnabled = false;

            // 键盘灯/Logo 都走 EC, 演示模式下整页禁用
            if (LightingPanel != null && (!f.KeyboardLight || !ecUsable))
            {
                LightingPanel.IsEnabled = false;
                if (CurrentEffectLabel != null)
                    CurrentEffectLabel.Text = ecUsable ? "本机不支持键盘灯效" : "EC 不可用";
            }

            if (LogoLightToggle != null)
            {
                LogoLightToggle.IsEnabled = f.LogoLight && ecUsable;
                if (!f.LogoLight && LogoLightStatus != null) LogoLightStatus.Text = "该机型不支持";
            }

            // 性能档读不到/不可用时, 标题区不能停在默认的"游戏模式"和橙色闪电上。
            if (!ecUsable || !f.PerfMode)
            {
                ApplyModeTheme(PerformanceMode.Unknown);
                if (EcPerfModeValue != null) EcPerfModeValue.Text = "未知";
            }
            _trayService?.SetModeMenuEnabled(ecUsable);
            UpdateConnectionStatus();

            Logger.Info($"已按能力扫描应用限制: {f.Summary}");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "应用能力限制失败");
        }
    }

    /// <summary>启动时恢复上次保存的记忆: 灯效/颜色/亮度/风扇/自启状态</summary>
    private void RestoreSettings()
    {
        try
        {
            var s = _settingsService.Settings;

            // 顺序很重要: 先恢复颜色与调色板, 最后再切效果。
            // 原先是先 SetEffect 再 SetColor —— 静态色会先闪一帧旧颜色, 渐变类效果
            // 会先用旧调色板跑一帧, 而且多色调色板(随机渐变生成的 3-6 色)根本没被恢复。
            _lightingEffects?.SetColor(s.Keyboard.ColorR, s.Keyboard.ColorG, s.Keyboard.ColorB);
            _lightingEffects?.SetColor2(s.Keyboard.Color2R, s.Keyboard.Color2G, s.Keyboard.Color2B);

            var savedPalette = new List<(byte r, byte g, byte b)>();
            foreach (var item in s.Keyboard.Palette)
            {
                var parts = item.Split(',');
                if (parts.Length == 3
                    && byte.TryParse(parts[0], out var pr)
                    && byte.TryParse(parts[1], out var pg)
                    && byte.TryParse(parts[2], out var pb))
                    savedPalette.Add((pr, pg, pb));
            }
            if (savedPalette.Count >= 2) _lightingEffects?.SetPalette(savedPalette);

            if (Enum.TryParse<LightingEffect>(s.Keyboard.Effect, out var effect))
            {
                _lightingEffects?.SetEffect(effect);
                HighlightEffectButton(effect);
                if (CurrentEffectLabel != null)
                    CurrentEffectLabel.Text = "当前: " + EffectDisplayName(effect);
                // 敲击灯效依赖全局键盘钩子, 走 MainWindow.SetEffect 时才会安装;
                // 直接调引擎的 SetEffect 会漏掉这一步, 表现为重启后"敲击"灯效不响应按键。
                if (effect == LightingEffect.Reactive) InstallKeyboardHook();
            }

            // 动态随机: 恢复上次的模式(单色/渐变)与开关状态
            _randomGradientMode = s.Keyboard.RandomMode == "Gradient";
            if (RandomDynamicToggle != null) RandomDynamicToggle.IsChecked = s.Keyboard.RandomDynamic;

            // 允许恢复 0: 以前 >0 才恢复, 用户把亮度调到 0 后重启会被强制点亮
            if (s.Keyboard.Brightness <= 100)
                BrightnessSlider.Value = s.Keyboard.Brightness;

            // A 面 Logo 背光: 以 EC 实际状态为准; 与记忆不一致时按记忆补发一次。
            // 读不到(-1)说明该 EC 不实现 0x0F, 直接禁用控件并如实标注, 不假装成功。
            if (LogoLightToggle != null && _controlService?.Ec != null)
            {
                var ec = _controlService.Ec;
                int logo = ec.GetLogoLight();
                if (logo < 0)
                {
                    LogoLightToggle.IsEnabled = false;
                    if (LogoLightStatus != null) LogoLightStatus.Text = "该机型不支持";
                }
                else
                {
                    if (logo != (s.Keyboard.LogoLight ? 1 : 0) && ec.SetLogoLight(s.Keyboard.LogoLight))
                        logo = ec.GetLogoLight();

                    _logoLightSuppress = true;
                    try { LogoLightToggle.IsChecked = logo == 1; }
                    finally { _logoLightSuppress = false; }
                    if (LogoLightStatus != null) LogoLightStatus.Text = logo == 1 ? "已开" : "已关";
                }
                Logger.Info($"Logo 背光恢复: 记忆={s.Keyboard.LogoLight}, EC读数={logo}");
            }

            if (s.Keyboard.PulseCap >= 10 && s.Keyboard.PulseCap <= 100)
                PulseCapSlider.Value = s.Keyboard.PulseCap;


            // 用户功耗墙设置: 重新下发 EC (该寄存器掉电不保存)
            // 只有"已武装"时才重放功耗墙: 未武装的残留值(还原后异常退出等)不该在开机时又写一遍 EC
            if (s.ExtremeModeArmed && s.CpuSplWatts >= 25 && s.CpuSplWatts <= 255 && _controlService != null)
            {
                _controlService.CpuPowerOverrideWatts = s.CpuSplWatts;
                _controlService.Ec?.SetCpuPowerLimit(s.CpuSplWatts,
                    Math.Clamp(s.CpuSplWatts + 40, s.CpuSplWatts, 255));
            }

            // 升级到 MEV 名称后, 把旧版(JiaolongControlCenter)的任务/注册表自启项清掉
            AutostartService.CleanupLegacy();
            _viewModel.SyncAutostartState(AutostartService.IsEnabled());

            Logger.Info($"已恢复记忆设置: 灯效={s.Keyboard.Effect}, 亮度={s.Keyboard.Brightness}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "恢复记忆设置失败");
        }
    }


    /// <summary>把当前界面状态写入设置文件(记忆)</summary>
    private void SaveCurrentState()
    {
        try
        {
            var effect = _lightingEffects?.CurrentEffect ?? LightingEffect.Static;
            var (r, g, b) = _lightingEffects?.CurrentColor ?? ((byte)255, (byte)0, (byte)0);
            var (r2, g2, b2) = _lightingEffects?.CurrentColor2 ?? ((byte)0, (byte)0, (byte)255);
            _settingsService.UpdateSettings(s =>
            {
                s.Keyboard.Effect = effect.ToString();
                s.Keyboard.Palette = _lightingEffects?.CurrentPalette
                    .Select(c => $"{c.r},{c.g},{c.b}").ToList() ?? new List<string>();
                s.Keyboard.RandomMode = _randomGradientMode ? "Gradient" : "Single";
                s.Keyboard.RandomDynamic = RandomDynamicToggle?.IsChecked == true;
                s.Keyboard.ColorR = r;
                s.Keyboard.ColorG = g;
                s.Keyboard.ColorB = b;
                s.Keyboard.Color2R = r2;
                s.Keyboard.Color2G = g2;
                s.Keyboard.Color2B = b2;
                s.Keyboard.Brightness = (byte)Math.Clamp(BrightnessSlider.Value, 0, 100);
                s.Keyboard.PulseCap = (int)Math.Clamp(PulseCapSlider.Value, 10, 100);
                s.CpuSplWatts = _settingsService.Settings.ExtremeModeArmed
                    ? (_controlService?.CpuPowerOverrideWatts ?? 0)
                    : 0;
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "保存记忆设置失败");
        }
    }

    private void RequestSave()
    {
        if (_saveDebounce == null) return; // InitializeComponent 期间的滑条事件早于计时器构造
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>
    /// 从后台线程/定时器回调投递到 UI。窗口关闭、Dispatcher 已停止时直接丢弃 —— 
    /// 否则 BeginInvoke 会抛 InvalidOperationException, 而它常常发生在没有 catch 的工作线程上。
    /// </summary>
    private void PostUi(Action action)
    {
        var d = Dispatcher;
        if (d == null || d.HasShutdownStarted || d.HasShutdownFinished) return;
        try { d.BeginInvoke(action); } catch { }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon != null)
        {
            MaximizeIcon.Kind = WindowState == WindowState.Maximized
                ? PackIconKind.WindowRestore
                : PackIconKind.WindowMaximize;
        }

        if (WindowState == WindowState.Minimized)
        {
            // 托盘没注册成功时绝不能 Hide(): 窗口会既没有托盘图标也没有任务栏按钮,
            // 再启动 exe 又会被单实例静默退出, 用户只能杀进程。
            if (_trayService?.IsAvailable == true)
            {
                Hide();
                _trayService.ShowNotification("MEV蛟龙控制台", "程序已最小化到系统托盘，右键托盘图标可操作");
            }
            else
            {
                ShowSnackbar("系统托盘不可用, 窗口保持在任务栏", true);
            }
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 把可换页的物理内存交还系统。常驻托盘的程序在没人看界面时不该占着两百多 MB 物理页;
    /// 只影响工作集(私有内存不变), 重新显示时按需换回, 首帧略慢。
    /// </summary>
    private void TrimWorkingSet()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); }
        catch (Exception ex) { Logger.Debug(ex, "工作集归还失败(可忽略)"); }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>无边框窗口在 Win11 默认直角, 用 DWM 恢复圆角</summary>
    private void EnableRoundedCorners()
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "DWM圆角设置失败(可忽略)");
        }
    }

    /// <summary>Hero 闪电图标与模式徽章随当前性能模式变色: 办公浅蓝 / 游戏橙 / 狂暴红</summary>
    private void ApplyModeTheme(PerformanceMode mode)
    {
        if (HeroIconBorder == null) return;

        LinearGradientBrush brush;
        Color glow;
        Color chip;
        string name;
        PackIconKind icon;

        switch (mode)
        {
            case PerformanceMode.Quiet:
                brush = new LinearGradientBrush(Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0x02, 0x88, 0xD1), 45);
                glow = Color.FromRgb(0x4F, 0xC3, 0xF7);
                chip = Color.FromRgb(0x4F, 0xC3, 0xF7);
                name = "办公模式";
                icon = PackIconKind.Briefcase;
                break;
            case PerformanceMode.Turbo:
                brush = new LinearGradientBrush(Color.FromRgb(0xFF, 0x4D, 0x63), Color.FromRgb(0xE8, 0x28, 0x4A), 45);
                glow = Color.FromRgb(0xFF, 0x3D, 0x5A);
                chip = Color.FromRgb(0xFF, 0x4D, 0x63);
                name = "狂暴模式";
                icon = PackIconKind.Fire;
                break;
            case PerformanceMode.Balanced:
                brush = new LinearGradientBrush(Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0xF5, 0x7C, 0x00), 45);
                glow = Color.FromRgb(0xFF, 0x98, 0x00);
                chip = Color.FromRgb(0xFF, 0xB7, 0x4D);
                name = "游戏模式";
                icon = PackIconKind.Gamepad;
                break;
            default:
                // Unknown: 本机没有可用的 EC, 不能假装成某个真实档位。灰色闪电 + 未知模式。
                brush = new LinearGradientBrush(Color.FromRgb(0x6B, 0x72, 0x80), Color.FromRgb(0x3F, 0x44, 0x4D), 45);
                glow = Color.FromRgb(0x6B, 0x72, 0x80);
                chip = Color.FromRgb(0x9A, 0xA3, 0xAF);
                name = "未知模式";
                icon = PackIconKind.LightningBolt;
                break;
        }

        HeroIconBorder.Background = brush;
        if (HeroIconGlow != null) HeroIconGlow.Color = glow;
        if (HeroModeText != null)
        {
            HeroModeText.Text = name;
            HeroModeText.Foreground = new SolidColorBrush(chip);
        }
        if (HeroModeIcon != null)
        {
            HeroModeIcon.Kind = icon;
            HeroModeIcon.Foreground = new SolidColorBrush(chip);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _updateTimer.Stop();
        _saveDebounce.Stop();
        _audioTimer.Stop();
        // 自检定时器也要停: 否则关窗过程中它可能又触发一次 EC 下发
        _configWatch.Stop();
        SaveCurrentState();
        RemoveKeyboardHook();

        _settingsService.UpdateSettings(s =>
        {
            // 保存"还原后"的尺寸: 最大化时 Width/Height 是铺满屏幕的尺寸, 直接存会导致
            // 下次以"最大化尺寸的普通窗口"启动(盖住任务栏且不是最大化语义)。
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            // 窗口被隐藏/最小化到托盘等状态下可能读到 NaN; System.Text.Json 遇到 NaN 会抛异常,
            // 导致"整份设置"保存失败(键盘灯效等一起丢)。
            if (double.IsFinite(bounds.Left)) s.Window.Left = bounds.Left;
            if (double.IsFinite(bounds.Top)) s.Window.Top = bounds.Top;
            if (double.IsFinite(bounds.Width) && bounds.Width >= 1) s.Window.Width = bounds.Width;
            if (double.IsFinite(bounds.Height) && bounds.Height >= 1) s.Window.Height = bounds.Height;
            s.Window.Maximized = WindowState == WindowState.Maximized;
        });
        try { _stressService.Dispose(); } catch { }
        try { _brightness.Dispose(); } catch { }
        try { _audio.Dispose(); } catch { }
        // 先停灯效(它持有多只后台定时器, 会用到 Wmi), 再释放共享的 ControlService/Wmi
        _lightingEffects?.Dispose();
        _controlService.Dispose();
        _trayService.Dispose();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _nowPlayingCounter++;
            // 播放器信息(灯光页显示当前曲目/换歌重置节拍模型)也按 2 秒轮询, 不做"页面不可见就停"
            if (_nowPlayingCounter % 2 == 0) PollNowPlaying();

            // 全部读数每秒刷新, 与窗口/分区是否可见无关: 界面上任何一处数据都不允许停在旧值。
            CpuTempValue.Text = _controlService.CpuTemp.ToString("F0") + "°C";
            GpuTempValue.Text = _controlService.GpuTemp.ToString("F0") + "°C";
            CpuPowerValue.Text = (_controlService.CpuPower ?? 0).ToString("F1") + "W";
            GpuPowerValue.Text = (_controlService.GpuPower ?? 0).ToString("F1") + "W";
            BatteryValue.Text = (_controlService.BatteryLevel ?? 0).ToString("F0") + "%";
            FanRpmValue.Text = $"CPU: {_controlService.CpuFanRpm} / GPU: {_controlService.GpuFanRpm} RPM";
            if (FanDetailValue != null)
                FanDetailValue.Text = $"CPU {_controlService.CpuFanRpm} · GPU {_controlService.GpuFanRpm} RPM";

            // 详细文本(设备详情/每核表)跟着 LHM 的 2 秒采集节奏走: 数据本身 2 秒才变一次,
            // 更频繁地重新拼字符串不会让信息更新, 只会多产生垃圾。
            if (_controlService.LatestHardwareData is { } hwData && (++_detailTick & 1) == 0)
                UpdateHardwareDetailLabels(hwData);

            UpdateTrayTelemetry();
            PollQuickControls();

            // 电池循环/分辨率/网卡这类"会变但变化慢"的信息每 30 秒重读一次(WMI 放后台)
            if (++_sysInfoTick >= 30)
            {
                _sysInfoTick = 0;
                _ = Task.Run(() =>
                {
                    try { _sysInfo.RefreshDynamic(); } catch { }
                    PostUi(UpdateSystemInfoLabels);
                });
            }

            // 音乐律动期间每 5 秒确认默认播放设备没被换掉(插耳机/切蓝牙后回环会一直静音)
            if (++_audioDeviceTick >= 5)
            {
                _audioDeviceTick = 0;
                if (_lightingEffects?.CurrentEffect == LightingEffect.Music) _lightingEffects.Audio.EnsureDefaultDevice();
            }

            // EC 连接健康度: 掉线自动降级为只读, 恢复后自动重探并重新启用, 不需要重启程序
            if (!_isDemoMode && ++_ecHealthTick >= 10)
            {
                _ecHealthTick = 0;
                bool healthy = _controlService.EcHealthy;
                if (_ecUsable && !healthy)
                {
                    _ecUsable = false;
                    ApplyCapabilities(new FeatureSupport());
                    ShowSnackbar("EC 连接丢失, 控制项已禁用, 后台会自动重连", true);
                }
                else if (!_ecUsable && healthy)
                {
                    _ecUsable = true;
                    var support = CapabilityStore.Load() ?? new FeatureSupport();
                    ApplyCapabilities(support);
                    ShowSnackbar("EC 已恢复连接, 控制功能已启用");
                }
            }

            StressTempValue.Text = _controlService.CpuTemp.ToString("F0") + "°C";
            StressPowerValue.Text = (_controlService.CpuPower ?? 0).ToString("F1") + "W";
            StressRpmValue.Text = $"{_controlService.CpuFanRpm} RPM";

            if (_stressService.IsRunning)
            {
                MemoryMetricText.Text = _stressService.MemoryMetric;
                DiskMetricText.Text = _stressService.DiskMetric;
                GpuMetricText.Text = _stressService.GpuMetric;

                // 每秒采样一次, 用于结束后的最小/平均/最大统计
                float t = _controlService.CpuTemp;
                float pw = _controlService.CpuPower ?? 0;
                int fan = _controlService.CpuFanRpm;
                _statCount++;
                _statTempMin = Math.Min(_statTempMin, t);
                _statTempMax = Math.Max(_statTempMax, t);
                _statTempSum += t;
                _statPowerMin = Math.Min(_statPowerMin, pw);
                _statPowerMax = Math.Max(_statPowerMax, pw);
                _statPowerSum += pw;
                _statFanMin = Math.Min(_statFanMin, fan);
                _statFanMax = Math.Max(_statFanMax, fan);
                _statFanSum += fan;
            }

            // EC 实时状态面板: 同样不受可见性影响, 每秒跟着 ControlService 的最新读值刷新
            EcPerfModeValue.Text = _ecUsable ? _controlService.EcPerfModeText : "未知";
            EcGpuModeValue.Text = _controlService.EcGpuModeText;
            EcAcTypeValue.Text = _controlService.EcAcTypeText;
            EcCpuTempValue.Text = _controlService.EcCpuTemp > 0 ? _controlService.EcCpuTemp + "°C" : "--";
            EcMaxFanValue.Text = _controlService.EcMaxFanOn ? "开启" : "关闭";
            EcMaxFanValue.Foreground = _controlService.EcMaxFanOn
                ? System.Windows.Media.Brushes.OrangeRed
                : System.Windows.Media.Brushes.Gray;
            EcKbBrightnessValue.Text = _controlService.EcKbBrightness >= 0 ? _controlService.EcKbBrightness.ToString() : "--";
            try
            {
                var hex = _controlService.EcKbColorHex;
                if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") && hex.Length == 7)
                {
                    // 颜色没变就复用同一支冻结画刷: 这里每秒都会跑到, 每次新建画刷 +
                    // ColorConverter 解析是空闲时最主要的分配来源。
                    if (hex != _lastSwatchHex)
                    {
                        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                        brush.Freeze();
                        _swatchBrush = brush;
                        _lastSwatchHex = hex;
                    }
                    if (_swatchBrush != null) EcKbColorSwatch.Background = _swatchBrush;
                }
            }
            catch { }

            UpdateConnectionStatus();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "UpdateTimer_Tick error");
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private UIElement? _currentZone;

    private void ZoneNav_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间会提前触发(此时后续 Zone 尚未注册), 用字段直取避免 FindName 竞态
        if (ZoneHome == null || ZonePerf == null || ZoneLight == null || ZoneStress == null) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        UIElement target = tag switch
        {
            "0" => ZoneHome,
            "1" => ZonePerf,
            "2" => ZoneLight,
            "3" => ZoneStress,
            _ => ZoneHome,
        };
        SwitchZone(target);
    }

    private void SwitchZone(UIElement target)
    {
        if (_currentZone == target) return;
        if (_currentZone != null) _currentZone.Visibility = Visibility.Collapsed;
        target.Visibility = Visibility.Visible;
        _currentZone = target;

        // 回到首页时立即补一次文本, 避免等待下一个定时器周期才刷新
        if (target == ZoneHome)
        {
            UpdateSystemInfoLabels();
            if (_controlService?.LatestHardwareData is { } hw)
                UpdateHardwareDetailLabels(hw);
        }
    }

    #region 压力测试

    private void StressButton_Click(object sender, RoutedEventArgs e)
    {
        // 压测不依赖 EC: CPU/内存/磁盘在台式机上同样可用, 温度/功耗由 LibreHardwareMonitor 读。
        // 只有 GPU 探测需要 nvidia-smi, 由 SetupStressAdaptiveAsync 单独禁用。
        if (_stressService.IsRunning)
        {
            _stressService.Stop();
            ShowSnackbar("压力测试已手动停止");
            return;
        }

        StressTarget targets = StressTarget.None;
        if (TargetCpu.IsChecked == true) targets |= StressTarget.Cpu;
        if (TargetMemory.IsChecked == true) targets |= StressTarget.Memory;
        if (TargetDisk.IsChecked == true) targets |= StressTarget.Disk;
        if (TargetGpu.IsChecked == true) targets |= StressTarget.Gpu;

        if (targets == StressTarget.None)
        {
            ShowSnackbar("请至少勾选一个测试目标", true);
            return;
        }

        int seconds = Dur30.IsChecked == true ? 30
            : Dur60.IsChecked == true ? 60
            : Dur120.IsChecked == true ? 120 : 300;

        _stressTotalSeconds = seconds;
        ResetStressStats();
        if (_stressService.Start(targets, seconds))
        {
            ShowSnackbar($"压力测试启动: {DescribeTargets(targets)} · {seconds}秒后自动停止");
        }
    }

    /// <summary>
    /// 压测参数按本机配置自适应: CPU 用全部逻辑处理器, 内存按可用内存的 40%,
    /// 磁盘按临时目录所在盘剩余空间的 10%; 没有 nvidia-smi 时禁用 GPU 探测。
    /// </summary>
    private async Task SetupStressAdaptiveAsync()
    {
        try
        {
            int threads = Math.Max(1, Environment.ProcessorCount);
            int memMb = StressTestService.SuggestMemoryMb();
            int memThreads = StressTestService.SuggestMemoryWorkers(memMb);
            int diskMb = StressTestService.SuggestDiskMb();

            if (StressAdaptiveText != null)
                StressAdaptiveText.Text =
                    $"自适应：CPU {threads} 线程 · 内存 {memMb / 1024.0:F1} GB（{memThreads} 线程）· 磁盘 {diskMb} MB";
            if (TargetCpu != null)
                TargetCpu.ToolTip = $"{threads} 个逻辑处理器全部满载";
            if (TargetMemory != null)
                TargetMemory.ToolTip = $"预计分配 {memMb} MB（物理内存 35%、可用内存 60%，留 2GB，上限 16GB；{memThreads} 个写线程）";
            if (TargetDisk != null)
                TargetDisk.ToolTip = $"临时文件约 {diskMb} MB（剩余空间 10%，上限 4GB，留 2GB）";

            bool gpuOk = await Task.Run(StressTestService.NvidiaSmiAvailable);
            if (TargetGpu != null)
            {
                TargetGpu.IsEnabled = gpuOk;
                if (!gpuOk)
                {
                    TargetGpu.IsChecked = false;
                    TargetGpu.ToolTip = "未检测到 NVIDIA GPU / 驱动，GPU 探测已禁用";
                }
                else
                {
                    TargetGpu.ToolTip = "通过 nvidia-smi 读取独显温度/功耗/利用率（仅状态探测）";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "压测自适应参数初始化失败");
        }
    }

    // 模式单选是"单向显示 + 点击下发": 程序化回显不会写 EC, 也避免单选组首次显示时
    // 因没有选中项而自动勾选最后一项, 把用户的档位悄悄改掉。
    private void PerfMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int idx))
            _viewModel.SelectedModeIndex = idx;
    }

    private void GpuMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int idx))
            _viewModel.SelectedGpuModeIndex = idx;
    }

    private void ResetStressStats()
    {
        _statTempMin = float.MaxValue; _statTempMax = float.MinValue; _statTempSum = 0;
        _statPowerMin = float.MaxValue; _statPowerMax = float.MinValue; _statPowerSum = 0;
        _statFanMin = int.MaxValue; _statFanMax = int.MinValue; _statFanSum = 0;
        _statCount = 0;
    }

    private void ShowStressResults()
    {
        if (_statCount == 0 || StressResultCard == null) return;

        StressResultCard.Visibility = Visibility.Visible;
        StressResultTime.Text = $"{DateTime.Now:HH:mm:ss} · 采样 {_statCount} 秒";

        ResTempMin.Text = $"{_statTempMin:F0}°C";
        ResTempAvg.Text = $"{_statTempSum / _statCount:F1}°C";
        ResTempMax.Text = $"{_statTempMax:F0}°C";

        ResPowerMin.Text = $"{_statPowerMin:F1}W";
        ResPowerAvg.Text = $"{_statPowerSum / _statCount:F1}W";
        ResPowerMax.Text = $"{_statPowerMax:F1}W";

        ResFanMin.Text = $"{_statFanMin}";
        ResFanAvg.Text = $"{(double)_statFanSum / _statCount:F0}";
        ResFanMax.Text = $"{_statFanMax}";
    }

    private static string DescribeTargets(StressTarget t)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (t.HasFlag(StressTarget.Cpu)) parts.Add("CPU");
        if (t.HasFlag(StressTarget.Memory)) parts.Add("内存");
        if (t.HasFlag(StressTarget.Disk)) parts.Add("硬盘");
        if (t.HasFlag(StressTarget.Gpu)) parts.Add("GPU");
        return string.Join(" + ", parts);
    }

    private void UpdateStressUi()
    {
        if (StressButton == null || StressButtonText == null) return;

        if (_stressService.IsRunning)
        {
            int remain = _stressService.Remaining;
            StressButtonText.Text = "停止测试";
            StressButtonIcon.Kind = PackIconKind.Stop;
            StressButton.Background = BrushStressRun;
            StressRemainText.Text = $"运行中 · 剩余 {remain}s";
            StressRemainText.Foreground = BrushStressRunText;

            double frac = _stressTotalSeconds > 0
                ? Math.Clamp((_stressTotalSeconds - remain) / (double)_stressTotalSeconds, 0, 1)
                : 0;
            double trackW = StressProgressTrack?.ActualWidth ?? 0;
            if (trackW > 0) StressProgress.Width = trackW * frac;
        }
        else
        {
            StressButtonText.Text = "开始压力测试";
            StressButtonIcon.Kind = PackIconKind.Play;
            StressButton.Background = BrushStressIdle;
            StressRemainText.Text = _stressService.StatusText == "已停止" ? "已停止" : "待机";
            StressRemainText.Foreground = BrushStressIdleText;
            StressProgress.Width = 0;

            MemoryMetricText.Text = "--";
            DiskMetricText.Text = "--";
            GpuMetricText.Text = "--";
        }
    }

    #endregion

    #region 性能增强(系统级调优)

    /// <summary>
    /// 控制台子进程(powercfg 等)用的是 OEM 代码页(简体中文 = 936), 不是 UTF-8。
    /// .NET 8 里 Encoding.Default 等于 UTF-8, 沿用它会把中文解成乱码, 于是所有依赖文本匹配
    /// 的回读都会静默失败 —— 曾经因此把 ExtremeModeArmed 清成 false。
    /// </summary>
    private static readonly Encoding ConsoleOemEncoding = ResolveOemEncoding();

    private static Encoding ResolveOemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static (int code, string output) RunCommand(string fileName, string arguments, int timeoutMs = 8000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleOemEncoding,
                StandardErrorEncoding = ConsoleOemEncoding,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "启动失败");

            // 先异步读、再等退出: 反过来写的话超时形同虚设 —— 子进程卡住时 ReadToEnd 会永久
            // 阻塞调用线程(powercfg 卡住就会冻住 UI)。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); p.WaitForExit(1000); } catch { }
                Observe(outTask);
                Observe(errTask);
                return (-1, $"超时({timeoutMs}ms)");
            }

            string outp = outTask.GetAwaiter().GetResult();
            string errp = errTask.GetAwaiter().GetResult();

            // stderr 只写日志, 不拼进返回值: nvidia-smi 的 WARNING 混进输出会让
            // int.TryParse 失败, 把锁频成功误判成失败; powercfg 的本地化错误同理。
            if (!string.IsNullOrWhiteSpace(errp))
                Logger.Debug($"命令 stderr: {fileName} -> {errp.Trim()}");
            return (p.ExitCode, outp.Trim());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"RunCommand失败: {fileName} {arguments}");
            return (-1, ex.Message);
        }
    }

    /// <summary>观察被丢弃的读取任务, 避免超时路径产生未观察的 Task 异常</summary>
    private static void Observe(Task<string> task)
        => _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private static readonly Guid HighPerfScheme = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>切换电源方案。方案 GUID 各机器不同, 所以按名称找而不是硬编码。</summary>
    /// <param name="highPerf">true=高性能, false=回到平衡</param>
    private bool ApplyPowerScheme(bool highPerf)
    {
        Guid? target = null;

        if (highPerf)
        {
            // 高性能方案 GUID 在所有中文/英文 Win10/11 上都是这个
            target = HighPerfScheme;
        }
        else
        {
            var (codeL, output) = RunCommand("powercfg", "/list");
            if (codeL == 0)
            {
                foreach (Match m in Regex.Matches(output, @"([0-9a-fA-F\-]{36})\s*(?:\*?)\s*(.+)"))
                {
                    var name = m.Groups[2].Value.Trim();
                    if (name.Contains("平衡") || name.Contains("Balanced", StringComparison.OrdinalIgnoreCase))
                    {
                        target = Guid.Parse(m.Groups[1].Value);
                        break;
                    }
                }
            }
        }

        if (target == null)
        {
            Logger.Warn($"未找到目标电源方案 (highPerf={highPerf})");
            return false;
        }

        var (code, _) = RunCommand("powercfg", $"/setactive {target.Value:D}");
        Logger.Info($"电源方案切换 -> {(highPerf ? "高性能" : "平衡")}: code={code}");
        return code == 0;
    }

    // 本机"最大处理器状态"设置GUID为厂商自定义值, 用powercfg别名保证跨机器可用
    private const string MaxProcStateAlias = "PROCTHROTTLEMAX";

    private int ApplyMaxProcessorState(int percent)
    {
        RunCommand("powercfg", $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR {MaxProcStateAlias} {percent}");
        RunCommand("powercfg", $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR {MaxProcStateAlias} {percent}");
        RunCommand("powercfg", "/setactive SCHEME_CURRENT");
        int back = QueryProcSetting("PROCTHROTTLEMAX") ?? -1;
        Logger.Info($"最大处理器状态设为 {percent}%, 回读 {back}");
        return back;
    }

    /// <summary>回读当前电源方案的处理器设置实际值(验证生效)</summary>
    /// <summary>
    /// 回读 powercfg 里某个处理器设置的实际值。依赖 RunCommand 用 OEM 代码页解码, 否则中文行
    /// ("当前交流电源设置索引: 0x…")会是乱码, 这里任何匹配都会静默失败并返回 null。
    /// </summary>
    private int? QueryProcSetting(string alias)
    {
        var (code, output) = RunCommand("powercfg", "/query SCHEME_CURRENT SUB_PROCESSOR");
        if (code != 0) return null;

        bool inSec = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains(alias))
            {
                inSec = true;
                continue;
            }
            if (!inSec) continue;

            // 走到下一个设置块就停, 防止把别人的交流值当成本项的回读
            if (line.Contains("电源设置 GUID") || line.Contains("Power Setting GUID")) break;

            bool isAcIndexLine = (line.Contains("索引") || line.Contains("Index", StringComparison.OrdinalIgnoreCase))
                && (line.Contains("交流") || line.Contains("AC", StringComparison.OrdinalIgnoreCase));
            if (isAcIndexLine)
            {
                var m = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                if (m.Success) return Convert.ToInt32(m.Groups[1].Value, 16);
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static readonly Random PresetRng = new();

    /// <summary>动态随机: false=单色随机, true=渐变随机(跟随用户最后点的那一个)</summary>
    private bool _randomGradientMode;

    private void RandomColor_Click(object sender, RoutedEventArgs e) => ApplyRandomColors(gradient: false);
    private void RandomGradient_Click(object sender, RoutedEventArgs e) => ApplyRandomColors(gradient: true);

    /// <summary>
    /// 生成并应用一组随机颜色(点一下生效一次)。
    /// 若「动态」开着, 则把随后的平滑漂移也切换到对应模式 —— 真正连续的变化由
    /// LightingEffects.StartRandomDrift 负责(12 秒一段的缓入缓出), 这里不自己跳动。
    /// </summary>
    private void ApplyRandomColors(bool gradient)
    {
        _randomGradientMode = gradient;
        string label;
        List<(byte r, byte g, byte b)> generated;

        if (gradient)
        {
            int count = PresetRng.Next(2, 4);
            double baseHue = PresetRng.Next(360);
            double spread = PresetRng.Next(100, 201);

            generated = new List<(byte r, byte g, byte b)>();
            for (int i = 0; i < count; i++)
            {
                double h = (baseHue + spread * i / Math.Max(1, count - 1) + PresetRng.Next(-15, 16) + 720) % 360;
                var c = HsvToRgb(h, 0.80 + 0.20 * PresetRng.NextDouble(), 0.85 + 0.15 * PresetRng.NextDouble());
                generated.Add((c.R, c.G, c.B));
            }

            SetPickerFromRgb(generated[0].r, generated[0].g, generated[0].b, apply: false);
            UpdateSwatchPrimary(generated[0].r, generated[0].g, generated[0].b);
            UpdateSwatchSecondary(generated[1].r, generated[1].g, generated[1].b);
            label = $"随机渐变 {generated.Count} 色 " + string.Join(" → ", generated.Select(c => $"#{c.r:X2}{c.g:X2}{c.b:X2}"));
        }
        else
        {
            double hue = PresetRng.Next(360);
            double sat = 0.80 + 0.20 * PresetRng.NextDouble();
            double val = 0.85 + 0.15 * PresetRng.NextDouble();

            var c1 = HsvToRgb(hue, sat, val);
            var c2 = HsvToRgb(hue, sat, val * 0.55);

            generated = new List<(byte r, byte g, byte b)> { (c1.R, c1.G, c1.B), (c2.R, c2.G, c2.B) };
            UpdateSwatchPrimary(c1.R, c1.G, c1.B);
            UpdateSwatchSecondary(c2.R, c2.G, c2.B);
            SetPickerFromRgb(c1.R, c1.G, c1.B, apply: false);
            label = $"随机单色 #{c1.R:X2}{c1.G:X2}{c1.B:X2}";
        }

        ApplyRandomPalette(label, gradient ? LightingEffect.GradientCycle : LightingEffect.Static);

        // 动态开着时, 让漂移的"第一段"就淡入到这组颜色, 而不是立刻又随机成别的
        // (否则用户会觉得刚点的颜色没生效); 动态关着就是一次 1.2 秒淡入。
        if (RandomDynamicToggle?.IsChecked == true)
            _lightingEffects?.StartRandomDrift(gradient, generated);
        else
            _lightingEffects?.MorphPaletteTo(generated);

        RequestSave();
    }

    private void RandomDynamic_Changed(object sender, RoutedEventArgs e)
    {
        bool on = RandomDynamicToggle?.IsChecked == true;
        if (on) _lightingEffects?.StartRandomDrift(_randomGradientMode);
        else _lightingEffects?.StopRandomDrift();

        if (!IsLoaded) return;
        _settingsService.UpdateSettings(s => s.Keyboard.RandomDynamic = on);
        ShowSnackbar(on
            ? $"动态随机已开启: 颜色会平滑过渡(约 12 秒一段), 当前灯效不变 · {(_randomGradientMode ? "渐变" : "单色")}"
            : "动态随机已关闭: 颜色保持当前");
        Logger.Info($"动态随机: {on}, 模式={(_randomGradientMode ? "渐变" : "单色")}");
    }

    /// <summary>
    /// 随机颜色只换调色板, 不切换用户当前正在用的灯效。
    /// 仅当当前是"关闭/静态"(没有动效)时, 才切到 fallback, 否则改完也看不出变化。
    /// </summary>
    private void ApplyRandomPalette(string label, LightingEffect idleFallback)
    {
        var cur = _lightingEffects?.CurrentEffect ?? LightingEffect.Static;
        if (cur is LightingEffect.Off or LightingEffect.Static)
        {
            SetEffect(idleFallback, label);
            return;
        }

        // 保持当前效果: 立刻推一帧新颜色, 让改动马上可见(不等下一个动画周期)
        var (r, g, b) = _lightingEffects?.CurrentColor ?? ((byte)0, (byte)0, (byte)0);
        _lightingEffects?.FlashColor(r, g, b);
        if (CurrentEffectLabel != null)
            CurrentEffectLabel.Text = "当前: " + EffectDisplayName(cur);
        ShowSnackbar($"{label} · 已换色，当前灯效保持「{EffectDisplayName(cur)}」");
    }

    /// <summary>
    /// 诊断 NVIDIA 关键设备的驱动绑定状态。本机实测有两个设备会"静默失去驱动":
    /// 独显本体(nvidia-smi 直接 Driver Not Loaded)和 NVPCF Dynamic Boost 控制器(GPU 死卡 80W 基础 TGP)。
    /// 两者系统都不报错(Status=OK, ProblemCode=0), 只有 Class 为空、无 Service, 所以必须主动查。
    /// </summary>
    private async void GpuDiagnose_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Primitives.ButtonBase;
        if (btn != null) btn.IsEnabled = false;
        if (GpuDriverStatusText != null) GpuDriverStatusText.Text = "诊断中…";

        try
        {
            var (smi, smiOk, devices) = await Task.Run(() =>
            {
                var (code, output) = RunCommand("nvidia-smi",
                    "--query-gpu=name,driver_version,clocks.max.gr,power.default_limit,power.max_limit --format=csv,noheader", 10000);
                bool ok = code == 0 && output.Contains(',');
                return (smi: ok ? output.Trim() : Truncate(output, 120),
                        smiOk: ok,
                        devices: Core.Hardware.NvidiaGpuDriver.FindNvidiaDevices());
            });

            if (devices.Count == 0)
            {
                SetGpuDriverStatus("未找到任何 NVIDIA 设备: 驱动库里没有匹配的 INF, 需要重新安装 GeForce 驱动", false);
                return;
            }

            var unbound = devices.Where(d => !d.DriverBound).ToList();
            string detail = string.Join(" · ", devices.Select(d =>
                $"{d.Role}: {(d.DriverBound ? $"已绑定 {d.Service} {d.DriverVersion ?? ""}" : "未绑定驱动!")}"));

            if (unbound.Count > 0)
            {
                string impact = string.Join("; ", unbound.Select(d => d.Role == "独显"
                    ? "独显完全不可用(nvidia-smi/锁频/跑分全废)"
                    : "Dynamic Boost 失效(GPU 只能用基础 TGP, 跑分明显偏低)"));
                SetGpuDriverStatus($"{detail} — 影响: {impact} · 点「重新绑定驱动」可从系统驱动库修复", false);
                ShowSnackbar($"发现 {unbound.Count} 个 NVIDIA 设备未绑定驱动, 可一键修复", true);
                return;
            }

            if (smiOk)
            {
                SetGpuDriverStatus($"全部正常 · {detail} · nvidia-smi: {smi}", true);
                ShowSnackbar("独显与 Dynamic Boost 驱动均已绑定, nvidia-smi 可通信");
            }
            else
            {
                SetGpuDriverStatus($"{detail} · 但 nvidia-smi 不可用: {Core.Hardware.NvidiaGpuDriver.ClassifySmiFailure(smi)} · {smi}", false);
                ShowSnackbar("驱动已绑定但 nvidia-smi 不通, 可尝试强制重装驱动", true);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NVIDIA 设备诊断失败");
            SetGpuDriverStatus("诊断异常: " + Truncate(ex.Message, 80), false);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private void SetGpuDriverStatus(string text, bool ok)
    {
        if (GpuDriverStatusText == null) return;
        GpuDriverStatusText.Text = text;
        GpuDriverStatusText.Foreground = new SolidColorBrush(ok
            ? Color.FromRgb(0x69, 0xF0, 0xAE)
            : Color.FromRgb(0xFF, 0x80, 0xAB));
    }

    /// <summary>
    /// 从系统驱动库(DriverStore)匹配 INF 并强制绑定所有 NVIDIA 关键设备。
    /// 未绑定时是修复, 已绑定时是强制重装(用于修复驱动损坏的情况)。
    /// </summary>
    private async void GpuDriverRepair_Click(object sender, RoutedEventArgs e)
    {
        var devices = await Task.Run(Core.Hardware.NvidiaGpuDriver.FindNvidiaDevices);
        if (devices.Count == 0)
        {
            SetGpuDriverStatus("无法修复: 没找到 NVIDIA 设备(驱动库里没有匹配的 INF), 请先安装 GeForce 驱动", false);
            ShowSnackbar("没找到 NVIDIA 设备或匹配的 INF, 无法绑定", true);
            return;
        }

        var targets = devices.Where(d => !d.DriverBound).ToList();
        bool forceAll = targets.Count == 0;
        if (forceAll) targets = devices.ToList();

        string list = string.Join("\n", targets.Select(d =>
            $"  · {d.Role}  {d.HardwareId}\n    {(d.DriverBound ? "强制重装" : "绑定")} ← {d.InfPath}"));

        var confirm = MessageBox.Show(
            (forceAll ? "所有设备驱动都已绑定, 将强制重新安装(用于修复驱动损坏):\n"
                      : $"发现 {targets.Count} 个设备未绑定驱动, 将从系统驱动库绑定:\n") +
            "\n" + list + "\n\n" +
            "失败不会影响显示输出, 并可通过设备管理器卸载驱动回滚。\n\n继续?",
            "重新绑定 NVIDIA 驱动", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var btn = sender as System.Windows.Controls.Primitives.ButtonBase;
        if (btn != null) btn.IsEnabled = false;
        SetGpuDriverStatus($"正在绑定 {targets.Count} 个设备的驱动 (可能需要一两分钟)…", false);

        try
        {
            var results = await Task.Run(() =>
            {
                var list2 = new List<string>();
                bool anyReboot = false;
                foreach (var d in targets)
                {
                    bool ok = Core.Hardware.NvidiaGpuDriver.BindDriver(d, out bool reboot, out string msg);
                    anyReboot |= reboot;
                    list2.Add($"{d.Role}: {(ok ? "成功" : "失败")} ({msg})");
                    Logger.Info($"驱动绑定 {d.Role} hwid={d.HardwareId} ok={ok} {msg}");
                }
                return (lines: list2, reboot: anyReboot);
            });

            await Task.Delay(4000);
            var (code, output) = await Task.Run(() => RunCommand("nvidia-smi",
                "--query-gpu=name,driver_version,power.default_limit,power.max_limit --format=csv,noheader", 10000));
            bool smiOk = code == 0 && output.Contains(',');

            string summary = string.Join(" · ", results.lines) +
                             (smiOk ? $" · nvidia-smi: {output.Trim()}" : $" · nvidia-smi 仍不可用: {Truncate(output, 60)}") +
                             (results.reboot ? " · 建议重启" : "");
            bool allOk = results.lines.All(l => l.Contains("成功")) && smiOk;
            SetGpuDriverStatus(summary, allOk);
            ShowSnackbar(allOk
                ? "NVIDIA 驱动绑定完成, nvidia-smi 已恢复, GPU 锁频与 Dynamic Boost 可用"
                : "驱动绑定已执行, 但仍有项目未通过, 详见状态行", !allOk);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NVIDIA 驱动绑定异常");
            SetGpuDriverStatus("绑定异常: " + Truncate(ex.Message, 80), false);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 持续睿频 = 最小处理器状态拉到 100%, 让核心不 park、不待在低频等着爬升。
    /// 实测收益(8 次 4 秒爆发 + 6 秒空闲, 32 线程): 5% → 1,014,261 MFLOP, 100% → 1,076,631 MFLOP,
    /// **+6.15%**, 且每个爆发从"先掉再爬"变成完全平 (27.07–27.18 GMFLOP/s)。
    /// 稳态测量看不出任何差别(两者稳态吞吐相同), 只有短促爆发才暴露这个爬升损耗。
    /// 注意: 落到鲁大师实测只兑现了约 1%(CPU 分 1,071,680 → 1,081,355), 说明它的 CPU 子项
    /// 比我建模的负载更接近持续型。仍然保留 —— 方向是正的, 只是没有仪器上那么值钱。
    /// 代价是空闲时核心不 park, 更热更费电, 所以「还原」会把它关回去。
    /// </summary>
    private int? SetContinuousBoost(bool on)
    {
        int min = on ? 100 : 5;
        RunCommand("powercfg", $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {min}");
        RunCommand("powercfg", $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {min}");
        RunCommand("powercfg", "/setactive SCHEME_CURRENT");
        var back = QueryProcSetting("PROCTHROTTLEMIN");
        Logger.Info($"持续睿频 -> {(on ? "开" : "关")}, 回读 {back}");
        return back;
    }



    /// <summary>一键超频: 把全部实测有效的极限设置一次下发</summary>
    private async void MaxPerf_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Primitives.ButtonBase;
        if (btn != null) btn.IsEnabled = false;
        try { await ApplyExtremeAsync("由「极限满血」下发"); }
        finally { if (btn != null) btn.IsEnabled = true; }
    }

    /// <summary>
    /// 下发极限配置, 开机时也会自动重放一次(必须重放是因为 nvidia-smi 锁频重启后必然失效)。
    /// 注意: 锁独显最高频只保证 GPU 子项跑满, 对 CPU 睿频没有帮助 —— A/B 实测两种情况下
    /// CPU 都是 4177~4277MHz, 之前看到的 4902MHz 是瞬时尖峰, 不是锁频带来的。
    /// </summary>
    private int _extremeRunning;

    /// <summary>
    /// 自检定时器与用户点击可能同时触发。两套下发交错写 EC(先切档、再写功耗墙的顺序会被打乱),
    /// 所以这里做互斥: 已在下发中就直接跳过本次触发。
    /// </summary>
    private async Task ApplyExtremeAsync(string source)
    {
        if (Interlocked.Exchange(ref _extremeRunning, 1) == 1)
        {
            Logger.Info($"极限档正在下发中, 跳过本次触发({source})");
            return;
        }
        try { await ApplyExtremeCoreAsync(source); }
        finally { Interlocked.Exchange(ref _extremeRunning, 0); }
    }

    private async Task ApplyExtremeCoreAsync(string source)
    {
        if (_controlService == null) return;
        SetMaxPerfStatus("极限满血配置下发中…", false);

        var steps = new List<string>();
        try
        {
            // 先立覆盖值, 切档位时套用到的就是极限功耗而不是档位预设
            _controlService.CpuPowerOverrideWatts = MaxSpl;

            // 以 EC 回读为准, 不能只看 VM 的 SelectedModeIndex —— 档位被别人(或自检补发)之外的
            // 路径改掉时, VM 缓存仍会显示旧的"狂暴", 于是这里会跳过写入, EC 永远切不回来。
            // (实测: 外部把 EC 写到办公档后, 补发流程只读缓存 → 不写 EC → 回读失败 → 武装被清)
            if (_viewModel.SelectedModeIndex != (int)PerformanceMode.Turbo)
                _viewModel.SelectedModeIndex = (int)PerformanceMode.Turbo;

            await Task.Delay(1200);

            var modeNow = await Task.Run(() => _controlService.Ec?.GetPerformanceMode() ?? PerformanceMode.Unknown);
            if (modeNow != PerformanceMode.Turbo)
            {
                Logger.Warn($"VM 未把档位写下去(EC 当前 {modeNow}), 直接补写狂暴档");
                await Task.Run(() => _controlService.SetPerformanceMode(PerformanceMode.Turbo));
                await Task.Delay(600);
                modeNow = await Task.Run(() => _controlService.Ec?.GetPerformanceMode() ?? PerformanceMode.Unknown);
            }
            steps.Add(modeNow == PerformanceMode.Turbo
                ? "狂暴档+满速风扇"
                : $"档位未切到狂暴(当前 {modeNow})");

            bool cpuOk = await Task.Run(() => _controlService.Ec?.SetCpuPowerLimit(MaxSpl, MaxSppt) ?? false);
            steps.Add(cpuOk ? $"功耗墙 {MaxSpl}/{MaxSppt}W 已写入 EC" : "功耗墙写入失败(EC 无应答)");

            // powercfg 调用同步执行在 UI 线程上最坏会冻结窗口数秒(每次超时 8s), 统一丢到后台
            int maxBack = await Task.Run(() => ApplyMaxProcessorState(100));
            steps.Add(maxBack == 100 ? "最大处理器状态 100%" : $"最大处理器状态回读 {maxBack}%");

            int? minBack = await Task.Run(() => SetContinuousBoost(true));
            steps.Add(minBack == 100 ? "持续睿频开启" : $"持续睿频回读 {minBack?.ToString() ?? "失败"}%");

            bool schemeOk = await Task.Run(() => ApplyPowerScheme(true));
            steps.Add(schemeOk ? "高性能电源方案" : "电源方案切换失败");

            bool gpuOk = await LockGpuToMaxAsync();
            steps.Add(gpuOk ? "独显锁最高频" : "独显锁频失败(见驱动状态行)");

            // 用显式布尔判断, 不再靠步骤文本里是否含"失败"——回读得到 99% 或 -1 时
            // 文本里没有"失败"二字, 以前会被误判成全部成功并武装。
            bool procOk = maxBack == 100 && minBack == 100;
            bool allOk = cpuOk && modeNow == PerformanceMode.Turbo && procOk && schemeOk && gpuOk;
            // 显式落盘: 开机重放路径上滑杆还没 IsLoaded, ValueChanged 里的 RequestSave 会被跳过,
            // 不写这行就会一直留着旧值, 下次开机先按旧值写一遍 EC 再被这里覆盖。
            // 失败时清掉覆盖值: 否则关窗时会被写进设置, 下次开机重放一个"没成功武装"的 120W
            if (!cpuOk) _controlService.CpuPowerOverrideWatts = 0;

            _settingsService.UpdateSettings(s =>
            {
                s.ExtremeModeArmed = allOk;
                s.CpuSplWatts = allOk ? MaxSpl : 0;
            });
            SetMaxPerfStatus(string.Join(" · ", steps)
                + (allOk ? " · 已设为开机自动重放 · 跑分前务必退出常驻渲染程序(动态壁纸/桌面精灵/微信/模拟器), 这些比任何超频项都值钱"
                         : " · 有项失败, 先点上方「诊断」看独显驱动状态"),
                allOk);
            ShowSnackbar(allOk ? "极限满血已释放" : "极限满血部分项失败, 详见状态行", !allOk);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "极限满血执行失败");
            SetMaxPerfStatus("执行异常: " + Truncate(ex.Message, 80), false);
        }
    }

    /// <summary>
    /// 极限档实际写入值。255/255 实测过: 短负载吞吐与 120/160 完全一致(506.2 vs 505.8 Mops, 差 0.08%),
    /// 但去掉功耗上限后芯片改由温度墙兜底, 近空载 thermal zone 从 67°C 抬到 78°C。鲁大师是数分钟持续
    /// 负载, 多出来的热量正是掉分的地方, 所以这里用实测等效、温度更低的 120/160。
    /// 滑杆和 EC 写入钳位仍开放到 255, 想自己试上限直接拖到顶即可。
    /// </summary>
    private const int MaxSpl = 120;
    private const int MaxSppt = 160;

    /// <summary>按 nvidia-smi 上报的 clocks.max 锁到最高频(留 600MHz 下限, 避免锁不住)</summary>
    private async Task<bool> LockGpuToMaxAsync()
    {
        int max = await Task.Run(() =>
        {
            var (code, output) = RunCommand("nvidia-smi", "--query-gpu=clocks.max.gr --format=csv,noheader,nounits", 10000);
            return code == 0 && int.TryParse(output.Trim(), out int m) && m > 500 ? m : 0;
        });
        if (max == 0)
        {
            var (code0, out0) = await Task.Run(() => RunCommand("nvidia-smi", "--query-gpu=name --format=csv,noheader", 10000));
            SetGpuDriverStatus("GPU 锁频失败: " + Core.Hardware.NvidiaGpuDriver.ClassifySmiFailure(out0)
                + " — 点上方「诊断」查看驱动绑定状态", false);
            return false;
        }

        int min = Math.Max(300, max - 600);
        var (code, output) = await Task.Run(() => RunCommand("nvidia-smi", $"-lgc {min},{max}", 10000));
        bool ok = code == 0 && output.Contains("clocks set to", StringComparison.OrdinalIgnoreCase);
        if (ok) GpuLockStatusText.Text = $"极限锁频生效: {min}-{max}MHz (驱动已回显确认)";
        else GpuLockStatusText.Text = "极限锁频失败: " + Core.Hardware.NvidiaGpuDriver.ClassifySmiFailure(output);
        return ok;
    }

    /// <summary>撤销极限配置: 解除 GPU 锁频 + 功耗墙回到档位预设</summary>
    private async void MaxPerfReset_Click(object sender, RoutedEventArgs e)
    {
        if (_controlService == null) return;

        // 与「一键超频」共用同一把门闩: 否则 Apply 在 await 期间被插队执行 Reset,
        // 结果会是 UI 显示"已还原"、EC 却仍被 Apply 写回 120/160W 并把 armed 置回 true。
        if (Interlocked.Exchange(ref _extremeRunning, 1) == 1)
        {
            ShowSnackbar("正在执行极限配置, 请稍候再点还原", true);
            return;
        }

        var btn = sender as System.Windows.Controls.Primitives.ButtonBase;
        if (btn != null) btn.IsEnabled = false;

        // 先解除武装再慢慢还原: 中途失败/被关掉, 下次开机也不会又把极限配置写回去
        // 立即落盘: 还原后若进程被强杀/断电, 文件里也不能还留着已武装状态和 120W
        _settingsService.UpdateSettings(s =>
        {
            s.ExtremeModeArmed = false;
            s.CpuSplWatts = 0;
        });

        try
        {
            _controlService.CpuPowerOverrideWatts = 0;
            _controlService.ReapplyCurrentPowerProfile();

            var (code, output) = await Task.Run(() => RunCommand("nvidia-smi", "-rgc", 10000));
            if (code == 0) GpuLockStatusText.Text = "独显: 锁频已解除, 恢复 NVIDIA 默认曲线";
            else GpuLockStatusText.Text = "独显: 解除失败 · " + Core.Hardware.NvidiaGpuDriver.ClassifySmiFailure(output);

            await Task.Run(() => SetContinuousBoost(false));
            await Task.Run(() => ApplyMaxProcessorState(100));
            await Task.Run(() => ApplyPowerScheme(false));

            SetMaxPerfStatus("已还原: 功耗墙跟随档位 · 持续睿频关 · 电源方案回平衡 · GPU "
                + (code == 0 ? "锁频已解除" : "解除失败") + " · 开机不再自动重放", true);
            ShowSnackbar("已还原极限配置 (性能档位本身未改动)");
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
            Interlocked.Exchange(ref _extremeRunning, 0);
        }
    }

    /// <summary>
    /// 跑分体检: 逐项回读机器是否处于最佳跑分状态。只读, 不下发任何设置,
    /// 所以它永远不会改变机器状态, 只回答"现在能不能跑、跑之前该做什么"。
    /// 每一项都来自能回读的通道: EC WMI / powercfg / nvidia-smi / 进程计数器 / DriveInfo。
    /// </summary>
    private async void ReadinessCheck_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Primitives.ButtonBase;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            if (ReadinessText != null) ReadinessText.Text = "体检中…";
            var sb = new System.Text.StringBuilder();
            var problems = new List<string>();

            var st = await Task.Run(() => _controlService?.Ec?.Wmi?.ReadEcStatus());
            if (st is { Valid: true })
            {
                bool perfOk = st.PerfMode == 1;
                bool fanOk = st.MaxFanOn == 1;
                if (!perfOk) problems.Add("EC 不在狂暴档");
                if (!fanOk) problems.Add("满速风扇开关未开");
                sb.AppendLine($"{(perfOk ? "OK" : "!!")} EC 性能档 = {st.PerfModeText} (需 狂暴)");
                sb.AppendLine($"{(fanOk ? "OK" : "!!")} 满速风扇开关 = {(st.MaxFanOn == 1 ? "开" : "关")}");
                sb.AppendLine($"     CPU {st.CpuTemp}°C · 风扇 {st.CpuFanRpm}/{st.GpuFanRpm} RPM");

                if (st.CpuTemp >= 75)
                    problems.Add($"CPU 已 {st.CpuTemp}°C, 建议空载降温 3-5 分钟");
            }
            else
            {
                problems.Add("EC 状态读取失败");
                sb.AppendLine("!! EC 状态读取失败");
            }

            int maxState = QueryProcSetting("PROCTHROTTLEMAX") ?? -1;
            int minState = QueryProcSetting("PROCTHROTTLEMIN") ?? -1;
            if (maxState != 100) problems.Add($"最大处理器状态 {maxState}% 应为 100%");
            if (minState != 100) problems.Add($"持续睿频 {minState}% 应为 100%");
            sb.AppendLine($"{(maxState == 100 ? "OK" : "!!")} 最大处理器状态 = {maxState}%");
            sb.AppendLine($"{(minState == 100 ? "OK" : "!!")} 持续睿频(最小) = {minState}%");

            var (expCode, expOut) = await Task.Run(() => RunCommand("powercfg", "/getactivescheme"));
            var gm = expCode == 0 ? Regex.Match(expOut, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})") : null;
            bool schemeOk = gm is { Success: true } && Guid.TryParse(gm.Groups[1].Value, out var activeScheme) && activeScheme == HighPerfScheme;
            if (!schemeOk) problems.Add("电源方案不是高性能");
            sb.AppendLine($"{(schemeOk ? "OK" : "!!")} 电源方案 = {(schemeOk ? "高性能" : "非高性能")}");

            var (smiCode, smiOut) = await Task.Run(() => RunCommand("nvidia-smi",
                "--query-gpu=clocks.current.graphics,pstate,temperature.gpu,power.draw --format=csv,noheader,nounits", 8000));
            if (smiCode == 0)
            {
                var parts = smiOut.Split(',', StringSplitOptions.TrimEntries);
                bool clockOk = parts.Length > 0 && int.TryParse(parts[0], out var gc) && gc >= 2400;
                if (!clockOk) problems.Add("独显未锁在最高频");
                sb.AppendLine($"{(clockOk ? "OK" : "!!")} 独显 = {smiOut.Trim()}{(clockOk ? "" : "  (应 ≥2400MHz / P0)")}");
            }
            else
            {
                problems.Add("nvidia-smi 读取失败");
                sb.AppendLine("!! nvidia-smi 读取失败");
            }

            var drive = new DriveInfo("C:");
            double freePct = drive.TotalSize > 0 ? drive.AvailableFreeSpace * 100.0 / drive.TotalSize : 0;
            if (freePct < 15) problems.Add($"C 盘剩余 {freePct:F0}%, 建议 >15%");
            sb.AppendLine($"{(freePct >= 15 ? "OK" : "!!")} C 盘剩余 {drive.AvailableFreeSpace / 1073741824.0:F0}GB ({freePct:F0}%)");

            var hogs = await TopCpuConsumersAsync(3);
            if (hogs.Count > 0)
            {
                sb.AppendLine($"!! 后台占用: {string.Join(" · ", hogs)}");
                problems.Add("有后台程序在占用 CPU");
            }
            else
            {
                sb.AppendLine("OK 后台无明显的 CPU 占用进程");
            }

            sb.AppendLine();
            sb.AppendLine(problems.Count == 0
                ? "结论: 可以跑分。"
                : $"结论: 先处理 {problems.Count} 项 → {string.Join("; ", problems)}");

            if (ReadinessText != null) ReadinessText.Text = sb.ToString();
            Logger.Info("跑分体检: " + sb.ToString().Replace(Environment.NewLine, " | "));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "跑分体检失败");
            if (ReadinessText != null) ReadinessText.Text = "体检失败: " + Truncate(ex.Message, 80);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 用两次 TotalProcessorTime 快照求差, 得到"最近 0.8 秒真正在吃 CPU 的进程"。
    /// 不能用累计 CPU 时间排序 —— 那只会列出开机以来跑得最久的进程, 与当前负载无关。
    /// </summary>
    private static async Task<List<string>> TopCpuConsumersAsync(int take, double thresholdPct = 2.0)
    {
        static Dictionary<int, TimeSpan> Snapshot()
        {
            var map = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses())
            {
                try { map[p.Id] = (p.Id, p.TotalProcessorTime).Item2; }
                catch { }
                finally { p.Dispose(); }
            }
            return map;
        }

        return await Task.Run(async () =>
        {
            var first = Snapshot();
            await Task.Delay(800);
            var second = Snapshot();
            int cores = Environment.ProcessorCount;
            var list = new List<(string name, double pct)>();
            foreach (var kv in second)
            {
                if (!first.TryGetValue(kv.Key, out var before)) continue;
                double pct = (kv.Value - before).TotalMilliseconds / 800.0 / cores * 100.0;
                if (pct < thresholdPct) continue;
                try
                {
                    using var p = Process.GetProcessById(kv.Key);
                    list.Add((p.ProcessName, pct));
                }
                catch { }
            }
            return list.OrderByDescending(x => x.pct).Take(take)
                       .Select(x => $"{x.name} {x.pct:F1}%").ToList();
        });
    }

    /// <summary>
    /// 极限档自检: 配置会被别的程序或"还原"悄悄改掉 —— 实测至少发生过两次
    /// (ExtremeModeArmed 被清空、EC 档位被切走)。武装期间每 5 分钟回读一次,
    /// 发现漂移就自动补发, 并把漂移前后的值写进日志。
    /// </summary>
    private async Task VerifyAndHealExtremeAsync()
    {
        if (_controlService == null || !_settingsService.Settings.ExtremeModeArmed) return;
        try
        {
            // 四路回读全部放后台: QueryProcSetting 是 WMI 调用, 桥/WMI 抖动时会在 UI 线程上
            // 卡住整整几秒(原来每 5 分钟就可能冻一次界面)。
            var (perf, fan, min, max) = await Task.Run(() =>
            {
                var w = _controlService.Ec?.Wmi;
                int p = -1, f = -1;
                if (w != null)
                {
                    p = w.GetPerformanceMode();
                    f = w.GetMaxFanSwitch();
                }
                return (p, f, QueryProcSetting("PROCTHROTTLEMIN") ?? -1, QueryProcSetting("PROCTHROTTLEMAX") ?? -1);
            });

            if (perf == 1 && fan == 1 && min == 100 && max == 100) return;

            Logger.Warn($"极限档漂移: perf={perf} maxFan={fan} min={min}% max={max}% → 自动补发");
            await ApplyExtremeAsync("自检发现漂移");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "极限档自检失败");
        }
    }

    private int _lastKbLevel = -1;
    /// <summary>程序化设置 Logo 开关时抑制 Checked/Unchecked 回调, 避免自己触发自己</summary>
    private bool _logoLightSuppress;
    private DateTime _lastTooltipUtc = DateTime.MinValue;
    private DateTime _lastCpuWarnUtc = DateTime.MinValue;
    private DateTime _lastGpuWarnUtc = DateTime.MinValue;

    /// <summary>
    /// 托盘提示与高温告警。窗口收进托盘后这是唯一能看到状态的地方:
    /// 提示文本每 5 秒刷新一次(纯 Win32 调用, 开销可忽略), 温度超阈值时弹一次气泡,
    /// 同类告警 5 分钟内不重复, 避免持续高温时刷屏。
    /// </summary>
    private void UpdateTrayTelemetry()
    {
        if (_trayService == null) return;
        var now = DateTime.UtcNow;

        if ((now - _lastTooltipUtc).TotalSeconds >= 5)
        {
            _lastTooltipUtc = now;
            string mode = _controlService.CurrentPerformanceMode switch
            {
                PerformanceMode.Turbo => "狂暴",
                PerformanceMode.Balanced => "游戏",
                PerformanceMode.Quiet => "办公",
                _ => "未知"
            };
            _trayService.UpdateTooltip(
                $"MEV蛟龙控制台 · {mode} · CPU {_controlService.CpuTemp:F0}°C / GPU {_controlService.GpuTemp:F0}°C");
        }

        if (_controlService.CpuTemp >= 92 && (now - _lastCpuWarnUtc).TotalMinutes >= 5)
        {
            _lastCpuWarnUtc = now;
            _trayService.ShowWarning("CPU 温度过高",
                $"CPU 已达 {_controlService.CpuTemp:F0}°C。建议切到狂暴档(满功耗+满速风扇), 并检查进风口是否被挡住。");
            Logger.Warn($"高温告警: CPU {_controlService.CpuTemp:F0}°C");
        }

        if (_controlService.GpuTemp >= 87 && (now - _lastGpuWarnUtc).TotalMinutes >= 5)
        {
            _lastGpuWarnUtc = now;
            _trayService.ShowWarning("独显温度过高", $"独显已达 {_controlService.GpuTemp:F0}°C。");
            Logger.Warn($"高温告警: GPU {_controlService.GpuTemp:F0}°C");
        }
    }

    private void SetMaxPerfStatus(string text, bool ok)
    {
        if (MaxPerfStatusText == null) return;
        MaxPerfStatusText.Text = "状态: " + text;
        MaxPerfStatusText.Foreground = new SolidColorBrush(ok
            ? Color.FromRgb(0x69, 0xF0, 0xAE)
            : Color.FromRgb(0xFF, 0x80, 0xAB));
    }

    #region 显卡模式验证

    private void UpdateGpuModeStatus()
    {
        if (GpuModeStatusText == null) return;
        var echo = _controlService.Ec?.GetGpuMode();
        GpuModeStatusText.Text = echo switch
        {
            GpuMode.Discrete => "EC 回读: 独显直连 · MUX 物理切换需重启生效",
            GpuMode.Hybrid => "EC 回读: 混合模式 · MUX 物理切换需重启生效",
            _ => "EC 回读: 不可用",
        };
    }

    private void VerifyGpuPhysical_Click(object sender, RoutedEventArgs e)
    {
        var (code, output) = RunCommand("nvidia-smi",
            "--query-gpu=name,clocks.gr,power.draw --format=csv,noheader,nounits", 10000);
        if (code == 0 && output.Contains(","))
        {
            GpuModeStatusText.Text = $"物理状态: 独显供电中 · {output.Trim()}";
            ShowSnackbar("独显物理状态: 供电中, nvidia-smi 可通信");
        }
        else
        {
            // 不能再笼统归因为"混合模式休眠": 本机出现过驱动未绑定导致 nvidia-smi 失效的情况
            var dev = Core.Hardware.NvidiaGpuDriver.FindNvidiaDisplayDevice();
            if (dev != null && !dev.DriverBound)
            {
                GpuModeStatusText.Text = "物理状态: 独显驱动未绑定 (与显卡模式无关) — 请到下方\"独显驱动状态\"点重新绑定";
                ShowSnackbar("独显驱动未绑定, 这才是 nvidia-smi 失效的原因, 可一键重新绑定", true);
            }
            else
            {
                GpuModeStatusText.Text = $"物理状态: 独显休眠(混合模式)或驱动不可达 · {Core.Hardware.NvidiaGpuDriver.ClassifySmiFailure(output)}";
                ShowSnackbar("独显不可通信: 休眠(混合模式)或驱动问题, 可用下方诊断按钮细查", true);
            }
        }
    }

    private void RebootApplyGpu_Click(object sender, RoutedEventArgs e) => RebootWithGpuMode(GpuMode.Discrete);

    private void RebootApplyHybrid_Click(object sender, RoutedEventArgs e) => RebootWithGpuMode(GpuMode.Hybrid);

    private void RebootWithGpuMode(GpuMode target)
    {
        string name = target == GpuMode.Discrete ? "独显直连" : "混合模式";
        var ans = MessageBox.Show(
            $"将把显卡模式写入 EC: {name}, 并在确认后 10 秒重启电脑使 MUX 切换生效。\n" +
            "独显直连下混合模式集显关闭, 耗电增加; 可随时用\"切混合并重启\"切回。\n\n确认继续?",
            "切换显卡模式并重启", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes) return;

        if (!_controlService.SetGpuMode(target))
        {
            ShowSnackbar($"EC 未确认 {name} ({_controlService.EcErrorMessage}), 已中止重启", true);
            return;
        }

        SaveCurrentState();
        RunCommand("shutdown", "/r /t 10 /c \"MEV蛟龙控制台: 10秒后重启以切换显卡模式\"");
        ShowSnackbar($"EC 已确认 {name}, 10 秒后重启生效 (取消: shutdown /a)");
    }

    #endregion

    #region 声音驱动灯效

    private void AudioSource_Changed(object sender, RoutedEventArgs e)
    {
        if (_lightingEffects == null) return;

        var kind = AudioSrcMic.IsChecked == true
            ? Core.Audio.AudioSourceKind.Microphone
            : Core.Audio.AudioSourceKind.SystemLoopback;
        _preferredAudioSource = kind;

        bool ok = _lightingEffects.SetAudioSource(kind);
        if (!ok)
        {
            // 采集不可用时内部模拟节拍兜底(电平表显示模拟能量), 不禁用按钮以便重试
            ShowSnackbar(kind == Core.Audio.AudioSourceKind.Microphone
                ? "麦克风采集失败(设备不存在或被占用), 暂用内部模拟节拍, 可稍后重试"
                : "电脑声音回环采集失败(无播放设备?), 暂用内部模拟节拍, 可稍后重试", true);
        }
        else
        {
            ShowSnackbar(kind == Core.Audio.AudioSourceKind.Microphone
                ? "音乐律动已切换到外界声音(麦克风)"
                : "音乐律动已切换到电脑声音(回环采集)");
        }

        if (_lightingEffects.CurrentEffect != LightingEffect.Music)
            SetEffect(LightingEffect.Music, "已自动切换到音乐律动灯效");

        // 采集失败也要启动电平计时器: 否则电平文本会永久停在"--", 看不出模拟节拍还在跑
        if (_lightingEffects.CurrentEffect == LightingEffect.Music) _audioTimer.Start();
        else _audioTimer.Stop();
        if (AudioLevelText != null)
            AudioLevelText.Text = _lightingEffects.Audio.IsActive ? "电平 0%" : "模拟 0%";
    }

    /// <summary>音乐律动效果启用时确保音频采集已启动(默认电脑声音)</summary>
    private void EnsureAudioCaptureForMusic()
    {
        if (_lightingEffects == null) return;
        if (_lightingEffects.CurrentEffect != LightingEffect.Music) return;
        if (_lightingEffects.Audio.IsActive) return;
        _lightingEffects.SetAudioSource(_preferredAudioSource);
        // 即使采集失败也要把电平计时器跑起来: 它内部有模拟节拍兜底, 界面不会停在"--"
        _audioTimer.Start();
    }


    #endregion



    #endregion

    private void ShowSnackbar(string message, bool isError = false)
    {
        try
        {
            // 错误提示加前缀: Snackbar 没有单独的样式通道, 前缀是唯一能让用户看出"出错了"的办法
            MainSnackbar.MessageQueue?.Enqueue(isError ? "[错误] " + message : message);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShowSnackbar error");
        }
    }

    #region 键盘灯效

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            _lightingEffects?.OnKeyPress();
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private void SetEffect(LightingEffect effect, string label)
    {
        if (_lightingEffects == null) return;
        _lightingEffects.SetEffect(effect);
        HighlightEffectButton(effect);
        if (CurrentEffectLabel != null)
            CurrentEffectLabel.Text = "当前: " + EffectDisplayName(effect);

        if (effect == LightingEffect.Reactive)
            InstallKeyboardHook();
        else
            RemoveKeyboardHook();

        if (effect == LightingEffect.Music)
        {
            EnsureAudioCaptureForMusic();
        }
        else
        {
            _audioTimer.Stop();
            // 离开音乐律动要真的释放回环/麦克风采集: 否则麦克风指示灯常亮, FFT 也一直空转
            _lightingEffects.Audio.Stop();
        }

        ShowSnackbar(label);
        RequestSave();
    }

    private static string EffectDisplayName(LightingEffect effect) => effect switch
    {
        LightingEffect.Off => "关闭",
        LightingEffect.Static => "静态",
        LightingEffect.Breathing => "渐变呼吸",
        LightingEffect.Rainbow => "彩虹",
        LightingEffect.Strobe => "渐变闪烁",
        LightingEffect.Pulse => "渐变脉冲",
        LightingEffect.Random => "真随机",
        LightingEffect.Reactive => "敲击",
        LightingEffect.Wave => "渐变海浪",
        LightingEffect.Flame => "火焰",
        LightingEffect.Aurora => "极光",
        LightingEffect.GradientCycle => "多色渐变循环",
        LightingEffect.Music => "音乐律动",
        LightingEffect.Comet => "彗星",
        _ => effect.ToString(),
    };

    private void HighlightEffectButton(LightingEffect effect)
    {
        if (BtnEffectStatic == null) return;

        var raised = (Style)FindResource("MaterialDesignRaisedButton");
        var flat = (Style)FindResource("MaterialDesignFlatButton");

        void Apply(Button? btn, LightingEffect target)
        {
            if (btn != null) btn.Style = effect == target ? raised : flat;
        }

        Apply(BtnEffectStatic, LightingEffect.Static);
        Apply(BtnEffectBreathing, LightingEffect.Breathing);
        Apply(BtnEffectRainbow, LightingEffect.Rainbow);
        Apply(BtnEffectStrobe, LightingEffect.Strobe);
        Apply(BtnEffectPulse, LightingEffect.Pulse);
        Apply(BtnEffectReactive, LightingEffect.Reactive);
        Apply(BtnEffectRandom, LightingEffect.Random);
        Apply(BtnEffectWave, LightingEffect.Wave);
        Apply(BtnEffectFlame, LightingEffect.Flame);
        Apply(BtnEffectAurora, LightingEffect.Aurora);
        Apply(BtnEffectGradientCycle, LightingEffect.GradientCycle);
        Apply(BtnEffectMusic, LightingEffect.Music);
        Apply(BtnEffectComet, LightingEffect.Comet);
        Apply(BtnEffectOff, LightingEffect.Off);
    }

    private void Effect_Static_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Static, "静态灯效");
    private void Effect_Breathing_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Breathing, "呼吸灯效");
    private void Effect_Rainbow_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Rainbow, "彩虹灯效");
    private void Effect_Strobe_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Strobe, "闪烁灯效");
    private void Effect_Pulse_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Pulse, "脉冲灯效");
    private void Effect_Reactive_Click(object sender, RoutedEventArgs e) { InstallKeyboardHook(); SetEffect(LightingEffect.Reactive, "敲击灯效(按键触发)"); }
    private void Effect_Random_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Random, "随机灯效");
    private void Effect_Wave_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Wave, "海浪: 主色色相正弦波动");
    private void Effect_Flame_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Flame, "火焰: 暖色升腾闪烁");
    private void Effect_Aurora_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Aurora, "极光: 青紫蓝漂移");
    private void Effect_GradientCycle_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.GradientCycle, "渐变循环: 主色⇄副色");
    private void Effect_Music_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Music, "音乐律动: 模拟节拍脉冲");
    private void Effect_Comet_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Comet, "彗星: 高亮划过");
    private void Effect_Off_Click(object sender, RoutedEventArgs e) => SetEffect(LightingEffect.Off, "键盘灯已关闭");

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var (ri, gi, bi) = ParseRgb(tag);
        if (ri < 0) return;              // 坏数据(手改设置)直接忽略, 不再抛 OverflowException

        byte r = (byte)ri, g = (byte)gi, b = (byte)bi;

        // 预设作用于当前编辑目标(主色/副色)
        if (EditingSecondary)
        {
            _lightingEffects?.SetColor2(r, g, b);
            UpdateSwatchSecondary(r, g, b);
            ShowSnackbar($"副色: RGB({r},{g},{b})");
        }
        else
        {
            _lightingEffects?.SetColor(r, g, b);
            UpdateSwatchPrimary(r, g, b);
            ShowSnackbar($"主色: RGB({r},{g},{b})");
        }
        SetPickerFromRgb(r, g, b, apply: false);
        UpdateGradientPreview();
        RequestSave();
    }

    private void GradientPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var segments = tag.Split('|');
        if (segments.Length < 2) return;

        var colors = new List<(byte r, byte g, byte b)>();
        foreach (var s in segments)
        {
            var (r, g, b) = ParseRgb(s);
            if (r < 0) return;
            colors.Add(((byte)r, (byte)g, (byte)b));
        }

        _lightingEffects?.SetPalette(colors);
        SetPickerFromRgb(colors[0].r, colors[0].g, colors[0].b, apply: false);
        UpdateSwatchPrimary(colors[0].r, colors[0].g, colors[0].b);
        UpdateSwatchSecondary(colors[1].r, colors[1].g, colors[1].b);
        SetEffect(LightingEffect.GradientCycle, $"渐变循环: {colors.Count} 色流动");
    }

    /// <summary>解析 "r,g,b"; 任一分量非法或超出 0-255 时返回 (-1,-1,-1)</summary>
    private static (int r, int g, int b) ParseRgb(string s)
    {
        var parts = s.Split(',');
        if (parts.Length < 3) return (-1, -1, -1);
        if (!int.TryParse(parts[0].Trim(), out int r) || r < 0 || r > 255) return (-1, -1, -1);
        if (!int.TryParse(parts[1].Trim(), out int g) || g < 0 || g > 255) return (-1, -1, -1);
        if (!int.TryParse(parts[2].Trim(), out int b) || b < 0 || b > 255) return (-1, -1, -1);
        return (r, g, b);
    }

    #region 自定义预设

    private void RenderCustomPresets()
    {
        if (CustomColorsPanel == null || CustomGradientsPanel == null) return;

        CustomColorsPanel.Children.Clear();
        foreach (var tag in _settingsService.Settings.Keyboard.CustomColors.ToArray())
        {
            var btn = MakeColorSwatch(tag, true);
            if (btn != null) CustomColorsPanel.Children.Add(btn);
        }

        CustomGradientsPanel.Children.Clear();
        foreach (var tag in _settingsService.Settings.Keyboard.CustomGradients.ToArray())
        {
            var btn = MakeGradientSwatch(tag, true);
            if (btn != null) CustomGradientsPanel.Children.Add(btn);
        }
    }

    private static readonly SolidColorBrush SwatchStroke = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    private Button? MakeColorSwatch(string tag, bool custom)
    {
        var (r, g, b) = ParseRgb(tag);
        if (r < 0) return null;

        var btn = new Button
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(0),
            Tag = tag,
            Style = (Style)FindResource("GlassFlatButton"),
            ToolTip = custom ? $"自定义 RGB({r},{g},{b}) · 右键删除" : $"RGB({r},{g},{b})",
            Content = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ColorFrom(r, g, b),
                BorderBrush = SwatchStroke,
                BorderThickness = new Thickness(1),
            },
        };
        btn.Click += ColorPreset_Click;
        if (custom)
        {
            btn.MouseRightButtonUp += CustomPreset_Remove;
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "CustomColor_" + tag);
        }
        return btn;
    }

    private Button? MakeGradientSwatch(string tag, bool custom)
    {
        var segments = tag.Split('|');
        if (segments.Length < 2) return null;

        var stops = new GradientStopCollection();
        var names = new List<string>();
        for (int i = 0; i < segments.Length; i++)
        {
            var (r, g, b) = ParseRgb(segments[i]);
            if (r < 0) return null;
            stops.Add(new GradientStop(
                Color.FromRgb((byte)r, (byte)g, (byte)b),
                segments.Length == 1 ? 0 : i / (double)(segments.Length - 1)));
            names.Add($"{r},{g},{b}");
        }

        var btn = new Button
        {
            Width = 72,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(0),
            Tag = tag,
            Style = (Style)FindResource("GlassFlatButton"),
            ToolTip = custom ? $"自定义渐变 {string.Join("→", names)} · 右键删除" : $"渐变 {string.Join("→", names)}",
            Content = new Border
            {
                Width = 56,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                BorderBrush = SwatchStroke,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0),
                    GradientStops = stops,
                },
            },
        };
        btn.Click += GradientPreset_Click;
        if (custom)
        {
            btn.MouseRightButtonUp += CustomPreset_Remove;
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, "CustomGrad_" + tag);
        }
        return btn;
    }

    private void AddCustomColor_Click(object sender, RoutedEventArgs e)
    {
        var c = EditingSecondary
            ? _lightingEffects?.CurrentColor2 ?? ((byte)0, (byte)0, (byte)255)
            : _lightingEffects?.CurrentColor ?? ((byte)255, (byte)0, (byte)128);

        string tag = $"{c.r},{c.g},{c.b}";
        var list = _settingsService.Settings.Keyboard.CustomColors;
        if (list.Contains(tag))
        {
            ShowSnackbar("该颜色已在自定义预设中");
            return;
        }
        if (list.Count >= 24)
        {
            ShowSnackbar("自定义单色预设已达上限(24个), 右键删除一些再添加", true);
            return;
        }

        list.Add(tag);
        _settingsService.SaveSettings();
        RenderCustomPresets();
        ShowSnackbar($"已添加自定义颜色 RGB({c.r},{c.g},{c.b})");
    }

    private void AddCustomGradient_Click(object sender, RoutedEventArgs e)
    {
        var pal = _lightingEffects?.CurrentPalette;
        if (pal == null || pal.Count < 2) return;

        string tag = string.Join("|", pal.Select(c => $"{c.r},{c.g},{c.b}"));
        var list = _settingsService.Settings.Keyboard.CustomGradients;
        if (list.Contains(tag))
        {
            ShowSnackbar("该渐变已在自定义预设中");
            return;
        }
        if (list.Count >= 16)
        {
            ShowSnackbar("自定义渐变预设已达上限(16个), 右键删除一些再添加", true);
            return;
        }

        list.Add(tag);
        _settingsService.SaveSettings();
        RenderCustomPresets();
        ShowSnackbar($"已添加自定义 {pal.Count} 色渐变预设");
    }

    private void CustomPreset_Remove(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        var kb = _settingsService.Settings.Keyboard;
        bool removed = kb.CustomColors.Remove(tag);
        if (!removed) removed = kb.CustomGradients.Remove(tag);

        if (removed)
        {
            _settingsService.SaveSettings();
            RenderCustomPresets();
            ShowSnackbar("已删除自定义预设");
        }
    }

    #endregion

    #endregion

    #region HSV 调色盘

    private bool EditingSecondary => EditSecondaryColor?.IsChecked == true;

    /// <summary>启动时用当前主色初始化取色器</summary>
    private void InitPickerFromCurrentColors()
    {
        if (_lightingEffects != null)
        {
            var (r, g, b) = _lightingEffects.CurrentColor;
            var (r2, g2, b2) = _lightingEffects.CurrentColor2;
            SetPickerFromRgb(r, g, b, apply: false);
            UpdateSwatchPrimary(r, g, b);
            UpdateSwatchSecondary(r2, g2, b2);
        }
        else
        {
            UpdatePickerVisuals();
        }
    }

    private void PickerTarget_Changed(object sender, RoutedEventArgs e)
    {
        // 切换主/副色时, 把取色器同步到该颜色的现值
        var c = EditingSecondary
            ? _lightingEffects?.CurrentColor2 ?? ((byte)0, (byte)0, (byte)255)
            : _lightingEffects?.CurrentColor ?? ((byte)255, (byte)0, (byte)128);
        SetPickerFromRgb(c.Item1, c.Item2, c.Item3, apply: false);
    }

    private void SetPickerFromRgb(double r, double g, double b, bool apply)
    {
        RgbToHsv((byte)r, (byte)g, (byte)b, out var h, out var s, out var v);
        _pickerH = h;
        _pickerS = s;
        _pickerV = v;
        UpdatePickerVisuals();
        if (apply) ApplyPickerColor();
    }

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pickerDown = true;
        SvHit.CaptureMouse();
        UpdateSvFromPointer(e.GetPosition(SvHit));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pickerDown) return;
        UpdateSvFromPointer(e.GetPosition(SvHit));
    }

    private void UpdateSvFromPointer(Point p)
    {
        double w = SvHit.ActualWidth, h = SvHit.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _pickerS = Math.Clamp(p.X / w, 0, 1);
        _pickerV = Math.Clamp(1 - p.Y / h, 0, 1);
        UpdatePickerVisuals();
        ApplyPickerColor();
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pickerDown = true;
        if (sender is UIElement ui) ui.CaptureMouse();
        UpdateHueFromPointer(e.GetPosition((IInputElement)sender));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pickerDown) return;
        UpdateHueFromPointer(e.GetPosition((IInputElement)sender));
    }

    private void UpdateHueFromPointer(Point p)
    {
        // 色相条高240
        double frac = Math.Clamp(p.Y / 240.0, 0, 1);
        _pickerH = frac * 360.0;
        UpdatePickerVisuals();
        ApplyPickerColor();
    }

    private void Picker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _pickerDown = false;
        if (sender is UIElement ui) ui.ReleaseMouseCapture();
        // 松手时强制落地最终颜色: 拖动过程中被 150ms 节流丢掉的那一帧不会成为最终状态
        ApplyPickerColor(force: true);
        RequestSave();
    }

    private void UpdatePickerVisuals()
    {
        if (SvThumb == null) return;

        // SV底层渐变: 白→当前色相纯色
        var hueColor = HsvToRgb(_pickerH, 1, 1);
        SvWhiteLayer.Background = new LinearGradientBrush(
            Colors.White, hueColor, new Point(0, 0), new Point(1, 0));

        var current = HsvToRgb(_pickerH, _pickerS, _pickerV);

        // 拖动点位置
        double w = SvHit?.ActualWidth is > 0 ? SvHit.ActualWidth : 240;
        double h = SvHit?.ActualHeight is > 0 ? SvHit.ActualHeight : 240;
        Canvas.SetLeft(SvThumb, _pickerS * w - 8);
        Canvas.SetTop(SvThumb, (1 - _pickerV) * h - 8);
        Canvas.SetTop(HueThumb, _pickerH / 360.0 * 240.0 - 4);

        PickerPreview.Background = new SolidColorBrush(current);
        PickerHexLabel.Text = $"#{current.R:X2}{current.G:X2}{current.B:X2} · H{_pickerH:F0}°";
    }

    private void ApplyPickerColor(bool force = false)
    {
        var c = HsvToRgb(_pickerH, _pickerS, _pickerV);
        if (EditingSecondary)
        {
            // 拖动中走节流版本: 每个 MouseMove 都强制同步写 EC 会在桥慢时把拖拽卡住
            if (force) _lightingEffects?.SetColor2(c.R, c.G, c.B);
            else _lightingEffects?.SetColor2Live(c.R, c.G, c.B);
            UpdateSwatchSecondary(c.R, c.G, c.B);
        }
        else
        {
            if (force) _lightingEffects?.SetColor(c.R, c.G, c.B);
            else _lightingEffects?.SetColorLive(c.R, c.G, c.B);
            UpdateSwatchPrimary(c.R, c.G, c.B);
        }
    }

    private void UpdateSwatchPrimary(double r, double g, double b)
    {
        SwatchPrimary.Background = ColorFrom(r, g, b);
        UpdateGradientPreview();
    }

    private void UpdateSwatchSecondary(double r, double g, double b)
    {
        SwatchSecondary.Background = ColorFrom(r, g, b);
        UpdateGradientPreview();
    }

    private void UpdateGradientPreview()
    {
        if (GradientPreviewBrush == null) return;

        var pal = _lightingEffects?.CurrentPalette;
        GradientPreviewBrush.GradientStops.Clear();
        if (pal == null || pal.Count == 0) return;

        for (int i = 0; i < pal.Count; i++)
        {
            double offset = pal.Count == 1 ? 0 : i / (double)(pal.Count - 1);
            GradientPreviewBrush.GradientStops.Add(
                new GradientStop(Color.FromRgb(pal[i].r, pal[i].g, pal[i].b), offset));
        }

        if (PickerHex2Label != null)
        {
            var c2 = pal.Count > 1 ? pal[1] : pal[0];
            PickerHex2Label.Text = $"副 #{c2.r:X2}{c2.g:X2}{c2.b:X2} · 共 {pal.Count} 色";
        }
    }

    private static SolidColorBrush ColorFrom(double r, double g, double b) =>
        new(Color.FromRgb((byte)r, (byte)g, (byte)b));

    internal static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    internal static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
            return;
        }

        if (max == rd) h = 60.0 * (((gd - bd) / delta) % 6);
        else if (max == gd) h = 60.0 * (((bd - rd) / delta) + 2);
        else h = 60.0 * (((rd - gd) / delta) + 4);
        if (h < 0) h += 360;
    }

    #endregion

    private void PulseCapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PulseCapLabel != null)
            PulseCapLabel.Text = $"{(int)e.NewValue}%";
        if (_lightingEffects != null)
            _lightingEffects.PulseBrightnessCap = Math.Clamp(e.NewValue / 100.0, 0.1, 1.0);
        if (IsLoaded) RequestSave();
    }

    private int _nowPlayingCounter;
    private string _lastTrackTitle = "";

    /// <summary>轮询酷狗/QQ音乐/网易云窗口标题, 换歌时重置节拍tempo模型</summary>
    private void PollNowPlaying()
    {
        try
        {
            string title = "";
            string player = "";
            foreach (var pname in new[] { "kugou", "QQMusic", "cloudmusic" })
            {
                foreach (var p in Process.GetProcessesByName(pname))
                {
                    using (p)
                    {
                        var t = p.MainWindowTitle;
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            title = t;
                            player = pname switch { "kugou" => "KuGou Music", "QQMusic" => "QQ Music", _ => "NetEase Cloud Music" };
                        }
                    }
                    if (title.Length > 0) break;
                }
                if (title.Length > 0) break;
            }

            // 归一化后标题稳定, 界面不会跟着跑马灯抖, 也不会被误判成换歌
            string norm = NormalizeTitle(title);

            if (NowPlayingText != null)
            {
                NowPlayingText.Text = norm.Length > 0
                    ? $"{player}: {Truncate(norm, 36)}"
                    : "未检测到播放器 (节拍基于实时音频分析)";
            }

            if (!SameTrack(norm, _lastTrackTitle))
            {
                // 只有真换歌才重置tempo; 标题滚动/刷新不算, 否则节拍模型会被反复清空
                if (_lastTrackTitle.Length > 0 && norm.Length > 0
                    && Environment.TickCount64 - _lastTempoReset > 8000)
                {
                    _lightingEffects?.Audio.ResetTempo();
                    _lastTempoReset = Environment.TickCount64;
                    Logger.Info($"检测到换歌, 重置节拍tempo: {norm}");
                }
                _lastTrackTitle = norm;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to poll player title");
        }
    }

    private long _lastTempoReset;

    /// <summary>
    /// 播放器标题归一化: 酷狗标题栏是跑马灯, 同一首歌会不断循环滚动。
    /// 以"酷狗音乐"之后的位置为标题起点还原, 再去掉空白, 任意滚动相位都得到同一串。
    /// </summary>
    private static string NormalizeTitle(string t)
    {
        t = t.Trim();
        if (t.Length == 0) return "";
        const string mark = "酷狗音乐";
        int i = t.IndexOf(mark, StringComparison.Ordinal);
        if (i < 0) return t;
        return Strip(t.Substring(i + mark.Length) + t.Substring(0, i));
    }

    /// <summary>是否同一首歌: 完全相同, 或去空白后互为循环旋转(跑马灯不同相位)</summary>
    private static bool SameTrack(string a, string b)
    {
        if (a == b) return true;
        string na = Strip(a);
        string nb = Strip(b);
        if (na.Length == 0 || nb.Length == 0) return na.Length == nb.Length;
        if (na == nb) return true;
        return na.Length == nb.Length && (na + na).Contains(nb);
    }

    private static string Strip(string s) => new(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    /// <summary>
    /// A 面 Logo 背光开关(EC 命令 0x0F)。
    /// 写后立即回读, 界面一律以 EC 实际值为准; 0x0F 不可读时禁用控件并标注"不支持",
    /// 而不是显示一个假的成功状态。该命令没有颜色参数, 所以只能开关。
    /// </summary>
    private async void LogoLight_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _logoLightSuppress) return;

        var ec = _controlService?.Ec;
        if (ec == null)
        {
            if (LogoLightStatus != null) LogoLightStatus.Text = "EC 不可用";
            return;
        }

        bool want = LogoLightToggle.IsChecked == true;
        if (LogoLightStatus != null) LogoLightStatus.Text = "切换中…";

        // 两次 WMI 往返放到后台, 别阻塞 UI 线程
        int actual = await Task.Run(() =>
        {
            ec.SetLogoLight(want);
            return ec.GetLogoLight();
        });

        if (actual < 0)
        {
            // 一次回读失败(桥超时)不等于"机型不支持", 不能永久禁用控件 —— 
            // 真正的"不支持"由安装时的能力扫描(f.LogoLight)判定。这里回滚开关并提示重试。
            _logoLightSuppress = true;
            try { LogoLightToggle.IsChecked = !want; }
            finally { _logoLightSuppress = false; }
            if (LogoLightStatus != null) LogoLightStatus.Text = "读取失败";
            ShowSnackbar("Logo 背光回读失败, 请重试", true);
            Logger.Warn("Logo 背光: 写入后无法回读, 已回滚开关(不判定为不支持)");
            return;
        }

        _logoLightSuppress = true;
        try { LogoLightToggle.IsChecked = actual == 1; }
        finally { _logoLightSuppress = false; }

        if (LogoLightStatus != null) LogoLightStatus.Text = actual == 1 ? "已开" : "已关";
        ShowSnackbar(actual == (want ? 1 : 0)
            ? (actual == 1 ? "A 面 Logo 背光已打开" : "A 面 Logo 背光已关闭")
            : "Logo 背光写入后回读不一致, 请重试", actual != (want ? 1 : 0));

        _settingsService.UpdateSettings(s => s.Keyboard.LogoLight = actual == 1);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValueLabel != null)
            BrightnessValueLabel.Text = $"{(int)e.NewValue}%";

        if (_controlService?.Ec == null) return;

        var wmi = _controlService.Ec.Wmi;
        if (wmi == null || !wmi.IsAvailable) return;

        // EC亮度仅0-3四档; 滑条0-100映射, 用四舍五入确保100→3(满亮度)
        int level = (int)Math.Round(e.NewValue / 100.0 * 3.0);
        if (level < 0) level = 0;
        if (level > 3) level = 3;
        // EC 只有 0-3 四档: 拖动时每个像素都写一次 WMI 没有意义, 还会让拖动发卡
        if (level != _lastKbLevel)
        {
            _lastKbLevel = level;
            wmi.SetKeyboardBrightness(level);
        }
        RequestSave();
    }

    private async void RefreshEc_Click(object sender, RoutedEventArgs e)
    {
        // 一轮 EC 状态是 10 条桥命令, 每条最长等 2.5s。在 UI 线程同步读会把界面冻住十几秒,
        // 跟 ControlService.PollEcStatus 当初遇到的问题一模一样。
        bool ok = await Task.Run(() => _controlService.RefreshEcStatusNow());
        if (ok)
        {
            UpdateTimer_Tick(null, EventArgs.Empty);
            ShowSnackbar("EC 状态已刷新");
        }
        else
        {
            ShowSnackbar("EC 状态不可用 (WMI 未就绪)", true);
        }
    }

    private async void ExportEc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 快照里含 EC 实时状态, 同样要先在后台读齐再格式化
            var text = await Task.Run(() => _controlService.ExportStatusSnapshot());
            Clipboard.SetText(text);
            ShowSnackbar("状态快照已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "导出状态快照失败");
            ShowSnackbar("导出失败: " + ex.Message, true);
        }
    }

    /// <summary>
    /// 启动时把EC键盘亮度同步到滑条当前值(默认满亮度), 避免EC停留在低档位显得暗。
    /// </summary>
    public void SyncKeyboardBrightness()
    {
        try
        {
            if (_controlService?.Ec == null) return;
            var wmi = _controlService.Ec.Wmi;
            if (wmi == null || !wmi.IsAvailable) return;
            int level = (int)Math.Round(BrightnessSlider.Value / 100.0 * 3.0);
            if (level < 0) level = 0;
            if (level > 3) level = 3;
            wmi.SetKeyboardBrightness(level);
            Logger.Info($"启动同步键盘亮度: 滑条={BrightnessSlider.Value} → EC档位={level}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "同步键盘亮度失败");
        }
    }
}
