using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 主界面文字美化（Issue #7）：字体 / 字号 / 字色 / 勾边，并支持「跟随背景自动反色」。
///
/// 设计要点：
/// <list type="bullet">
/// <item>不改宿主 XAML，只在运行时对可视树里的文字控件逐一施加样式，并在停用时还原 ——
/// 与注入器其余部分（背景 / 边框 / 底图）的实现方式保持一致。</item>
/// <item>每个文字控件记录「首次接管时的原值」（字体 / 字号 / 字色 / 效果），
/// 关闭功能或改回「保持宿主」时按记录还原，不会把宿主样式永久改坏。</item>
/// <item>自动反色按「所属分体块 / 主界面」的有效背景色算相对亮度，因此 SMTC 动态取色、
/// 渐变、分块独立配色都能正确跟随（浅底黑字、深底白字）。</item>
/// <item>勾边：Avalonia 11.3 的 TextBlock 没有 Stroke 属性（已核对 11.3.6 的 API），
/// 因此用四个方向偏移的 <see cref="DropShadowDirectionEffect"/> 叠加模拟描边 —— 只影响
/// 文字自身的渲染，不像外层 Border 方案那样会撑开布局、顶坏主界面排版。</item>
/// </list>
/// </summary>
internal sealed class TextStylingInjector
{
    /// <summary>单个文字控件首次接管时的原始样式快照（用于还原）。</summary>
    private sealed class OriginalStyle
    {
        public FontFamily? FontFamily;
        public double FontSize;
        public IBrush? Foreground;
        public FontWeight FontWeight;
        public IEffect? Effect;
    }

    private readonly Dictionary<Control, OriginalStyle> _captured = [];

    /// <summary>已应用的勾边效果实例（按控件缓存，避免每帧重建效果对象触发重绘）。</summary>
    private readonly Dictionary<Control, OutlineEffect> _outlines = [];

    /// <summary>已应用的勾边参数（用于判断是否需要重建效果）。</summary>
    private sealed class OutlineEffect
    {
        public double Thickness;
        public Color Color;
        public DropShadowEffect Effect = null!;
    }

    /// <summary>本次会话累计处理过的文字控件数（诊断用）。</summary>
    public int StyledCount => _captured.Count;

    /// <summary>
    /// 对给定可视树应用文字美化。传入的 <paramref name="descendants"/> 应为主界面（或主窗口）
    /// 的可视后代快照，由调用方复用同一份快照以避免重复遍历。
    /// </summary>
    /// <param name="settings">当前设置。</param>
    /// <param name="descendants">可视后代快照。</param>
    /// <param name="isInMainWindow">判断某个控件是否属于主界面（决定是否跳过提醒等其它区域）。</param>
    /// <param name="backgroundProvider">取某控件所属区域的「有效背景色」，用于自动反色。</param>
    public void Apply(
        InjectorSettings settings,
        IEnumerable<Control> descendants,
        Func<Control, bool> isInMainWindow,
        Func<Control, Color?> backgroundProvider)
    {
        if (!settings.TextStylingEnabled)
        {
            RestoreAll();
            return;
        }

        var fontFamily = ParseFontFamily(settings.TextFontFamily);
        var scale = Math.Clamp(settings.TextFontScale, 0.3, 3);
        var baseSize = settings.TextFontSize;
        var colorMode = settings.TextColorMode;
        var fixedColor = ParseColor(settings.TextColor, Colors.White);
        var lightColor = ParseColor(settings.TextLightColor, Colors.White);
        var darkColor = ParseColor(settings.TextDarkColor, Color.FromRgb(0x1E, 0x20, 0x24));
        var threshold = Math.Clamp(settings.TextInvertThreshold, 0, 1);
        var outline = Math.Clamp(settings.TextOutlineThickness, 0, 8);
        var outlineColor = ParseColor(settings.TextOutlineColor, Color.FromArgb(0xCC, 0, 0, 0));

        // 主界面可能同时存在多行；这里遍历一次快照统一处理。
        foreach (var control in descendants)
        {
            if (control is not TextBlock textBlock)
            {
                continue;
            }

            // 只作用于主界面（可选）：提醒、设置页等其它区域的文字保持原样。
            if (settings.TextStylingMainWindowOnly && !isInMainWindow(textBlock))
            {
                continue;
            }

            if (!_captured.TryGetValue(textBlock, out var original))
            {
                original = Capture(textBlock);
                _captured[textBlock] = original;
            }

            ApplyFont(textBlock, original, fontFamily, baseSize, scale);
            ApplyColor(textBlock, original, colorMode, fixedColor, lightColor, darkColor, threshold, backgroundProvider);
            ApplyOutline(textBlock, original, outline, outlineColor);
        }
    }

