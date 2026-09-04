using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace ClassIslandInjector;

/// <summary>
/// FFmpeg 安装包类型：动态壁纸（解码）与视频剪辑渲染（解码+编码）对编码器的要求不同，
/// 提供两档包体按需安装，最大化下载利用率。
/// </summary>
public enum FfmpegPackageKind
{
    /// <summary>精简解码包（约 7MB）：仅解码，满足动态壁纸、视频预览播放。无任何编码器。</summary>
    Minimal,

    /// <summary>完整包（约 50MB）：解码 + H.264/HEVC/AAC 编码，渲染剪辑、素材压缩转码必需。</summary>
    Full,
}

/// <summary>
/// FFmpeg 解码库运行时管理：检测插件所需的 FFmpeg 共享库（avcodec-63.dll 等，
/// 版本由 FFmpeg.AutoGen 的 LibraryVersionMap 决定）是否已安装，缺失时提供
/// 联机下载安装入口。
///
/// 库文件放在配置目录的 ffmpeg 子目录（用户数据目录，部署脚本不会清空），
/// 通过 <c>ffmpeg.RootPath</c> 指向该目录供 FFmpeg.AutoGen 加载。
///
/// 加载机制（已验证）：FFmpeg.AutoGen 是惰性加载——静态构造不加载任何 dll，
/// 首次调用 FFmpeg 函数时才按当时的 RootPath 查找并加载。因此：
///  - 文件缺失时「绝不调用任何 FFmpeg 函数」（否则失败委托会被缓存为占位，
///    之后即使装好库也要重启才能恢复）；
///  - 文件齐全后再设置 RootPath 并做一次轻量验证（<see cref="EnsureLoaded"/>）。
///
/// 包拆分（A∪B=C）：文件层面精简包与完整包是同一组 DLL（avcodec 同时含解码与
/// 编码，拆分只能靠构建裁剪），因此区分两档构建：精简包仅含解码器（动态壁纸够用），
/// 完整包额外含 libx264 等编码器（视频编辑器渲染/压缩转码必需）。运行中通过
/// <see cref="EncoderAvailable"/> 探测实际能力，剪辑功能缺失编码器时引导升级完整包。
/// </summary>
public static class FFmpegRuntime
{
    /// <summary>FFmpegVideoDecoder 实际使用的库名（avcodec 依赖 avutil+swresample，须一起部署）。</summary>
    private static readonly string[] RequiredLibraryNames = ["avcodec", "avformat", "avutil", "swscale", "swresample"];

    /// <summary>FFmpeg 共享库目录（配置目录\ffmpeg）。</summary>
    public static string LibraryDirectory { get; private set; } = string.Empty;

    /// <summary>内置默认下载源（精简解码包，用户自建镜像）。仅解码，动态壁纸够用。</summary>
    public const string DefaultSourceUrl = "https://xxtsoft.top/support/injector/ffmpeg-8.1-win64-shared-min-decode.zip";

    /// <summary>内置默认下载源（完整包，用户自建镜像）。解码 + 编码（libx264），剪辑渲染必需。</summary>
    public const string FullSourceUrl = "https://xxtsoft.top/support/injector/ffmpeg-8.1-win64-shared-full.zip";

    /// <summary>「重启后彻底删除」标记文件名（删除时库正被进程占用时写入，下次启动自动清空剩余文件）。</summary>
    private const string PendingDeleteFlag = "_delete-on-restart";

    /// <summary>是否已安装全部所需解码库。</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>
    /// 当前已加载的库是否带 H.264 编码器（完整包为 true，精简包为 false）。
    /// 仅在 <see cref="EnsureLoaded"/> 成功后探测一次；未加载时恒为 false。
    /// </summary>
    public static bool EncoderAvailable { get; private set; }

    /// <summary>缺失的库文件名列表（IsAvailable 为 false 时用于提示）。</summary>
    public static IReadOnlyList<string> MissingLibraries { get; private set; } = [];

    /// <summary>所需库文件名列表（按当前 FFmpeg.AutoGen 版本动态计算）。</summary>
    public static IReadOnlyList<string> RequiredFileNames { get; private set; } = [];

    /// <summary>最近一次加载验证失败的说明（dll 损坏等）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>当前 FFmpeg 版本系列（如 8.x），由 avcodec 主版本推断，用于下载提示。</summary>
    public static string FfmpegVersion => $"{GetFfmpegMajor()}.x";

    private static bool _loaded;

