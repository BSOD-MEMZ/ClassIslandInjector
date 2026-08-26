using System.IO.Compression;
using System.Text.Json;

namespace ClassIslandInjector;

/// <summary>
/// 预设包元数据：作者 / 学校 / 打包时间 / 宿主版本 / 插件版本等，写入 zip 的 metadata.json，
/// 供接收方安装前查看与确认。
/// </summary>
public sealed class PresetMetadata
{
    /// <summary>预设作者（导出时填写）。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>预设作者所属学校 / 组织（可选）。</summary>
    public string School { get; set; } = string.Empty;

    /// <summary>预设备注 / 描述（商店展示用，可选）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>打包时间（ISO 8601）。</summary>
    public string CreatedAt { get; set; } = DateTime.Now.ToString("O");

    /// <summary>打包时的宿主 ClassIsland 版本。</summary>
    public string HostVersion { get; set; } = string.Empty;

    /// <summary>打包时的插件版本。</summary>
    public string PluginVersion { get; set; } = string.Empty;
}

/// <summary>
/// 预设交换：把用户预设（含底图等静态资源）导出为 zip 文件，或从 zip 导入预设。
/// zip 结构：
///   preset.json            —— UserPreset（Name + 设置快照），其中本地路径已改写为 resources/ 相对引用
///   metadata.json          —— 预设元数据（作者 / 学校 / 打包时间 / 宿主版本 / 插件版本）
///   resources/...          —— 打包的静态资源（自定义样式表 / 简单模式底图 / 图层式底图图片或文件夹）
/// 导入时把 resources/ 解包到插件数据目录的 imported/ 下，并把相对引用重写为本地绝对路径。
/// </summary>
internal static class PresetExchange
{
    public const string PresetFileName = "preset.json";
    public const string MetadataFileName = "metadata.json";
    private const string ResourcesDirName = "resources";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>操作结果；导入成功时 <see cref="Preset"/> 为解析出的用户预设，<see cref="Metadata"/> 为元数据。</summary>
    public sealed record Result(bool Success, string Message, UserPreset? Preset = null, PresetMetadata? Metadata = null);

