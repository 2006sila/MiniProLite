using System.Globalization;
using System.Management;
using System.Windows.Threading;

namespace MiniProLite;

/// <summary>
/// WMI 会话与状态的唯一持有者：1 秒轮询、写入模式/风扇、退出时把风扇交回 EC。
/// 只在 UI 线程上使用，<see cref="Changed"/> 也在 UI 线程触发。
/// </summary>
internal sealed class PowerMonitor
{
    // 退避期的占位文案：只说明「这次没去读」，不代表真实故障
    private const string UnavailableText = "WMI 暂不可用，稍后重试";

    // 写入模式后的回读等待窗口：EC 的 FCMO 会滞后，写完立刻读会拿到上一个模式，
    // 所以只做有限时长的轮询（7 × 120ms 间隔 + 8 次读取 ≈ 1 秒上限）。
    private const int ModeReadbackAttempts = 8;
    private const int ModeReadbackIntervalMs = 120;

    // 「快速回自动」的手动保持时长（真机实测依据，别改小）
    // 1) 满转 100 → ~4000 RPM；直接写 101 交回 EC 后，EC 只肯按 ~15 RPM/s 慢降（4007→2414 用了 81 秒，到目标约 130 秒）。
    // 2) 只手动写低占空比 1 秒再交回 101 → 无效：转速仍按 15 RPM/s 慢降。
    //    （~90 RPM/s 是「持续」手动低占空比时风扇本体的响应，时间常数约 20 秒；EC 自动模式自己只肯走 15 RPM/s。）
    // 3) 手动保持 15 秒则有效：duty=40 保持 15s → 4007→2619（~93 RPM/s），再交回 101 后第 35 秒到 2039（已落到 EC 目标），温度未见上升。
    private const int FastReturnHoldSeconds = 15;

    // 保持期的 Tick 周期：每秒刷新温度/转速并累计保持时间。
    private const int FastReturnTickSeconds = 1;

    // 温度守卫：保持期间手动值会压着散热，温度一涨就得立刻交回 EC。
    // 相对守卫用「起始温度 + 5°C」（实测保持 15 秒温度未见上升，涨 5°C 说明异常），
    // 绝对守卫用 88°C（EC 曲线在 87–89 已经给到 79% duty，再高就不该由手动值压着）。
    private const int FastReturnGuardDeltaCelsius = 5;
    private const int FastReturnGuardMaxCelsius = 88;

    // 占空比 → 转速的线性近似：实测 100%→4030、50%→2536、40%→~2234 RPM，duty*40 够用。
    private const int RpmPerDuty = 40;

    // “本来就不快”的判定余量：转速 ≤ 估算值 + 300 时跳过手动降速，直接交回 EC。
    private const int RpmSlack = 300;

    // 快速回自动的目标占空比下限：任何情况下不低于 40%，避免过度降冷。
    private const int MinAutoDuty = 40;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // 快速回自动的保持期定时器：每 1 秒 Tick 一次，累计保持到 15 秒（或触发温度守卫）才写 101。
    // 用 DispatcherTimer 而不是 Thread.Sleep，保证保持期间不阻塞 UI 线程；
    // 本类只在 UI 线程上使用，Tick 也回到 UI 线程，不会与写操作并发。
    private readonly DispatcherTimer _fastReturnTimer = new()
    {
        Interval = TimeSpan.FromSeconds(FastReturnTickSeconds),
    };

    private ManagementObject? _target;
    private bool _released;

    // 快速回自动：手动低占空比是否还在保持中（用户中途点了别的东西时用它取消）
    private bool _fastReturnPending;

    // 保持起点温度（相对守卫的基准）与已累计的保持秒数
    private int _fastReturnStartTemperature;
    private int _fastReturnHeldSeconds;

    // 打开 WMI 失败后的退避截止时间（UTC）：在此之前不再重试，避免每秒刷一次“找不到实例”。
    private DateTime _retryAfterUtc;

    public PowerMonitor()
    {
        // Tick 只能订阅一次：Start() 可能在关机被取消后再次调用，重复订阅会导致每秒多次轮询
        _timer.Tick += (_, _) => Refresh();
        _fastReturnTimer.Tick += (_, _) => TickFastReturn();
    }

