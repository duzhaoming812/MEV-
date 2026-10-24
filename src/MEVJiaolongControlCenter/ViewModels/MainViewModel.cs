using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MEVJiaolongControlCenter.Core.EC;
using MEVJiaolongControlCenter.Services;
using NLog;

namespace MEVJiaolongControlCenter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ControlService _controlService;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _statusMessage = "正在初始化...";

    [ObservableProperty]
    // -1 = 未选中: EC 不可用(Unknown)时不能默认落在"游戏"上, 否则界面会假装知道当前档位
    private int _selectedModeIndex = -1;

    [ObservableProperty]
    private int _selectedGpuModeIndex = -1;

    [ObservableProperty]
    private bool _isMaxFan;

    [ObservableProperty]
    private bool _startWithWindows;

    private bool _suppressAutostart;

    // EC → 界面的"回显"赋值不能反过来再写一次 EC: 外部(Fn 快捷键)改档位时,
    // 属性同步会走到同一个 OnChanged, 不加这个闸门就会把刚读到的值再写回去。
    private bool _suppressModeWrite;
    private bool _suppressGpuWrite;

    public ControlService ControlService => _controlService;
    public StressTestService Stress { get; }

    public MainViewModel(ControlService controlService, SettingsService settingsService, StressTestService stress)
    {
        _controlService = controlService;
        _settingsService = settingsService;
        Stress = stress;

        _controlService.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ControlService.CurrentPerformanceMode):
                    _suppressModeWrite = true;
                    SelectedModeIndex = (int)_controlService.CurrentPerformanceMode;
                    _suppressModeWrite = false;
                    break;
                case nameof(ControlService.CurrentGpuMode):
                    _suppressGpuWrite = true;
                    SelectedGpuModeIndex = (int)_controlService.CurrentGpuMode;
                    _suppressGpuWrite = false;
                    break;
                case nameof(ControlService.IsMaxFan):
                    IsMaxFan = _controlService.IsMaxFan;
                    break;
            }
        };
    }

    /// <summary>加载时同步自启状态, 不触发启用/禁用动作</summary>
    public void SyncAutostartState(bool enabled)
    {
        _suppressAutostart = true;
        StartWithWindows = enabled;
        _suppressAutostart = false;
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        try
        {
            await _controlService.InitializeAsync();

            // 先回显、最后才打开 IsInitialized: 这两个属性一赋值就会触发
            // OnSelectedModeIndexChanged → 写 EC。若在启动时赋值, 等于每次开机都把
            // 读到的档位(以及读失败时的 Unknown→0)再写回 EC 一遍。
            var mode = _controlService.CurrentPerformanceMode;
            if (mode != PerformanceMode.Unknown) SelectedModeIndex = (int)mode;

            var gpu = _controlService.CurrentGpuMode;
            if (gpu != GpuMode.Unknown) SelectedGpuModeIndex = (int)gpu;

            IsInitialized = true;
            StatusMessage = "就绪";

            Logger.Info("主界面初始化完成");
        }
        catch (Exception ex)
        {
            StatusMessage = $"初始化失败: {ex.Message}";
            Logger.Error(ex, "主界面初始化失败");
        }
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        if (!IsInitialized || _suppressModeWrite) return;

        var mode = (PerformanceMode)value;
        if (_controlService.SetPerformanceMode(mode))
        {
            StatusMessage = mode == PerformanceMode.Turbo
                ? "已切换至狂暴模式(风扇自动拉满)"
                : $"已切换至{GetModeName(mode)}";
            return;
        }

        // EC 没接受: 把单选回滚到真实档位。回滚赋值不能再触发一次写入, 否则等于把
        // 刚被拒绝的操作又反向做一遍。
        RollbackModeSelection();
    }

    private void RollbackModeSelection()
    {
        _suppressModeWrite = true;
        // Unknown = -1, 正好是"没有单选被勾上"(界面显示未知模式)
        SelectedModeIndex = (int)_controlService.CurrentPerformanceMode;
        _suppressModeWrite = false;
        StatusMessage = string.IsNullOrEmpty(_controlService.EcErrorMessage)
            ? "性能档切换未生效(EC 无应答)"
            : _controlService.EcErrorMessage;
    }

    partial void OnSelectedGpuModeIndexChanged(int value)
    {
        if (!IsInitialized || _suppressGpuWrite) return;

        var mode = (GpuMode)value;
        if (_controlService.SetGpuMode(mode))
        {
            StatusMessage = $"显卡模式: {GetGpuModeName(mode)} · MUX 需重启后生效";
            return;
        }

        RollbackGpuSelection();
    }

    private void RollbackGpuSelection()
    {
        _suppressGpuWrite = true;
        SelectedGpuModeIndex = _controlService.CurrentGpuMode == GpuMode.Unknown
            ? -1
            : (int)_controlService.CurrentGpuMode;
        _suppressGpuWrite = false;
        StatusMessage = string.IsNullOrEmpty(_controlService.EcErrorMessage)
            ? "显卡模式切换未生效(EC 无应答)"
            : _controlService.EcErrorMessage;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressAutostart || !IsInitialized) return;

        bool ok = value ? AutostartService.Enable() : AutostartService.Disable();

        if (value && !ok)
        {
            // 启用失败(非管理员/计划任务被策略拦住)时把开关拨回去, 否则界面显示"已开启"骗人
            _suppressAutostart = true;
            try { StartWithWindows = false; }
            finally { _suppressAutostart = false; }
            StatusMessage = "开机自启启用失败(需要管理员权限或计划任务被禁用)";
            return;
        }

        StatusMessage = value ? "已启用开机自启(最高权限)" : "已关闭开机自启";
    }

    public string CpuTempText => $"{_controlService.CpuTemp:F0}°C";
    public string GpuTempText => $"{_controlService.GpuTemp:F0}°C";
    public string CpuPowerText => $"{_controlService.CpuPower:F1}W";
    public string GpuPowerText => $"{_controlService.GpuPower:F1}W";
    public string CpuFanText => $"{_controlService.CpuFanRpm} RPM";
    public string GpuFanText => $"{_controlService.GpuFanRpm} RPM";
    public string BatteryText => $"{_controlService.BatteryLevel:F0}%";

    private static string GetModeName(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Quiet => "办公模式",
        PerformanceMode.Balanced => "游戏模式",
        PerformanceMode.Turbo => "狂暴模式",
        _ => "未知"
    };

    private static string GetGpuModeName(GpuMode mode) => mode switch
    {
        GpuMode.Hybrid => "混合模式",
        GpuMode.Discrete => "独显直连",
        _ => "未知"
    };
}
