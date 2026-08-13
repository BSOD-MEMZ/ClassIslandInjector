using Avalonia.Media;

namespace ClassIslandInjector;

/// <summary>
/// 颜色解析工具：把字符串解析为 <see cref="Color"/>，格式非法时回退默认值。
/// 收敛各文件重复的「Color.Parse + FormatException 回退」样板。
/// </summary>
internal static class ColorUtil
{
    /// <summary>解析颜色字符串，失败时回退到 <paramref name="fallback"/>。</summary>
    public static Color Parse(string text, Color fallback)
    {
        try
        {
            return Color.Parse(text);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
