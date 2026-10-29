using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// 解包后资源总字节上限：包体大小（远程下发 50MB）只限制压缩后的字节，高压缩比的 zip 解开后可以撑满教室电脑的磁盘。
    /// 按实际写出的字节计（不信 zip 头里声明的长度），超出即中止并删掉已解出的目录。
    /// </summary>
    public const long MaxExtractedBytes = 256L * 1024 * 1024;

    /// <summary>资源条目数上限（幻灯片文件夹图层可能有很多图，但不会上万）。</summary>
    public const int MaxResourceEntries = 4096;

    /// <summary>
    /// preset.json / metadata.json 解压后的字节上限：它们在解包资源之前就被整体读进内存反序列化，
    /// 高压缩比的超大 JSON 会在资源上限生效前耗尽内存 / CPU。正常预设只有几十 KB。
    /// </summary>
    public const int MaxJsonBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 已解包、但还没装进预设列表的导入目录（全路径，不区分大小写）。
    /// 用户取消安装时目录会一直留在这里，直到宿主重启——那之后它不再受保护，下次清理时按「无引用」删除。
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> PendingImportDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 导入解析出的预设 → 它的解包目录。<see cref="CompleteImport"/> 按这份对应关系释放「导入中」登记，
    /// 而不是看设置有没有引用目录里的文件：包里可以带没被引用的资源，按引用判断会让目录永远挂着「导入中」、清理不掉。
    /// </summary>
    private static readonly ConditionalWeakTable<UserPreset, string> ImportDirectories = new();

    /// <summary>操作结果；导入成功时 <see cref="Preset"/> 为解析出的用户预设，<see cref="Metadata"/> 为元数据。</summary>
    public sealed record Result(bool Success, string Message, UserPreset? Preset = null, PresetMetadata? Metadata = null);

    /// <summary>导入目录是否仍在「已解包、未装进预设列表」的过程中（清理导入目录时必须跳过）。</summary>
    public static bool IsImportPending(string directory) =>
        PendingImportDirectories.ContainsKey(Path.GetFullPath(directory));

    /// <summary>预设已装进列表：释放这次导入的解包目录，此后由「是否被引用」决定去留（没被引用的资源随下次清理删除）。</summary>
    public static void CompleteImport(UserPreset preset)
    {
        if (ImportDirectories.TryGetValue(preset, out var directory))
        {
            PendingImportDirectories.TryRemove(directory, out _);
            ImportDirectories.Remove(preset);
        }
    }

    /// <summary>设置里引用了本地静态资源的路径（与导出/导入改写的范围一致：自定义样式表 + 图层底图）。</summary>
    public static IEnumerable<string> EnumerateResourcePaths(InjectorSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.StyleSheetPath))
        {
            yield return settings.StyleSheetPath;
        }

        foreach (var layer in settings.WallpaperLayers.Where(l => !string.IsNullOrWhiteSpace(l.Path)))
        {
            yield return layer.Path;
        }
    }

    /// <summary><paramref name="path"/> 是否就是 <paramref name="directory"/>（全路径）或位于其下。</summary>
    public static bool IsPathWithin(string path, string directory)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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

            // 2. 图层式底图的本地图片 / 幻灯片文件夹图层（简单模式底图已删除）。
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
    /// 解包总量受 <paramref name="maxExtractedBytes"/> 与 <see cref="MaxResourceEntries"/> 限制（防 zip 炸弹）。
    /// </summary>
    public static Result Import(string zipPath, string importRootDirectory, long maxExtractedBytes = MaxExtractedBytes)
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

            if (ReadBoundedText(presetEntry) is not { } presetJson)
            {
                return new Result(false, $"预设包的 preset.json 解压后超过 {MaxJsonBytes / (1024 * 1024)}MB，已拒绝导入。");
            }

            var preset = JsonSerializer.Deserialize<UserPreset>(presetJson);
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name))
            {
                return new Result(false, "预设包内容无效。");
            }

            // 元数据（可选）：老版本预设包可能没有 metadata.json。
            PresetMetadata? metadata = null;
            var metadataEntry = archive.GetEntry(MetadataFileName);
            if (metadataEntry != null)
            {
                try
                {
                    // 超限的元数据按「没有元数据」处理（不影响安装）。
                    metadata = ReadBoundedText(metadataEntry) is { } metadataJson
                        ? JsonSerializer.Deserialize<PresetMetadata>(metadataJson)
                        : null;
                }
                catch
                {
                    metadata = null;
                }
            }


            // 解包资源：每次导入独立目录（预设名 + 时间戳 + 随机后缀），避免覆盖已有导入；
            // 同一秒内导入两个同名包（如远程连点两次下发）也不会解进同一个目录、互相抢文件。
            var importDir = Path.GetFullPath(Path.Combine(importRootDirectory,
                $"{Sanitize(preset.Name)}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}"));
            // 先登记为「导入中」再解包：从解包到装进预设列表之间（安装确认对话框、远程下发切回 UI 线程）
            // 这个目录还没有任何预设引用，清理导入目录时必须跳过它。
            var resourceEntries = archive.Entries
                .Where(e => e.FullName.StartsWith($"{ResourcesDirName}/", StringComparison.OrdinalIgnoreCase) &&
                            !e.FullName.EndsWith("/"))
                .ToList();
            if (resourceEntries.Count > MaxResourceEntries)
            {
                return new Result(false, $"预设包资源文件过多（{resourceEntries.Count} 个，上限 {MaxResourceEntries}），已拒绝导入。");
            }

            PendingImportDirectories.TryAdd(importDir, 0);
            var extracted = 0;
            var budget = maxExtractedBytes;
            foreach (var entry in resourceEntries)
            {
                if (ExtractEntry(entry, importDir, ref budget))
                {
                    extracted++;
                }

                if (budget < 0)
                {
                    // 超出解包上限：已解出的部分整体删掉，不留半截目录。
                    PendingImportDirectories.TryRemove(importDir, out _);
                    TryDeleteDirectory(importDir);
                    return new Result(false, $"预设包资源解包后超过 {maxExtractedBytes / (1024 * 1024)}MB，已中止导入。");
                }
            }

            if (extracted == 0)
            {
                // 没有资源就不会建目录，也就无需保护。
                PendingImportDirectories.TryRemove(importDir, out _);
            }
            else
            {
                ImportDirectories.AddOrUpdate(preset, importDir);
            }

            // 重写 resources/ 相对引用为本地绝对路径；其余残留路径（不应出现）清空。
            var settings = preset.Settings;
            settings.StyleSheetPath = ResolveResource(settings.StyleSheetPath, importDir);
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

    /// <summary>
    /// 把 zip 条目解压到 importDir（防路径穿越），返回是否成功。按实际写出的字节扣减 <paramref name="budget"/>，
    /// 扣到负数即停止写入（调用方据此中止整个导入）。
    /// </summary>
    private static bool ExtractEntry(ZipArchiveEntry entry, string importDir, ref long budget)
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
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                budget -= read;
                if (budget < 0)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读出 zip 条目的文本，按实际解压字节限制在 <see cref="MaxJsonBytes"/> 以内；超出返回 null。</summary>
    private static string? ReadBoundedText(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        return reader.ReadToEnd();
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // 删不掉不影响结果：目录不再登记为导入中，之后的导入资源清理会按「无引用」删除。
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
