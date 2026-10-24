using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MEVJiaolongControlCenter.Core.Audio;
using MEVJiaolongControlCenter.Core.Wmi;
using NLog;

namespace MEVJiaolongControlCenter.Core.EC;

public enum LightingEffect
{
    Off,
    Static,
    Breathing,
    Rainbow,
    Strobe,
    Pulse,
    Random,
    Reactive,
    /// <summary>色相正弦摆动(主色附近)</summary>
    Wave,
    /// <summary>暖色火焰上升</summary>
    Flame,
    /// <summary>极光: 青→紫→蓝漂移</summary>
    Aurora,
    /// <summary>主色⇄副色渐变循环</summary>
    GradientCycle,
    /// <summary>音乐律动: 模拟节拍脉冲</summary>
    Music,
    /// <summary>彗星: 高亮划过回落主色</summary>
    Comet
}

public class LightingEffects : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly WmiInterface? _wmi;
    private Timer? _timer;
    /// <summary>灯效定时器代次: 切效果后在飞回调据此作废, 不会把旧颜色盖到新效果上</summary>
    private int _effectGen;
    private LightingEffect _currentEffect = LightingEffect.Static;
    private byte _r = 255, _g = 0, _b = 0;
    private byte _r2 = 0, _g2 = 0, _b2 = 255;
    private double _hue;
    private double _breathPhase;
    private double _pulsePhase;
    private readonly Random _rng = new();
    private bool _disposed;
    private long _lastWmiCall;
    private const int MIN_WMI_INTERVAL_MS = 150;

    public LightingEffect CurrentEffect => _currentEffect;
    public (byte r, byte g, byte b) CurrentColor => (_r, _g, _b);
    public (byte r, byte g, byte b) CurrentColor2 => (_r2, _g2, _b2);

    /// <summary>声音电平表: 音乐律动效果的数据源(模拟/电脑声音/麦克风)</summary>
    public AudioLevelMeter Audio { get; } = new();

    /// <summary>律动亮度上限 0.1-1.0, 默认1.0=满亮度脉冲(用户可手动压低)</summary>
    public double PulseBrightnessCap { get; set; } = 1.0;

    public bool SetAudioSource(AudioSourceKind kind) => Audio.Start(kind);

    /// <summary>
    /// 多色渐变调色板(2-6色), 渐变类灯效沿调色板循环插值。
    /// **写时复制**: 每个效果定时器(50-150ms)都在读它, 而"动态随机"的漂移定时器(60ms)
    /// 同时改它, UI 线程还会枚举它。原来的 List 会被并发 Clear/RemoveAt → 读者抛
    /// InvalidOperationException/IndexOutOfRange, 而 System.Threading.Timer 回调里的异常在
    /// .NET 8 会直接终止整个进程。改成每次写入都换一个新数组, 读者拿到的永远是一致快照。
    /// </summary>
    private (byte r, byte g, byte b)[] _palette = { (255, 0, 128), (0, 0, 255) };
    public IReadOnlyList<(byte r, byte g, byte b)> CurrentPalette => _palette;

    public void SetPalette(IEnumerable<(byte r, byte g, byte b)> colors)
    {
        var arr = colors.Take(6).ToArray();
        if (arr.Length < 2) return;
        _palette = arr;
        (_r, _g, _b) = _palette[0];
        (_r2, _g2, _b2) = _palette[1];
    }

    /// <summary>沿调色板闭环插值, t∈[0,1)</summary>
    private (byte r, byte g, byte b) PaletteLerp(double t)
    {
        var pal = _palette;           // 取一次快照, 避免中途被替换后 Count 与索引不一致
        int n = pal.Length;
        double pos = ((t % 1.0) + 1.0) % 1.0 * n;
        int i = (int)pos % n;
        double f = pos - Math.Floor(pos);
        var a = pal[i];
        var b = pal[(i + 1) % n];
        return ((byte)(a.r * (1 - f) + b.r * f),
                (byte)(a.g * (1 - f) + b.g * f),
                (byte)(a.b * (1 - f) + b.b * f));
    }

    /// <summary>主色⇄副色插值</summary>
    private (byte r, byte g, byte b) Lerp2(double t)
    {
        var pal = _palette;
        var a = pal[0];
        var b = pal.Length > 1 ? pal[1] : pal[0];
        return ((byte)(a.r * (1 - t) + b.r * t),
                (byte)(a.g * (1 - t) + b.g * t),
                (byte)(a.b * (1 - t) + b.b * t));
    }

    public LightingEffects(WmiInterface? wmi)
    {
        _wmi = wmi;
    }

    /// <summary>
    /// 拖动取色器时用: 只更新颜色状态并按 150ms 节流下发, 不在每个 MouseMove 上同步写 EC。
    /// 松手时再调用 SetColor 强制落地最终颜色, 保证最后选中的颜色一定生效。
    /// </summary>
    public void SetColorLive(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        var pal = _palette.ToArray();
        if (pal.Length > 0)
        {
            pal[0] = (r, g, b);
            _palette = pal;
        }
        if (_currentEffect == LightingEffect.Static)
        {
            SendColor(r, g, b);
        }
    }

    /// <summary>拖动副色取色器时用, 语义同 <see cref="SetColorLive"/>。</summary>
    public void SetColor2Live(byte r, byte g, byte b)
    {
        _r2 = r; _g2 = g; _b2 = b;
        var pal = _palette;
        if (pal.Length > 1)
        {
            var copy = pal.ToArray();
            copy[1] = (r, g, b);
            _palette = copy;
        }
        else
        {
            _palette = pal.Append((r, g, b)).ToArray();
        }
        if (_currentEffect == LightingEffect.Static)
        {
            SendColor(r, g, b);
        }
    }

    public void SetColor(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        var pal = _palette.ToArray();
        if (pal.Length > 0)
        {
            pal[0] = (r, g, b);
            _palette = pal;
        }
        if (_currentEffect == LightingEffect.Static)
        {
            SendColorNow(r, g, b);
        }
    }

    /// <summary>设置副色(渐变/双色的第二色)</summary>
    public void SetColor2(byte r, byte g, byte b)
    {
        _r2 = r; _g2 = g; _b2 = b;
        var pal = _palette;                 // 先取快照, 避免判长和拷贝之间被别的线程换成更短的数组
        if (pal.Length > 1)
        {
            var copy = pal.ToArray();
            copy[1] = (r, g, b);
            _palette = copy;
        }
        else
        {
            _palette = pal.Append((r, g, b)).ToArray();
        }
    }

    public void SetEffect(LightingEffect effect)
    {
        _currentEffect = effect;
        StopTimer();

        switch (effect)
        {
            case LightingEffect.Off:
                SendMode(0);
                break;

            case LightingEffect.Static:
                SendMode(1);
                SendColorNow(_r, _g, _b);
                break;

            case LightingEffect.Breathing:
                SendMode(2);
                SendColor(_r, _g, _b);
                _breathPhase = 0;
                StartTimer(100, BreathingTick);
                break;

            case LightingEffect.Rainbow:
                SendMode(1);
                _hue = 0;
                StartTimer(100, RainbowTick);
                break;

            case LightingEffect.Strobe:
                SendMode(1);
                StartTimer(150, StrobeTick);
                break;

            case LightingEffect.Pulse:
                SendMode(1);
                _pulsePhase = 0;
                StartTimer(100, PulseTick);
                break;

            case LightingEffect.Random:
                SendMode(1);
                StartTimer(800, RandomTick);
                break;

            case LightingEffect.Reactive:
                SendMode(1);
                SendColorNow(_r, _g, _b);
                _reactiveFade = 0;
                StartTimer(100, ReactiveTick);
                break;

            case LightingEffect.Wave:
                SendMode(1);
                _wavePhase = 0;
                StartTimer(120, WaveTick);
                break;

            case LightingEffect.Flame:
                SendMode(1);
                _flamePhase = 0;
                StartTimer(120, FlameTick);
                break;

            case LightingEffect.Aurora:
                SendMode(1);
                _auroraPhase = 0;
                StartTimer(150, AuroraTick);
                break;

            case LightingEffect.GradientCycle:
                SendMode(1);
                _gradPhase = 0;
                StartTimer(120, GradientTick);
                break;

            case LightingEffect.Music:
                SendMode(1);
                _musicEnergy = 0;
                _lastBeatCount = Audio.BeatCount;
                StartTimer(50, MusicTick);
                break;

            case LightingEffect.Comet:
                SendMode(1);
                _cometPhase = 0;
                StartTimer(100, CometTick);
                break;
        }

        Logger.Info($"灯光效果已切换: {effect}");
    }

    private bool _strobeOn;
    private int _strobeIdx;
    private double _reactiveFade;
    private byte _reactiveR, _reactiveG, _reactiveB;
    private long _lastKeyPress;

    public void OnKeyPress()
    {
        if (_currentEffect != LightingEffect.Reactive) return;
        _lastKeyPress = Environment.TickCount64;
        _reactiveFade = 1.0;
        _reactiveR = (byte)_rng.Next(256);
        _reactiveG = (byte)_rng.Next(256);
        _reactiveB = (byte)_rng.Next(256);
    }

    private void BreathingTick(object? state)
    {
        _breathPhase += 0.1;
        if (_breathPhase > Math.PI * 2) _breathPhase -= Math.PI * 2;

        double factor = (Math.Sin(_breathPhase) + 1.0) / 2.0;
        factor = factor * factor;

        // 渐变呼吸: 主色⇄副色之间呼吸过渡
        var (lr, lg, lb) = Lerp2(factor);
        SendColor(lr, lg, lb);
    }

    private void RainbowTick(object? state)
    {
        _hue += 3;
        if (_hue >= 360) _hue -= 360;

        var (r, g, b) = HsvToRgb(_hue, 1.0, 1.0);
        SendColor(r, g, b);
    }

    private void StrobeTick(object? state)
    {
        _strobeOn = !_strobeOn;
        if (_strobeOn)
        {
            // 每次闪烁轮换调色板颜色(取快照, 避免与漂移定时器并发)
            var pal = _palette;
            var c = pal[_strobeIdx % pal.Length];
            _strobeIdx++;
            SendColor(c.r, c.g, c.b);
        }
        else
        {
            SendColor(0, 0, 0);
        }
    }

    private void PulseTick(object? state)
    {
        _pulsePhase += 0.15;
        if (_pulsePhase > Math.PI * 2) _pulsePhase -= Math.PI * 2;

        double factor = (Math.Sin(_pulsePhase) + 1.0) / 2.0;
        factor = factor * factor;
        // 渐变脉冲: 主⇄副过渡叠加心跳亮度
        var (lr, lg, lb) = Lerp2(factor);
        double lum = 0.3 + 0.7 * factor;
        SendColor((byte)(lr * lum), (byte)(lg * lum), (byte)(lb * lum));
    }

    private void RandomTick(object? state)
    {
        // 持续随机: 每帧重新生成 2-4 色的随机调色板并沿其流动, 灯光一直在变
        int n = 2 + _rng.Next(3); // 2-4 色
        var colors = new (byte r, byte g, byte b)[n];
        for (int i = 0; i < n; i++)
            colors[i] = HsvToRgb(_rng.Next(360), 0.75 + 0.25 * _rng.NextDouble(), 0.75 + 0.25 * _rng.NextDouble());
        SetPalette(colors);
        _gradPhase += 0.012;
        if (_gradPhase >= 1.0) _gradPhase -= 1.0;
        var (cr, cg, cb) = PaletteLerp(_gradPhase);
        SendColor(cr, cg, cb);
    }

    private void ReactiveTick(object? state)
    {
        if (_reactiveFade > 0.01)
        {
            _reactiveFade *= 0.85;
            byte fr = (byte)(_reactiveR * _reactiveFade + _r * (1 - _reactiveFade));
            byte fg = (byte)(_reactiveG * _reactiveFade + _g * (1 - _reactiveFade));
            byte fb = (byte)(_reactiveB * _reactiveFade + _b * (1 - _reactiveFade));
            SendColor(fr, fg, fb);
        }
        else if (_reactiveFade > 0)
        {
            _reactiveFade = 0;
            SendColor(_r, _g, _b);
        }
    }

    // ===== 新增效果 =====

    private double _wavePhase;

    /// <summary>波浪: 主色⇄副色正弦往返, 叠加明度波动</summary>
    private void WaveTick(object? state)
    {
        _wavePhase += 0.12;
        if (_wavePhase > Math.PI * 2) _wavePhase -= Math.PI * 2;

        double t = (Math.Sin(_wavePhase) + 1.0) / 2.0;
        var (lr, lg, lb) = Lerp2(t);
        double vv = 0.75 + 0.25 * Math.Sin(_wavePhase * 2);
        SendColor((byte)(lr * vv), (byte)(lg * vv), (byte)(lb * vv));
    }

    private double _flamePhase;

    /// <summary>火焰: 红→橙→黄暖色随机抖动上升</summary>
    private void FlameTick(object? state)
    {
        _flamePhase += 0.18;
        double flicker = 0.15 * _rng.NextDouble();
        double t = (Math.Sin(_flamePhase) + 1.0) / 2.0 * 0.85 + flicker;
        // 色相 0(红)→45(橙黄)
        double h = t * 48;
        double v = 0.55 + 0.45 * (1.0 - Math.Abs(Math.Sin(_flamePhase * 1.7)));
        var (cr, cg, cb) = HsvToRgb(h, 1.0, Math.Min(1.0, v));
        SendColor(cr, cg, cb);
    }

    private double _auroraPhase;

    /// <summary>极光: 青(160)→紫(280)→蓝(220) 缓慢漂移</summary>
    private void AuroraTick(object? state)
    {
        _auroraPhase += 0.045;
        double h = (200 + 60 * Math.Sin(_auroraPhase) + 360) % 360;
        double s = 0.7 + 0.3 * Math.Sin(_auroraPhase * 0.7);
        double v = 0.75 + 0.25 * Math.Sin(_auroraPhase * 1.3);
        var (cr, cg, cb) = HsvToRgb(h, Math.Clamp(s, 0.4, 1.0), Math.Clamp(v, 0.35, 1.0));
        SendColor(cr, cg, cb);
    }

    private double _gradPhase;

    /// <summary>渐变循环: 沿多色调色板闭环流动</summary>
    private void GradientTick(object? state)
    {
        _gradPhase += 0.008;
        if (_gradPhase >= 1.0) _gradPhase -= 1.0;
        var (cr, cg, cb) = PaletteLerp(_gradPhase);
        SendColor(cr, cg, cb);
    }

    private double _musicEnergy;
    private int _lastBeatCount;

    /// <summary>模拟节拍时的能量(供UI电平表显示)</summary>
    public double SimulatedLevel => _musicEnergy;

    /// <summary>音乐律动: 电脑声音=节拍模式(每拍换色+脉冲), 外界声音=音量模式, 无采集=模拟节拍; 安静时静态微光</summary>
    private void MusicTick(object? state)
    {
        if (Audio.IsActive && Audio.Source == AudioSourceKind.SystemLoopback)
        {
            // 节拍模式: 不读音量, 跟鼓点一跳一跳
            double p = Audio.BeatPulse;
            // 拍点那一帧强制下发, 不被WMI节流吃掉(否则最快的鼓点会看不出来)
            bool newBeat = Audio.BeatCount != _lastBeatCount;
            _lastBeatCount = Audio.BeatCount;

            if (p < 0.02)
            {
                SendColor((byte)(_r * 0.15), (byte)(_g * 0.15), (byte)(_b * 0.15), newBeat);
                return;
            }

            var pal = _palette;                 // 快照: 漂移定时器会整体替换该数组
            int n = Math.Max(1, pal.Length);
            // 用 BeatCount 而不是 BeatIndex: BeatIndex 只在"正好落在节拍网格上"的正拍才递增,
            // 节奏不稳或不是强拍时颜色几乎不换, 听感上就像"音乐律动没有动态"。
            // 现在每一拍都换色, 网格(BeatPeriodMs)只负责算脉冲衰减速度。
            int idx = Audio.BeatCount;
            var cur = pal[((idx - 1) % n + n) % n];
            var nxt = pal[idx % n];
            // 拍点瞬间亮当前拍色, 衰减期间滑向下一拍色
            double t = 1 - p;
            byte cr = (byte)(cur.r * p + nxt.r * t);
            byte cg = (byte)(cur.g * p + nxt.g * t);
            byte cb = (byte)(cur.b * p + nxt.b * t);
            double flash = Math.Max(0, (p - 0.7) * 3.33) * 0.6; // 拍点白闪
            cr = (byte)(cr * (1 - flash) + 255 * flash);
            cg = (byte)(cg * (1 - flash) + 255 * flash);
            cb = (byte)(cb * (1 - flash) + 255 * flash);
            double lum = (0.18 + 0.82 * p) * PulseBrightnessCap; // 默认满亮度脉冲
            SendColor((byte)(cr * lum), (byte)(cg * lum), (byte)(cb * lum), newBeat);
            return;
        }

        double e;
        if (Audio.IsActive)
        {
            e = Math.Clamp(Audio.Level, 0, 1);
        }
        else
        {
            // 模拟鼓点: 约120BPM 每拍一次强脉冲, 衰减稍慢让肉眼看清跳动
            if (_rng.NextDouble() < 0.16) _musicEnergy = 0.75 + 0.25 * _rng.NextDouble();
            _musicEnergy *= 0.90;
            e = Math.Clamp(_musicEnergy, 0, 1);
        }

        // 安静时保持静态微光, 不做任何摆动(用户要求: 没声音就不动)
        if (e < 0.02)
        {
            SendColor((byte)(_r * 0.15), (byte)(_g * 0.15), (byte)(_b * 0.15));
            return;
        }

        // 能量沿调色板推进, 高能量冲白
        var (lr, lg, lb) = PaletteLerp(e * 0.999);
        double white = Math.Max(0, (e - 0.5) * 2);
        lr = (byte)(lr * (1 - white) + 255 * white);
        lg = (byte)(lg * (1 - white) + 255 * white);
        lb = (byte)(lb * (1 - white) + 255 * white);
        // 亮度随能量放大: 弱拍暗、强拍亮, 节拍感更明显; 受亮度上限约束
        double lum2 = (0.45 + 0.55 * Math.Min(1.0, e * 1.4)) * PulseBrightnessCap;
        lr = (byte)(lr * lum2);
        lg = (byte)(lg * lum2);
        lb = (byte)(lb * lum2);
        // 静默时保底微光
        double floorV = 0.12;
        lr = (byte)Math.Max(lr, _r * floorV);
        lg = (byte)Math.Max(lg, _g * floorV);
        lb = (byte)Math.Max(lb, _b * floorV);
        SendColor(lr, lg, lb);
    }

    private double _cometPhase;

    /// <summary>彗星: 底色沿调色板缓移, 周期性白色高光划过</summary>
    private void CometTick(object? state)
    {
        _cometPhase += 0.09;
        if (_cometPhase > Math.PI * 2) _cometPhase -= Math.PI * 2;
        double head = (Math.Sin(_cometPhase) + 1.0) / 2.0;
        head = head * head * head; // 收窄高亮峰
        var (br, bg, bb) = PaletteLerp(_cometPhase / (Math.PI * 2));
        byte cr = (byte)(br * (1 - head) + 255 * head);
        byte cg = (byte)(bg * (1 - head) + 255 * head);
        byte cb = (byte)(bb * (1 - head) + 255 * head);
        SendColor(cr, cg, cb);
    }

    private byte _sentR, _sentG, _sentB;
    private bool _hasSent;
    private int _sendBusy;

    /// <summary>发送颜色; 与上次已发送的完全一致时直接跳过, 静止画面不占用EC总线</summary>
    private void SendColor(byte r, byte g, byte b, bool force = false)
    {
        if (_wmi == null || !_wmi.IsAvailable) return;
        if (!force && _hasSent && r == _sentR && g == _sentG && b == _sentB) return;

        // 动画帧要有且只有一个在飞: 单次下发最长等 2.5s(桥超时), 远大于 50-150ms 的定时器周期,
        // 没有这道闸门时定时器回调会在线程池里越堆越多(每个已提交栈约 1MB)。
        // 用户主动操作(force)不受限, 它必须马上落地, 而且本身就是零星几次。
        if (!force && Interlocked.CompareExchange(ref _sendBusy, 1, 0) == 1) return;

        long now = Environment.TickCount64;
        if (!force)
        {
            if (now - _lastWmiCall < MIN_WMI_INTERVAL_MS)
            {
                Interlocked.Exchange(ref _sendBusy, 0);
                return;
            }
            _lastWmiCall = now;
        }

        bool ok = false;
        try { ok = _wmi.SetKeyboardColor(r, g, b); }
        catch { }
        finally
        {
            if (!force) Interlocked.Exchange(ref _sendBusy, 0);
        }

        // 只有真正下发成功才记进去重缓存。桥抖动/超时时如果也记成"已发送",
        // 之后完全相同的颜色会被永久跳过, 键盘停在旧色直到重启程序。
        if (ok)
        {
            _sentR = r; _sentG = g; _sentB = b; _hasSent = true;
        }
        else
        {
            _hasSent = false;
        }
    }

    /// <summary>用户主动操作(切换灯效/颜色)时使用, 绕过节流与去重确保立即生效</summary>
    private void SendColorNow(byte r, byte g, byte b)
    {
        _lastWmiCall = 0;
        SendColor(r, g, b, true);
    }

    /// <summary>立即闪显一帧颜色(不改变当前效果), 用于随机色等需要马上可见的操作</summary>
    public void FlashColor(byte r, byte g, byte b) => SendColorNow(r, g, b);

    private void SendMode(int mode)
    {
        if (_wmi == null || !_wmi.IsAvailable) return;
        try { _wmi.SetKeyboardMode(mode); } catch { }
    }

    private void StartTimer(int intervalMs, TimerCallback callback)
    {
        // 防御性: 任何路径下都不允许出现两个灯效定时器同时跑(会重复下发颜色、互相打架)
        StopTimer();
        // 解除节流并立即发送首帧: 切换灯效后键盘马上变化, 无需等待首个定时器周期或额外触发
        _lastWmiCall = 0;
        try { callback(null); } catch { }

        int gen = ++_effectGen;
        // 必须包一层 try/catch: System.Threading.Timer 回调里漏出的异常在 .NET 8 会直接终结进程
        _timer = new Timer(_ =>
        {
            if (gen != _effectGen) return;   // 已被更新的效果替换, 这个回调作废
            try { callback(null); }
            catch (Exception ex) { Logger.Error(ex, "灯效定时器回调异常"); }
        }, null, intervalMs, intervalMs);
    }

    private void StopTimer()
    {
        _effectGen++;        // 让在飞回调立即失效(Dispose 不会等待正在执行的回调)
        _timer?.Dispose();
        _timer = null;
    }

    // ===== 动态随机(平滑) =====
    // 在两套随机调色板之间做 12 秒的缓入缓出插值。单色模式下整块键盘是一个连续变化的颜色;
    // 渐变/音乐等效果则让调色板本身缓慢漂移, 由各自已有的定时器去取色。
    // 这样变化是"流淌"的, 不是每几秒硬跳一次。
    private Timer? _driftTimer;
    /// <summary>漂移定时器代次: 旧回调完成时不能去 Dispose 刚启动的新 timer</summary>
    private int _driftGen;
    private List<(byte r, byte g, byte b)> _driftFrom = new();
    private List<(byte r, byte g, byte b)> _driftTo = new();
    private double _driftPhase;
    private bool _driftGradient;
    private bool _driftOneShot;
    private int _driftTickMs = 60;
    private int _driftDurationMs = 18000;

    public bool RandomDriftActive => _driftTimer != null;

    /// <summary>
    /// 平滑过渡到指定调色板(一次性): 点"随机单色/随机渐变"时用它, 让颜色淡入到新配色,
    /// 而不是瞬间硬切。durationMs 约 1.2 秒。
    /// </summary>
    public void MorphPaletteTo(IReadOnlyList<(byte r, byte g, byte b)> target, int durationMs = 1200)
    {
        if (target == null || target.Count == 0) return;
        _driftFrom = _palette.ToList();
        if (_driftFrom.Count == 0) _driftFrom.Add((_r, _g, _b));
        _driftTo = target.ToList();
        _driftPhase = 0;
        _driftOneShot = true;
        _driftDurationMs = Math.Max(200, durationMs);
        _driftTickMs = 60;

        _driftTimer?.Dispose();
        int gen = ++_driftGen;
        _driftTimer = new Timer(_ => DriftTick(gen), null, _driftTickMs, _driftTickMs);
        Logger.Info($"调色板淡入启动: {_driftFrom.Count}→{_driftTo.Count} 色, {_driftDurationMs}ms, 步长{_driftTickMs}ms");
    }

    /// <summary>启动/重启动态随机。gradient=false 单色平滑变化, true 渐变调色板平滑漂移。</summary>
    /// <summary>
    /// 启动/重启动态随机。gradient=false 单色平滑变化, true 渐变调色板平滑漂移。
    /// firstTarget 不为空时, 第一段先淡入到它(用户刚点的预设), 之后才继续随机漂移 ——
    /// 否则"点随机 → 动态立刻又选了个新目标", 用户会觉得点了没用。
    /// </summary>
    public void StartRandomDrift(bool gradient, IReadOnlyList<(byte r, byte g, byte b)>? firstTarget = null)
    {
        _driftGradient = gradient;
        _driftFrom = _palette.ToList();
        if (_driftFrom.Count == 0) _driftFrom.Add((_r, _g, _b));
        _driftTo = firstTarget != null && firstTarget.Count > 0
            ? firstTarget.ToList()
            : NewRandomPalette(gradient);
        _driftPhase = 0;
        _driftOneShot = false;
        _driftDurationMs = firstTarget != null ? 1200 : 18000;
        _driftTickMs = 60;

        _driftTimer?.Dispose();
        int gen = ++_driftGen;
        _driftTimer = new Timer(_ => DriftTick(gen), null, _driftTickMs, _driftTickMs);
        Logger.Info($"动态随机启动: {(gradient ? "渐变" : "单色")}, 首段 {_driftDurationMs}ms{(firstTarget != null ? "(淡入所选颜色)" : "")}");
    }

    public void StopRandomDrift()
    {
        if (_driftTimer == null) return;
        _driftGen++;          // 在飞回调作废
        _driftTimer.Dispose();
        _driftTimer = null;
        Logger.Info("动态随机停止");
    }

    private List<(byte r, byte g, byte b)> NewRandomPalette(bool gradient)
    {
        var list = new List<(byte r, byte g, byte b)>();
        if (!gradient)
        {
            double hue = _rng.Next(360);
            double sat = 0.80 + 0.20 * _rng.NextDouble();
            double val = 0.85 + 0.15 * _rng.NextDouble();
            var a = HsvToRgb(hue, sat, val);
            var b = HsvToRgb(hue, sat, val * 0.55);
            list.Add((a.r, a.g, a.b));
            list.Add((b.r, b.g, b.b));
            return list;
        }

        int count = _rng.Next(2, 4);
        double baseHue = _rng.Next(360);
        double spread = 100 + _rng.Next(101);
        for (int i = 0; i < count; i++)
        {
            double h = (baseHue + spread * i / Math.Max(1, count - 1) + _rng.Next(-15, 16) + 720) % 360;
            var c = HsvToRgb(h, 0.80 + 0.20 * _rng.NextDouble(), 0.85 + 0.15 * _rng.NextDouble());
            list.Add((c.r, c.g, c.b));
        }
        return list;
    }

    private static byte Mix(byte a, byte b, double t) => (byte)(a + (b - a) * t);

    private void DriftTick(int gen)
    {
        if (gen != _driftGen) return;   // 已被替换/停止, 这个回调作废
        // 同上: 定时器回调不能抛, 否则进程直接退出
        try { DriftTickCore(); }
        catch (Exception ex) { Logger.Error(ex, "动态随机定时器回调异常"); }
    }

    private void DriftTickCore()
    {
        if (_disposed) return;

        _driftPhase += (double)_driftTickMs / _driftDurationMs;
        if (_driftPhase >= 1.0)
        {
            if (_driftOneShot)
            {
                ApplyDriftFrame(1.0);
                Logger.Info($"调色板淡入完成: #{_r:X2}{_g:X2}{_b:X2}");
                _driftGen++;                 // 同代次排队回调立即失效, 不能去动新 timer
                _driftTimer?.Dispose();
                _driftTimer = null;
                return;
            }

            // 下一段从"当前实际颜色"出发, 接缝处不会跳
            _driftFrom = _palette.ToList();
            if (_driftFrom.Count == 0) _driftFrom.Add((_r, _g, _b));
            _driftTo = NewRandomPalette(_driftGradient);
            _driftPhase = 0;
            _driftDurationMs = 18000;   // 只有"用户刚点预设"的那一段是短的, 之后都慢
        }

        ApplyDriftFrame(_driftPhase);
    }

    /// <summary>按 0..1 的进度把 from→to 插值写进调色板, 静态灯效顺手推一帧</summary>
    private void ApplyDriftFrame(double phase)
    {
        double t = phase * phase * (3 - 2 * phase); // smoothstep: 两端慢中间快
        var from = _driftFrom;              // 取一次快照: UI 线程可能整体替换这两个 List
        var to = _driftTo;
        int n = Math.Max(1, to.Count);

        // 整块新数组一次性替换(写时复制), 读者永远是完整一致的一份
        var next = new (byte r, byte g, byte b)[n];
        for (int i = 0; i < n; i++)
        {
            (byte r, byte g, byte b) f = from.Count > 0 ? from[Math.Min(i, from.Count - 1)] : (_r, _g, _b);
            var dst = to[i];
            next[i] = (Mix(f.r, dst.r, t), Mix(f.g, dst.g, t), Mix(f.b, dst.b, t));
        }
        _palette = next;

        (_r, _g, _b) = next[0];
        if (n > 1) (_r2, _g2, _b2) = next[1];

        // 只有静态灯效需要自己推帧; 其他效果各自的定时器会读这个正在漂移的调色板
        if (_currentEffect == LightingEffect.Static)
            SendColor(_r, _g, _b);
    }

    internal static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        byte p = (byte)(v * (1 - s) * 255);
        byte q = (byte)(v * (1 - f * s) * 255);
        byte t = (byte)(v * (1 - (1 - f) * s) * 255);
        byte vv = (byte)(v * 255);

        return hi switch
        {
            0 => (vv, t, p),
            1 => (q, vv, p),
            2 => (p, vv, t),
            3 => (p, q, vv),
            4 => (t, p, vv),
            _ => (vv, p, q)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
        StopRandomDrift();
        Audio.Dispose();
    }
}
