using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace ClassIslandInjector;

/// <summary>
/// 预设商店索引中的一个预设条目。字段与 <c>Defaults/preset-index.sample.json</c> 的约定一致
/// （服务端 JSON 为 camelCase，读取大小写不敏感；多余字段被忽略，缺失字段取默认值）。
/// </summary>
public sealed class StorePresetEntry
{
    /// <summary>预设唯一 Id（商店内标识，用于下载缓存与已安装记录）。</summary>
    public string Id { get; set; } = "";

    /// <summary>预设名称（与包内 preset.json 的 Name 一致）。</summary>
    public string Name { get; set; } = "";

    /// <summary>作者。</summary>
    public string Author { get; set; } = "";

    /// <summary>作者所属学校 / 组织（可选）。</summary>
    public string School { get; set; } = "";

    /// <summary>描述（卡片 / 详情页展示）。</summary>
    public string Description { get; set; } = "";

    /// <summary>打包时的插件版本（参考信息）。</summary>
    public string PluginVersion { get; set; } = "";

    /// <summary>要求的最低插件版本；当前插件低于该版本时禁止下载。</summary>
    public string MinPluginVersion { get; set; } = "";

    /// <summary>上架时间（ISO 8601）。</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>预设包（.cizip，即 <see cref="PresetExchange"/> 导出的 zip）下载地址。</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>预览图（PNG）地址；主界面截图。</summary>
    public string PreviewUrl { get; set; } = "";

    /// <summary>预设包大小（字节；0 = 未知）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>下载次数（可选字段，服务端未提供时为 0；热门排序用）。</summary>
    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    /// <summary>解析后的上架时间；格式非法时返回 null。</summary>
    [JsonIgnore]
    public DateTime? Created => DateTime.TryParse(CreatedAt, out var t) ? t.ToLocalTime() : null;

    /// <summary>大小展示文本（「12.3 MB」；未知时「—」）。</summary>
    [JsonIgnore]
    public string SizeText => SizeBytes switch
    {
        <= 0 => "—",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:0.#} MB"
    };

    /// <summary>作者展示文本（作者 + 可选学校）。</summary>
    [JsonIgnore]
    public string AuthorText => string.IsNullOrWhiteSpace(School)
        ? (string.IsNullOrWhiteSpace(Author) ? "未知作者" : Author)
        : $"{Author} · {School}";
}

/// <summary>预设商店索引文件（schemaVersion 1）。</summary>
public sealed class StoreIndex
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>索引来源地址（服务端可省略，客户端会回填）。</summary>
    public string? IndexUrl { get; set; }

    /// <summary>索引生成时间（ISO 8601）。</summary>
    public string? UpdatedAt { get; set; }

    public List<StorePresetEntry> Presets { get; set; } = [];
}

/// <summary>预设安装记录（store/installed.json）：记录「已从商店安装过哪个预设」，供已安装检测与更新提示。</summary>
public sealed class StoreInstallRecord
{
    /// <summary>商店预设 Id。</summary>
    public string Id { get; set; } = "";

    /// <summary>导入后的实际预设名（可能与商店名不同：重名时插件会自动追加 GUID 后缀）。</summary>
    public string InstalledName { get; set; } = "";

    /// <summary>安装时索引条目的 CreatedAt（检测「商店端有更新」用）。</summary>
    public string SourceCreatedAt { get; set; } = "";

    /// <summary>本地安装时间（ISO 8601）。</summary>
    public string InstalledAt { get; set; } = "";
}

/// <summary>
/// 预设商店联机服务：抓取 / 缓存商店索引、下载并缓存预览图、下载预设包（.cizip）、
/// 维护已安装记录、检查插件版本兼容性。所有网络操作都返回 Task，不阻塞 UI。
/// </summary>
internal static class PresetStoreService
{
    /// <summary>默认商店索引地址（与 <c>Defaults/preset-index.sample.json</c> 中约定一致，写死无需用户填写）。</summary>
    public const string DefaultIndexUrl = "https://xxtsoft.top/support/injector/presets/index.json";

    /// <summary>索引缓存有效期（该时间内直接使用磁盘缓存，过期后后台重新拉取）。</summary>
    private static readonly TimeSpan IndexCacheLifetime = TimeSpan.FromMinutes(15);

    private static readonly HttpClient IndexHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>索引 / 预览 / 安装记录的 JSON 序列化配置（写入 camelCase 与服务端一致；读取大小写不敏感）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>预览图内存缓存（id → PNG 字节），避免窗口内重复下载/解码。</summary>
    private static readonly Dictionary<string, byte[]> PreviewMemoryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>预览图下载并发限制（打开商店时一次渲染约 20 张卡片，避免瞬时打满连接）。</summary>
    private static readonly SemaphoreSlim PreviewGate = new(6);

