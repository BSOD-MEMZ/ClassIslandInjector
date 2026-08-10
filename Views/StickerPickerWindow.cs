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
/// </summary>
internal sealed class StickerPickerWindow : MyWindow
{
    /// <summary>贴纸库根目录（GitHub contents API）。</summary>
    private const string StickersRootApi = "https://api.github.com/repos/TheOriginalAyaka/sekai-stickers/contents/public/img";

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
        Background = ThemePalette.WindowBackground();
        _cacheDir = Path.Combine(InjectorRuntime.ConfigDirectory, "stickers");
        Directory.CreateDirectory(_cacheDir);
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
            if (_folderBox.SelectedItem is FolderEntry { Name: { } folder })
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

    private static async Task<List<FolderEntry>> ListContentsAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ClassIslandInjector");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
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

    /// <summary>拉取根目录下的文件夹列表并填充下拉框。</summary>
    private async Task LoadFoldersAsync()
    {
        _status.Text = "正在获取贴纸库文件夹…";
        try
        {
            var folders = (await ListContentsAsync(StickersRootApi))
                .Where(f => f.Type == "dir")
                .ToList();
            _folderBox.ItemsSource = folders;
            _folderBox.SelectedItem = folders.FirstOrDefault();
            if (folders.Count == 0)
            {
                _status.Text = "贴纸库中没有文件夹，可能网络不可用或仓库结构已变化。";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"获取贴纸库失败：{ex.Message}";
        }
    }

    /// <summary>加载指定文件夹的贴纸列表并铺网格缩略图。</summary>
    private async Task LoadFolderAsync(string folder)
    {
        var token = ++_loadToken;
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
        _status.Text = "正在加载贴纸列表…";

        List<FolderEntry> files;
        try
        {
            var url = $"{StickersRootApi}/{Uri.EscapeDataString(folder)}";
            files = (await ListContentsAsync(url)).Where(f => f.Type == "file").ToList();
        }
        catch (Exception ex)
        {
            if (token == _loadToken)
            {
                _status.Text = $"加载贴纸列表失败：{ex.Message}";
            }

            return;
        }

        if (token != _loadToken || files.Count == 0)
        {
            if (token == _loadToken)
            {
                _status.Text = "该文件夹没有图片。";
            }

            return;
        }

        _status.Text = $"共 {files.Count} 张贴纸，正在加载预览…";
        foreach (var file in files)
        {
            _grid.Children.Add(BuildTile(file.Name));
        }

        _ = DownloadThumbnailsAsync(files, token);
    }

    /// <summary>并发下载缩略图（限流 6），完成后逐张回填到网格。</summary>
    private async Task DownloadThumbnailsAsync(List<FolderEntry> files, int token)
    {
        var tasks = files.Select(async file =>
        {
            await _throttle.WaitAsync();
            try
            {
                if (token != _loadToken || string.IsNullOrEmpty(file.DownloadUrl))
                {
                    return;
                }

                var bytes = await Http.GetByteArrayAsync(file.DownloadUrl);
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
