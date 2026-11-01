using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MiniProLite;

public partial class MainWindow : Window
{
    private static readonly Brush ActiveBackground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
    private static readonly Brush ErrorForeground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    private readonly PowerMonitor _monitor;
    private readonly EventHandler _changedHandler;

    internal MainWindow(PowerMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        _changedHandler = (_, _) => Render();
        _monitor.Changed += _changedHandler;
        Render();
    }

    /// <summary>真正退出前置位，否则 OnClosing 会一直把窗口收进托盘。</summary>
    public bool AllowRealClose { get; set; }

    /// <summary>从托盘重新显示窗口。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void Render()
    {
        StatusText.Foreground = _monitor.Error is null ? SystemColors.ControlTextBrush : ErrorForeground;
        StatusText.Text = _monitor.StatusLine;
        Highlight(SilentButton, _monitor.Mode == PowerSwitch.ModeSilent);
        Highlight(BalancedButton, _monitor.Mode == PowerSwitch.ModeBalanced);
        Highlight(PerformanceButton, _monitor.Mode == PowerSwitch.ModePerformance);
    }

    private static void Highlight(Button button, bool active)
    {
        button.Background = active ? ActiveBackground : SystemColors.ControlBrush;
        button.Foreground = active ? Brushes.White : SystemColors.ControlTextBrush;
        button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
    }

    private void SilentButton_Click(object sender, RoutedEventArgs e) => _monitor.TrySetMode(PowerSwitch.ModeSilent);

    private void BalancedButton_Click(object sender, RoutedEventArgs e) => _monitor.TrySetMode(PowerSwitch.ModeBalanced);

    private void PerformanceButton_Click(object sender, RoutedEventArgs e) => _monitor.TrySetMode(PowerSwitch.ModePerformance);

    private void FanFullButton_Click(object sender, RoutedEventArgs e) => _monitor.TrySetFanDuty(PowerSwitch.FanFull, auto: false);

    // 交回 EC 自动走「快速回自动」（先写低占空比再写 101）：EC 自己降速只有约 15 RPM/s，
    // 手动占空比降速约 90 RPM/s，详见 PowerMonitor.ReturnToAutoFast。满转按钮不变，仍直接写 100。
    private void FanAutoButton_Click(object sender, RoutedEventArgs e) => _monitor.ReturnToAutoFast();

    protected override void OnClosing(CancelEventArgs e)
    {
        // X 按钮只收进托盘；风扇状态保持不变，彻底退出走托盘菜单「退出（交回 EC 自动）」
        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 窗口已经关掉就不再需要每秒重绘，退订避免 PowerMonitor 长期持有已销毁的窗口
        _monitor.Changed -= _changedHandler;
        base.OnClosed(e);
    }
}