    /// <summary>商店数据根目录（配置目录\store）。</summary>
    public static string StoreDirectory => Path.Combine(InjectorRuntime.ConfigDirectory, "store");

    /// <summary>磁盘索引缓存路径。</summary>
    public static string IndexCachePath => Path.Combine(StoreDirectory, "index.json");

    /// <summary>预览图磁盘缓存目录。</summary>
    public static string PreviewCacheDirectory => Path.Combine(StoreDirectory, "previews");

    /// <summary>预设包下载目录。</summary>
    public static string DownloadDirectory => Path.Combine(StoreDirectory, "downloads");

    /// <summary>已安装记录路径。</summary>
    public static string InstallRecordsPath => Path.Combine(StoreDirectory, "installed.json");

    /// <summary>当前插件版本（manifest 声明；读取失败时为空串，视为不做版本限制）。</summary>
    public static string CurrentPluginVersion => Plugin.Manifest?.Version ?? "";

    /// <summary>
    /// 获取商店索引：优先磁盘缓存（15 分钟内有效），过期/强制时联机刷新；
    /// 联机失败时回退到磁盘缓存（哪怕过期），保证离线也能看到上次的内容。
    /// </summary>
    /// <param name="forceRefresh">跳过缓存有效期直接联机。</param>
    /// <returns>索引；索引不可用（联机失败且无缓存）时返回 null。</returns>
    public static async Task<StoreIndex?> FetchIndexAsync(bool forceRefresh = false)
    {
        var cacheUsable = File.Exists(IndexCachePath) &&
                          DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexCachePath) < IndexCacheLifetime;
        if (cacheUsable && !forceRefresh)
        {
            var cached = TryLoadIndexFile(IndexCachePath);
            if (cached != null)
            {
                return cached;
            }
        }

