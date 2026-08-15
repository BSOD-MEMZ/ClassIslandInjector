using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 在线贴纸选择窗口：从 sekai-stickers 仓库的 public/img 目录拉取角色/主题文件夹，
/// 顶部用下拉框筛选文件夹，网格展示贴纸缩略图；点击一张即下载到本地缓存并回调插入图层。
///
/// 网络策略（解决 GitHub API 403 限流）：
/// - 仓库已于 2023-09 归档（只读），顶层文件夹与各文件夹内文件列表固定不变 → 可安全缓存；
/// - 文件夹列表优先走本地缓存，其次 API，最后内置兜底列表（已知全部角色文件夹）；
/// - 各文件夹文件列表优先本地缓存，其次 API，再其次 GitHub 树页面 HTML（不走 API，不受限流）；
/// - 缩略图/贴纸直接走 raw.githubusercontent.com 直链（CDN，不受 API 限流），并缓存到本地磁盘；
/// - 全部网络请求都带 User-Agent（GitHub 强制要求，否则 403）。
/// </summary>
internal sealed class StickerPickerWindow : MyWindow
{
    private const string RepoOwner = "TheOriginalAyaka";
    private const string RepoName = "sekai-stickers";
    private const string RepoBranch = "main";
    private const string RepoPath = "public/img";
    /// <summary>GitHub contents API（仅用于拉取目录/文件列表，限流后由缓存/HTML 兜底）。</summary>
    private const string StickersRootApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/contents/{RepoPath}";
    /// <summary>raw.githubusercontent.com 直链前缀（CDN，不限流，用于下载缩略图与贴纸）。</summary>
    private const string RawRoot = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{RepoBranch}/{RepoPath}";