    /// <summary>当前性能模式；null 表示还没读到。</summary>
    public byte? Mode { get; private set; }

    /// <summary>温度（摄氏度）；null 表示不可用。</summary>
    public int? Temperature { get; private set; }

    /// <summary>风扇转速；null 表示还没读到。</summary>
    public ushort? Rpm { get; private set; }

    /// <summary>
    /// 风扇控制状态：true=EC 自动，false=满转。
    /// WMI 读不回来（SetFanControl 没有对应的读法），只能记在内存里。
    /// 注意：快速回自动的保持期内它也保持原值（目的地是自动），展示请用 <see cref="FanControlText"/>。
    /// </summary>
    public bool FanAuto { get; private set; } = true;

    /// <summary>
    /// 快速回自动是否正在保持手动低占空比（目的地仍是 EC 自动）。
    /// 托盘菜单据此把勾打在「EC 自动」上，展示文案显示「回落中」。
    /// </summary>
    public bool FanReturningToAuto => _fastReturnPending;

    /// <summary>最近一次的错误文本；null 表示状态正常。</summary>
    public string? Error { get; private set; }

    public event EventHandler? Changed;

    /// <summary>先读一次，再开始每秒轮询。</summary>
    public void Start()
    {
        // 释放后（例如关机被取消）再启动时重新允许释放
        _released = false;
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>
    /// 懒打开的 WMI 对象。绑定失效时由 <see cref="Refresh"/> 置空后重建；
    /// 打开失败进入 5 秒退避期，期间直接报“稍后重试”而不去碰 WMI。
    /// </summary>
    private ManagementObject Target
    {
        get
        {
            if (_target is not null)
            {
                return _target;
            }

            if (DateTime.UtcNow < _retryAfterUtc)
            {
                throw new WmiUnavailableException();
            }

            try
            {
                _target = PowerSwitch.Open();
            }
            catch
            {
                _retryAfterUtc = DateTime.UtcNow.AddSeconds(5);
                throw;
            }

            _retryAfterUtc = default;
            return _target;
        }
    }

    /// <summary>读一次状态并通知界面；失败只记录错误文本，下次轮询会重试。</summary>
    public void Refresh()
    {
        try
        {
            var target = Target;
            Temperature = PowerSwitch.GetTemperature(target);
            Rpm = PowerSwitch.GetFanRpm(target);
            Mode = PowerSwitch.GetPowerMode(target);
            Error = null;
        }
        catch (Exception ex)
        {
            if (ex is WmiUnavailableException)
            {
                // 退避期只是「没去读」，不能用来冲掉上一次读到的真实错误
                // （如「访问被拒绝：请以管理员身份运行」），否则文案会每 5 秒抖动一次。
                Error ??= UnavailableText;
            }
            else
            {
                Error = ErrorText.Describe(ex);
            }

            // 绑定可能已经失效（驱动重载、设备复位）：丢掉旧对象，下次轮询重新打开，
            // 否则会把一个坏对象永久复用下去。
            try
            {
                _target?.Dispose();
            }
            catch (ManagementException)
            {
                // 已失效的对象释放失败无需处理，置空后下次重建
            }
            finally
            {
                _target = null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 写入性能模式。写被 EC 拒绝时返回 false 并填 Error；
    /// 写成功后轮询回读等待 EC 跟上，回读超时不报错（见方法内注释）。
    /// </summary>
    public bool TrySetMode(byte mode)
    {
        try
        {
            PowerSwitch.SetPowerMode(Target, mode);
        }
        catch (Exception ex)
        {
            // 包含 EnsureOk 判定的 EC 拒绝：这是确定的写入失败，必须报错
            Error = ErrorText.Describe(ex);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        // 写操作是否成功已由 SetPowerMode 的 ResultStatus/ReturnValue（EnsureOk）确认。
        // 下面的回读只用来把界面上的 Mode 刷成最新值，不再作为成败判定依据：
        // 实测 EC 的 FCMO（GetPowerMode 的返回值）在 SetPowerMode 之后不会立刻更新，
        // 会滞后一步 —— 连续切换时读到的是上一个模式（写 1 读回 0、写 2 读回 1）。
        // 若像以前那样回读不一致就报错，用户每次点按钮都会看到「模式切换未生效」这种假失败。
        for (var attempt = 1; attempt <= ModeReadbackAttempts; attempt++)
        {
            byte readback;
            try
            {
                readback = PowerSwitch.GetPowerMode(Target);
            }
            catch (Exception ex)
            {
                // 轮询期间读失败（WMI 掉线、权限被收回）：记录到 Error 后返回，不把异常抛给 UI
                Error = ErrorText.Describe(ex);
                Changed?.Invoke(this, EventArgs.Empty);
                return false;
            }

            // Refresh() 统一把 Mode/Temperature/Rpm 刷到字段并触发 Changed（状态行、托盘勾选要跟着动）。
            // 它成功时会把 Error 清成 null，这也正是这里想要的：写已经成功，不该留着上一次的错误。
            Refresh();

            if (readback == mode)
            {
                // EC 已经跟上，提前结束等待
                return true;
            }

            if (attempt < ModeReadbackAttempts)
            {
                // 只在这个按钮点击路径上短暂阻塞，总时长受 ModeReadbackAttempts 限制（≈1 秒）
                Thread.Sleep(ModeReadbackIntervalMs);
            }
        }

        // 轮询超时仍不一致：EC 只是还没刷新 FCMO（写操作本身是成功的），
        // 继续报错就会变成假失败，所以按成功返回，并让 Error 保持 null。
        // Mode 字段保留最后一次回读值，下一秒的定时 Refresh() 会继续把它追平。
        Error = null;
        return true;
    }

    /// <summary>写入风扇占空比，成功后立即回读。失败返回 false 并填 Error。</summary>
    /// <remarks>
    /// 风扇做不了回读校验：SetFanControl 只能从 GetFanSpeed 读回 RPM（低 16 位），
    /// 读不到“EC 当前是手动还是自动”，没有可用来和写入值比对的状态。
    /// </remarks>
    public bool TrySetFanDuty(byte duty, bool auto)
    {
        // 用户明确指定了占空比（满转 / 普通交回）：排队中的快速回自动不能再覆盖它
        CancelPendingFastReturn();

        try
        {
            PowerSwitch.SetFanDuty(Target, duty);
            FanAuto = auto;
            // 记盘：进程被强杀/断电时，下次启动只能靠这个标记知道风扇可能还停在手动满转
            FanState.Save(manualFull: !auto);
        }
        catch (Exception ex)
        {
            Error = ErrorText.Describe(ex);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>
    /// 快速回自动：先把占空比手动压到“EC 在当前温度下本来会给的值”并保持约 15 秒，再写 101 交回 EC。
    /// </summary>
    /// <remarks>
    /// 为什么要「保持 15 秒」而不是写完立刻交回（真机实测，别轻易改回一步／一秒）：
    /// 1) 直接写 101 交回 EC 后，EC 自己降速极慢：约 15 RPM/s，4030→2200 要 130 秒；
    /// 2) 只手动写低占空比 1 秒再交回 101 → 无效，转速仍按 EC 的 15 RPM/s 慢降（4007→2414 用了 81 秒）；
    /// 3) 手动保持 15 秒则有效：duty=40 保持 15s → 4007→2619（~93 RPM/s），交回后第 35 秒到 2039（落到 EC 目标），温度未见上升。
    ///    对比纯 101 的 130 秒：130s → 35s。
    /// 保持期间有温度守卫（见 <see cref="TickFastReturn"/>），温度一涨就立刻交回，不让手动值压着散热。
    /// 若当前转速本来就不高（≤ 估算值 + <see cref="RpmSlack"/>），手动跳变没有收益，直接写 101。
    /// 退出/关机路径不适用本方法：<see cref="ReleaseEcAndDispose"/> 仍然直接写 101，退出要快。
    /// </remarks>
    public bool ReturnToAutoFast()
    {
        // 上一次的快速回自动可能还在保持：先取消（会把风扇交回 101），避免旧 Tick 把这次的新决定冲掉
        CancelPendingFastReturn();

        if (Temperature is not { } temperature)
        {
            // 读不到温度就没法估目标占空比：退回普通 101 路径
            return TrySetFanDuty(PowerSwitch.FanEcAuto, auto: true);
        }

        var targetDuty = AutoDuty(temperature);
        var estimatedRpm = targetDuty * RpmPerDuty;

        // 本来就不快（转速已经在目标附近）：直接交回 EC，不做无意义的手动跳变。
        // 转速未知时按“可能很快”处理，走下面保持路径（目标占空比本来就是 EC 该给的值，不会过度降冷）。
        if (Rpm is { } rpm && rpm <= estimatedRpm + RpmSlack)
        {
            return TrySetFanDuty(PowerSwitch.FanEcAuto, auto: true);
        }

        // 第一步：直接把目标占空比写给 EC，但**不改** FanAuto、也**不落盘**。
        // 这只是「让转速快速掉下来」的过渡，最终状态仍然是 EC 自动；
        // 如果用 TrySetFanDuty(..., auto:false)，状态行和托盘勾选会在保持期里闪成“满转”，
        // FanState 也会被写成 manualFull=true 再写回来（脏写 + 视觉抖动）。
        try
        {
            PowerSwitch.SetFanDuty(Target, targetDuty);
        }
        catch (Exception ex)
        {
            Error = ErrorText.Describe(ex);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        // 第二步：保持目标占空比 FastReturnHoldSeconds 秒，由 TickFastReturn 计时并交回 EC。
        _fastReturnStartTemperature = temperature;
        _fastReturnHeldSeconds = 0;
        _fastReturnPending = true;
        _fastReturnTimer.Start();

        Refresh();
        return true;
    }

    /// <summary>
    /// 保持期每秒一次：刷新温度/转速、检查温度守卫、累计保持时间，到点或触发守卫就写 101 交回 EC。
    /// </summary>
    private void TickFastReturn()
    {
        if (!_fastReturnPending)
        {
            _fastReturnTimer.Stop();
            return;
        }

        if (_released)
        {
            // 退出路径已经写过 101 并释放了 WMI 对象，这里不能再碰 _target
            _fastReturnTimer.Stop();
            _fastReturnPending = false;
            _fastReturnHeldSeconds = 0;
            return;
        }

        // 刷新温度/转速（顺带触发 Changed，界面上的转速在保持期内也会动）
        Refresh();

        // 温度守卫：相对涨 5°C 或直接到 88°C 就立即交回，别让手动占空比压着散热。
        // 温度读不到（null）时不判定，保持计时继续走到 15 秒结束。
        if (Temperature is { } temperature
            && (temperature >= _fastReturnStartTemperature + FastReturnGuardDeltaCelsius
                || temperature >= FastReturnGuardMaxCelsius))
        {
            FinishReturnToAutoFast();
            return;
        }

        _fastReturnHeldSeconds += FastReturnTickSeconds;
        if (_fastReturnHeldSeconds >= FastReturnHoldSeconds)
        {
            FinishReturnToAutoFast();
        }
    }

    /// <summary>保持结束：写 101 交回 EC，并把最终状态固定为自动。</summary>
    private void FinishReturnToAutoFast()
    {
        _fastReturnTimer.Stop();

        if (!_fastReturnPending)
        {
            return;
        }

        _fastReturnPending = false;
        _fastReturnHeldSeconds = 0;

        if (_released)
        {
            // 退出路径已经直接写过 101 并释放了 WMI 对象，这里不能再碰 _target
            return;
        }

        // 直接写 101 而不是走 TrySetFanDuty：那里面会再调 CancelPendingFastReturn（此刻已无意义），
        // 而且这里需要区分“写成功才落盘 manualFull:false”。
        try
        {
            PowerSwitch.SetFanDuty(Target, PowerSwitch.FanEcAuto);
            FanAuto = true;
            FanState.Save(manualFull: false);
        }
        catch (Exception ex)
        {
            // 交回失败：风扇还停在这个手动占空比上，界面必须报错；
            // 落盘标记保留“手动”，下次启动提示用户点「EC 自动」，避免谎报自动。
            Error = ErrorText.Describe(ex);
            FanState.Save(manualFull: true);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Refresh();
    }

    /// <summary>
    /// 取消还在保持中的快速回自动（用户改主意 / 退出时）。
    /// 取消时风扇还停在手动占空比上：先写 101 交回 EC，不留手动状态。
    /// </summary>
    private void CancelPendingFastReturn()
    {
        if (!_fastReturnPending)
        {
            return;
        }

        _fastReturnPending = false;
        _fastReturnHeldSeconds = 0;
        _fastReturnTimer.Stop();

        // 退出路径（_released）由 ReleaseEcAndDispose 自己写 101，这里不重复碰 WMI
        if (_released)
        {
            return;
        }

        try
        {
            PowerSwitch.SetFanDuty(Target, PowerSwitch.FanEcAuto);
        }
        catch
        {
            // 交回失败不在这里报错：调用方（写满转 / 退出）紧接着就会自己写风扇并给出错误
        }
    }

    /// <summary>
    /// 温度 → EC 自动曲线的近似目标占空比（来自真机实测）：
    /// ≤78°C→40、79–80→50、81–85→55、86→63、87–89→79、≥90→90，最终 clamp 到 40..100。
    /// </summary>
    private static byte AutoDuty(int temperatureCelsius)
    {
        var duty = temperatureCelsius switch
        {
            <= 78 => 40,
            <= 80 => 50,
            <= 85 => 55,
            86 => 63,
            <= 89 => 79,
            _ => 90,
        };

        // 不低于 40（不过度降冷），也不高于 100（满转），防止上面的表被改错
        return (byte)Math.Clamp(duty, MinAutoDuty, PowerSwitch.FanFull);
    }

    /// <summary>
    /// 退出专用：先把风扇交回 EC（失败吞掉，不能因此卡住退出），再释放 WMI 对象。
    /// 幂等：重复调用（托盘退出 + 系统会话结束）不会重复写风扇，也不会抛异常。
    /// </summary>
    public void ReleaseEcAndDispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _timer.Stop();
        // 退出要快：不搞快速回落，取消排队中的第二步，下面直接写 101
        CancelPendingFastReturn();
        try
        {
            if (_target is not null)
            {
                PowerSwitch.SetFanDuty(_target, PowerSwitch.FanEcAuto);
                // 写 101 成功才认为风扇已交回 EC；失败则保留「手动满转」标记，下次启动继续提示
                FanState.Save(manualFull: false);
            }
        }
        catch
        {
            // 交回 EC 失败也要正常退出
        }
        finally
        {
            _target?.Dispose();
            _target = null;
            FanAuto = true;
        }
    }

    /// <summary>风扇控制的中文状态：自动 / 满转 / 回落中（快速回自动的保持期）。</summary>
    public string FanControlText => _fastReturnPending
        ? "回落中"
        : FanAuto ? "自动" : "满转";

    /// <summary>窗口状态行。</summary>
    public string StatusLine => Error is not null
        ? $"错误：{Error}"
        : $"温度 {Value(Temperature)}°C · 风扇 {Value(Rpm)} RPM · 模式 {ModeText()} · 风扇控制 {FanControlText}";

    /// <summary>托盘菜单第一项与 tooltip 用的短状态。</summary>
    public string BriefStatus => Error is not null
        ? "读取失败"
        : $"{Value(Temperature)}°C · {Value(Rpm)} RPM · {ModeText()} · {FanControlText}";

    /// <summary>
    /// 退避期内拒绝打开 WMI 时的异常。只在本类内部用来区分「没去读」和「读了但失败」，
    /// 不影响 <see cref="Error"/> 里的真实错误。
    /// </summary>
    private sealed class WmiUnavailableException : Exception
    {
        public WmiUnavailableException()
            : base(UnavailableText)
        {
        }
    }

    private string ModeText() => Mode is null ? "--" : PowerSwitch.ModeText(Mode.Value);

    private static string Value(int? value) => value is null ? "--" : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Value(ushort? value) => value is null ? "--" : value.Value.ToString(CultureInfo.InvariantCulture);
}
