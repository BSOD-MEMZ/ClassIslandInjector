using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClassIslandInjector.Views;

/// <summary>
/// Fluent 质感的页面切换：旧页向上滑出并淡出，新页自下方滑入（OutCubic 减速缓动），
/// 对应 QFW StackedWidget / WinUI 的纵向滑动页面过渡。
/// </summary>
internal static class FluentSlideTransition
{
    /// <summary>纵向滑动位移（px）。</summary>
    private const double Offset = 44;

    /// <summary>旧页退场时长。</summary>
    private const int OutDuration = 180;

    /// <summary>新页入场时长。</summary>
    private const int InDuration = 260;

    /// <summary>执行切换动画：<paramref name="from"/> 上滑淡出，<paramref name="to"/> 自下滑入。</summary>
    public static async Task RunAsync(Control? from, Control to)
    {
        // 新页自下方滑入。
        to.Opacity = 0;
        var toTranslate = new TranslateTransform(0, Offset);
        to.RenderTransform = toTranslate;

        var tasks = new List<Task>
        {
            Animate(to, Visual.OpacityProperty, 0, 1, InDuration),
            Animate(toTranslate, TranslateTransform.YProperty, Offset, 0, InDuration)
        };

        // 旧页向上滑出并淡出。
        if (from != null)
        {
            var fromTranslate = new TranslateTransform(0, 0);
            from.RenderTransform = fromTranslate;
            tasks.Add(Animate(from, Visual.OpacityProperty, 1, 0, OutDuration));
            tasks.Add(Animate(fromTranslate, TranslateTransform.YProperty, 0, -Offset, OutDuration));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            to.RenderTransform = null;
            to.Opacity = 1;
            if (from != null)
            {
                from.RenderTransform = null;
                from.Opacity = 1;
            }
        }
    }

    /// <summary>单个属性的双帧减速动画。</summary>
    private static Task Animate(Animatable target, AvaloniaProperty property, double from, double to, int durationMs)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = new CubicEaseOut(),
            // 关键：结束后保持最终帧。Avalonia 动画默认 FillMode.None 会在结束的下一拍把属性
            // 回退到动画前的基值（新页入场前 Opacity=0）——异步晚于 finally 的恢复赋值，
            // 表现为「页面闪一下就消失」，必须显式 Forward。
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property, to) } }
            }
        };
        return animation.RunAsync(target);
    }
}
