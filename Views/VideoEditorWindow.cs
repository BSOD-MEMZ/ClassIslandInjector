using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 视频编辑器（PR 风格）：左侧素材库、中间舞台（宽高比与 ClassIsland 主界面显示一致）、
/// 右侧片段属性、底部时间轴。支持片段裁剪（入/出点）、拼接（多片段顺序播放）、以及
/// 缩放/偏移/旋转/不透明度/边缘裁剪让画面比例合适。编辑后「渲染并应用」保存工程
/// （配置目录 video-project.json）并应用到主界面（运行时由 VideoProjectPlayer 实时渲染）。
/// </summary>
internal sealed class VideoEditorWindow : MyWindow
{
    public static VideoEditorWindow? Current { get; private set; }

    private const int PreviewMaxDimension = 1280;
    private readonly int _targetFps = 24;

    private readonly VideoProject _project = VideoProjectStore.Load(VideoProjectStore.DefaultPath);
    private readonly List<string> _assets = [];
    private VideoClip? _selected;
    private VideoProjectPlayer? _player;
    private bool _playing;
    private bool _updatingUi;
    /// <summary>「添加到时间轴」的目标轨道（点击轨道头部切换）。</summary>
    private int _selectedTrack;
    /// <summary>播放头当前时间（秒，非播放时也保留位置）。</summary>
    private double _playheadTime;
    /// <summary>正在裁剪的片段（左右手柄拖动）状态：片段 / 是否出点 / 上次指针 X / 块。</summary>
    private (VideoClip Clip, bool IsOut, double LastX, Border Block)? _trimState;
    /// <summary>是否正在拖动播放头（scrub）。</summary>
    private bool _scrubbing;
    /// <summary>拖动片段块的拖拽状态（块跟手实时移动，释放时落轨）。</summary>
    private (VideoClip Clip, double StartRootX, double OriginalStartTime)? _moveDrag;
    /// <summary>时间轴 ScrollViewer 可视宽度（时间轴内容至少铺满，短工程也能拖到最开头）。</summary>
    private double _timelineViewportWidth = 800;
    /// <summary>时间轴宽度变化防抖重建。</summary>
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // ---- 素材库 ----
    private readonly ListBox _assetList = new() { MinHeight = 120 };
    private readonly CommandBarButton _addAssetButton = new() { IconSource = new FluentIconSource("\uF3D1"), Label = "添加素材" };
    private readonly CommandBarButton _addToTimelineButton = new() { IconSource = new FluentIconSource("\uE0F7"), Label = "添加到时间轴", IsEnabled = false };

    // ---- 舞台 ----
    private readonly Border _stageBorder = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Grid _stageHostGrid = new();
    private readonly List<StageTrackLayer> _stageLayers = [];
    /// <summary>舞台八向手柄覆盖层（选中片段时显示，仿底图图层编辑器）。</summary>
    private readonly Canvas _stageHandleOverlay = new() { IsHitTestVisible = true, ZIndex = 40 };
    private readonly Border[] _handles = new Border[8];
    private Border _handleOutline = null!;
    private int _resizeHandle = -1;
    private Point _resizeStart;
    private Rect _resizeStartRect;
    private VideoClip? _resizeClip;
    private readonly TextBlock _stageCaption = new()
    {
        FontSize = 11,
        Opacity = 0.7,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(6)
    };

