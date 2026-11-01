using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;

namespace MiniProLite;

// App 的生命周期等于进程：_mutex 在 OnExit 释放，_tray/_window 在各退出路径释放，
// 给 Application 套一层 IDisposable 反而会造出没人调用的假契约。
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application 由 WPF 托管，资源均在 OnExit/退出路径中释放")]
public partial class App : Application
{
    private const string MutexName = @"Global\MiniProLite.SingleInstance";

    private Mutex? _mutex;
    private PowerMonitor? _monitor;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式：不显示窗口，只读一次状态后退出（行为保持不变）
        if (HasFlag(e.Args, "--selftest"))
        {
            // 自检必须先退出进程：窗口还没建，用 Environment.Exit 保证退出码可控。
            var exitCode = RunSelfTest();
            Console.Out.Flush();
            Environment.Exit(exitCode);
            return;
        }

        // 单实例：第二个进程静默退出，不弹窗
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // --tray 供开机自启使用：不显示窗口，只读状态，不接管风扇（默认就是 EC 自动）
        var trayOnly = HasFlag(e.Args, "--tray");

        // 用局部变量拿到非 null 引用，避免字段可空性带来的警告
        var monitor = new PowerMonitor();
        _monitor = monitor;
        _window = new MainWindow(monitor);
        var tray = new TrayIcon(monitor, ShowWindow, ExitApplication);
        _tray = tray;
        monitor.Start();

        // --tray（开机自启）没有窗口，控制台也看不到，读取失败必须用托盘气泡告知一次。
        // 只在启动时弹一次，不要放进轮询里。
        if (trayOnly && monitor.Error is not null)
        {
            tray.NotifyError("MiniProLite 启动失败", monitor.Error);
        }
        else if (FanState.WasManualFull())
        {
            // 上次进程被强杀/断电，风扇可能还停在手动满转（EC 状态读不回来）：启动时提示一次
            tray.NotifyError("风扇可能仍在满转", FanState.ManualFullHint);
        }

        // 开机自启绑定的可能是旧的 exe 路径（用户换了安装目录）：后台校正一次，不阻塞 UI
        RepairAutoStartInBackground(trayOnly);

        if (!trayOnly)
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// 后台校正计划任务里的 exe 路径：只在任务已存在且路径不一致时才重建（内部 /F 覆盖），
    /// 不会擅自创建任务。只有 --tray 才弹失败气泡（普通启动有窗口，气泡容易被忽略）。
    /// </summary>
    private void RepairAutoStartInBackground(bool reportToBalloon)
    {
        Task.Run(() =>
        {
            string? error;
            try
            {
                error = AutoStart.Repair();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error is null || !reportToBalloon)
            {
                return;
            }

            try
            {
                // 托盘可能已随退出销毁，回 UI 线程后重新取字段
                var dispatcher = Dispatcher;
                if (dispatcher.HasShutdownStarted)
                {
                    return;
                }

                dispatcher.BeginInvoke(new Action(() => _tray?.NotifyError("开机自启修复失败", error)));
            }
            catch (InvalidOperationException)
            {
                // 程序已在退出，气泡没人看也无所谓
            }
        });
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private void ShowWindow() => _window?.ShowFromTray();

    /// <summary>只有托盘菜单「退出（交回 EC 自动）」会走到这里。</summary>
    private void ExitApplication()
    {
        _monitor?.ReleaseEcAndDispose();
        _tray?.Dispose();
        _tray = null;

        if (_window is not null)
        {
            _window.AllowRealClose = true;
            _window.Close();
            _window = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }

    /// <summary>
    /// 注销 / 关机 / 重启兜底：用户点 X 收进托盘后直接注销关机，风扇可能停在手动满转。
    /// 这里把风扇交回 EC 自动（与托盘「退出」同一条释放路径）。
    /// 任何异常一律吞掉，不 Shutdown、不弹窗，绝不阻塞或打断系统关机流程。
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            // 保留默认行为（事件订阅者仍可取消）；订阅者抛异常也不能打断关机
            base.OnSessionEnding(e);
        }
        catch
        {
            // 忽略：不能因为事件分发失败而影响系统关机
        }

        try
        {
            _monitor?.ReleaseEcAndDispose();
        }
        catch
        {
            // 交回 EC 失败也要让系统正常关机
        }

        // 关机/注销被取消时进程继续运行，把轮询恢复回来，避免监控停在已释放状态
        if (e.Cancel)
        {
            try
            {
                _monitor?.Start();
            }
            catch
            {
                // 恢复失败也只影响状态显示，下次轮询会重试
            }
        }
    }

    /// <summary>成功返回 0；WMI 失败打印异常并返回 2。</summary>
    private static int RunSelfTest()
    {
        var text = new StringBuilder();
        text.AppendLine("MiniProLite selftest");

        try
        {
            using var target = PowerSwitch.Open();
            var mode = PowerSwitch.GetPowerMode(target);
            var temperature = PowerSwitch.GetTemperature(target);
            var rpm = PowerSwitch.GetFanRpm(target);

            text.AppendLine(CultureInfo.InvariantCulture, $"模式: {PowerSwitch.ModeText(mode)} ({mode})");
            text.AppendLine(CultureInfo.InvariantCulture, $"温度: {(temperature is null ? "不可用" : temperature.Value + " °C")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"风扇: {rpm} RPM");
            text.AppendLine("结果: OK");
            Console.Out.Write(text.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"结果: 失败 - {ErrorText.Describe(ex)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"异常: {ex.GetType().Name}: {ex.Message}");
            Console.Out.Write(text.ToString());
            return 2;
        }
    }
}
