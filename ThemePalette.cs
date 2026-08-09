using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIslandInjector;

/// <summary>
/// 编辑器/设置页共用的主题调色板。
/// 深浅色判断优先用宿主的公开服务 <see cref="IThemeService.CurrentRealThemeMode"/>
/// （0 = 浅色，1 = 深色），这是最可靠的信号；资源亮度检测仅作兜底。
/// 面板 / 浮层配色按深浅色直接给出稳定色值，不再依赖插件窗口里可能解析错误的主题资源。
/// </summary>
public static class ThemePalette
{
    /// <summary>判断当前主题是否为深色（优先宿主 IThemeService，失败回退资源亮度）。</summary>
    public static bool IsDarkTheme()
    {
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
            // 服务不可用时回退资源亮度检测。
        }

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

        return true;
    }

    /// <summary>查找主题资源（找不到返回 null）。</summary>
    public static object? FindResource(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    /// <summary>查找主题画刷。</summary>
    public static IBrush? ThemeBrush(string key) => FindResource(key) as IBrush;

    /// <summary>手搓面板/浮层的回退背景：深色 = 接近 FAUI 的 #202020，浅色 = 接近白。</summary>
    public static IBrush PanelBackground() =>
        IsDarkTheme()
            ? new SolidColorBrush(Color.FromArgb(245, 32, 32, 36))
            : new SolidColorBrush(Color.FromArgb(248, 243, 243, 243));
}

