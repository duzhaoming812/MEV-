using System;
using NAudio.CoreAudioApi;
using NLog;

namespace MEVJiaolongControlCenter.Core.Audio;

/// <summary>
/// 系统默认播放设备的音量/静音控制(CoreAudio)。仅操作默认 Render 端点, 不改设备配置。
/// 必须在 STA 线程创建和调用(WPF UI 线程满足)。
/// </summary>
public sealed class AudioVolumeControl : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;

    public bool Available { get; private set; }
    public string DeviceName { get; private set; } = "";
    public string DeviceId { get; private set; } = "";
    public string FormatText { get; private set; } = "";
    public int VolumePercent { get; private set; }
    public bool Mute { get; private set; }

    public bool Initialize()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Rebind(device);
            Available = true;
            Refresh();
            Logger.Info($"音频端点已就绪: {DeviceName}, 音量 {VolumePercent}%");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "音频端点初始化失败, 音量控制禁用");
            Available = false;
            return false;
        }
    }

    /// <summary>绑定到一个具体端点(初始化与默认设备切换共用), 旧的端点在这里释放。</summary>
    private void Rebind(MMDevice device)
    {
        try { _volume = null; _device?.Dispose(); } catch { }
        _device = device;
        _volume = device.AudioEndpointVolume;
        DeviceName = device.FriendlyName;
        DeviceId = device.ID;
        FormatText = "";
        try
        {
            var fmt = device.AudioClient.MixFormat;
            FormatText = $"{fmt.Channels}ch {fmt.SampleRate}Hz";
        }
        catch { }
    }

    /// <summary>读取当前音量/静音(系统热键或其它程序改动后同步用)。</summary>
    public void Refresh()
    {
        if (_volume == null || _enumerator == null) return;
        try
        {
            // 默认播放设备变了(插耳机/切蓝牙/显示器音频)时, 继续读旧端点会让音量与静音
            // 永久停在旧值, 写也写不回新设备 —— 检测到 ID 变化就重新绑定。
            MMDevice? current = null;
            try { current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch (Exception ex) { Logger.Debug(ex, "枚举默认音频设备失败"); }

            if (current != null)
            {
                if (string.Equals(current.ID, DeviceId, StringComparison.Ordinal))
                {
                    current.Dispose();   // 只用于比较
                }
                else
                {
                    Rebind(current);
                    Logger.Info($"默认音频设备已切换: {DeviceName}");
                }
            }
            if (_volume == null) return;
            VolumePercent = (int)Math.Round(_volume.MasterVolumeLevelScalar * 100);
            Mute = _volume.Mute;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "读取系统音量失败");
        }
    }

    public bool SetVolume(int percent)
    {
        if (_volume == null) return false;
        try
        {
            int p = Math.Clamp(percent, 0, 100);
            _volume.MasterVolumeLevelScalar = p / 100f;
            VolumePercent = p;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "设置系统音量失败");
            return false;
        }
    }

    public bool SetMute(bool mute)
    {
        if (_volume == null) return false;
        try
        {
            _volume.Mute = mute;
            Mute = mute;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "设置静音失败");
            return false;
        }
    }

    public void Dispose()
    {
        try { _volume = null; _device?.Dispose(); _enumerator?.Dispose(); } catch { }
        Available = false;
        GC.SuppressFinalize(this);
    }
}