    /// <summary>
    /// 导出用户预设到 zip（含引用的本地静态资源：自定义样式表 / 底图 / 图层图片）。
    /// 对不存在或非本地来源的路径一律清空，避免把导出者的本机绝对路径带进包里。
    /// 可附带主界面预览图（<paramref name="previewPng"/>，PNG 字节），写入 preview.png 供商店展示。
    /// </summary>
    public static Result Export(UserPreset preset, PresetMetadata metadata, string zipPath, byte[]? previewPng = null)
    {
        try
        {
            // 深拷贝设置，避免污染原预设。
            var settings = preset.Settings.Clone();
            var copied = 0;

            using var stream = File.Create(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            // 1. 自定义样式表（.axaml 文件）
            if (TryMapLocalPath(archive, settings.StyleSheetPath, "resources/style", isFolder: false, out var styleRel))
            {
                settings.StyleSheetPath = styleRel;
                copied++;
            }
            else
            {
                settings.StyleSheetPath = string.Empty;
            }

            // 2. 简单模式底图（本地图片文件 / 幻灯片文件夹）
            if (settings.WallpaperSource is WallpaperSource.LocalImage or WallpaperSource.FolderSlideshow)
            {
                var isFolder = settings.WallpaperSource == WallpaperSource.FolderSlideshow;
                if (TryMapLocalPath(archive, settings.WallpaperPath, "resources/wallpaper", isFolder, out var wallpaperRel))
                {
                    settings.WallpaperPath = wallpaperRel;
                    copied++;
                }
                else
                {
                    settings.WallpaperPath = string.Empty;
                }
            }
            else
            {
                settings.WallpaperPath = string.Empty;
            }

            // 3. 图层式底图的本地图片 / 幻灯片文件夹图层
            for (var i = 0; i < settings.WallpaperLayers.Count; i++)
            {
                var layer = settings.WallpaperLayers[i];
                if (layer.Kind != WallpaperLayerKind.Image ||
                    layer.Source is not (WallpaperSource.LocalImage or WallpaperSource.FolderSlideshow))
                {
                    // 非本地来源（SMTC 专辑等）的图层不留本机路径。
                    layer.Path = string.Empty;
                    continue;
                }

                var isFolder = layer.Source == WallpaperSource.FolderSlideshow;
                if (TryMapLocalPath(archive, layer.Path, $"resources/layer_{i}", isFolder, out var layerRel))
                {
                    layer.Path = layerRel;
                    copied++;
                }
                else
                {
                    layer.Path = string.Empty;
                }
            }

            // 4. 最后写入清洗后的设置快照、元数据与预览图（资源已全部进包）。
            AddTextEntry(archive, PresetFileName, JsonSerializer.Serialize(new UserPreset
            {
                Name = preset.Name,
                Settings = settings
            }, JsonOptions));
            AddTextEntry(archive, MetadataFileName, JsonSerializer.Serialize(metadata, JsonOptions));
            if (previewPng is { Length: > 0 })
            {
                AddBytesEntry(archive, "preview.png", previewPng);
            }

            return new Result(true, $"已导出（含 {copied} 个资源文件）。");
        }
        catch (Exception ex)
        {
            return new Result(false, $"导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从 zip 导入预设：解包 resources/ 到 <paramref name="importRootDirectory"/>（按预设名建子目录），
    /// 并把 settings 里的 resources/ 相对引用重写为解包后的本地绝对路径。
    /// </summary>
    public static Result Import(string zipPath, string importRootDirectory)
    {
        try
        {
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var presetEntry = archive.GetEntry(PresetFileName);
            if (presetEntry == null)
            {
                return new Result(false, "不是有效的预设包：缺少 preset.json。");
            }

            UserPreset? preset;
            using (var reader = new StreamReader(presetEntry.Open()))
            {
                preset = JsonSerializer.Deserialize<UserPreset>(reader.ReadToEnd());
            }

            if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
            {
                return new Result(false, "预设包内容无效（缺少名称或设置）。");
            }

            // 元数据（可选）：老版本预设包可能没有 metadata.json。
            PresetMetadata? metadata = null;
            var metadataEntry = archive.GetEntry(MetadataFileName);
            if (metadataEntry != null)
            {
                try
                {
                    using var reader = new StreamReader(metadataEntry.Open());
                    metadata = JsonSerializer.Deserialize<PresetMetadata>(reader.ReadToEnd());
                }
                catch
                {
                    metadata = null;
                }
            }


            // 解包资源：每次导入独立目录（预设名 + 时间戳），避免覆盖已有导入。
            var importDir = Path.Combine(importRootDirectory, $"{Sanitize(preset.Name)}_{DateTime.Now:yyyyMMddHHmmss}");
            Directory.CreateDirectory(importDir);
            var extracted = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith($"{ResourcesDirName}/", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith("/"))
                {
                    if (ExtractEntry(entry, importDir))
                    {
                        extracted++;
                    }
                }
            }

            // 重写 resources/ 相对引用为本地绝对路径；其余残留路径（不应出现）清空。
            var settings = preset.Settings;
            settings.StyleSheetPath = ResolveResource(settings.StyleSheetPath, importDir);
            settings.WallpaperPath = ResolveResource(settings.WallpaperPath, importDir);
            foreach (var layer in settings.WallpaperLayers)
            {
                layer.Path = ResolveResource(layer.Path, importDir);
            }

            return new Result(true, $"已导入（含 {extracted} 个资源文件）。", preset, metadata);
        }
        catch (Exception ex)
        {
            return new Result(false, $"导入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把本地路径打包进 zip 并返回相对引用。isFolder 时打包整个文件夹；
    /// 路径不存在时返回 false（relative 输出空串，仅在返回 true 时有效）。
    /// </summary>
    private static bool TryMapLocalPath(ZipArchive archive, string localPath, string slot, bool isFolder, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return false;
        }

        if (isFolder && Directory.Exists(localPath))
        {
            var dirName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dirName))
            {
                dirName = "folder";
            }

            relative = $"{slot}_{dirName}";
            foreach (var file in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(localPath, file);
                AddFile(archive, file, $"{relative}/{rel.Replace('\\', '/')}");
            }

            return true;
        }

        if (File.Exists(localPath))
        {
            var ext = Path.GetExtension(localPath).ToLowerInvariant();
            relative = $"{slot}{ext}";
            AddFile(archive, localPath, relative);
            return true;
        }

        return false;
    }

    /// <summary>把 zip 条目解压到 importDir（防路径穿越），返回是否成功。</summary>
    private static bool ExtractEntry(ZipArchiveEntry entry, string importDir)
    {
        var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(importDir, rel));
        var root = Path.GetFullPath(importDir) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 zip 内的 resources/ 相对引用解析为 importDir 下的绝对路径；非相对引用一律清空。</summary>
    private static string ResolveResource(string path, string importDir)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith($"{ResourcesDirName}/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(importDir, normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return string.Empty;
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void AddBytesEntry(ZipArchive archive, string entryName, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static void AddFile(ZipArchive archive, string localPath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = File.OpenRead(localPath);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    /// <summary>把预设名里的非法文件名字符替换为下划线（用作导入目录名）。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
