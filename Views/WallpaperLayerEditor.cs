using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// Photoshop 风格底图图层编辑器。
/// 概念：
/// - 画布 = 一层「ClassIsland 岛屿」（默认锁定，解锁后可拖动边缘模拟岛屿长度变化）
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
    /// <summary>自定义尺寸的两行（铺满岛屿关闭时显示）。</summary>
    private Control _widthItem = null!;
    private Control _heightItem = null!;
    /// <summary>SMTC 模式行（仅选中 SMTC 图层时显示）。</summary>
    private Control _smtcModeItem = null!;
    /// <summary>显示方式行（仅位图图层显示）。</summary>
    private Control _displayModeItem = null!;
    // 形状图层检查器
    private readonly ComboBox _shapeTypeBox = new() { MinWidth = 140 };
    private readonly ColorPicker _shapeFillPicker = ColorPicker();
    private readonly ColorPicker _shapeStrokePicker = ColorPicker();
    private readonly EditorSpin _shapeStrokeSpin = new(0, 40, 0.25, "0.##");
    private Control _shapeTypeItem = null!;
    private Control _shapeFillItem = null!;
    private Control _shapeStrokeItem = null!;
    private Control _shapeStrokeWidthItem = null!;
    // 文本图层检查器
    private readonly TextBox _textBox = new() { MaxWidth = 200, Watermark = "文本内容" };
    private readonly EditorSpin _textFontSizeSpin = new(6, 200, 1, "0");
    private readonly ComboBox _textFontFamilyBox = new() { MinWidth = 140, MaxDropDownHeight = 360 };
    private readonly ColorPicker _textColorPicker = ColorPicker();
    private readonly ToggleSwitch _textBoldToggle = new() { OnContent = "开", OffContent = "关" };
    private readonly ComboBox _textAlignBox = new() { MinWidth = 140 };
    private Control _textItem = null!;
    private Control _textFontSizeItem = null!;
    private Control _textFontFamilyItem = null!;
    private Control _textColorItem = null!;
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
    /// <summary>命令栏撤销/重做按钮（按栈状态启停）。</summary>
    private CommandBarButton _undoButton = null!;
    private CommandBarButton _redoButton = null!;
    /// <summary>拖拽排序：独立置顶的「幽灵快照」预览窗口（参考「主界面 → 组件」拖拽）。</summary>
    private Window? _dragPreviewWindow;
    private Border? _dragPreviewHost;
    /// <summary>拖拽排序：指针在源行内的抓取偏移（屏幕像素）。</summary>
    private Point _reorderGrabOffset;
    /// <summary>左侧工具栏按钮（按工具选中态更新）。</summary>
    private readonly Dictionary<WallpaperEditorTool, Button> _toolButtons = [];

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

        WireCanvas();
        BuildContent();
        RefreshLayerList();
        RefreshInspector();
        UpdateStatus();
        Closing += OnClosingConfirm;
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
        };
        _canvas.SelectionChanged += () =>
        {
            RefreshLayerList();
            RefreshInspector();
            UpdateStatus();
        };
        _canvas.IslandChanged += () =>
        {
            RefreshInspector();
            UpdateStatus();
        };
        _canvas.ImagesChanged += RefreshLayerList;
        _canvas.DeleteRequested += DeleteLayer;
        _canvas.ToolChanged += _ => UpdateToolBarSelection();
    }

    private void BuildContent()
    {
        // ---- 顶部命令栏（参考 ClassIsland 档案编辑窗口的 CommandBar）----
        // 层级不再用下拉框：改为直接拖拽图层面板里的「背景图层」行调整（顶部 = 底色之后，底部 = 底色之上）。
        _undoButton = CommandButton("\uE195", "撤销", "撤销上一步操作", Undo);
        _redoButton = CommandButton("\uE121", "重做", "重做已撤销的操作", Redo);
        _undoButton.IsEnabled = false;
        _redoButton.IsEnabled = false;
        var commandBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            PrimaryCommands =
            {
                CommandButton("\uE9B4", "添加图片图层", "选择一张图片作为新的底图图层", AddImageLayer),
                new CommandBarSeparator(),
                _undoButton,
                _redoButton,
                new CommandBarSeparator(),
                new CommandBarElementContainer
                {
                    Content = new IconText
                    {
                        Glyph = "\uF42D",
                        Text = "底图图层编辑器",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 0)
                    }
                },
                new CommandBarSeparator(),
                CommandButton("\uE62F", "重置岛屿尺寸", "把岛屿预览尺寸恢复为 ClassIsland 实际尺寸", ResetIslandSize),
                CommandButton("\uEEB5", "保存并应用", "保存图层并应用到主界面", Save)
            }
        };

        // ---- 右侧：图层面板 + 检查器（原生设置项平铺，不包裹卡片）----
        var layerListHost = new Grid { Children = { _layerStack, _reorderIndicator } };
        var layersTitle = SectionTitle("\uEA2F", "图层");
        var inspector = BuildInspector();
        var rightColumn = new ScrollViewer
        {
            Background = ThemePalette.PanelBackground(),
            BorderBrush = ThemePalette.SurfaceBorder(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Content = new StackPanel
            {
                Classes = { "settings-container" },
                Spacing = 4,
                Children =
                {
                    layersTitle,
                    new ScrollViewer { MaxHeight = 260, Content = layerListHost },
                    new Separator { Margin = new Thickness(0, 8, 0, 8) },
                    inspector
                }
            }
        };

        // 左侧工具栏（Photoshop 式）+ 舞台 + 右侧设置区之间加垂直分割手柄，可左右拖动调整宽度。
        var toolbar = new Border
        {
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemePalette.PanelBackground(),
            Child = BuildToolBar()
        };
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
            ColumnSpacing = 10,
            Children = { toolbar, stageHost, columnSplitter, rightColumn }
        };
        Grid.SetColumn(toolbar, 0);
        Grid.SetColumn(stageHost, 1);
        Grid.SetColumn(columnSplitter, 2);
        Grid.SetColumn(rightColumn, 3);

        _contentGrid = new Grid
        {
            Margin = new Thickness(12, 4, 12, 12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
            Children = { commandBar, body }
        };
        Grid.SetRow(body, 1);
        Content = _contentGrid;
        UpdateToolBarSelection();
    }

    // ============ 左侧工具栏（Photoshop 式）============

    /// <summary>构建左侧纵向工具栏：移动 / 选择 / 缩放 / 形状 / 文本。</summary>
    private Control BuildToolBar()
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(ToolButton(WallpaperEditorTool.Move, "\uE113", "移动工具（V）：拖拽图层移动，拖动空白取消选中"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Select, "\uE5BE", "选择工具（S）：点击只选中图层，不拖拽"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Zoom, "\uF4D0", "缩放工具（Z）：单击放大 / Alt+单击缩小 / 拖拽框选放大；Ctrl + / Ctrl - 也可缩放"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Shape, "\uE774", "形状工具（U）：拖拽绘制矩形；创建后可在右侧修改形状类型"));
        panel.Children.Add(ToolButton(WallpaperEditorTool.Text, "\uF1BD", "文本工具（T）：点击插入文本框图层"));
        panel.Children.Add(new Separator { Margin = new Thickness(2, 5) });
        panel.Children.Add(ToolActionButton("\uEBCA", "添加 SMTC 图层", "把当前播放的专辑封面作为新的底图图层（无播放时显示占位封面）", AddSmtcLayer));
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

    /// <summary>按当前工具刷新工具栏按钮的选中态（强调色底 + 白色图标）。</summary>
    private void UpdateToolBarSelection()
    {
        foreach (var (tool, button) in _toolButtons)
        {
            var active = tool == _canvas.Tool;
            button.Background = active
                ? ThemeBrush("AccentFillColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(150, 0, 120, 212))
                : Brushes.Transparent;
            button.Foreground = active
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(ThemePalette.ForegroundColor());
        }
    }

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
        _textFontFamilyBox.ItemTemplate = new FuncDataTemplate<FontFamily>((font, _) => new TextBlock
        {
            Text = font.Name,
            FontFamily = font,
            Width = 220,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _textFontFamilyBox.SelectedItem = FontFamily.Default;
        _fillIslandToggle.IsChecked = true;

        _nameBox.TextChanged += (_, _) => ApplyToSelected(l => l.Name = _nameBox.Text ?? "底图图层");
        _opacitySlider.ValueChanged += (_, _) => ApplyToSelected(l => l.Opacity = _opacitySlider.Value);
        _displayModeBox.SelectionChanged += (_, _) => ApplyToSelected(l => l.DisplayMode = Selected(_displayModeBox, WallpaperDisplayMode.Fill));
        _shapeTypeBox.SelectionChanged += (_, _) => ApplyToSelected(l =>
        {
            if (l.Kind == WallpaperLayerKind.Shape)
            {
                l.ShapeType = Selected(_shapeTypeBox, WallpaperShapeType.Rectangle);
            }
        });
        _shapeFillPicker.PropertyChanged += (_, e) =>
        {
            if (!_updatingInspector && e.Property?.Name == "Color")
            {
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Shape) l.FillColor = _shapeFillPicker.Color.ToString(); });
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
                ApplyToSelected(l => { if (l.Kind == WallpaperLayerKind.Text) l.TextColor = _textColorPicker.Color.ToString(); });
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
            // 默认处理：强制铺满岛屿（不可自定义尺寸/位移）。
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

        // 对齐按钮（图标）：横向 左/中/右，纵向 顶/中/底
        var alignXRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var alignYRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        alignXRow.Children.Add(AlignIconButton("\uE03B", "左对齐", () => SetAnchor(0, null)));
        alignXRow.Children.Add(AlignIconButton("\uE033", "水平居中", () => SetAnchor(1, null)));
        alignXRow.Children.Add(AlignIconButton("\uE03D", "右对齐", () => SetAnchor(2, null)));
        alignYRow.Children.Add(AlignIconButton("\uE057", "顶对齐", () => SetAnchor(null, 0)));
        alignYRow.Children.Add(AlignIconButton("\uE035", "垂直居中", () => SetAnchor(null, 1)));
        alignYRow.Children.Add(AlignIconButton("\uE031", "底对齐", () => SetAnchor(null, 2)));

        // 设置项平铺：小标题分组，不包裹卡片。
        var inspector = new StackPanel { Spacing = 4 };
        inspector.Children.Add(SectionTitle("\uEF27", "属性"));
        inspector.Children.Add(GroupSubtitle("\uE9B2", "图层"));
        inspector.Children.Add(SettingsRow("名称", _nameBox));
        inspector.Children.Add(GroupSubtitle("\uEC4A", "外观"));
        _smtcModeItem = SettingsRow("SMTC 模式", _smtcModeBox);
        inspector.Children.Add(_smtcModeItem);
        inspector.Children.Add(SettingsRow("不透明度", _opacitySlider));
        _displayModeItem = SettingsRow("显示方式", _displayModeBox);
        inspector.Children.Add(_displayModeItem);
        // 形状图层专属（仅选中形状图层时显示）
        _shapeTypeItem = SettingsRow("形状类型", _shapeTypeBox);
        _shapeFillItem = SettingsRow("填充色", _shapeFillPicker);
        _shapeStrokeItem = SettingsRow("描边色", _shapeStrokePicker);
        _shapeStrokeWidthItem = SettingsRow("描边粗细", _shapeStrokeSpin);
        inspector.Children.Add(_shapeTypeItem);
        inspector.Children.Add(_shapeFillItem);
        inspector.Children.Add(_shapeStrokeItem);
        inspector.Children.Add(_shapeStrokeWidthItem);
        // 文本图层专属（仅选中文本图层时显示）
        _textItem = SettingsRow("文本内容", _textBox);
        _textFontSizeItem = SettingsRow("字号", _textFontSizeSpin);
        _textFontFamilyItem = SettingsRow("字体", _textFontFamilyBox);
        _textColorItem = SettingsRow("文字颜色", _textColorPicker);
        _textBoldItem = SettingsRow("加粗", _textBoldToggle);
        _textAlignItem = SettingsRow("水平对齐", _textAlignBox);
        inspector.Children.Add(_textItem);
        inspector.Children.Add(_textFontSizeItem);
        inspector.Children.Add(_textFontFamilyItem);
        inspector.Children.Add(_textColorItem);
        inspector.Children.Add(_textBoldItem);
        inspector.Children.Add(_textAlignItem);
        _widthItem = SettingsRow("宽度 (px)", _widthSpin);
        _heightItem = SettingsRow("高度 (px)", _heightSpin);
        inspector.Children.Add(GroupSubtitle("\uE27E", "尺寸"));
        inspector.Children.Add(SettingsRow("铺满岛屿", _fillIslandToggle));
        inspector.Children.Add(_widthItem);
        inspector.Children.Add(_heightItem);
        inspector.Children.Add(GroupSubtitle("\uEEA5", "旋转"));
        inspector.Children.Add(SettingsRow("角度 (°)", _rotationSpin));
        inspector.Children.Add(GroupSubtitle("\uE113", "相对定位"));
        inspector.Children.Add(SettingsRow("锚点", _anchorPicker));
        inspector.Children.Add(SettingsRow("水平偏移 (px)", _offsetXSpin));
        inspector.Children.Add(SettingsRow("垂直偏移 (px)", _offsetYSpin));
        inspector.Children.Add(_relativeHint);
        inspector.Children.Add(SettingsRow("水平对齐", alignXRow));
        inspector.Children.Add(SettingsRow("垂直对齐", alignYRow));
        inspector.Children.Add(SettingsRow("重置变换", Button("重置变换", ResetLayerTransform)));
        return inspector;
    }

    /// <summary>设置分组小标题（不包裹卡片）。</summary>
    private static IconText GroupSubtitle(string glyph, string text) => new()
    {
        Glyph = glyph,
        Text = text,
        Margin = new Thickness(0, 8, 0, 2),
        Opacity = 0.85
    };

    /// <summary>设置项行：左侧标签 + 右侧控件（平铺，无卡片）。</summary>
    private static Control SettingsRow(string label, Control footer)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 30,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 },
                footer
            }
        };
        Grid.SetColumn(footer, 1);
        return row;
    }

    /// <summary>对齐图标按钮。</summary>
    private static Button AlignIconButton(string glyph, string tooltip, Action action)
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

    /// <summary>把选中图层水平/垂直对齐到岛屿对应参考点（等价于把锚点设为对应值并清零偏移）。</summary>
    private void SetAnchor(int? xIndex, int? yIndex)
    {
        ApplyToSelected(l =>
        {
            if (xIndex is { } xi)
            {
                l.AnchorX = xi switch { 0 => WallpaperLayerAnchorX.Left, 1 => WallpaperLayerAnchorX.Center, _ => WallpaperLayerAnchorX.Right };
                l.OffsetX = 0;
            }

            if (yIndex is { } yi)
            {
                l.AnchorY = yi switch { 0 => WallpaperLayerAnchorY.Top, 1 => WallpaperLayerAnchorY.Center, _ => WallpaperLayerAnchorY.Bottom };
                l.OffsetY = 0;
            }
        });
        UpdateStatus();
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

    /// <summary>对选中图层应用修改：压入撤销、置脏、刷新画布与检查器。</summary>
    private void ApplyToSelected(Action<WallpaperLayerItem> edit)
    {
        var layer = _canvas.SelectedLayer;
        if (layer == null || _updatingInspector)
        {
            return;
        }

        PushUndo();
        edit(layer);
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

    // ============ 添加图层 / 岛屿重置 ============

    private async void AddImageLayer()
    {
        var topLevel = TopLevel.GetTopLevel(this);
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
        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

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
    }

    /// <summary>添加一个 SMTC 专辑封面图层（无播放时画布显示占位封面 album.png）。</summary>
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

    /// <summary>在岛屿中央添加一个矢量形状图层（可在画布上继续移动 / 调整）。</summary>
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

    /// <summary>在岛屿中央添加一个文本框图层。</summary>
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
        _statusText.Text = "已把岛屿尺寸重置为 ClassIsland 实际尺寸。";
    }

    // ============ 图层面板 ============

    /// <summary>当前正在拖拽排序的图层（非空表示正在拖拽中）。</summary>
    private WallpaperLayerItem? _reorderLayer;
    /// <summary>当前正在拖拽的背景（岛屿）行（与 _reorderLayer 互斥）。</summary>
    private bool _reorderBackground;
    /// <summary>背景行拖拽的目标层级。</summary>
    private WallpaperLayerZOrder _reorderBackgroundTarget;
    /// <summary>拖拽排序：源图层在 _layers 中的索引。</summary>
    private int _reorderSourceIndex;
    /// <summary>拖拽排序：目标插入索引。</summary>
    private int _reorderInsertIndex;
    /// <summary>拖拽排序：插入位置指示线（舞台右上角图层面板内）。</summary>
    private readonly Border _reorderIndicator = new()
    {
        Height = 3,
        CornerRadius = new CornerRadius(1.5),
        Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
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
            Title = "ClassIsland 岛屿",
            Subtitle = "背景图层 · 默认锁定",
            IconGlyph = "\uE62F",
            Unlocked = _canvas.IslandUnlocked,
            Selected = _canvas.SelectedLayer == null
        }.WithHandlers(
            () => _canvas.Select(null),
            null,
            () => ToggleIslandUnlock(),
            null);
        // 背景行可拖拽调整层级：顶部 = 底色之后，底部 = 底色之上、组件之下。
        islandRow.DragHandlePressed += e => BeginBackgroundReorder(islandRow, e);

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
                Subtitle = layer.Kind == WallpaperLayerKind.Image
                    ? $"{DisplayKind(layer)}{SmtcModeSuffix(layer)} · {DisplayModeName(layer.DisplayMode)}"
                    : $"{DisplayKind(layer)}{SmtcModeSuffix(layer)}",
                IconGlyph = layer.Kind switch
                {
                    WallpaperLayerKind.Shape => "\uE774",
                    WallpaperLayerKind.Text => "\uF1BD",
                    _ => layer.Source == WallpaperSource.SmtcAlbum ? "\uE021" : "\uE9B2"
                },
                Visible = layer.Visible,
                Locked = _canvas.IsLocked(layer.Id),
                Selected = _canvas.SelectedLayer == layer,
                Thumbnail = _canvas.GetThumbnail(layer.Id)
            };
            row.WithHandlers(
                () => _canvas.Select(captured.Id),
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
    /// 开始拖拽背景（岛屿）行：放到列表顶部 = 底色之后，放到列表底部 = 底色之上、组件之下。
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
        _canvas.IslandUnlocked = !_canvas.IslandUnlocked;
        RefreshLayerList();
        UpdateStatus();
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
        PushUndo();
        _layers.Remove(layer);
        _dirty = true;
        _canvas.Layers = _layers;
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
            var layer = _canvas.SelectedLayer;
            if (layer == null)
            {
                _nameBox.IsEnabled = false;
                _opacitySlider.IsEnabled = false;
                _displayModeBox.IsEnabled = false;
                _smtcModeBox.IsEnabled = false;
                _smtcModeItem.IsVisible = false;
                _displayModeItem.IsVisible = false;
                _fillIslandToggle.IsEnabled = false;
                _widthSpin.IsEnabled = false;
                _heightSpin.IsEnabled = false;
                _rotationSpin.IsEnabled = false;
                _offsetXSpin.IsEnabled = false;
                _offsetYSpin.IsEnabled = false;
                _anchorPicker.IsEnabled = false;
                _shapeTypeItem.IsVisible = false;
                _shapeFillItem.IsVisible = false;
                _shapeStrokeItem.IsVisible = false;
                _shapeStrokeWidthItem.IsVisible = false;
                _textItem.IsVisible = false;
                _textFontSizeItem.IsVisible = false;
                _textFontFamilyItem.IsVisible = false;
                _textColorItem.IsVisible = false;
                _textBoldItem.IsVisible = false;
                _textAlignItem.IsVisible = false;
                _relativeHint.Text = "未选中图层。点击画布上的图层，或在左侧图层面板选择。";
                RefreshCustomSizePanel();
                return;
            }

            var smtcDefault = IsSmtcDefaultMode(layer);
            var isShape = layer.Kind == WallpaperLayerKind.Shape;
            var isText = layer.Kind == WallpaperLayerKind.Text;
            _nameBox.IsEnabled = true;
            _opacitySlider.IsEnabled = true;
            _displayModeBox.IsEnabled = layer.Kind == WallpaperLayerKind.Image;
            _smtcModeBox.IsEnabled = true;
            _smtcModeItem.IsVisible = layer.Source == WallpaperSource.SmtcAlbum;
            _displayModeItem.IsVisible = layer.Kind == WallpaperLayerKind.Image;
            _shapeTypeItem.IsVisible = isShape;
            _shapeFillItem.IsVisible = isShape;
            _shapeStrokeItem.IsVisible = isShape;
            _shapeStrokeWidthItem.IsVisible = isShape;
            _textItem.IsVisible = isText;
            _textFontSizeItem.IsVisible = isText;
            _textFontFamilyItem.IsVisible = isText;
            _textColorItem.IsVisible = isText;
            _textBoldItem.IsVisible = isText;
            _textAlignItem.IsVisible = isText;
            // 默认处理模式：锁定尺寸/位移/旋转，强制铺满岛屿。
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
            _smtcModeBox.SelectedItem = SmtcModeChoices.FirstOrDefault(c => c.Value == layer.SmtcMode) ?? SmtcModeChoices[0];
            _shapeTypeBox.SelectedItem = ShapeTypeChoices.FirstOrDefault(c => c.Value == layer.ShapeType) ?? ShapeTypeChoices[0];
            _shapeFillPicker.Color = ReadColor(layer.FillColor, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            _shapeStrokePicker.Color = ReadColor(layer.StrokeColor, Colors.White);
            _shapeStrokeSpin.DoubleValue = layer.StrokeThickness;
            _textBox.Text = layer.Text;
            _textFontSizeSpin.DoubleValue = layer.TextFontSize;
            _textFontFamilyBox.SelectedItem = ((IEnumerable<FontFamily>)_textFontFamilyBox.ItemsSource!)
                .FirstOrDefault(font => string.Equals(font.Name, layer.TextFontFamily, StringComparison.CurrentCultureIgnoreCase))
                ?? FontFamily.Default;
            _textColorPicker.Color = ReadColor(layer.TextColor, Colors.White);
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
            _relativeHint.Text = RelativeHintText(layer);
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

    /// <summary>把选中图层的相对位置表达成人类可读的提示，如「右边缘 = 岛屿右边缘 - 16px」。</summary>
    private string RelativeHintText(WallpaperLayerItem layer)
    {
        if (IsSmtcDefaultMode(layer))
        {
            return "当前为 SMTC 图层的默认处理：铺满整个岛屿，仅可调整透明度与显示方式。\n切换为「当作图片处理」后可自由位移、缩放、旋转。";
        }

        if (layer.SizeMode == WallpaperLayerSizeMode.FillIsland)
        {
            return "当前图层铺满整个岛屿，随岛屿尺寸自适应。拖动手柄或旋转后会切换为自定义尺寸。";
        }

        var xText = layer.AnchorX switch
        {
            WallpaperLayerAnchorX.Left => $"左边缘 = 岛屿左边缘 {OffsetText(layer.OffsetX)}",
            WallpaperLayerAnchorX.Center => $"中心 = 岛屿中心 {OffsetText(layer.OffsetX)}",
            WallpaperLayerAnchorX.Right => $"右边缘 = 岛屿右边缘 {OffsetText(layer.OffsetX)}",
            _ => string.Empty
        };
        var yText = layer.AnchorY switch
        {
            WallpaperLayerAnchorY.Top => $"上边缘 = 岛屿上边缘 {OffsetText(layer.OffsetY)}",
            WallpaperLayerAnchorY.Center => $"垂直中心 = 岛屿垂直中心 {OffsetText(layer.OffsetY)}",
            WallpaperLayerAnchorY.Bottom => $"下边缘 = 岛屿下边缘 {OffsetText(layer.OffsetY)}",
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
        var islandPart = $"岛屿 {_canvas.IslandWidth:0} × {_canvas.IslandHeight:0}";
        var unlockPart = _canvas.IslandUnlocked
            ? "· 岛屿已解锁：拖动右/下边缘可模拟 ClassIsland 长度变化，观察底图自适应"
            : "· 在右侧图层面板解锁岛屿后可拖动边缘测试自适应";
        var selectedPart = _canvas.SelectedLayer == null ? string.Empty : $"· 已选「{_canvas.SelectedLayer.Name}」";
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
        var result = await dialog.ShowAsync();
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
        WallpaperShapeType.Ellipse => "椭圆",
        WallpaperShapeType.Line => "直线",
        WallpaperShapeType.Triangle => "三角形",
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
        new(WallpaperLayerSmtcMode.Default, "默认处理（铺满岛屿）"),
    ];

    private static readonly Pick<WallpaperShapeType>[] ShapeTypeChoices =
    [
        new(WallpaperShapeType.Rectangle, "矩形"),
        new(WallpaperShapeType.Ellipse, "椭圆"),
        new(WallpaperShapeType.Line, "直线"),
        new(WallpaperShapeType.Triangle, "三角形"),
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
        private Action? _select;
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

        public LayerRowControl WithHandlers(Action? select, Action? visibility, Action? lockAction, Action? delete)
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
            BorderThickness = new Thickness(1);
            Padding = new Thickness(8, 6);

            // 原生风格：透明底 + 悬停微高亮 + 选中强调色（不手搓深色卡片）。
            void ApplyBackground(bool hover)
            {
                Background = Selected
                    ? new SolidColorBrush(Color.FromArgb(70, 0, 120, 212))
                    : hover
                        ? ThemePalette.SubtleFill()
                        : Brushes.Transparent;
                BorderBrush = Selected
                    ? new SolidColorBrush(Color.FromArgb(180, 0, 120, 212))
                    : Brushes.Transparent;
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

            var label = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = Title, FontWeight = FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = Subtitle, FontSize = 11, Opacity = 0.6 }
                }
            };
            var contentArea = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Cursor = new Cursor(StandardCursorType.Hand),
                Children = { preview, label }
            };
            contentArea.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(contentArea).Properties.IsLeftButtonPressed)
                {
                    _select?.Invoke();
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
                // 岛屿行：左侧拖拽手柄调整背景层级（顶部 = 底色之后，底部 = 底色之上、组件之下）；
                // 右侧为「解锁岛屿」按钮（眼睛不可用，岛屿始终可见）。
                var dragHandle = new Border
                {
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new IconText
                    {
                        Glyph = "\uE771",
                        Text = string.Empty,
                        Opacity = 0.45,
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
                    Unlocked ? "锁定岛屿" : "解锁岛屿（可拖动边缘测试自适应）", _lockAction);
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
                        Glyph = "\uE771",
                        Text = string.Empty,
                        Opacity = 0.45,
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