    /// <summary>当前进程是否已加载过 FFmpeg 库（dll 被加载后锁定，覆盖安装需重启宿主才能生效）。</summary>
    public static bool IsLoaded => _loaded;

    /// <summary>安装时覆盖所需 dll 失败：目标文件正被当前进程加载占用（如已装精简库后升级完整包）。</summary>
    private sealed class FfmpegLibraryInUseException : IOException
    {
        public FfmpegLibraryInUseException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>初始化：设置库目录并检测可用性。App 启动时调用一次。</summary>
    public static void Initialize(string libraryDirectory)
    {
        LibraryDirectory = libraryDirectory;
        // 上次「彻底删除」因库正被进程占用而残留了待删标记：此时尚未加载任何 dll，
        // 趁启动早期清空剩余文件（见 DeleteLibraries），保证删除真正彻底。
        PurgePendingDelete();
        Refresh();
    }

    /// <summary>
    /// 读取 FFmpeg.AutoGen 的库版本映射。读取会触发其静态构造，但静态构造是惰性的
    /// （已验证不加载 dll、不抛异常），因此安全。读取失败时回退 FFmpeg 9.0 的已知版本。
    /// </summary>
    private static Dictionary<string, int> GetLibraryVersionMap()
    {
        try
        {
            return new Dictionary<string, int>(FFmpeg.AutoGen.ffmpeg.LibraryVersionMap);
        }
        catch
        {
            return new Dictionary<string, int>
            {
                ["avcodec"] = 62, ["avformat"] = 62, ["avutil"] = 60,
                ["swresample"] = 6, ["swscale"] = 9,
            };
        }
    }

    /// <summary>重新检测可用性（启动与下载安装后调用）。仅检查文件存在，不做加载验证。</summary>
    public static void Refresh()
    {
        var versionMap = GetLibraryVersionMap();
        var required = RequiredLibraryNames
            .Where(versionMap.ContainsKey)
            .Select(name => $"{name}-{versionMap[name]}.dll")
            .ToList();
        RequiredFileNames = required;

        var missing = required
            .Where(fileName => !File.Exists(Path.Combine(LibraryDirectory, fileName)))
            .ToList();
        MissingLibraries = missing;
        IsAvailable = missing.Count == 0;
        if (!IsAvailable)
        {
            _loaded = false;
            EncoderAvailable = false;
        }
    }

    /// <summary>
    /// 若有「重启后删除」标记（上次彻底删除时库正被本进程占用），趁库尚未加载清空目录并移除标记。
    /// 启动早期没有任何 dll 被加载，此时删除不会被锁定，可实现“彻底删除”的最终落定。
    /// </summary>
    private static void PurgePendingDelete()
    {
        if (string.IsNullOrEmpty(LibraryDirectory) || !Directory.Exists(LibraryDirectory))
        {
            return;
        }

        var marker = Path.Combine(LibraryDirectory, PendingDeleteFlag);
        if (!File.Exists(marker))
        {
            return;
        }

        var allOk = true;
        foreach (var file in Directory.EnumerateFiles(LibraryDirectory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, marker, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 标记文件最后再删。
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                allOk = false; // 仍有文件被占用：保留标记，下次启动再清。
            }
        }

        if (allOk)
        {
            try
            {
                File.Delete(marker);
            }
            catch
            {
                // 标记删除失败不影响（下次启动再清）。
            }
        }
    }

    /// <summary>
    /// 彻底删除已安装的 FFmpeg 解码库（清空库目录并重置运行时状态）。
    /// 若当前进程已加载库（dll 被锁定）导致部分文件无法删除，会写入「重启后删除」标记，
    /// 下次启动时自动清空剩余文件——因此删除总能彻底完成，不受进程占用阻碍。
    /// </summary>
    public static (bool Success, string Message) DeleteLibraries()
    {
        if (string.IsNullOrEmpty(LibraryDirectory))
        {
            Refresh();
            return (true, "未安装 FFmpeg 解码库，无需删除。");
        }

        var locked = new List<string>();
        var deleted = 0;
        if (Directory.Exists(LibraryDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(LibraryDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), PendingDeleteFlag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    locked.Add(Path.GetFileName(file));
                }
            }
        }

        LastError = null;
        Refresh(); // 重置 IsAvailable / _loaded / EncoderAvailable / MissingLibraries。

