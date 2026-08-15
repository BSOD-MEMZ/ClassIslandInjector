using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector;

/// <summary>
/// 自建编辑器窗口的 Mica 背景辅助。
///
/// Mica 的正确组合（FluentAvalonia 官方示例与宿主 MyWindow 一致）：
/// <code>Background = Brushes.Transparent + TransparencyLevelHint = [Mica]</code>。
/// 关键点：
/// - Background 必须完全透明，Mica 才会由 DWM 渲染并透出——半透明实色背景会走
///   普通透明/合成路径，Mica 不显示。
/// - 宿主 MyWindow 在 <see cref="MyWindow.Loaded"/> 事件里才设置透明级别（窗口已显示），
///   对纯代码创建的插件窗口时机太晚；因此这里在构造函数（Show 之前）就设置，
///   并同时打开宿主 <c>EnableMicaWindow</c> 作为 Loaded 兜底（幂等）。
/// - 仅 Win11 21H2+（Build ≥ 22000）支持 Mica；Windows 10 保持主题感知的实色基底。
/// </summary>
public static class EditorMica
{
    /// <summary>
    /// 启用 Mica 窗口背景。必须在窗口 <c>Show()</c> 之前（构造函数中）调用。
    /// 非 Windows 或 Windows 10 自动保持实色主题背景。
    /// </summary>
    public static void EnableMica(MyWindow window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            // Win11 21H2+：官方验证的组合。Background 完全透明让 Mica 透出。
            window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            window.Background = Brushes.Transparent;
            // 宿主 Loaded 兜底（仅在宿主判定支持时重复设置，幂等无害）。
            window.EnableMicaWindow = true;
        }
        else
        {
            // Windows 10：无 Mica，保持主题感知的实色基底。
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            window.Background = ThemePalette.WindowBackground();
        }
    }
}