    // ---- 属性 ----
    private readonly EditorSpin _inSpin = new(0, 36000, 0.1, "0.#");
    private readonly EditorSpin _outSpin = new(0.1, 36000, 0.1, "0.#");
    private readonly EditorSpin _scaleSpin = new(0.05, 10, 0.01, "0.##");
    private readonly EditorSpin _offsetXSpin = new(-1, 1, 0.01, "0.##");
    private readonly EditorSpin _offsetYSpin = new(-1, 1, 0.01, "0.##");
    private readonly EditorSpin _rotationSpin = new(-180, 180, 0.5, "0.#");
    private readonly EditorSpin _opacitySpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropLSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropTSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropRSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropBSpin = new(0, 1, 0.01, "0.##");
    private readonly TextBlock _clipNameText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };
    private Control[] _propertyControls = [];

    // ---- 时间轴（多轨）----
    private readonly Grid _timeline = new();
    // 头部与泳道行高一致（60）且均无行间距，才能逐行对齐。
    private readonly StackPanel _trackHeaders = new() { Orientation = Orientation.Vertical };
    /// <summary>时间轴像素/秒（横向缩放）。</summary>
    private const double PxPerSecond = 6;
    /// <summary>轨道泳道高度。</summary>
    private const double LaneHeight = 60;
    private readonly TextBlock _clipCountText = new() { FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };

    // ---- 传输控制 / 播放头 ----
    private readonly CommandBarButton _playButton = new() { IconSource = new FluentIconSource("\uEDB9"), Label = "播放" };
    private readonly CommandBarButton _stopButton = new() { IconSource = new FluentIconSource("\uF087"), Label = "停止" };
    /// <summary>时间轴上方工具条：刀片切割 / 删除片段。</summary>
    private readonly CommandBarButton _cutButton = new() { IconSource = new FluentIconSource("\uE5C9"), Label = "刀片切割" };
    private readonly CommandBarButton _deleteButton = new() { IconSource = new FluentIconSource("\uE61D"), Label = "删除片段" };
    /// <summary>拖动片段块时按下点相对块左边缘的偏移（Drop 时减去，块跟手才能头贴尾拼接）。</summary>
    private double _dragOffsetX;
    private readonly TextBlock _timeText = new()
    {
        FontFamily = new FontFamily("Consolas, monospace"),
        FontSize = 12,
        Opacity = 0.85,
        VerticalAlignment = VerticalAlignment.Center
    };
    /// <summary>播放时钟：轮询播放器当前时间驱动播放头与时间码。</summary>
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    /// <summary>时间轴播放头（跨所有轨道，可拖动 scrub；18px 触摸热区 + 2px 强调色竖线）。</summary>
    private readonly Border _playhead = new()
    {
        Width = 18,
        Background = Brushes.Transparent,
        IsHitTestVisible = true,
        ZIndex = 20,
        Child = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemePalette.AccentBrush()
        }
    };
    /// <summary>时间轴根画布（泳道网格 + 播放头覆盖层）。左对齐保证时间轴 0 点在视口最左。</summary>
    private readonly Canvas _timelineRoot = new() { HorizontalAlignment = HorizontalAlignment.Left };
    /// <summary>素材时长缓存（裁剪右边界上限）。</summary>
    private readonly Dictionary<string, double> _assetDurations = [];

    /// <summary>舞台多轨预览图层：一轨一个 Image + 复用位图。</summary>
    private sealed class StageTrackLayer
    {
        public required int Track { get; init; }
        public required Image Image { get; init; }
        /// <summary>字段（非属性）：需以 ref 传给位图写入助手。</summary>
        public WriteableBitmap? Bitmap;
    }

    public VideoEditorWindow()
    {
        Title = "视频编辑器";
        Width = 1120;
        Height = 720;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 舞台比例与 ClassIsland 主界面显示宽高比一致。
        var size = InjectorRuntime.GetCurrentIslandSize();
        if (size is { Width: > 0, Height: > 0 })
        {
            _project.OutputWidth = size.Value.Width;
            _project.OutputHeight = size.Value.Height;
        }

        _stageBorder.Child = _stageHostGrid;
        _stageHostGrid.Children.Add(_stageCaption);
        // 八向手柄覆盖层（最上层）；轨道图层插到最底，字幕在图层之上。
        _stageHostGrid.Children.Add(_stageHandleOverlay);
        BuildStageHandles();
        // 素材选中变化时更新「添加到时间轴」可用性（之前仅刷新列表时更新，导致选中后仍置灰）。
        _assetList.SelectionChanged += (_, _) => UpdateAssetButtons();
        // 快捷键：空格 = 播放/暂停，Delete/Backspace = 删除选中片段（焦点在文本输入框时不拦截）。
        KeyDown += (_, e) =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    TogglePreview();
                    e.Handled = true;
                    break;
                case Key.Delete:
                case Key.Back:
                    DeleteSelectedClip();
                    e.Handled = true;
                    break;
            }
        };
        // 播放时钟：轮询播放器当前时间驱动播放头与时间码。
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        // 时间轴宽度变化（窗口调整）防抖后重建，让时间轴铺满视口。
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            RefreshTimeline();
        };
        // 播放头：按下捕获后拖动 scrub（用状态字段判断，本 Avalonia 版本无 IsCaptured）。
        _playhead.PointerPressed += (_, e) =>
        {
            _scrubbing = true;
            e.Pointer.Capture(_playhead);
            e.Handled = true;
        };
        _playhead.PointerMoved += (_, e) =>
        {
            if (_scrubbing)
            {
                SetPlayhead(Math.Max(0, e.GetPosition(_timelineRoot).X / PxPerSecond));
            }
        };
        _playhead.PointerReleased += (_, e) =>
        {
            if (_scrubbing)
            {
                _scrubbing = false;
                e.Pointer.Capture(null);
            }
        };
        _playhead.PointerCaptureLost += (_, _) => _scrubbing = false;
        Content = BuildContent();
        RefreshAssetList();
        RefreshTimeline();
        ClearSelection();
        Opened += (_, _) => Current = this;
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }

            StopPreview();
            _clockTimer.Stop();
        };
    }

    // ============ 布局 ============

    private Control BuildContent()
    {
        _addAssetButton.Click += (_, _) => _ = AddAssetAsync();
        _addToTimelineButton.Click += (_, _) => AddClipFromAsset();
        // 时间轴上方工具条：刀片切割 / 删除片段。
        _cutButton.Click += (_, _) => CutAtPlayhead();
        _deleteButton.Click += (_, _) => DeleteSelectedClip();
        // 素材列表按住拖出 → 拖到时间轴指定轨道/位置（更符合人类操作习惯）。
        _assetList.PointerMoved += (_, e) => StartAssetDrag(e);

        // 顶部命令栏（仿底图图层编辑器 CommandBar：图标 + 文字，无 emoji）。
        var toolbar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            PrimaryCommands =
            {
                _addAssetButton,
                _addToTimelineButton,
                new CommandBarSeparator(),
                CommandButton("\uE197", "上移轨道", "把选中片段上移一个轨道", MoveClipUp),
                CommandButton("\uE0CB", "下移轨道", "把选中片段下移一个轨道", MoveClipDown),
                new CommandBarSeparator(),
                CommandButton("\uE7FF", "清空时间轴", "删除时间轴上全部片段", ClearTimeline),
                CommandButton("\uEEB5", "渲染并应用", "保存工程并应用到主界面", RenderAndApply)
            }
        };

        // 素材库（左）
        var assetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _assetList
        };
        var assetHint = new TextBlock
        {
            Text = "添加视频文件到素材库：选中后点「添加到时间轴」，或按住拖到下方时间轴指定轨道/位置。可多素材多轨叠放。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap
        };
        var assetPanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                RowSpacing = 8,
                Children = { assetScroll, assetHint }
            }
        };
        Grid.SetRow(assetScroll, 0);
        Grid.SetRow(assetHint, 1);

        // 舞台（中，固定宽高比）+ 底部传输控制条（播放/暂停/停止/时间码）
        _stageBorder.HorizontalAlignment = HorizontalAlignment.Center;
        _stageBorder.VerticalAlignment = VerticalAlignment.Center;
        var stageHost = new Border
        {
            ClipToBounds = true,
            Background = ThemePalette.MicaPanelBackground(),
            Child = _stageBorder
        };
        stageHost.SizeChanged += (_, e) => LayoutStage(e.NewSize.Width, e.NewSize.Height);

        _playButton.Click += (_, _) => TogglePreview();
        _stopButton.Click += (_, _) => StopPreview();
        var transportBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { _playButton, _stopButton, _timeText }
        };
        var stageColumn = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children = { stageHost, transportBar }
        };
        Grid.SetRow(stageHost, 0);
        Grid.SetRow(transportBar, 1);

        // 属性（右）
        var rightPanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BuildInspector()
            }
        };

        // 时间轴（底部，多轨：左轨道头 + 右泳道 + 播放头）
        var timelineScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _timelineRoot
        };
        // 视口宽度变化时记录，供时间轴内容铺满视口（防抖重建）。
        timelineScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || Math.Abs(e.NewSize.Width - _timelineViewportWidth) < 1)
            {
                return;
            }

            _timelineViewportWidth = e.NewSize.Width;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
        var timelineArea = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("92,*"),
            ColumnSpacing = 8,
            Children = { _trackHeaders, timelineScroll }
        };
        Grid.SetColumn(_trackHeaders, 0);
        Grid.SetColumn(timelineScroll, 1);
        // 时间轴上方工具条：刀片切割 / 删除片段 / 输出比例 / 片段统计 / 状态。
        var timelineToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _cutButton,
                _deleteButton,
                new TextBlock
                {
                    Text = $"输出 {_project.OutputWidth:0}×{_project.OutputHeight:0}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.7,
                    FontSize = 12,
                    Margin = new Thickness(10, 0, 0, 0)
                },
                _clipCountText,
                _statusText
            }
        };
        var timelinePanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 6,
                Children = { timelineToolbar, timelineArea }
            }
        };
        Grid.SetRow(timelineToolbar, 0);
        Grid.SetRow(timelineArea, 1);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*,300"),
            ColumnSpacing = 8,
            Children = { assetPanel, stageColumn, rightPanel }
        };
        Grid.SetColumn(assetPanel, 0);
        Grid.SetColumn(stageColumn, 1);
        Grid.SetColumn(rightPanel, 2);

        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*,190"),
            RowSpacing = 8,
            Children = { toolbar, body, timelinePanel }
        };
        Grid.SetRow(body, 1);
        Grid.SetRow(timelinePanel, 2);
        return root;
    }

    private Control BuildInspector()
    {
        _propertyControls =
        [
            _inSpin, _outSpin, _scaleSpin, _offsetXSpin, _offsetYSpin,
            _rotationSpin, _opacitySpin, _cropLSpin, _cropTSpin, _cropRSpin, _cropBSpin
        ];
        foreach (var spin in _propertyControls)
        {
            spin.PropertyChanged += (_, e) =>
            {
                if (e.Property == NumericUpDown.ValueProperty)
                {
                    ApplyPropertyEdits();
                }
            };
        }

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "片段属性", FontWeight = FontWeight.SemiBold },
                _clipNameText,
                InspectorRow("入点（秒）", _inSpin),
                InspectorRow("出点（秒）", _outSpin),
                new TextBlock { Text = "变换", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                InspectorRow("缩放", _scaleSpin),
                InspectorRow("水平偏移", _offsetXSpin),
                InspectorRow("垂直偏移", _offsetYSpin),
                InspectorRow("旋转（度）", _rotationSpin),
                InspectorRow("不透明度", _opacitySpin),
                new TextBlock { Text = "边缘裁剪（0~1 归一化）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                InspectorRow("裁左", _cropLSpin),
                InspectorRow("裁上", _cropTSpin),
                InspectorRow("裁右", _cropRSpin),
                InspectorRow("裁下", _cropBSpin)
            }
        };
    }

    private static Control InspectorRow(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8,
            FontSize = 12
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    /// <summary>图标 + 文字命令按钮（仿底图图层编辑器 CommandBar，无 emoji）。</summary>
    private static CommandBarButton CommandButton(string glyph, string label, string tooltip, Action action)
    {
        var button = new CommandBarButton
        {
            IconSource = new FluentIconSource(glyph),
            Label = label
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>按输出比例缩放舞台，居中于可用空间。</summary>
    private void LayoutStage(double availableW, double availableH)
    {
        if (availableW <= 0 || availableH <= 0)
        {
            return;
        }

        var ratio = _project.OutputWidth / Math.Max(1, _project.OutputHeight);
        var w = Math.Max(50, availableW - 24);
        var h = w / ratio;
        if (h > Math.Max(20, availableH - 24))
        {
            h = Math.Max(20, availableH - 24);
            w = Math.Max(50, h * ratio);
        }

        _stageBorder.Width = w;
        _stageBorder.Height = h;
    }

    // ============ 素材库 ============

    private async Task AddAssetAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } provider)
        {
            return;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加视频素材",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频") { Patterns = ["*.mp4", "*.wmv", "*.avi", "*.mkv", "*.mov", "*.webm", "*.m4v"] },
                FilePickerFileTypes.All
            ]
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && !_assets.Contains(path))
            {
                _assets.Add(path);
            }
        }

        RefreshAssetList();
        if (_assets.Count > 0 && _assetList.SelectedIndex < 0)
        {
            _assetList.SelectedIndex = 0;
        }
    }

    private void RefreshAssetList()
    {
        _assetList.ItemsSource = _assets.Select(p => Path.GetFileName(p)).ToList();
        UpdateAssetButtons();
    }

    private void UpdateAssetButtons() => _addToTimelineButton.IsEnabled = _assetList.SelectedIndex >= 0;

    /// <summary>素材列表按住并拖动时发起拖拽（数据为素材路径，供时间轴泳道接收）。</summary>
    private void StartAssetDrag(PointerEventArgs e)
    {
        if (_assetList.SelectedIndex < 0 || _assetList.SelectedIndex >= _assets.Count)
        {
            return;
        }

        var point = e.GetCurrentPoint(_assetList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_assetList.ContainerFromIndex(_assetList.SelectedIndex) is not Control item)
        {
            return;
        }

        var pos = e.GetPosition(item);
        if (pos.X < 0 || pos.Y < 0 || pos.X > item.Bounds.Width || pos.Y > item.Bounds.Height)
        {
            return;
        }

        var data = new DataObject();
        data.Set(DataFormats.Text, _assets[_assetList.SelectedIndex]);
        // Avalonia 11 的 DoDragDrop 首参为触发拖拽的指针事件。
        DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private void AddClipFromAsset()
    {
        if (_assetList.SelectedIndex < 0 || _assetList.SelectedIndex >= _assets.Count)
        {
            return;
        }

        var path = _assets[_assetList.SelectedIndex];
        var duration = GetAssetDuration(path);
        var clip = new VideoClip
        {
            SourcePath = path,
            Track = _selectedTrack,
            StartTime = _project.TrackEnd(_selectedTrack),
            InPoint = 0,
            OutPoint = duration > 0.5 ? duration : 10
        };
        _project.Clips.Add(clip);
        _selected = clip;
        RefreshTimeline();
        FillPropertyPanel();
    }

    /// <summary>打开素材探测时长（打开解码器后立即释放）。</summary>
    private static double ProbeDuration(string path)
    {
        try
        {
            using var source = new VideoFrameSource();
            return source.Open(path, 640) ? source.Duration : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>取素材时长（带缓存，供裁剪右边界上限）。</summary>
    private double GetAssetDuration(string path)
    {
        if (_assetDurations.TryGetValue(path, out var d))
        {
            return d;
        }

        d = ProbeDuration(path);
        _assetDurations[path] = d;
        return d;
    }

    // ============ 时间轴（多轨）============

    private void RefreshTimeline()
    {
        var trackCount = _project.TrackCount;

        // 轨道头部：点击切换「添加到时间轴」的目标轨道。
        _trackHeaders.Children.Clear();
        for (var t = 0; t < trackCount; t++)
        {
            var trackIndex = t;
            var header = new Border
            {
                Height = LaneHeight,
                CornerRadius = new CornerRadius(4),
                Background = trackIndex == _selectedTrack
                    ? ThemePalette.AccentBrushWithAlpha(110)
                    : new SolidColorBrush(TrackHeaderIdleColor()),
                Child = new TextBlock
                {
                    Text = $"轨道 {trackIndex + 1}",
                    FontSize = 11,
                    Opacity = 0.9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            header.PointerPressed += (_, _) =>
            {
                _selectedTrack = trackIndex;
                RefreshTimeline();
            };
            _trackHeaders.Children.Add(header);
        }

        // 轨道泳道：每轨一个横向画布，片段按 StartTime 绝对定位。
        _timeline.RowDefinitions.Clear();
        _timeline.Children.Clear();
        // 宽度至少铺满视口（短工程也能把片段拖到最开头）。
        var totalWidth = Math.Max(_timelineViewportWidth, _project.Duration * PxPerSecond + 120);
        for (var t = 0; t < trackCount; t++)
        {
            var trackIndex = t;
            var lane = new Border
            {
                Height = LaneHeight,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(LaneFillColor()),
                ClipToBounds = true
            };
            var canvas = new Canvas { Width = totalWidth, Height = LaneHeight };
            lane.Child = canvas;
            Grid.SetRow(lane, trackIndex);
            _timeline.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _timeline.Children.Add(lane);

            // 泳道接受拖放：素材拖入 = 新增片段；片段拖入 = 移动到该轨道/位置。
            DragDrop.SetAllowDrop(lane, true);
            lane.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
                e.Handled = true;
            });
            lane.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e, lane, trackIndex));
            // 点击泳道空白处：把播放头移到该位置（播放中则跳转）；点片段由块自己处理选中。
            lane.PointerPressed += (_, e) =>
            {
                if (e.Source == lane || e.Source == canvas)
                {
                    SetPlayhead(Math.Max(0, e.GetPosition(_timelineRoot).X / PxPerSecond));
                }
            };

            foreach (var clip in _project.Clips.Where(c => c.Track == trackIndex).OrderBy(c => c.StartTime))
            {
                canvas.Children.Add(BuildClipBlock(clip));
            }
        }

        // 时间轴根画布：泳道网格在下、播放头竖线覆盖在上（跨所有轨道，并延伸到面板底部）。
        var playheadHeight = trackCount * LaneHeight + 16;
        _timelineRoot.Children.Clear();
        _timelineRoot.Width = totalWidth;
        _timelineRoot.Height = playheadHeight;
        _timelineRoot.Children.Add(_timeline);
        _timelineRoot.Children.Add(_playhead);
        _playhead.Height = playheadHeight;
        Canvas.SetLeft(_playhead, _playheadTime * PxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);

        _clipCountText.Text = $"轨道 {trackCount} · 片段 {_project.Clips.Count} · 总时长 {_project.Duration:0.#}s（循环播放，同刻多轨叠放）";
    }

    /// <summary>把播放头移到指定时间（秒）；播放中同步跳转播放器。</summary>
    private void SetPlayhead(double time)
    {
        _playheadTime = Math.Max(0, time);
        Canvas.SetLeft(_playhead, _playheadTime * PxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);
        if (_playing && _player != null)
        {
            _player.Seek(_playheadTime);
        }
    }

    /// <summary>播放时钟：播放头跟随播放器时钟（真实耗时推进，与画面同步；无帧时段照常前进）。</summary>
    private void UpdateClock()
    {
        if (!_playing || _player == null)
        {
            return;
        }

        _playheadTime = _player.CurrentTime;
        Canvas.SetLeft(_playhead, _playheadTime * PxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);
    }

    private string FormatTime(double seconds) => $"{Math.Max(0, seconds):0.0} / {_project.Duration:0.0} s";

    /// <summary>未选中片段块的底色（随主题）。</summary>
    private static Color UnselectedBlockColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(90, 90, 90, 100)
        : Color.FromArgb(110, 205, 205, 210);

    /// <summary>轨道泳道底色（随主题）。</summary>
    private static Color LaneFillColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(26, 120, 120, 130)
        : Color.FromArgb(36, 205, 205, 210);

    /// <summary>轨道头空闲底色（随主题）。</summary>
    private static Color TrackHeaderIdleColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(50, 90, 90, 100)
        : Color.FromArgb(60, 180, 180, 185);

    /// <summary>把片段放到指定轨道上不与任何同轨片段重叠的最近可用位置（优先向后，其次向前）。</summary>
    private double FitToTrack(VideoClip clip, double desired, int track)
    {
        var duration = clip.Duration;
        if (duration <= 0)
        {
            return Math.Max(0, desired);
        }

        var occupied = _project.Clips
            .Where(c => c.Track == track && !ReferenceEquals(c, clip))
            .Select(c => (c.StartTime, c.StartTime + c.Duration))
            .OrderBy(o => o.Item1)
            .ToList();
        if (occupied.Count == 0)
        {
            return Math.Max(0, desired);
        }

        bool Overlaps(double start) => occupied.Any(o => start < o.Item2 - 0.001 && start + duration > o.Item1 + 0.001);

        // 生成所有可用空隙起点（每个占用区间前 + 末尾之后）。
        var gaps = new List<double>();
        var cursor = 0.0;
        foreach (var o in occupied)
        {
            if (o.Item1 > cursor)
            {
                gaps.Add(cursor);
            }

            cursor = Math.Max(cursor, o.Item2);
        }

        gaps.Add(cursor);
        var d = Math.Max(0, desired);
        if (!Overlaps(d))
        {
            return d;
        }

        double? after = null;
        double? before = null;
        foreach (var g in gaps)
        {
            if (g >= d && !Overlaps(g) && after == null)
            {
                after = g;
            }

            if (g <= d && !Overlaps(g))
            {
                before = g;
            }
        }

        if (before != null && (after == null || d - before.Value <= after.Value - d))
        {
            return before.Value;
        }

        if (after != null)
        {
            return after.Value;
        }

        // 没有足够大的空隙：放到最后一个片段之后。
        return Math.Max(0, occupied.Max(o => o.Item2));
    }

    /// <summary>构建一个时间轴片段块（绝对定位到泳道画布；支持点击选中、按住拖拽移动、左右边缘拖拽裁剪入/出点）。</summary>
    private Border BuildClipBlock(VideoClip clip)
    {
        var isSelected = ReferenceEquals(clip, _selected);
        var durationText = new TextBlock
        {
            Text = $"{clip.StartTime:0.#}s · {clip.Duration:0.#}s",
            FontSize = 10,
            Opacity = 0.7
        };
        var content = new StackPanel
        {
            Margin = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = Path.GetFileName(clip.SourcePath),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150
                },
                durationText
            }
        };
        // 选中时显示左右裁剪手柄（拖左 = 改入点，拖右 = 改出点）。
        var leftHandle = new Border
        {
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            IsVisible = isSelected
        };
        var rightHandle = new Border
        {
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            IsVisible = isSelected
        };
        var block = new Border
        {
            Width = Math.Max(30, clip.Duration * PxPerSecond),
            Height = LaneHeight - 12,
            CornerRadius = new CornerRadius(6),
            Background = isSelected
                ? ThemePalette.AccentBrushWithAlpha(170)
                : new SolidColorBrush(UnselectedBlockColor()),
            BorderBrush = isSelected ? Brushes.White : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = new Grid { Children = { content, leftHandle, rightHandle } }
        };
        Canvas.SetLeft(block, clip.StartTime * PxPerSecond);
        Canvas.SetTop(block, 6);

        // 点击选中 + 按下准备拖拽（指针捕获 + 跟手实时移动，不用 DragDrop，稳定可靠）。
        block.PointerPressed += (_, e) =>
        {
            _selected = clip;
            _dragOffsetX = e.GetPosition(block).X;
            _moveDrag = (clip, e.GetPosition(_timelineRoot).X, clip.StartTime);
            e.Pointer.Capture(block);
            ApplyBlockSelected(block, leftHandle, rightHandle, clip);
            FillPropertyPanel();
            UpdateStageHandles();
        };
        // 裁剪手柄按下：记录裁剪状态并阻止冒泡（避免触发块选中/拖拽）。
        leftHandle.PointerPressed += (_, e) =>
        {
            _trimState = (clip, false, e.GetPosition(block).X, block);
            e.Handled = true;
        };
        rightHandle.PointerPressed += (_, e) =>
        {
            _trimState = (clip, true, e.GetPosition(block).X, block);
            e.Handled = true;
        };
        // 块上移动/释放：优先裁剪，其次拖拽移动（实时移动块）。
        block.PointerMoved += (_, e) =>
        {
            if (_trimState is { } trim && ReferenceEquals(trim.Clip, clip))
            {
                var x = e.GetPosition(block).X;
                var delta = (x - trim.LastX) / PxPerSecond;
                _trimState = (clip, trim.IsOut, x, block);
                ApplyTrim(clip, trim.IsOut, delta, block, durationText);
                return;
            }

            if (_moveDrag is not { } md || !ReferenceEquals(md.Clip, clip))
            {
                return;
            }

            if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
            {
                _moveDrag = null;
                return;
            }

            var rootX = e.GetPosition(_timelineRoot).X;
            var desired = Math.Max(0, md.OriginalStartTime + (rootX - md.StartRootX) / PxPerSecond);
            // 同轨不重叠：拖动中实时修正到最近可用位置（块会被相邻片段"挡"住）。
            clip.StartTime = FitToTrack(clip, desired, clip.Track);
            Canvas.SetLeft(block, clip.StartTime * PxPerSecond);
        };
        block.PointerReleased += (_, e) =>
        {
            if (_trimState is { } t && ReferenceEquals(t.Clip, clip))
            {
                _trimState = null;
                RefreshTimeline();
                FillPropertyPanel();
                return;
            }

            if (_moveDrag is { } md && ReferenceEquals(md.Clip, clip))
            {
                _moveDrag = null;
                e.Pointer.Capture(null);
                // 释放：吸附 → 按指针 Y 落轨 → 保证同轨不重叠。
                clip.StartTime = SnapTime(clip.StartTime);
                var rootPos = e.GetPosition(_timelineRoot);
                var targetTrack = (int)(rootPos.Y / LaneHeight);
                clip.Track = Math.Clamp(targetTrack, 0, Math.Max(0, _project.TrackCount - 1));
                clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
                RefreshTimeline();
                FillPropertyPanel();
            }
        };
        block.PointerCaptureLost += (_, _) => _moveDrag = null;
        return block;
    }

    /// <summary>直接更新片段块的选中样式（不重建整条时间轴，避免打断拖拽）。</summary>
    private void ApplyBlockSelected(Border block, Border leftHandle, Border rightHandle, VideoClip clip)
    {
        var selected = ReferenceEquals(clip, _selected);
        block.Background = selected
            ? ThemePalette.AccentBrushWithAlpha(170)
            : new SolidColorBrush(UnselectedBlockColor());
        block.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
        leftHandle.IsVisible = selected;
        rightHandle.IsVisible = selected;
    }

    /// <summary>拖拽裁剪：按拖动秒数增量调整入/出点，即时更新块宽与时长文本（不重建，避免丢失拖拽状态）。</summary>
    private void ApplyTrim(VideoClip clip, bool isOut, double deltaSeconds, Border block, TextBlock durationText)
    {
        if (isOut)
        {
            var maxOut = _assetDurations.TryGetValue(clip.SourcePath, out var d) && d > 0 ? d : double.MaxValue;
            clip.OutPoint = Math.Clamp(clip.OutPoint + deltaSeconds, clip.InPoint + 0.1, maxOut);
        }
        else
        {
            clip.InPoint = Math.Clamp(clip.InPoint + deltaSeconds, 0, clip.OutPoint - 0.1);
        }

        block.Width = Math.Max(30, clip.Duration * PxPerSecond);
        durationText.Text = $"{clip.StartTime:0.#}s · {clip.Duration:0.#}s";
        _clipNameText.Text = $"{Path.GetFileName(clip.SourcePath)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s";
        if (_playing && _player != null && clip.Track < _stageLayers.Count)
        {
            ApplyTransform(_stageLayers[clip.Track].Image, clip);
        }
    }

    /// <summary>时间轴泳道放置处理：素材 → 新增片段；clip:n → 移动既有片段。</summary>
    private void HandleTimelineDrop(DragEventArgs e, Border lane, int trackIndex)
    {
        if (!e.Data.Contains(DataFormats.Text))
        {
            return;
        }

        var text = e.Data.Get(DataFormats.Text)?.ToString() ?? "";
        // 用时间轴根坐标计算落点（0 = 时间轴开头）；拖动既有片段时减去按下点偏移，让块跟手。
        var isMove = text.StartsWith("clip:", StringComparison.Ordinal);
        var rawX = e.GetPosition(_timelineRoot).X - (isMove ? _dragOffsetX : 0);
        var startTime = SnapTime(Math.Max(0, rawX / PxPerSecond));

        if (isMove &&
            int.TryParse(text.AsSpan(5), out var index) &&
            index >= 0 && index < _project.Clips.Count)
        {
            // 移动既有片段到该轨道/位置。
            var clip = _project.Clips[index];
            clip.Track = trackIndex;
            clip.StartTime = startTime;
            _selected = clip;
        }
        else if (_assets.Contains(text))
        {
            // 素材拖入：目标轨道/位置新增片段（同轨不重叠，自动放到最近可用位置）。
            var duration = GetAssetDuration(text);
            var clip = new VideoClip
            {
                SourcePath = text,
                Track = trackIndex,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = duration > 0.5 ? duration : 10
            };
            clip.StartTime = FitToTrack(clip, startTime, trackIndex);
            _project.Clips.Add(clip);
            _selected = clip;
        }

        RefreshTimeline();
        FillPropertyPanel();
        e.Handled = true;
    }

    /// <summary>时间吸附：距离时间轴开头、任意片段边缘（头/尾）或播放头 1s 内自动对齐，便于头贴尾拼接。</summary>
    private double SnapTime(double time)
    {
        var best = time;
        var bestDist = 1.0;
        if (time < bestDist)
        {
            best = 0;
            bestDist = time;
        }

        foreach (var clip in _project.Clips)
        {
            var head = clip.StartTime;
            var tail = clip.StartTime + clip.Duration;
            if (Math.Abs(time - head) < bestDist)
            {
                best = head;
                bestDist = Math.Abs(time - head);
            }

            if (Math.Abs(time - tail) < bestDist)
            {
                best = tail;
                bestDist = Math.Abs(time - tail);
            }
        }

        if (Math.Abs(time - _playheadTime) < bestDist)
        {
            best = _playheadTime;
        }

        return best;
    }

    private void MoveClipUp()
    {
        if (_selected == null || _selected.Track <= 0)
        {
            return;
        }

        _selected.Track--;
        RefreshTimeline();
    }

    private void MoveClipDown()
    {
        if (_selected == null)
        {
            return;
        }

        _selected.Track++;
        RefreshTimeline();
    }

    private void DeleteSelectedClip()
    {
        if (_selected == null)
        {
            return;
        }

        _project.Clips.Remove(_selected);
        _selected = null;
        RefreshTimeline();
        ClearSelection();
    }

    private void ClearTimeline()
    {
        _project.Clips.Clear();
        _selected = null;
        RefreshTimeline();
        ClearSelection();
        StopPreview();
    }

    /// <summary>刀片工具：在播放头位置把所有覆盖该时刻的片段切成两段（保留入出点与变换）。</summary>
    private void CutAtPlayhead()
    {
        var cut = CutClipsAt(_playheadTime);
        _statusText.Text = cut == 0
            ? $"播放头位置 {_playheadTime:0.#}s 没有片段，无法切割。"
            : $"已在 {_playheadTime:0.#}s 切割 {cut} 个片段。";
    }

    /// <summary>在指定时间切割所有覆盖该时刻的片段，返回切割数量。</summary>
    private int CutClipsAt(double time)
    {
        var toAdd = new List<VideoClip>();
        var toRemove = new List<VideoClip>();
        foreach (var clip in _project.Clips)
        {
            var end = clip.StartTime + clip.Duration;
            if (time <= clip.StartTime + 0.001 || time >= end - 0.001)
            {
                continue; // 播放头不在片段内部（含端点），跳过。
            }

            var rel = time - clip.StartTime; // 相对片段开始（秒）
            // 左段：保留 [InPoint, InPoint+rel)。
            var left = clip.Clone();
            left.OutPoint = clip.InPoint + rel;
            // 右段：从 rel 处继续，StartTime 移到切割点，InPoint 前移。
            var right = clip.Clone();
            right.StartTime = time;
            right.InPoint = clip.InPoint + rel;
            toRemove.Add(clip);
            toAdd.Add(left);
            toAdd.Add(right);
        }

        if (toRemove.Count == 0)
        {
            return 0;
        }

        foreach (var clip in toRemove)
        {
            _project.Clips.Remove(clip);
        }

        _project.Clips.AddRange(toAdd);
        if (ReferenceEquals(_selected, toRemove[0]))
        {
            _selected = toAdd[0]; // 选中左段。
        }

        RefreshTimeline();
        FillPropertyPanel();
        return toRemove.Count;
    }

    private void ClearSelection()
    {
        _selected = null;
        FillPropertyPanel();
        UpdateStageHandles();
    }

    // ============ 属性 ============

    private void FillPropertyPanel()
    {
        _updatingUi = true;
        try
        {
            var clip = _selected;
            var has = clip != null;
            _clipNameText.Text = has
                ? $"{Path.GetFileName(clip!.SourcePath)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s"
                : "未选中片段。在底部时间轴点击一个片段，或从左侧素材库添加。";
            _inSpin.DoubleValue = clip?.InPoint ?? 0;
            _outSpin.DoubleValue = clip?.OutPoint ?? 10;
            _scaleSpin.DoubleValue = clip?.Scale ?? 1;
            _offsetXSpin.DoubleValue = clip?.OffsetX ?? 0;
            _offsetYSpin.DoubleValue = clip?.OffsetY ?? 0;
            _rotationSpin.DoubleValue = clip?.Rotation ?? 0;
            _opacitySpin.DoubleValue = clip?.Opacity ?? 1;
            _cropLSpin.DoubleValue = clip?.CropLeft ?? 0;
            _cropTSpin.DoubleValue = clip?.CropTop ?? 0;
            _cropRSpin.DoubleValue = clip?.CropRight ?? 1;
            _cropBSpin.DoubleValue = clip?.CropBottom ?? 1;
            foreach (var control in _propertyControls)
            {
                control.IsEnabled = has;
            }

            // 未播放时选中片段：自动显示首帧，调整属性即可实时预览。
            if (clip != null)
            {
                ShowSelectedClipFrame(clip);
            }
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void ApplyPropertyEdits()
    {
        if (_updatingUi || _selected == null)
        {
            return;
        }

        var clip = _selected;
        clip.InPoint = Math.Max(0, _inSpin.DoubleValue);
        clip.OutPoint = Math.Max(clip.InPoint + 0.1, _outSpin.DoubleValue);
        clip.Scale = _scaleSpin.DoubleValue;
        clip.OffsetX = _offsetXSpin.DoubleValue;
        clip.OffsetY = _offsetYSpin.DoubleValue;
        clip.Rotation = _rotationSpin.DoubleValue;
        clip.Opacity = Math.Clamp(_opacitySpin.DoubleValue, 0, 1);
        clip.CropLeft = Math.Clamp(_cropLSpin.DoubleValue, 0, 1);
        clip.CropTop = Math.Clamp(_cropTSpin.DoubleValue, 0, 1);
        clip.CropRight = Math.Clamp(_cropRSpin.DoubleValue, 0, 1);
        clip.CropBottom = Math.Clamp(_cropBSpin.DoubleValue, 0, 1);
        RefreshTimeline();
        _clipNameText.Text = $"{Path.GetFileName(clip.SourcePath)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s";
        // 舞台实时预览：只要该轨道已有画面（含暂停/未播放），立即应用变换并刷新八向手柄。
        // （之前用 _playing && _player 条件，暂停时调整缩放/偏移完全不更新舞台。）
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            ApplyTransform(_stageLayers[clip.Track].Image, clip);
            UpdateStageHandles();
        }
    }

    /// <summary>
    /// 未播放时选中片段自动在舞台显示其首帧（入点处），这样不预览也能直接调整属性实时看到效果。
    /// 后台解码，Post 到 UI 线程；期间若选中变化则丢弃旧帧。
    /// </summary>
    private void ShowSelectedClipFrame(VideoClip clip)
    {
        if (_playing || clip == null || _selected != clip)
        {
            return;
        }

        // 该轨道已有可见画面（预览过）则跳过，避免重复解码。
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            return;
        }

        var path = clip.SourcePath;
        var inPoint = clip.InPoint;
        var track = clip.Track;
        Task.Run(() =>
        {
            try
            {
                using var source = new VideoFrameSource();
                if (!source.Open(path, PreviewMaxDimension))
                {
                    return;
                }

                if (inPoint > 0)
                {
                    source.SeekTo(inPoint);
                }

                if (!source.TryReadFrame(out var frame) || frame == null)
                {
                    return;
                }

                var pixels = new byte[frame.Pixels.Length];
                Buffer.BlockCopy(frame.Pixels, 0, pixels, 0, pixels.Length);
                var w = frame.Width;
                var h = frame.Height;
                Dispatcher.UIThread.Post(() =>
                {
                    // 只有仍选中同一片段时才应用（防旧任务覆盖新选中）。
                    if (!ReferenceEquals(_selected, clip))
                    {
                        return;
                    }

                    UpdateStageLayer(track, new VideoFrame(pixels, w, h), clip);
                    UpdateStageHandles();
                });
            }
            catch
            {
                // 解码失败静默忽略（不影响编辑）。
            }
        });
    }

    // ============ 预览播放 ============

    private void TogglePreview()
    {
        if (_player != null)
        {
            // 播放 ⇄ 暂停（保留播放头位置）。
            if (_playing)
            {
                _player.Pause();
                _playing = false;
                _statusText.Text = "已暂停";
            }
            else
            {
                _player.Seek(_playheadTime);
                _player.Resume();
                _playing = true;
                _statusText.Text = "预览播放中…";
            }

            UpdateTransportUi();
            return;
        }

        StartPreview();
    }

    private void StartPreview()
    {
        StopPreview();
        if (_project.Clips.Count == 0)
        {
            _statusText.Text = "时间轴为空，先添加片段再预览。";
            return;
        }

        if (!FFmpegRuntime.IsAvailable)
        {
            _statusText.Text = "缺少 FFmpeg 解码库，无法预览。";
            return;
        }

        // 清空旧舞台预览图层（轨道数可能变化）。
        foreach (var layer in _stageLayers)
        {
            layer.Bitmap?.Dispose();
        }

        _stageLayers.Clear();
        foreach (var child in _stageHostGrid.Children)
        {
            if (child is Image img)
            {
                img.Source = null;
                img.IsVisible = false;
            }
        }

        _playheadTime = 0;
        Canvas.SetLeft(_playhead, -9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(0);
        _player = new VideoProjectPlayer(_project, PreviewMaxDimension, _targetFps, OnPreviewFrame);
        _player.Start();
        _playing = true;
        UpdateTransportUi();
        _statusText.Text = "预览播放中…";
    }

    private void StopPreview()
    {
        _player?.Dispose();
        _player = null;
        _playing = false;
        UpdateTransportUi();
    }

    private void UpdateTransportUi()
    {
        _playButton.Label = _playing ? "暂停" : "播放";
        _playButton.IconSource = new FluentIconSource(_playing ? "\uEC91" : "\uEDB9");
    }

    private void OnPreviewFrame(VideoFrame frame, VideoClip clip, int track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UpdateStageLayer(track, frame, clip);
                _stageCaption.Text = $"轨道 {track + 1}: {Path.GetFileName(clip.SourcePath)}";
            }
            catch
            {
                // 位图已释放等竞态：忽略。
            }
            finally
            {
                // 通知播放器本轨道帧已消费，允许覆写缓冲。
                _player?.MarkTrackConsumed(track);
            }
        });
    }

    /// <summary>更新指定轨道的舞台预览图层（位图 + 变换 + 显示）。</summary>
    private void UpdateStageLayer(int track, VideoFrame frame, VideoClip clip)
    {
        while (_stageLayers.Count <= track)
        {
            // 默认原比例（不拉伸）；轨道号越大越靠上层（后插在上），字幕与手柄覆盖层在最顶。
            var img = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false, IsVisible = false };
            _stageHostGrid.Children.Insert(_stageLayers.Count, img);
            _stageLayers.Add(new StageTrackLayer { Track = _stageLayers.Count, Image = img });
        }

        var layer = _stageLayers[track];
        WriteFrameToImage(layer.Image, ref layer.Bitmap, frame);
        ApplyTransform(layer.Image, clip);
        layer.Image.IsVisible = true;
        UpdateStageHandles();
    }

    /// <summary>把解码帧写入可复用的 WriteableBitmap 并挂到目标 Image（显式失效触发重绘）。</summary>
    private static void WriteFrameToImage(Image image, ref WriteableBitmap? bitmap, VideoFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        if (bitmap == null ||
            bitmap.PixelSize.Width != w ||
            bitmap.PixelSize.Height != h)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var fb = bitmap.Lock())
        {
            var src = frame.Pixels;
            var dst = fb.Address;
            var srcStride = frame.Stride;
            var dstStride = fb.RowBytes;
            if (srcStride == dstStride)
            {
                var len = Math.Min(src.Length, (int)(fb.RowBytes * h));
                Marshal.Copy(src, 0, dst, len);
            }
            else
            {
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(src, y * srcStride, IntPtr.Add(dst, y * dstStride),
                        Math.Min(srcStride, dstStride));
                }
            }
        }

        image.Source = bitmap;
        image.InvalidateVisual();
    }

    /// <summary>把片段变换应用到指定舞台 Image（与运行时 ApplyVideoClipTransform 同逻辑）。</summary>
    private void ApplyTransform(Image image, VideoClip clip)
    {
        var w = _stageBorder.Bounds.Width;
        var h = _stageBorder.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(clip.Scale, clip.Scale));
        group.Children.Add(new RotateTransform(clip.Rotation));
        group.Children.Add(new TranslateTransform(clip.OffsetX * w, clip.OffsetY * h));
        image.RenderTransform = group;
        image.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        image.Opacity = Math.Clamp(clip.Opacity, 0, 1);
        if (clip.CropLeft > 0 || clip.CropTop > 0 || clip.CropRight < 1 || clip.CropBottom < 1)
        {
            image.Clip = new RectangleGeometry(new Rect(
                clip.CropLeft * w,
                clip.CropTop * h,
                Math.Max(0, (clip.CropRight - clip.CropLeft) * w),
                Math.Max(0, (clip.CropBottom - clip.CropTop) * h)));
        }
        else
        {
            image.Clip = null;
        }
    }

    // ============ 舞台八向手柄（仿底图图层编辑器）============

    /// <summary>建立八向缩放手柄 + 选中虚线框（隐藏，选中片段且有画面时显示）。</summary>
    private void BuildStageHandles()
    {
        _handleOutline = new Border
        {
            BorderBrush = ThemePalette.AccentBrushWithAlpha(220),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Background = ThemePalette.AccentBrushWithAlpha(18),
            IsHitTestVisible = true
        };
        _handleOutline.PointerPressed += (_, e) =>
        {
            _resizeHandle = 8; // 8 = 移动
            _resizeStart = e.GetPosition(_stageBorder);
            _resizeStartRect = _selected != null ? GetSelectedDisplayRect(_selected) : default;
            _resizeClip = _selected;
            e.Pointer.Capture(_handleOutline);
            e.Handled = true;
        };
        _handleOutline.PointerMoved += (_, e) =>
        {
            if (_resizeHandle == 8 && _resizeClip != null)
            {
                ApplyResizeDrag(8, e.GetPosition(_stageBorder), _resizeStart, _resizeStartRect, _resizeClip);
            }
        };
        _handleOutline.PointerReleased += (_, e) =>
        {
            if (_resizeHandle == 8)
            {
                _resizeHandle = -1;
                _resizeClip = null;
                e.Pointer.Capture(null);
                FillPropertyPanel();
            }
        };
        _stageHandleOverlay.Children.Add(_handleOutline);

        for (var i = 0; i < 8; i++)
        {
            // 12px 视觉方块 + 负边距扩大触摸命中热区（实际 24px）。
            var handle = new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(-6),
                Background = Brushes.White,
                BorderBrush = ThemePalette.AccentBrush(),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(2)
            };
            // 该 Avalonia 版本无对角光标，角用 SizeAll、上下 SizeNorthSouth、左右 SizeWestEast。
            handle.Cursor = new Cursor(i switch
            {
                1 or 5 => StandardCursorType.SizeNorthSouth,
                3 or 7 => StandardCursorType.SizeWestEast,
                _ => StandardCursorType.SizeAll
            });
            var index = i;
            handle.PointerPressed += (_, e) =>
            {
                _resizeHandle = index;
                _resizeStart = e.GetPosition(_stageBorder);
                _resizeStartRect = _selected != null ? GetSelectedDisplayRect(_selected) : default;
                _resizeClip = _selected;
                e.Pointer.Capture(handle);
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (_resizeHandle == index && _resizeClip != null)
                {
                    ApplyResizeDrag(index, e.GetPosition(_stageBorder), _resizeStart, _resizeStartRect, _resizeClip);
                }
            };
            handle.PointerReleased += (_, e) =>
            {
                if (_resizeHandle == index)
                {
                    _resizeHandle = -1;
                    _resizeClip = null;
                    e.Pointer.Capture(null);
                    FillPropertyPanel();
                }
            };
            _handles[i] = handle;
            _stageHandleOverlay.Children.Add(handle);
        }

        _stageHandleOverlay.IsVisible = false;
    }

    /// <summary>按选中片段在舞台上的显示矩形（原比例基准 + 缩放/偏移，忽略旋转）摆放八向手柄。</summary>
    private void UpdateStageHandles()
    {
        if (_stageHandleOverlay == null)
        {
            return;
        }

        var clip = _selected;
        if (clip == null || clip.Track >= _stageLayers.Count || !_stageLayers[clip.Track].Image.IsVisible)
        {
            _stageHandleOverlay.IsVisible = false;
            return;
        }

        var rect = GetSelectedDisplayRect(clip);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _stageHandleOverlay.IsVisible = false;
            return;
        }

        _stageHandleOverlay.IsVisible = true;
        Canvas.SetLeft(_handleOutline, rect.X);
        Canvas.SetTop(_handleOutline, rect.Y);
        _handleOutline.Width = rect.Width;
        _handleOutline.Height = rect.Height;

        var pts = new[]
        {
            new Point(rect.Left, rect.Top),
            new Point(rect.Center.X, rect.Top),
            new Point(rect.Right, rect.Top),
            new Point(rect.Right, rect.Center.Y),
            new Point(rect.Right, rect.Bottom),
            new Point(rect.Center.X, rect.Bottom),
            new Point(rect.Left, rect.Bottom),
            new Point(rect.Left, rect.Center.Y)
        };
        for (var i = 0; i < 8; i++)
        {
            // 手柄布局 24px（含负边距），以锚点为中心。
            Canvas.SetLeft(_handles[i], pts[i].X - 12);
            Canvas.SetTop(_handles[i], pts[i].Y - 12);
        }
    }

    /// <summary>选中片段在舞台上的显示矩形（原比例 Uniform 基准 + 缩放 + 偏移）。</summary>
    private Rect GetSelectedDisplayRect(VideoClip clip)
    {
        var W = _stageBorder.Bounds.Width;
        var H = _stageBorder.Bounds.Height;
        if (W <= 0 || H <= 0)
        {
            return default;
        }

        var layer = clip.Track < _stageLayers.Count ? _stageLayers[clip.Track] : null;
        var bw = layer?.Bitmap?.PixelSize.Width ?? 16;
        var bh = layer?.Bitmap?.PixelSize.Height ?? 9;
        var aspect = bw / (double)Math.Max(1, bh);
        var stageAspect = W / H;
        double baseW, baseH;
        if (aspect >= stageAspect)
        {
            baseW = W;
            baseH = W / aspect;
        }
        else
        {
            baseH = H;
            baseW = H * aspect;
        }

        var sw = baseW * Math.Max(0.01, clip.Scale);
        var sh = baseH * Math.Max(0.01, clip.Scale);
        var x = W / 2.0 - sw / 2.0 + clip.OffsetX * W;
        var y = H / 2.0 - sh / 2.0 + clip.OffsetY * H;
        return new Rect(x, y, sw, sh);
    }

    /// <summary>拖拽手柄/移动：更新选中片段的缩放与偏移，并实时应用到舞台。</summary>
    private void ApplyResizeDrag(int handle, Point current, Point start, Rect startRect, VideoClip clip)
    {
        var W = _stageBorder.Bounds.Width;
        var H = _stageBorder.Bounds.Height;
        if (W <= 0 || H <= 0)
        {
            return;
        }

        if (handle == 8)
        {
            // 移动：偏移增量。
            clip.OffsetX = Math.Clamp(clip.OffsetX + (current.X - start.X) / W, -2, 2);
            clip.OffsetY = Math.Clamp(clip.OffsetY + (current.Y - start.Y) / H, -2, 2);
        }
        else
        {
            var scale0 = Math.Max(0.01, clip.Scale);
            var baseW = startRect.Width / scale0;
            var baseH = startRect.Height / scale0;
            var anchor = HandleAnchor(handle, startRect);
            double scale;
            if (handle is 0 or 2 or 4 or 6)
            {
                // 角：按对角距离，保持比例（cover）。
                scale = Math.Max(
                    baseW > 0 ? Math.Abs(current.X - anchor.X) / baseW : 1,
                    baseH > 0 ? Math.Abs(current.Y - anchor.Y) / baseH : 1);
            }
            else if (handle is 1 or 5)
            {
                scale = baseH > 0 ? Math.Abs(current.Y - anchor.Y) / baseH : 1;
            }
            else
            {
                scale = baseW > 0 ? Math.Abs(current.X - anchor.X) / baseW : 1;
            }

            scale = Math.Clamp(scale, 0.05, 10);
            var newW = baseW * scale;
            var newH = baseH * scale;
            var center = new Point((anchor.X + current.X) / 2.0, (anchor.Y + current.Y) / 2.0);
            var newCx = Math.Clamp(center.X, newW / 2.0, Math.Max(newW / 2.0, W - newW / 2.0));
            var newCy = Math.Clamp(center.Y, newH / 2.0, Math.Max(newH / 2.0, H - newH / 2.0));
            clip.Scale = scale;
            clip.OffsetX = (newCx - W / 2.0) / W;
            clip.OffsetY = (newCy - H / 2.0) / H;
        }

        if (clip.Track < _stageLayers.Count)
        {
            ApplyTransform(_stageLayers[clip.Track].Image, clip);
        }

        UpdateStageHandles();
        _clipNameText.Text = $"{Path.GetFileName(clip.SourcePath)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n缩放 {clip.Scale:0.##}x · 偏移 ({clip.OffsetX:0.##}, {clip.OffsetY:0.##})";
    }

    /// <summary>手柄的固定锚点（对角 / 对边中点）。</summary>
    private static Point HandleAnchor(int handle, Rect r) => handle switch
    {
        0 => new Point(r.Right, r.Bottom),
        1 => new Point(r.Center.X, r.Bottom),
        2 => new Point(r.Left, r.Bottom),
        3 => new Point(r.Left, r.Center.Y),
        4 => new Point(r.Left, r.Top),
        5 => new Point(r.Center.X, r.Top),
        6 => new Point(r.Right, r.Top),
        _ => new Point(r.Right, r.Center.Y)
    };

    // ============ 渲染并应用 ============

    private async void RenderAndApply()
    {
        StopPreview();
        _project.Clips.RemoveAll(c => string.IsNullOrWhiteSpace(c.SourcePath) || !File.Exists(c.SourcePath));
        if (_project.Clips.Count == 0)
        {
            _statusText.Text = "时间轴为空，无法渲染。请先添加片段。";
            return;
        }

        if (!FFmpegRuntime.IsAvailable || !FFmpegRuntime.EnsureLoaded())
        {
            _statusText.Text = "缺少 FFmpeg 编码库，无法渲染。请先在设置页下载 FFmpeg。";
            return;
        }

        // 1. 选择输出位置。
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } provider)
        {
            return;
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "选择渲染输出位置",
            SuggestedFileName = "injector-video.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices =
            [
                new FilePickerFileType("MP4 视频") { Patterns = ["*.mp4"] }
            ]
        });
        if (file?.TryGetLocalPath() is not { } outputPath || string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        // 2. 分辨率 / 画质选项（默认 270p）。
        var options = await AskRenderOptionsAsync();
        if (options == null)
        {
            return;
        }

        // 3. 保存工程快照并后台渲染（进度实时显示在状态栏）。
        var (outW, outH, crf) = options.Value;
        VideoProjectStore.Save(_project, VideoProjectStore.DefaultPath);
        _statusText.Text = "正在渲染…（后台进行，完成后自动应用）";
        IProgress<(double percent, string msg)> progress = new Progress<(double percent, string msg)>(p =>
        {
            _statusText.Text = $"{p.msg}（{p.percent:P0}）";
        });
        try
        {
            await Task.Run(() =>
            {
                new VideoProjectRenderer(_project, outputPath, outW, outH, crf, _targetFps,
                    (percent, msg) => progress.Report((percent, msg))).Render();
            });
        }
        catch (Exception ex)
        {
            _statusText.Text = $"渲染失败：{ex.Message}";
            return;
        }

        // 4. 把渲染出的 mp4 应用为主界面视频背景（单文件模式）。
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        settings.VideoFillEnabled = true;
        settings.VideoFillPath = outputPath;
        settings.VideoFillLoop = true;
        settings.VideoProjectEnabled = false;
        settings.EndUpdate();
        InjectorRuntime.SaveAndApply();
        _statusText.Text = $"已渲染 {outW}×{outH} 并应用到主界面。";
        Close();
    }

    /// <summary>渲染选项对话框：分辨率（默认 270p）+ 画质（CRF）。返回 null 表示取消。</summary>
    private async Task<(int outW, int outH, int crf)?> AskRenderOptionsAsync()
    {
        var resolutions = new[] { "270p", "360p", "480p", "720p", "原尺寸" };
        var qualities = new[] { "高", "中", "低" };
        var resCombo = new ComboBox { ItemsSource = resolutions, SelectedIndex = 0 };
        var qualityCombo = new ComboBox { ItemsSource = qualities, SelectedIndex = 1 };
        var dialog = new ContentDialog
        {
            Title = "渲染设置",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "分辨率（默认 270p 足够做主界面背景）：" },
                    resCombo,
                    new TextBlock { Text = "画质（越高文件越大）：" },
                    qualityCombo
                }
            },
            PrimaryButtonText = "开始渲染",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var targetH = (resCombo.SelectedItem?.ToString()) switch
        {
            "360p" => 360,
            "480p" => 480,
            "720p" => 720,
            "原尺寸" => (int)_project.OutputHeight,
            _ => 270
        };
        var aspect = _project.OutputWidth / Math.Max(1.0, _project.OutputHeight);
        var outH = Math.Max(2, targetH) & ~1;
        var outW = Math.Max(2, (int)Math.Round(outH * aspect / 2.0) * 2);
        var crf = (qualityCombo.SelectedItem?.ToString()) switch
        {
            "高" => 20,
            "低" => 28,
            _ => 24
        };
        return (outW, outH, crf);
    }

    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        return TopLevel.GetTopLevel(this) is Window host
            ? dialog.ShowAsync(host)
            : dialog.ShowAsync();
    }
}
