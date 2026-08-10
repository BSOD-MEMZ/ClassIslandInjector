using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using System.Globalization;

namespace ClassIslandInjector.Views;

/// <summary>
/// 底图编辑器的工具（Photoshop 式左侧工具栏）。
/// </summary>
internal enum WallpaperEditorTool
{
    /// <summary>移动工具（默认）：拖拽图层移动。</summary>
    Move,
    /// <summary>选择工具：点击只选中图层，不拖拽。</summary>
    Select,
    /// <summary>缩放工具：单击放大 / Alt+单击缩小 / 拖拽框选放大。</summary>
    Zoom,
    /// <summary>形状工具：拖拽绘制矢量形状图层。</summary>
    Shape,
    /// <summary>文本工具：点击插入文本框图层。</summary>
    Text,
    /// <summary>抓手工具：按住拖动平移画布视图。</summary>
    Hand
}

/// <summary>
/// 底图图层编辑器的画布：渲染岛屿 + 图片/形状/文本图层，提供移动 / 八向缩放 / 旋转、
/// 智能对齐标尺（吸附）、岛屿解锁拖动测试自适应。
/// 所有矩形运算都在「岛屿坐标系」（原点 = 岛屿左上角）进行，锚点定位公式与运行时一致。
/// </summary>
internal sealed class WallpaperLayerCanvas : UserControl
{
    private const double CanvasMargin = 180;
    private const double SnapThreshold = 7;
    private const double MinLayerSize = 8;

