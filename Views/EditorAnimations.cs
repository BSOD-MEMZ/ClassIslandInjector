using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClassIslandInjector.Views;

/// <summary>
/// 图层编辑器的统一动画辅助（非线性动画）。
/// 节奏约定：快速利落——入场 150–250ms，交互反馈 120–160ms。
/// 缓动约定：入场用 BackEase 弹性回弹（Entrance），交互反馈用 CubicEase 柔滑（Interaction）。
///
/// 实现说明（重要）：本类**不用 Avalonia 的 Animation/RunAsync**——该机制在本插件窗口
/// 环境下实测不可靠（工具栏按钮的错峰动画不执行、文本闪动）。改用 <see cref="DispatcherTimer"/>
/// 手动逐帧插值：每帧 16ms 按缓动曲线计算进度并直接写本地值，完全确定性、UI 线程内执行，
/// 结束帧把终值写入本地（控件绝不会停留在初值）。
///
/// 位移/缩放作用在控件 RenderTransform 里的变换对象上；<see cref="Ensure{T}"/> 保证每种
/// 类型变换在顶层只有一份，按压缩放与入场缩放共享同一个 ScaleTransform，互不冲突。
/// 调用方应在窗口显示前先把控件置于「初值」本地状态（如 Opacity=0、偏移变换），避免首帧闪烁。
/// </summary>
internal static class EditorAnimations
{
    /// <summary>入场弹性缓动（BackEase 轻微回弹，非线性）。</summary>
    internal static readonly Easing Entrance = new BackEaseOut();

    /// <summary>交互柔滑缓动（CubicEase，非线性）。</summary>
    internal static readonly Easing Interaction = new CubicEaseOut();

    /// <summary>入场时长（快速利落）。</summary>
    internal static readonly TimeSpan InDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>交互反馈时长。</summary>
    internal static readonly TimeSpan TapDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>逐个进场错峰间隔。</summary>
    internal static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(35);

    /// <summary>每帧间隔（≈60fps）。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 手动驱动一次性动画：每帧把「进度 0→1」经缓动后传给 apply(eased)。
    /// delayMs 内保持 apply(0)。结束帧写入 e=1 的终值后停止。
    /// 返回定时器供调用方取消（如按压反馈的连点）。
    /// </summary>
    private static DispatcherTimer Drive(Action<double> apply, double durationMs, double delayMs, Easing easing)
    {
        var timer = new DispatcherTimer { Interval = FrameInterval };
        var started = Environment.TickCount64;
        timer.Tick += (_, _) =>
        {
            var elapsed = Environment.TickCount64 - started - delayMs;
            if (elapsed < 0)
            {
                apply(0);
                return;
            }

            var p = Math.Min(1.0, elapsed / Math.Max(1.0, durationMs));
            apply(easing.Ease(p));
            if (p >= 1.0)
            {
                timer.Stop();
            }
        };
        timer.Start();
        return timer;
    }

    /// <summary>不透明度淡入（初值 → 终值）。调用方需先把控件 Opacity 置为初值。</summary>
    internal static void FadeIn(Control control, double from = 0, double to = 1,
        TimeSpan? duration = null, Easing? easing = null, TimeSpan? delay = null)
    {
        var d = duration ?? InDuration;
        var ms = d.TotalMilliseconds;
        var dm = delay?.TotalMilliseconds ?? 0;
        // 不透明度默认用柔滑缓动：BackEase 过冲会让文本快速闪过（观感=闪动）。
        Drive(v => control.Opacity = from + (to - from) * v, ms, dm, easing ?? Interaction);
    }

    /// <summary>从偏移滑入（位移 → 0）。自动确保 RenderTransform 里有一个 TranslateTransform。</summary>
    internal static void SlideIn(Control control, double fromX, double fromY,
        TimeSpan? duration = null, Easing? easing = null, TimeSpan? delay = null)
    {
        var translate = Ensure<TranslateTransform>(control);
        var d = duration ?? InDuration;
        var ms = d.TotalMilliseconds;
        var dm = delay?.TotalMilliseconds ?? 0;
        Drive(v =>
        {
            translate.X = fromX * (1 - v);
            translate.Y = fromY * (1 - v);
        }, ms, dm, easing ?? Entrance);
    }