    /// <summary>还原全部已接管的文字控件（功能关闭 / 注入器卸载时调用）。</summary>
    public void RestoreAll()
    {
        foreach (var (control, original) in _captured)
        {
            try
            {
                if (control is TextBlock tb)
                {
                    tb.FontFamily = original.FontFamily;
                    tb.FontSize = original.FontSize;
                    tb.FontWeight = original.FontWeight;
                    tb.Foreground = original.Foreground;
                    tb.Effect = original.Effect;
                }
            }
            catch
            {
                // 还原失败（控件已释放等）不影响其余控件。
            }
        }

        _captured.Clear();
        _outlines.Clear();
    }

    /// <summary>自动反色使用的相对亮度（sRGB 加权，0=黑 1=白）。忽略透明度：半透明底色上仍按色相判断。</summary>
    public static double RelativeLuminance(Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

    /// <summary>解析字体名（空 / 非法 → null，表示保持宿主字体）。</summary>
    public static FontFamily? ParseFontFamily(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return new FontFamily(text);
        }
        catch
        {
            return null;
        }
    }

    private static OriginalStyle Capture(TextBlock tb) => new()
    {
        FontFamily = tb.FontFamily,
        FontSize = tb.FontSize,
        Foreground = tb.Foreground,
        FontWeight = tb.FontWeight,
        Effect = tb.Effect
    };

    private static void ApplyFont(
        TextBlock tb,
        OriginalStyle original,
        FontFamily? family,
        double baseSize,
        double scale)
    {
        if (family != null)
        {
            tb.FontFamily = family;
        }

        if (baseSize > 0)
        {
            // 指定了基础字号：主界面上「标准文字」按该字号，其余按原始比例缩放，
            // 避免时钟大字、日期小字被拉成一样大。
            tb.FontSize = baseSize;
        }
        else if (Math.Abs(scale - 1) > 0.001)
        {
            // 未指定字号时按比例整体缩放宿主原字号。
            tb.FontSize = Math.Max(1, original.FontSize * scale);
        }
    }

    private static void ApplyColor(
        TextBlock tb,
        OriginalStyle original,
        TextColorMode mode,
        Color fixedColor,
        Color lightColor,
        Color darkColor,
        double threshold,
        Func<Control, Color?> backgroundProvider)
    {
        switch (mode)
        {
            case TextColorMode.KeepHost:
                tb.Foreground = original.Foreground;
                return;
            case TextColorMode.Fixed:
                tb.Foreground = new SolidColorBrush(fixedColor);
                return;
        }

        // 自动反色：按所属区域的有效背景色亮度挑选浅色 / 深色字。
        var background = backgroundProvider(tb);
        var color = background is { } bg && RelativeLuminance(bg) >= threshold ? darkColor : lightColor;
        tb.Foreground = new SolidColorBrush(color);
    }

    /// <summary>
    /// 勾边（文字轮廓）：Avalonia 11.3 的 TextBlock 既没有 Stroke 属性，Effect 也只接受单个
    /// 效果（没有效果组），因此这里用「模糊半径 0 的硬阴影」模拟描边 —— 模糊为 0 时阴影就是
    /// 文字的实心拷贝，紧贴文字四下垫出一圈轮廓。勾边方向随 <c>OffsetX/Y</c> 斜向偏移，
    /// 对「深色背景上的深色字」「亮背景上的亮字」这类看不清的情况效果最明显。
    /// </summary>
    private void ApplyOutline(TextBlock tb, OriginalStyle original, double thickness, Color color)
    {
        if (thickness <= 0)
        {
            tb.Effect = original.Effect;
            _outlines.Remove(tb);
            return;
        }

        if (_outlines.TryGetValue(tb, out var existing))
        {
            if (Math.Abs(existing.Thickness - thickness) < 0.01 && existing.Color == color)
            {
                // 参数未变化：保持已有效果（避免每帧重建触发重绘）。
                return;
            }

            existing.Thickness = thickness;
            existing.Color = color;
            existing.Effect.BlurRadius = 0;
            existing.Effect.OffsetX = thickness;
            existing.Effect.OffsetY = thickness;
            existing.Effect.Color = color;
            return;
        }

        var effect = new DropShadowEffect
        {
            BlurRadius = 0,
            OffsetX = thickness,
            OffsetY = thickness,
            Color = color
        };
        _outlines[tb] = new OutlineEffect { Thickness = thickness, Color = color, Effect = effect };
        tb.Effect = effect;
    }

    private static Color ParseColor(string text, Color fallback)
        => Color.TryParse(text, out var color) ? color : fallback;
}
