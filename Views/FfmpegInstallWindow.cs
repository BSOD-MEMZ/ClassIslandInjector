using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// FFmpeg 解码库安装器：仿 Linux 软件包管理器的安装窗口。
/// 展示包信息、下载进度条、实时速度与剩余时间估算、安装日志；可随时取消。
/// 下载完成后窗口显示结果，关闭后由设置页刷新可用性（<see cref="FFmpegRuntime.Refresh"/>）。
/// </summary>
internal sealed class FfmpegInstallWindow : MyWindow
{
    /// <summary>下载统计用的等宽字体（增强「终端/包管理器」观感）。</summary>
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");

    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100, Value = 0, Height = 8 };
    private readonly TextBlock _stageText = new() { FontSize = 13, Opacity = 0.85, Text = "正在准备…" };
    private readonly TextBlock _bytesText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _speedText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _etaText = new() { FontFamily = MonoFont, FontSize = 12, Opacity = 0.75, Text = "" };
    private readonly TextBlock _logText = new() { FontFamily = MonoFont, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _logScroller = new() { MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _actionButton = new() { Content = "取消" };
    /// <summary>彻底删除已安装解码库（库已就绪时可见，独立于安装流程）。</summary>
    private readonly Button _deleteButton = new() { Content = "删除已安装库" };
    private readonly StackPanel _choicePanel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Progress<FfmpegInstallProgress> _progress;
    private readonly FfmpegPackageKind? _fixedKind;

    /// <summary>当前打开的安装器窗口（单实例；已存在时聚焦而非重复打开）。</summary>
    public static FfmpegInstallWindow? Current { get; private set; }

    /// <summary>
    /// 打开安装器。<paramref name="kind"/> 为 null 时先让用户选择包档位（精简 / 完整），
    /// 指定档位（如剪辑渲染升级完整包）则直接开始安装。
    /// </summary>
    public FfmpegInstallWindow(FfmpegPackageKind? kind = null)
    {
        _fixedKind = kind;
        Title = "FFmpeg 库安装器";
        Width = 480;
        Height = 490;
        CanResize = false;   // 禁止拖拽边缘调整大小
        CanMaximize = false; // 禁止最大化
        // 与 Min/Max 相等：彻底锁死尺寸。
        MinWidth = 480;
        MaxWidth = 480;
        MinHeight = 490;
        MaxHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Progress<T> 捕获创建时的同步上下文（UI 线程），回调自动 marshal 回 UI 线程。
        _progress = new Progress<FfmpegInstallProgress>(ApplyProgress);
        _choicePanel = BuildChoicePanel();
        Content = BuildContent();
        Opened += (_, _) =>
        {
            Current = this;
            // 未指定档位时先展示选择界面，由用户点「开始安装」触发。
            if (_fixedKind is { } fixedKind)
            {
                StartInstall(fixedKind);
            }
        };
        _actionButton.Click += (_, _) => Close();
        _deleteButton.Click += async (_, _) => await DeleteLibrariesAsync();
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }

            _cts.Cancel();
        };
    }

    /// <summary>包档位选择区（精简 = 动态壁纸够用；完整 = 剪辑渲染必需）。</summary>
    private StackPanel BuildChoicePanel()
    {
        var minimal = new RadioButton
        {
            GroupName = "pkg",
            IsChecked = true,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "精简解码包（约 7 MB）", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "仅解码：动态壁纸、视频预览。",
                        FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        var full = new RadioButton
        {
            GroupName = "pkg",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "完整包（约 50 MB）", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "解码+编码：剪辑渲染、素材压缩转码。",
                        FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        var start = new Button
        {
            Content = "开始安装",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        start.Click += (_, _) => StartInstall(full.IsChecked == true ? FfmpegPackageKind.Full : FfmpegPackageKind.Minimal);
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                BuildComparisonTable(),
                new TextBlock
                {
                    Text = "注：均为软解（构建未含硬解）；完整包额外支持编码。",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
                },
                minimal,
                full,
                start
            }
        };
    }

    /// <summary>包档位对比表：不安装 / 精简解码包 / 完整包的能力差异（原生 DataGrid）。</summary>
    private Control BuildComparisonTable()
    {
        const string no = "✗";
        const string yes = "✓";
        var rows = new List<FfmpegComparisonRow>
        {
            new("动态视频背景", no, yes, yes),
            new("编辑器预览 / 素材播放", no, yes, yes),
            new("剪辑渲染 / 压缩转码（编码）", no, no, yes),
            new("下载体积", "—", "约 7 MB", "约 50 MB"),
        };

        var grid = new DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.All,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            CanUserReorderColumns = false,
            CanUserResizeColumns = false,
            CanUserSortColumns = false,
            RowHeight = 20,
            MaxHeight = 120,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 0)
        };
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "功能",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = CellTemplate("Feature", null, center: false)
        });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "不安装", Width = new DataGridLength(60), CellTemplate = CellTemplate("None", "NoneBrush", center: true) });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "精简版", Width = new DataGridLength(60), CellTemplate = CellTemplate("Minimal", "MinimalBrush", center: true) });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "完整版", Width = new DataGridLength(60), CellTemplate = CellTemplate("Full", "FullBrush", center: true) });
        return grid;
    }

    /// <summary>对比表行：✓ 用强调色、✗ 弱化灰、体积等文本用默认前景。</summary>
    private sealed record FfmpegComparisonRow(string Feature, string None, string Minimal, string Full)
    {
        public IBrush? NoneBrush => MarkBrush(None);
        public IBrush? MinimalBrush => MarkBrush(Minimal);
        public IBrush? FullBrush => MarkBrush(Full);

        private static IBrush? MarkBrush(string s) => s switch
        {
            "✓" => ThemePalette.AccentBrush(),
            "✗" => Brushes.Gray,
            _ => null
        };
    }

    /// <summary>DataGrid 单元格模板：文本 + 可选前景画刷（null = 默认前景）。</summary>
    private static IDataTemplate CellTemplate(string textPath, string? brushPath, bool center) =>
        new FuncDataTemplate<object>((_, _) =>
        {
            var tb = new TextBlock
            {
                FontSize = 11,
                HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = center ? new Thickness(0) : new Thickness(6, 0, 0, 0)
            };
            tb.Bind(TextBlock.TextProperty, new Binding(textPath));
            if (brushPath != null)
            {
                tb.Bind(TextBlock.ForegroundProperty, new Binding(brushPath));
            }

            return tb;
        });

    /// <summary>开始安装指定档位（隐藏选择区，启动下载任务）。</summary>
    private void StartInstall(FfmpegPackageKind kind)
    {
        if (_installStarted)
        {
            return;
        }

        _installStarted = true;
        _choicePanel.IsVisible = false;
        // 当前进程已加载 FFmpeg 库（dll 被锁定）：覆盖安装必然失败，直接提示重启，避免白下载再报错。
        if (FFmpegRuntime.IsLoaded)
        {
            _progressBar.IsIndeterminate = false;
            SetProgressVisible(false);
            _stageText.Text = "需要重启";
            _actionButton.Content = "关闭";
            AppendLog("✗ 当前进程已加载 FFmpeg 解码库（文件被占用），无法覆盖安装。\n请重启 ClassIsland 后再安装。");
            return;
        }

        SetProgressVisible(true);
        _ = RunInstallAsync(kind);
    }

    private bool _installStarted;

    /// <summary>启动安装任务（窗口打开后触发）。完成后更新 UI 为结果状态（隐藏进度条与统计）。</summary>
    private async Task RunInstallAsync(FfmpegPackageKind kind)
    {
        try
        {
            var (success, message) = await FFmpegRuntime.InstallAsync(kind, _progress, _cts.Token);
            _progressBar.IsIndeterminate = false;
            SetProgressVisible(false); // 安装结束：进度条与速度/剩余时间不再有意义，隐藏。
            _stageText.Text = success ? "安装完成" : "安装失败";
            _actionButton.Content = "关闭";
            AppendLog((success ? "✓ " : "✗ ") + message);
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            SetProgressVisible(false);
            _stageText.Text = "安装中断";
            _actionButton.Content = "关闭";
            AppendLog($"✗ {ex.Message}");
        }
    }

    /// <summary>显示 / 隐藏下载进度区（进度条 + 速度/剩余时间统计）。</summary>
    private void SetProgressVisible(bool visible)
    {
        _progressBar.IsVisible = visible;
        _bytesText.IsVisible = visible;
        _speedText.IsVisible = visible;
        _etaText.IsVisible = visible;
    }

    /// <summary>消费进度快照，更新进度条 / 速度 / 剩余时间 / 日志。</summary>
    private void ApplyProgress(FfmpegInstallProgress p)
    {
        _stageText.Text = p.Stage;
        if (p.Indeterminate)
        {
            _progressBar.IsIndeterminate = true;
        }
        else
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = p.TotalBytes > 0 ? Math.Clamp(p.DownloadedBytes * 100.0 / p.TotalBytes, 0, 100) : 0;
            // 仅下载阶段刷新统计；解压/完成等阶段保留最后一次下载统计。
            _bytesText.Text = p.TotalBytes > 0
                ? $"{FormatBytes(p.DownloadedBytes)} / {FormatBytes(p.TotalBytes)}"
                : FormatBytes(p.DownloadedBytes);
            _speedText.Text = p.SpeedBytesPerSecond > 0 ? $"{FormatBytes(p.SpeedBytesPerSecond)}/s" : string.Empty;
            _etaText.Text = p.Remaining is { } remaining ? $"剩余约 {FormatDuration(remaining)}" : string.Empty;
        }

        if (!string.IsNullOrEmpty(p.LogLine))
        {
            AppendLog(p.LogLine);
        }
    }

    private void AppendLog(string line)
    {
        _logText.Text += line + "\n";
        _logScroller.Offset = new Vector(0, _logScroller.Extent.Height);
    }

    private Control BuildContent()
    {
        // 包信息头部。
        var header = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "FFmpeg 库安装", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"FFmpeg {FFmpegRuntime.FfmpegVersion} 共享解码库",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        // 统计行：已下载/总大小 | 速度 | 剩余时间。
        var statsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _bytesText, _speedText, _etaText }
        };

        _logScroller.Content = _logText;
        _actionButton.HorizontalAlignment = HorizontalAlignment.Right;
        _actionButton.MinWidth = 88;
        _deleteButton.IsVisible = FFmpegRuntime.IsAvailable;
        // 底部：左 = 删除已安装库（库就绪时显示），右 = 关闭/取消。
        var bottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { _deleteButton, _actionButton }
        };
        Grid.SetColumn(_actionButton, 1);

        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                header,
                _choicePanel,
                _progressBar,
                _stageText,
                statsRow,
                _logScroller,
                bottomRow
            }
        };
    }

    /// <summary>确认并彻底删除已安装解码库（库被占用时写入「重启后自动清除」标记）。</summary>
    private async Task DeleteLibrariesAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "彻底删除 FFmpeg 解码库",
            Content = new TextBlock
            {
                Text = "将删除已安装的 FFmpeg 解码库（约 7~50 MB）。删除后动态视频背景、视频编辑器预览与渲染均不可用，需要时重新下载安装即可。\n确定要彻底删除吗？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "彻底删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var (success, message) = FFmpegRuntime.DeleteLibraries();
        FFmpegRuntime.Refresh();
        AppendLog((success ? "✓ " : "✗ ") + message);
        _stageText.Text = success ? "已删除" : "删除待完成";
        _deleteButton.IsVisible = FFmpegRuntime.IsAvailable;
    }

    /// <summary>以本窗口为宿主弹出 ContentDialog（无参会挂到主界面，点不到）。</summary>
    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog) =>
        TopLevel.GetTopLevel(this) is Window host ? dialog.ShowAsync(host) : dialog.ShowAsync();

    private static string FormatBytes(double bytes) =>
        bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024 * 1024 * 1024):0.00} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024:0} KB"
        : $"{bytes:0} B";

    private static string FormatDuration(TimeSpan t)
    {
        var total = (int)t.TotalSeconds;
        return total >= 3600
            ? $"{total / 3600:0}:{(total % 3600) / 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }
}