    /// <summary>缩放淡入（比例 → 1）。自动确保 RenderTransform 里有一个 ScaleTransform。</summary>
    internal static void ScaleIn(Control control, double from = 0.94,
        TimeSpan? duration = null, Easing? easing = null, TimeSpan? delay = null)
    {
        var scale = Ensure<ScaleTransform>(control);
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var d = duration ?? InDuration;
        var ms = d.TotalMilliseconds;
        var dm = delay?.TotalMilliseconds ?? 0;
        Drive(v =>
        {
            scale.ScaleX = from + (1 - from) * v;
            scale.ScaleY = from + (1 - from) * v;
        }, ms, dm, easing ?? Entrance);
    }

    /// <summary>滑入 + 缩放 + 淡入的组合入场。缩放复用按压反馈的 ScaleTransform（同一变换）。
    /// 不透明度用柔滑缓动（无过冲），位移/缩放用弹性缓动。</summary>
    internal static void PopIn(Control control, double fromX, double fromY, double scaleFrom,
        TimeSpan? duration = null, Easing? easing = null, TimeSpan? delay = null)
    {
        var translate = Ensure<TranslateTransform>(control);
        var scale = Ensure<ScaleTransform>(control);
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var d = duration ?? InDuration;
        var ms = d.TotalMilliseconds;
        var dm = delay?.TotalMilliseconds ?? 0;
        var e = easing ?? Entrance;
        // 不透明度单独驱动（柔滑，避免文本闪过）；位移/缩放走弹性。
        Drive(v => control.Opacity = v, ms, dm, Interaction);
        Drive(v =>
        {
            translate.X = fromX * (1 - v);
            translate.Y = fromY * (1 - v);
            scale.ScaleX = scaleFrom + (1 - scaleFrom) * v;
            scale.ScaleY = scaleFrom + (1 - scaleFrom) * v;
        }, ms, dm, e);
    }

    /// <summary>
    /// 给按钮附加按压缩放反馈（Fluent 风格：按下缩到 0.96，松开弹性回弹）。
    /// 缩放作用在按钮 RenderTransform 里的 ScaleTransform（与入场 PopIn/ScaleIn 共享同一变换）。
    /// 连点时取消上一次驱动，避免抖动。
    /// </summary>
    internal static void AddPressFeedback(Button button, double scale = 0.96)
    {
        var pressScale = Ensure<ScaleTransform>(button);
        button.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        DispatcherTimer? active = null;

        void Run(double from, double to)
        {
            active?.Stop();
            var ms = TapDuration.TotalMilliseconds;
            active = Drive(v =>
            {
                pressScale.ScaleX = from + (to - from) * v;
                pressScale.ScaleY = from + (to - from) * v;
            }, ms, 0, Interaction);
        }

        button.PointerPressed += (_, _) => Run(1, scale);
        button.PointerReleased += (_, _) => Run(pressScale.ScaleX, 1);
        button.PointerCaptureLost += (_, _) => Run(pressScale.ScaleX, 1);
    }

    /// <summary>在控件的 RenderTransform 里查找指定类型的变换（顶层）。</summary>
    private static T? Find<T>(Control control) where T : Transform
    {
        if (control.RenderTransform is T direct)
        {
            return direct;
        }

        return control.RenderTransform is TransformGroup group
            ? group.Children.OfType<T>().FirstOrDefault()
            : null;
    }

    /// <summary>
    /// 确保控件的 RenderTransform 是一个包含 T 类型变换的 TransformGroup，并返回该变换。
    /// 已有则复用（保证同类型变换在顶层只有一份，避免动画目标歧义）。
    /// </summary>
    private static T Ensure<T>(Control control) where T : Transform, new()
    {
        var existing = Find<T>(control);
        if (existing != null)
        {
            return existing;
        }

        var created = new T();
        var group = new TransformGroup();
        if (control.RenderTransform is TransformGroup existingGroup)
        {
            foreach (var child in existingGroup.Children)
            {
                group.Children.Add(child);
            }
        }
        else if (control.RenderTransform is Transform existingTransform)
        {
            group.Children.Add(existingTransform);
        }

        group.Children.Add(created);
        control.RenderTransform = group;
        return created;
    }
}