        try
        {
            var json = await IndexHttp.GetStringAsync(DefaultIndexUrl);
            var index = JsonSerializer.Deserialize<StoreIndex>(json, JsonOptions);
            if (index == null)
            {
                return TryLoadIndexFile(IndexCachePath);
            }

            index.IndexUrl = DefaultIndexUrl;
            Directory.CreateDirectory(StoreDirectory);
            await File.WriteAllTextAsync(IndexCachePath, JsonSerializer.Serialize(index, JsonOptions));
            return index;
        }
        catch (Exception)
        {
            // 联机失败：回退磁盘缓存（哪怕过期），保证离线也能浏览上次的内容。
            return TryLoadIndexFile(IndexCachePath);
        }
    }

    /// <summary>磁盘索引缓存是否可用（离线兜底提示用）。</summary>
    public static bool IsCacheAvailable() => File.Exists(IndexCachePath);

    /// <summary>读取磁盘索引缓存；损坏时返回 null 并删除坏文件。</summary>
    private static StoreIndex? TryLoadIndexFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var index = JsonSerializer.Deserialize<StoreIndex>(File.ReadAllText(path), JsonOptions);
            if (index != null)
            {
                index.IndexUrl ??= DefaultIndexUrl;
                return index;
            }
        }
        catch (Exception)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 删除失败无碍。
            }
        }

        return null;
    }

    /// <summary>
    /// 获取预设预览图（PNG 字节）：内存缓存 → 磁盘缓存 → 联机下载。
    /// 失败返回 null（UI 显示占位符）。
    /// </summary>
    public static async Task<byte[]?> FetchPreviewAsync(StorePresetEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PreviewUrl))
        {
            return null;
        }

        lock (PreviewMemoryCache)
        {
            if (PreviewMemoryCache.TryGetValue(entry.Id, out var cached))
            {
                return cached;
            }
        }

        var diskPath = PreviewCachePath(entry.Id);
        try
        {
            if (File.Exists(diskPath) && new FileInfo(diskPath).Length > 0)
            {
                var bytes = await File.ReadAllBytesAsync(diskPath);
                lock (PreviewMemoryCache)
                {
                    PreviewMemoryCache[entry.Id] = bytes;
                }

                return bytes;
            }
        }
        catch (Exception)
        {
            // 读缓存失败则继续联机。
        }

        try
        {
            await PreviewGate.WaitAsync();
            try
            {
                var data = await IndexHttp.GetByteArrayAsync(entry.PreviewUrl);
                if (data.Length == 0)
                {
                    return null;
                }

                Directory.CreateDirectory(PreviewCacheDirectory);
                await File.WriteAllBytesAsync(diskPath, data);
                lock (PreviewMemoryCache)
                {
                    PreviewMemoryCache[entry.Id] = data;
                }

                return data;
            }
            finally
            {
                PreviewGate.Release();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>预览图磁盘缓存路径（按 Id 命名；Id 清洗为安全文件名）。</summary>
    private static string PreviewCachePath(string id)
    {
        var safe = SanitizeFileName(string.IsNullOrWhiteSpace(id) ? "preset" : id);
        return Path.Combine(PreviewCacheDirectory, $"{safe}.png");
    }

    /// <summary>
    /// 下载预设包（.cizip）到本地下载目录；同名文件已存在时直接复用（商店包为静态内容）。
    /// 进度通过 <paramref name="progress"/> 上报（received, total?）。
    /// </summary>
    /// <returns>下载的预设包路径；失败时抛异常（由 UI 层捕获提示）。</returns>
    public static async Task<string> DownloadPresetAsync(
        StorePresetEntry entry, IProgress<(long Received, long? Total)>? progress)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
        {
            throw new InvalidOperationException("该预设未提供下载地址。");
        }

        Directory.CreateDirectory(DownloadDirectory);
        var target = Path.Combine(DownloadDirectory, $"{SanitizeFileName(entry.Id)}.cizip");
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            progress?.Report((new FileInfo(target).Length, new FileInfo(target).Length));
            return target;
        }

        var partPath = target + ".part";
        using var response = await DownloadHttp.GetAsync(
            entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(partPath);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await httpStream.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            // 限频：每 100ms 上报一次，避免 UI 刷新过密。
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report((received, total));
            }
        }

        await output.FlushAsync();
        output.Close();

        // 校验 zip 可读（防止下载到 HTML 错误页等），失败即删除半成品。
        if (!IsReadableZip(partPath))
        {
            File.Delete(partPath);
            throw new InvalidDataException("下载内容不是有效的预设包。");
        }

        File.Move(partPath, target);
        progress?.Report((received, received));
        return target;
    }

    /// <summary>快速判断文件是否可被 ZipArchive 打开（下载校验）。</summary>
    private static bool IsReadableZip(string path)
    {
        try
        {
            using var archive = new System.IO.Compression.ZipArchive(File.OpenRead(path), System.IO.Compression.ZipArchiveMode.Read);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    #region 已安装记录

    /// <summary>读取全部安装记录（文件缺失/损坏时返回空列表）。</summary>
    public static List<StoreInstallRecord> LoadInstallRecords()
    {
        try
        {
            if (!File.Exists(InstallRecordsPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<StoreInstallRecord>>(File.ReadAllText(InstallRecordsPath), JsonOptions) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>预设是否已从商店安装过。</summary>
    public static bool IsInstalled(string id) =>
        LoadInstallRecords().Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>商店端是否已更新（已安装但索引条目的上架时间比安装记录的更新）。</summary>
    public static bool HasUpdate(StorePresetEntry entry)
    {
        var record = LoadInstallRecords()
            .FirstOrDefault(r => string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            return false;
        }

        return DateTime.TryParse(entry.CreatedAt, out var remote) &&
               DateTime.TryParse(record.SourceCreatedAt, out var local) &&
               remote > local;
    }

    /// <summary>记录一次安装（覆盖同 Id 记录）。</summary>
    public static void MarkInstalled(StorePresetEntry entry, string installedName)
    {
        var records = LoadInstallRecords();
        records.RemoveAll(r => string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
        records.Add(new StoreInstallRecord
        {
            Id = entry.Id,
            InstalledName = installedName,
            SourceCreatedAt = entry.CreatedAt,
            InstalledAt = DateTime.Now.ToString("O")
        });
        Directory.CreateDirectory(StoreDirectory);
        File.WriteAllText(InstallRecordsPath, JsonSerializer.Serialize(records, JsonOptions));
    }

    #endregion

    /// <summary>
    /// 检查当前插件版本是否满足条目要求。minPluginVersion 为空视为无限制；
    /// 任一端版本号解析失败时视为满足（避免因服务端格式问题直接禁用）。
    /// </summary>
    public static bool IsCompatible(StorePresetEntry entry, out string minVersion)
    {
        minVersion = entry.MinPluginVersion?.Trim() ?? "";
        if (minVersion.Length == 0)
        {
            return true;
        }

        if (!Version.TryParse(minVersion, out var min) ||
            !Version.TryParse(CurrentPluginVersion, out var current))
        {
            return true;
        }

        return current >= min;
    }

    /// <summary>把字符串清洗为安全文件名（非法字符替换为「_」并截断长度）。</summary>
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var text = new string(chars).Trim();
        return text.Length <= 64 ? text : text[..64];
    }
}