        if (locked.Count == 0)
        {
            // 目录已清空，尝试删除整个 ffmpeg 目录。
            try
            {
                if (Directory.Exists(LibraryDirectory) && !Directory.EnumerateFileSystemEntries(LibraryDirectory).Any())
                {
                    Directory.Delete(LibraryDirectory, true);
                }
            }
            catch
            {
                // 目录删除失败不影响结果。
            }

            return (true, deleted == 0
                ? "未安装 FFmpeg 解码库，无需删除。"
                : $"已彻底删除 FFmpeg 解码库（{deleted} 个文件）。");
        }

        // 有文件正被进程占用：写「重启后删除」标记，下次启动自动清空剩余文件。
        var markerWritten = false;
        try
        {
            Directory.CreateDirectory(LibraryDirectory);
            File.WriteAllText(Path.Combine(LibraryDirectory, PendingDeleteFlag), DateTime.Now.ToString("O"));
            markerWritten = true;
        }
        catch
        {
            // 标记写入失败：只能提示重启后手动删除。
        }

        return (false,
            $"已删除 {deleted} 个文件；{locked.Count} 个文件正被当前进程占用（{string.Join("、", locked)}）。\n" +
            (markerWritten
                ? "重启 ClassIsland 后剩余文件会自动彻底清除。"
                : "请重启 ClassIsland 后再删除。"));
    }

