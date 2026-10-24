using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;

namespace MEVJiaolongControlCenter.Core.Audio;

public enum AudioSourceKind
{
    /// <summary>模拟节拍(无采集)</summary>
    Simulated = 0,
    /// <summary>电脑声音(默认播放设备回环采集)</summary>
    SystemLoopback = 1,
    /// <summary>外界声音(默认麦克风)</summary>
    Microphone = 2,
}

/// <summary>
/// 回环采集(渲染端点 + Loopback 标志), 事件同步 + 10ms 缓冲, 回调延迟压到最低。
/// NAudio 自带的 WasapiLoopbackCapture 是 100ms 定时轮询, 帧粒度太粗。
/// </summary>
internal sealed class FastLoopbackCapture : WasapiCapture
{
    public FastLoopbackCapture(MMDevice renderDevice)
        : base(renderDevice, true, 10)
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback;
}

/// <summary>
/// 实时音频分析: 电脑声音(回环)=帧级节拍检测, 麦克风=音量电平。
/// 回环 10ms 缓冲内部再切成 5ms 帧, 时间分辨率 5ms;
/// 节拍只认"能量抬起后快速回落"的瞬态, 与音量大小完全无关。
/// </summary>
public class AudioLevelMeter : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly double TICKS_PER_MS = Stopwatch.Frequency / 1000.0;

    private IWaveIn? _capture;
    private MMDevice? _renderDevice;
    private string _renderDeviceId = "";
    private bool _disposed;
    private volatile bool _resetRequested;

    public AudioSourceKind Source { get; private set; } = AudioSourceKind.Simulated;

    /// <summary>平滑后的音量电平 0..1 (麦克风模式)</summary>
    public double Level { get; private set; }

    /// <summary>已确认的节拍数 (所有起音, 含装饰音)</summary>
    public int BeatCount { get; private set; }

    /// <summary>正拍序号 (只统计落在tempo网格上的拍, 用于换色)</summary>
    public int BeatIndex { get; private set; }

    /// <summary>最近一拍的强度: 1=正拍, 0.5=网格外装饰音</summary>
    public double BeatStrength { get; private set; } = 1.0;

    /// <summary>IOI直方图估计的BPM (0=样本不足)</summary>
    public double EstimatedBpm { get; private set; }

    /// <summary>当前拍周期(ms)</summary>
    public double BeatPeriodMs { get; private set; } = 500;

    private long _lastBeatTime;

    /// <summary>
    /// 节拍脉冲 0..1: 命中=强度值, 之后按拍周期的1/3做半衰期指数衰减。
    /// 按高精度时钟实时算, 不依赖采集回调节奏(静音时回环可能不再送数据)。
    /// </summary>
    public double BeatPulse
    {
        get
        {
            if (_lastBeatTime == 0) return 0;
            double half = Math.Clamp(BeatPeriodMs / 3.0, 70, 240);
            double age = Math.Max(0, NowMs() - _lastBeatTime);
            double v = Math.Pow(0.5, age / half) * BeatStrength;
            return v < 0.004 ? 0 : v;
        }
    }

    // ===== 帧级DSP状态 =====
    private const double HOP_MS = 5.0;          // 分析帧步进
    private const double ENV_TAU_MS = 6.0;      // 快包络: 抓瞬态起点(5ms级)
    private const double SLOW_TAU_MS = 30.0;    // 慢包络: 判回落, 抹平波形纹波
    private const double BASE_TAU_MS = 250.0;   // 自适应基线时间常数
    private int _hopLen = 240;
    private int _hopAcc;
    private int _hopIndex;
    private double _sumAbs;
    private double _envFull, _envSlow, _baseFull;
    private double _envCoef, _slowCoef, _baseCoef;
    private int _sampleRate;

    // 待确认瞬态
    private long _candTick;
    private double _candPeak, _candBase;

    // IOI直方图
    private const int HIST_BIN_MS = 8;
    private const int HIST_MIN_MS = 280;   // ≈214BPM
    private const int HIST_MAX_MS = 1200;  // ≈50BPM
    private const int HIST_BINS = (HIST_MAX_MS - HIST_MIN_MS) / HIST_BIN_MS + 1;
    private readonly List<long> _onsets = new();
    private readonly int[] _hist = new int[HIST_BINS];

    // 判定参数
    private const double FLOOR = 0.0020;      // 静音门限(整流均值)
    private const double RISE = 1.40;         // 相对基线抬升≥40%才算瞬态候选
    private const double FALL_BACK = 0.30;    // 需在窗口内回落掉抬升量的30%
    private const int CONFIRM_MIN_MS = 25;
    private const int CONFIRM_MAX_MS = 100;
    private const int REFRACTORY_MS = 120;    // 两拍最小间隔
    private const double GRID_TOLERANCE = 0.18; // 落在拍网格±18%内算正拍

    private static long NowMs() => (long)(Stopwatch.GetTimestamp() / TICKS_PER_MS);

    /// <summary>换歌/换源时清空tempo模型。UI 线程只置标志, 清空动作放到采集线程里做,
    /// 否则会和正在跑的 _onsets.Add/RemoveAt 抢同一个 List。</summary>
    public void ResetTempo() => _resetRequested = true;

    private void ResetTempoCore()
    {
        _onsets.Clear();
        EstimatedBpm = 0;
        BeatPeriodMs = 500;
        _lastBeatTime = 0;
        _candTick = 0;
        BeatStrength = 1.0;
    }

    public bool IsActive => _capture != null;

    /// <summary>
    /// 采集期间默认播放设备可能被换掉(插耳机/切蓝牙/显示器音频)。回环仍绑在旧端点上时电平会一直是 0,
    /// 这里发现默认端点变了就按当前源重启采集。
    /// </summary>
    public void EnsureDefaultDevice()
    {
        if (_capture == null || Source != AudioSourceKind.SystemLoopback) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (!string.Equals(current.ID, _renderDeviceId, StringComparison.Ordinal))
            {
                Logger.Info("默认播放设备已变化, 重启音频采集");
                Start(AudioSourceKind.SystemLoopback);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "检查默认音频设备失败");
        }
    }

    public event Action<string>? Failed;

    public bool Start(AudioSourceKind kind)
    {
        Stop();
        if (kind == AudioSourceKind.Simulated)
        {
            Source = kind;
            return true;
        }

        try
        {
            if (kind == AudioSourceKind.SystemLoopback)
            {
                using var enumerator = new MMDeviceEnumerator();
                var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                // 端点对象的生命周期由本类持有: WasapiCapture 内部只借用, 不会释放它。
                _renderDevice?.Dispose();
                _renderDevice = render;
                _renderDeviceId = render.ID;
                _capture = new FastLoopbackCapture(render);
            }
            else
            {
                _capture = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(44100, 16, 1),
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 5
                };
            }

            _capture.DataAvailable += OnData;
            _capture.StartRecording();
            Source = kind;
            Logger.Info($"音频采集已启动: {kind}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"音频采集初始化失败: {kind}");
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            Source = AudioSourceKind.Simulated;
            Failed?.Invoke(ex.Message);
            return false;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            ResetTempoCore();
        }

        try
        {
            var fmt = _capture?.WaveFormat;
            if (fmt == null || e.BytesRecorded <= 0) return;
            if (Source == AudioSourceKind.SystemLoopback) ProcessLoopback(e, fmt);
            else if (Source == AudioSourceKind.Microphone) ProcessMic(e, fmt);
        }
        catch
        {
            // 采集缓冲异常忽略, 不影响主流程
        }
    }

    /// <summary>麦克风: 只算音量电平(峰值+能量混合, 快攻击慢释放)</summary>
    private void ProcessMic(WaveInEventArgs e, WaveFormat fmt)
    {
        var buf = new WaveBuffer(e.Buffer);
        int ch = Math.Max(1, fmt.Channels);
        double peak = 0, sumSq = 0;
        int count = 0;

        if (fmt.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (2 * ch);
            for (int i = 0; i < frames; i++)
            {
                double acc = 0;
                for (int c = 0; c < ch; c++) acc += buf.ShortBuffer[i * ch + c];
                double v = Math.Abs(acc / ch) / 32768.0;
                if (v > peak) peak = v;
                sumSq += v * v;
                count++;
            }
        }
        else if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int frames = e.BytesRecorded / (4 * ch);
            for (int i = 0; i < frames; i++)
            {
                double acc = 0;
                for (int c = 0; c < ch; c++) acc += buf.FloatBuffer[i * ch + c];
                double v = Math.Abs(acc / ch);
                if (v > peak) peak = v;
                sumSq += v * v;
                count++;
            }
        }
        else return;

        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        double lvl = Math.Min(1.0, (peak * 0.45 + rms * 3.0) * 1.35);
        Level = lvl > Level ? lvl : Level * 0.86 + lvl * 0.14;
    }

    /// <summary>电脑声音: 混成单声道后按5ms帧推进包络与节拍判定</summary>
    private void ProcessLoopback(WaveInEventArgs e, WaveFormat fmt)
    {
        int ch = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        if (!isFloat && fmt.BitsPerSample != 16) return;

        if (_sampleRate != fmt.SampleRate) InitDsp(fmt.SampleRate);

        var buf = new WaveBuffer(e.Buffer);
        int frames = e.BytesRecorded / ((isFloat ? 4 : 2) * ch);
        long t0 = NowMs();
        _hopIndex = 0;

        for (int i = 0; i < frames; i++)
        {
            double s;
            if (isFloat)
            {
                double acc = 0;
                for (int c = 0; c < ch; c++) acc += buf.FloatBuffer[i * ch + c];
                s = acc / ch;
            }
            else
            {
                double acc = 0;
                for (int c = 0; c < ch; c++) acc += buf.ShortBuffer[i * ch + c];
                s = acc / ch / 32768.0;
            }

            _sumAbs += Math.Abs(s);
            if (++_hopAcc >= _hopLen)
            {
                double mean = _sumAbs / _hopLen;
                _sumAbs = 0;
                _hopAcc = 0;
                AnalyzeHop(mean, t0 + (long)(_hopIndex * HOP_MS));
                _hopIndex++;
            }
        }
    }

    private void InitDsp(int sampleRate)
    {
        _sampleRate = sampleRate;
        _hopLen = Math.Max(16, (int)(sampleRate * HOP_MS / 1000.0));
        _envCoef = 1 - Math.Exp(-HOP_MS / ENV_TAU_MS);
        _slowCoef = 1 - Math.Exp(-HOP_MS / SLOW_TAU_MS);
        _baseCoef = 1 - Math.Exp(-HOP_MS / BASE_TAU_MS);
        _hopAcc = 0;
        _sumAbs = 0;
        _envFull = 0;
        _envSlow = 0;
        _baseFull = 0;
        _candTick = 0;
    }

    /// <summary>
    /// 每5ms一帧: 快包络抓瞬态起点, 慢包络判"是否回落"。
    /// 音量调节(台阶或斜坡)抬起来后维持在新水平不回落, 因此永远不会被判成拍;
    /// 慢包络(30ms)还能抹平纯音/和声的波形纹波, 避免纹波被当成回落。
    /// </summary>
    private void AnalyzeHop(double meanAbs, long tick)
    {
        _envFull += _envCoef * (meanAbs - _envFull);
        _envSlow += _slowCoef * (meanAbs - _envSlow);
        _baseFull += _baseCoef * (_envFull - _baseFull);

        if (_candTick > 0)
        {
            long age = tick - _candTick;
            if (_envSlow > _candPeak) _candPeak = _envSlow;
            double rise = _candPeak - _candBase;
            if (age >= CONFIRM_MIN_MS && rise > 0 && (_candPeak - _envSlow) >= rise * FALL_BACK)
            {
                AcceptBeat(_candTick);
                _candTick = 0;
            }
            else if (age >= CONFIRM_MAX_MS)
            {
                _candTick = 0; // 音量台阶/持续长音: 丢弃
            }
            return;
        }

        if (_envFull > FLOOR && _baseFull > 0
            && _envFull > _baseFull * RISE
            && tick - _lastBeatTime > REFRACTORY_MS)
        {
            _candTick = tick;
            _candPeak = _envSlow;
            _candBase = _envSlow;
        }
    }

    private void AcceptBeat(long tick)
    {
        // 落在tempo网格上的算正拍(满强度+换色), 网格外的装饰音减半强度且不换色
        double strength = 1.0;
        if (EstimatedBpm > 0 && _lastBeatTime > 0 && BeatPeriodMs > 0)
        {
            double ibi = tick - _lastBeatTime;
            double k = Math.Round(ibi / BeatPeriodMs, MidpointRounding.AwayFromZero);
            if (k < 1) strength = 0.5;
            else if (Math.Abs(ibi - k * BeatPeriodMs) > BeatPeriodMs * GRID_TOLERANCE) strength = 0.5;
        }

        _lastBeatTime = tick;
        BeatStrength = strength;
        BeatCount++;
        if (strength >= 1.0) BeatIndex++;

        _onsets.Add(tick);
        while (_onsets.Count > 0 && tick - _onsets[0] > 6000) _onsets.RemoveAt(0);
        UpdateTempo();
    }

    /// <summary>
    /// 用最近6秒内所有拍间隔做IOI直方图投票, 取最强周期当tempo。
    /// 比"最近几个间隔取中位数"稳得多: 偶发的多检/漏检会被周期性投票自然压掉。
    /// </summary>
    private void UpdateTempo()
    {
        int n = _onsets.Count;
        if (n < 6) return;

        Array.Clear(_hist, 0, _hist.Length);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                long d = _onsets[j] - _onsets[i];
                if (d < HIST_MIN_MS) continue;
                if (d > HIST_MAX_MS) break; // 时间升序, 后面只会更长
                int b = (int)((d - HIST_MIN_MS) / HIST_BIN_MS);
                int w = d >= 375 && d <= 800 ? 4 : 2; // 偏好 75-160BPM
                _hist[b] += w;
                if (b > 0) _hist[b - 1] += w / 2;
                if (b + 1 < HIST_BINS) _hist[b + 1] += w / 2;
            }
        }

        int best = 0;
        for (int b = 1; b < HIST_BINS; b++) if (_hist[b] > _hist[best]) best = b;
        if (_hist[best] < 10) return;

        double period = HIST_MIN_MS + best * HIST_BIN_MS + HIST_BIN_MS / 2.0;

        // 倍频/半频纠正: 把tempo拉回 75-160BPM 主区间
        if (period < 400 && ScoreAt(period * 2) >= _hist[best] * 0.55) period *= 2;
        else if (period > 800 && ScoreAt(period / 2) >= _hist[best] * 0.55) period /= 2;

        // 直方图本身已经很稳(6秒窗口投票), 所以可以较快跟随;
        // 只在突变(换歌/变速)时直接吸附, 平时用EMA避免读数抖动
        double diff = Math.Abs(period - BeatPeriodMs) / BeatPeriodMs;
        BeatPeriodMs = diff > 0.25 ? period : BeatPeriodMs * 0.7 + period * 0.3;

        EstimatedBpm = 60000.0 / BeatPeriodMs;
    }

    private int ScoreAt(double p)
    {
        if (p < HIST_MIN_MS || p > HIST_MAX_MS) return 0;
        int b = (int)((p - HIST_MIN_MS) / HIST_BIN_MS);
        return _hist[Math.Clamp(b, 0, HIST_BINS - 1)];
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        try { _renderDevice?.Dispose(); } catch { }
        _renderDevice = null;
        _renderDeviceId = "";
        Level = 0;
        BeatCount = 0;
        BeatIndex = 0;
        BeatStrength = 1.0;
        EstimatedBpm = 0;
        BeatPeriodMs = 500;
        _onsets.Clear();
        _candTick = 0;
        _lastBeatTime = 0;
        _envFull = 0;
        _envSlow = 0;
        _baseFull = 0;
        _sumAbs = 0;
        _hopAcc = 0;
        _sampleRate = 0;
        if (Source != AudioSourceKind.Simulated)
            Logger.Info("音频采集已停止");
        Source = AudioSourceKind.Simulated;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
