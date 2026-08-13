namespace ClassIslandInjector;

/// <summary>
/// 图片文件枚举工具：幻灯片 / 文件夹图片的统一扩展名白名单与排序。
/// 收敛各处重复的「扩展名数组 + 过滤 + 排序」样板。
/// </summary>
internal static class ImageFiles
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>枚举目录下按名称排序的图片文件（扩展名白名单，忽略大小写）。</summary>
    public static IReadOnlyList<string> EnumerateSorted(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