    /// <summary>
    /// 确保 FFmpeg 已配置可加载：设置 RootPath 并触发一次轻量加载验证。
    /// 仅在 IsAvailable（文件齐全）时调用；失败说明 dll 损坏/依赖缺失，降级并提示重启。
    /// </summary>
    public static bool EnsureLoaded()
    {
        if (!IsAvailable)
        {
            return false;
        }

        if (_loaded)
        {
            return true;
        }

        try
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = LibraryDirectory;
            // 依次触发各库惰性加载：avutil（无依赖）→ avcodec(+swresample) → avformat → swscale。
            _ = FFmpeg.AutoGen.ffmpeg.av_version_info();
            _ = FFmpeg.AutoGen.ffmpeg.avcodec_version();
            _ = FFmpeg.AutoGen.ffmpeg.avformat_version();
            _ = FFmpeg.AutoGen.ffmpeg.swscale_version();
            _loaded = true;
            LastError = null;
            ProbeEncoder();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsAvailable = false;
            _loaded = false;
            EncoderAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// 探测已加载的 avcodec 是否带 H.264 编码器（区分精简解码包与完整包）。
    /// 必须在 EnsureLoaded 成功（库已可加载）之后调用：无编码器时返回 null，安全不抛。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void ProbeEncoder()
    {
        try
        {
            var codec = FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder(
                FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264);
            EncoderAvailable = codec != null;
        }
        catch
        {
            EncoderAvailable = false;
        }
    }

    /// <summary>
    /// 联机下载并安装 FFmpeg 共享库到配置目录（<paramref name="kind"/> 决定包体档位）。
    /// 依次尝试多个源（用户自定义 → xxtsoft 自建镜像 → GitHub BtbN → ghps 代理 → gyan），
    /// 解压后把所需 dll 复制到库目录并重新检测。全部失败时给出手动放置指引。
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => await InstallAsync(FfmpegPackageKind.Minimal, progress, cancellationToken);

    /// <summary>按包档位安装（精简包 = 仅解码；完整包 = 解码+编码）。供安装器窗口按用户选择调用。</summary>
    public static async Task<(bool Success, string Message)> InstallAsync(
        FfmpegPackageKind kind,
        IProgress<FfmpegInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(LibraryDirectory);
            var githubUrls = BuildGithubCandidates().ToList();
            var sources = new List<string>();
            // 用户设置的自定义源最优先；随后是内置默认源（xxtsoft 自建镜像，按档位取 URL，
            // 精简档回退旧命名 min.zip 兼容早期镜像）；都失败再回退 GitHub / 代理 / gyan。
            var customUrl = InjectorRuntime.Settings.CustomFfmpegDownloadUrl;
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                sources.Add(customUrl);
            }

            sources.Add(kind == FfmpegPackageKind.Full ? FullSourceUrl : DefaultSourceUrl);
            if (kind == FfmpegPackageKind.Minimal)
            {
                // 旧镜像文件名兼容（重命名为 min-decode.zip 前的地址）。
                sources.Add("https://xxtsoft.top/support/injector/ffmpeg-8.1-win64-shared-min.zip");
            }

            foreach (var githubUrl in githubUrls)
            {
                sources.Add(githubUrl);
                sources.Add("https://ghps.cc/" + githubUrl);
            }
            sources.Add("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");

            var packageTitle = kind == FfmpegPackageKind.Full ? "完整包（解码+编码）" : "精简解码包";

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (var url in sources)
            {
                var sourceHost = new Uri(url).Host;
                Report(progress, "正在连接下载源…", indeterminate: true,
                    log: $"→ 尝试从 {sourceHost} 获取 FFmpeg {FfmpegVersion} {packageTitle}");
                var tempZip = Path.Combine(Path.GetTempPath(), $"classisland-injector-ffmpeg-{Guid.NewGuid():N}.zip");
                try
                {
                    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? 0;
                        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        await using (var file = File.Create(tempZip))
                        {
                            var buffer = new byte[81920];
                            long downloaded = 0;
                            var sw = Stopwatch.StartNew();
                            var window = new Queue<(long Bytes, double Seconds)>();
                            long lastReportAt = 0;
                            while (true)
                            {
                                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                                if (read == 0)
                                {
                                    break;
                                }

                                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                downloaded += read;

                                // 速度滑动窗口（最近约 3 秒）。
                                window.Enqueue((read, sw.Elapsed.TotalSeconds));
                                while (window.Count > 0 && sw.Elapsed.TotalSeconds - window.Peek().Seconds > 3.0)
                                {
                                    window.Dequeue();
                                }

                                var windowStart = window.Count > 0 ? window.Peek().Seconds : sw.Elapsed.TotalSeconds;
                                var windowSpan = Math.Max(sw.Elapsed.TotalSeconds - windowStart, 0.1);
                                var speed = window.Sum(w => w.Bytes) / windowSpan;
                                var remaining = totalBytes > 0 && speed > 0
                                    ? TimeSpan.FromSeconds((totalBytes - downloaded) / speed)
                                    : (TimeSpan?)null;
                                // 节流上报（约 150ms 一次），避免高频回调压垮 UI。
                                if (sw.ElapsedMilliseconds - lastReportAt >= 150 || downloaded >= totalBytes)
                                {
                                    lastReportAt = sw.ElapsedMilliseconds;
                                    Report(progress, "正在下载…", downloaded, totalBytes, speed, remaining,
                                        indeterminate: totalBytes <= 0);
                                }
                            }
                        }
                    }

                    Report(progress, "正在解压 FFmpeg 解码库…", indeterminate: true, log: "→ 下载完成，正在解压…");
                    List<string>? installed;
                    try
                    {
                        installed = ExtractRequiredLibraries(tempZip, kind == FfmpegPackageKind.Full);
                    }
                    catch (FfmpegLibraryInUseException ex)
                    {
                        // 目标 dll 正被当前进程加载占用，换任何下载源都同样无法覆盖，直接给出明确指引。
                        Report(progress, "安装失败：库文件被占用", indeterminate: true, log: "✗ " + ex.Message);
                        return (false, ex.Message);
                    }

                    if (installed != null)
                    {
                        Refresh();
                        if (IsAvailable)
                        {
                            Report(progress, "安装完成", indeterminate: true, log: $"已安装 {packageTitle}（{installed.Count} 个文件）");
                            return (true, $"FFmpeg {packageTitle} 安装完成（{installed.Count} 个文件）。");
                        }
                    }
                    else
                    {
                        Report(progress, "版本不匹配，尝试下一源…", indeterminate: true,
                            log: "包内不包含所需版本的解码库");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Report(progress, $"{sourceHost} 下载失败，尝试下一源…", indeterminate: true, log: $"✗ {ex.Message}");
                }
                finally
                {
                    try
                    {
                        File.Delete(tempZip);
                    }
                    catch
                    {
                        // 临时文件删除失败忽略
                    }
                }
            }

            Refresh();
            var missing = string.Join("、", MissingLibraries);
            return (false,
                $"所有下载源均失败。请检查网络后重试，或手动下载 FFmpeg {FfmpegVersion} 共享库\n" +
                $"（{missing}）放入：{LibraryDirectory}\n然后重新打开本设置页即可生效。");
        }
        catch (OperationCanceledException)
        {
            return (false, "下载已取消。");
        }
        catch (Exception ex)
        {
            return (false, $"下载失败：{ex.Message}");
        }
    }

    /// <summary>按 avcodec 主版本推断 FFmpeg 主版本（61→7、62→8、63→9）。</summary>
    private static int GetFfmpegMajor()
    {
        var versionMap = GetLibraryVersionMap();
        return versionMap.TryGetValue("avcodec", out var v) ? v - 54 : 9;
    }

    /// <summary>
    /// 构造 BtbN/FFmpeg-Builds latest release 的共享库 zip 候选地址（版本动态匹配）。
    /// avcodec 主版本无法唯一确定 FFmpeg minor（62 同时对应 8.0/8.1），因此生成多个精确候选依次尝试。
    /// </summary>
    private static IEnumerable<string> BuildGithubCandidates()
    {
        var major = GetFfmpegMajor();
        var known = new[] { "9.0", "8.1", "8.0", "7.1", "7.0" };
        foreach (var version in known.Where(v => v.StartsWith($"{major}.", StringComparison.Ordinal)))
        {
            yield return $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/" +
                         $"ffmpeg-n{version}-latest-win64-gpl-shared-{version}.zip";
        }

        // 兜底：未知 minor 时按 major.0 构造（可能 404，由下一候选兜底）。
        yield return $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/" +
                     $"ffmpeg-n{major}.0-latest-win64-gpl-shared-{major}.0.zip";
    }

    /// <summary>
    /// 从 zip 中提取所需 dll 到库目录；缺少任一所需文件返回 null。
    /// <paramref name="includeEncoders"/>（完整包）时额外提取 libx264*.dll 等
    /// 外置编码器依赖（部分构建把 x264 链接为独立 dll；BtbN 为静态链接，无此文件也无害）。
    /// </summary>
    private static List<string>? ExtractRequiredLibraries(string zipPath, bool includeEncoders = false)
    {
        var required = RequiredFileNames;
        var installed = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var match = required.FirstOrDefault(r => r.Equals(fileName, StringComparison.OrdinalIgnoreCase));
                var isEncoderExtra = includeEncoders &&
                                     fileName.StartsWith("libx264", StringComparison.OrdinalIgnoreCase);
                if (match == null && !isEncoderExtra)
                {
                    continue;
                }

                entry.ExtractToFile(Path.Combine(LibraryDirectory, match ?? fileName), overwrite: true);
                var target = match ?? fileName;
                if (!installed.Contains(target))
                {
                    installed.Add(target);
                }
            }
        }
        catch (IOException ex)
        {
            // 覆盖写入失败 = 目标 dll 正被当前进程加载占用（已装精简库后覆盖完整包）。
            // 换任何下载源结果都一样，抛给 InstallAsync 给出明确指引，不再误报“版本不匹配”。
            throw new FfmpegLibraryInUseException(
                "所需解码库文件正被当前进程占用（已加载 FFmpeg 库），无法覆盖写入。\n" +
                "请重启 ClassIsland 后再安装。", ex);
        }
        catch
        {
            return null; // zip 损坏
        }

        return installed.Count >= required.Count ? installed : null;
    }

    /// <summary>报告一条安装进度（阶段/字节/速度/ETA/日志）。</summary>
    private static void Report(IProgress<FfmpegInstallProgress>? progress, string stage,
        long downloaded = 0, long total = 0, double speedBytesPerSecond = 0, TimeSpan? remaining = null,
        bool indeterminate = true, string? log = null)
    {
        progress?.Report(new FfmpegInstallProgress
        {
            Stage = stage,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            SpeedBytesPerSecond = speedBytesPerSecond,
            Remaining = remaining,
            Indeterminate = indeterminate,
            LogLine = log,
        });
    }
}

/// <summary>FFmpeg 安装进度快照（安装器窗口消费）。</summary>
public sealed class FfmpegInstallProgress
{
    /// <summary>当前阶段描述（如「正在下载…」「正在解压…」）。</summary>
    public string Stage { get; init; } = string.Empty;
    /// <summary>已下载字节数。</summary>
    public long DownloadedBytes { get; init; }
    /// <summary>总字节数（0 表示未知，进度条进入不确定模式）。</summary>
    public long TotalBytes { get; init; }
    /// <summary>实时下载速度（字节/秒）。</summary>
    public double SpeedBytesPerSecond { get; init; }
    /// <summary>剩余时间估算（依据当前速度）。</summary>
    public TimeSpan? Remaining { get; init; }
    /// <summary>进度是否不确定（非下载阶段或总大小未知）。</summary>
    public bool Indeterminate { get; init; } = true;
    /// <summary>追加一行安装日志（可为 null）。</summary>
    public string? LogLine { get; init; }
}
