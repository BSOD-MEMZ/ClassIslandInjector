using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// Photoshop 风格底图图层编辑器。
/// 概念：
/// - 画布 = 一层「ClassIsland 主界面」（默认锁定，解锁后可拖动边缘模拟主界面长度变化）
///   叠加任意数量的图片图层（锚点相对定位 + 像素偏移 + 尺寸 + 旋转）。
/// - 相对定位：图层矩形由「锚点（左/中/右 × 上/中/下）+ 偏移」表达，
///   因此 ClassIsland 主界面长度变化时底图按锚点自适应。
/// - 拖动/缩放时显示智能对齐标尺（PS 式洋红色/青色参考线），并自动吸附。
/// </summary>
internal sealed class WallpaperLayerEditorWindow : MyWindow
{
    private const double DefaultIslandWidth = 400;
    private const double DefaultIslandHeight = 90;

    private readonly WallpaperLayerCanvas _canvas = new();
    private readonly StackPanel _layerStack = new() { Spacing = 4 };
    private readonly TextBlock _statusText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.85,
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---- 检查器控件 ----
    private readonly TextBox _nameBox = new() { MaxWidth = 180 };
    private readonly Slider _opacitySlider = SliderControl(0, 1, 0.05);
    private readonly ComboBox _displayModeBox = new() { MinWidth = 120 };
    private readonly ComboBox _smtcModeBox = new() { MinWidth = 150 };
    private readonly ToggleSwitch _fillIslandToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly EditorSpin _widthSpin = new(1, 2000, 1, "0");
    private readonly EditorSpin _heightSpin = new(1, 2000, 1, "0");
    private readonly EditorSpin _rotationSpin = new(-360, 360, 1, "0");
    private readonly EditorSpin _offsetXSpin = new(-2000, 2000, 1, "0");
    private readonly EditorSpin _offsetYSpin = new(-2000, 2000, 1, "0");
    private readonly AnchorGridPicker _anchorPicker = new();
    /// <summary>相对位置说明。</summary>
    private readonly TextBlock _relativeHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        FontSize = 12
    };
    /// <summary>自定义尺寸的两行（铺满主界面关闭时显示）。</summary>
    private Control _widthItem = null!;
    private Control _heightItem = null!;
    /// <summary>SMTC 模式行（仅选中 SMTC 图层时显示）。</summary>
    private Control _smtcModeItem = null!;
    /// <summary>显示方式行（仅位图图层显示）。</summary>
    private Control _displayModeItem = null!;
    // 全屏扩展 / 九宫格切图（仅图片图层）
    private readonly ToggleSwitch _fullscreenToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ToggleSwitch _sliceToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly EditorSpin _sliceLeftSpin = new(0, 5000, 1, "0");
    private readonly EditorSpin _sliceTopSpin = new(0, 5000, 1, "0");
    private readonly EditorSpin _sliceRightSpin = new(0, 5000, 1, "0");
    private readonly EditorSpin _sliceBottomSpin = new(0, 5000, 1, "0");
    private readonly Button _editSliceButton = new() { Content = "编辑切图" };
    private Control _fullscreenItem = null!;
    private Control _sliceItem = null!;
    private Control _editSliceItem = null!;
    private Control _sliceLeftItem = null!;
    private Control _sliceTopItem = null!;
    private Control _sliceRightItem = null!;
    private Control _sliceBottomItem = null!;
    /// <summary>全屏扩展说明。</summary>
    private readonly TextBlock _fullscreenHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        FontSize = 12
    };
    // 效果（仅图片图层）：投影（高斯模糊 / 色相饱和度等改由顶部命令栏的滤镜窗口调整）
    private readonly ToggleSwitch _shadowToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly EditorSpin _shadowBlurSpin = new(0, 100, 0.5, "0.##");
    private readonly EditorSpin _shadowOffsetXSpin = new(-100, 100, 1, "0");
    private readonly EditorSpin _shadowOffsetYSpin = new(-100, 100, 1, "0");
    private readonly ColorPicker _shadowColorPicker = ColorPicker();
    private readonly Slider _shadowOpacitySlider = SliderControl(0, 1, 0.05);
    private Control _shadowItem = null!;
    private Control _shadowBlurItem = null!;
    private Control _shadowOffsetXItem = null!;
    private Control _shadowOffsetYItem = null!;
    private Control _shadowColorItem = null!;
    private Control _shadowOpacityItem = null!;
    // 画笔 / 橡皮擦设置（对应工具激活时显示）
    private readonly ColorPicker _brushColorPicker = ColorPicker();
    private readonly Slider _brushSizeSlider = SliderControl(1, 100, 1);
    private Control _brushColorItem = null!;
    private Control _brushSizeItem = null!;
    /// <summary>命令栏滤镜按钮（仅选中图片图层时可用）。</summary>
    private CommandBarButton _hslFilterButton = null!;
    private CommandBarButton _brightnessFilterButton = null!;
    private CommandBarButton _blurFilterButton = null!;
    // 形状图层检查器
    private readonly ComboBox _shapeTypeBox = new() { MinWidth = 140, Name = "EditorShapeType" };
    private readonly ColorPicker _shapeFillPicker = ColorPicker();
    private readonly ColorPicker _shapeStrokePicker = ColorPicker();
    private readonly EditorSpin _shapeStrokeSpin = new(0, 40, 0.25, "0.##");
    private readonly ToggleSwitch _shapeFillThemeToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ToggleSwitch _shapeStrokeThemeToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly EditorSpin _shapeCornerRadiusSpin = new(0, 100, 1, "0");
    private readonly EditorSpin _shapeStarPointsSpin = new(3, 16, 1, "0");
    private readonly EditorSpin _shapeStarInsetSpin = new(0.1, 0.95, 0.05, "0.##");
    private Control _shapeTypeItem = null!;
    private Control _shapeCornerRadiusItem = null!;
    private Control _shapeStarPointsItem = null!;
    private Control _shapeStarInsetItem = null!;
    private Control _shapeFillItem = null!;
    private Control _shapeStrokeItem = null!;
    private Control _shapeStrokeWidthItem = null!;
    private Control _shapeFillThemeItem = null!;
    private Control _shapeStrokeThemeItem = null!;
    // 文本图层检查器
    private readonly TextBox _textBox = new() { MaxWidth = 200, Watermark = "文本内容" };
    private readonly EditorSpin _textFontSizeSpin = new(6, 200, 1, "0");
    private readonly ComboBox _textFontFamilyBox = new() { MinWidth = 140, MaxDropDownHeight = 360 };
    private readonly ColorPicker _textColorPicker = ColorPicker();
    private readonly ToggleSwitch _textColorThemeToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ToggleSwitch _textBoldToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ToggleSwitch _textStrokeToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ColorPicker _textStrokeColorPicker = ColorPicker();
    private readonly EditorSpin _textStrokeThicknessSpin = new(0, 20, 0.25, "0.##");
    private readonly ToggleSwitch _textUseSmtcTitleToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ComboBox _textAlignBox = new() { MinWidth = 140 };
    private Control _textItem = null!;
    private Control _textFontSizeItem = null!;
    private Control _textFontFamilyItem = null!;
    private Control _textColorItem = null!;
    private Control _textColorThemeItem = null!;
    private Control _textStrokeItem = null!;
    private Control _textStrokeColorItem = null!;
    private Control _textStrokeThicknessItem = null!;
    private Control _textUseSmtcTitleItem = null!;
    private Control _textBoldItem = null!;
    private Control _textAlignItem = null!;

    // ---- 状态 ----
    private List<WallpaperLayerItem> _layers = [];
    private readonly List<List<WallpaperLayerItem>> _undoStack = [];
    private readonly List<List<WallpaperLayerItem>> _redoStack = [];
    private bool _updatingInspector;
    private bool _dirty;
    /// <summary>窗口内容根。</summary>
    private Grid? _contentGrid;
    /// <summary>舞台 + 右侧栏所在的主体网格（提升为字段以在窗口缩放时约束其高度）。</summary>
    private Grid _body = null!;
    /// <summary>命令栏撤销/重做按钮（按栈状态启停）。</summary>
    private CommandBarButton _undoButton = null!;
    private CommandBarButton _redoButton = null!;
    /// <summary>命令栏组合/取消组合按钮（按选中状态启停）。</summary>
    private CommandBarButton _groupButton = null!;
    private CommandBarButton _ungroupButton = null!;
    /// <summary>图层面板操作按钮（按选中状态启停；效果仅背景可用）。</summary>
    private Button _newLayerButton = null!;
    /// <summary>图层面板「新建空白图层」按钮（始终可用）。</summary>
    private Button _newBlankLayerButton = null!;
    private Button _duplicateButton = null!;
    private Button _deleteButton = null!;
    private Button _effectButton = null!;
    /// <summary>图层面板「栅格化」按钮（形状 / 文本图层选中时可用）。</summary>
    private Button _rasterizeButton = null!;
    /// <summary>拖拽排序：独立置顶的「幽灵快照」预览窗口（参考「主界面 → 组件」拖拽）。</summary>
    private Window? _dragPreviewWindow;
    private Border? _dragPreviewHost;
    /// <summary>拖拽排序：指针在源行内的抓取偏移（屏幕像素）。</summary>
    private Point _reorderGrabOffset;
    /// <summary>左侧工具栏按钮（按工具选中态更新）。</summary>
    private readonly Dictionary<WallpaperEditorTool, Button> _toolButtons = [];

    /// <summary>当前打开的编辑器实例。同一时刻只允许一个编辑器窗口（多开会造成关闭确认
    /// 的 ContentDialog 找不到 TopLevel 而崩溃）。</summary>
    public static WallpaperLayerEditorWindow? Current { get; private set; }

    public WallpaperLayerEditorWindow()
    {
        Title = "底图图层编辑器";
        Width = 1240;
        Height = 800;
        MinWidth = 980;
        MinHeight = 640;
        SystemDecorations = SystemDecorations.Full;
        // 插件窗口里 Mica 背景不可靠（会导致整窗半透明看不清），
        // 用主题感知的实色基底；侧栏和画布再使用独立表面，避免深色主题整窗一片灰。
        Background = ThemePalette.WindowBackground();

        _layers = InjectorRuntime.Settings.WallpaperLayers.Select(l => l.Clone()).ToList();
        var islandSize = InjectorRuntime.GetCurrentIslandSize();
        _canvas.SetIslandSize(islandSize?.Width > 0 ? islandSize.Value.Width : DefaultIslandWidth,
            islandSize?.Height > 0 ? islandSize.Value.Height : DefaultIslandHeight);
        _canvas.ZOrder = InjectorRuntime.Settings.WallpaperZOrder;
        _canvas.Layers = _layers;
        // 恢复上次吸管取到的默认色（新建形状 / 文本 / 画笔的默认色，自动记忆）。
        if (Color.TryParse(InjectorRuntime.Settings.EditorPickedColor, out var picked))
        {
            _canvas.ActiveColor = picked;
        }

        // 供宿主教程 TargetSelector 定位（#EditorCanvas）。
        _canvas.Name = "EditorCanvas";

        WireCanvas();
        BuildContent();
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
        // 编辑器级快捷键（撤销 / 重做、PS 式滤镜快捷键）；画布未处理的按键会冒泡到这里。
        KeyDown += EditorWindowOnKeyDown;
        Closing += OnClosingConfirm;
        // 单例跟踪：新窗口打开时覆盖 Current，关闭时若仍是本窗口则清空。
        Current = this;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };
        Opened += OnOpened;
    }

    /// <summary>窗口显示后触发未完成的「底图编辑器入门」教程（只播一次，完成后不再自动出现）。</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HostTutorial.BeginNotCompletedTutorials("classislandInjector.tutorials.wallpaperEditor/prologue");
        }, DispatcherPriority.Background);
    }

    /// <summary>按标签向前推动教程（仅当教程正停在该标签的等待句时生效）。</summary>
    private static void TutorialServicePush(string tag)
    {
        HostTutorial.PushToNextSentenceByTag(tag);
    }

    private void WireCanvas()
    {
        _canvas.EditStarted += () =>
        {
            PushUndo();
            _dirty = true;
        };
        _canvas.Edited += () =>
        {
            RefreshLayerList();
            RefreshInspector();
            UpdateStatus();
            // 推进教程的「拖动手柄调整位置」等待句（非该句时自动忽略）。
            TutorialServicePush("move");
        };
        _canvas.ShapeCreated += () => TutorialServicePush("shape");
        _canvas.TextCreated += () => TutorialServicePush("text");
        _canvas.SelectionChanged += () =>
        {
            RefreshLayerList();
            RefreshInspector();
            UpdateStatus();
            UpdateGroupButtons();
            UpdateLayerActionButtons();
            // 选中变化时同步各滤镜窗口的数值。
            SyncFilterWindows();
        };
        _canvas.IslandChanged += () =>
        {
            RefreshInspector();
            UpdateStatus();
            // 推进教程的「拖动主界面边缘」等待句（非该句时自动忽略）。
            TutorialServicePush("resize");
        };
        _canvas.ImagesChanged += RefreshLayerList;
        _canvas.DeleteRequested += DeleteLayer;
        _canvas.RasterizeRequested += RasterizeSelected;
        _canvas.ColorPicked += OnColorPicked;
        _canvas.ColorPreview += OnColorPreview;
        _canvas.ToolChanged += _ =>
        {
            UpdateToolBarSelection();
            // 切换工具后刷新检查器，显示 / 隐藏「画笔」设置。
            RefreshInspector();
        };
    }

    private void BuildContent()
    {
        // ---- 顶部命令栏（参考 ClassIsland 档案编辑窗口的 CommandBar）----
        // 层级不再用下拉框：改为直接拖拽图层面板里的「背景图层」行调整（顶部 = 底色之后，底部 = 底色之上）。
        _undoButton = CommandButton("\uE195", "撤销", "撤销上一步操作", Undo);
        _redoButton = CommandButton("\uE121", "重做", "重做已撤销的操作", Redo);
        _undoButton.IsEnabled = false;
        _redoButton.IsEnabled = false;
        _groupButton = CommandButton("\uE92F", "组合", "把选中的多个图层编为一组（Ctrl+G），之后拖动任一组内图层即可整组移动", _canvas.GroupSelection);
        _ungroupButton = CommandButton("\uE931", "取消组合", "把选中图层从所在组中拆出（Ctrl+Shift+G）", _canvas.UngroupSelection);
        _groupButton.IsEnabled = false;
        _ungroupButton.IsEnabled = false;
        var addImageButton = CommandButton("\uE9B4", "添加图片图层", "选择一张图片作为新的底图图层", AddImageLayer);
        // 供教程 TargetSelector 定位（#EditorAddImage）。
        addImageButton.Name = "EditorAddImage";
        var saveButton = CommandButton("\uEEB5", "保存并应用", "保存图层并应用到主界面", Save);
        // 供教程 TargetSelector 定位（#EditorSave）。
        saveButton.Name = "EditorSave";
        _hslFilterButton = CommandButton("\uE51E", "色相 / 饱和度", "打开滤镜窗口：逐像素调整选中图片图层的色相、饱和度与明度（含滤镜预设）", OpenHslAdjustWindow);
        _brightnessFilterButton = CommandButton("\uE2BC", "亮度 / 对比度", "打开滤镜窗口：逐像素调整选中图片图层的亮度与对比度（含滤镜预设）", OpenBrightnessContrastWindow);
        _blurFilterButton = CommandButton("\uE20B", "高斯模糊", "打开滤镜窗口：调整选中图片图层的高斯模糊半径", OpenBlurAdjustWindow);
        var commandBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            PrimaryCommands =
            {
                addImageButton,
                new CommandBarSeparator(),
                _undoButton,
                _redoButton,
                new CommandBarSeparator(),
                _groupButton,
                _ungroupButton,
                new CommandBarSeparator(),
                CommandButton("\uE62F", "重置主界面尺寸", "把主界面预览尺寸恢复为 ClassIsland 实际尺寸", ResetIslandSize),
                CommandButton("\uE92A", "棋盘格配色", "设置画布背景棋盘格：跟随主题自动按深浅色选择，或自定义两种颜色", OpenCheckerboardSettings),
                new CommandBarSeparator(),
                _hslFilterButton,
                _brightnessFilterButton,
                _blurFilterButton,
                saveButton
            }
        };

        // ---- 右侧：上 = 属性检查器（可滚动），下 = 图层面板（固定在底部），
        // 两区之间用水平手柄分割高度。----
        var layerListHost = new Grid { Children = { _layerStack, _reorderIndicator } };
        var inspector = BuildInspector();
        // 上：属性检查器面板（无边框，仅用背景区分）。
        var inspectorPanel = new Border
        {
            Background = ThemePalette.PanelBackground(),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = inspector
            }
        };
        // 供教程 TargetSelector 定位（#EditorInspector）。
        inspectorPanel.Name = "EditorInspector";
        // 下：图层面板（固定在底部，无边框）：图层列表（可滚动）+ 底部固定操作按钮行。
        var layerActions = BuildLayerActions();
        var layerPanel = new Border
        {
            Background = ThemePalette.PanelBackground(),
            Padding = new Thickness(12),
            MaxHeight = 340,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children =
                {
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = layerListHost
                    },
                    layerActions
                }
            }
        };
        // 供教程 TargetSelector 定位（#EditorLayerPanel）。
        layerPanel.Name = "EditorLayerPanel";
        Grid.SetRow(layerActions, 1);
        // 上下区之间的水平分割手柄（高度与横向间隙一致）。
        var rowSplitter = new GridSplitter
        {
            Height = 8,
            ResizeDirection = GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        // 两个区域 + 中间手柄。
        var rightColumn = new Grid
        {
            RowDefinitions = new RowDefinitions("*,8,Auto"),
            Children = { inspectorPanel, rowSplitter, layerPanel }
        };
        Grid.SetRow(rowSplitter, 1);
        Grid.SetRow(layerPanel, 2);

        // 左侧工具栏（Photoshop 式）+ 舞台 + 右侧设置区之间加垂直分割手柄，可左右拖动调整宽度。
        var toolbar = new Border
        {
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemePalette.PanelBackground(),
            Child = BuildToolBar()
        };
        // 供教程 TargetSelector 定位（#EditorToolBar）。
        toolbar.Name = "EditorToolBar";
        var stageHost = new Border
        {
            ClipToBounds = true,
            Background = ThemePalette.PanelBackground(),
            Child = _canvas
        };
        var columnSplitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,330"),
            Children = { toolbar, stageHost, columnSplitter, rightColumn }
        };
        Grid.SetColumn(toolbar, 0);
        Grid.SetColumn(stageHost, 1);
        Grid.SetColumn(columnSplitter, 2);
        Grid.SetColumn(rightColumn, 3);
        // 统一左右间隙为 8px：左侧 = 工具栏右 margin；右侧 = 舞台右 margin(2) + 分割手柄(6)。
        toolbar.Margin = new Thickness(0, 0, 8, 0);
        stageHost.Margin = new Thickness(0, 0, 2, 0);

        _body = body;
        _contentGrid = new Grid
        {
            Margin = new Thickness(12, 4, 12, 12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
            Children = { commandBar, body }
        };
        Grid.SetRow(body, 1);
        // 舞台与右侧栏共用同一个 Grid 行（天然等高）；这里再限制 body 高度不超过
        // 可视区，避免右侧栏内容过高把行撑出窗口导致两侧底部被裁、看起来高度不一。
        _contentGrid.SizeChanged += (_, _) => ConstrainBodyHeight();
        Content = _contentGrid;
        UpdateToolBarSelection();
        UpdateGroupButtons();
        UpdateLayerActionButtons();
    }

    /// <summary>把 body（舞台 + 右侧栏）高度限制在当前可视区内，保证两侧严格等高且都在窗口内。</summary>
    private void ConstrainBodyHeight()
    {
        if (_contentGrid == null || _body == null)
        {
            return;
        }

        var barRow = _contentGrid.RowDefinitions.Count > 0 ? _contentGrid.RowDefinitions[0] : null;
        var barHeight = barRow?.ActualHeight > 0 ? barRow.ActualHeight : 52;
        // RowSpacing(6) + Margin 上 4 下 12
        var available = _contentGrid.Bounds.Height - barHeight - 6 - 16;
        _body.MaxHeight = Math.Max(160, available);
    }

    // ============ 左侧工具栏（Photoshop 式）============

    /// <summary>图层面板底部的操作按钮行（纯图标 + 提示，固定在面板底边）。</summary>
    private Control BuildLayerActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _newBlankLayerButton = LayerIconButton("\uE010", "新建空白图层（透明，可用画笔 / 橡皮擦绘制）", AddBlankLayer);
        _newLayerButton = LayerIconButton("\uE9B4", "新建图层（选择一张图片）", AddImageLayer);
        _duplicateButton = LayerIconButton("\uE58B", "复制图层（Ctrl+J）", () => _canvas.DuplicateSelection());
        _deleteButton = LayerIconButton("\uE61D", "删除图层", () =>
        {
            var layer = _canvas.SelectedLayer;
            if (layer != null)
            {
                DeleteLayer(layer);
            }
        });
        _effectButton = LayerIconButton("\uF42F", "效果选项（只作用于 ClassIsland 背景，需先点击背景行）", OpenBackgroundEffects);
        _rasterizeButton = LayerIconButton("\uE928", "栅格化图层（Ctrl+Shift+R）：把选中的形状 / 文本图层渲染成位图，之后当作图片图层处理（不可再编辑矢量）", RasterizeSelected);
        panel.Children.Add(_newBlankLayerButton);
        panel.Children.Add(_newLayerButton);
        panel.Children.Add(_duplicateButton);
        panel.Children.Add(_deleteButton);
        panel.Children.Add(_rasterizeButton);
        panel.Children.Add(_effectButton);
        return panel;
    }

    /// <summary>图层面板操作按钮（纯图标，提示文字放 ToolTip）。</summary>
    private static Button LayerIconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = glyph, Text = string.Empty },
            Padding = new Thickness(8, 5)
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>按当前选中状态同步图层面板操作按钮：复制/删除仅选中图层可用；栅格化仅选中形状/文本图层可用；效果仅背景（无选中）可用。命令栏滤镜按钮仅选中图片图层时可用。</summary>
    private void UpdateLayerActionButtons()
    {
        var hasSelection = _canvas.SelectedLayer != null;
        var hasVector = _canvas.SelectedLayers.Any(l => l.Kind != WallpaperLayerKind.Image);
        var hasImageLayer = _canvas.SelectedLayers.Any(l => l.Kind == WallpaperLayerKind.Image);
        _duplicateButton.IsEnabled = hasSelection;
        _deleteButton.IsEnabled = hasSelection;
        _rasterizeButton.IsEnabled = hasVector;
        _effectButton.IsEnabled = !hasSelection;
        _hslFilterButton.IsEnabled = hasImageLayer;
        _brightnessFilterButton.IsEnabled = hasImageLayer;
        _blurFilterButton.IsEnabled = hasImageLayer;
    }

    /// <summary>
    /// 新建空白（透明）图片图层：尺寸与主界面一致，导出为 PNG 后作为本地图片图层，
    /// 可用画笔 / 橡皮擦在其上绘制。
    /// </summary>
    private void AddBlankLayer()
    {
        // 位图按显示器缩放（DPI）创建，保证画出来的笔迹在屏幕上 1:1 清晰，
        // 不会因为「位图分辨率 = DIP 尺寸」而被系统放大变糊。
        var dpr = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = (int)Math.Max(1, Math.Round(_canvas.IslandWidth * dpr));
        var h = (int)Math.Max(1, Math.Round(_canvas.IslandHeight * dpr));
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(InjectorRuntime.ConfigDirectory, "layers");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.png");
        try
        {
            // WriteableBitmap 默认清零（全透明）。
            using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96));
            using var fs = File.Create(path);
            bmp.Save(fs);
        }
        catch
        {
            return;
        }

        var layer = new WallpaperLayerItem
        {
            Id = id,
            Name = $"空白图层 {_layers.Count + 1}",
            Kind = WallpaperLayerKind.Image,
            Source = WallpaperSource.LocalImage,
            Path = path,
            DisplayMode = WallpaperDisplayMode.Stretch,
            SizeMode = WallpaperLayerSizeMode.Custom,
            // 图层显示尺寸保持 DIP（与主界面一致），位图分辨率更高（dpr 倍），
            // 画笔按 BrushRadiusFor 换算后屏幕大小不变、笔迹更清晰。
            Width = _canvas.IslandWidth,
            Height = _canvas.IslandHeight,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center
        };
        PushUndo();
        _layers.Add(layer);
        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(layer.Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>
    /// 栅格化选中的形状 / 文本图层：把矢量内容渲染成 PNG 位图，转为图片图层
    /// （保留尺寸 / 位置 / 旋转等变换，此后按位图处理，不能再编辑矢量）。
    /// 首次弹出警告（可勾选「不再提示」）。
    /// </summary>
    private async void RasterizeSelected()
    {
        var layers = _canvas.SelectedLayers.Where(l => l.Kind != WallpaperLayerKind.Image).ToList();
        if (layers.Count == 0)
        {
            return;
        }

        if (!InjectorRuntime.Settings.RasterizeWarningDismissed)
        {
            var dismiss = new CheckBox { Content = "以后不再提示" };
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "栅格化图层",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"栅格化后将被渲染成位图，从此当作图片图层处理，不能再编辑矢量。" },
                        dismiss
                    }
                },
                PrimaryButtonText = "栅格化",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await dialog.ShowAsync(topLevel);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            if (dismiss.IsChecked == true)
            {
                var settings = InjectorRuntime.Settings;
                settings.BeginUpdate();
                settings.RasterizeWarningDismissed = true;
                settings.EndUpdate();
                InjectorRuntime.SaveAndApply();
            }
        }

        PushUndo();
        foreach (var layer in layers)
        {
            RasterizeLayer(layer);
        }

        _dirty = true;
        // 触发 RefreshImages + SyncImageControls，按新 Path 加载位图。
        _canvas.Layers = _layers;
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>把单个矢量图层渲染成 PNG 并转为图片图层；失败时保持原图层不变。</summary>
    private void RasterizeLayer(WallpaperLayerItem layer)
    {
        var w = layer.Width > 0 ? layer.Width : _canvas.IslandWidth;
        var h = layer.Height > 0 ? layer.Height : _canvas.IslandHeight;
        if (w < 1 || h < 1)
        {
            return;
        }

        try
        {
            var visual = new WallpaperLayerVisual { Layer = layer, Width = w, Height = h };
            visual.Measure(new Size(w, h));
            visual.Arrange(new Rect(0, 0, w, h));
            using var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w), (int)Math.Ceiling(h)));
            rtb.Render(visual);
            var dir = Path.Combine(InjectorRuntime.ConfigDirectory, "rasterized");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{layer.Id}.png");
            using (var fs = File.Create(path))
            {
                rtb.Save(fs);
            }

            layer.Kind = WallpaperLayerKind.Image;
            layer.Source = WallpaperSource.LocalImage;
            layer.Path = path;
            layer.DisplayMode = WallpaperDisplayMode.Stretch;
            layer.SmtcMode = WallpaperLayerSmtcMode.AsImage;
        }
        catch
        {
            // 栅格化失败（渲染 / 写文件异常）时保持原矢量图层不变。
        }
    }

    /// <summary>打开背景效果窗口（单例；已打开则聚焦）。</summary>
    private void OpenBackgroundEffects()
    {
        if (BackgroundEffectsWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        new BackgroundEffectsWindow().Show();
    }

    // ============ 图层滤镜窗口（色相/饱和度、亮度/对比度、高斯模糊）============

    /// <summary>当前选中图层中第一个图片图层（滤镜窗口读取 / 同步用；无则 null）。</summary>
    internal WallpaperLayerItem? FirstSelectedImageLayer =>
        _canvas.SelectedLayers.FirstOrDefault(l => l.Kind == WallpaperLayerKind.Image);

    /// <summary>当前选中的全部图片图层。</summary>
    internal IEnumerable<WallpaperLayerItem> SelectedImageLayers =>
        _canvas.SelectedLayers.Where(l => l.Kind == WallpaperLayerKind.Image);

    /// <summary>滤镜窗口预览改动前压一次撤销（整次会话只压一次，由窗口跟踪）。</summary>
    internal void PushLayerFilterUndo() => PushUndo();

    /// <summary>把滤镜预览应用到选中的图片图层并刷新画布（标记脏，等待确定才最终提交）。</summary>
    internal void ApplyLayerFilter(Action apply)
    {
        apply();
        _dirty = true;
        _canvas.Refresh();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>滤镜「确定」后：标记脏并刷新（值已由窗口写入图层）。</summary>
    internal void CommitLayerFilter()
    {
        _dirty = true;
        _canvas.Refresh();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>滤镜「取消 / 关闭恢复快照」后：只刷新画布，不标记脏。</summary>
    internal void RefreshAfterLayerFilter()
    {
        _canvas.Refresh();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>同步所有已打开的滤镜窗口（选中变化时调用）。</summary>
    private void SyncFilterWindows()
    {
        HslAdjustWindow.Current?.SyncFromEditor();
        BrightnessContrastWindow.Current?.SyncFromEditor();
        BlurAdjustWindow.Current?.SyncFromEditor();
    }

    /// <summary>打开色相 / 饱和度窗口（单例；已打开则聚焦）。</summary>
    private void OpenHslAdjustWindow()
    {
        if (HslAdjustWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        new HslAdjustWindow(this).Show();
    }

    /// <summary>打开亮度 / 对比度窗口（单例；已打开则聚焦）。</summary>
    private void OpenBrightnessContrastWindow()
    {
        if (BrightnessContrastWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        new BrightnessContrastWindow(this).Show();
    }

    /// <summary>打开高斯模糊窗口（单例；已打开则聚焦）。</summary>
    private void OpenBlurAdjustWindow()
    {
        if (BlurAdjustWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        new BlurAdjustWindow(this).Show();
    }

    /// <summary>窗口级快捷键：撤销 / 重做与 PS 式滤镜快捷键（画布未处理的键冒泡到这里）。</summary>
    private void EditorWindowOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // Ctrl+Shift+Z：重做（Photoshop 同款）。
                Redo();
                e.Handled = true;
                break;
            case Key.Z:
                // Ctrl+Z：撤销。
                Undo();
                e.Handled = true;
                break;
            case Key.Y:
                // Ctrl+Y：重做。
                Redo();
                e.Handled = true;
                break;
            case Key.U:
                // Ctrl+U：色相 / 饱和度（Photoshop 同款）。
                OpenHslAdjustWindow();
                e.Handled = true;
                break;
            case Key.M:
                // Ctrl+M：亮度 / 对比度（Photoshop 的曲线 / 色调调整的实用替代）。
                OpenBrightnessContrastWindow();
                e.Handled = true;
                break;
        }
    }

    /// <summary>吸管最终取色：更新状态栏，并把取到的颜色设为默认色并自动记忆。</summary>
    private void OnColorPicked(Color color)
    {
        _statusText.Text = $"已取色 RGB({color.R}, {color.G}, {color.B})  {color.ToString()}";
        RememberActiveColor(color);
    }

    /// <summary>吸管悬停预览：状态栏实时汇报 RGB。</summary>
    private void OnColorPreview(Color color)
    {
        _statusText.Text = $"RGB {color.R}, {color.G}, {color.B}  {color.ToString()}";
    }

    /// <summary>
    /// 把用户最后使用的颜色设为默认色并自动记忆（新建形状 / 文本 / 画笔的默认色）。
    /// 直接写设置属性即可：Changed 事件会自动触发保存应用。
    /// </summary>
    private void RememberActiveColor(Color color)
    {
        _canvas.ActiveColor = color;
        InjectorRuntime.Settings.EditorPickedColor = color.ToString();
    }

    /// <summary>
    /// 打开「画布棋盘格配色」对话框：跟随主题自动按深浅色选择，或自定义两种颜色；
    /// 改动实时写回设置并刷新画布。
    /// </summary>
    private async void OpenCheckerboardSettings()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        var settings = InjectorRuntime.Settings;
        var followTheme = new ToggleSwitch
        {
            OnContent = "开",
            OffContent = "关",
            IsChecked = settings.WallpaperCheckerFollowTheme
        };
        var color1 = ColorPicker();
        color1.Color = ReadColor(settings.WallpaperCheckerColor1, Color.FromRgb(45, 47, 52));
        var color2 = ColorPicker();
        color2.Color = ReadColor(settings.WallpaperCheckerColor2, Color.FromRgb(38, 40, 45));
        void SyncColors() => color1.IsEnabled = color2.IsEnabled = followTheme.IsChecked != true;
        followTheme.PropertyChanged += (_, _) => SyncColors();
        SyncColors();

        // 改动实时写回设置并刷新画布棋盘格（EndUpdate 会触发 Changed → 保存应用）。
        void Apply()
        {
            settings.BeginUpdate();
            settings.WallpaperCheckerFollowTheme = followTheme.IsChecked == true;
            settings.WallpaperCheckerColor1 = color1.Color.ToString();
            settings.WallpaperCheckerColor2 = color2.Color.ToString();
            settings.EndUpdate();
            _canvas.ApplyCheckerboardColors();
        }

        followTheme.PropertyChanged += (_, _) => Apply();
        color1.PropertyChanged += (_, e) => { if (e.Property?.Name == "Color") Apply(); };
        color2.PropertyChanged += (_, e) => { if (e.Property?.Name == "Color") Apply(); };

        var panel = new StackPanel
        {
            Spacing = 4,
            Width = 360,
            Children =
            {
                SettingsRow("跟随主题", followTheme),
                SettingsRow("棋盘格颜色 1", color1),
                SettingsRow("棋盘格颜色 2", color2)
            }
        };
        var dialog = new ContentDialog
        {
            Title = "画布棋盘格配色",
            Content = panel,
            PrimaryButtonText = "完成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync(topLevel);
    }

    /// <summary>打开九宫格切图编辑器（在图片上框选切边，实时预览拉伸效果）。</summary>
    private void OpenSliceEditor()
    {
        var layer = _canvas.SelectedLayer;
        if (layer == null || _canvas.GetThumbnail(layer.Id) is not { } bitmap)
        {
            return;
        }

        new SliceEditorWindow(layer, bitmap).Show();
    }

    /// <summary>构建左侧纵向工具栏：移动 / 选择 / 缩放 / 形状 / 文本。</summary>
    private Control BuildToolBar()
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(ToolButton(WallpaperEditorTool.Move, "\uE113", "移动工具（V）：拖拽图层移动，拖动空白取消选中"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Hand, "\uE941", "抓手工具（H）：按住拖动平移画布，查看画布任意区域"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Select, "\uE5BF", "选择工具（S）：点击只选中图层，不拖拽"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Zoom, "\uF4D1", "缩放工具（Z）：单击放大 / Alt+单击缩小 / 拖拽框选放大；Ctrl + / Ctrl - 也可缩放"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Crop, "\uE59B", "裁剪工具（C）：在图片图层上拖拽框选要保留的区域，松手即裁剪（裁剪后切回移动工具）"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Eyedropper, "\uE81D", "吸管工具（I）：拾取屏幕上任意位置的颜色，按住拖拽可在窗口外取色；取到的颜色会成为新建形状 / 文本 / 画笔的默认色并自动记忆"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Brush, "\uEC4A", "画笔工具（B）：在图片图层上按住拖动绘制（右侧可调颜色 / 大小）"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Eraser, "\uE7FF", "橡皮擦工具（E）：擦除图片图层的像素（变为透明）"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Shape, "\uE775", "形状工具（U）：拖拽绘制矩形；创建后可在右侧修改形状类型"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Text, "\uF1BE", "文本工具（T）：点击插入文本框图层"));
        panel.Children.Add(new Separator { Margin = new Thickness(2, 5) });
        panel.Children.Add(ToolActionButton("\uEBCA", "添加 SMTC 图层", "把当前播放的专辑封面作为新的底图图层（无播放时显示占位封面）", AddSmtcLayer));
        panel.Children.Add(ToolActionButton("\uE7DC", "添加贴纸", "在线获取 Project Sekai 角色贴纸，插入为新的底图图层", OpenStickerPicker));
        return panel;
    }

    private Button ToolButton(WallpaperEditorTool tool, string glyph, string tip)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = glyph, Text = string.Empty },
            Padding = new Thickness(9, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        // 供教程 TargetSelector 定位形状工具按钮（#EditorShapeTool）。
        if (tool == WallpaperEditorTool.Shape)
        {
            button.Name = "EditorShapeTool";
        }
        // 供教程 TargetSelector 定位文本工具按钮（#EditorTextTool）。
        if (tool == WallpaperEditorTool.Text)
        {
            button.Name = "EditorTextTool";
        }

        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            // MenuFlyout 在宿主的 FluentAvalonia 2.4.1 中会在 PointerExited 时抛空引用，
            // 因此此处不再弹出菜单。先直接绘制矩形，创建后可在检查器切换其它形状。
            _canvas.Tool = tool;
        };
        _toolButtons[tool] = button;
        return button;
    }

    /// <summary>工具栏中的一次性命令按钮，不改变当前编辑工具。</summary>
    private static Button ToolActionButton(string glyph, string label, string tip, Action action)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = glyph, Text = string.Empty },
            Padding = new Thickness(9, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(ThemePalette.ForegroundColor())
        };
        ToolTip.SetTip(button, $"{label}：{tip}");
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>按当前工具刷新工具栏按钮的选中态（强调色底 + 白色图标；选中用实心图标，未选中用空心图标）。</summary>
    private void UpdateToolBarSelection()
    {
        foreach (var (tool, button) in _toolButtons)
        {
            var active = tool == _canvas.Tool;
            button.Background = active
                ? ThemeBrush("AccentFillColorDefaultBrush") ?? new SolidColorBrush(ThemePalette.AccentColorWithAlpha(150))
                : Brushes.Transparent;
            button.Foreground = active
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(ThemePalette.ForegroundColor());
            // 选中工具显示实心图标，未选中显示空心（regular）图标。
            if (button.Content is IconText icon && ToolGlyphs.TryGetValue(tool, out var glyphs))
            {
                icon.Glyph = active ? glyphs.Filled : glyphs.Regular;
            }
        }
    }

    /// <summary>左侧工具栏各工具的实心/空心图标码点（FluentSystemIcons，filled/regular 成对）。</summary>
    private static readonly Dictionary<WallpaperEditorTool, (string Filled, string Regular)> ToolGlyphs = new()
    {
        [WallpaperEditorTool.Move] = ("\uE112", "\uE113"),
        [WallpaperEditorTool.Hand] = ("\uE940", "\uE941"),
        [WallpaperEditorTool.Select] = ("\uE5BE", "\uE5BF"),
        [WallpaperEditorTool.Zoom] = ("\uF4D0", "\uF4D1"),
        [WallpaperEditorTool.Shape] = ("\uE774", "\uE775"),
        [WallpaperEditorTool.Text] = ("\uF1BD", "\uF1BE"),
        [WallpaperEditorTool.Crop] = ("\uE59A", "\uE59B"),
        [WallpaperEditorTool.Brush] = ("\uEC49", "\uEC4A"),
        [WallpaperEditorTool.Eraser] = ("\uE7FE", "\uE7FF"),
        [WallpaperEditorTool.Eyedropper] = ("\uE81C", "\uE81D")
    };

    private StackPanel BuildInspector()
    {
        _displayModeBox.ItemsSource = DisplayModeChoices;
        _displayModeBox.SelectedItem = DisplayModeChoices[0];
        _smtcModeBox.ItemsSource = SmtcModeChoices;
        _smtcModeBox.SelectedItem = SmtcModeChoices[0];
        _shapeTypeBox.ItemsSource = ShapeTypeChoices;
        _shapeTypeBox.SelectedItem = ShapeTypeChoices[0];
        _textAlignBox.ItemsSource = TextAlignChoices;
        _textAlignBox.SelectedItem = TextAlignChoices[1];
        var fonts = FontManager.Current.SystemFonts
            .Append(FontFamily.Default)
            .DistinctBy(font => font.Name)
            .OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _textFontFamilyBox.ItemsSource = fonts;
        _textFontFamilyBox.ItemTemplate = new FuncDataTemplate<FontFamily>((font, _) =>
        {
            // 虚拟化回收 ComboBox 项时模板可能短暂收到 null，不能直接读取 Name。
            if (font == null)
            {
                return new TextBlock { Height = 24 };
            }

            return new TextBlock
            {
                Text = font.Name,
                FontFamily = font,
                Width = 220,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        });
        _textFontFamilyBox.SelectedItem = FontFamily.Default;
        _fillIslandToggle.IsChecked = true;

        _nameBox.TextChanged += (_, _) => ApplyToSelected(l => l.Name = _nameBox.Text ?? "底图图层");
        _opacitySlider.ValueChanged += (_, _) => ApplyToSelected(l => l.Opacity = _opacitySlider.Value);
        _displayModeBox.SelectionChanged += (_, _) => ApplyToSelected(l => l.DisplayMode = Selected(_displayModeBox, WallpaperDisplayMode.Fill));
        _fullscreenToggle.PropertyChanged += async (_, e) =>
        {
            if (_updatingInspector || e.Property != ToggleSwitch.IsCheckedProperty)
            {
                return;
            }

            if (_fullscreenToggle.IsChecked == true)
            {
                // 实验性功能：启用前弹窗警告。
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "实验性功能警告",
                        Content = "「扩展到整个显示框架」是实验性功能：启用后该图片会铺满整个 ClassIsland 主界面，并临时隐藏底色、边框与阴影。\n\n若图片比例与主界面不一致，请务必开启「九宫格切图」并对图片进行切图，防止拉伸变形。确定要启用吗？",
                        PrimaryButtonText = "我已知晓并启用",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close
                    };
                    var result = await dialog.ShowAsync(topLevel);
                    if (result != ContentDialogResult.Primary)
                    {
                        _fullscreenToggle.IsChecked = false;
                        return;
                    }
                }

                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.FullscreenExtend = true; });
            }
            else
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.FullscreenExtend = false; });
            }

            // 全屏扩展会切换画布渲染控件（普通 Image ↔ 九宫格），重新赋列表触发控件重建。
            _canvas.Layers = _layers;
        };
        _sliceToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                ApplyToSelected(l =>
                {
                    if (l.Kind == WallpaperLayerKind.Image)
                    {
                        l.SliceEnabled = _sliceToggle.IsChecked == true;
                    }
                });
            }
        };
        _sliceLeftSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.SliceLeft = _sliceLeftSpin.DoubleValue); };
        _sliceTopSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.SliceTop = _sliceTopSpin.DoubleValue); };
        _sliceRightSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.SliceRight = _sliceRightSpin.DoubleValue); };
        _sliceBottomSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.SliceBottom = _sliceBottomSpin.DoubleValue); };
        _editSliceButton.Click += (_, _) => OpenSliceEditor();
        _shadowToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowEnabled = _shadowToggle.IsChecked == true; });
            }
        };
        _shadowBlurSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowBlurRadius = _shadowBlurSpin.DoubleValue; });
            }
        };
        _shadowOffsetXSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowOffsetX = _shadowOffsetXSpin.DoubleValue; });
            }
        };
        _shadowOffsetYSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowOffsetY = _shadowOffsetYSpin.DoubleValue; });
            }
        };
        _shadowColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowColor = _shadowColorPicker.Color.ToString(); });
            }
        };
        _shadowOpacitySlider.ValueChanged += (_, _) => ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Image) l.ShadowOpacity = _shadowOpacitySlider.Value; });
        _brushColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                RememberActiveColor(_brushColorPicker.Color);
            }
        };
        _brushSizeSlider.ValueChanged += (_, _) =>
        {
            if (!_updatingInspector)
            {
                _canvas.BrushSize = _brushSizeSlider.Value;
            }
        };
        _shapeTypeBox.SelectionChanged += (_, _) =>
        {
            // 刷新检查器时的程序化选择不应触发应用或教程推进。
            if (_updatingInspector)
            {
                return;
            }

            ApplyToSelected(l =>
            {
                if (l.Kind == WallpaperLayerKind.Shape)
                {
                    l.ShapeType = Selected(_shapeTypeBox, WallpaperShapeType.Rectangle);
                }
            });
            // 推进教程的「选择形状类型」等待句（非该句时自动忽略）。
            TutorialServicePush("shape-type");
        };
        _shapeFillPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.FillColor = _shapeFillPicker.Color.ToString(); });
                // 手动换的填充色也会成为「记忆颜色」（新建形状 / 文本 / 画笔的默认色）。
                RememberActiveColor(_shapeFillPicker.Color);
            }
        };
        _shapeStrokePicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.StrokeColor = _shapeStrokePicker.Color.ToString(); });
            }
        };
        _shapeStrokeSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.StrokeThickness = _shapeStrokeSpin.DoubleValue; });
            }
        };
        _shapeFillThemeToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                var on = _shapeFillThemeToggle.IsChecked == true;
                ApplyToSelected(l =>
                {
                    if (l.Kind == WallpaperLayerKind.Shape)
                    {
                        l.FillUsesThemeColor = on;
                    }
                });
            }
        };
        _shapeStrokeThemeToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                var on = _shapeStrokeThemeToggle.IsChecked == true;
                ApplyToSelected(l =>
                {
                    if (l.Kind == WallpaperLayerKind.Shape)
                    {
                        l.StrokeUsesThemeColor = on;
                    }
                });
            }
        };
        _shapeCornerRadiusSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.ShapeCornerRadius = _shapeCornerRadiusSpin.DoubleValue; });
            }
        };
        _shapeStarPointsSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.ShapeStarPoints = (int)_shapeStarPointsSpin.DoubleValue; });
            }
        };
        _shapeStarInsetSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.ShapeStarInset = _shapeStarInsetSpin.DoubleValue; });
            }
        };
        _textBox.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == TextBox.TextProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.Text = _textBox.Text ?? string.Empty; });
            }
        };
        _textFontSizeSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextFontSize = _textFontSizeSpin.DoubleValue; });
            }
        };
        _textFontFamilyBox.SelectionChanged += (_, _) =>
        {
            if (!_updatingInspector && _textFontFamilyBox.SelectedItem is FontFamily font)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextFontFamily = font.Name; });
            }
        };
        _textColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l =>
                {
                    if (l.Kind == WallpaperLayerKind.Text)
                    {
                        l.TextColor = _textColorPicker.Color.ToString();
                    }
                });
                // 手动换的文字颜色也会成为「记忆颜色」（新建形状 / 文本 / 画笔的默认色）。
                RememberActiveColor(_textColorPicker.Color);
            }
        };
        _textColorThemeToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                var on = _textColorThemeToggle.IsChecked == true;
                ApplyToSelected(l =>
                {
                    if (l.Kind == WallpaperLayerKind.Text)
                    {
                        l.TextUsesThemeColor = on;
                    }
                });
            }
        };
        _textStrokeToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextStrokeEnabled = _textStrokeToggle.IsChecked == true; });
            }
        };
        _textStrokeColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextStrokeColor = _textStrokeColorPicker.Color.ToString(); });
            }
        };
        _textStrokeThicknessSpin.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextStrokeThickness = _textStrokeThicknessSpin.DoubleValue; });
            }
        };
        _textUseSmtcTitleToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextUseSmtcTitle = _textUseSmtcTitleToggle.IsChecked == true; });
            }
        };
        _textBoldToggle.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextBold = _textBoldToggle.IsChecked == true; });
            }
        };
        _textAlignBox.SelectionChanged += (_, _) => ApplyToSelected(l =>
        {
            if (l.Kind == WallpaperLayerKind.Text)
            {
                l.TextAlign = Selected(_textAlignBox, WallpaperTextAlign.Center);
            }
        });
        _smtcModeBox.SelectionChanged += (_, _) => ApplyToSelected(l =>
        {
            l.SmtcMode = Selected(_smtcModeBox, WallpaperLayerSmtcMode.AsImage);
            // 默认处理：强制铺满主界面（不可自定义尺寸/位移）。
            if (l.SmtcMode == WallpaperLayerSmtcMode.Default)
            {
                l.SizeMode = WallpaperLayerSizeMode.FillIsland;
            }
        });
        _fillIslandToggle.PropertyChanged += (_, e) =>
        {
            if (_updatingInspector || e.Property != ToggleSwitch.IsCheckedProperty)
            {
                return;
            }

            ApplyToSelected(l =>
            {
                l.SizeMode = _fillIslandToggle.IsChecked == true ? WallpaperLayerSizeMode.FillIsland : WallpaperLayerSizeMode.Custom;
                if (l.SizeMode == WallpaperLayerSizeMode.Custom && (l.Width <= 0 || l.Height <= 0))
                {
                    l.Width = _canvas.IslandWidth;
                    l.Height = _canvas.IslandHeight;
                }
            });
            RefreshCustomSizePanel();
        };
        _widthSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.Width = _widthSpin.DoubleValue); };
        _heightSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.Height = _heightSpin.DoubleValue); };
        _rotationSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.Rotation = _rotationSpin.DoubleValue); };
        _offsetXSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.OffsetX = _offsetXSpin.DoubleValue); };
        _offsetYSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ApplyToSelected(l => l.OffsetY = _offsetYSpin.DoubleValue); };
        _anchorPicker.Changed += () => ApplyToSelected(l =>
        {
            l.AnchorX = _anchorPicker.AnchorX;
            l.AnchorY = _anchorPicker.AnchorY;
        });

        // 设置项平铺：小标题分组，不包裹卡片（参考弃用可视化编辑器的检查器罗列方式：
        // 分组小标题 + 每行「标签 | 控件」，统一行高、统一间距，视觉更整齐）。
        var inspector = new StackPanel { Spacing = 8 };
        inspector.Children.Add(new TextBlock
        {
            Text = "检查器",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        inspector.Children.Add(GroupSubtitle("\uE9B2", "图层"));
        inspector.Children.Add(SettingsRow("名称", _nameBox));
        inspector.Children.Add(GroupSubtitle("\uEC4A", "外观"));
        _smtcModeItem = SettingsRow("SMTC 模式", _smtcModeBox);
        inspector.Children.Add(_smtcModeItem);
        inspector.Children.Add(SettingsRow("不透明度", _opacitySlider));
        _displayModeItem = SettingsRow("显示方式", _displayModeBox);
        inspector.Children.Add(_displayModeItem);
        // 全屏扩展 + 九宫格切图（仅图片图层）
        _fullscreenItem = SettingsRow("扩展到整个显示框架", _fullscreenToggle);
        _sliceItem = SettingsRow("启用九宫格切图", _sliceToggle);
        _editSliceItem = SettingsRow("切图编辑", _editSliceButton);
        _sliceLeftItem = SettingsRow("左切边 (px)", _sliceLeftSpin);
        _sliceTopItem = SettingsRow("上切边 (px)", _sliceTopSpin);
        _sliceRightItem = SettingsRow("右切边 (px)", _sliceRightSpin);
        _sliceBottomItem = SettingsRow("下切边 (px)", _sliceBottomSpin);
        inspector.Children.Add(_fullscreenItem);
        inspector.Children.Add(_sliceItem);
        inspector.Children.Add(_editSliceItem);
        inspector.Children.Add(_sliceLeftItem);
        inspector.Children.Add(_sliceTopItem);
        inspector.Children.Add(_sliceRightItem);
        inspector.Children.Add(_sliceBottomItem);
        inspector.Children.Add(_fullscreenHint);
        // 效果（仅图片图层）：投影（高斯模糊 / 色相饱和度 / 亮度对比度改由顶部命令栏的滤镜窗口调整）
        inspector.Children.Add(GroupSubtitle("\uF42F", "效果"));
        _shadowItem = SettingsRow("投影", _shadowToggle);
        _shadowBlurItem = SettingsRow("投影模糊", _shadowBlurSpin);
        _shadowOffsetXItem = SettingsRow("投影水平偏移", _shadowOffsetXSpin);
        _shadowOffsetYItem = SettingsRow("投影垂直偏移", _shadowOffsetYSpin);
        _shadowColorItem = SettingsRow("投影颜色", _shadowColorPicker);
        _shadowOpacityItem = SettingsRow("投影不透明度", _shadowOpacitySlider);
        inspector.Children.Add(_shadowItem);
        inspector.Children.Add(_shadowBlurItem);
        inspector.Children.Add(_shadowOffsetXItem);
        inspector.Children.Add(_shadowOffsetYItem);
        inspector.Children.Add(_shadowColorItem);
        inspector.Children.Add(_shadowOpacityItem);
        // 画笔 / 橡皮擦设置（对应工具激活时显示）
        _brushColorItem = SettingsRow("画笔颜色", _brushColorPicker);
        _brushSizeItem = SettingsRow("画笔大小", _brushSizeSlider);
        inspector.Children.Add(GroupSubtitle("\uEC49", "画笔"));
        inspector.Children.Add(_brushColorItem);
        inspector.Children.Add(_brushSizeItem);
        // 形状图层专属（仅选中形状图层时显示）
        _shapeTypeItem = SettingsRow("形状类型", _shapeTypeBox);
        _shapeCornerRadiusItem = SettingsRow("圆角半径", _shapeCornerRadiusSpin);
        _shapeStarPointsItem = SettingsRow("星角数", _shapeStarPointsSpin);
        _shapeStarInsetItem = SettingsRow("内凹比例", _shapeStarInsetSpin);
        _shapeFillItem = SettingsRow("填充色", _shapeFillPicker);
        _shapeFillThemeItem = SettingsRow("填充色跟随主题", _shapeFillThemeToggle);
        _shapeStrokeItem = SettingsRow("描边色", _shapeStrokePicker);
        _shapeStrokeThemeItem = SettingsRow("描边色跟随主题", _shapeStrokeThemeToggle);
        _shapeStrokeWidthItem = SettingsRow("描边粗细", _shapeStrokeSpin);
        inspector.Children.Add(_shapeTypeItem);
        inspector.Children.Add(_shapeCornerRadiusItem);
        inspector.Children.Add(_shapeStarPointsItem);
        inspector.Children.Add(_shapeStarInsetItem);
        inspector.Children.Add(_shapeFillItem);
        inspector.Children.Add(_shapeFillThemeItem);
        inspector.Children.Add(_shapeStrokeItem);
        inspector.Children.Add(_shapeStrokeThemeItem);
        inspector.Children.Add(_shapeStrokeWidthItem);
        // 文本图层专属（仅选中文本图层时显示）
        _textItem = SettingsRow("文本内容", _textBox);
        _textFontSizeItem = SettingsRow("字号", _textFontSizeSpin);
        _textFontFamilyItem = SettingsRow("字体", _textFontFamilyBox);
        _textColorItem = SettingsRow("文字颜色", _textColorPicker);
        _textColorThemeItem = SettingsRow("文字颜色跟随主题", _textColorThemeToggle);
        _textStrokeItem = SettingsRow("文字描边", _textStrokeToggle);
        _textStrokeColorItem = SettingsRow("描边颜色", _textStrokeColorPicker);
        _textStrokeThicknessItem = SettingsRow("描边粗细", _textStrokeThicknessSpin);
        _textUseSmtcTitleItem = SettingsRow("显示为媒体标题", _textUseSmtcTitleToggle);
        _textBoldItem = SettingsRow("加粗", _textBoldToggle);
        _textAlignItem = SettingsRow("水平对齐", _textAlignBox);
        inspector.Children.Add(_textItem);
        inspector.Children.Add(_textFontSizeItem);
        inspector.Children.Add(_textFontFamilyItem);
        inspector.Children.Add(_textColorItem);
        inspector.Children.Add(_textColorThemeItem);
        inspector.Children.Add(_textStrokeItem);
        inspector.Children.Add(_textStrokeColorItem);
        inspector.Children.Add(_textStrokeThicknessItem);
        inspector.Children.Add(_textUseSmtcTitleItem);
        inspector.Children.Add(_textBoldItem);
        inspector.Children.Add(_textAlignItem);
        _widthItem = SettingsRow("宽度 (px)", _widthSpin);
        _heightItem = SettingsRow("高度 (px)", _heightSpin);
        inspector.Children.Add(GroupSubtitle("\uE27E", "尺寸"));
        inspector.Children.Add(SettingsRow("铺满主界面", _fillIslandToggle));
        inspector.Children.Add(_widthItem);
        inspector.Children.Add(_heightItem);
        inspector.Children.Add(GroupSubtitle("\uEEA5", "旋转"));
        inspector.Children.Add(SettingsRow("角度 (°)", _rotationSpin));
        inspector.Children.Add(GroupSubtitle("\uE113", "相对定位"));
        inspector.Children.Add(SettingsRow("锚点", _anchorPicker));
        inspector.Children.Add(SettingsRow("水平偏移 (px)", _offsetXSpin));
        inspector.Children.Add(SettingsRow("垂直偏移 (px)", _offsetYSpin));
        inspector.Children.Add(_relativeHint);
        inspector.Children.Add(SettingsRow("重置变换", Button("重置变换", ResetLayerTransform)));
        return inspector;
    }

    /// <summary>设置分组小标题（不包裹卡片）。</summary>
    private static IconText GroupSubtitle(string glyph, string text) => new()
    {
        Glyph = glyph,
        Text = text,
        Margin = new Thickness(0, 10, 0, 2),
        Opacity = 0.85
    };

    /// <summary>设置项行：左侧标签 + 右侧控件（平铺，无卡片；统一行高保证视觉对齐）。</summary>
    private static Control SettingsRow(string label, Control footer)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 32,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9,
                    Margin = new Thickness(0, 0, 10, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                },
                footer
            }
        };
        Grid.SetColumn(footer, 1);
        return row;
    }

    private void ResetLayerTransform()
    {
        ApplyToSelected(l =>
        {
            l.SizeMode = WallpaperLayerSizeMode.FillIsland;
            l.Rotation = 0;
            l.Opacity = 1;
            l.AnchorX = WallpaperLayerAnchorX.Center;
            l.AnchorY = WallpaperLayerAnchorY.Center;
            l.OffsetX = 0;
            l.OffsetY = 0;
            l.Width = 0;
            l.Height = 0;
        });
        UpdateStatus();
    }

    /// <summary>对全部选中图层应用修改：压入撤销、置脏、刷新画布与检查器。</summary>
    private void ApplyToSelected(Action<WallpaperLayerItem> edit)
    {
        var layers = _canvas.SelectedLayers;
        if (layers.Count == 0 || _updatingInspector)
        {
            return;
        }

        PushUndo();
        foreach (var layer in layers)
        {
            edit(layer);
        }

        _dirty = true;
        _canvas.Refresh();
        RefreshInspector();
        UpdateStatus();
    }

    // ============ 撤销 / 重做 / 保存 ============

    private void PushUndo()
    {
        _undoStack.Add(_layers.Select(l => l.Clone()).ToList());
        if (_undoStack.Count > 100)
        {
            _undoStack.RemoveAt(0);
        }

        _redoStack.Clear();
        UpdateUndoRedoState();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Add(_layers.Select(l => l.Clone()).ToList());
        _layers = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _canvas.Layers = _layers;
        _dirty = true;
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
        UpdateUndoRedoState();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Add(_layers.Select(l => l.Clone()).ToList());
        _layers = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _canvas.Layers = _layers;
        _dirty = true;
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
        UpdateUndoRedoState();
    }

    private void Save()
    {
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        settings.WallpaperDesignerEnabled = true;
        settings.WallpaperEnabled = true;
        settings.WallpaperZOrder = _canvas.ZOrder;
        settings.WallpaperLayers = _layers.Select(l => l.Clone()).ToList();
        settings.EndUpdate();
        InjectorRuntime.SaveAndApply();
        _dirty = false;
        _statusText.Text = $"已保存并应用：共 {_layers.Count} 个图片图层 · 层级「{DisplayZOrder(_canvas.ZOrder)}」。";
        UpdateUndoRedoState();
        // 向前推动教程的「保存」等待句。
        TutorialServicePush("save");
    }

    /// <summary>按撤销/重做栈同步命令栏按钮状态。</summary>
    private void UpdateUndoRedoState()
    {
        if (_undoButton == null)
        {
            return;
        }

        _undoButton.IsEnabled = _undoStack.Count > 0;
        _redoButton.IsEnabled = _redoStack.Count > 0;
    }

    /// <summary>按当前选中状态同步「组合 / 取消组合」按钮：单个或无选中 → 都禁用；
    /// 多选且未同组 → 仅「组合」；多选且同组 → 仅「取消组合」。</summary>
    private void UpdateGroupButtons()
    {
        var selected = _canvas.SelectedLayers.Where(l => !_canvas.IsLocked(l.Id)).ToList();
        var sameGroup = selected.Count >= 2 &&
                        !string.IsNullOrEmpty(selected[0].GroupId) &&
                        selected.All(l => l.GroupId == selected[0].GroupId);
        _groupButton.IsEnabled = selected.Count >= 2 && !sameGroup;
        _ungroupButton.IsEnabled = sameGroup;
    }

    // ============ 添加图层 / 主界面重置 ============

    private async void AddImageLayer()
    {
        // 仅在教学引导（教程正停在「添加图片」句）时询问图片来源（从文件 / 示例图）；
        // 平时与以前一样，直接打开文件选择器。
        if (HostTutorial.GetCurrentSentenceTag() == "add-image")
        {
            await AskImageSourceAsync();
            return;
        }

        await PickImageFromFileAsync(TopLevel.GetTopLevel(this));
    }

    /// <summary>教学引导中：让用户选择图片来源（从文件选择 / 使用示例图片 / 取消）。</summary>
    private async Task AskImageSourceAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return;
        }

        // 让用户明确选择图片来源：从文件选择，或使用内置示例图片（取消则不添加）。
        var dialog = new ContentDialog
        {
            Title = "添加图片图层",
            Content = "选择一张图片文件作为新的底图图层，或使用一张内置的示例图片。",
            PrimaryButtonText = "从文件选择",
            SecondaryButtonText = "使用示例图片",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync(topLevel);
        switch (result)
        {
            case ContentDialogResult.Primary:
                await PickImageFromFileAsync(topLevel);
                break;
            case ContentDialogResult.Secondary:
                AddLayerFromPath(Path.Combine(InjectorRuntime.PluginDirectory, "Assets", "editorbackground.jpg"));
                break;
            // None（取消）：不添加。
        }
    }

    /// <summary>打开系统文件选择器挑选底图图片；取消则不添加。</summary>
    private async Task PickImageFromFileAsync(TopLevel? topLevel)
    {
        if (topLevel?.StorageProvider is not { } provider)
        {
            return;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择底图图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] },
                FilePickerFileTypes.All
            ]
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            AddLayerFromPath(path);
        }
    }

    /// <summary>按本地路径创建一张图片图层并选中（添加成功后会推进教程的 add-image 句）。</summary>
    private void AddLayerFromPath(string path)
    {
        PushUndo();
        _layers.Add(new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"底图图层 {_layers.Count + 1}",
            Source = WallpaperSource.LocalImage,
            Path = path,
            SizeMode = WallpaperLayerSizeMode.FillIsland,
            DisplayMode = WallpaperDisplayMode.Fill
        });
        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(_layers[^1].Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
        // 向前推动教程的「添加图片」等待句。
        TutorialServicePush("add-image");
    }

    /// <summary>添加一个 SMTC 专辑封面图层（无播放时画布显示占位封面 album.jpg）。</summary>
    private void AddSmtcLayer()
    {
        PushUndo();
        _layers.Add(new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"SMTC 封面图层 {_layers.Count + 1}",
            Source = WallpaperSource.SmtcAlbum,
            SmtcMode = WallpaperLayerSmtcMode.AsImage,
            SizeMode = WallpaperLayerSizeMode.FillIsland,
            DisplayMode = WallpaperDisplayMode.Fill
        });
        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(_layers[^1].Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>打开在线贴纸选择窗口（单例；已打开则聚焦）。</summary>
    private void OpenStickerPicker()
    {
        if (StickerPickerWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        new StickerPickerWindow(AddStickerLayer).Show();
    }

    /// <summary>把下载到本地缓存的贴纸插入为新的图片图层（按贴纸比例自动设定初始尺寸）。</summary>
    private void AddStickerLayer(string path, string name)
    {
        PushUndo();
        var layer = new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Source = WallpaperSource.LocalImage,
            Path = path,
            SizeMode = WallpaperLayerSizeMode.Custom,
            DisplayMode = WallpaperDisplayMode.Fit,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center
        };
        _layers.Add(layer);
        _dirty = true;
        _canvas.Layers = _layers; // 触发 RefreshImages 加载位图
        // 按贴纸宽高比设定初始尺寸（高 = 主界面 0.8，宽按比例），并居中放置。
        if (_canvas.GetThumbnail(layer.Id) is { } bitmap && bitmap.PixelSize.Height > 0)
        {
            var aspect = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
            var h = _canvas.IslandHeight * 0.8;
            layer.Width = Math.Max(1, h * aspect);
            layer.Height = h;
            _canvas.Refresh();
        }

        _canvas.Select(layer.Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>在主界面中央添加一个矢量形状图层（可在画布上继续移动 / 调整）。</summary>
    private void AddShapeLayer()
    {
        PushUndo();
        _layers.Add(new WallpaperLayerItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"形状图层 {_layers.Count + 1}",
            Kind = WallpaperLayerKind.Shape,
            ShapeType = WallpaperShapeType.Rectangle,
            Source = WallpaperSource.None,
            SizeMode = WallpaperLayerSizeMode.Custom,
            AnchorX = WallpaperLayerAnchorX.Center,
            AnchorY = WallpaperLayerAnchorY.Center,
            Width = 160,
            Height = 100
        });
        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(_layers[^1].Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    /// <summary>在主界面中央添加一个文本框图层。</summary>
    private void AddTextLayer()
    {
        PushUndo();
        _layers.Add(new WallpaperLayerItem
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
        });
        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(_layers[^1].Id);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    private void ResetIslandSize()
    {
        var size = InjectorRuntime.GetCurrentIslandSize();
        _canvas.SetIslandSize(size?.Width > 0 ? size.Value.Width : DefaultIslandWidth,
            size?.Height > 0 ? size.Value.Height : DefaultIslandHeight);
        _statusText.Text = "已把主界面尺寸重置为 ClassIsland 实际尺寸。";
    }

    // ============ 图层面板 ============

    /// <summary>当前正在拖拽排序的图层（非空表示正在拖拽中）。</summary>
    private WallpaperLayerItem? _reorderLayer;
    /// <summary>当前正在拖拽的背景（主界面）行（与 _reorderLayer 互斥）。</summary>
    private bool _reorderBackground;
    /// <summary>背景行拖拽的目标层级。</summary>
    private WallpaperLayerZOrder _reorderBackgroundTarget;
    /// <summary>拖拽排序：源图层在 _layers 中的索引。</summary>
    private int _reorderSourceIndex;
    /// <summary>拖拽排序：目标插入索引。</summary>
    private int _reorderInsertIndex;
    /// <summary>拖拽排序：插入位置指示线（舞台右上角图层面板内，跟随主题强调色）。</summary>
    private readonly Border _reorderIndicator = new()
    {
        Height = 3,
        CornerRadius = new CornerRadius(1.5),
        Background = new SolidColorBrush(ThemePalette.AccentColor()),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        IsVisible = false,
        IsHitTestVisible = false,
        ZIndex = 10
    };

    private void RefreshLayerList()
    {
        _layerStack.Children.Clear();

        var islandRow = new LayerRowControl
        {
            IsIsland = true,
            Title = "ClassIsland 主界面",
            Subtitle = "背景图层",
            IconGlyph = "\uE62F",
            Unlocked = _canvas.IslandUnlocked,
            Selected = _canvas.SelectedLayer == null
        }.WithHandlers(
            _ => _canvas.Select(null),
            null,
            () => ToggleIslandUnlock(),
            null);
        // 背景行可拖拽调整层级：顶部 = 底色之后，底部 = 底色之上、组件之下。
        islandRow.DragHandlePressed += e => BeginBackgroundReorder(islandRow, e);
        // 双击背景行 = 打开背景效果窗口（与 Photoshop 双击图层唤出效果一致）。
        islandRow.DoubleTapRequested += OpenBackgroundEffects;

        // 背景行位置跟随层级：底色之后 → 列表顶部（最前）；其余 → 列表底部（最后）。
        var islandAtTop = _canvas.ZOrder == WallpaperLayerZOrder.BehindBackground;
        if (islandAtTop)
        {
            _layerStack.Children.Add(islandRow);
        }

        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            var captured = layer;
            var row = new LayerRowControl
            {
                LayerId = layer.Id,
                IsIsland = false,
                Title = layer.Name,
                Subtitle = (string.IsNullOrEmpty(layer.GroupId) ? string.Empty : "组 · ")
                    + (layer.Kind == WallpaperLayerKind.Image
                        ? $"{DisplayKind(layer)}{SmtcModeSuffix(layer)} · {DisplayModeName(layer.DisplayMode)}"
                        : $"{DisplayKind(layer)}{SmtcModeSuffix(layer)}"),
                IconGlyph = layer.Kind switch
                {
                    WallpaperLayerKind.Shape => "\uE774",
                    WallpaperLayerKind.Text => "\uF1BD",
                    _ => layer.Source == WallpaperSource.SmtcAlbum ? "\uE021" : "\uE9B2"
                },
                Visible = layer.Visible,
                Locked = _canvas.IsLocked(layer.Id),
                Selected = _canvas.SelectedLayers.Contains(layer),
                Thumbnail = _canvas.GetThumbnail(layer.Id)
            };
            // Ctrl + 点击 = 多选（切换选中）；普通点击 = 单选（若属于组则选中整组）。
            row.WithHandlers(
                ctrl =>
                {
                    if (ctrl)
                    {
                        _canvas.SelectWithToggle(captured.Id);
                    }
                    else
                    {
                        _canvas.SelectWithGroup(captured.Id);
                    }
                },
                () => ToggleLayerVisibility(captured),
                () => ToggleLayerLock(captured),
                () => DeleteLayer(captured));
            row.DragHandlePressed += e => BeginLayerReorder(captured, e);
            _layerStack.Children.Add(row);
        }

        if (!islandAtTop)
        {
            _layerStack.Children.Add(islandRow);
        }
    }

    // ============ 拖拽排序（图层列表）============
    // 平滑拖拽：拖拽期间不重建列表，只显示「插入指示线」+ 源行半透明，释放时一次性重排。

    /// <summary>开始拖拽排序：记录图层、弹出置顶幽灵预览窗口、捕获指针。</summary>
    private void BeginLayerReorder(WallpaperLayerItem layer, PointerPressedEventArgs e)
    {
        if (_reorderLayer != null || _reorderBackground)
        {
            return;
        }

        _reorderLayer = layer;
        _reorderSourceIndex = _layers.IndexOf(layer);
        _reorderInsertIndex = _reorderSourceIndex;
        PushUndo();
        _layerStack.PointerMoved += LayerReorderPointerMoved;
        _layerStack.PointerReleased += LayerReorderPointerReleased;
        _layerStack.PointerCaptureLost += LayerReorderOnCaptureLost;

        // 幽灵预览：独立置顶窗口跟随鼠标（参考「主界面 → 组件」的拖拽预览），
        // 完全不参与本窗口布局，避免破坏右侧面板。先显示预览再捕获指针，
        // 避免 Show 新窗口导致指针捕获被取消。
        foreach (var child in _layerStack.Children)
        {
            if (child is LayerRowControl row && row.LayerId == layer.Id)
            {
                var childOrigin = child.TranslatePoint(new Point(0, 0), this) ?? default;
                var grabScreen = this.PointToScreen(childOrigin);
                var pointerScreen = this.PointToScreen(e.GetPosition(this));
                _reorderGrabOffset = new Point(
                    pointerScreen.X - grabScreen.X,
                    pointerScreen.Y - grabScreen.Y);
                ShowDragPreview(child);
                break;
            }
        }

        UpdateReorderIndicator();
        UpdateDragPreview(e);
        e.Pointer.Capture(_layerStack);
        e.Handled = true;
    }

    /// <summary>
    /// 开始拖拽背景（主界面）行：放到列表顶部 = 底色之后，放到列表底部 = 底色之上、组件之下。
    /// 层级由背景行在图层面板中的上下位置决定，释放时才生效。
    /// </summary>
    private void BeginBackgroundReorder(LayerRowControl row, PointerPressedEventArgs e)
    {
        if (_reorderLayer != null || _reorderBackground)
        {
            return;
        }

        _reorderBackground = true;
        _reorderBackgroundTarget = _canvas.ZOrder;
        _layerStack.PointerMoved += LayerReorderPointerMoved;
        _layerStack.PointerReleased += LayerReorderPointerReleased;
        _layerStack.PointerCaptureLost += LayerReorderOnCaptureLost;

        // 幽灵预览：截取背景行外观跟随鼠标（参考图层行的拖拽预览）。
        var childOrigin = row.TranslatePoint(new Point(0, 0), this) ?? default;
        var grabScreen = this.PointToScreen(childOrigin);
        var pointerScreen = this.PointToScreen(e.GetPosition(this));
        _reorderGrabOffset = new Point(
            pointerScreen.X - grabScreen.X,
            pointerScreen.Y - grabScreen.Y);
        ShowDragPreview(row);
        UpdateReorderIndicator();
        UpdateDragPreview(e);
        e.Pointer.Capture(_layerStack);
        e.Handled = true;
    }

    private void LayerReorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_reorderBackground)
        {
            var target = ComputeBackgroundDropOrder(e.GetPosition(_layerStack));
            if (target != _reorderBackgroundTarget)
            {
                _reorderBackgroundTarget = target;
                UpdateReorderIndicator();
            }

            UpdateDragPreview(e);
            return;
        }

        if (_reorderLayer == null)
        {
            return;
        }

        var insert = ComputeReorderInsertIndex(e.GetPosition(_layerStack));
        if (insert != _reorderInsertIndex)
        {
            _reorderInsertIndex = insert;
            UpdateReorderIndicator();
        }

        UpdateDragPreview(e);
    }

    private void LayerReorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndLayerReorder(e);
    }

    private void LayerReorderOnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndLayerReorder(null);
    }

    /// <summary>创建（如需要）并显示拖拽幽灵预览窗口，截取源行外观。</summary>
    private void ShowDragPreview(Control sourceRow)
    {
        if (_dragPreviewWindow == null || _dragPreviewHost == null)
        {
            var host = new Border
            {
                CornerRadius = new CornerRadius(6),
                Opacity = 0.65,
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Color = Color.FromArgb(120, 0, 0, 0) })
            };
            _dragPreviewHost = host;
            _dragPreviewWindow = new Window
            {
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                CanResize = false,
                Topmost = true,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Background = Brushes.Transparent,
                Content = host
            };
        }

        _dragPreviewHost!.Width = sourceRow.Bounds.Width > 0 ? sourceRow.Bounds.Width : 240;
        _dragPreviewHost.Height = sourceRow.Bounds.Height > 0 ? sourceRow.Bounds.Height : 40;
        _dragPreviewHost.Background = new VisualBrush(sourceRow);
        _dragPreviewWindow.Width = _dragPreviewHost.Width;
        _dragPreviewWindow.Height = _dragPreviewHost.Height;
        _dragPreviewWindow.Show();
    }

    /// <summary>把幽灵预览窗口移动到指针位置（屏幕坐标，保留抓取偏移）。</summary>
    private void UpdateDragPreview(PointerEventArgs e)
    {
        if (_dragPreviewWindow is not { IsVisible: true })
        {
            return;
        }

        var screen = this.PointToScreen(e.GetPosition(this));
        _dragPreviewWindow.Position = new PixelPoint(
            (int)(screen.X - _reorderGrabOffset.X),
            (int)(screen.Y - _reorderGrabOffset.Y));
    }

    private void EndLayerReorder(PointerEventArgs? e)
    {
        if (_reorderBackground)
        {
            var target = _reorderBackgroundTarget;
            _reorderBackground = false;
            _layerStack.PointerMoved -= LayerReorderPointerMoved;
            _layerStack.PointerReleased -= LayerReorderPointerReleased;
            _layerStack.PointerCaptureLost -= LayerReorderOnCaptureLost;
            if (e != null)
            {
                e.Pointer.Capture(null);
            }

            _dragPreviewWindow?.Hide();
            _reorderIndicator.IsVisible = false;
            if (target != _canvas.ZOrder)
            {
                _canvas.ZOrder = target;
                _dirty = true;
                RefreshLayerList();
            }

            UpdateStatus();
            return;
        }

        if (_reorderLayer == null)
        {
            return;
        }

        var layer = _reorderLayer;
        var sourceIndex = _reorderSourceIndex;
        var insertIndex = _reorderInsertIndex;
        _reorderLayer = null;
        _layerStack.PointerMoved -= LayerReorderPointerMoved;
        _layerStack.PointerReleased -= LayerReorderPointerReleased;
        _layerStack.PointerCaptureLost -= LayerReorderOnCaptureLost;
        if (e != null)
        {
            e.Pointer.Capture(null);
        }

        _dragPreviewWindow?.Hide();
        _reorderIndicator.IsVisible = false;
        if (insertIndex != sourceIndex)
        {
            _dirty = true;
            _layers.RemoveAt(sourceIndex);
            var adjusted = insertIndex > sourceIndex ? insertIndex - 1 : insertIndex;
            _layers.Insert(Math.Clamp(adjusted, 0, _layers.Count), layer);
        }

        RefreshLayerList();
        _canvas.Refresh();
        UpdateStatus();
    }

    /// <summary>
    /// 计算指针位置对应的插入索引（_layers 中的位置，0 = 最底，Count = 最前）。
    /// 图层面板自上而下 = 从前到后：指针在列表上方 → 更靠前，下方 → 更靠后。
    /// 背景行固定在一端（顶部 = 底色之后 / 底部 = 底色之上），图层不能越过它。
    /// </summary>
    private int ComputeReorderInsertIndex(Point pos)
    {
        foreach (var child in _layerStack.Children)
        {
            if (child is not LayerRowControl row)
            {
                continue;
            }

            var b = child.Bounds;
            if (pos.Y >= b.Y && pos.Y <= b.Y + b.Height)
            {
                if (row.IsIsland)
                {
                    // 背景行：顶部（底色之后）→ 图层只能落在其下（最前）；底部 → 只能落在其上（最底）。
                    return _canvas.ZOrder == WallpaperLayerZOrder.BehindBackground ? _layers.Count : 0;
                }

                var index = _layers.FindIndex(l => l.Id == row.LayerId);
                // 上半 → 落在此行上方（更前）；下半 → 落在此行下方（更后）。
                return pos.Y < b.Y + b.Height / 2 ? index : Math.Max(0, index - 1);
            }

            if (pos.Y < b.Y)
            {
                // 指针在整列上方 → 最前。
                return _layers.Count;
            }
        }

        // 指针在整列下方 → 最底。
        return 0;
    }

    /// <summary>背景行拖拽：按指针在面板中的上下位置决定目标层级（上 = 底色之后，下 = 底色之上、组件之下）。</summary>
    private WallpaperLayerZOrder ComputeBackgroundDropOrder(Point pos)
    {
        var height = _layerStack.Bounds.Height > 0 ? _layerStack.Bounds.Height : 200;
        return pos.Y < height / 2
            ? WallpaperLayerZOrder.BehindBackground
            : WallpaperLayerZOrder.AboveBackground;
    }

    /// <summary>插入索引对应的指示线 Y 坐标（图层面板坐标系）。</summary>
    private double GetReorderIndicatorY(int insertIndex)
    {
        var rows = _layerStack.Children.OfType<LayerRowControl>().Where(r => r.LayerId != null).ToArray();
        if (rows.Length == 0)
        {
            return 0;
        }

        if (insertIndex >= _layers.Count)
        {
            return rows[0].Bounds.Y; // 顶部之上
        }

        if (insertIndex <= 0)
        {
            return rows[^1].Bounds.Bottom; // 底部之下
        }

        var target = rows.FirstOrDefault(r => r.LayerId == _layers[insertIndex].Id);
        return target?.Bounds.Y ?? rows[0].Bounds.Y;
    }

    private void UpdateReorderIndicator()
    {
        if (_reorderBackground)
        {
            // 背景行：顶部 = 底色之后，底部 = 底色之上、组件之下。
            var atTop = _reorderBackgroundTarget == WallpaperLayerZOrder.BehindBackground;
            _reorderIndicator.Margin = new Thickness(0,
                atTop ? 0 : Math.Max(0, _layerStack.Bounds.Height - 3), 0, 0);
            _reorderIndicator.IsVisible = true;
            return;
        }

        if (_reorderLayer == null)
        {
            _reorderIndicator.IsVisible = false;
            return;
        }

        _reorderIndicator.Margin = new Thickness(0, Math.Max(0, GetReorderIndicatorY(_reorderInsertIndex) - 1.5), 0, 0);
        _reorderIndicator.IsVisible = true;
    }

    private void ToggleIslandUnlock()
    {
        var newState = !_canvas.IslandUnlocked;
        _canvas.IslandUnlocked = newState;
        RefreshLayerList();
        UpdateStatus();
        // 教程推进：区分「正常解锁」与「已解锁用户先锁回去再解锁」两种分支，
        // 保证已解锁时教程不会卡在后续的拖动步骤上。
        var tag = HostTutorial.GetCurrentSentenceTag();
        if (newState)
        {
            // 点击后处于解锁状态：解锁完成，推进对应等待句（unlock 或 unlock-reset）。
            if (tag is "unlock" or "unlock-reset")
            {
                TutorialServicePush(tag);
            }
        }
        else if (tag == "unlock")
        {
            // 点击后处于锁定状态：用户把原本已解锁的主界面锁回去了。
            // 跳到「吃惊 + 重新解锁」分支句，让用户再解锁一次。
            HostTutorial.PushToNextSentence();
        }
    }

    private void ToggleLayerVisibility(WallpaperLayerItem layer)
    {
        PushUndo();
        layer.Visible = !layer.Visible;
        _dirty = true;
        _canvas.Refresh();
        RefreshLayerList();
    }

    private void ToggleLayerLock(WallpaperLayerItem layer)
    {
        _canvas.ToggleLock(layer.Id);
        RefreshLayerList();
    }

    private void DeleteLayer(WallpaperLayerItem layer)
    {
        if (_canvas.IsLocked(layer.Id))
        {
            return;
        }

        // 多选时删除整个选中集（跳过锁定）；仅单个触发时只删该图层。
        var toDelete = _canvas.SelectedLayers.Count > 0 && _canvas.SelectedLayers.Contains(layer)
            ? _canvas.SelectedLayers.Where(l => !_canvas.IsLocked(l.Id)).ToList()
            : [layer];
        PushUndo();
        foreach (var l in toDelete)
        {
            _layers.Remove(l);
        }

        _dirty = true;
        _canvas.Layers = _layers;
        _canvas.Select(null);
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
    }

    private void RefreshCustomSizePanel()
    {
        var layer = _canvas.SelectedLayer;
        var custom = layer is { SizeMode: WallpaperLayerSizeMode.Custom } &&
                     !(layer.Source == WallpaperSource.SmtcAlbum && layer.SmtcMode == WallpaperLayerSmtcMode.Default);
        _widthItem.IsVisible = custom;
        _heightItem.IsVisible = custom;
    }

    /// <summary>SMTC 图层是否处于「默认处理」模式（仅透明度/显示方式可改）。</summary>
    private static bool IsSmtcDefaultMode(WallpaperLayerItem layer) =>
        layer.Source == WallpaperSource.SmtcAlbum && layer.SmtcMode == WallpaperLayerSmtcMode.Default;

    private void RefreshInspector()
    {
        _updatingInspector = true;
        try
        {
            // 画笔 / 橡皮擦设置：仅对应工具激活时显示（与图层选中无关）。
            var brushActive = _canvas.Tool is WallpaperEditorTool.Brush or WallpaperEditorTool.Eraser;
            _brushColorItem.IsVisible = brushActive;
            _brushSizeItem.IsVisible = brushActive;
            _brushColorPicker.Color = _canvas.ActiveColor;
            _brushSizeSlider.Value = _canvas.BrushSize;
            var layer = _canvas.SelectedLayer;
            if (layer == null)
            {
                _nameBox.IsEnabled = false;
                _opacitySlider.IsEnabled = false;
                _displayModeBox.IsEnabled = false;
                _smtcModeBox.IsEnabled = false;
                _smtcModeItem.IsVisible = false;
                _displayModeItem.IsVisible = false;
                _fullscreenItem.IsVisible = false;
                _sliceItem.IsVisible = false;
                _editSliceItem.IsVisible = false;
                _sliceLeftItem.IsVisible = false;
                _sliceTopItem.IsVisible = false;
                _sliceRightItem.IsVisible = false;
                _sliceBottomItem.IsVisible = false;
                _fullscreenHint.IsVisible = false;
                _shadowItem.IsVisible = false;
                _shadowBlurItem.IsVisible = false;
                _shadowOffsetXItem.IsVisible = false;
                _shadowOffsetYItem.IsVisible = false;
                _shadowColorItem.IsVisible = false;
                _shadowOpacityItem.IsVisible = false;
                _fillIslandToggle.IsEnabled = false;
                _widthSpin.IsEnabled = false;
                _heightSpin.IsEnabled = false;
                _rotationSpin.IsEnabled = false;
                _offsetXSpin.IsEnabled = false;
                _offsetYSpin.IsEnabled = false;
                _anchorPicker.IsEnabled = false;
                _shapeTypeItem.IsVisible = false;
                _shapeCornerRadiusItem.IsVisible = false;
                _shapeStarPointsItem.IsVisible = false;
                _shapeStarInsetItem.IsVisible = false;
                _shapeFillItem.IsVisible = false;
                _shapeFillThemeItem.IsVisible = false;
                _shapeStrokeItem.IsVisible = false;
                _shapeStrokeThemeItem.IsVisible = false;
                _shapeStrokeWidthItem.IsVisible = false;
                _textItem.IsVisible = false;
                _textFontSizeItem.IsVisible = false;
                _textFontFamilyItem.IsVisible = false;
                _textColorItem.IsVisible = false;
                _textColorThemeItem.IsVisible = false;
                _textStrokeItem.IsVisible = false;
                _textStrokeColorItem.IsVisible = false;
                _textStrokeThicknessItem.IsVisible = false;
                _textUseSmtcTitleItem.IsVisible = false;
                _textBoldItem.IsVisible = false;
                _textAlignItem.IsVisible = false;
                _relativeHint.Text = "未选中图层。点击画布上的图层，或在左侧图层面板选择。";
                RefreshCustomSizePanel();
                return;
            }

            var selected = _canvas.SelectedLayers;
            var multi = selected.Count > 1;
            // 形状/文本专属项仅在「全部选中图层同类型」时显示，避免混合多选时出现误导。
            var allSameKind = selected.Select(l => l.Kind).Distinct().Count() <= 1;
            var smtcDefault = IsSmtcDefaultMode(layer);
            var isShape = allSameKind && layer.Kind == WallpaperLayerKind.Shape;
            var isText = allSameKind && layer.Kind == WallpaperLayerKind.Text;
            _nameBox.IsEnabled = true;
            _opacitySlider.IsEnabled = true;
            _displayModeBox.IsEnabled = layer.Kind == WallpaperLayerKind.Image;
            _smtcModeBox.IsEnabled = true;
            _smtcModeItem.IsVisible = layer.Source == WallpaperSource.SmtcAlbum;
            _displayModeItem.IsVisible = layer.Kind == WallpaperLayerKind.Image;
            var isFullscreen = layer.Kind == WallpaperLayerKind.Image && layer.FullscreenExtend;
            _fullscreenItem.IsVisible = layer.Kind == WallpaperLayerKind.Image;
            _sliceItem.IsVisible = isFullscreen;
            _editSliceItem.IsVisible = isFullscreen && layer.SliceEnabled;
            _sliceLeftItem.IsVisible = isFullscreen && layer.SliceEnabled;
            _sliceTopItem.IsVisible = isFullscreen && layer.SliceEnabled;
            _sliceRightItem.IsVisible = isFullscreen && layer.SliceEnabled;
            _sliceBottomItem.IsVisible = isFullscreen && layer.SliceEnabled;
            _fullscreenHint.IsVisible = isFullscreen;
            _fullscreenHint.Text = isFullscreen
                ? "该图片将铺满整个 ClassIsland 显示框架，运行时隐藏底色、边框与阴影。开启「九宫格切图」后点击「编辑切图」，可在图片上直接框选四条切边防止四角拉伸变形。"
                : string.Empty;
            // 效果仅图片图层显示；投影子项仅在启用投影后展开。
            var isImage = allSameKind && layer.Kind == WallpaperLayerKind.Image;
            _shadowItem.IsVisible = isImage;
            _shadowBlurItem.IsVisible = isImage && layer.ShadowEnabled;
            _shadowOffsetXItem.IsVisible = isImage && layer.ShadowEnabled;
            _shadowOffsetYItem.IsVisible = isImage && layer.ShadowEnabled;
            _shadowColorItem.IsVisible = isImage && layer.ShadowEnabled;
            _shadowOpacityItem.IsVisible = isImage && layer.ShadowEnabled;
            _shadowToggle.IsChecked = layer.ShadowEnabled;
            _shadowBlurSpin.DoubleValue = layer.ShadowBlurRadius;
            _shadowOffsetXSpin.DoubleValue = layer.ShadowOffsetX;
            _shadowOffsetYSpin.DoubleValue = layer.ShadowOffsetY;
            _shadowColorPicker.Color = ReadColor(layer.ShadowColor, Color.FromArgb(0x99, 0, 0, 0));
            _shadowOpacitySlider.Value = layer.ShadowOpacity;
            _shapeTypeItem.IsVisible = isShape;
            // 圆角半径仅圆角矩形、星角/内凹仅五角星显示；属性值在 ShapeType 变化时由读值刷新。
            _shapeCornerRadiusItem.IsVisible = isShape && layer.ShapeType == WallpaperShapeType.RoundedRectangle;
            _shapeStarPointsItem.IsVisible = isShape && layer.ShapeType == WallpaperShapeType.Star;
            _shapeStarInsetItem.IsVisible = isShape && layer.ShapeType == WallpaperShapeType.Star;
            _shapeFillItem.IsVisible = isShape;
            _shapeFillThemeItem.IsVisible = isShape;
            _shapeStrokeItem.IsVisible = isShape;
            _shapeStrokeThemeItem.IsVisible = isShape;
            _shapeStrokeWidthItem.IsVisible = isShape;
            _textItem.IsVisible = isText;
            _textFontSizeItem.IsVisible = isText;
            _textFontFamilyItem.IsVisible = isText;
            _textColorItem.IsVisible = isText;
            _textColorThemeItem.IsVisible = isText;
            _textStrokeItem.IsVisible = isText;
            _textStrokeColorItem.IsVisible = isText && layer.TextStrokeEnabled;
            _textStrokeThicknessItem.IsVisible = isText && layer.TextStrokeEnabled;
            _textUseSmtcTitleItem.IsVisible = isText;
            _textBoldItem.IsVisible = isText;
            _textAlignItem.IsVisible = isText;
            // 默认处理模式：锁定尺寸/位移/旋转，强制铺满主界面。
            _fillIslandToggle.IsEnabled = !smtcDefault;
            _rotationSpin.IsEnabled = !smtcDefault;
            _offsetXSpin.IsEnabled = !smtcDefault;
            _offsetYSpin.IsEnabled = !smtcDefault;
            _anchorPicker.IsEnabled = !smtcDefault;
            _widthSpin.IsEnabled = !smtcDefault;
            _heightSpin.IsEnabled = !smtcDefault;
            _nameBox.Text = layer.Name;
            _opacitySlider.Value = layer.Opacity;
            _displayModeBox.SelectedItem = DisplayModeChoices.FirstOrDefault(c => c.Value == layer.DisplayMode) ?? DisplayModeChoices[0];
            _fullscreenToggle.IsChecked = layer.FullscreenExtend;
            _sliceToggle.IsChecked = layer.SliceEnabled;
            _sliceLeftSpin.DoubleValue = layer.SliceLeft;
            _sliceTopSpin.DoubleValue = layer.SliceTop;
            _sliceRightSpin.DoubleValue = layer.SliceRight;
            _sliceBottomSpin.DoubleValue = layer.SliceBottom;
            _smtcModeBox.SelectedItem = SmtcModeChoices.FirstOrDefault(c => c.Value == layer.SmtcMode) ?? SmtcModeChoices[0];
            _shapeTypeBox.SelectedItem = ShapeTypeChoices.FirstOrDefault(c => c.Value == layer.ShapeType) ?? ShapeTypeChoices[0];
            _shapeCornerRadiusSpin.DoubleValue = layer.ShapeCornerRadius;
            _shapeStarPointsSpin.DoubleValue = layer.ShapeStarPoints;
            _shapeStarInsetSpin.DoubleValue = layer.ShapeStarInset;
            _shapeFillPicker.Color = InspectorColor(layer.FillColor, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), layer.FillUsesThemeColor);
            _shapeStrokePicker.Color = InspectorColor(layer.StrokeColor, Colors.White, layer.StrokeUsesThemeColor);
            _shapeStrokeSpin.DoubleValue = layer.StrokeThickness;
            _textBox.Text = layer.Text;
            _textFontSizeSpin.DoubleValue = layer.TextFontSize;
            _textFontFamilyBox.SelectedItem = ((IEnumerable<FontFamily>)_textFontFamilyBox.ItemsSource!)
                .FirstOrDefault(font => string.Equals(font.Name, layer.TextFontFamily, StringComparison.CurrentCultureIgnoreCase))
                ?? FontFamily.Default;
            _textColorPicker.Color = InspectorColor(layer.TextColor, Colors.White, layer.TextUsesThemeColor);
            _textStrokeToggle.IsChecked = layer.TextStrokeEnabled;
            _textStrokeColorPicker.Color = ReadColor(layer.TextStrokeColor, Colors.Black);
            _textStrokeThicknessSpin.DoubleValue = layer.TextStrokeThickness;
            _textUseSmtcTitleToggle.IsChecked = layer.TextUseSmtcTitle;
            _textBoldToggle.IsChecked = layer.TextBold;
            _textAlignBox.SelectedItem = TextAlignChoices.FirstOrDefault(c => c.Value == layer.TextAlign) ?? TextAlignChoices[1];
            _fillIslandToggle.IsChecked = smtcDefault || layer.SizeMode == WallpaperLayerSizeMode.FillIsland;
            _widthSpin.DoubleValue = layer.Width;
            _heightSpin.DoubleValue = layer.Height;
            _rotationSpin.DoubleValue = layer.Rotation;
            _offsetXSpin.DoubleValue = layer.OffsetX;
            _offsetYSpin.DoubleValue = layer.OffsetY;
            _anchorPicker.AnchorX = layer.AnchorX;
            _anchorPicker.AnchorY = layer.AnchorY;
            _anchorPicker.InvalidateVisual();
            // 主题色跟随：开启时颜色选择器只读（实际颜色由宿主主题驱动，这里展示当前强调色）。
            _shapeFillThemeToggle.IsChecked = layer.FillUsesThemeColor;
            _shapeStrokeThemeToggle.IsChecked = layer.StrokeUsesThemeColor;
            _textColorThemeToggle.IsChecked = layer.TextUsesThemeColor;
            _shapeFillPicker.IsEnabled = isShape && !layer.FillUsesThemeColor;
            _shapeStrokePicker.IsEnabled = isShape && !layer.StrokeUsesThemeColor;
            _textColorPicker.IsEnabled = isText && !layer.TextUsesThemeColor;
            _relativeHint.Text = multi
                ? $"已选中 {selected.Count} 个图层：对属性的修改将应用到全部选中图层（部分类型专属设置仅在全部同类型时可用）。"
                : RelativeHintText(layer);
            RefreshCustomSizePanel();
        }
        finally
        {
            _updatingInspector = false;
        }
    }

    /// <summary>解析颜色（失败回退）。</summary>
    private static Color ReadColor(string text, Color fallback)
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

    /// <summary>取检查器显示的颜色：启用主题色时显示当前主题强调色（保留配置颜色的透明度）。</summary>
    private static Color InspectorColor(string text, Color fallback, bool useTheme)
    {
        var color = ReadColor(text, fallback);
        if (!useTheme)
        {
            return color;
        }

        var accent = ThemePalette.AccentColor();
        return Color.FromArgb(color.A, accent.R, accent.G, accent.B);
    }

    /// <summary>把选中图层的相对位置表达成人类可读的提示，如「右边缘 = 主界面右边缘 - 16px」。</summary>
    private string RelativeHintText(WallpaperLayerItem layer)
    {
        if (IsSmtcDefaultMode(layer))
        {
            return "当前为 SMTC 图层的默认处理：铺满整个主界面，仅可调整透明度与显示方式。\n切换为「当作图片处理」后可自由位移、缩放、旋转。";
        }

        if (layer.SizeMode == WallpaperLayerSizeMode.FillIsland)
        {
            return "当前图层铺满整个主界面，随主界面尺寸自适应。拖动手柄或旋转后会切换为自定义尺寸。";
        }

        var xText = layer.AnchorX switch
        {
            WallpaperLayerAnchorX.Left => $"左边缘 = 主界面左边缘 {OffsetText(layer.OffsetX)}",
            WallpaperLayerAnchorX.Center => $"中心 = 主界面中心 {OffsetText(layer.OffsetX)}",
            WallpaperLayerAnchorX.Right => $"右边缘 = 主界面右边缘 {OffsetText(layer.OffsetX)}",
            _ => string.Empty
        };
        var yText = layer.AnchorY switch
        {
            WallpaperLayerAnchorY.Top => $"上边缘 = 主界面上边缘 {OffsetText(layer.OffsetY)}",
            WallpaperLayerAnchorY.Center => $"垂直中心 = 主界面垂直中心 {OffsetText(layer.OffsetY)}",
            WallpaperLayerAnchorY.Bottom => $"下边缘 = 主界面下边缘 {OffsetText(layer.OffsetY)}",
            _ => string.Empty
        };
        return $"{xText}\n{yText} · 宽 {layer.Width:0}px × 高 {layer.Height:0}px · 旋转 {layer.Rotation:0}°";
    }

    private static string OffsetText(double offset)
    {
        if (Math.Abs(offset) < 0.05)
        {
            return "（0px，精确对齐）";
        }

        return offset < 0 ? $"+ {Math.Abs(offset):0}px（向左/上）" : $"+ {offset:0}px（向右/下）";
    }

    private void UpdateStatus()
    {
        var islandPart = $"主界面 {_canvas.IslandWidth:0} × {_canvas.IslandHeight:0}";
        var unlockPart = _canvas.IslandUnlocked
            ? "· 主界面已解锁：拖动右/下边缘可模拟 ClassIsland 长度变化，观察底图自适应"
            : "· 在右侧图层面板解锁主界面后可拖动边缘测试自适应";
        var selected = _canvas.SelectedLayers;
        var selectedPart = selected.Count switch
        {
            0 => string.Empty,
            1 => $"· 已选「{selected[0].Name}」",
            _ => $"· 已选 {selected.Count} 个图层"
        };
        _statusText.Text = $"{islandPart} {selectedPart} {unlockPart}";
    }

    // ============ 关闭确认 ============

    private async void OnClosingConfirm(object? sender, WindowClosingEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        e.Cancel = true;
        var dialog = new ContentDialog
        {
            Title = "保存更改？",
            Content = "底图图层编辑器中有尚未保存的更改。",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        // 显式传入本窗口的 TopLevel：多窗口/窗口分离时无参重载可能找不到根而崩溃。
        var topLevel = TopLevel.GetTopLevel(this);
        var result = topLevel != null
            ? await dialog.ShowAsync(topLevel)
            : ContentDialogResult.None;
        if (result == ContentDialogResult.Primary)
        {
            Save();
            _dirty = false;
            Close();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _dirty = false;
            Close();
        }
    }

    // ============ 小工具 ============

    private static IconText SectionTitle(string glyph, string text) => new()
    {
        Glyph = glyph,
        Text = text,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>图标 + 文字按钮（如「＋ 添加图片图层」）。</summary>
    private static Button ActionButton(string glyph, string text, Action action)
    {
        var button = new Button { Content = new IconText { Glyph = glyph, Text = text } };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>命令栏按钮（图标 + 右侧文字标签，参考档案编辑窗口）。</summary>
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

    /// <summary>纯图标工具栏按钮（带提示文字）。</summary>
    private static Button ToolbarIconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = glyph, Text = string.Empty },
            Padding = new Thickness(9, 5)
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Slider SliderControl(double min, double max, double tick) => new()
    {
        Width = 150,
        Minimum = min,
        Maximum = max,
        TickFrequency = tick,
        IsSnapToTickEnabled = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static T Selected<T>(ComboBox box, T fallback) => box.SelectedItem is Pick<T> choice ? choice.Value : fallback;

    /// <summary>查找主题画刷（插件窗口解析不到时返回 null，调用方回退到深色）。</summary>
    private static IBrush? ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;

    private static string DisplayModeName(WallpaperDisplayMode mode) => mode switch
    {
        WallpaperDisplayMode.Fill => "填充（裁剪）",
        WallpaperDisplayMode.Fit => "适应",
        WallpaperDisplayMode.Stretch => "拉伸",
        WallpaperDisplayMode.Tile => "平铺",
        _ => "填充"
    };

    private static string DisplaySource(WallpaperLayerItem layer) => layer.Source switch
    {
        WallpaperSource.LocalImage => "本地图片",
        WallpaperSource.FolderSlideshow => "幻灯片",
        WallpaperSource.SmtcAlbum => "SMTC 封面",
        _ => "无来源"
    };

    /// <summary>图层类型显示名（位图 / 形状·类型 / 文本）。</summary>
    private static string DisplayKind(WallpaperLayerItem layer) => layer.Kind switch
    {
        WallpaperLayerKind.Shape => $"形状·{ShapeTypeName(layer.ShapeType)}",
        WallpaperLayerKind.Text => "文本",
        _ => DisplaySource(layer)
    };

    private static string ShapeTypeName(WallpaperShapeType type) => type switch
    {
        WallpaperShapeType.Rectangle => "矩形",
        WallpaperShapeType.RoundedRectangle => "圆角矩形",
        WallpaperShapeType.Ellipse => "椭圆",
        WallpaperShapeType.Line => "直线",
        WallpaperShapeType.Triangle => "三角形",
        WallpaperShapeType.Diamond => "菱形",
        WallpaperShapeType.Pentagon => "五边形",
        WallpaperShapeType.Hexagon => "六边形",
        WallpaperShapeType.Star => "五角星",
        WallpaperShapeType.Heart => "心形",
        WallpaperShapeType.Parallelogram => "平行四边形",
        _ => "矩形"
    };

    /// <summary>SMTC 图层在列表副标题里的模式后缀。</summary>
    private static string SmtcModeSuffix(WallpaperLayerItem layer) =>
        layer.Source == WallpaperSource.SmtcAlbum
            ? layer.SmtcMode == WallpaperLayerSmtcMode.AsImage ? "（当作图片）" : "（默认铺满）"
            : string.Empty;

    private static string DisplayZOrder(WallpaperLayerZOrder order) => order switch
    {
        WallpaperLayerZOrder.BehindBackground => "底色之后（默认）",
        WallpaperLayerZOrder.AboveBackground => "底色之上、组件之下",
        WallpaperLayerZOrder.AboveComponents => "组件之上",
        _ => "底色之后"
    };

    private static readonly Pick<WallpaperDisplayMode>[] DisplayModeChoices =
    [
        new(WallpaperDisplayMode.Fill, "填充（裁剪）"),
        new(WallpaperDisplayMode.Fit, "适应（完整显示）"),
        new(WallpaperDisplayMode.Stretch, "拉伸（变形）"),
    ];

    private static readonly Pick<WallpaperLayerSmtcMode>[] SmtcModeChoices =
    [
        new(WallpaperLayerSmtcMode.AsImage, "当作图片处理"),
        new(WallpaperLayerSmtcMode.Default, "默认处理（铺满主界面）"),
    ];

    private static readonly Pick<WallpaperShapeType>[] ShapeTypeChoices =
    [
        new(WallpaperShapeType.Rectangle, "矩形"),
        new(WallpaperShapeType.RoundedRectangle, "圆角矩形"),
        new(WallpaperShapeType.Ellipse, "椭圆"),
        new(WallpaperShapeType.Line, "直线"),
        new(WallpaperShapeType.Triangle, "三角形"),
        new(WallpaperShapeType.Diamond, "菱形"),
        new(WallpaperShapeType.Pentagon, "五边形"),
        new(WallpaperShapeType.Hexagon, "六边形"),
        new(WallpaperShapeType.Star, "五角星"),
        new(WallpaperShapeType.Heart, "心形"),
        new(WallpaperShapeType.Parallelogram, "平行四边形"),
    ];

    private static readonly Pick<WallpaperTextAlign>[] TextAlignChoices =
    [
        new(WallpaperTextAlign.Left, "左对齐"),
        new(WallpaperTextAlign.Center, "居中"),
        new(WallpaperTextAlign.Right, "右对齐"),
    ];

    /// <summary>检查器用的颜色选择器（跟随主题，浅色/深色均可）。</summary>
    private static ColorPicker ColorPicker() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private sealed record Pick<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed class LayerRowControl : Border
    {
        private Action<bool>? _select;
        private Action? _visibilityAction;
        private Action? _lockAction;
        private Action? _deleteAction;

        public string? LayerId { get; init; }
        public bool IsIsland { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public string IconGlyph { get; init; } = string.Empty;
        public bool Visible { get; init; } = true;
        public bool Locked { get; init; }
        public bool Unlocked { get; init; }
        public bool Selected { get; init; }
        public Bitmap? Thumbnail { get; init; }

        /// <summary>拖拽手柄按下（用于图层列表排序）。</summary>
        public event Action<PointerPressedEventArgs>? DragHandlePressed;
        /// <summary>内容区双击（用于唤出背景效果窗口）。</summary>
        public event Action? DoubleTapRequested;

        private DateTime _lastPressUtc = DateTime.MinValue;
        private double _lastPressX;
        private double _lastPressY;

        public LayerRowControl WithHandlers(Action<bool>? select, Action? visibility, Action? lockAction, Action? delete)
        {
            _select = select;
            _visibilityAction = visibility;
            _lockAction = lockAction;
            _deleteAction = delete;
            Build();
            return this;
        }

        private void Build()
        {
            CornerRadius = new CornerRadius(6);
            // 无边框（参考 ClassIsland 组件库卡片的框子样式），选中仅用底色高亮。
            BorderThickness = new Thickness(0);
            Padding = new Thickness(8, 6);

            // 原生风格：透明底 + 悬停微高亮 + 选中强调色（跟随主题，不手搓深色卡片）。
            void ApplyBackground(bool hover)
            {
                Background = Selected
                    ? new SolidColorBrush(ThemePalette.AccentColorWithAlpha(70))
                    : hover
                        ? ThemePalette.SubtleFill()
                        : Brushes.Transparent;
                BorderBrush = Brushes.Transparent;
            }

            ApplyBackground(false);
            PointerEntered += (_, _) => ApplyBackground(true);
            PointerExited += (_, _) => ApplyBackground(false);

            Control preview;
            if (Thumbnail != null)
            {
                preview = new Image
                {
                    Source = Thumbnail,
                    Width = 40,
                    Height = 26,
                    Stretch = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                preview = new Border
                {
                    Width = 40,
                    Height = 26,
                    CornerRadius = new CornerRadius(4),
                    Background = ThemePalette.SubtleFill(),
                    Child = new IconText
                    {
                        Glyph = IconGlyph,
                        Text = string.Empty,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = 0.55
                    }
                };
            }

            // 标签用 Grid（而非 StackPanel）：StackPanel 会给子项无限宽度，TextBlock 无法省略；
            // Grid 单列会让 TextBlock 在可用宽度内自动以「…」截断长名称/副标题。
            var titleBlock = new TextBlock
            {
                Text = Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var subtitleBlock = new TextBlock
            {
                Text = Subtitle,
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                Children = { titleBlock, subtitleBlock }
            };
            Grid.SetRow(subtitleBlock, 1);
            // 内容区用 Grid（而非水平 StackPanel）：StackPanel 会给子项无限宽度，
            // 导致 label 里的 TextBlock 永不截断；Grid 的 * 列会把 label 限制在可用宽度内。
            var contentArea = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8,
                Cursor = new Cursor(StandardCursorType.Hand),
                Children = { preview, label }
            };
            Grid.SetColumn(label, 1);
            contentArea.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(contentArea).Properties.IsLeftButtonPressed)
                {
                    var now = DateTime.UtcNow;
                    var pos = e.GetPosition(this);
                    // 500ms 内、位移小于 8px 的连续两次左键点击 = 双击。
                    if ((now - _lastPressUtc).TotalMilliseconds < 500 &&
                        Math.Abs(pos.X - _lastPressX) < 8 && Math.Abs(pos.Y - _lastPressY) < 8)
                    {
                        DoubleTapRequested?.Invoke();
                    }

                    _lastPressUtc = now;
                    _lastPressX = pos.X;
                    _lastPressY = pos.Y;
                    var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                    _select?.Invoke(ctrl);
                }
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                ColumnSpacing = 4,
                Children = { contentArea }
            };
            Grid.SetColumn(contentArea, 1);

            if (IsIsland)
            {
                // 主界面行：左侧拖拽手柄调整背景层级（顶部 = 底色之后，底部 = 底色之上、组件之下）；
                // 右侧为「解锁主界面」按钮（眼睛不可用，主界面始终可见）。
                var dragHandle = new Border
                {
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new IconText
                    {
                        // 与 ClassIsland 组件库 TouchDragThumb（compact）同款拖拽手柄图标。
                        Glyph = "\uEE49",
                        Text = string.Empty,
                        FontSize = 18,
                        Opacity = 0.8,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                ToolTip.SetTip(dragHandle, "拖动调整背景层级：放到列表顶部 → 底色之后；放到列表底部 → 底色之上、组件之下");
                dragHandle.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
                    {
                        DragHandlePressed?.Invoke(e);
                        e.Handled = true;
                    }
                };
                var lockButton = IconButton(Unlocked ? "\uEAF8" : "\uEAF0",
                    Unlocked ? "锁定主界面" : "解锁主界面（可拖动边缘测试自适应）", _lockAction);
                grid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto");
                Grid.SetColumn(dragHandle, 0);
                Grid.SetColumn(lockButton, 3);
                grid.Children.Add(dragHandle);
                grid.Children.Add(lockButton);
            }
            else
            {
                var dragHandle = new Border
                {
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new IconText
                    {
                        // 与 ClassIsland 组件库 TouchDragThumb（compact）同款拖拽手柄图标。
                        Glyph = "\uEE49",
                        Text = string.Empty,
                        FontSize = 18,
                        Opacity = 0.8,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                ToolTip.SetTip(dragHandle, "拖动调整图层顺序：列表越靠上，显示越靠前");
                dragHandle.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
                    {
                        DragHandlePressed?.Invoke(e);
                        e.Handled = true;
                    }
                };
                var eye = IconButton(Visible ? "\uE813" : "\uE817", Visible ? "隐藏图层" : "显示图层", _visibilityAction);
                var lockButton = IconButton(Locked ? "\uEAF0" : "\uEAF8", Locked ? "解锁图层" : "锁定图层", _lockAction);
                var delete = IconButton("\uE61D", "删除图层", _deleteAction);
                Grid.SetColumn(dragHandle, 0);
                Grid.SetColumn(eye, 2);
                Grid.SetColumn(lockButton, 3);
                Grid.SetColumn(delete, 4);
                grid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto");
                grid.Children.Add(dragHandle);
                grid.Children.Add(eye);
                grid.Children.Add(lockButton);
                grid.Children.Add(delete);
            }

            Child = grid;
        }

        private static Button IconButton(string glyph, string tooltip, Action? action)
        {
            var button = new Button
            {
                Content = new IconText { Glyph = glyph, Text = string.Empty },
                Padding = new Thickness(7, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            button.IsEnabled = action != null;
            if (action != null)
            {
                button.Click += (_, _) => action();
            }

            return button;
        }
    }

    /// <summary>检查器用 NumericUpDown（必须把 StyleKey 指回基类，否则 FAUI 隐式主题找不到导致不可见）。</summary>
    private sealed class EditorSpin : NumericUpDown
    {
        protected override Type StyleKeyOverride => typeof(NumericUpDown);

        public EditorSpin(double minimum, double maximum, double increment, string format)
        {
            Minimum = (decimal)minimum;
            Maximum = (decimal)maximum;
            Increment = (decimal)increment;
            FormatString = format;
            Value = (decimal)minimum;
            Width = 140;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalContentAlignment = HorizontalAlignment.Right;
        }

        public double DoubleValue
        {
            get => (double)(Value ?? 0);
            set => Value = (decimal)Math.Clamp(value, (double)Minimum, (double)Maximum);
        }
    }
}