    private readonly ScrollViewer _scrollViewer = new();
    private readonly Border _stageHost = new() { ClipToBounds = true };
    private readonly Canvas _stage = new();
    private readonly Border _island;
    private readonly TextBlock _islandTitle = new()
    {
        Text = "正在上课",
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock _islandSubtitle = new()
    {
        Text = "数学  ·  08:00 – 08:45",
        Opacity = 0.8,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly IslandOutlineOverlay _islandOutline = new() { IsHitTestVisible = false };
    private readonly SelectionOverlay _selectionOverlay = new() { IsHitTestVisible = false };
    private readonly GuideOverlay _guideOverlay = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, Image> _layerImages = [];
    private readonly Dictionary<string, WallpaperLayerVisual> _layerVisuals = [];
    private readonly Dictionary<string, WallpaperNineSliceVisual> _layerNineSlices = [];
    private readonly Dictionary<string, Bitmap> _bitmaps = [];
    private readonly Dictionary<string, MemoryStream> _streams = [];
    private readonly Dictionary<string, string> _loadedSignatures = [];
    /// <summary>缩放工具拖拽框选 / 形状工具预览用的半透明选框（跟随主题强调色）。</summary>
    private readonly Border _marqueeRect = new()
    {
        IsVisible = false,
        IsHitTestVisible = false,
        BorderBrush = new SolidColorBrush(ThemePalette.AccentColorWithAlpha(200)),
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(ThemePalette.AccentColorWithAlpha(26)),
        ZIndex = 200
    };
    private readonly List<Border> _resizeHandles = [];
    private readonly Dictionary<Border, (int Dx, int Dy)> _handleDirs = [];
    private readonly Border _rotationHandle;
    private readonly List<Border> _islandHandles = [];
    private readonly Dictionary<Border, (int Dx, int Dy)> _islandHandleDirs = [];
    /// <summary>选中图层上方的浮动操作条（对齐 / 删除）。</summary>
    private readonly Border _floatToolbar;
    /// <summary>浮动操作条层序按钮（置顶/置底时禁用）。</summary>
    private Button _moveUpButton = null!;
    private Button _moveDownButton = null!;
    /// <summary>缩放滑动条（舞台右下角）。</summary>
    private readonly Slider _zoomSlider = new()
    {
        Minimum = 0.4,
        Maximum = 2.5,
        Value = 1,
        Width = 130,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _zoomText = new()
    {
        Text = "100%",
        MinWidth = 40,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.8
    };

    private List<WallpaperLayerItem> _layers = [];
    private double _islandWidth = 400;
    private double _islandHeight = 90;
    private double _zoom = 1;
    private WallpaperLayerZOrder _zOrder;
    /// <summary>主选中图层 Id（最后一个点击/操作的选中项）。</summary>
    private string? _selectedId;
    /// <summary>全部选中图层 Id（含主选中；Ctrl 多选时包含多个）。</summary>
    private readonly List<string> _selectedIds = [];
    /// <summary>内部剪贴板：Ctrl+C 复制的图层（Ctrl+V 粘贴）。</summary>
    private WallpaperLayerItem? _copiedLayer;
    private readonly HashSet<string> _lockedIds = [];
    private bool _islandUnlocked;
    private DragState? _drag;
    /// <summary>浮动工具条是否已显示（用于首次显示后按真实尺寸重定位）。</summary>
    private bool _floatToolbarShown;
    /// <summary>当前工具（Photoshop 式左侧工具栏）。</summary>
    private WallpaperEditorTool _tool = WallpaperEditorTool.Move;
    /// <summary>形状工具当前形状类型。</summary>
    private WallpaperShapeType _shapeToolType = WallpaperShapeType.Rectangle;
    /// <summary>当前位于舞台上的触摸点；用于空白处平移和双指捏合缩放。</summary>
    private readonly Dictionary<IPointer, Point> _touchPoints = [];
    private bool _isPinching;
    private Point _pinchCenter;
    private double _pinchStartDistance;
    private double _pinchStartZoom;

    public event Action? EditStarted;
    public event Action? Edited;
    public event Action? SelectionChanged;
    public event Action? IslandChanged;
    public event Action? ImagesChanged;
    public event Action<WallpaperLayerItem>? DeleteRequested;
    /// <summary>工具切换（供窗口左侧工具栏同步选中态）。</summary>
    public event Action<WallpaperEditorTool>? ToolChanged;

    public WallpaperLayerCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        _stage.Background = BuildCheckerBrush();
        UpdateToolCursor();
        _stage.Width = _islandWidth + CanvasMargin * 2;
        _stage.Height = _islandHeight + CanvasMargin * 2;
        _stage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        _stage.RenderTransform = new ScaleTransform(_zoom, _zoom);
        _stage.SizeChanged += (_, _) =>
        {
            _islandOutline.Width = _stage.Width;
            _islandOutline.Height = _stage.Height;
            _guideOverlay.Width = _stage.Width;
            _guideOverlay.Height = _stage.Height;
            _guideOverlay.InvalidateVisual();
        };

        _island = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0.72,
            Padding = new Thickness(18, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _islandTitle,
                    _islandSubtitle
                }
            }
        };

        // 八向缩放手柄
        foreach (var (name, dir, cursor) in new[]
                 {
                     ("nw", (Dx: -1, Dy: -1), StandardCursorType.TopLeftCorner),
                     ("n", (Dx: 0, Dy: -1), StandardCursorType.TopSide),
                     ("ne", (Dx: 1, Dy: -1), StandardCursorType.TopRightCorner),
                     ("e", (Dx: 1, Dy: 0), StandardCursorType.RightSide),
                     ("se", (Dx: 1, Dy: 1), StandardCursorType.BottomRightCorner),
                     ("s", (Dx: 0, Dy: 1), StandardCursorType.BottomSide),
                     ("sw", (Dx: -1, Dy: 1), StandardCursorType.BottomLeftCorner),
                     ("w", (Dx: -1, Dy: 0), StandardCursorType.LeftSide)
                 })
        {
            var handle = Handle(9, new SolidColorBrush(Color.FromRgb(0, 120, 212)), cursor);
            handle.Name = name;
            handle.PointerPressed += (s, e) => ResizeHandleOnPointerPressed(handle, e);
            handle.PointerMoved += (s, e) => ResizeHandleOnPointerMoved(handle, e);
            handle.PointerReleased += (s, e) => ResizeHandleOnPointerReleased(handle, e);
            _resizeHandles.Add(handle);
            _handleDirs[handle] = dir;
            _stage.Children.Add(handle);
        }

        // 旋转手柄（选中图层上方的紫色圆点）
        _rotationHandle = Handle(11, new SolidColorBrush(Color.FromRgb(121, 80, 242)), StandardCursorType.Hand);
        _rotationHandle.PointerPressed += RotationHandleOnPointerPressed;
        _rotationHandle.PointerMoved += RotationHandleOnPointerMoved;
        _rotationHandle.PointerReleased += RotationHandleOnPointerReleased;
        _stage.Children.Add(_rotationHandle);

        // 岛屿缩放手柄（解锁后出现：右 / 下 / 右下角）
        foreach (var (dir, cursor) in new[]
                 {
                     ((Dx: 1, Dy: 0), StandardCursorType.RightSide),
                     ((Dx: 0, Dy: 1), StandardCursorType.BottomSide),
                     ((Dx: 1, Dy: 1), StandardCursorType.BottomRightCorner)
                 })
        {
            var handle = Handle(11, new SolidColorBrush(Color.FromRgb(0, 170, 120)), cursor);
            handle.PointerPressed += (s, e) => IslandHandleOnPointerPressed(handle, e);
            handle.PointerMoved += (s, e) => IslandHandleOnPointerMoved(handle, e);
            handle.PointerReleased += (s, e) => IslandHandleOnPointerReleased(handle, e);
            _islandHandles.Add(handle);
            _islandHandleDirs[handle] = dir;
            _stage.Children.Add(handle);
        }

        _stage.Children.Add(_island);
        _stage.Children.Add(_islandOutline);
        _stage.Children.Add(_selectionOverlay);
        _stage.Children.Add(_guideOverlay);
        _stage.Children.Add(_marqueeRect);
        _island.ZIndex = 20;
        _islandOutline.ZIndex = 40;
        _selectionOverlay.ZIndex = 100;
        _guideOverlay.ZIndex = 110;
        foreach (var h in _resizeHandles.Concat(_islandHandles).Append(_rotationHandle))
        {
            h.ZIndex = 120;
        }

        // 选中图层上方的浮动操作条（参考 ClassIsland 编辑模式）：对齐 + 层序 + 复制 + 删除。
        // 置于根网格（不随舞台缩放/滚动）。背景按宿主主题深浅直接取稳定色值（不依赖可能解析错误的主题资源），
        // 图标前景按背景明暗自适应，避免深色主题下出现「浅色浮动条」。
        var toolbarBackground = ThemePalette.PanelBackground();
        var toolbarForeground = ThemePalette.ForegroundColor();
        var toolbarChildren = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        toolbarChildren.Children.Add(FloatButton("\uE03B", "左对齐", () => AlignSelected(0, null), toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE033", "水平居中", () => AlignSelected(1, null), toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE03D", "右对齐", () => AlignSelected(2, null), toolbarForeground));
        toolbarChildren.Children.Add(ToolbarSeparator(toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE057", "顶对齐", () => AlignSelected(null, 0), toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE035", "垂直居中", () => AlignSelected(null, 1), toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE031", "底对齐", () => AlignSelected(null, 2), toolbarForeground));
        toolbarChildren.Children.Add(ToolbarSeparator(toolbarForeground));
        _moveUpButton = FloatButton("\uE197", "上一层", () => MoveLayerUp(), toolbarForeground);
        _moveDownButton = FloatButton("\uE0CB", "下一层", () => MoveLayerDown(), toolbarForeground);
        toolbarChildren.Children.Add(_moveUpButton);
        toolbarChildren.Children.Add(_moveDownButton);
        toolbarChildren.Children.Add(ToolbarSeparator(toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE58B", "复制图层", () => DuplicateSelection(), toolbarForeground));
        toolbarChildren.Children.Add(ToolbarSeparator(toolbarForeground));
        toolbarChildren.Children.Add(FloatButton("\uE61D", "删除图层", () =>
        {
            var layer = SelectedLayer;
            if (layer != null)
            {
                DeleteRequested?.Invoke(layer);
            }
        }, toolbarForeground, isDanger: true));
        _floatToolbar = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(6),
            Background = toolbarBackground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = true,
            ZIndex = 500,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Color = Color.FromArgb(90, 0, 0, 0) }),
            Child = toolbarChildren
        };

        _stage.PointerPressed += StageOnPointerPressed;
        _stage.PointerMoved += StageOnPointerMoved;
        _stage.PointerReleased += StageOnPointerReleased;
        _stage.PointerCaptureLost += StageOnPointerCaptureLost;
        _stage.PointerWheelChanged += StageOnPointerWheelChanged;
        KeyDown += CanvasOnKeyDown;
        // 支持从系统文件管理器直接拖拽图片到画布创建图层。
        DragDrop.SetAllowDrop(_stage, true);
        _stage.AddHandler(DragDrop.DragOverEvent, StageOnDragOver);
        _stage.AddHandler(DragDrop.DropEvent, StageOnDrop);

        _stageHost.Child = _stage;
        _scrollViewer.Content = _stageHost;
        _zoomSlider.ValueChanged += (_, _) =>
        {
            Zoom = _zoomSlider.Value;
            _zoomText.Text = $"{_zoomSlider.Value:P0}";
        };
        var zoomPanel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            Background = ThemePalette.PanelBackground(),
            BorderThickness = new Thickness(0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "缩放", Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center },
                    _zoomSlider,
                    _zoomText
                }
            }
        };
        Content = new Grid { Children = { _scrollViewer, zoomPanel, _floatToolbar } };
        UpdateStageSize();
    }

    /// <summary>浮动操作条按钮（透明底 + 悬停微高亮；危险操作用印度红）。前景色跟随工具条背景明暗。</summary>
    private static Button FloatButton(string glyph, string tooltip, Action action, Color foreground, bool isDanger = false)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = glyph, Text = string.Empty },
            Padding = new Thickness(6, 3),
            MinWidth = 26,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(isDanger ? Color.FromRgb(205, 92, 92) : foreground)
        };
        var hover = Color.FromArgb(36, foreground.R, foreground.G, foreground.B);
        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(hover);
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>浮动操作条内的竖向分隔线（优先原生分割线颜色，回退与前景同色系）。</summary>
    private static Avalonia.Controls.Shapes.Line ToolbarSeparator(Color foreground) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 24),
        Stroke = ThemeBrush("DividerStrokeColorDefaultBrush")
            ?? new SolidColorBrush(Color.FromArgb(110, foreground.R, foreground.G, foreground.B)),
        StrokeThickness = 1,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0)
    };

    /// <summary>查找主题资源。</summary>
    private static object? FindThemeResource(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    /// <summary>查找主题画刷。</summary>
    private static IBrush? ThemeBrush(string key) => FindThemeResource(key) as IBrush;

    // ============ 公共接口 ============

    public List<WallpaperLayerItem> Layers
    {
        get => _layers;
        set
        {
            _layers = value;
            RefreshImages();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var v = Math.Clamp(value, 0.4, 2.5);
            if (Math.Abs(_zoom - v) < 0.0001)
            {
                return;
            }

            _zoom = v;
            _stage.RenderTransform = new ScaleTransform(_zoom, _zoom);
            UpdateStageSize();
            _zoomSlider.Value = v;
            _zoomText.Text = $"{v:P0}";
        }
    }

    /// <summary>当前工具（左侧工具栏切换；移动工具为默认）。</summary>
    public WallpaperEditorTool Tool
    {
        get => _tool;
        set => SwitchTool(value);
    }

    /// <summary>形状工具当前形状类型。</summary>
    public WallpaperShapeType ShapeToolType
    {
        get => _shapeToolType;
        set => _shapeToolType = value;
    }

    private void SwitchTool(WallpaperEditorTool tool)
    {
        if (_tool == tool)
        {
            return;
        }

        _tool = tool;
        _drag = null;
        _guideOverlay.Clear();
        _marqueeRect.IsVisible = false;
        UpdateToolCursor();
        ToolChanged?.Invoke(tool);
    }

    private void UpdateToolCursor()
    {
        Cursor = _tool switch
        {
            WallpaperEditorTool.Move => new Cursor(StandardCursorType.SizeAll),
            WallpaperEditorTool.Zoom => new Cursor(StandardCursorType.Cross),
            WallpaperEditorTool.Shape => new Cursor(StandardCursorType.Cross),
            WallpaperEditorTool.Text => new Cursor(StandardCursorType.Ibeam),
            WallpaperEditorTool.Hand => new Cursor(StandardCursorType.Hand),
            _ => new Cursor(StandardCursorType.Arrow)
        };
    }

    public WallpaperLayerZOrder ZOrder
    {
        get => _zOrder;
        set
        {
            _zOrder = value;
            Refresh();
        }
    }

    public double IslandWidth => _islandWidth;
    public double IslandHeight => _islandHeight;

    public bool IslandUnlocked
    {
        get => _islandUnlocked;
        set
        {
            _islandUnlocked = value;
            UpdateIslandHandles();
        }
    }

    public WallpaperLayerItem? SelectedLayer =>
        _selectedId == null ? null : _layers.FirstOrDefault(l => l.Id == _selectedId);

    /// <summary>全部选中的图层（含主选中；顺序与 _layers 一致）。</summary>
    public IReadOnlyList<WallpaperLayerItem> SelectedLayers =>
        _layers.Where(l => _selectedIds.Contains(l.Id)).ToList();

    public bool IsLocked(string id) => _lockedIds.Contains(id);

    public void ToggleLock(string id)
    {
        if (!_lockedIds.Add(id))
        {
            _lockedIds.Remove(id);
        }

        Refresh();
    }

    /// <summary>单选：清空旧选择后选中指定图层（null 取消全部选中）。</summary>
    public void Select(string? id)
    {
        if (_selectedId == id && _selectedIds.Count == (id == null ? 0 : 1) &&
            (id == null || _selectedIds.Contains(id)))
        {
            return;
        }

        _selectedId = id;
        _selectedIds.Clear();
        if (id != null)
        {
            _selectedIds.Add(id);
        }

        Refresh();
        SelectionChanged?.Invoke();
    }

    /// <summary>Ctrl 多选：切换指定图层的选中状态（保留其它已选），主选中 = 最后点击项。</summary>
    public void SelectWithToggle(string? id)
    {
        if (id == null)
        {
            Select(null);
            return;
        }

        if (_selectedIds.Contains(id))
        {
            _selectedIds.Remove(id);
            if (_selectedId == id)
            {
                _selectedId = _selectedIds.Count > 0 ? _selectedIds[^1] : null;
            }
        }
        else
        {
            _selectedIds.Add(id);
            _selectedId = id;
        }

        Refresh();
        SelectionChanged?.Invoke();
    }

    /// <summary>选中指定图层；若该图层属于某个组，则选中整个组（点击组内任意成员 = 选中整组，
    /// 主选中 = 点击的成员，便于直接拖动/缩放该成员带动整组）。</summary>
    public void SelectWithGroup(string? id)
    {
        if (id == null)
        {
            Select(null);
            return;
        }

        var layer = _layers.FirstOrDefault(l => l.Id == id);
        if (layer == null || string.IsNullOrEmpty(layer.GroupId))
        {
            Select(id);
            return;
        }

        var ids = _layers.Where(l => l.GroupId == layer.GroupId).Select(l => l.Id).ToList();
        _selectedId = id;
        _selectedIds.Clear();
        _selectedIds.AddRange(ids);
        Refresh();
        SelectionChanged?.Invoke();
    }

    public void SetIslandSize(double width, double height)
    {
        _islandWidth = Math.Clamp(width, 120, 1600);
        _islandHeight = Math.Clamp(height, 40, 500);
        UpdateStageSize();
        Refresh();
    }

    public void Refresh()
    {
        UpdateStageSize();
        RefreshIslandAppearance();
        LayoutImages();
        UpdateSelectionOverlay();
        UpdateIslandHandles();
    }

    public Bitmap? GetThumbnail(string id) => _bitmaps.TryGetValue(id, out var bm) ? bm : null;

    /// <summary>
    /// 把选中图层对齐到岛屿对应参考点（等价于把锚点设为对应值并清零偏移），
    /// 供浮动操作条与键盘操作调用；多选时对全部选中生效（跳过锁定）；会压入撤销并触发刷新。
    /// </summary>
    public void AlignSelected(int? xIndex, int? yIndex)
    {
        var layers = SelectedLayers.Where(l => !_lockedIds.Contains(l.Id)).ToList();
        if (layers.Count == 0)
        {
            return;
        }

        EditStarted?.Invoke();
        foreach (var layer in layers)
        {
            if (xIndex is { } xi)
            {
                layer.AnchorX = xi switch { 0 => WallpaperLayerAnchorX.Left, 1 => WallpaperLayerAnchorX.Center, _ => WallpaperLayerAnchorX.Right };
                layer.OffsetX = 0;
            }

            if (yIndex is { } yi)
            {
                layer.AnchorY = yi switch { 0 => WallpaperLayerAnchorY.Top, 1 => WallpaperLayerAnchorY.Center, _ => WallpaperLayerAnchorY.Bottom };
                layer.OffsetY = 0;
            }
        }

        Refresh();
        Edited?.Invoke();
    }

    // ============ 图片加载 ============

    private void RefreshImages()
    {
        var ids = _layers.Select(l => l.Id).ToHashSet();
        foreach (var staleId in _bitmaps.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _bitmaps[staleId].Dispose();
            _bitmaps.Remove(staleId);
            if (_streams.TryGetValue(staleId, out var s))
            {
                s.Dispose();
                _streams.Remove(staleId);
            }

            _loadedSignatures.Remove(staleId);
        }

        foreach (var layer in _layers)
        {
            var signature = SignatureOf(layer);
            if (_loadedSignatures.TryGetValue(layer.Id, out var loaded) && loaded == signature)
            {
                continue;
            }

            _loadedSignatures[layer.Id] = signature;
            LoadBitmapFor(layer);
        }

        SyncImageControls();
        Refresh();
        ImagesChanged?.Invoke();
    }

    private static string SignatureOf(WallpaperLayerItem layer) => $"{layer.Source}|{layer.Path}";

    private void LoadBitmapFor(WallpaperLayerItem layer)
    {
        if (_bitmaps.TryGetValue(layer.Id, out var old))
        {
            old.Dispose();
            _bitmaps.Remove(layer.Id);
        }

        if (_streams.TryGetValue(layer.Id, out var oldStream))
        {
            oldStream.Dispose();
            _streams.Remove(layer.Id);
        }

        var path = ResolveLayerPath(layer);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));
            stream.Position = 0;
            var bitmap = new Bitmap(stream);
            _bitmaps[layer.Id] = bitmap;
            _streams[layer.Id] = stream;
        }
        catch
        {
            // 图片损坏时忽略，画布显示空图层。
        }
    }

    private static string? ResolveLayerPath(WallpaperLayerItem layer)
    {
        if (layer.Source == WallpaperSource.LocalImage)
        {
            return layer.Path;
        }

        if (layer.Source == WallpaperSource.FolderSlideshow)
        {
            if (string.IsNullOrWhiteSpace(layer.Path) || !Directory.Exists(layer.Path))
            {
                return null;
            }

            var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
            return Directory.EnumerateFiles(layer.Path)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (layer.Source == WallpaperSource.SmtcAlbum)
        {
            // 编辑器内用占位专辑封面预览；运行时由 SMTC 事件推送真实封面。
            var dir = Path.GetDirectoryName(typeof(WallpaperLayerCanvas).Assembly.Location);
            return dir == null ? null : Path.Combine(dir, "Assets", "album.png");
        }

        return null;
    }

    private double? AspectOf(WallpaperLayerItem layer) =>
        _bitmaps.TryGetValue(layer.Id, out var bm) && bm.PixelSize.Width > 0 && bm.PixelSize.Height > 0
            ? (double)bm.PixelSize.Width / bm.PixelSize.Height
            : null;

    private void SyncImageControls()
    {
        var wantedIds = _layers.Select(l => l.Id).ToHashSet();
        foreach (var staleId in _layerImages.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
        {
            _stage.Children.Remove(_layerImages[staleId]);
            _layerImages.Remove(staleId);
        }

        foreach (var staleId in _layerVisuals.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
        {
            _stage.Children.Remove(_layerVisuals[staleId]);
            _layerVisuals.Remove(staleId);
        }

        foreach (var staleId in _layerNineSlices.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
        {
            _stage.Children.Remove(_layerNineSlices[staleId]);
            _layerNineSlices.Remove(staleId);
        }

        foreach (var layer in _layers)
        {
            var isFullscreenImage = layer.Kind == WallpaperLayerKind.Image && layer.FullscreenExtend;
            if (isFullscreenImage)
            {
                // 全屏扩展图层用九宫格控件渲染（铺满显示框架）；若曾以普通 Image 存在则移除。
                if (_layerImages.Remove(layer.Id, out var oldImage))
                {
                    _stage.Children.Remove(oldImage);
                }

                if (!_layerNineSlices.TryGetValue(layer.Id, out var nine))
                {
                    nine = new WallpaperNineSliceVisual
                    {
                        IsHitTestVisible = false,
                        RenderTransformOrigin = RelativePoint.Center
                    };
                    _layerNineSlices[layer.Id] = nine;
                    _stage.Children.Add(nine);
                }

                nine.Bitmap = _bitmaps.TryGetValue(layer.Id, out var bm) ? bm : null;
                nine.SliceEnabled = layer.SliceEnabled;
                nine.SliceLeft = layer.SliceLeft;
                nine.SliceTop = layer.SliceTop;
                nine.SliceRight = layer.SliceRight;
                nine.SliceBottom = layer.SliceBottom;
            }
            else if (layer.Kind == WallpaperLayerKind.Image)
            {
                if (_layerNineSlices.Remove(layer.Id, out var oldNine))
                {
                    _stage.Children.Remove(oldNine);
                }

                if (!_layerImages.TryGetValue(layer.Id, out var image))
                {
                    image = new Image
                    {
                        IsHitTestVisible = false,
                        RenderTransformOrigin = RelativePoint.Center
                    };
                    _layerImages[layer.Id] = image;
                    _stage.Children.Add(image);
                }

                image.Source = _bitmaps.TryGetValue(layer.Id, out var bm) ? bm : null;
            }
            else if (!_layerVisuals.TryGetValue(layer.Id, out var visual))
            {
                visual = new WallpaperLayerVisual
                {
                    IsHitTestVisible = false,
                    RenderTransformOrigin = RelativePoint.Center
                };
                _layerVisuals[layer.Id] = visual;
                _stage.Children.Add(visual);
            }
        }
    }

    private void LayoutImages()
    {
        // 图层 z 序跟随列表顺序（后面的在上层），使拖拽排序在预览中即时生效。
        var imageBase = _zOrder == WallpaperLayerZOrder.BehindBackground ? 10 : 30;
        for (var i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            var rect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer));
            if (layer.Kind == WallpaperLayerKind.Image)
            {
                if (layer.FullscreenExtend)
                {
                    // 全屏扩展图层：画布中以岛屿区域近似预览（运行时铺满整个显示框架）。
                    if (!_layerNineSlices.TryGetValue(layer.Id, out var nine))
                    {
                        continue;
                    }

                    nine.Width = rect.Width;
                    nine.Height = rect.Height;
                    Canvas.SetLeft(nine, CanvasMargin + rect.X);
                    Canvas.SetTop(nine, CanvasMargin + rect.Y);
                    nine.Opacity = layer.Visible ? layer.Opacity : 0;
                    nine.IsVisible = layer.Visible;
                    nine.ZIndex = imageBase + i;
                    // 同步九宫格参数（检查器改动后预览实时更新）。
                    nine.SliceEnabled = layer.SliceEnabled;
                    nine.SliceLeft = layer.SliceLeft;
                    nine.SliceTop = layer.SliceTop;
                    nine.SliceRight = layer.SliceRight;
                    nine.SliceBottom = layer.SliceBottom;
                    nine.InvalidateVisual();
                    continue;
                }

                if (!_layerImages.TryGetValue(layer.Id, out var image))
                {
                    continue;
                }

                image.Width = rect.Width;
                image.Height = rect.Height;
                Canvas.SetLeft(image, CanvasMargin + rect.X);
                Canvas.SetTop(image, CanvasMargin + rect.Y);
                image.RenderTransform = new RotateTransform(layer.Rotation);
                image.Opacity = layer.Visible ? layer.Opacity : 0;
                image.IsVisible = layer.Visible;
                image.Stretch = WallpaperLayerLayout.ToStretch(layer.DisplayMode);
                image.ZIndex = imageBase + i;
            }
            else if (_layerVisuals.TryGetValue(layer.Id, out var visual))
            {
                visual.Width = rect.Width;
                visual.Height = rect.Height;
                Canvas.SetLeft(visual, CanvasMargin + rect.X);
                Canvas.SetTop(visual, CanvasMargin + rect.Y);
                visual.RenderTransform = new RotateTransform(layer.Rotation);
                visual.Opacity = layer.Visible ? layer.Opacity : 0;
                visual.IsVisible = layer.Visible;
                visual.ZIndex = imageBase + i;
                visual.Layer = layer;
            }
        }

        _island.ZIndex = _zOrder == WallpaperLayerZOrder.BehindBackground ? 20 : 5;
    }

    // ============ 渲染辅助 ============

    private void UpdateStageSize()
    {
        _stage.Width = _islandWidth + CanvasMargin * 2;
        _stage.Height = _islandHeight + CanvasMargin * 2;
        _stageHost.Width = _stage.Width * _zoom;
        _stageHost.Height = _stage.Height * _zoom;
        _islandOutline.Width = _stage.Width;
        _islandOutline.Height = _stage.Height;
        _guideOverlay.Width = _stage.Width;
        _guideOverlay.Height = _stage.Height;
    }

    private void RefreshIslandAppearance()
    {
        // 岛屿占位内容：铺满岛屿尺寸并置于舞台中央，避免固定贴在舞台左上角。
        Canvas.SetLeft(_island, CanvasMargin);
        Canvas.SetTop(_island, CanvasMargin);
        _island.Width = _islandWidth;
        _island.Height = _islandHeight;

        var s = InjectorRuntime.Settings;
        var color = TryParse(s.BackgroundColor, Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
        IBrush? background;
        if (s.CustomBackgroundEnabled && s.GradientEnabled)
        {
            var end = TryParse(s.GradientEndColor, Color.FromArgb(0xCC, 0x40, 0x40, 0xA0));
            var (p1, p2) = GradientGeometry.Points(s.GradientDirection);
            background = new LinearGradientBrush
            {
                StartPoint = p1,
                EndPoint = p2,
                GradientStops = [new GradientStop(color, 0), new GradientStop(end, 1)]
            };
        }
        else
        {
            // 未启用自定义背景时，预览也应尊重宿主明暗主题；否则浅色主题会得到
            // 深底黑字的低对比度岛屿。
            color = s.CustomBackgroundEnabled
                ? color
                : ThemePalette.IsDarkTheme()
                    ? Color.FromRgb(48, 51, 58)
                    : Color.FromRgb(255, 255, 255);
            background = new SolidColorBrush(color);
        }

        _island.Background = background;
        var foreground = new SolidColorBrush(ThemePalette.ContrastForeground(color));
        _islandTitle.Foreground = foreground;
        _islandSubtitle.Foreground = foreground;
        _island.CornerRadius = new CornerRadius(Math.Clamp(s.CornerRadius, 0, 60));
        _island.BorderBrush = s.BorderEnabled ? new SolidColorBrush(TryParse(s.BorderColor, Colors.White)) : null;
        _island.BorderThickness = s.BorderEnabled ? new Thickness(Math.Clamp(s.BorderThickness, 0, 20)) : new Thickness(0);
        _island.Effect = s.ShadowEnabled
            ? new DropShadowEffect
            {
                Color = TryParse(s.ShadowColor, Colors.Black),
                BlurRadius = Math.Min(s.ShadowBlur, 60),
                OffsetX = s.ShadowOffsetX,
                OffsetY = s.ShadowOffsetY,
                Opacity = s.ShadowOpacity
            }
            : null;
        _islandOutline.IslandBounds = new Rect(CanvasMargin, CanvasMargin, _islandWidth, _islandHeight);
        _islandOutline.InvalidateVisual();
    }

    private static Color TryParse(string text, Color fallback)
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

    /// <summary>
    /// 构建舞台棋盘格：跟随主题时按深浅色自动选择（深 = 深棋盘格，浅 = 白/浅灰 fff/ccc）；
    /// 关闭跟随主题时使用用户自定义的两色。
    /// </summary>
    private IBrush BuildCheckerBrush()
    {
        const double size = 12;
        var s = InjectorRuntime.Settings;
        Color c1;
        Color c2;
        if (s.WallpaperCheckerFollowTheme)
        {
            if (ThemePalette.IsDarkTheme())
            {
                c1 = Color.FromRgb(45, 47, 52);
                c2 = Color.FromRgb(38, 40, 45);
            }
            else
            {
                c1 = Color.FromRgb(255, 255, 255);
                c2 = Color.FromRgb(204, 204, 204);
            }
        }
        else
        {
            c1 = TryParse(s.WallpaperCheckerColor1, Color.FromRgb(45, 47, 52));
            c2 = TryParse(s.WallpaperCheckerColor2, Color.FromRgb(38, 40, 45));
        }

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing
        {
            Brush = new SolidColorBrush(c1),
            Geometry = new RectangleGeometry(new Rect(0, 0, size, size))
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = new SolidColorBrush(c2),
            Geometry = new RectangleGeometry(new Rect(0, 0, size / 2, size / 2))
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = new SolidColorBrush(c2),
            Geometry = new RectangleGeometry(new Rect(size / 2, size / 2, size / 2, size / 2))
        });
        return new DrawingBrush
        {
            Drawing = group,
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute)
        };
    }

    /// <summary>重建舞台棋盘格（设置变化后调用）。</summary>
    public void ApplyCheckerboardColors() => _stage.Background = BuildCheckerBrush();

    private static Border Handle(double size, IBrush background, StandardCursorType cursor) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = background,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 5, Color = Color.FromArgb(115, 0, 0, 0) }),
        Cursor = new Cursor(cursor),
        IsVisible = false
    };

    // ============ 交互：选中框与手柄定位 ============

    /// <summary>图层的舞台坐标选中矩形（岛屿坐标 + 边距）。</summary>
    private Rect LayerSelectionRect(WallpaperLayerItem layer)
    {
        var r = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer));
        return new Rect(CanvasMargin + r.X, CanvasMargin + r.Y, r.Width, r.Height);
    }

    private void UpdateSelectionOverlay()
    {
        var layer = SelectedLayer;
        if (layer == null)
        {
            _selectionOverlay.IsVisible = false;
            _selectionOverlay.SelectionRect = default;
            _selectionOverlay.SecondaryRects.Clear();
            _floatToolbar.IsVisible = false;
            foreach (var handle in _resizeHandles)
            {
                handle.IsVisible = false;
            }

            _rotationHandle.IsVisible = false;
            return;
        }

        // 多选时：主选中显示完整虚线框 + 手柄；其它选中只画虚线框（不画手柄）。
        var selected = SelectedLayers;
        var primary = LayerSelectionRect(layer);
        var x = primary.X;
        var y = primary.Y;
        var w = primary.Width;
        var h = primary.Height;
        _selectionOverlay.SecondaryRects.Clear();
        foreach (var other in selected)
        {
            if (other == layer)
            {
                continue;
            }

            _selectionOverlay.SecondaryRects.Add(LayerSelectionRect(other));
        }

        _selectionOverlay.IsVisible = true;
        _selectionOverlay.SelectionRect = new Rect(x, y, w, h);
        _selectionOverlay.RotationStart = new Point(x + w / 2, y);
        _selectionOverlay.RotationEnd = new Point(x + w / 2, y - 34);
        _selectionOverlay.InvalidateVisual();

        var locked = _lockedIds.Contains(layer.Id) || layer.FullscreenExtend;
        // 层序按钮：置顶时「上一层」禁用，置底时「下一层」禁用。
        var zIndex = _layers.IndexOf(layer);
        _moveUpButton.IsEnabled = !locked && zIndex >= 0 && zIndex < _layers.Count - 1;
        _moveDownButton.IsEnabled = !locked && zIndex > 0;
        foreach (var handle in _resizeHandles)
        {
            var dir = _handleDirs[handle];
            Canvas.SetLeft(handle, x + w * (dir.Dx + 1) / 2 - handle.Width / 2);
            Canvas.SetTop(handle, y + h * (dir.Dy + 1) / 2 - handle.Height / 2);
            handle.IsVisible = !locked;
        }

        Canvas.SetLeft(_rotationHandle, x + w / 2 - _rotationHandle.Width / 2);
        Canvas.SetTop(_rotationHandle, y - 40);
        _rotationHandle.IsVisible = !locked;

        // 浮动操作条：显示在选中图层上方（避开旋转手柄，位于其上约 50px 处）。
        // 舞台可能被缩放/滚动，先把选中框顶部中心换算到本控件坐标，再用 Margin 定位。
        var showToolbar = !locked;
        _floatToolbar.IsVisible = showToolbar;
        if (showToolbar)
        {
            var tw = _floatToolbar.Bounds.Width > 0 ? _floatToolbar.Bounds.Width : 250;
            var th = _floatToolbar.Bounds.Height > 0 ? _floatToolbar.Bounds.Height : 32;
            var anchor = _stage.TranslatePoint(new Point(x + w / 2, y), this) ?? new Point(x, y);
            var left = Math.Clamp(anchor.X - tw / 2, 2, Math.Max(2, Bounds.Width - tw - 2));
            var top = Math.Max(2, anchor.Y - th - 50);
            _floatToolbar.Margin = new Thickness(left, top, 0, 0);
            if (!_floatToolbarShown)
            {
                // 首次显示时 Bounds 尚未测量，下一帧按真实尺寸重定位。
                _floatToolbarShown = true;
                Dispatcher.UIThread.Post(Refresh);
            }
        }
        else
        {
            _floatToolbarShown = false;
        }
    }

    private void UpdateIslandHandles()
    {
        var x = CanvasMargin;
        var y = CanvasMargin;
        var w = _islandWidth;
        var h = _islandHeight;
        foreach (var handle in _islandHandles)
        {
            var dir = _islandHandleDirs[handle];
            Canvas.SetLeft(handle, x + w * (dir.Dx + 1) / 2 - handle.Width / 2);
            Canvas.SetTop(handle, y + h * (dir.Dy + 1) / 2 - handle.Height / 2);
            handle.IsVisible = _islandUnlocked;
        }
    }

    // ============ 命中测试 ============

    private WallpaperLayerItem? HitTestLayer(Point stagePos)
    {
        var islandPos = new Point(stagePos.X - CanvasMargin, stagePos.Y - CanvasMargin);
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            var rect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer));
            if (rect.Contains(islandPos))
            {
                return layer;
            }
        }

        return null;
    }

    private bool IsInsideIsland(Point stagePos) =>
        stagePos.X >= CanvasMargin && stagePos.Y >= CanvasMargin &&
        stagePos.X <= CanvasMargin + _islandWidth && stagePos.Y <= CanvasMargin + _islandHeight;

    // ============ 画布手势（按工具分发）============

    private void StageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_stage);
        var pos = point.Position;
        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoints[e.Pointer] = pos;
            if (_touchPoints.Count >= 2)
            {
                BeginPinch();
                e.Handled = true;
                return;
            }

            // 触屏在空白画布上拖动 = 平移视图；在图层上按下仍保留原有编辑行为。
            if (HitTestLayer(pos) == null && !IsInsideIsland(pos))
            {
                Focus();
                _drag = new DragState
                {
                    Kind = DragKind.Pan,
                    Pointer = e.Pointer,
                    StartPointer = pos,
                    StartScrollOffset = _scrollViewer.Offset
                };
                e.Handled = true;
                return;
            }
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        switch (_tool)
        {
            case WallpaperEditorTool.Select:
                SelectToolPress(pos, e);
                e.Handled = true;
                break;
            case WallpaperEditorTool.Zoom:
                _drag = new DragState
                {
                    Kind = DragKind.ZoomMarquee,
                    StartPointer = pos,
                    ZoomOut = point.Properties.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                };
                _marqueeRect.IsVisible = true;
                PositionMarquee(new Rect(pos.X, pos.Y, 0, 0));
                e.Pointer.Capture(_stage);
                e.Handled = true;
                break;
            case WallpaperEditorTool.Shape:
                BeginShapeDraw(pos, e);
                break;
            case WallpaperEditorTool.Text:
                PlaceText(pos);
                e.Handled = true;
                break;
            case WallpaperEditorTool.Hand:
                BeginHandPan(pos, e);
                break;
            default:
                MoveToolPress(pos, e);
                break;
        }
    }

    private void StageOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch && _touchPoints.ContainsKey(e.Pointer))
        {
            _touchPoints[e.Pointer] = e.GetPosition(_stage);
            if (_isPinching)
            {
                UpdatePinch();
                e.Handled = true;
                return;
            }

            if (_drag is { Kind: DragKind.Pan } pan && ReferenceEquals(pan.Pointer, e.Pointer))
            {
                UpdatePan(pan, e.GetPosition(_stage));
                e.Handled = true;
                return;
            }
        }

        switch (_drag?.Kind)
        {
            case DragKind.ZoomMarquee:
                PositionMarquee(NormalizeRect(_drag.StartPointer, e.GetPosition(_stage)));
                e.Handled = true;
                break;
            case DragKind.ShapeDraw:
                UpdateShapeDraw(_drag, e.GetPosition(_stage));
                e.Handled = true;
                break;
            case DragKind.Move:
                UpdateMove(_drag, e.GetPosition(_stage));
                e.Handled = true;
                break;
            case DragKind.Pan:
                UpdatePan(_drag, e.GetPosition(_stage));
                e.Handled = true;
                break;
        }
    }

    private void StageOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            var wasPan = _drag is { Kind: DragKind.Pan } pan && ReferenceEquals(pan.Pointer, e.Pointer);
            var wasPinching = _isPinching;
            _touchPoints.Remove(e.Pointer);
            if (_touchPoints.Count < 2)
            {
                _isPinching = false;
            }

            if (wasPan || wasPinching)
            {
                _drag = null;
                // 大倍率平移时 Skia 可能暂时裁掉画布外的图层；手势结束后重新布局并
                // 使舞台失效，确保移入视口的内容立即补绘。
                Refresh();
                _stage.InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        switch (_drag?.Kind)
        {
            case DragKind.ZoomMarquee:
                FinishZoomMarquee(_drag, e.GetPosition(_stage));
                _drag = null;
                e.Pointer.Capture(null);
                e.Handled = true;
                break;
            case DragKind.ShapeDraw:
                FinishShapeDraw(_drag);
                _drag = null;
                e.Pointer.Capture(null);
                e.Handled = true;
                break;
            case DragKind.Move:
                _drag = null;
                e.Pointer.Capture(null);
                _guideOverlay.Clear();
                Edited?.Invoke();
                e.Handled = true;
                break;
            case DragKind.Pan:
                _drag = null;
                e.Pointer.Capture(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>触摸点被系统取消捕获时清理对应的平移/捏合状态。</summary>
    private void StageOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        _touchPoints.Remove(e.Pointer);
        if (_drag is { Kind: DragKind.Pan } pan && ReferenceEquals(pan.Pointer, e.Pointer))
        {
            _drag = null;
            Refresh();
            _stage.InvalidateVisual();
        }

        if (_touchPoints.Count < 2)
        {
            _isPinching = false;
        }
    }

    /// <summary>触控板/鼠标滚轮平移；Ctrl + 滚轮按光标位置缩放。</summary>
    private void StageOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var factor = Math.Pow(1.15, e.Delta.Y);
            ZoomTo(e.GetPosition(_stage), _zoom * factor);
        }
        else
        {
            // 触控板的双指滚动会作为 PointerWheel 发送；同时处理水平和垂直分量。
            SetScrollOffset(_scrollViewer.Offset - new Vector(e.Delta.X * 48, e.Delta.Y * 48));
        }

        e.Handled = true;
    }

    /// <summary>开始双指捏合，固定两指中点以避免缩放时画面跳动。</summary>
    private void BeginPinch()
    {
        var pair = _touchPoints.Values.Take(2).ToArray();
        _pinchCenter = new Point((pair[0].X + pair[1].X) / 2, (pair[0].Y + pair[1].Y) / 2);
        _pinchStartDistance = Distance(pair[0], pair[1]);
        _pinchStartZoom = _zoom;
        _isPinching = _pinchStartDistance > 0.1;
        _drag = null;
    }

    /// <summary>按两指距离比例更新缩放。</summary>
    private void UpdatePinch()
    {
        if (!_isPinching || _touchPoints.Count < 2)
        {
            return;
        }

        var pair = _touchPoints.Values.Take(2).ToArray();
        var distance = Distance(pair[0], pair[1]);
        if (distance > 0.1)
        {
            ZoomTo(_pinchCenter, _pinchStartZoom * distance / _pinchStartDistance);
        }
    }

    /// <summary>手指拖动画布空白区时平移滚动视口。</summary>
    private void UpdatePan(DragState drag, Point pointer)
    {
        var delta = pointer - drag.StartPointer;
        SetScrollOffset(drag.StartScrollOffset - new Vector(delta.X * _zoom, delta.Y * _zoom));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // ============ 工具实现 ============

    /// <summary>移动工具按下：命中图层则选中并开始拖拽移动（Ctrl 多选、可整组拖动），否则取消选中。</summary>
    private void MoveToolPress(Point pos, PointerPressedEventArgs e)
    {
        var layer = HitTestLayer(pos);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (layer == null)
        {
            Select(null);
            return;
        }

        if (ctrl)
        {
            // Ctrl+点击：切换选中状态；若因此取消了选中则不进入拖拽。
            SelectWithToggle(layer.Id);
            if (!_selectedIds.Contains(layer.Id))
            {
                return;
            }
        }
        else if (!_selectedIds.Contains(layer.Id))
        {
            // 点击不在选中集 → 单选（若属于组则选中整组）；已在选中集 → 保持整组选中并整组拖动。
            SelectWithGroup(layer.Id);
        }

        if (_lockedIds.Contains(layer.Id) || layer.FullscreenExtend)
        {
            // 锁定 / 全屏扩展图层只允许选中，不进入拖拽（全屏图层固定铺满显示框架）。
            return;
        }

        // 拖动整组：参与移动的 = 选中图层 ∪ 同组成员（跳过锁定），
        // 并把「铺满岛屿」切为「自定义尺寸」，否则锚点偏移被忽略导致拖动无效。
        var moving = new List<WallpaperLayerItem>();
        foreach (var sel in SelectedLayers)
        {
            if (_lockedIds.Contains(sel.Id))
            {
                continue;
            }

            foreach (var m in GroupMembers(sel))
            {
                if (!_lockedIds.Contains(m.Id) && !moving.Contains(m))
                {
                    moving.Add(m);
                }
            }
        }

        foreach (var sel in moving)
        {
            if (sel.SizeMode == WallpaperLayerSizeMode.FillIsland)
            {
                sel.SizeMode = WallpaperLayerSizeMode.Custom;
                sel.Width = _islandWidth;
                sel.Height = _islandHeight;
            }
        }

        _drag = new DragState
        {
            Layer = layer,
            Kind = DragKind.Move,
            StartPointer = pos,
            StartRect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer)),
            StartIslandW = _islandWidth,
            StartIslandH = _islandHeight
        };
        EditStarted?.Invoke();
        e.Pointer.Capture(_stage);
        e.Handled = true;
    }

    /// <summary>选择工具按下：只选中图层（Ctrl 多选；普通点击若属于组则选中整组），不进入拖拽移动。</summary>
    private void SelectToolPress(Point pos, PointerPressedEventArgs e)
    {
        var layer = HitTestLayer(pos);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SelectWithToggle(layer?.Id);
        }
        else
        {
            SelectWithGroup(layer?.Id);
        }
    }

    /// <summary>抓手工具按下：开始平移画布视图（拖动不选中、不移动任何图层）。</summary>
    private void BeginHandPan(Point pos, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _drag = new DragState
        {
            Kind = DragKind.Pan,
            Pointer = e.Pointer,
            StartPointer = pos,
            StartScrollOffset = _scrollViewer.Offset
        };
        e.Pointer.Capture(_stage);
        e.Handled = true;
    }

    /// <summary>形状工具按下：在起点创建图层并开始拖拽绘制。</summary>
    private void BeginShapeDraw(Point pos, PointerPressedEventArgs e)
    {
        // 先压撤销再创建图层，保证「撤销」能移除刚绘制的形状。
        EditStarted?.Invoke();
        var layer = CreateShapeLayer(new Rect(pos.X, pos.Y, 0, 0));
        Select(layer.Id);
        _drag = new DragState { Kind = DragKind.ShapeDraw, Layer = layer, StartPointer = pos };
        e.Pointer.Capture(_stage);
        e.Handled = true;
    }

    /// <summary>形状工具拖拽：按起点到指针的矩形实时更新图层尺寸与位置。</summary>
    private void UpdateShapeDraw(DragState drag, Point pointer)
    {
        var layer = drag.Layer!;
        var rect = NormalizeRect(drag.StartPointer, pointer);
        layer.Width = Math.Max(1, rect.Width);
        layer.Height = Math.Max(1, rect.Height);
        ApplyRectOffsets(layer, ToIslandRect(rect));
        Refresh();
    }

    /// <summary>形状工具释放：过小则生成默认尺寸，随后切回移动工具。</summary>
    private void FinishShapeDraw(DragState drag)
    {
        var layer = drag.Layer!;
        if (layer.Width < MinLayerSize || layer.Height < MinLayerSize)
        {
            var rect = new Rect(drag.StartPointer.X - 60, drag.StartPointer.Y - 40, 120, 80);
            layer.Width = 120;
            layer.Height = 80;
            ApplyRectOffsets(layer, ToIslandRect(rect));
        }

        Refresh();
        SwitchTool(WallpaperEditorTool.Move);
        Edited?.Invoke();
    }

    /// <summary>文本工具：在点击处创建文本框图层，随后切回移动工具。</summary>
    private void PlaceText(Point pos)
    {
        var layer = new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"文本图层 {_layers.Count + 1}",
            Kind = WallpaperLayerKind.Text,
            Source = WallpaperSource.None,
            SizeMode = WallpaperLayerSizeMode.Custom,
            Text = "双击修改文本",
            TextFontSize = 16,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center,
            Width = 180,
            Height = 48
        };
        // 先压撤销再添加，保证「撤销」能移除刚创建的图层。
        EditStarted?.Invoke();
        ApplyRectOffsets(layer, ToIslandRect(new Rect(pos.X - 90, pos.Y - 24, 180, 48)));
        _layers.Add(layer);
        SyncImageControls();
        Select(layer.Id);
        Refresh();
        SwitchTool(WallpaperEditorTool.Move);
        Edited?.Invoke();
    }
    private WallpaperLayerItem CreateShapeLayer(Rect rect)
    {
        var layer = new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"形状图层 {_layers.Count + 1}",
            Kind = WallpaperLayerKind.Shape,
            ShapeType = _shapeToolType,
            Source = WallpaperSource.None,
            SizeMode = WallpaperLayerSizeMode.Custom,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center,
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height)
        };
        ApplyRectOffsets(layer, ToIslandRect(rect));
        _layers.Add(layer);
        // 新建的矢量图层没有位图加载流程，必须立即补入舞台视觉树；否则编辑器只
        // 会显示选中框，保存并由运行时重建后才会出现实际形状。
        SyncImageControls();
        return layer;
    }

    /// <summary>把舞台坐标矩形转换为岛屿坐标矩形（舞台原点 = 岛屿左上角 + CanvasMargin）。</summary>
    private static Rect ToIslandRect(Rect stageRect) =>
        new(stageRect.X - CanvasMargin, stageRect.Y - CanvasMargin, stageRect.Width, stageRect.Height);

    /// <summary>把矩形位置写回图层偏移（保持当前锚点不变）。</summary>
    private void ApplyRectOffsets(WallpaperLayerItem layer, Rect rect)
    {
        var (ox, oy) = WallpaperLayerLayout.ToOffsets(layer, rect, _islandWidth, _islandHeight);
        layer.OffsetX = ox;
        layer.OffsetY = oy;
    }

    // ============ 拖拽图片到画布 ============

    /// <summary>拖拽悬停：仅文件（图片）显示可放置。</summary>
    private void StageOnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>把拖入的图片文件添加为画布上的图片图层（位置 = 拖放点，尺寸按图片比例自适应）。</summary>
    private void StageOnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files))
        {
            return;
        }

        var file = e.Data.GetFiles()?.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // 先压撤销再添加，保证「撤销」能移除拖入的图层。
        EditStarted?.Invoke();
        AddDroppedImageLayer(path, e.GetPosition(_stage));
        e.Handled = true;
    }

    /// <summary>在指定舞台坐标处创建一张图片图层（锚点居中，初始尺寸按图片比例自适应）。</summary>
    private void AddDroppedImageLayer(string path, Point stagePos)
    {
        var layer = new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"底图图层 {_layers.Count + 1}",
            Source = WallpaperSource.LocalImage,
            Path = path,
            SizeMode = WallpaperLayerSizeMode.Custom,
            DisplayMode = WallpaperDisplayMode.Fill,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center
        };
        _layers.Add(layer);
        RefreshImages(); // 加载位图并同步舞台控件
        var aspect = AspectOf(layer);
        var w = aspect is > 0 ? _islandHeight * 0.8 * aspect.Value : _islandWidth * 0.6;
        var h = aspect is > 0 ? _islandHeight * 0.8 : _islandHeight * 0.6;
        layer.Width = w;
        layer.Height = h;
        // 锚点居中 + 偏移，使图层中心对准拖放点（岛屿坐标）。
        var islandPos = new Point(stagePos.X - CanvasMargin, stagePos.Y - CanvasMargin);
        ApplyRectOffsets(layer, new Rect(islandPos.X - w / 2, islandPos.Y - h / 2, w, h));
        SyncImageControls();
        Select(layer.Id);
        Refresh();
        Edited?.Invoke();
    }

    /// <summary>缩放工具释放：小矩形 = 单击（缩放/Alt 缩小），大矩形 = 框选放大到视图。</summary>
    private void FinishZoomMarquee(DragState drag, Point pointer)
    {
        _marqueeRect.IsVisible = false;
        var rect = NormalizeRect(drag.StartPointer, pointer);
        if (rect.Width < 8 && rect.Height < 8)
        {
            ZoomTo(drag.StartPointer, drag.ZoomOut ? _zoom / 1.25 : _zoom * 1.25);
        }
        else
        {
            ZoomToRect(rect);
        }
    }

    /// <summary>把逻辑坐标点保持在同一屏幕位置进行缩放（保持光标下的内容不跑）。</summary>
    private void ZoomTo(Point logicalPos, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, 0.4, 2.5);
        var oldZoom = _zoom;
        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        var offset = _scrollViewer.Offset;
        var ox = offset.X + logicalPos.X * (newZoom - oldZoom);
        var oy = offset.Y + logicalPos.Y * (newZoom - oldZoom);
        Zoom = newZoom;
        SetScrollOffset(new Vector(ox, oy));
    }

    /// <summary>把逻辑坐标矩形放大到铺满视图。</summary>
    private void ZoomToRect(Rect logicalRect)
    {
        if (logicalRect.Width < 8 || logicalRect.Height < 8)
        {
            return;
        }

        var viewport = _scrollViewer.Viewport;
        var scale = Math.Min(viewport.Width / logicalRect.Width, viewport.Height / logicalRect.Height);
        var newZoom = Math.Clamp(_zoom * scale, 0.4, 2.5);
        var center = new Point(logicalRect.X + logicalRect.Width / 2, logicalRect.Y + logicalRect.Height / 2);
        Zoom = newZoom;
        SetScrollOffset(new Vector(
            center.X * newZoom - viewport.Width / 2,
            center.Y * newZoom - viewport.Height / 2));
    }

    /// <summary>将滚动偏移限制在当前画布内容边界内。</summary>
    private void SetScrollOffset(Vector offset)
    {
        var maxX = Math.Max(0, _scrollViewer.Extent.Width - _scrollViewer.Viewport.Width);
        var maxY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        _scrollViewer.Offset = new Vector(Math.Clamp(offset.X, 0, maxX), Math.Clamp(offset.Y, 0, maxY));
    }

    /// <summary>把普通两点矩形归一化为左上 + 宽高的矩形。</summary>
    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private void PositionMarquee(Rect rect)
    {
        Canvas.SetLeft(_marqueeRect, rect.X);
        Canvas.SetTop(_marqueeRect, rect.Y);
        _marqueeRect.Width = rect.Width;
        _marqueeRect.Height = rect.Height;
    }

    // ============ 移动 ============

    private void UpdateMove(DragState drag, Point pointer)
    {
        var delta = pointer - drag.StartPointer;
        var rect = new Rect(drag.StartRect.X + delta.X, drag.StartRect.Y + delta.Y,
            drag.StartRect.Width, drag.StartRect.Height);
        var others = OtherLayerRects(drag.Layer!);
        rect = SnapRect(rect, others,
            true, true, true, true, true, true,
            out var guides, out var xIsland, out var yIsland);
        var layer = drag.Layer!;
        var (ox, oy) = WallpaperLayerLayout.ToOffsets(layer, rect, _islandWidth, _islandHeight);
        layer.OffsetX = ox;
        layer.OffsetY = oy;
        if (xIsland)
        {
            ApplyIslandSnapX(layer, rect);
        }

        if (yIsland)
        {
            ApplyIslandSnapY(layer, rect);
        }

        // 整组移动：其它选中 + 组内成员按相同位移同步移动（跳过锁定、去重，保持组内相对位置）。
        var handled = new HashSet<WallpaperLayerItem>();
        foreach (var other in SelectedLayers.Concat(GroupMembers(layer)))
        {
            if (other == layer || _lockedIds.Contains(other.Id) || !handled.Add(other))
            {
                continue;
            }

            var or = WallpaperLayerLayout.ComputeRect(other, _islandWidth, _islandHeight, AspectOf(other));
            var moved = new Rect(or.X + delta.X, or.Y + delta.Y, or.Width, or.Height);
            var (oox, ooy) = WallpaperLayerLayout.ToOffsets(other, moved, _islandWidth, _islandHeight);
            other.OffsetX = oox;
            other.OffsetY = ooy;
        }

        _guideOverlay.SetGuides(ToStageGuides(guides));
        Refresh();
    }

    // ============ 八向缩放 ============

    private void ResizeHandleOnPointerPressed(Border handle, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var layer = SelectedLayer;
        if (layer == null || _lockedIds.Contains(layer.Id))
        {
            return;
        }

        // 铺满岛屿的图层被拖动时自动切换为自定义尺寸（以当前岛屿大小为初始尺寸）。
        if (layer.SizeMode == WallpaperLayerSizeMode.FillIsland)
        {
            layer.SizeMode = WallpaperLayerSizeMode.Custom;
            layer.Width = _islandWidth;
            layer.Height = _islandHeight;
        }

        _drag = new DragState
        {
            Layer = layer,
            Kind = DragKind.Resize,
            HandleDir = _handleDirs[handle],
            StartPointer = e.GetPosition(_stage),
            StartRect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer)),
            StartIslandW = _islandWidth,
            StartIslandH = _islandHeight
        };
        EditStarted?.Invoke();
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void ResizeHandleOnPointerMoved(Border handle, PointerEventArgs e)
    {
        if (_drag is not { Kind: DragKind.Resize })
        {
            return;
        }

        var keepAspect = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        UpdateResize(_drag, e.GetPosition(_stage), keepAspect);
        e.Handled = true;
    }

    private void ResizeHandleOnPointerReleased(Border handle, PointerReleasedEventArgs e)
    {
        if (_drag is not { Kind: DragKind.Resize })
        {
            return;
        }

        _drag = null;
        e.Pointer.Capture(null);
        _guideOverlay.Clear();
        Edited?.Invoke();
        e.Handled = true;
    }

    private void UpdateResize(DragState drag, Point pointer, bool keepAspect)
    {
        var delta = pointer - drag.StartPointer;
        var r = drag.StartRect;
        var (dx, dy) = drag.HandleDir;
        var x = r.X;
        var y = r.Y;
        var w = r.Width;
        var h = r.Height;
        if (dx < 0)
        {
            x += delta.X;
            w -= delta.X;
        }
        else if (dx > 0)
        {
            w += delta.X;
        }

        if (dy < 0)
        {
            y += delta.Y;
            h -= delta.Y;
        }
        else if (dy > 0)
        {
            h += delta.Y;
        }

        if (keepAspect && (dx != 0 || dy != 0) && r.Width > 0 && r.Height > 0)
        {
            var aspect = r.Width / r.Height;
            if (dx != 0 && dy != 0)
            {
                if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
                {
                    h = w / aspect;
                }
                else
                {
                    w = h * aspect;
                }

                if (dy < 0)
                {
                    y = r.Bottom - h;
                }
            }
            else if (dx != 0)
            {
                h = w / aspect;
            }
            else
            {
                w = h * aspect;
                if (dx < 0)
                {
                    x = r.Right - w;
                }
            }
        }

        w = Math.Max(MinLayerSize, w);
        h = Math.Max(MinLayerSize, h);
        if (dx < 0)
        {
            x = Math.Min(x, r.Right - MinLayerSize);
        }

        if (dy < 0)
        {
            y = Math.Min(y, r.Bottom - MinLayerSize);
        }

        var rect = new Rect(x, y, w, h);
        var others = OtherLayerRects(drag.Layer!);
        // 只吸附被拖动的边 + 中心
        rect = SnapRect(rect, others,
            dx < 0, dx > 0, true, dy < 0, dy > 0, true,
            out var guides, out var xIsland, out var yIsland);
        var layer = drag.Layer!;
        layer.Width = rect.Width;
        layer.Height = rect.Height;
        layer.SizeMode = WallpaperLayerSizeMode.Custom;
        var (ox, oy) = WallpaperLayerLayout.ToOffsets(layer, rect, _islandWidth, _islandHeight);
        layer.OffsetX = ox;
        layer.OffsetY = oy;
        if (xIsland)
        {
            ApplyIslandSnapX(layer, rect);
        }

        if (yIsland)
        {
            ApplyIslandSnapY(layer, rect);
        }

        _guideOverlay.SetGuides(ToStageGuides(guides));
        Refresh();
    }

    // ============ 旋转 ============

    private void RotationHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_rotationHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var layer = SelectedLayer;
        if (layer == null || _lockedIds.Contains(layer.Id))
        {
            return;
        }

        if (layer.SizeMode == WallpaperLayerSizeMode.FillIsland)
        {
            layer.SizeMode = WallpaperLayerSizeMode.Custom;
            layer.Width = _islandWidth;
            layer.Height = _islandHeight;
        }

        _drag = new DragState
        {
            Layer = layer,
            Kind = DragKind.Rotate,
            StartPointer = e.GetPosition(_stage),
            StartRect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer)),
            StartRotation = layer.Rotation,
            StartIslandW = _islandWidth,
            StartIslandH = _islandHeight
        };
        EditStarted?.Invoke();
        e.Pointer.Capture(_rotationHandle);
        e.Handled = true;
    }

    private void RotationHandleOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { Kind: DragKind.Rotate })
        {
            return;
        }

        UpdateRotate(_drag, e.GetPosition(_stage));
        e.Handled = true;
    }

    private void RotationHandleOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { Kind: DragKind.Rotate })
        {
            return;
        }

        _drag = null;
        e.Pointer.Capture(null);
        Edited?.Invoke();
        e.Handled = true;
    }

    private void UpdateRotate(DragState drag, Point pointer)
    {
        var center = new Point(CanvasMargin + drag.StartRect.Center.X, CanvasMargin + drag.StartRect.Center.Y);
        var v0 = drag.StartPointer - center;
        var v1 = pointer - center;
        var baseAngle = Math.Atan2(v0.Y, v0.X) * 180 / Math.PI;
        var currentAngle = Math.Atan2(v1.Y, v1.X) * 180 / Math.PI;
        var angle = NormalizeAngle(drag.StartRotation + (currentAngle - baseAngle));
        // 吸附到 15° 倍（阈值 2.5°）
        var snapped = Math.Round(angle / 15.0) * 15.0;
        if (Math.Abs(snapped - angle) < 2.5)
        {
            angle = snapped;
        }

        drag.Layer!.Rotation = angle;
        Refresh();
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    // ============ 岛屿缩放（预览自适应）============

    private void IslandHandleOnPointerPressed(Border handle, PointerPressedEventArgs e)
    {
        if (!_islandUnlocked || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _drag = new DragState
        {
            Kind = DragKind.IslandResize,
            HandleDir = _islandHandleDirs[handle],
            StartPointer = e.GetPosition(_stage),
            StartIslandW = _islandWidth,
            StartIslandH = _islandHeight
        };
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void IslandHandleOnPointerMoved(Border handle, PointerEventArgs e)
    {
        if (_drag is not { Kind: DragKind.IslandResize })
        {
            return;
        }

        UpdateIslandResize(_drag, e.GetPosition(_stage));
        e.Handled = true;
    }

    private void IslandHandleOnPointerReleased(Border handle, PointerReleasedEventArgs e)
    {
        if (_drag is not { Kind: DragKind.IslandResize })
        {
            return;
        }

        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateIslandResize(DragState drag, Point pointer)
    {
        var delta = pointer - drag.StartPointer;
        var (dx, dy) = drag.HandleDir;
        var w = drag.StartIslandW + (dx > 0 ? delta.X : 0);
        var h = drag.StartIslandH + (dy > 0 ? delta.Y : 0);
        _islandWidth = Math.Clamp(w, 120, 1600);
        _islandHeight = Math.Clamp(h, 40, 500);
        UpdateStageSize();
        Refresh();
        IslandChanged?.Invoke();
    }

    // ============ 智能对齐标尺（PS 式吸附）============

    /// <summary>获取图层所在组的全部成员（未分组则仅自身）。</summary>
    private IEnumerable<WallpaperLayerItem> GroupMembers(WallpaperLayerItem layer) =>
        string.IsNullOrEmpty(layer.GroupId)
            ? [layer]
            : _layers.Where(l => l.GroupId == layer.GroupId);

    /// <summary>把选中的多个图层编为一组（同组图层可整组移动；不足 2 个或已同组时不操作）。</summary>
    public void GroupSelection()
    {
        var selected = SelectedLayers.Where(l => !_lockedIds.Contains(l.Id)).ToList();
        if (selected.Count < 2)
        {
            return;
        }

        if (selected.All(l => l.GroupId == selected[0].GroupId) && !string.IsNullOrEmpty(selected[0].GroupId))
        {
            return;
        }

        EditStarted?.Invoke();
        var groupId = Guid.NewGuid().ToString("N");
        foreach (var l in selected)
        {
            l.GroupId = groupId;
        }

        // 把组内成员在列表中排在一起（插到第一个选中位置），图层面板同组相邻。
        var firstIndex = selected.Min(l => _layers.IndexOf(l));
        foreach (var l in selected)
        {
            _layers.Remove(l);
        }

        _layers.InsertRange(firstIndex, selected);
        Refresh();
        Edited?.Invoke();
    }

    /// <summary>把选中图层从所在组中拆出（清空 GroupId）。</summary>
    public void UngroupSelection()
    {
        var selected = SelectedLayers.Where(l => !_lockedIds.Contains(l.Id)).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        EditStarted?.Invoke();
        foreach (var l in selected)
        {
            l.GroupId = string.Empty;
        }

        Refresh();
        Edited?.Invoke();
    }

    /// <summary>复制主选中的图层（新 Id、名字加「副本」、轻微偏移）；无选中返回 null。</summary>
    public WallpaperLayerItem? DuplicateSelection()
    {
        var layer = SelectedLayer;
        if (layer == null)
        {
            return null;
        }

        // 先压撤销再添加，保证「撤销」能移除副本。
        EditStarted?.Invoke();
        var clone = layer.Clone();
        clone.Id = Guid.NewGuid().ToString("N");
        clone.Name = layer.Name + " 副本";
        clone.OffsetX += 12;
        clone.OffsetY += 12;
        _layers.Add(clone);
        // 关键：必须走 RefreshImages 重新加载位图（新 Id 在 _bitmaps 中没有对应的图，
        // 只调 SyncImageControls 会导致复制的图片图层没有 Source 而不显示）。
        RefreshImages();
        Select(clone.Id);
        Edited?.Invoke();
        return clone;
    }

    /// <summary>把选中图层上移一层（z 序更靠前）；已在最前时返回 false。</summary>
    public bool MoveLayerUp()
    {
        var layer = SelectedLayer;
        if (layer == null)
        {
            return false;
        }

        var index = _layers.IndexOf(layer);
        if (index < 0 || index >= _layers.Count - 1)
        {
            return false;
        }

        EditStarted?.Invoke();
        _layers.RemoveAt(index);
        _layers.Insert(index + 1, layer);
        Refresh();
        Edited?.Invoke();
        return true;
    }

    /// <summary>把选中图层下移一层（z 序更靠后）；已在最底时返回 false。</summary>
    public bool MoveLayerDown()
    {
        var layer = SelectedLayer;
        if (layer == null)
        {
            return false;
        }

        var index = _layers.IndexOf(layer);
        if (index <= 0)
        {
            return false;
        }

        EditStarted?.Invoke();
        _layers.RemoveAt(index);
        _layers.Insert(index - 1, layer);
        Refresh();
        Edited?.Invoke();
        return true;
    }

    /// <summary>把选中的图层复制到内部剪贴板（Ctrl+C）。</summary>
    public void CopySelection()
    {
        var layer = SelectedLayer;
        if (layer == null)
        {
            return;
        }

        _copiedLayer = layer.Clone();
    }

    /// <summary>粘贴内部剪贴板中的图层（Ctrl+V；无复制内容时无操作）。</summary>
    public WallpaperLayerItem? PasteLayer()
    {
        if (_copiedLayer == null)
        {
            return null;
        }

        // 先压撤销再添加，保证「撤销」能移除粘贴的副本。
        EditStarted?.Invoke();
        var clone = _copiedLayer.Clone();
        clone.Id = Guid.NewGuid().ToString("N");
        clone.Name = clone.Name + " 副本";
        clone.OffsetX += 12;
        clone.OffsetY += 12;
        _layers.Add(clone);
        // 走 RefreshImages 重载位图，否则粘贴的图片图层没有 Source 而不显示。
        RefreshImages();
        Select(clone.Id);
        Edited?.Invoke();
        return clone;
    }

    private List<Rect> OtherLayerRects(WallpaperLayerItem exclude)
    {
        var result = new List<Rect>();
        foreach (var layer in _layers)
        {
            if (layer == exclude || !layer.Visible || layer.FullscreenExtend)
            {
                continue;
            }

            var rect = WallpaperLayerLayout.ComputeRect(layer, _islandWidth, _islandHeight, AspectOf(layer));
            if (rect.Width > 0 && rect.Height > 0)
            {
                result.Add(rect);
            }
        }

        return result;
    }

    /// <summary>
    /// 把矩形吸附到岛屿或其它图层的边/中心（匹配同类型参考点：左对左、中对中、右对右），
    /// 返回吸附后的矩形与参考线。参考线坐标位于岛屿坐标系，调用方负责转换为舞台坐标。
    /// </summary>
    private Rect SnapRect(Rect rect, List<Rect> others,
        bool useLeft, bool useRight, bool useCenterX,
        bool useTop, bool useBottom, bool useCenterY,
        out List<Guide> guides, out bool xIsland, out bool yIsland)
    {
        guides = [];
        xIsland = false;
        yIsland = false;

        var xRefs = new List<(double Pos, int Kind)>();
        var yRefs = new List<(double Pos, int Kind)>();
        if (useLeft)
        {
            xRefs.Add((rect.X, 0));
        }

        if (useCenterX)
        {
            xRefs.Add((rect.Center.X, 1));
        }

        if (useRight)
        {
            xRefs.Add((rect.Right, 2));
        }

        if (useTop)
        {
            yRefs.Add((rect.Y, 0));
        }

        if (useCenterY)
        {
            yRefs.Add((rect.Center.Y, 1));
        }

        if (useBottom)
        {
            yRefs.Add((rect.Bottom, 2));
        }

        var xTargets = new List<(double Pos, int Kind, bool Island)>
        {
            (0, 0, true),
            (_islandWidth / 2, 1, true),
            (_islandWidth, 2, true)
        };
        var yTargets = new List<(double Pos, int Kind, bool Island)>
        {
            (0, 0, true),
            (_islandHeight / 2, 1, true),
            (_islandHeight, 2, true)
        };
        foreach (var r in others)
        {
            xTargets.Add((r.X, 0, false));
            xTargets.Add((r.Center.X, 1, false));
            xTargets.Add((r.Right, 2, false));
            yTargets.Add((r.Y, 0, false));
            yTargets.Add((r.Center.Y, 1, false));
            yTargets.Add((r.Bottom, 2, false));
        }

        var bestX = FindBestSnap(xRefs, xTargets);
        var bestY = FindBestSnap(yRefs, yTargets);
        var result = rect;
        if (bestX is { } bx)
        {
            result = new Rect(result.X + bx.Shift, result.Y, result.Width, result.Height);
            xIsland = bx.Island;
            guides.Add(new Guide(true, bx.Target, SnapLabel(bx.Kind, bx.Island, true), bx.Kind == 1));
        }

        if (bestY is { } by)
        {
            result = new Rect(result.X, result.Y + by.Shift, result.Width, result.Height);
            yIsland = by.Island;
            guides.Add(new Guide(false, by.Target, SnapLabel(by.Kind, by.Island, false), by.Kind == 1));
        }

        return result;
    }

    private SnapResult? FindBestSnap(List<(double Pos, int Kind)> refs, List<(double Pos, int Kind, bool Island)> targets)
    {
        SnapResult? best = null;
        foreach (var (pos, kind) in refs)
        {
            foreach (var (targetPos, targetKind, island) in targets)
            {
                if (kind != targetKind)
                {
                    continue;
                }

                var shift = targetPos - pos;
                if (Math.Abs(shift) > SnapThreshold)
                {
                    continue;
                }

                if (best == null || Math.Abs(shift) < Math.Abs(best.Shift))
                {
                    best = new SnapResult(shift, targetPos, targetKind, island);
                }
            }
        }

        return best;
    }

    private static string SnapLabel(int kind, bool island, bool vertical)
    {
        var name = kind switch
        {
            0 => vertical ? "左对齐" : "顶对齐",
            1 => vertical ? "水平居中" : "垂直居中",
            _ => vertical ? "右对齐" : "底对齐"
        };
        return island ? name : $"{name} · 对齐图层";
    }

    /// <summary>吸附到岛屿左/右/水平中心时，把锚点切换为对应值并清零偏移（实现「右边缘 = 岛屿右边缘」）。</summary>
    private void ApplyIslandSnapX(WallpaperLayerItem layer, Rect rect)
    {
        const double eps = 0.5;
        if (Math.Abs(rect.X) < eps)
        {
            layer.AnchorX = WallpaperLayerAnchorX.Left;
            layer.OffsetX = 0;
        }
        else if (Math.Abs(rect.Right - _islandWidth) < eps)
        {
            layer.AnchorX = WallpaperLayerAnchorX.Right;
            layer.OffsetX = 0;
        }
        else if (Math.Abs(rect.Center.X - _islandWidth / 2) < eps)
        {
            layer.AnchorX = WallpaperLayerAnchorX.Center;
            layer.OffsetX = 0;
        }
    }

    private void ApplyIslandSnapY(WallpaperLayerItem layer, Rect rect)
    {
        const double eps = 0.5;
        if (Math.Abs(rect.Y) < eps)
        {
            layer.AnchorY = WallpaperLayerAnchorY.Top;
            layer.OffsetY = 0;
        }
        else if (Math.Abs(rect.Bottom - _islandHeight) < eps)
        {
            layer.AnchorY = WallpaperLayerAnchorY.Bottom;
            layer.OffsetY = 0;
        }
        else if (Math.Abs(rect.Center.Y - _islandHeight / 2) < eps)
        {
            layer.AnchorY = WallpaperLayerAnchorY.Center;
            layer.OffsetY = 0;
        }
    }

    private List<Guide> ToStageGuides(List<Guide> guides) =>
        guides.Select(g => g with { Position = g.Position + CanvasMargin }).ToList();

    // ============ 键盘 ============

    private void CanvasOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    ZoomAtViewportCenter(1.15);
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomAtViewportCenter(1 / 1.15);
                    e.Handled = true;
                    return;
                case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    UngroupSelection();
                    e.Handled = true;
                    return;
                case Key.G:
                    GroupSelection();
                    e.Handled = true;
                    return;
                case Key.J:
                    DuplicateSelection();
                    e.Handled = true;
                    return;
                case Key.C:
                    CopySelection();
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteLayer();
                    e.Handled = true;
                    return;
            }
        }

        var layer = SelectedLayer;
        switch (e.Key)
        {
            case Key.V:
                SwitchTool(WallpaperEditorTool.Move);
                e.Handled = true;
                break;
            case Key.S:
                SwitchTool(WallpaperEditorTool.Select);
                e.Handled = true;
                break;
            case Key.Z:
                SwitchTool(WallpaperEditorTool.Zoom);
                e.Handled = true;
                break;
            case Key.U:
                SwitchTool(WallpaperEditorTool.Shape);
                e.Handled = true;
                break;
            case Key.T:
                SwitchTool(WallpaperEditorTool.Text);
                e.Handled = true;
                break;
            case Key.H:
                SwitchTool(WallpaperEditorTool.Hand);
                e.Handled = true;
                break;
            case Key.Delete when layer != null && !_lockedIds.Contains(layer.Id):
                DeleteRequested?.Invoke(layer);
                e.Handled = true;
                break;
            case Key.Escape:
                _drag = null;
                _guideOverlay.Clear();
                Select(null);
                e.Handled = true;
                break;
            case Key.Left:
                Nudge(-1, 0, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Right:
                Nudge(1, 0, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Up:
                Nudge(0, -1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Down:
                Nudge(0, 1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
        }
    }

    /// <summary>以当前视口中心为锚点进行快捷键缩放。</summary>
    private void ZoomAtViewportCenter(double factor)
    {
        var viewport = _scrollViewer.Viewport;
        var offset = _scrollViewer.Offset;
        var center = new Point(
            (offset.X + viewport.Width / 2) / _zoom,
            (offset.Y + viewport.Height / 2) / _zoom);
        ZoomTo(center, _zoom * factor);
    }

    /// <summary>方向键微移全部选中图层（跳过锁定；Shift = 大步）。</summary>
    private void Nudge(double dx, double dy, bool large)
    {
        var layers = SelectedLayers.Where(l => !_lockedIds.Contains(l.Id)).ToList();
        if (layers.Count == 0)
        {
            return;
        }

        EditStarted?.Invoke();
        var step = large ? 10 : 1;
        foreach (var layer in layers)
        {
            layer.OffsetX += dx * step;
            layer.OffsetY += dy * step;
        }

        Refresh();
        Edited?.Invoke();
    }

    // ============ 内部类型 ============

    private enum DragKind
    {
        None,
        Move,
        Resize,
        Rotate,
        IslandResize,
        ZoomMarquee,
        ShapeDraw,
        Pan
    }

    private sealed class DragState
    {
        public WallpaperLayerItem? Layer;
        public DragKind Kind;
        public Point StartPointer;
        public IPointer? Pointer;
        public Vector StartScrollOffset;
        public Rect StartRect;
        public double StartIslandW;
        public double StartIslandH;
        public (int Dx, int Dy) HandleDir;
        public double StartRotation;
        /// <summary>缩放工具：单击时是否缩小（Alt / 右键）。</summary>
        public bool ZoomOut;
    }

    private sealed record Guide(bool Vertical, double Position, string Label, bool IsCenter);

    private sealed record SnapResult(double Shift, double Target, int Kind, bool Island);

    /// <summary>岛屿虚线边界（始终显示，帮助用户理解「锚点相对定位」的参照系）。</summary>
    private sealed class IslandOutlineOverlay : Control
    {
        public Rect IslandBounds { get; set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (IslandBounds.Width <= 0)
            {
                return;
            }
            // 占位，实际绘制在下方完整方法中

            var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 120, 190, 255)), 1)
            {
                DashStyle = new DashStyle([5, 4], 0)
            };
            var b = IslandBounds;
            context.DrawRectangle(pen, new Rect(b.X + 0.5, b.Y + 0.5, b.Width - 1, b.Height - 1), 0);
        }
    }

    /// <summary>选中图层的虚线框 + 旋转臂（PowerPoint 风格；多选时其它选中显示浅色虚线框）。</summary>
    private sealed class SelectionOverlay : Control
    {
        public Rect SelectionRect { get; set; }
        public List<Rect> SecondaryRects { get; } = [];
        public Point RotationStart { get; set; }
        public Point RotationEnd { get; set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            // 多选时其它选中的虚线框（浅色、无旋转臂）。
            var secondaryPen = new Pen(new SolidColorBrush(ThemePalette.AccentColorWithAlpha(160)), 1)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            foreach (var sr in SecondaryRects)
            {
                if (sr.Width <= 0 || sr.Height <= 0)
                {
                    continue;
                }

                context.DrawRectangle(secondaryPen, new Rect(sr.X + 0.5, sr.Y + 0.5, sr.Width - 1, sr.Height - 1), 0);
            }

            if (SelectionRect.Width <= 0 || SelectionRect.Height <= 0)
            {
                return;
            }

            var boxPen = new Pen(new SolidColorBrush(ThemePalette.AccentColor()), 1)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            var b = SelectionRect;
            context.DrawRectangle(boxPen, new Rect(b.X + 0.5, b.Y + 0.5, b.Width - 1, b.Height - 1), 0);
            var armPen = new Pen(new SolidColorBrush(Color.FromRgb(121, 80, 242)), 1)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            context.DrawLine(armPen, RotationStart, RotationEnd);
        }
    }

    /// <summary>智能对齐标尺：洋红色（边缘）/ 青色（中心）参考线 + 标签。</summary>
    private sealed class GuideOverlay : Control
    {
        private readonly List<Guide> _guides = [];

        public void SetGuides(List<Guide> guides)
        {
            _guides.Clear();
            _guides.AddRange(guides);
            InvalidateVisual();
        }

        public void Clear()
        {
            _guides.Clear();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            foreach (var guide in _guides)
            {
                var brush = new SolidColorBrush(guide.IsCenter ? Color.FromRgb(0, 200, 255) : Color.FromRgb(255, 61, 194));
                var pen = new Pen(brush, 1);
                if (guide.Vertical)
                {
                    context.DrawLine(pen, new Point(guide.Position, 0), new Point(guide.Position, Bounds.Height));
                }
                else
                {
                    context.DrawLine(pen, new Point(0, guide.Position), new Point(Bounds.Width, guide.Position));
                }

                var text = new FormattedText(guide.Label, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, Typeface.Default, 11, brush);
                var labelX = guide.Vertical
                    ? Math.Clamp(guide.Position + 6, 2, Math.Max(2, Bounds.Width - text.Width - 10))
                    : 2;
                var labelY = guide.Vertical
                    ? 2
                    : Math.Clamp(guide.Position + 6, 2, Math.Max(2, Bounds.Height - text.Height - 10));
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 24, 24, 28)), null,
                    new Rect(labelX, labelY, text.Width + 8, text.Height + 4), 0, 0, default);
                context.DrawText(text, new Point(labelX + 4, labelY + 2));
            }
        }
    }
}

