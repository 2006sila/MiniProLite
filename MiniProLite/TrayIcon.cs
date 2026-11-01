using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;

namespace MiniProLite;

/// <summary>
/// 系统托盘图标与菜单。所有 WinForms 类型都写在 <c>using</c> 之后，
/// 本文件不引用 WPF 类型（DispatcherTimer 用全限定名），避免同名类型冲突。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string ProductName = "MiniProLite";

    private readonly PowerMonitor _monitor;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _menuTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu;
    private readonly EventHandler _changedHandler;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _silentItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _performanceItem;
    private readonly ToolStripMenuItem _fanFullItem;
    private readonly ToolStripMenuItem _fanAutoItem;
    private readonly ToolStripMenuItem _autoStartItem;

    // 开机自启的勾选状态是异步查出来的，先缓存
    private bool _autoStartEnabled;
    private bool _autoStartBusy;

    public TrayIcon(PowerMonitor monitor, Action showWindow, Action exit)
    {
        _monitor = monitor;
        _showWindow = showWindow;
        _exit = exit;
        // 处理器要存字段，Dispose 时才能退订（否则 PowerMonitor 会一直持有已销毁的托盘对象）
        _changedHandler = (_, _) => Render();

        _statusItem = new ToolStripMenuItem("读取中…") { Enabled = false };

        var modeMenu = new ToolStripMenuItem("性能模式");
        _silentItem = AddModeItem(modeMenu, "静音", PowerSwitch.ModeSilent);
        _balancedItem = AddModeItem(modeMenu, "均衡", PowerSwitch.ModeBalanced);
        _performanceItem = AddModeItem(modeMenu, "性能", PowerSwitch.ModePerformance);

        var fanMenu = new ToolStripMenuItem("风扇");
        _fanFullItem = AddFanItem(fanMenu, "满转 100%", PowerSwitch.FanFull, auto: false);
        _fanAutoItem = AddFanItem(fanMenu, "EC 自动", PowerSwitch.FanEcAuto, auto: true);

        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = false };
        _autoStartItem.Click += (_, _) => ToggleAutoStart();

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(modeMenu);
        _menu.Items.Add(fanMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("打开窗口", null, (_, _) => _showWindow()));
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出（交回 EC 自动）", null, (_, _) => _exit()));
        // 打开菜单时立刻刷新勾选，避免看到过期状态
        _menu.Opening += (_, _) => RefreshAutoStartDisplay();

        _notify = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = ProductName,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => _showWindow();

        _monitor.Changed += _changedHandler;

        // 菜单打开期间每 3 秒复查一次计划任务是否存在
        _menuTimer.Tick += (_, _) =>
        {
            if (_menu.Visible)
            {
                RefreshAutoStartDisplay();
            }
        };
        _menuTimer.Start();

        Render();
        RefreshAutoStartDisplay();
    }

    /// <summary>
    /// 托盘图标：盾牌已换成应用图标（与 exe 图标同一份 app.ico）。
    /// 优先取 WPF 嵌入式资源并按系统托盘尺寸选最合适的一帧；
    /// 取不到时回落到 exe 自身图标，最后才是系统默认应用图标。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
            if (res?.Stream is { } stream)
            {
                // 必须 Clone：原 Icon 释放后 handle 会失效，返回的实例由 NotifyIcon 持有
                using (var tmp = new Icon(stream, SystemInformation.SmallIconSize))
                {
                    return (Icon)tmp.Clone();
                }
            }
        }
        catch
        {
            // 资源未嵌入或没有匹配尺寸的帧，走下面的回落
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var exeIcon = Icon.ExtractAssociatedIcon(path);
                if (exeIcon is not null)
                {
                    return exeIcon;
                }
            }
        }
        catch
        {
            // 拿不到 exe 图标就直接用系统默认
        }

        return SystemIcons.Application;
    }

    private ToolStripMenuItem AddModeItem(ToolStripMenuItem parent, string text, byte mode)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) => ApplyMode(mode);
        parent.DropDownItems.Add(item);
        return item;
    }

    private ToolStripMenuItem AddFanItem(ToolStripMenuItem parent, string text, byte duty, bool auto)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) => ApplyFan(duty, auto);
        parent.DropDownItems.Add(item);
        return item;
    }

    private void ApplyMode(byte mode)
    {
        if (!_monitor.TrySetMode(mode))
        {
            ShowBalloon("切换性能模式失败", _monitor.Error ?? "未知错误", ToolTipIcon.Error);
        }
    }

    private void ApplyFan(byte duty, bool auto)
    {
        // 交回 EC 自动走「快速回自动」：先手动压到目标占空比并保持约 15 秒再写 101，
        // 否则 EC 自己降速只有约 15 RPM/s（见 ReturnToAutoFast 的注释）。满转仍直接写 100。
        var ok = auto ? _monitor.ReturnToAutoFast() : _monitor.TrySetFanDuty(duty, auto: false);
        if (!ok)
        {
            ShowBalloon("切换风扇控制失败", _monitor.Error ?? "未知错误", ToolTipIcon.Error);
        }
    }

    /// <summary>状态项、tooltip 与各项打勾。</summary>
    private void Render()
    {
        var brief = _monitor.BriefStatus;
        _statusItem.Text = brief;
        _notify.Text = Truncate($"{ProductName} · {brief}", 63);

        _silentItem.Checked = _monitor.Mode == PowerSwitch.ModeSilent;
        _balancedItem.Checked = _monitor.Mode == PowerSwitch.ModeBalanced;
        _performanceItem.Checked = _monitor.Mode == PowerSwitch.ModePerformance;

        // 快速回自动的保持期内目的地仍是 EC 自动，所以勾要停在「EC 自动」上，不能勾「满转」
        var autoTarget = _monitor.FanReturningToAuto || _monitor.FanAuto;
        _fanFullItem.Checked = !autoTarget;
        _fanAutoItem.Checked = autoTarget;
    }

    /// <summary>Windows 托盘 tooltip 长度有限制，超了会抛异常。</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = text;
        _notify.BalloonTipIcon = icon;
        _notify.ShowBalloonTip(5000);
    }

    /// <summary>对外暴露一个错误气泡入口（只在启动失败等一次性场景调用，不要放进轮询里）。</summary>
    public void NotifyError(string title, string text) => ShowBalloon(title, text, ToolTipIcon.Error);

    /// <summary>勾选/取消勾选开机自启；失败时弹气泡并把勾选回滚成真实状态。</summary>
    private void ToggleAutoStart()
    {
        if (_autoStartBusy)
        {
            return;
        }

        _autoStartBusy = true;
        var enable = !_autoStartEnabled;
        RunInBackground(
            () =>
            {
                var error = enable ? AutoStart.Enable() : AutoStart.Disable();
                // 回读真实状态：失败时勾选自然回滚
                return new AutoStartResult(error, AutoStart.IsEnabled());
            },
            result => FinishAutoStartToggle(result.Error, result.Enabled));
    }

    /// <summary>重读计划任务是否存在并更新勾选（菜单打开时/菜单打开期间定时调用）。</summary>
    private void RefreshAutoStartDisplay()
    {
        if (_autoStartBusy)
        {
            return;
        }

        _autoStartBusy = true;
        RunInBackground(
            () => new AutoStartResult(null, AutoStart.IsEnabled()),
            result => FinishAutoStartToggle(result.Error, result.Enabled));
    }

    private void FinishAutoStartToggle(string? error, bool enabled)
    {
        _autoStartBusy = false;
        _autoStartEnabled = enabled;
        _autoStartItem.Checked = enabled;

        if (error is not null)
        {
            ShowBalloon("开机自启切换失败", error, ToolTipIcon.Error);
        }
    }

    private readonly record struct AutoStartResult(string? Error, bool Enabled);

    /// <summary>后台跑 schtasks（可能阻塞几百毫秒），完成后回到 UI 线程。</summary>
    private void RunInBackground<T>(Func<T> work, Action<T> done)
    {
        Task.Run(() =>
        {
            try
            {
                var value = work();
                _dispatcher.BeginInvoke(new Action(() => done(value)));
            }
            catch (Exception ex)
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    _autoStartBusy = false;
                    ShowBalloon("开机自启操作失败", ex.Message, ToolTipIcon.Error);
                }));
            }
        });
    }

    public void Dispose()
    {
        _menuTimer.Stop();
        _monitor.Changed -= _changedHandler;
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
    }
}