    /// <summary>
    /// 内置兜底文件夹列表（仓库已归档，顶层文件夹固定；API 限流且无缓存时保证窗口仍可用）。
    /// </summary>
    private static readonly string[] KnownFolders =
    [
        "Haruka", "Honami", "Ichika", "KAITO", "Kanade", "Kohane", "Len", "Luka",
        "Mafuyu", "Meiko", "Miku", "Minori", "Mizuki", "Nene", "Rin", "Rui",
        "Saki", "Shiho", "Shizuku", "Touya", "Tsukasa", "airi", "akito", "an", "emu", "ena"
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>选中贴纸后的回调（参数：本地缓存路径、显示名）。</summary>
    private readonly Action<string, string> _onPick;

    /// <summary>文件夹选择下拉框（文件夹多，用下拉比选项卡更稳妥）。</summary>
    private readonly ComboBox _folderBox = new()
    {
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };

    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    /// <summary>缩略图并发下载数（GitHub 限制，避免一次性几十个请求）。</summary>
    private readonly SemaphoreSlim _throttle = new(6);
    /// <summary>贴纸名 → 缩略图。</summary>
    private readonly Dictionary<string, Bitmap> _thumbs = [];
    /// <summary>贴纸名 → 图片流（保持引用，切换文件夹时统一释放）。</summary>
    private readonly Dictionary<string, Stream> _thumbStreams = [];
    /// <summary>贴纸名 → 下载的字节（插入时直接写缓存文件，避免二次下载）。</summary>
    private readonly Dictionary<string, byte[]> _bytesCache = [];
    /// <summary>贴纸名 → 网格缩略图控件。</summary>
    private readonly Dictionary<string, Image> _tiles = [];
    private readonly string _cacheDir;
    /// <summary>文件列表缓存目录（folders.json + {folder}.json）。</summary>
    private readonly string _listCacheDir;
    /// <summary>缩略图字节缓存目录。</summary>
    private readonly string _thumbCacheDir;
    private string _currentFolder = "";
    /// <summary>加载令牌：切换文件夹后旧任务的下载结果丢弃。</summary>
    private int _loadToken;

    /// <summary>当前打开的贴纸选择窗口（单例，已打开时聚焦）。</summary>
    public static StickerPickerWindow? Current { get; private set; }

    public StickerPickerWindow(Action<string, string> onPick)
    {
        _onPick = onPick;
        Title = "添加贴纸";
        Width = 760;
        Height = 580;
        MinWidth = 500;
        MinHeight = 400;
        EditorMica.EnableMica(this);
        _cacheDir = Path.Combine(InjectorRuntime.ConfigDirectory, "stickers");
        _listCacheDir = Path.Combine(_cacheDir, "lists");
        _thumbCacheDir = Path.Combine(_cacheDir, "thumbs");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_listCacheDir);
        Directory.CreateDirectory(_thumbCacheDir);
        _scroller.Content = _grid;

        // 顶部：说明 + 文件夹下拉 + 刷新按钮。
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadFoldersAsync();
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                new TextBlock { Text = "贴纸库", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 },
                _folderBox,
                refresh
            }
        };
        _folderBox.SelectionChanged += async (_, _) =>
        {
            if (_folderBox.SelectedItem is string { Length: > 0 } folder)
            {
                _currentFolder = folder;
                await LoadFolderAsync(folder);
            }
        };

        var root = new DockPanel
        {
            Children =
            {
                header,
                _status,
                _scroller
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(0, 8, 0, 0);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = root
        };

        // 单例跟踪。
        Current = this;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };

        Loaded += async (_, _) => await LoadFoldersAsync();
    }

    /// <summary>GitHub contents API 返回的目录/文件项。</summary>
    private sealed record FolderEntry(string Name, string Type, string? DownloadUrl)
    {
        public override string ToString() => Name;
    }

    // ============ 网络与缓存 ============

    /// <summary>GitHub contents API 拉取目录项；任何失败（含限流 403）返回 null。</summary>
    private static async Task<List<FolderEntry>?> ListContentsAsync(string url)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ClassIslandInjector");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var list = new List<FolderEntry>();
            foreach (var element in json.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
                var type = element.TryGetProperty("type", out var t) ? t.GetString() : null;
                var downloadUrl = element.TryGetProperty("download_url", out var d) ? d.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type))
                {
                    list.Add(new FolderEntry(name, type, downloadUrl));
                }
            }

            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>带 User-Agent 的 GET 下载（GitHub 要求所有请求都带，否则 403）。</summary>
    private static async Task<byte[]> DownloadBytesAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ClassIslandInjector");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string RawThumbUrl(string folder, string file) =>
        $"{RawRoot}/{Uri.EscapeDataString(folder)}/{Uri.EscapeDataString(file)}";

    /// <summary>取贴纸图片字节：本地缓存 → raw.githubusercontent.com 直链（CDN，不受 API 限流）。</summary>
    private async Task<byte[]?> LoadThumbBytesAsync(string folder, string file)
    {
        var path = ThumbCachePath(folder, file);
        if (File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path);
            }
            catch
            {
                // 缓存损坏时忽略，走网络重新下载。
            }
        }

        try
        {
            var bytes = await DownloadBytesAsync(RawThumbUrl(folder, file));
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch
            {
                // 缓存写入失败不影响使用。
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GitHub 树页面 HTML 兜底：解析 react-app.embeddedData 里的文件列表（不走 API，不受限流）。</summary>
    private static async Task<List<string>> ScrapeFolderFilesAsync(string folder)
    {
        try
        {
            var url = $"https://github.com/{RepoOwner}/{RepoName}/tree/{RepoBranch}/{RepoPath}/{Uri.EscapeDataString(folder)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ClassIslandInjector");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html = await response.Content.ReadAsStringAsync();
            const string marker = "data-target=\"react-app.embeddedData\">";
            var idx = html.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return [];
            }

            var start = idx + marker.Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return [];
            }

            using var doc = JsonDocument.Parse(html.Substring(start, end - start));
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("codeViewTreeRoute", out var route) ||
                !route.TryGetProperty("tree", out var tree) ||
                !tree.TryGetProperty("items", out var items))
            {
                return [];
            }

            var result = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameNode) || nameNode.GetString() is not { } name)
                {
                    continue;
                }

                var isDir = item.TryGetProperty("contentType", out var ct) && ct.GetString() == "directory";
                if (!isDir && IsImageName(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsImageName(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取文件列表缓存（字符串数组；失败返回空）。</summary>
    private List<string> LoadListCache(string key)
    {
        try
        {
            var path = Path.Combine(_listCacheDir, key);
            if (!File.Exists(path))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
                {
                    result.Add(s);
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>写文件列表缓存（失败不影响使用）。</summary>
    private void SaveListCache(string key, List<string> names)
    {
        try
        {
            File.WriteAllText(Path.Combine(_listCacheDir, key), JsonSerializer.Serialize(names));
        }
        catch
        {
            // 缓存失败不影响使用。
        }
    }

    private string ThumbCachePath(string folder, string file)
    {
        var safeFolder = string.Concat(folder.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var safeFile = string.Concat(file.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return Path.Combine(_thumbCacheDir, $"{safeFolder}__{safeFile}");
    }

    // ============ 加载流程 ============

    /// <summary>拉取根目录文件夹列表并填充下拉框：本地缓存 → API → 内置兜底列表。</summary>
    private async Task LoadFoldersAsync()
    {
        _status.Text = "正在获取贴纸库文件夹…";
        var entries = await ListContentsAsync(StickersRootApi);
        if (entries is { Count: > 0 })
        {
            var folders = entries.Where(f => f.Type == "dir").Select(f => f.Name).ToList();
            if (folders.Count > 0)
            {
                SaveListCache("folders.json", folders);
                ApplyFolders(folders, "已加载贴纸库文件夹。");
                return;
            }
        }

        // API 失败（通常为限流 403）：本地缓存 → 内置兜底列表，保证窗口仍可用。
        var cached = LoadListCache("folders.json");
        if (cached.Count > 0)
        {
            ApplyFolders(cached, "GitHub 接口暂不可用（可能限流），已使用本地缓存列表。");
        }
        else
        {
            ApplyFolders(KnownFolders.ToList(), "GitHub 接口暂不可用（可能限流），已使用内置列表。");
        }
    }

    private void ApplyFolders(List<string> folders, string statusText)
    {
        _folderBox.ItemsSource = folders;
        _folderBox.SelectedItem = folders.FirstOrDefault();
        if (folders.Count == 0)
        {
            _status.Text = "贴纸库中没有文件夹，可能网络不可用或仓库结构已变化。";
        }
        else
        {
            _status.Text = statusText;
        }
    }

    /// <summary>加载指定文件夹的贴纸列表并铺网格缩略图：本地缓存 → API → 树页面 HTML 兜底。</summary>
    private async Task LoadFolderAsync(string folder)
    {
        var token = ++_loadToken;
        ClearGrid();
        _status.Text = "正在加载贴纸列表…";

        var cacheKey = $"{folder}.json";
        // 仓库已归档且只读，本地缓存永远有效：命中即直接用，不访问网络。
        var cached = LoadListCache(cacheKey);
        List<FolderEntry>? files = cached.Count > 0
            ? cached.Select(n => new FolderEntry(n, "file", null)).ToList()
            : await FetchFolderFilesAsync(folder, cacheKey);

        if (token != _loadToken)
        {
            return;
        }

        if (files is not { Count: > 0 })
        {
            _status.Text = "该文件夹没有图片（或网络不可用）。";
            return;
        }

        _status.Text = $"共 {files.Count} 张贴纸，正在加载预览…";
        foreach (var file in files)
        {
            _grid.Children.Add(BuildTile(file.Name));
        }

        _ = DownloadThumbnailsAsync(files, token);
    }

    /// <summary>无缓存时联网获取文件夹文件列表：contents API → 树页面 HTML 兜底，成功后写缓存。</summary>
    private async Task<List<FolderEntry>?> FetchFolderFilesAsync(string folder, string cacheKey)
    {
        // 1) GitHub contents API
        var entries = await ListContentsAsync($"{StickersRootApi}/{Uri.EscapeDataString(folder)}");
        if (entries is { Count: > 0 })
        {
            var files = entries.Where(f => f.Type == "file").ToList();
            if (files.Count > 0)
            {
                SaveListCache(cacheKey, files.Select(f => f.Name).ToList());
                return files;
            }
        }

        // 2) GitHub 树页面 HTML 兜底（不走 API，不受接口限流影响）
        var scraped = await ScrapeFolderFilesAsync(folder);
        if (scraped.Count > 0)
        {
            SaveListCache(cacheKey, scraped);
            return scraped.Select(n => new FolderEntry(n, "file", null)).ToList();
        }

        return null;
    }

    /// <summary>清空网格与图片缓存（切换文件夹前调用，释放旧缩略图）。</summary>
    private void ClearGrid()
    {
        _grid.Children.Clear();
        _tiles.Clear();
        foreach (var bm in _thumbs.Values)
        {
            bm.Dispose();
        }

        _thumbs.Clear();
        foreach (var stream in _thumbStreams.Values)
        {
            stream.Dispose();
        }

        _thumbStreams.Clear();
        _bytesCache.Clear();
    }

    /// <summary>并发下载缩略图（限流 6，走 raw CDN + 本地缓存），完成后逐张回填到网格。</summary>
    private async Task DownloadThumbnailsAsync(List<FolderEntry> files, int token)
    {
        var tasks = files.Select(async file =>
        {
            await _throttle.WaitAsync();
            try
            {
                if (token != _loadToken)
                {
                    return;
                }

                var bytes = await LoadThumbBytesAsync(_currentFolder, file.Name);
                if (token != _loadToken || bytes == null)
                {
                    return;
                }

                _bytesCache[file.Name] = bytes;
                var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                if (token != _loadToken)
                {
                    bitmap.Dispose();
                    stream.Dispose();
                    return;
                }

                _thumbStreams[file.Name] = stream;
                _thumbs[file.Name] = bitmap;
                Dispatcher.UIThread.Post(() =>
                {
                    if (token != _loadToken)
                    {
                        return;
                    }

                    if (_tiles.TryGetValue(file.Name, out var image))
                    {
                        image.Source = bitmap;
                    }
                });
            }
            catch
            {
                // 个别贴纸下载/解码失败时静默跳过，不影响其它贴纸。
            }
            finally
            {
                _throttle.Release();
            }
        });
        await Task.WhenAll(tasks);
        if (token == _loadToken)
        {
            _status.Text = $"共 {files.Count} 张贴纸，点击任意一张即可插入图层。";
        }
    }

    /// <summary>构建一个贴纸网格项（缩略图 + 名称，点击插入）。</summary>
    private Button BuildTile(string name)
    {
        var image = new Image
        {
            Width = 96,
            Height = 96,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _tiles[name] = image;
        var button = new Button
        {
            Width = 110,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    image,
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        MaxWidth = 98,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Opacity = 0.85
                    }
                }
            }
        };
        ToolTip.SetTip(button, $"插入贴纸：{name}");
        button.Click += async (_, _) => await PickAsync(name);
        return button;
    }

    /// <summary>把选中的贴纸下载到本地缓存并回调编辑器插入图层。</summary>
    private async Task PickAsync(string name)
    {
        if (!_bytesCache.TryGetValue(name, out var bytes))
        {
            _status.Text = "贴纸尚未加载完成，请稍候…";
            return;
        }

        try
        {
            _status.Text = "正在保存贴纸…";
            var safeName = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            var path = Path.Combine(_cacheDir, $"{_currentFolder}_{safeName}");
            await File.WriteAllBytesAsync(path, bytes);
            _onPick(path, $"{_currentFolder} · {Path.GetFileNameWithoutExtension(name)}");
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = $"保存贴纸失败：{ex.Message}";
        }
    }
}