/// <summary>
/// 九宫格锚点选择器（Photoshop / 游戏 UI 风格）：点击任意格点同时设置水平与垂直锚点。
/// </summary>
internal sealed class AnchorGridPicker : Control
{
    private const double Cell = 22;
    private const double Gap = 5;
    private const double Padding = 5;

    public WallpaperLayerAnchorX AnchorX { get; set; } = WallpaperLayerAnchorX.Center;
    public WallpaperLayerAnchorY AnchorY { get; set; } = WallpaperLayerAnchorY.Center;

    public event Action? Changed;

    public AnchorGridPicker()
    {
        Width = Padding * 2 + Cell * 3 + Gap * 2;
        Height = Padding * 2 + Cell * 3 + Gap * 2;
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var pos = e.GetPosition(this);
            var col = (int)((pos.X - Padding) / (Cell + Gap));
            var row = (int)((pos.Y - Padding) / (Cell + Gap));
            col = Math.Clamp(col, 0, 2);
            row = Math.Clamp(row, 0, 2);
            AnchorX = col switch { 0 => WallpaperLayerAnchorX.Left, 1 => WallpaperLayerAnchorX.Center, _ => WallpaperLayerAnchorX.Right };
            AnchorY = row switch { 0 => WallpaperLayerAnchorY.Top, 1 => WallpaperLayerAnchorY.Center, _ => WallpaperLayerAnchorY.Bottom };
            InvalidateVisual();
            Changed?.Invoke();
            e.Handled = true;
        };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var selectedRow = AnchorY switch { WallpaperLayerAnchorY.Top => 0, WallpaperLayerAnchorY.Center => 1, _ => 2 };
        var selectedCol = AnchorX switch { WallpaperLayerAnchorX.Left => 0, WallpaperLayerAnchorX.Center => 1, _ => 2 };
        var accent = new SolidColorBrush(ThemePalette.AccentColor());
        var idle = new SolidColorBrush(ThemePalette.IsDarkTheme()
            ? Color.FromArgb(150, 255, 255, 255)
            : Color.FromArgb(130, 0, 0, 0));
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var selected = r == selectedRow && c == selectedCol;
                var cx = Padding + c * (Cell + Gap) + Cell / 2;
                var cy = Padding + r * (Cell + Gap) + Cell / 2;
                context.DrawEllipse(selected ? accent : null, new Pen(selected ? accent : idle, 2), new Point(cx, cy), 4, 4);
            }
        }
    }
}
