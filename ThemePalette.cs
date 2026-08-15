using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIslandInjector;

/// <summary>
/// 编辑器/设置页共用的主题调色板。
/// 深浅色判断优先使用 Avalonia 已实际应用到窗口的主题变体；宿主主题服务可能在
/// 插件窗口创建时仍保留旧值，因此仅作为最后回退。
/// 面板 / 浮层配色按深浅色直接给出稳定色值，不再依赖插件窗口里可能解析错误的主题资源。
/// 编辑器不能把窗口、侧栏和浮层都画成同一种颜色；这里同时提供基础层和表面层，
/// 以便在两个主题下都保留清晰的层级和边界。
/// </summary>
public static class ThemePalette
{
    /// <summary>判断当前主题是否为深色。</summary>
    public static bool IsDarkTheme()
    {
        // RequestedThemeVariant 在“跟随系统”时会是 Default，不能据此决定明暗；
        // ActualThemeVariant 则是 Avalonia 已解析的真实主题，插件窗口也会继承它。
        var actualTheme = Application.Current?.ActualThemeVariant;
        if (actualTheme == ThemeVariant.Dark)
        {
            return true;
        }

        if (actualTheme == ThemeVariant.Light)
        {
            return false;
        }

        // 当宿主使用 Default（跟随系统）且 Avalonia 没有暴露最终变体时，优先从已
        // 应用的主题资源取样；这比 ThemeService 的可能滞后缓存更可靠。
        foreach (var key in new[]
                 {
                     "CommandBarOverflowPresenterBackground",
                     "LayerFillColorDefaultBrush",
                     "SystemControlBackgroundAltHighBrush"
                 })
        {
            if (FindResource(key) is ISolidColorBrush b)
            {
                var c = b.Color;
                return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 < 0.5;
            }
        }

        try
        {
            var theme = IAppHost.TryGetService<IThemeService>();
            if (theme != null)
            {
                // 0 = 浅色，1 = 深色
                return theme.CurrentRealThemeMode == 1;
            }
        }
        catch
        {
            // 服务不可用时使用保守的深色回退。
        }

        return true;
    }

    /// <summary>查找主题资源（找不到返回 null）。</summary>
    public static object? FindResource(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    /// <summary>查找主题画刷。</summary>
    public static IBrush? ThemeBrush(string key) => FindResource(key) as IBrush;

    /// <summary>读取当前 ClassIsland 的主题强调色，资源不可用时回退到系统蓝。</summary>
    public static Color AccentColor()
    {
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "SystemAccentColor" })
        {
            if (ThemeBrush(key) is ISolidColorBrush brush)
            {
                return brush.Color;
            }
        }

        return Color.FromRgb(0, 120, 212);
    }

    /// <summary>主题强调色 + 指定不透明度（用于选中高亮、参考线、锚点等需要透明强调色的地方）。</summary>
    public static Color AccentColorWithAlpha(byte alpha)
    {
        var c = AccentColor();
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>编辑器窗口的基础背景。</summary>
    public static IBrush WindowBackground() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromRgb(24, 26, 30)
        : Color.FromRgb(244, 246, 248));

    /// <summary>
    /// Mica 窗口的基底背景：半透明主题色，让 Mica 纹理透出的同时保证内容可读。
    /// 深色主题透明度略低（Mica 本身偏暗），浅色主题略高。
    /// </summary>
    public static IBrush MicaWindowBackground() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(0xB8, 24, 26, 30)
        : Color.FromArgb(0xD9, 244, 246, 248));

    /// <summary>编辑器侧栏、缩放浮层等抬升表面的背景。</summary>
    public static IBrush PanelBackground() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromRgb(37, 40, 46)
        : Color.FromRgb(255, 255, 255));

    /// <summary>
    /// Mica 窗口内的表面背景：半透明主题色，让 Mica 透出。
    /// 用于编辑器的大区块（工具栏 / 舞台 / 右侧栏），避免大块实心色盖住 Mica；
    /// 小控件与浮动条仍用实心的 <see cref="PanelBackground"/> 保证可读性。
    /// </summary>
    public static IBrush MicaPanelBackground() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(0x70, 37, 40, 46)
        : Color.FromArgb(0xC0, 255, 255, 255));

    /// <summary>编辑器表面的描边，避免深色主题下整窗混成一片灰色。</summary>
    public static IBrush SurfaceBorder() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(100, 255, 255, 255)
        : Color.FromArgb(36, 0, 0, 0));

    /// <summary>列表和小控件的轻微悬停/占位填充。</summary>
    public static IBrush SubtleFill() => new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(28, 255, 255, 255)
        : Color.FromArgb(14, 0, 0, 0));

    /// <summary>在手工绘制的表面上使用的主文字颜色。</summary>
    public static Color ForegroundColor() => IsDarkTheme()
        ? Color.FromRgb(245, 245, 245)
        : Color.FromRgb(30, 32, 36);

    /// <summary>根据背景亮度取得可读文字颜色。</summary>
    public static Color ContrastForeground(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance < 0.55 ? Colors.White : Color.FromRgb(30, 32, 36);
    }
}

