using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 从右上角飞入的操作提醒 Toast：透明、置顶、不抢焦点的独立小窗，内容是一个 FAUI InfoBar。
/// 每次展示新建一个窗口：复用窗口在 Hide 后重 Show 时 SizeToContent 高度不会重新测量
/// （第二次起高度坍缩、矮得看不见字），新建窗口 + 全新 InfoBar 可彻底避免。
/// 动画用 DispatcherTimer 逐帧驱动（Avalonia 的 Animation 在插件窗口不可靠），可被取消。
/// </summary>
internal sealed class ReminderToastWindow : Window
{
    private const double ToastWidth = 340;
    private const double MarginPx = 16;
    private readonly Border _card;
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private DispatcherTimer? _anim;

    public ReminderToastWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        _card = new Border
        {
            Margin = new Thickness(0, 0, MarginPx, MarginPx),
            CornerRadius = new CornerRadius(8),
            Background = ThemePalette.PanelBackground(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        // 初始在右侧外（滑入起点），透明不可见。
        _card.RenderTransform = new TranslateTransform { X = 30 };
        _card.Opacity = 0;
        Content = _card;
        _dismissTimer.Tick += (_, _) => Dismiss();
    }

    /// <summary>在宿主窗口右上角展示一条提醒；展示完自动 Close，宿主关闭时一并收起。</summary>
    public void ShowFor(Window host, string message)
    {
        // 每次全新构建 InfoBar：复用控件在窗口隐藏后重显示时，布局高度会残留/坍缩。
        var infoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = true,
            Message = message
        };
        infoBar.CloseButtonClick += (_, _) => Dismiss();
        _card.Child = infoBar;

        // 宿主（编辑器）关闭时收起本 Toast，避免遗留置顶小窗。
        host.Closed += (_, _) => Close();

        // 定位到宿主窗口右上角（换算屏幕坐标，自动含 DPI 缩放）。
        var screen = host.PointToScreen(new Point(Math.Max(0, host.Bounds.Width - ToastWidth - MarginPx), MarginPx));
        Position = new PixelPoint(screen.X, screen.Y);
        Show();
        _dismissTimer.Stop();
        FlyIn();
        _dismissTimer.Start();
    }

    private void FlyIn()
    {
        _anim?.Stop();
        SetCardState(0, 30);
        Animate(v =>
        {
            _card.Opacity = v;
            if (_card.RenderTransform is TranslateTransform t)
            {
                t.X = 30 * (1 - v);
            }
        }, 220);
    }

    private void Dismiss()
    {
        _dismissTimer.Stop();
        _anim?.Stop();
        Animate(v =>
        {
            _card.Opacity = 1 - v;
            if (_card.RenderTransform is TranslateTransform t)
            {
                t.X = 30 * v;
            }
        }, 180, Close);
    }

    private void SetCardState(double opacity, double x)
    {
        _card.Opacity = opacity;
        if (_card.RenderTransform is TranslateTransform t)
        {
            t.X = x;
        }
    }

    /// <summary>用 DispatcherTimer 逐帧驱动一次性动画（可取消），结束写终值后调用 onDone。</summary>
    private void Animate(Action<double> apply, double durationMs, Action? onDone = null)
    {
        _anim?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var started = Environment.TickCount64;
        var easing = new CubicEaseOut();
        timer.Tick += (_, _) =>
        {
            var p = Math.Min(1.0, (Environment.TickCount64 - started) / Math.Max(1.0, durationMs));
            apply(easing.Ease(p));
            if (p >= 1.0)
            {
                timer.Stop();
                onDone?.Invoke();
            }
        };
        _anim = timer;
        timer.Start();
    }
}
