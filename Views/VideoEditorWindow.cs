using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
// Shapes 命名空间与 System.IO.Path 冲突，只取需要的类型。
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
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

    /// <summary>预览解码降采样上限（舞台显示尺寸远小于此；1280 时 UI 写帧吃力会导致预览丢帧卡顿）。</summary>
    private const int PreviewMaxDimension = 800;
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
    /// <summary>seek 帧解码请求代次（每次播放头变化递增，只有最新代次才应用）。</summary>
    private int _seekFrameGen;
    /// <summary>seek 帧解码 worker 是否忙（忙时只记录最新 pending 时间，结束后再解最新一帧）。</summary>
    private bool _seekFrameBusy;
    private double _seekFramePendingTime;
    private bool _seekFrameHasPending;
    /// <summary>拖动片段块的拖拽状态（块跟手实时移动，释放时落轨；GrabX/Y = 按下点相对块偏移）。</summary>
    private (VideoClip Clip, double GrabX, double GrabY, double OriginalStartTime)? _moveDrag;
    /// <summary>时间轴泳道列表（拖拽时高亮目标轨道用，RefreshTimeline 重建）。</summary>
    private readonly List<Border> _lanes = [];
    /// <summary>「新建轨道」拖放区（拖拽时高亮用）。</summary>
    private Border? _newTrackZone;
    /// <summary>时间轴 ScrollViewer 可视宽度（时间轴内容至少铺满，短工程也能拖到最开头）。</summary>
    private double _timelineViewportWidth = 800;
    /// <summary>时间轴宽度变化防抖重建。</summary>
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // ---- 素材库 ----
    private readonly ListBox _assetList = new() { MinHeight = 120 };
    /// <summary>素材右键菜单（删除 = 移除时间轴引用 + 从素材库删除；查看媒体信息）。Opening 时按选中重建项。</summary>
    private readonly MenuFlyout _assetMenu = new();
    private readonly CommandBarButton _addAssetButton = new() { IconSource = new FluentIconSource("\uF3D1"), Label = "添加素材" };
    private readonly CommandBarButton _addToTimelineButton = new() { IconSource = new FluentIconSource("\uE0F7"), Label = "添加到时间轴", IsEnabled = false };
    /// <summary>素材封面视图（缩略图网格，可切换；列表 ⇄ 封面）。</summary>
    private readonly ScrollViewer _assetCoverScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
    private readonly WrapPanel _assetCoverPanel = new();
    /// <summary>当前素材库视图：false=列表，true=封面。</summary>
    private bool _assetCoverView;
    /// <summary>封面缩略图缓存（路径 → 位图）。</summary>
    private readonly Dictionary<string, Bitmap> _assetThumbs = [];
    /// <summary>素材库视图切换按钮（列表 ⇄ 封面）。</summary>
    private readonly Button _assetViewToggle = new()
    {
        Content = new IconText { Glyph = "\uE929", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 28,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    /// <summary>内部剪贴板片段（Ctrl+C 复制 / Ctrl+V 粘贴到播放头）。</summary>
    private VideoClip? _clipboard;
    /// <summary>素材库标题（随工具切换：素材库 / 形状库 / 滤镜库）。</summary>
    private readonly TextBlock _libraryTitle = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>形状/滤镜库视图：true = 卡片缩略图，false = 紧凑列表。</summary>
    private bool _libraryCardsView = true;
    /// <summary>库视图切换按钮（仅形状/滤镜库模式显示）。</summary>
    private Button _libraryViewToggle = new();
    /// <summary>形状/滤镜卡片面板（库内容随工具切换：形状工具→形状卡片，效果工具→滤镜卡片）。</summary>
    private readonly ScrollViewer _libraryScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
    private readonly WrapPanel _libraryPanel = new();
    /// <summary>当前选中的形状（形状工具下：库点击 / 舞台点击放置用）。</summary>
    private string _currentShape = "Rect";
    /// <summary>形状定义（键 → 显示名）。</summary>
    private static readonly (string Key, string Name)[] ShapeDefs =
    [
        ("Rect", "矩形"),
        ("Ellipse", "椭圆"),
        ("Triangle", "三角形"),
        ("Diamond", "菱形"),
        ("Star", "五角星"),
        ("Heart", "心形"),
        ("ArrowRight", "右箭头"),
        ("Pentagon", "五边形"),
        ("Ring", "圆环"),
        ("Cross", "十字")
    ];
    /// <summary>滤镜定义（键 → 显示名）。</summary>
    private static readonly (string Key, string Name)[] FilterDefs =
    [
        ("Grayscale", "灰度"),
        ("Sepia", "棕褐色"),
        ("Invert", "反色"),
        ("Brighten", "变亮"),
        ("Darken", "变暗"),
        ("Mosaic", "马赛克")
    ];

    // ---- 舞台 ----
    private readonly Border _stageBorder = new() { ClipToBounds = true, Background = Brushes.Black };
    private readonly Grid _stageHostGrid = new();
    private readonly List<StageTrackLayer> _stageLayers = [];
    /// <summary>舞台八向手柄覆盖层（选中片段时显示，仿底图图层编辑器）。</summary>
    private readonly Canvas _stageHandleOverlay = new() { IsHitTestVisible = true, ZIndex = 40 };
    /// <summary>磁吸基准线覆盖层（移动/缩放对齐时显示对齐参考线，ZIndex 低于手柄）。</summary>
    private readonly Canvas _stageGuides = new() { IsHitTestVisible = false, ZIndex = 35, IsVisible = false };
    private readonly Avalonia.Controls.Shapes.Line _guideV = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0xC2)),
        StrokeThickness = 1,
        StrokeDashArray = [4, 3]
    };
    private readonly Avalonia.Controls.Shapes.Line _guideH = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF)),
        StrokeThickness = 1,
        StrokeDashArray = [4, 3]
    };
    /// <summary>磁吸参考点（拖动开始时冻结）：其它片段显示中心 + 舞台边缘/中心。</summary>
    private readonly List<double> _magnetRefsX = [];
    private readonly List<double> _magnetRefsY = [];
    /// <summary>磁吸半径（舞台像素）。</summary>
    private const double MagnetThreshold = 6;
    private readonly Border[] _handles = new Border[8];
    private Border _handleOutline = null!;
    private int _resizeHandle = -1;
    private Point _resizeStart;
    private Rect _resizeStartRect;
    /// <summary>按下时捕获的基准尺寸（拖动中 clip.Scale 会变，基准必须冻结在按下瞬间）。</summary>
    private double _resizeBaseW;
    private double _resizeBaseH;
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
    private readonly EditorSpin _scaleXSpin = new(0.05, 10, 0.01, "0.##");
    private readonly EditorSpin _scaleYSpin = new(0.05, 10, 0.01, "0.##");
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
    /// <summary>时间轴像素/秒（横向缩放，可调 2..60）。</summary>
    private double _pxPerSecond = 6;
    /// <summary>时间轴顶部标尺高度（与轨道头对齐）。</summary>
    private const double RulerHeight = 22;
    /// <summary>时间轴工具条近似高度（用于分割条拖拽时保证内容可见）。</summary>
    private const double TimelineToolbarHeight = 32;
    /// <summary>轨道泳道高度（可调，40..140；默认 60）。</summary>
    private double _laneHeight = 60;
    /// <summary>末轨下方「新建轨道」拖放区高度。</summary>
    private const double NewTrackZoneHeight = 36;
    /// <summary>每轨独立高度（轨道头底缘拖拽调整；未设置时用 _laneHeight 滑块默认值）。</summary>
    private readonly Dictionary<int, double> _laneHeights = [];
    private double LaneHeightOf(int track) => _laneHeights.TryGetValue(track, out var h) ? h : _laneHeight;
    /// <summary>轨道头控件按数据轨号索引（视觉顺序反转后仍按轨号取）。</summary>
    private readonly Dictionary<int, Border> _headerByTrack = [];
    /// <summary>泳道区顶部固定「新建最上层轨道」拖放区（标尺下方）。</summary>
    private Border? _topTrackZone;
    /// <summary>正在拖拽调整高度的轨道号（-1 = 无）。</summary>
    private int _resizeLane = -1;
    private double _resizeLaneStartY;
    private double _resizeLaneStartH;
    private readonly TextBlock _clipCountText = new() { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8, FontSize = 12, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>时间轴工具条上的输出尺寸文本（画幅按钮可改）。</summary>
    private readonly TextBlock _outputSizeText = new()
    {
        FontSize = 12,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 0, 0)
    };
    /// <summary>工程自动保存防抖计时器（编辑后延迟写盘，重开不丢）。</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // ---- 面板尺寸 / 分割条 ----
    private double _assetPanelWidth = 240;
    private double _inspectorWidth = 300;
    /// <summary>时间轴面板高度（0 = 自动按内容；拖分割条后为固定值）。</summary>
    private double _timelinePanelHeight;
    /// <summary>时间轴内容 + 边框所需的最小面板高度（RefreshTimeline 记录，供分割条下限）。</summary>
    private double _timelineChromeHeight;
    private Grid _rootGrid = null!;

    // ---- 撤销/重做 ----
    private readonly List<VideoProject> _undoStack = [];
    private readonly List<VideoProject> _redoStack = [];
    private const int MaxUndoDepth = 100;
    private DateTime _lastUndoPush;
    /// <summary>上一次压栈是否为合并操作（连续数值调整合并为一步撤销）。</summary>
    private bool _lastPushCoalesced;
    private bool _moveUndoPushed;
    private bool _trimUndoPushed;
    private bool _resizeUndoPushed;
    private CommandBarButton _undoButton = null!;
    private CommandBarButton _redoButton = null!;
    private CommandBarButton _copyButton = null!;
    private CommandBarButton _pasteButton = null!;

    // ---- 时间轴标尺/缩放 ----
    private readonly Button _zoomOutButton = new()
    {
        Content = new IconText { Glyph = "\uF4D2", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 24,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    private readonly Button _zoomInButton = new()
    {
        Content = new IconText { Glyph = "\uF4D0", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 24,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    private readonly TextBlock _zoomText = new()
    {
        FontSize = 11,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 40,
        TextAlignment = TextAlignment.Center
    };
    /// <summary>时间轴工具条上的轨道高度滑块（调整泳道高度，立即重建时间轴）。</summary>
    private readonly Slider _laneHeightSlider = new()
    {
        Minimum = 40,
        Maximum = 140,
        Value = 60,
        Width = 84,
        VerticalAlignment = VerticalAlignment.Center,
        IsSnapToTickEnabled = true,
        TickFrequency = 4
    };
    private readonly TextBlock _laneHeightText = new()
    {
        FontSize = 11,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 36
    };

    // ---- 左侧工具栏（Photoshop 式：选中高亮滑块 + 动画，仿底图图层编辑器）----
    /// <summary>当前工具：select / text / shape / effect。</summary>
    private string _currentTool = "select";
    private readonly Dictionary<string, Button> _toolButtons = [];
    private StackPanel _toolPanel = null!;
    private Grid _toolHighlightHost = null!;
    private Border _toolHighlight = null!;
    private bool _toolHighlightPositioned;
    private readonly Dictionary<string, bool> _toolHovered = [];
    /// <summary>工具栏图标（FluentSystemIcons filled/regular 成对）。</summary>
    private static readonly Dictionary<string, (string Filled, string Regular)> ToolGlyphs = new()
    {
        ["select"] = ("\uE5BE", "\uE5BF"),
        ["text"] = ("\uF1BD", "\uF1BE"),
        ["shape"] = ("\uE774", "\uE775"),
        ["effect"] = ("\uF42E", "\uF42F")
    };

    // ---- 文本/形状覆盖层属性 ----
    private readonly TextBox _overlayText = new() { MinWidth = 140, Watermark = "文本内容" };
    private readonly TextBox _overlayColor = new() { MinWidth = 100, Watermark = "#AARRGGBB" };
    private readonly ComboBox _overlayShape = new() { MinWidth = 110 };
    private readonly StackPanel _overlayPanel = new() { Spacing = 4, IsVisible = false };
    /// <summary>覆盖层编辑区的三行（内容/颜色/形状），按片段类型显隐（图片只留通用变换）。</summary>
    private Control? _overlayTextRow;
    private Control? _overlayColorRow;
    private Control? _overlayShapeRow;
    // ---- 滤镜片段属性 ----
    private readonly ComboBox _filterCombo = new() { MinWidth = 110 };
    private readonly StackPanel _filterPanel = new() { Spacing = 4, IsVisible = false };
    /// <summary>检查器 Tab 选项卡（变换/覆盖层/滤镜三页分组）。</summary>
    private TabControl _inspectorTabs = null!;
    private TabItem _transformTab = null!;
    private TabItem _overlayTab = null!;
    private TabItem _filterTab = null!;
    /// <summary>上次检查器展示的片段（选中变化时才自动切 Tab）。</summary>
    private VideoClip? _lastInspectorClip;

    // ---- 传输控制 / 播放头 ----
    /// <summary>舞台内底部播放/暂停按钮（纯图标，悬停提示；无文字标签、无停止按钮）。</summary>
    private readonly Button _playButton = new()
    {
        Content = new IconText { Glyph = "\uEDB9", Text = "" },
        Padding = new Thickness(8, 3),
        MinWidth = 32,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand)
    };
    /// <summary>时间轴上方工具条：刀片切割 / 删除片段（紧凑图标按钮，悬停显示说明）。</summary>
    private readonly Button _cutButton = new()
    {
        Content = new IconText { Glyph = "\uE5C9", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 28,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    private readonly Button _deleteButton = new()
    {
        Content = new IconText { Glyph = "\uE61D", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 28,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    /// <summary>时间轴上方工具条：自定义画幅（输出宽高）。</summary>
    private readonly Button _canvasButton = new()
    {
        Content = new IconText { Glyph = "\uE0EC", Text = "" },
        Padding = new Thickness(6, 3),
        MinWidth = 28,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    /// <summary>拖动片段块时按下点相对块左边缘的偏移（Drop 时减去，块跟手才能头贴尾拼接）。</summary>
    private double _dragOffsetX;
    /// <summary>拖拽中最后指针位置（相对时间轴滚动视口），驱动边缘自动滚动。</summary>
    private Point _lastDragPointer;
    /// <summary>拖拽自动滚动探测委托（非空 = 计时器运行中）。</summary>
    private Func<(double Dx, double Dy)>? _dragScrollProbe;
    /// <summary>拖拽自动滚动计时器：指针贴近视口边缘时持续滚动时间轴（横向到末尾、纵向到新建轨道区）。</summary>
    private readonly DispatcherTimer _dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    /// <summary>拖拽落点提示标签（accent 底白字「轨道 N / 新建轨道」，跟随目标泳道移动，不被拖拽块遮挡）。</summary>
    private Border? _dropTrackBadge;
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
    /// <summary>时间轴横向滚动容器（刷新时重置偏移，避免拖拽引起的滚动导致泳道/轨道头错位）。</summary>
    private ScrollViewer _timelineScroll = null!;
    /// <summary>固定标尺宿主（在滚动区上方，不随纵向滚动走）；标尺随内容横向滚动经 _rulerTranslate 同步。</summary>
    private Border _rulerHost = null!;
    private readonly TranslateTransform _rulerTranslate = new();
    /// <summary>标尺画布（RefreshTimeline 重建标尺内容，宽度 = 时间轴内容宽）。</summary>
    private readonly Canvas _rulerCanvas = new();
    /// <summary>素材时长缓存（裁剪右边界上限）。</summary>
    private readonly Dictionary<string, double> _assetDurations = [];
    /// <summary>seek 解码持久源缓存（轨 → 片段+源+帧号）；scrub 期间复用，让拖动即时显示帧。</summary>
    private readonly Dictionary<int, SeekSourceState> _seekSources = [];

    /// <summary>scrub 解码源状态：同片段复用解码器并跟踪已读帧号，微移用顺序读帧代替反复 seek。</summary>
    private sealed class SeekSourceState
    {
        public required VideoClip Clip { get; init; }
        public required VideoFrameSource Source { get; init; }
        /// <summary>当前已读帧号（相对片段入点，按源帧率换算）；-1 = 未定位。</summary>
        public long LastFrameIndex = -1;
    }

    /// <summary>舞台多轨预览图层：一轨一个 Image + 复用位图。</summary>
    private sealed class StageTrackLayer
    {
        public required int Track { get; init; }
        public required Image Image { get; init; }
        /// <summary>字段（非属性）：需以 ref 传给位图写入助手。</summary>
        public WriteableBitmap? Bitmap;
        /// <summary>上次应用的变换签名（ApplyTransform 每帧调用，未变化时跳过重建 TransformGroup）。</summary>
        public (double W, double H, double SX, double SY, double Rot, double OX, double OY) LastTransformA;
        public (double OP, double CL, double CT, double CR, double CB) LastTransformB;
        /// <summary>是否已有签名（首个入参默认全 0 会误判命中）。</summary>
        public bool HasTransform;
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
        _stageGuides.Children.Add(_guideV);
        _stageGuides.Children.Add(_guideH);
        _stageHostGrid.Children.Add(_stageGuides);
        BuildStageHandles();
        // 素材选中变化时更新「添加到时间轴」可用性（之前仅刷新列表时更新，导致选中后仍置灰）。
        _assetList.SelectionChanged += (_, _) => UpdateAssetButtons();
        // 右键素材：先选中命中的行，再弹菜单（Opening 时按选中重建菜单项，删除/查看信息）。
        _assetList.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_assetList).Properties.IsRightButtonPressed)
            {
                SelectAssetAt(_assetList, e);
            }
        };
        _assetList.ContextFlyout = _assetMenu;
        _assetMenu.Opening += (_, _) =>
        {
            var path = SelectedAssetPath;
            _assetMenu.Items.Clear();
            if (path == null)
            {
                return;
            }

            var info = new MenuItem { Header = "查看媒体信息" };
            info.Click += (_, _) => _ = ShowAssetInfoAsync(path);
            var remove = new MenuItem { Header = "删除" };
            remove.Click += (_, _) => _ = RemoveAssetAsync(path);
            _assetMenu.Items.Add(info);
            _assetMenu.Items.Add(remove);
        };
        // 快捷键：空格 = 播放/暂停，Delete/Backspace = 删除选中片段，Ctrl+Z/Y = 撤销/重做，
        // Ctrl+C/V = 复制/粘贴片段（焦点在文本输入框时不拦截）。
        KeyDown += (_, e) =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            {
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (e.Key == Key.Z)
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }

                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Y)
                {
                    Redo();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.C)
                {
                    CopySelectedClip();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.V)
                {
                    PasteClip();
                    e.Handled = true;
                    return;
                }
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
        // 工程自动保存：任何修改后延迟写盘（重开编辑器不丢对齐/轨道/变换），关闭时立即保存。
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveProject();
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
                SetPlayhead(Math.Max(0, e.GetPosition(_timelineRoot).X / _pxPerSecond));
            }
        };
        _playhead.PointerReleased += (_, e) =>
        {
            if (_scrubbing)
            {
                _scrubbing = false;
                e.Pointer.Capture(null);
                // 没有在途解码时立即释放持久 seek 源；否则由解码收尾释放。
                if (!_seekFrameBusy)
                {
                    DisposeSeekSources();
                }
            }
        };
        _playhead.PointerCaptureLost += (_, _) => _scrubbing = false;
        Content = BuildContent();
        RefreshAssetList();
        RefreshTimeline();
        ClearSelection();
        // 初始化素材库内容状态（标题/可见性，默认素材模式）与工具栏选中态。
        UpdateLibraryContent();
        UpdateToolBarSelection();
        Opened += (_, _) => Current = this;
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }

            // 关闭前把未落盘的编辑写回工程文件（含片段对齐/轨道/画幅等），重开不丢失。
            _saveTimer.Stop();
            SaveProject();
            StopPreview();
            _clockTimer.Stop();
            _scrubbing = false;
            if (!_seekFrameBusy)
            {
                DisposeSeekSources();
            }
        };
    }

    // ============ 布局 ============

    private Control BuildContent()
    {
        _addAssetButton.Click += (_, _) => _ = AddAssetAsync();
        _addToTimelineButton.Click += (_, _) => AddClipFromAsset();
        // 时间轴上方工具条：刀片切割 / 删除片段（图标化，悬停显示说明）。
        _cutButton.Click += (_, _) => CutAtPlayhead();
        _deleteButton.Click += (_, _) => DeleteSelectedClip();
        _canvasButton.Click += (_, _) => _ = ChangeCanvasSizeAsync();
        _zoomOutButton.Click += (_, _) => ZoomTimeline(1 / 1.25);
        _zoomInButton.Click += (_, _) => ZoomTimeline(1.25);
        // 轨道高度滑块：实时调整泳道高度并重建时间轴。
        _laneHeightSlider.ValueChanged += (_, e) =>
        {
            _laneHeight = Math.Round(e.NewValue);
            // 滑块改的是统一默认值：清除各轨独立高度（轨道头拖拽设置的值一并重置）。
            _laneHeights.Clear();
            _laneHeightText.Text = $"{_laneHeight:0}px";
            RefreshTimeline();
        };
        _laneHeightText.Text = $"{_laneHeight:0}px";
        ToolTip.SetTip(_laneHeightSlider, "调整轨道（泳道）高度");
        ToolTip.SetTip(_cutButton, "刀片切割：在播放头位置把所有覆盖该时刻的片段切成两段");
        ToolTip.SetTip(_deleteButton, "删除选中片段");
        ToolTip.SetTip(_canvasButton, "自定义画幅：修改输出宽高");
        ToolTip.SetTip(_zoomOutButton, "缩小时间轴（也可 Ctrl+滚轮）");
        ToolTip.SetTip(_zoomInButton, "放大时间轴（也可 Ctrl+滚轮）");
        // 素材列表按住拖出 → 拖到时间轴指定轨道/位置（更符合人类操作习惯）。
        _assetList.PointerMoved += (_, e) => StartAssetDrag(e);

        // 顶部命令栏（仿底图图层编辑器 CommandBar：图标 + 文字，无 emoji）。
        _undoButton = CommandButton("\uE195", "撤销", "撤销上一步操作（Ctrl+Z）", Undo);
        _redoButton = CommandButton("\uE121", "重做", "重做已撤销的操作（Ctrl+Y / Ctrl+Shift+Z）", Redo);
        _copyButton = CommandButton("\uE58B", "复制", "复制选中片段（Ctrl+C）", CopySelectedClip);
        _pasteButton = CommandButton("\uE4AE", "粘贴", "把复制的片段粘贴到播放头位置（Ctrl+V）；当前轨道放不下时自动新建轨道", PasteClip);
        _pasteButton.IsEnabled = false;
        _undoButton.IsEnabled = false;
        _redoButton.IsEnabled = false;
        var toolbar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            PrimaryCommands =
            {
                _undoButton,
                _redoButton,
                new CommandBarSeparator(),
                _copyButton,
                _pasteButton,
                new CommandBarSeparator(),
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

        // 素材库（左）：列表视图 ⇄ 封面缩略图视图可切换。
        var assetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _assetList
        };
        _assetCoverScroll.Content = _assetCoverPanel;
        _assetViewToggle.Click += (_, _) => ToggleAssetView();
        ToolTip.SetTip(_assetViewToggle, "切换素材库视图：列表 / 封面缩略图");
        _libraryViewToggle.Content = new IconText { Glyph = "\uE929", Text = "" };
        _libraryViewToggle.Padding = new Thickness(6, 3);
        _libraryViewToggle.Background = Brushes.Transparent;
        _libraryViewToggle.BorderThickness = new Thickness(0);
        _libraryViewToggle.Click += (_, _) =>
        {
            _libraryCardsView = !_libraryCardsView;
            UpdateLibraryContent();
        };
        var assetHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Children = { _libraryTitle, _libraryViewToggle, _assetViewToggle }
        };
        Grid.SetColumn(_libraryViewToggle, 1);
        _libraryViewToggle.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_assetViewToggle, 2);
        var assetHint = new TextBlock
        {
            Text = "添加视频文件到素材库：选中后点「添加到时间轴」，或按住拖到下方时间轴指定轨道/位置。可多素材多轨叠放。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap
        };
        _libraryScroll.Content = _libraryPanel;
        var assetPanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 8,
                Children = { assetHeader, assetScroll, _assetCoverScroll, _libraryScroll, assetHint }
            }
        };
        Grid.SetRow(assetHeader, 0);
        Grid.SetRow(assetScroll, 1);
        Grid.SetRow(_assetCoverScroll, 1);
        Grid.SetRow(_libraryScroll, 1);
        Grid.SetRow(assetHint, 2);

        // 舞台（中，固定宽高比）：播放内容 + 底部传输条（播放/暂停 + 时间码）在**同一个容器**内，
        // 容器下半段是操作条（不再悬在容器外面）。
        _stageBorder.HorizontalAlignment = HorizontalAlignment.Center;
        _stageBorder.VerticalAlignment = VerticalAlignment.Center;
        _playButton.Click += (_, _) => TogglePreview();
        ToolTip.SetTip(_playButton, "播放 / 暂停（空格）");
        var transportBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        transportBar.Children.Add(_playButton);
        transportBar.Children.Add(_timeText);
        var stageInner = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children = { _stageBorder, transportBar }
        };
        Grid.SetRow(transportBar, 1);
        var stageHost = new Border
        {
            ClipToBounds = true,
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(0),
            Child = stageInner
        };
        stageHost.SizeChanged += (_, e) => LayoutStage(e.NewSize.Width, e.NewSize.Height);
        // 整个容器（舞台 + 传输条）占中列；传输条在容器内底部。
        var stageColumn = stageHost;

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

        // 时间轴（底部，多轨：左轨道头 + 右标尺/泳道 + 播放头）。
        // 纵向允许滚动：轨道多 / 轨道高调大时，「新建轨道」拖放区会被面板高度裁掉，
        // 之前纵向滚动禁用导致无法把素材拖到新轨道（体验差）。
        // 标尺固定在滚动区上方（不随纵向滚动走），横向随内容滚动同步（_rulerTranslate）。
        _rulerHost = new Border
        {
            ClipToBounds = true,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ThemePalette.AccentBrushWithAlpha(50),
            Child = _rulerCanvas
        };
        _rulerCanvas.RenderTransform = _rulerTranslate;
        _timelineScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _timelineRoot
        };
        // 泳道纵向滚动时轨道头同步平移；横向滚动时标尺同步平移（固定标尺跟随内容）。
        var trackHeaderTranslate = new TranslateTransform();
        _trackHeaders.RenderTransform = trackHeaderTranslate;
        _timelineScroll.ScrollChanged += (_, _) =>
        {
            trackHeaderTranslate.Y = -_timelineScroll.Offset.Y;
            _rulerTranslate.X = -_timelineScroll.Offset.X;
        };
        // 拖拽自动滚动：指针贴近视口边缘时持续滚动（贴边越近越快）。
        _dragScrollTimer.Tick += (_, _) =>
        {
            if (_dragScrollProbe is not { } probe)
            {
                _dragScrollTimer.Stop();
                return;
            }

            var (dx, dy) = probe();
            if (dx == 0 && dy == 0)
            {
                return;
            }

            var maxX = Math.Max(0, _timelineScroll.Extent.Width - _timelineScroll.Viewport.Width);
            var maxY = Math.Max(0, _timelineScroll.Extent.Height - _timelineScroll.Viewport.Height);
            _timelineScroll.Offset = new Vector(
                Math.Clamp(_timelineScroll.Offset.X + dx, 0, maxX),
                Math.Clamp(_timelineScroll.Offset.Y + dy, 0, maxY));
        };
        var timelineScroll = _timelineScroll;
        // 时间轴滚轮：Ctrl+滚轮缩放（锚定鼠标下的时间），普通滚轮横向滚动（纵向滚动已禁用）。
        _timelineRoot.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ZoomTimeline(e.Delta.Y > 0 ? 1.25 : 1 / 1.25, e.GetPosition(_timelineRoot).X);
            }
            else
            {
                _timelineScroll.Offset = new Vector(
                    Math.Max(0, _timelineScroll.Offset.X - (e.Delta.Y + e.Delta.X) * 40),
                    _timelineScroll.Offset.Y);
            }

            e.Handled = true;
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
        // 右列：固定标尺行 / 顶部新建轨道区 / 泳道滚动区。
        _topTrackZone = new Border
        {
            Height = 22,
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = ThemePalette.AccentBrushWithAlpha(120),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "＋ 拖到这里在最上层新建轨道（轨道 1）",
                FontSize = 11,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        DragDrop.SetAllowDrop(_topTrackZone, true);
        _topTrackZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        });
        _topTrackZone.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e, _topTrackZone, -2));
        var timelineRight = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { _rulerHost, _topTrackZone, timelineScroll }
        };
        Grid.SetRow(_rulerHost, 0);
        Grid.SetRow(_topTrackZone, 1);
        Grid.SetRow(timelineScroll, 2);
        // 左列：顶部空行（高 = 固定标尺 + 顶部新建区）+ 轨道头列表（滚动时 Y 平移同步），
        // 使「轨道 N」色块与右侧泳道逐行对齐。
        var trackHeadColumn = new Grid
        {
            RowDefinitions = new RowDefinitions($"{RulerHeight + 26},*"),
            Children = { new Border { Height = RulerHeight + 26 }, _trackHeaders }
        };
        var timelineArea = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("92,*"),
            ColumnSpacing = 8,
            Children = { trackHeadColumn, timelineRight }
        };
        Grid.SetColumn(trackHeadColumn, 0);
        Grid.SetColumn(timelineRight, 1);
        // 时间轴上方工具条：缩放 / 刀片切割 / 删除片段 / 自定义画幅 / 输出比例 / 片段统计 / 状态。
        // 全部挤在一行：操作按钮只留图标（悬停有说明），文本不换行不高占。
        _zoomText.Text = $"{_pxPerSecond / 6 * 100:0}%";
        var timelineToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _zoomOutButton,
                _zoomText,
                _zoomInButton,
                _cutButton,
                _deleteButton,
                _canvasButton,
                new TextBlock { Text = "轨道高", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                _laneHeightSlider,
                _laneHeightText,
                _outputSizeText,
                _clipCountText,
                _statusText
            }
        };
        var timelinePanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            ClipToBounds = true,
            MaxHeight = 460,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 6,
                Children = { timelineToolbar, timelineArea }
            }
        };
        Grid.SetRow(timelineToolbar, 0);
        Grid.SetRow(timelineArea, 1);

        // 主体：左侧工具栏 | 素材库 | 分割条 | 舞台 | 分割条 | 属性，面板宽度可拖拽调整。
        var toolStrip = BuildToolStrip();
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"44,{_assetPanelWidth},6,*,6,{_inspectorWidth}"),
            Children = { toolStrip, assetPanel, stageColumn, rightPanel }
        };
        var vSplitter1 = VerticalSplitter(body, 1, () => _assetPanelWidth, w => _assetPanelWidth = w, 160, 480);
        // 舞台与右侧检查器之间的分割条：分割条位于检查器左侧（也是舞台右边缘），向右拖应使检查器
        // 变窄（舞台变宽），因此方向取反（reverse: true）。
        var vSplitter2 = VerticalSplitter(body, 5, () => _inspectorWidth, w => _inspectorWidth = w, 240, 520, reverse: true);
        body.Children.Insert(2, vSplitter1);
        body.Children.Insert(4, vSplitter2);
        Grid.SetColumn(toolStrip, 0);
        Grid.SetColumn(assetPanel, 1);
        Grid.SetColumn(vSplitter1, 2);
        Grid.SetColumn(stageColumn, 3);
        Grid.SetColumn(vSplitter2, 4);
        Grid.SetColumn(rightPanel, 5);

        _rootGrid = new Grid
        {
            Margin = new Thickness(12),
            // 行：顶部命令栏 / 主体 / 分割条 / 时间轴（高度随轨道数自适应，也可拖分割条）。
            RowDefinitions = new RowDefinitions("Auto,*,6,Auto"),
            RowSpacing = 8
        };
        var hSplitter = HorizontalSplitter(_rootGrid, 3,
            // 起始高度 = 当前实际行高（自动时 = 内容所需高度，用户拖过 = 用户值与内容下限的较大者）。
            () => _timelinePanelHeight > 0 ? Math.Max(_timelinePanelHeight, _timelineChromeHeight) : _timelineChromeHeight,
            w => _timelinePanelHeight = w, 140, 460);
        _rootGrid.Children.Add(toolbar);
        _rootGrid.Children.Add(body);
        _rootGrid.Children.Add(hSplitter);
        _rootGrid.Children.Add(timelinePanel);
        Grid.SetRow(body, 1);
        Grid.SetRow(hSplitter, 2);
        Grid.SetRow(timelinePanel, 3);
        return _rootGrid;
    }

    /// <summary>
    /// 垂直分割条：拖动调整左右面板宽度（同时写回字段与网格列宽）。
    /// reverse=true 时方向取反（用于分割条位于面板左侧的情形，如右侧检查器：向右拖 → 面板变窄）。
    /// </summary>
    private Border VerticalSplitter(Grid grid, int columnIndex, Func<double> get, Action<double> set, double min, double max, bool reverse = false)
    {
        var splitter = new Border
        {
            Width = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            VerticalAlignment = VerticalAlignment.Stretch,
            ZIndex = 50
        };
        var hover = ThemePalette.AccentBrushWithAlpha(70);
        splitter.PointerEntered += (_, _) => splitter.Background = hover;
        splitter.PointerExited += (_, _) => splitter.Background = Brushes.Transparent;
        double? startX = null;
        var startW = 0.0;
        splitter.PointerPressed += (_, e) =>
        {
            startX = e.GetPosition(splitter).X;
            startW = get();
            e.Pointer.Capture(splitter);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (startX == null)
            {
                return;
            }

            var delta = e.GetPosition(splitter).X - startX.Value;
            var w = Math.Clamp(reverse ? startW - delta : startW + delta, min, max);
            set(w);
            grid.ColumnDefinitions[columnIndex] = new ColumnDefinition(new GridLength(w));
        };
        splitter.PointerReleased += (_, e) =>
        {
            startX = null;
            e.Pointer.Capture(null);
        };
        splitter.PointerCaptureLost += (_, _) => startX = null;
        return splitter;
    }

    /// <summary>水平分割条：拖动调整时间轴面板高度（下限为内容所需高度，防轨道被裁掉）。</summary>
    private Border HorizontalSplitter(Grid grid, int rowIndex, Func<double> get, Action<double> set, double min, double max)
    {
        var splitter = new Border
        {
            Height = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ZIndex = 50
        };
        var hover = ThemePalette.AccentBrushWithAlpha(70);
        splitter.PointerEntered += (_, _) => splitter.Background = hover;
        splitter.PointerExited += (_, _) => splitter.Background = Brushes.Transparent;
        double? startY = null;
        var startH = 0.0;
        splitter.PointerPressed += (_, e) =>
        {
            startY = e.GetPosition(splitter).Y;
            startH = get();
            e.Pointer.Capture(splitter);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (startY == null)
            {
                return;
            }

            // 时间轴面板锚定在窗口底部：往下拖 = 时间轴变矮（边界下移）、往上拖 = 变高。
            // 行高 = 起始高 - 位移，边界始终跟手；缩小后内容被裁剪，下次刷新会按内容自动恢复。
            var delta = e.GetPosition(splitter).Y - startY.Value;
            var h = Math.Clamp(startH - delta, min, max);
            set(h);
            grid.RowDefinitions[rowIndex] = new RowDefinition(new GridLength(h));
        };
        splitter.PointerReleased += (_, e) =>
        {
            startY = null;
            e.Pointer.Capture(null);
        };
        splitter.PointerCaptureLost += (_, _) => startY = null;
        return splitter;
    }

    /// <summary>左侧垂直工具栏：选择 / 文本 / 矩形 / 椭圆 / 效果。</summary>
    private Control BuildToolStrip()
    {
        var panel = new StackPanel { Spacing = 2 };
        _toolPanel = panel;
        panel.Children.Add(ToolButton("select", "\uE5BE", "\uE5BF", "选择工具（默认）：点击选中片段"));
        panel.Children.Add(ToolButton("text", "\uF1BD", "\uF1BE", "文本工具：在舞台点击放置文本覆盖层"));
        panel.Children.Add(ToolButton("shape", "\uE774", "\uE775", "形状工具：在素材库选择形状，点击舞台放置或拖到时间轴"));
        panel.Children.Add(ToolButton("effect", "\uF42E", "\uF42F", "效果工具：在素材库选择滤镜，拖到时间轴作为片段应用"));
        // 选中高亮滑块（同底图图层编辑器）：独立圆角块，切换工具时平滑滑动到新位置。
        _toolHighlight = new Border
        {
            IsHitTestVisible = false,
            // 圆角与按钮一致（40×40 圆角 8 的正方形按钮）。
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(ThemePalette.AccentColorWithAlpha(190)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0
        };
        // 宿主 Grid：高亮块在前（底层），按钮面板在后（顶层，按钮透明底露出高亮）。
        var host = new Grid
        {
            Margin = new Thickness(0, 0, 4, 0),
            Children = { _toolHighlight, panel }
        };
        _toolHighlightHost = host;
        // 首次布局完成后定位高亮（此前 Bounds 未测量）。
        host.SizeChanged += (_, _) =>
        {
            if (!_toolHighlightPositioned)
            {
                SlideToolHighlight(false);
                EditorAnimations.FadeIn(_toolHighlight, 0, 1, delay: TimeSpan.FromMilliseconds(30));
            }
        };
        // 入场：按钮逐个弹性进场（错峰）。
        EditorAnimations.After(TimeSpan.FromMilliseconds(30), () =>
        {
            var idx = 0;
            foreach (var b in panel.Children.OfType<Button>())
            {
                EditorAnimations.PopIn(b, -10, 0, 0.9, delay: TimeSpan.FromMilliseconds(idx * 35));
                idx++;
            }
        });
        // 舞台点击：若处于文本/形状工具，在该位置放置覆盖层片段。
        // 挂在 _stageBorder（黑背景可命中）：点舞台空白处时事件源是 _stageBorder，
        // 冒泡路径不经过 _stageHostGrid（它是 _stageBorder 的子级），挂子级会漏掉空白区点击。
        _stageBorder.PointerPressed += (_, e) =>
        {
            if (_currentTool is not ("text" or "shape"))
            {
                return;
            }

            AddOverlayClip(
                _currentTool == "text" ? "Text" : "Shape",
                _currentTool == "text" ? "Text" : _currentShape,
                e.GetPosition(_stageBorder));
            e.Handled = true;
        };
        return host;
    }

    /// <summary>工具栏工具按钮（仿底图图层编辑器：40×40 圆角正方形，透明底 + 选中由高亮滑块表达）。</summary>
    private Button ToolButton(string tool, string filledGlyph, string regularGlyph, string tip)
    {
        var button = new Button
        {
            Content = new IconText { Glyph = filledGlyph, Text = string.Empty },
            Width = 40,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        // 选中态过渡：背景 / 前景切换时平滑渐变。
        button.Transitions = new Transitions
        {
            new BrushTransition { Property = Avalonia.Controls.Button.BackgroundProperty, Duration = EditorAnimations.TapDuration },
            new BrushTransition { Property = Avalonia.Controls.Button.ForegroundProperty, Duration = EditorAnimations.TapDuration }
        };
        // 按压缩放反馈（Fluent 风格）。
        EditorAnimations.AddPressFeedback(button);
        // 初始不可见，打开窗口时逐个弹性进场。
        button.Opacity = 0;
        // 悬停：已选中的工具保持透明（主题色高亮块表达选中），其它工具显示半透明悬停底色。
        button.PointerEntered += (_, _) =>
        {
            _toolHovered[tool] = true;
            UpdateToolBarSelection();
        };
        button.PointerExited += (_, _) =>
        {
            _toolHovered[tool] = false;
            UpdateToolBarSelection();
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => SetTool(tool);
        _toolButtons[tool] = button;
        return button;
    }

    private void SetTool(string tool)
    {
        _currentTool = tool;
        // 选中态由高亮滑块表达（按钮透明露底）。
        UpdateToolBarSelection();
        // 高亮滑块滑动到新工具的位置。
        SlideToolHighlight(true);
        // 素材库内容随工具切换：形状工具→形状库，效果工具→滤镜库。
        UpdateLibraryContent();
        _statusText.Text = tool switch
        {
            "text" => "文本工具：在舞台点击放置文本覆盖层。",
            "shape" => "形状工具：在左侧素材库选择形状，点击舞台放置或拖到时间轴。",
            "effect" => "效果工具：在左侧素材库选择滤镜，拖到时间轴作为片段应用。",
            _ => "选择工具。"
        };
    }

    /// <summary>按当前工具刷新工具栏按钮状态：选中态由高亮滑块表达（按钮透明露底），
    /// 已选中工具悬停保持主题色（高亮块），其它工具悬停显示半透明底色。</summary>
    private void UpdateToolBarSelection()
    {
        if (_toolHighlight != null)
        {
            _toolHighlight.Background = new SolidColorBrush(ThemePalette.AccentColorWithAlpha(190));
        }

        foreach (var (tool, button) in _toolButtons)
        {
            var active = tool == _currentTool;
            // 选中态背景交给高亮滑块：按钮本身 = 已选中 ? 透明 : (悬停 ? 半透明 : 透明)。
            button.Background = active
                ? Brushes.Transparent
                : _toolHovered.GetValueOrDefault(tool)
                    ? ThemePalette.SubtleFill()
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

    /// <summary>把选中高亮滑块移动到当前工具的按钮位置：首次直接放置，之后平滑滑动。</summary>
    private void SlideToolHighlight(bool animate)
    {
        if (_toolHighlight == null || !_toolButtons.TryGetValue(_currentTool, out var button))
        {
            return;
        }

        var pos = button.TranslatePoint(new Point(0, 0), _toolHighlightHost);
        if (pos == null || button.Bounds.Height <= 0)
        {
            return; // 尚未布局，稍后由 SizeChanged 补齐。
        }

        var targetY = pos.Value.Y;
        if (_toolHighlight.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            _toolHighlight.RenderTransform = translate;
        }

        // 首次测量时把高度对齐到按钮。
        if (double.IsNaN(_toolHighlight.Height) || _toolHighlight.Height <= 0)
        {
            _toolHighlight.Height = Math.Max(20, button.Bounds.Height);
        }

        if (animate && _toolHighlightPositioned)
        {
            // 平滑非线性移动（CubicEaseOut），不弹跳。
            EditorAnimations.AnimateValue(v => translate.Y = v, translate.Y, targetY,
                EditorAnimations.InDuration, EditorAnimations.Interaction);
        }
        else
        {
            translate.Y = targetY;
        }

        _toolHighlightPositioned = true;
    }

    /// <summary>在当前工具/播放头处添加文本或形状覆盖层片段（stagePos 非空 = 舞台点击放置）。</summary>
    private void AddOverlayClip(string kind, string shape, Point? stagePos = null)
    {
        PushUndo();
        var clip = new VideoClip
        {
            Kind = kind,
            Shape = shape,
            Text = "文本",
            Color = "#FFFFEB3B",
            Track = _selectedTrack,
            StartTime = _playheadTime,
            InPoint = 0,
            OutPoint = 5,
            Scale = kind == "Text" ? 1 : 0.4,
            ScaleX = 1,
            ScaleY = 1
        };
        if (stagePos != null)
        {
            var W = _stageBorder.Bounds.Width;
            var H = _stageBorder.Bounds.Height;
            clip.OffsetX = Math.Clamp((stagePos.Value.X - W / 2.0) / Math.Max(1, W), -2, 2);
            clip.OffsetY = Math.Clamp((stagePos.Value.Y - H / 2.0) / Math.Max(1, H), -2, 2);
        }

        clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
        _project.Clips.Add(clip);
        _selected = clip;
        _currentTool = "select";
        UpdateToolBarSelection();
        SlideToolHighlight(true);
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
        _statusText.Text = kind == "Text"
            ? "已添加文本覆盖层（在右侧编辑文字与颜色）。"
            : "已添加形状覆盖层（在右侧编辑形状与颜色）。";
    }

    /// <summary>效果工具：对选中片段应用灰度/水平翻转/垂直翻转。</summary>
    private async Task EditEffectsAsync()
    {
        if (_selected is not { } clip)
        {
            _statusText.Text = "先选中一个片段再应用效果。";
            return;
        }

        var gray = new CheckBox { Content = "灰度", IsChecked = clip.Grayscale > 0.5 };
        var flipH = new CheckBox { Content = "水平翻转", IsChecked = clip.FlipH };
        var flipV = new CheckBox { Content = "垂直翻转", IsChecked = clip.FlipV };
        var dialog = new ContentDialog
        {
            Title = $"效果 - {ClipDisplayName(clip)}",
            Content = new StackPanel { Spacing = 8, Children = { gray, flipH, flipV } },
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        PushUndo();
        clip.Grayscale = gray.IsChecked == true ? 1 : 0;
        clip.FlipH = flipH.IsChecked == true;
        clip.FlipV = flipV.IsChecked == true;
        ScheduleSave();
        _statusText.Text = "效果已应用。";
        // 舞台实时更新：覆盖层重新生成；视频重新显示首帧（带灰度）。
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            _stageLayers[clip.Track].Image.IsVisible = false;
            ShowSelectedClipFrame(clip);
        }
    }

    private Control BuildInspector()
    {
        _propertyControls =
        [
            _inSpin, _outSpin, _scaleSpin, _scaleXSpin, _scaleYSpin, _offsetXSpin, _offsetYSpin,
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

        // 文本/形状/图片覆盖层编辑（覆盖层 Tab 内）。
        _overlayShape.ItemsSource = new[] { "Rect", "Ellipse" };
        _overlayText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _overlayColor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _overlayShape.SelectionChanged += (_, _) => ApplyOverlayEdits();
        _overlayTextRow = InspectorRow("内容", _overlayText);
        _overlayColorRow = InspectorRow("颜色 #AARRGGBB", _overlayColor);
        _overlayShapeRow = InspectorRow("形状", _overlayShape);
        _overlayPanel.Children.Add(_overlayTextRow);
        _overlayPanel.Children.Add(_overlayColorRow);
        _overlayPanel.Children.Add(_overlayShapeRow);
        // 滤镜类型编辑（滤镜 Tab 内）。
        _filterCombo.ItemsSource = FilterDefs.Select(d => d.Name).ToList();
        _filterCombo.SelectionChanged += (_, _) => ApplyFilterEdits();
        _filterPanel.Children.Add(InspectorRow("类型", _filterCombo));

        // Tab 选项卡分组：变换 / 覆盖层（仅 Text/Shape/Image）/ 滤镜（仅 Filter）。
        var transformPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                InspectorRow("入点（秒）", _inSpin),
                InspectorRow("出点（秒）", _outSpin),
                new TextBlock { Text = "变换", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                InspectorRow("缩放", _scaleSpin),
                InspectorRow("横向拉伸", _scaleXSpin),
                InspectorRow("纵向拉伸", _scaleYSpin),
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
        _transformTab = new TabItem { Header = "变换", Content = transformPanel };
        _overlayTab = new TabItem { Header = "覆盖层", Content = _overlayPanel, IsVisible = false };
        _filterTab = new TabItem { Header = "滤镜", Content = _filterPanel, IsVisible = false };
        _inspectorTabs = new TabControl
        {
            Background = Brushes.Transparent,
            Items = { _transformTab, _overlayTab, _filterTab }
        };

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "片段属性", FontWeight = FontWeight.SemiBold },
                _clipNameText,
                _inspectorTabs
            }
        };
    }

    /// <summary>滤镜类型下拉修改应用到选中滤镜片段（连续修改合并为一步撤销）。</summary>
    private void ApplyFilterEdits()
    {
        if (_updatingUi || _selected is not { Kind: "Filter" } clip)
        {
            return;
        }

        var idx = _filterCombo.SelectedIndex;
        if (idx < 0 || idx >= FilterDefs.Length)
        {
            return;
        }

        PushUndo(true);
        clip.Filter = FilterDefs[idx].Key;
        _clipNameText.Text = $"{ClipDisplayName(clip)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n滤镜 {FilterDefs[idx].Name}";
        ScheduleSave();
        _statusText.Text = $"滤镜已改为「{FilterDefs[idx].Name}」。";
        // 立即刷新当前时刻的舞台预览（滤镜作用于下方画面）。
        if (!_playing)
        {
            ShowFrameAt(_playheadTime);
        }
    }

    /// <summary>覆盖层文本/颜色/形状编辑应用到选中片段（连续输入合并为一步撤销）。</summary>
    private void ApplyOverlayEdits()
    {
        if (_updatingUi || _selected is not { } clip || clip.Kind == "Video")
        {
            return;
        }

        PushUndo(true);
        clip.Text = _overlayText.Text ?? "";
        clip.Color = string.IsNullOrWhiteSpace(_overlayColor.Text) ? "#FFFFFFFF" : _overlayColor.Text!.Trim();
        clip.Shape = _overlayShape.SelectedItem?.ToString() ?? "Rect";
        _clipNameText.Text = $"{ClipDisplayName(clip)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s";
        RefreshTimeline();
        ScheduleSave();
        // 舞台实时刷新覆盖层帧。
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            var frame = GenerateOverlayFrame(clip);
            if (frame != null)
            {
                UpdateStageLayer(clip.Track, frame, clip);
            }
        }
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
    /// <summary>舞台容器底部传输条（播放/暂停 + 时间码）占位高度，画幅按剩余高度适配。</summary>
    private const double StageTransportHeight = 40;

    private void LayoutStage(double availableW, double availableH)
    {
        // 容器底部是传输条（播放按钮 + 时间码），画幅只用剩余高度。
        availableH -= StageTransportHeight;
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

    // ============ 工程保存 ============

    /// <summary>安排一次防抖保存（编辑操作后调用；连续编辑只写一次盘）。</summary>
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>编辑器诊断日志（配置目录 video-editor.log）：导入/弹窗等关键路径打点，便于定位 UI 卡顿。</summary>
    private static void EditorLog(string message) =>
        DiagnosticLog.Write(Path.Combine(InjectorRuntime.ConfigDirectory, "video-editor.log"), $"[editor] {message}");

    /// <summary>把当前工程写入配置目录（供重开编辑器时恢复）。</summary>
    private void SaveProject()
    {
        try
        {
            VideoProjectStore.Save(_project, VideoProjectStore.DefaultPath);
        }
        catch
        {
            // 写盘失败不打扰编辑。
        }
    }

    /// <summary>删除空轨道并把轨道号压缩为连续 0..n-1（用户要求：无内容的轨道自动删除）。</summary>
    private void CompactTracks()
    {
        var used = _project.Clips.Select(c => c.Track).Distinct().OrderBy(t => t).ToList();
        if (used.Count == 0)
        {
            _selectedTrack = 0;
            return;
        }

        var map = new Dictionary<int, int>();
        for (var i = 0; i < used.Count; i++)
        {
            map[used[i]] = i;
        }

        foreach (var clip in _project.Clips)
        {
            clip.Track = map[clip.Track];
        }

        if (_selectedTrack >= _project.TrackCount)
        {
            _selectedTrack = Math.Max(0, _project.TrackCount - 1);
        }
    }

    // ============ 时间轴缩放 ============

    /// <summary>
    /// 缩放时间轴像素/秒（2..60）。anchorX 非空时保持该内容坐标对应的时间在相同屏幕位置
    /// （鼠标滚轮锚定）；为空时锚定播放头。
    /// </summary>
    private void ZoomTimeline(double factor, double? anchorX = null)
    {
        var oldPx = _pxPerSecond;
        var newPx = Math.Clamp(oldPx * factor, 2, 60);
        if (Math.Abs(newPx - oldPx) < 0.01)
        {
            return;
        }

        var offsetX = _timelineScroll.Offset.X;
        var anchorTime = anchorX != null ? Math.Max(0, anchorX.Value / oldPx) : _playheadTime;
        _pxPerSecond = newPx;
        RefreshTimeline();
        // 锚定：t*newPx - newOffsetX = t*oldPx - oldOffsetX → newOffsetX = t*(newPx-oldPx) + oldOffsetX。
        var newOffset = Math.Max(0, anchorTime * (newPx - oldPx) + offsetX);
        _timelineScroll.Offset = new Vector(newOffset, 0);
        _zoomText.Text = $"{_pxPerSecond / 6 * 100:0}%";
    }

    // ============ 撤销/重做 ============

    /// <summary>深拷贝工程快照（撤销栈用；片段用 Clone 保持独立性）。</summary>
    private static VideoProject CloneProject(VideoProject p) => new()
    {
        OutputWidth = p.OutputWidth,
        OutputHeight = p.OutputHeight,
        Clips = p.Clips.Select(c => c.Clone()).ToList()
    };

    /// <summary>
    /// 压入撤销快照（必须在修改前调用）。coalesce=true 且与上一次同为合并操作、间隔 500ms 内时
    /// 不压新快照（数值框连续拖动 = 一步撤销）；离散操作与合并操作之间互不合并。
    /// </summary>
    private void PushUndo(bool coalesce = false)
    {
        var merge = coalesce && _lastPushCoalesced && (DateTime.Now - _lastUndoPush).TotalMilliseconds < 500;
        if (!merge)
        {
            _undoStack.Add(CloneProject(_project));
            while (_undoStack.Count > MaxUndoDepth)
            {
                _undoStack.RemoveAt(0);
            }

            _redoStack.Clear();
        }

        _lastPushCoalesced = coalesce;
        _lastUndoPush = DateTime.Now;
        UpdateUndoUi();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Add(CloneProject(_project));
        while (_redoStack.Count > MaxUndoDepth)
        {
            _redoStack.RemoveAt(0);
        }

        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreProject(snapshot);
        UpdateUndoUi();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Add(CloneProject(_project));
        while (_undoStack.Count > MaxUndoDepth)
        {
            _undoStack.RemoveAt(0);
        }

        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreProject(snapshot);
        UpdateUndoUi();
    }

    /// <summary>把快照恢复到当前工程（保留选中片段、停止预览、刷新 UI 并保存）。</summary>
    private void RestoreProject(VideoProject snapshot)
    {
        var selectedIndex = _selected != null ? _project.Clips.IndexOf(_selected) : -1;
        _project.OutputWidth = snapshot.OutputWidth;
        _project.OutputHeight = snapshot.OutputHeight;
        _project.Clips = snapshot.Clips.Select(c => c.Clone()).ToList();
        _selected = selectedIndex >= 0 && selectedIndex < _project.Clips.Count
            ? _project.Clips[selectedIndex]
            : null;

        // 舞台/预览与工程不再同步：停止预览并清理舞台图层。
        StopPreview();
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

        _stageCaption.Text = "";
        RefreshTimeline();
        FillPropertyPanel();
        UpdateStageHandles();
        ScheduleSave();
    }

    private void UpdateUndoUi()
    {
        if (_undoButton == null)
        {
            return;
        }

        _undoButton.IsEnabled = _undoStack.Count > 0;
        _redoButton.IsEnabled = _redoStack.Count > 0;
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
            Title = "添加素材（视频 / 图片）",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("视频 / 图片")
                {
                    Patterns = ["*.mp4", "*.wmv", "*.avi", "*.mkv", "*.mov", "*.webm", "*.m4v",
                                "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"]
                },
                FilePickerFileTypes.All
            ]
        });
        EditorLog($"选择器返回 {files.Count} 个文件");
        // 预览播放会持续占用 UI 线程写帧；先停掉再弹询问对话框，避免对话框延迟出现/动画卡顿。
        if (_playing)
        {
            StopPreview();
        }

        var added = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && !_assets.Contains(path))
            {
                added.Add(path);
            }
        }

        // 视频素材先询问是否压缩转码（需要完整 FFmpeg 包），图片直接加入。
        EditorLog($"开始导入 {added.Count} 个新素材（即将弹压缩询问）");
        await ImportAssetsAsync(added);
        RefreshAssetList();
        if (_assets.Count > 0 && _assetList.SelectedIndex < 0)
        {
            _assetList.SelectedIndex = 0;
        }
    }

    /// <summary>把新素材加入素材库：图片直接加入（Kind=Image 覆盖层）；视频逐个询问压缩后加入。</summary>
    private async Task ImportAssetsAsync(List<string> paths)
    {
        foreach (var path in paths)
        {
            if (VideoTranscoder.IsImageFile(path))
            {
                _assets.Add(path);
            }
        }

        var videos = paths.Where(p => !VideoTranscoder.IsImageFile(p) && !p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (videos.Count == 0)
        {
            return;
        }

        var compress = await AskCompressImportAsync(videos);
        if (compress == null)
        {
            return; // 取消导入
        }

        foreach (var video in videos)
        {
            if (compress != true)
            {
                _assets.Add(video);
                continue;
            }

            // 压缩失败（取消/编码失败）回退导入原文件。
            var compressed = await CompressAssetAsync(video);
            _assets.Add(compressed ?? video);
        }
    }

    /// <summary>
    /// 询问「压缩后导入 / 直接导入 / 取消」。多选时选择应用到全部。
    /// 精简包（无编码器）时引导升级完整包；升级完成自动回到本询问。
    /// 返回 true=压缩，false=直接导入，null=取消。
    /// </summary>
    private async Task<bool?> AskCompressImportAsync(List<string> videos)
    {
        var label = videos.Count == 1
            ? $"「{Path.GetFileName(videos[0])}」"
            : $"已选择 {videos.Count} 个视频素材";
        var dialog = new ContentDialog
        {
            Title = "压缩后导入？",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = label, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "压缩会把素材转为 720p H.264（约数秒到一分钟），\n" +
                               "减小体积、剪辑与渲染更流畅；原始文件不会被修改。",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.75
                    }
                }
            },
            PrimaryButtonText = "压缩后导入",
            SecondaryButtonText = "直接导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary && !FFmpegRuntime.EncoderAvailable)
        {
            // 选择了压缩但当前是精简包：引导升级完整包，升级完成后重新询问。
            var upgraded = await EnsureFullPackageAsync("压缩素材需要完整 FFmpeg 包（当前为精简解码包，仅支持解码）。");
            if (!upgraded)
            {
                return null;
            }

            return await AskCompressImportAsync(videos);
        }

        return result switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null
        };
    }

    /// <summary>
    /// 确保已安装带编码器的完整 FFmpeg 包：缺失时弹询问并打开安装器（单实例），
    /// 等安装窗口关闭后返回是否已具备编码能力。注意：若本进程已加载过精简包的
    /// 解码库（Windows 同名 dll 不会重复加载），新库要重启宿主才能生效，此时提示并返回 false。
    /// </summary>
    private async Task<bool> EnsureFullPackageAsync(string reason)
    {
        if (FFmpegRuntime.IsAvailable && FFmpegRuntime.EnsureLoaded() && FFmpegRuntime.EncoderAvailable)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "需要完整 FFmpeg 包",
            Content = new TextBlock
            {
                Text = reason + "\n完整包约 50 MB，安装后渲染剪辑 / 压缩转码均可使用。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "升级完整包",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        // 打开（或聚焦已打开的）安装器并等待其关闭。
        FfmpegInstallWindow window;
        if (FfmpegInstallWindow.Current is { } existing)
        {
            existing.Activate();
            window = existing;
        }
        else
        {
            window = new FfmpegInstallWindow(FfmpegPackageKind.Full);
            window.Show();
        }

        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        await closed.Task;
        FFmpegRuntime.Refresh();

        if (FFmpegRuntime.EnsureLoaded() && FFmpegRuntime.EncoderAvailable)
        {
            return true;
        }

        // 完整包装好了但本进程已加载过精简库：需要重启宿主后新库才生效。
        if (FFmpegRuntime.IsAvailable)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = "完整包已安装",
                Content = new TextBlock
                {
                    Text = "当前进程已加载精简包的解码库，重启 ClassIsland 后完整包（编码能力）生效。",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "知道了"
            });
        }

        return false;
    }

    /// <summary>压缩单个素材（后台转码 + 进度对话框，可取消）。成功返回输出路径，取消/失败返回 null。</summary>
    private async Task<string?> CompressAssetAsync(string source)
    {
        try
        {
            var cacheDir = Path.Combine(InjectorRuntime.ConfigDirectory, "video-cache");
            var output = VideoTranscoder.BuildOutputPath(source, cacheDir, 720, new FileInfo(source).Length);
            if (File.Exists(output))
            {
                return output; // 已有同源压缩结果，直接复用。
            }

            var status = new TextBlock { Text = "准备中…" };
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, MinHeight = 4 };
            var dialog = new ContentDialog
            {
                Title = "正在压缩素材",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileName(source), Opacity = 0.75 },
                        bar,
                        status
                    }
                },
                PrimaryButtonText = "取消"
            };
            using var cts = new CancellationTokenSource();
            var dialogTask = ShowDialogAsync(dialog);
            // 用户点「取消」按钮会关闭对话框并返回 Primary → 触发取消令牌（半成品自动清理）。
            _ = dialogTask.ContinueWith(t =>
            {
                if (t.Result == ContentDialogResult.Primary)
                {
                    cts.Cancel();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

            var ok = await Task.Run(() => VideoTranscoder.Compress(source, output, 720, 27,
                p => Dispatcher.UIThread.Post(() =>
                {
                    bar.Value = p;
                    status.Text = $"已处理 {p:P0}";
                }), cts.Token));
            try
            {
                if (dialog.IsVisible)
                {
                    dialog.Hide();
                }
            }
            catch
            {
                // 对话框已被用户关闭。
            }

            if (ok)
            {
                _statusText.Text = $"已压缩：{Path.GetFileName(output)}";
            }

            return ok ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshAssetList()
    {
        // 工程里引用过的文件自动进素材库（避免重开编辑器后素材库为空：只要片段引用了就出现在库里）。
        foreach (var clip in _project.Clips)
        {
            if (clip.Kind is "Video" or "Image" && !string.IsNullOrWhiteSpace(clip.SourcePath) &&
                File.Exists(clip.SourcePath) && !_assets.Contains(clip.SourcePath))
            {
                _assets.Add(clip.SourcePath);
            }
        }

        _assetList.ItemsSource = _assets.Select(p => Path.GetFileName(p)).ToList();
        // 封面视图与列表视图同步重建。
        RefreshAssetCovers();
        UpdateAssetButtons();
    }

    private void UpdateAssetButtons() => _addToTimelineButton.IsEnabled = _assetList.SelectedIndex >= 0;

    /// <summary>复制选中片段到内部剪贴板（Ctrl+C / 复制按钮）。</summary>
    private void CopySelectedClip()
    {
        if (_selected == null)
        {
            _statusText.Text = "先选中一个片段再复制。";
            return;
        }

        _clipboard = _selected.Clone();
        UpdateClipboardUi();
        _statusText.Text = $"已复制「{ClipDisplayName(_selected)}」（Ctrl+V 可粘贴到播放头）。";
    }

    /// <summary>判断片段能否放进指定轨道的指定位置（不与同轨其它片段重叠；exclude 用于移动时排除自身）。</summary>
    private bool FitsOnTrack(int track, double start, double duration, VideoClip? exclude = null)
    {
        var end = start + duration;
        return !_project.Clips.Any(c =>
            !ReferenceEquals(c, exclude) &&
            c.Track == track && start < c.StartTime + c.Duration - 0.001 && end > c.StartTime + 0.001);
    }

    /// <summary>把剪贴板中的片段粘贴到播放头位置（Ctrl+V / 粘贴按钮）；当前轨道挤不下时新建轨道。</summary>
    private void PasteClip()
    {
        if (_clipboard == null)
        {
            return;
        }

        var clip = _clipboard.Clone();
        clip.StartTime = Math.Max(0, _playheadTime);
        clip.Track = _selectedTrack;
        // 当前轨道放不下（与同轨片段重叠）→ 粘贴到新轨道（新轨道必为空）。
        if (!FitsOnTrack(clip.Track, clip.StartTime, clip.Duration))
        {
            clip.Track = _project.TrackCount;
        }

        PushUndo();
        _project.Clips.Add(clip);
        _selected = clip;
        _selectedTrack = clip.Track;
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
        _statusText.Text = $"已粘贴「{ClipDisplayName(clip)}」到轨道 {clip.Track + 1}（{clip.StartTime:0.#}s）。";
    }

    /// <summary>粘贴按钮可用性与剪贴板状态同步。</summary>
    private void UpdateClipboardUi()
    {
        if (_pasteButton != null)
        {
            _pasteButton.IsEnabled = _clipboard != null;
        }
    }

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
        // 图片素材：固定 5 秒的图片覆盖层片段（Kind=Image）；视频：按素材时长建片段。
        var isImage = VideoTranscoder.IsImageFile(path);
        var duration = isImage ? 5 : GetAssetDuration(path);
        var clip = new VideoClip
        {
            Kind = isImage ? "Image" : "Video",
            SourcePath = path,
            Track = _selectedTrack,
            StartTime = _project.TrackEnd(_selectedTrack),
            InPoint = 0,
            OutPoint = isImage ? duration : duration > 0.5 ? duration : 10
        };
        PushUndo();
        _project.Clips.Add(clip);
        _selected = clip;
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
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

    /// <summary>
    /// 从底图图层编辑器导入：把合成的底图快照 PNG 作为图片覆盖层加入时间轴。
    /// 图片按原比例居中显示（比例与舞台一致时正好铺满，即按现有舞台画幅排好）；
    /// 默认放轨道 0 最底层、时长跟随工程总时长（不足 5 秒按 5 秒）。
    /// </summary>
    public void ImportImageAsClip(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        if (!_assets.Contains(imagePath))
        {
            _assets.Add(imagePath);
        }

        var duration = Math.Max(5, Math.Ceiling(_project.Duration));
        var clip = new VideoClip
        {
            Kind = "Image",
            SourcePath = imagePath,
            Track = 0,
            StartTime = 0,
            InPoint = 0,
            OutPoint = duration
        };
        if (!FitsOnTrack(0, clip.StartTime, clip.Duration))
        {
            clip.Track = _project.TrackCount;
            clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
        }

        PushUndo();
        _project.Clips.Add(clip);
        _selected = clip;
        _selectedTrack = clip.Track;
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        RefreshAssetList();
        ScheduleSave();
        _statusText.Text = "已导入底图图层快照（图片覆盖层，可在属性面板调整）。";
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

    // ============ 素材库封面视图 ============

    /// <summary>切换素材库视图：列表 ⇄ 封面缩略图。</summary>
    private void ToggleAssetView()
    {
        _assetCoverView = !_assetCoverView;
        if (_assetViewToggle.Content is IconText icon)
        {
            // 封面视图时显示「列表」图标，列表视图时显示「网格」图标。
            icon.Glyph = _assetCoverView ? "\uEAC0" : "\uE929";
        }

        if (_assetCoverView)
        {
            RefreshAssetCovers();
        }

        UpdateLibraryContent();
    }

    /// <summary>重建封面视图（每素材一张缩略图卡片）。</summary>
    private void RefreshAssetCovers()
    {
        _assetCoverPanel.Children.Clear();
        for (var i = 0; i < _assets.Count; i++)
        {
            _assetCoverPanel.Children.Add(BuildAssetCoverItem(_assets[i], i));
        }

        UpdateAssetCoverSelection();
    }

    /// <summary>构建单个封面卡片（缩略图 + 文件名；点击选中，按住可拖到时间轴）。</summary>
    private Border BuildAssetCoverItem(string path, int index)
    {
        var thumb = new Image
        {
            Width = 108,
            Height = 60,
            Stretch = Stretch.UniformToFill,
            ClipToBounds = true,
            Source = _assetThumbs.TryGetValue(path, out var cached) ? cached : null
        };
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 10,
            Opacity = 0.85,
            MaxWidth = 108,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var item = new Border
        {
            Width = 116,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            Padding = new Thickness(4),
            Child = new StackPanel { Spacing = 3, Children = { thumb, name } }
        };
        item.PointerPressed += (_, e) =>
        {
            // 左/右键都先选中（右键随后弹 per-item 菜单：查看媒体信息 / 删除）。
            _assetList.SelectedIndex = index;
            UpdateAssetCoverSelection();
            UpdateAssetButtons();
        };
        item.ContextFlyout = BuildAssetCoverMenu(path);
        // 封面项同样支持按住拖到时间轴（数据为素材路径，与列表视图一致）。
        item.PointerMoved += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var data = new DataObject();
            data.Set(DataFormats.Text, path);
            DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        };
        // 后台解码首帧做缩略图（带缓存，避免重复解码）。
        LoadAssetThumbnail(path, thumb);
        return item;
    }

    /// <summary>封面视图高亮当前选中的素材。</summary>
    private void UpdateAssetCoverSelection()
    {
        var selected = _assetList.SelectedIndex;
        for (var i = 0; i < _assetCoverPanel.Children.Count; i++)
        {
            if (_assetCoverPanel.Children[i] is Border b)
            {
                var isSel = i == selected;
                b.BorderBrush = isSel ? ThemePalette.AccentBrush() : Brushes.Transparent;
                b.BorderThickness = new Thickness(isSel ? 1.5 : 0);
            }
        }
    }

    /// <summary>后台加载素材封面缩略图（缓存；重复调用只取缓存）。图片直接解码，视频解码首帧。</summary>
    private void LoadAssetThumbnail(string path, Image target)
    {
        if (_assetThumbs.ContainsKey(path))
        {
            return; // 已有缓存，构建时已设置 Source。
        }

        if (VideoTranscoder.IsImageFile(path))
        {
            Task.Run(() =>
            {
                try
                {
                    // Avalonia Bitmap(stream)：读完即解码，可立即释放流。
                    using var stream = File.OpenRead(path);
                    var bmp = new Bitmap(stream);
                    Dispatcher.UIThread.Post(() =>
                    {
                        _assetThumbs[path] = bmp;
                        if (target.Source == null)
                        {
                            target.Source = bmp;
                        }
                    });
                }
                catch
                {
                    // 图片加载失败静默忽略（不显示缩略图）。
                }
            });
            return;
        }

        Task.Run(() =>
        {
            try
            {
                using var source = new VideoFrameSource();
                if (!source.Open(path, 256) || !source.TryReadFrame(out var frame) || frame == null)
                {
                    return;
                }

                var pixels = new byte[frame.Pixels.Length];
                Buffer.BlockCopy(frame.Pixels, 0, pixels, 0, pixels.Length);
                var w = frame.Width;
                var h = frame.Height;
                Dispatcher.UIThread.Post(() =>
                {
                    var bmp = CreateBitmap(pixels, w, h);
                    if (bmp == null)
                    {
                        return;
                    }

                    _assetThumbs[path] = bmp;
                    if (target.Source == null)
                    {
                        target.Source = bmp;
                    }
                });
            }
            catch
            {
                // 解码失败静默忽略（不显示缩略图）。
            }
        });
    }

    /// <summary>从 BGRA 像素字节数组创建 WriteableBitmap。</summary>
    private static WriteableBitmap? CreateBitmap(byte[] pixels, int w, int h)
    {
        try
        {
            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = bmp.Lock();
            var dst = fb.Address;
            var dstStride = fb.RowBytes;
            var srcStride = w * 4;
            if (srcStride == dstStride)
            {
                var len = Math.Min(pixels.Length, (int)(fb.RowBytes * h));
                Marshal.Copy(pixels, 0, dst, len);
            }
            else
            {
                for (var y = 0; y < h; y++)
                {
                    Marshal.Copy(pixels, y * srcStride, IntPtr.Add(dst, y * dstStride),
                        Math.Min(srcStride, dstStride));
                }
            }

            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ============ 素材库内容随工具切换（形状 / 滤镜）============

    /// <summary>按当前工具更新素材库内容：选择/文本→素材；形状→形状卡片；效果→滤镜卡片。</summary>
    private void UpdateLibraryContent()
    {
        var isLibraryMode = _currentTool is "shape" or "effect";
        var isAssetMode = !isLibraryMode;
        _assetList.IsVisible = isAssetMode && !_assetCoverView;
        _assetCoverScroll.IsVisible = isAssetMode && _assetCoverView;
        _libraryScroll.IsVisible = isLibraryMode;
        // 视图切换按钮只在素材模式下有意义。
        _assetViewToggle.IsVisible = isAssetMode;
        // 形状/滤镜库：卡片/列表切换。
        _libraryViewToggle.IsVisible = isLibraryMode;
        if (_libraryViewToggle.Content is IconText libIcon)
        {
            libIcon.Glyph = _libraryCardsView ? "\uEAC0" : "\uE929"; // 列表视图时显示「列表」图标
        }

        ToolTip.SetTip(_libraryViewToggle, _libraryCardsView ? "切换到列表视图" : "切换到缩略图视图");
        _libraryTitle.Text = _currentTool switch
        {
            "shape" => "形状库",
            "effect" => "滤镜库",
            _ => "素材库"
        };
        if (isLibraryMode)
        {
            RefreshLibraryCards();
        }
    }

    /// <summary>重建形状/滤镜卡片面板（卡片/列表两种视图；按当前工具）。</summary>
    private void RefreshLibraryCards()
    {
        _libraryPanel.Children.Clear();
        if (_currentTool == "shape")
        {
            foreach (var (key, name) in ShapeDefs)
            {
                _libraryPanel.Children.Add(_libraryCardsView
                    ? BuildLibraryCard(name, $"shape:{key}", BuildShapePreview(key), key == _currentShape)
                    : BuildLibraryRow(name, $"shape:{key}", key == _currentShape));
            }
        }
        else if (_currentTool == "effect")
        {
            foreach (var (key, name) in FilterDefs)
            {
                _libraryPanel.Children.Add(_libraryCardsView
                    ? BuildLibraryCard(name, $"filter:{key}", BuildFilterPreview(key), false)
                    : BuildLibraryRow(name, $"filter:{key}", false));
            }
        }
    }

    /// <summary>构建库列表行（紧凑：名称 + 拖到时间轴；点击选中与卡片一致）。</summary>
    private Border BuildLibraryRow(string name, string payload, bool selected)
    {
        var item = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderBrush = selected ? ThemePalette.AccentBrush() : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1.5 : 0),
            Padding = new Thickness(8, 5),
            Child = new TextBlock
            {
                Text = name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        item.PointerPressed += (_, _) =>
        {
            if (_currentTool == "shape")
            {
                _currentShape = payload["shape:".Length..];
                RefreshLibraryCards();
            }
        };
        item.PointerMoved += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var data = new DataObject();
            data.Set(DataFormats.Text, payload);
            DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        };
        return item;
    }

    /// <summary>构建库卡片（预览图 + 名称；点击选中，按住拖到时间轴）。</summary>
    private Border BuildLibraryCard(string name, string payload, Bitmap? preview, bool selected)
    {
        var img = new Image
        {
            Width = 96,
            Height = 54,
            Stretch = Stretch.Uniform,
            Source = preview
        };
        var item = new Border
        {
            Width = 104,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderBrush = selected ? ThemePalette.AccentBrush() : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1.5 : 0),
            Padding = new Thickness(4),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    img,
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 10,
                        Opacity = 0.85,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 96
                    }
                }
            }
        };
        item.PointerPressed += (_, _) =>
        {
            // 形状工具：点击卡片选中（舞台点击即放置该形状）。
            if (_currentTool == "shape")
            {
                _currentShape = payload["shape:".Length..];
                RefreshLibraryCards();
            }
        };
        // 按住拖到时间轴（数据为 shape:/filter: 前缀，时间轴放置时创建对应片段）。
        item.PointerMoved += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var data = new DataObject();
            data.Set(DataFormats.Text, payload);
            DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        };
        return item;
    }

    /// <summary>生成形状卡片预览（小尺寸渲染）。</summary>
    private Bitmap? BuildShapePreview(string key)
    {
        var clip = new VideoClip { Kind = "Shape", Shape = key, Color = "#FF6CB6FF" };
        var frame = OverlayFrameGenerator.Render(clip, 96, 54);
        return frame == null ? null : CreateBitmap(frame.Pixels, frame.Width, frame.Height);
    }

    /// <summary>生成滤镜卡片预览（彩色渐变 + 应用滤镜）。</summary>
    private Bitmap? BuildFilterPreview(string key)
    {
        const int w = 96;
        const int h = 54;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                pixels[i] = (byte)(x * 255 / w);
                pixels[i + 1] = (byte)(y * 255 / h);
                pixels[i + 2] = (byte)((x + y) * 255 / (w + h));
                pixels[i + 3] = 255;
            }
        }

        FilterUtils.ApplyInPlace(pixels, w, h, key);
        return CreateBitmap(pixels, w, h);
    }

    /// <summary>形状键 → 显示名。</summary>
    private static string ShapeName(string key)
    {
        foreach (var (k, n) in ShapeDefs)
        {
            if (k == key)
            {
                return n;
            }
        }

        return key;
    }

    /// <summary>滤镜键 → 显示名。</summary>
    private static string FilterName(string key)
    {
        foreach (var (k, n) in FilterDefs)
        {
            if (k == key)
            {
                return n;
            }
        }

        return key;
    }

    // ============ 时间轴（多轨）============

    private void RefreshTimeline()
    {
        var trackCount = _project.TrackCount;
        _lanes.Clear();
        _newTrackZone = null;
        // 保留纵向滚动偏移：轨道头与泳道已通过 ScrollChanged 同步平移（不会错位），
        // 拖到下方轨道释放后重建时间轴时不能跳回顶部（否则"很难把素材放到下面轨道"）。

        // 轨道头：视觉从上到下 = 数据轨从高到低（「轨道 1」= 最上层，图层面板习惯）。
        // 头部底缘 5px 热区：上下拖动调整该轨高度（每轨独立，存 _laneHeights）。
        // 注意：固定标尺在轨道头列上方独立行（timelineArea 左列 row0），因此这里不再留标尺空行。
        _trackHeaders.Children.Clear();
        _headerByTrack.Clear();
        for (var t = trackCount - 1; t >= 0; t--)
        {
            var trackIndex = t;
            var title = new TextBlock
            {
                Text = TrackName(t, trackCount),
                FontSize = 11,
                Opacity = 0.9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var resizeGrip = new Border
            {
                Height = 6,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth)
            };
            resizeGrip.PointerPressed += (_, e) =>
            {
                _resizeLane = trackIndex;
                _resizeLaneStartY = e.GetPosition(_trackHeaders).Y;
                _resizeLaneStartH = LaneHeightOf(trackIndex);
                e.Pointer.Capture(resizeGrip);
                e.Handled = true;
            };
            resizeGrip.PointerMoved += (_, e) =>
            {
                if (_resizeLane != trackIndex)
                {
                    return;
                }

                SetLaneHeight(trackIndex,
                    Math.Clamp(_resizeLaneStartH + e.GetPosition(_trackHeaders).Y - _resizeLaneStartY, 28, 220));
                e.Handled = true;
            };
            resizeGrip.PointerReleased += (_, e) =>
            {
                if (_resizeLane == trackIndex)
                {
                    _resizeLane = -1;
                    e.Pointer.Capture(null);
                    RefreshTimeline();
                    ScheduleSave();
                    e.Handled = true;
                }
            };
            var header = new Border
            {
                Height = LaneHeightOf(t),
                CornerRadius = new CornerRadius(4),
                Background = trackIndex == _selectedTrack
                    ? ThemePalette.AccentBrushWithAlpha(110)
                    : new SolidColorBrush(TrackHeaderIdleColor()),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Children = { title, resizeGrip }
                }
            };
            Grid.SetRow(resizeGrip, 1);
            header.PointerPressed += (_, _) =>
            {
                _selectedTrack = trackIndex;
                RefreshTimeline();
            };
            _trackHeaders.Children.Add(header);
            _headerByTrack[t] = header;
        }

        // 轨道泳道：每轨一个横向画布，片段按 StartTime 绝对定位。
        _timeline.RowDefinitions.Clear();
        _timeline.Children.Clear();
        // 宽度至少铺满视口（短工程也能把片段拖到最开头）。
        var totalWidth = Math.Max(_timelineViewportWidth, _project.Duration * _pxPerSecond + 120);
        for (var t = trackCount - 1; t >= 0; t--)
        {
            var trackIndex = t;
            var laneHeight = LaneHeightOf(t);
            var lane = new Border
            {
                Height = laneHeight,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(LaneFillColor()),
                ClipToBounds = true
            };
            var canvas = new Canvas { Width = totalWidth, Height = laneHeight };
            lane.Child = canvas;
            _lanes.Add(lane);
            // 视觉反转：数据轨号越大（越上层）显示在越上面。
            Grid.SetRow(lane, RowOfTrack(t, trackCount));
            _timeline.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _timeline.Children.Add(lane);

            // 泳道接受拖放：素材拖入 = 新增片段；片段拖入 = 移动到该轨道/位置。
            // DragOver 同时高亮目标泳道 + 更新落点标签 + 驱动边缘自动滚动（素材多轨道时拖到远处/新轨道）。
            DragDrop.SetAllowDrop(lane, true);
            lane.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
                UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
                UpdateDropHighlight(trackIndex, _project.TrackCount);
                e.Handled = true;
            });
            lane.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
            lane.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e, lane, trackIndex));
            // 点击泳道空白处：把播放头移到该位置（播放中则跳转）；点片段由块自己处理选中。
            lane.PointerPressed += (_, e) =>
            {
                if (e.Source == lane || e.Source == canvas)
                {
                    SetPlayhead(Math.Max(0, e.GetPosition(_timelineRoot).X / _pxPerSecond));
                }
            };

            foreach (var clip in _project.Clips.Where(c => c.Track == trackIndex).OrderBy(c => c.StartTime))
            {
                canvas.Children.Add(BuildClipBlock(clip));
            }
        }

        // 时间轴根画布：泳道网格 + 播放头（标尺已移出滚动区固定在面板顶部，见 _rulerHost），
        // 底部留「新建最底层轨道」拖放区（反转后底部 = 数据轨 0 下方，插入式新建）。
        var lanesHeight = 0.0;
        for (var t = 0; t < trackCount; t++)
        {
            lanesHeight += LaneHeightOf(t);
        }

        var contentHeight = lanesHeight + NewTrackZoneHeight + 8;
        _timelineRoot.Children.Clear();
        _timelineRoot.Width = totalWidth;
        _timelineRoot.Height = contentHeight;
        Canvas.SetTop(_timeline, 0);
        _timelineRoot.Children.Add(_timeline);
        // 标尺重建进固定宿主（宽度 = 内容宽；横向随滚动平移，纵向不动）。
        _rulerCanvas.Children.Clear();
        _rulerCanvas.Width = totalWidth;
        _rulerCanvas.Height = RulerHeight;
        _rulerCanvas.Children.Add(BuildRuler(totalWidth));
        var newTrackZone = new Border
        {
            Height = NewTrackZoneHeight,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = ThemePalette.AccentBrushWithAlpha(120),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "＋ 拖到这里在最底层新建轨道",
                FontSize = 11,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetTop(newTrackZone, lanesHeight + 4);
        Canvas.SetLeft(newTrackZone, 4);
        newTrackZone.Width = Math.Max(100, totalWidth - 8);
        _newTrackZone = newTrackZone;
        // 新建轨道区接受拖放：素材/片段落到这里 = 建一个新轨道并放置。
        DragDrop.SetAllowDrop(newTrackZone, true);
        newTrackZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
            UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
            UpdateDropHighlight(trackCount, trackCount);
            e.Handled = true;
        });
        newTrackZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
        newTrackZone.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e, newTrackZone, -1));
        _timelineRoot.Children.Add(newTrackZone);
        _timelineRoot.Children.Add(_playhead);
        _playhead.Height = contentHeight;
        Canvas.SetLeft(_playhead, _playheadTime * _pxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);

        // 输出尺寸与统计（单行精简文本）。
        _outputSizeText.Text = $"输出 {_project.OutputWidth:0}×{_project.OutputHeight:0}";
        _clipCountText.Text = $"轨道 {trackCount} · 片段 {_project.Clips.Count} · 总时长 {FormatClock(_project.Duration)}";
        _zoomText.Text = $"{_pxPerSecond / 6 * 100:0}%";
        // 轨道头部底部的「新建轨道」提示条（与拖放区对齐，仅提示不可交互）。
        _trackHeaders.Children.Add(new Border
        {
            Height = NewTrackZoneHeight,
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = "＋ 新建轨道",
                FontSize = 11,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

        // 时间轴面板行高：默认随内容自动；拖分割条后取用户高度（下限为内容所需，上限 460）。
        // 内容高 = 标尺（固定行）+ 泳道/新建轨道区（滚动区）。
        _timelineChromeHeight = 24 + TimelineToolbarHeight + 6 + RulerHeight + contentHeight;
        if (_timelinePanelHeight > 0 && _rootGrid != null)
        {
            _rootGrid.RowDefinitions[3] =
                new RowDefinition(new GridLength(Math.Min(460, Math.Max(_timelinePanelHeight, _timelineChromeHeight))));
        }

        // 非播放时刷新舞台预览帧：新增滤镜/覆盖层/拖入素材后，播放头所在时刻的画面立即反映
        // （之前拖入滤镜后舞台纹丝不动，效果"预览不出来"，要手动拖一下播放头才出现）。
        if (!_playing && _project.Clips.Count > 0)
        {
            ShowFrameAt(_playheadTime);
        }
    }

    /// <summary>构建时间轴顶部标尺（带主/次刻度与时间标签，随内容宽度滚动）。</summary>
    private Canvas BuildRuler(double width)
    {
        var ruler = new Canvas
        {
            Width = width,
            Height = RulerHeight,
            ClipToBounds = true
        };
        // 点击标尺：把播放头（seek 光条）移动到点击位置（与泳道空白处点击行为一致）。
        // 标尺 Canvas 自身随滚动平移，GetPosition(ruler) 即内容坐标（与泳道 0 点对齐）。
        ruler.PointerPressed += (_, e) =>
        {
            SetPlayhead(Math.Max(0, e.GetPosition(ruler).X / _pxPerSecond));
            e.Handled = true;
        };
        var interval = NiceTickInterval(_pxPerSecond);
        var half = interval / 2;
        var total = Math.Max(_project.Duration + 5, 20);
        var count = (int)Math.Ceiling(total / half);
        var majorBrush = new SolidColorBrush(Color.FromArgb(170, 190, 190, 200));
        var minorBrush = new SolidColorBrush(Color.FromArgb(90, 190, 190, 200));
        for (var i = 0; i <= count; i++)
        {
            var t = i * half;
            var x = t * _pxPerSecond;
            if (x > width)
            {
                break;
            }

            var isMajor = i % 2 == 0;
            ruler.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(x, RulerHeight - (isMajor ? 14 : 8)),
                EndPoint = new Point(x, RulerHeight),
                Stroke = isMajor ? majorBrush : minorBrush,
                StrokeThickness = 1
            });
            if (isMajor)
            {
                ruler.Children.Add(new TextBlock
                {
                    Text = FormatRulerTick(t),
                    FontSize = 9,
                    Opacity = 0.8,
                    Margin = new Thickness(x + 3, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top
                });
            }
        }

        return ruler;
    }

    /// <summary>标尺主刻度间隔：目标约 90px 一个刻度，取"好看"的步长。</summary>
    private static double NiceTickInterval(double pxPerSec)
    {
        var target = 90.0 / pxPerSec;
        foreach (var s in new[] { 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
        {
            if (s >= target)
            {
                return s;
            }
        }

        return 600;
    }

    /// <summary>标尺刻度标签（≥60s 显示分钟）。</summary>
    private static string FormatRulerTick(double t)
    {
        if (t >= 60)
        {
            var m = t / 60.0;
            return Math.Abs(m - Math.Round(m)) < 0.01 ? $"{m:0}m" : $"{m:0.#}m";
        }

        return Math.Abs(t - Math.Round(t)) < 0.01 ? $"{t:0}s" : $"{t:0.#}s";
    }

    /// <summary>把播放头移到指定时间（秒）；播放中同步跳转播放器，非播放时在舞台预览该时刻画面。</summary>
    private void SetPlayhead(double time)
    {
        _playheadTime = Math.Max(0, time);
        Canvas.SetLeft(_playhead, _playheadTime * _pxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);
        if (_playing && _player != null)
        {
            _player.Seek(_playheadTime);
        }
        else
        {
            ShowFrameAt(_playheadTime);
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
        Canvas.SetLeft(_playhead, _playheadTime * _pxPerSecond - 9); // 18px 热区以视觉线居中
        _timeText.Text = FormatTime(_playheadTime);
    }

    private string FormatTime(double seconds) => $"{FormatClock(seconds)} / {FormatClock(_project.Duration)}";

    /// <summary>时长格式化（xx:xx:xx 时:分:秒恒三段）。</summary>
    private static string FormatClock(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        return $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}";
    }

    /// <summary>未选中片段块的底色（随主题）。</summary>
    private static Color UnselectedBlockColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(90, 90, 90, 100)
        : Color.FromArgb(110, 205, 205, 210);

    /// <summary>滤镜片段块的底色（紫色调，区分普通片段）。</summary>
    private static Color FilterBlockColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(150, 110, 70, 200)
        : Color.FromArgb(170, 165, 115, 225);

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

    /// <summary>拖拽期间更新滚动探针（首次调用启动自动滚动计时器）。</summary>
    private void UpdateDragAutoScroll(Point pointerInScroll)
    {
        _lastDragPointer = pointerInScroll;
        if (_dragScrollProbe == null)
        {
            _dragScrollProbe = ComputeDragScroll;
            _dragScrollTimer.Start();
        }
    }

    /// <summary>停止拖拽自动滚动（释放/取消拖拽时调用）。</summary>
    private void StopDragAutoScroll()
    {
        _dragScrollProbe = null;
        _dragScrollTimer.Stop();
    }

    /// <summary>边缘检测：指针距滚动视口边缘 40px 内产生滚动增量（越贴边越快）。</summary>
    private (double Dx, double Dy) ComputeDragScroll()
    {
        const double edge = 40;
        const double speed = 14;
        double dx = 0, dy = 0;
        var w = _timelineScroll.Bounds.Width;
        var h = _timelineScroll.Bounds.Height;
        if (_lastDragPointer.X < edge)
        {
            dx = -speed * (1 - Math.Max(0, _lastDragPointer.X) / edge);
        }
        else if (_lastDragPointer.X > w - edge)
        {
            dx = speed * (1 - Math.Clamp(w - _lastDragPointer.X, 0, edge) / edge);
        }

        if (_lastDragPointer.Y < edge)
        {
            dy = -speed * (1 - Math.Max(0, _lastDragPointer.Y) / edge);
        }
        else if (_lastDragPointer.Y > h - edge)
        {
            dy = speed * (1 - Math.Clamp(h - _lastDragPointer.Y, 0, edge) / edge);
        }

        return (dx, dy);
    }

    /// <summary>拖拽落点提示标签（目标轨道号 / 新建轨道；accent 底白字，跟随目标泳道顶部移动）。</summary>
    private Border BuildDropTrackBadge()
    {
        return new Border
        {
            IsHitTestVisible = false,
            ZIndex = 40,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2),
            Background = ThemePalette.AccentBrushWithAlpha(235),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            }
        };
    }

    /// <summary>拖拽片段时高亮目标泳道 + 显示落点标签（反转后视觉位置按每轨高度累加）。
    /// mode：lane = 落既有轨；newTop = 在最上层新建；bottom = 在最底层插入。
    /// overlap = 目标位置被同轨素材占用，标签变红提示「释放后自动腾位」。</summary>
    private void UpdateDropHighlight(int targetTrack, int trackCount, bool overlap = false, string mode = "lane")
    {
        var highlight = mode == "newTop" ? trackCount - 1 : targetTrack;
        for (var i = 0; i < _lanes.Count; i++)
        {
            _lanes[i].Background = i == highlight
                ? ThemePalette.AccentBrushWithAlpha((byte)(overlap ? 110 : 90))
                : new SolidColorBrush(LaneFillColor());
        }

        if (_newTrackZone != null)
        {
            var isBottom = mode == "bottom";
            _newTrackZone.BorderThickness = new Thickness(isBottom ? 2 : 1);
            _newTrackZone.BorderBrush = isBottom
                ? ThemePalette.AccentBrush()
                : ThemePalette.AccentBrushWithAlpha(120);
        }

        if (_topTrackZone != null)
        {
            var isTop = mode == "newTop";
            _topTrackZone.BorderThickness = new Thickness(isTop ? 2 : 1);
            _topTrackZone.BorderBrush = isTop
                ? ThemePalette.AccentBrush()
                : ThemePalette.AccentBrushWithAlpha(120);
        }

        // 落点标签：拖拽块本身会遮挡泳道高亮，用一枚小标签直接标明会落到哪条轨道。
        if (_dropTrackBadge == null)
        {
            _dropTrackBadge = BuildDropTrackBadge();
        }

        var badge = _dropTrackBadge;
        if (badge.Parent != _timelineRoot)
        {
            _timelineRoot.Children.Add(badge);
        }

        if (badge.Child is TextBlock text)
        {
            text.Text = mode switch
            {
                "newTop" => "＋ 新轨道 1（最上层）",
                "bottom" => "＋ 新建最底层轨道",
                _ => overlap ? $"{TrackName(targetTrack, trackCount)}（占用）" : TrackName(targetTrack, trackCount)
            };
        }

        badge.Background = overlap
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23))
            : ThemePalette.AccentBrushWithAlpha(235);

        // 标签定位：视觉行按每轨高度累加（反转后最上行 = 最高轨）。
        var top = mode switch
        {
            "newTop" => 2,
            "bottom" => VisualTopOfTrack(0, trackCount) + LaneHeightOf(0) + 4,
            _ => VisualTopOfTrack(Math.Clamp(targetTrack, 0, trackCount - 1), trackCount) + 2
        };
        Canvas.SetTop(badge, top);
        Canvas.SetLeft(badge, 8);
        badge.IsVisible = true;
    }

    /// <summary>清除拖拽高亮（释放/取消时）。</summary>
    private void ClearDropHighlight()
    {
        foreach (var lane in _lanes)
        {
            lane.Background = new SolidColorBrush(LaneFillColor());
        }

        if (_newTrackZone != null)
        {
            _newTrackZone.BorderThickness = new Thickness(1);
            _newTrackZone.BorderBrush = ThemePalette.AccentBrushWithAlpha(120);
        }

        if (_dropTrackBadge is { } badge)
        {
            badge.IsVisible = false;
        }

        StopDragAutoScroll();
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
                    Text = ClipDisplayName(clip),
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
            Width = Math.Max(30, clip.Duration * _pxPerSecond),
            Height = _laneHeight - 12,
            CornerRadius = new CornerRadius(6),
            Background = isSelected
                ? ThemePalette.AccentBrushWithAlpha(170)
                : new SolidColorBrush(clip.Kind == "Filter" ? FilterBlockColor() : UnselectedBlockColor()),
            BorderBrush = isSelected ? Brushes.White : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = new Grid { Children = { content, leftHandle, rightHandle } }
        };
        Canvas.SetLeft(block, clip.StartTime * _pxPerSecond);
        Canvas.SetTop(block, 6);

        // 点击选中 + 按下准备拖拽（指针捕获 + 跟手实时移动，不用 DragDrop，稳定可靠）。
        // 按下只记录拖动起点并捕获指针，不挪动块；真正开始拖动（PointerMoved）时才浮到根画布——
        // 原泳道 ClipToBounds 会裁剪跨泳道浮动，因此需要根画布。纯点击不触碰视觉树，
        // 避免捕获被中途夺走/丢失后块从画布上消失（下次 RefreshTimeline 才恢复）。
        block.PointerPressed += (_, e) =>
        {
            _selected = clip;
            var grab = e.GetPosition(block);
            _dragOffsetX = grab.X;
            var rootPos = e.GetPosition(_timelineRoot);
            _moveDrag = (clip, rootPos.X, grab.Y, clip.StartTime);
            _moveUndoPushed = false;
            e.Pointer.Capture(block);
            ApplyBlockSelected(block, leftHandle, rightHandle, clip);
            FillPropertyPanel();
            UpdateStageHandles();
        };
        // 裁剪手柄按下：记录裁剪状态并阻止冒泡（避免触发块选中/拖拽）。
        leftHandle.PointerPressed += (_, e) =>
        {
            _trimState = (clip, false, e.GetPosition(block).X, block);
            _trimUndoPushed = false;
            e.Handled = true;
        };
        rightHandle.PointerPressed += (_, e) =>
        {
            _trimState = (clip, true, e.GetPosition(block).X, block);
            _trimUndoPushed = false;
            e.Handled = true;
        };
        // 块上移动/释放：优先裁剪，其次拖拽移动（块横向跟手 + 纵向跨泳道跟手 + 目标泳道高亮）。
        block.PointerMoved += (_, e) =>
        {
            if (_trimState is { } trim && ReferenceEquals(trim.Clip, clip))
            {
                var x = e.GetPosition(block).X;
                var delta = (x - trim.LastX) / _pxPerSecond;
                _trimState = (clip, trim.IsOut, x, block);
                // 首次实际裁剪才压撤销（按下不压，避免空拖拽污染栈）。
                if (!_trimUndoPushed)
                {
                    PushUndo();
                    _trimUndoPushed = true;
                }

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
                ClearDropHighlight();
                return;
            }

            var rootPos = e.GetPosition(_timelineRoot);
            // 首次实际移动才压撤销（按下仅选中，不压栈）。
            if (!_moveUndoPushed)
            {
                PushUndo();
                _moveUndoPushed = true;
            }

            // 开始拖动时才把块浮到根画布（跨泳道需要；纯点击不挪动，避免捕获丢失导致片段消失）。
            if (block.Parent != _timelineRoot)
            {
                if (block.Parent is Panel p)
                {
                    p.Children.Remove(block);
                }

                block.ZIndex = 30; // 高于播放头（20），拖动时浮在所有泳道上
                block.Opacity = 0.8; // 半透明：目标泳道的高亮/落点标签在块下仍可见
                _timelineRoot.Children.Add(block);
                // 拖拽启动自动滚动（贴滚动区边缘时滚动，含纵向滚到「新建轨道」区）。
                UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
            }
            else
            {
                UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
            }

            // 横向：时间 = 指针 X - 按下偏移；拖动中实时吸附（片段边缘/播放头 ~8px）且不避让——
            // 旧版实时 FitToTrack 避让会把块从目标位置弹开，导致「很难把 B 拖到 A 正上方对齐」。
            var desired = Math.Max(0, (rootPos.X - md.GrabX) / _pxPerSecond);
            clip.StartTime = SnapTime(desired);
            Canvas.SetLeft(block, clip.StartTime * _pxPerSecond);
            // 纵向：块顶 = 指针 Y - 按下偏移（跨泳道浮动跟手；泳道区顶 = _timelineRoot 顶，无标尺偏移）。
            var blockTop = Math.Max(0, rootPos.Y - md.GrabY);
            Canvas.SetTop(block, blockTop);
            // 目标轨道按「块的视觉顶部」计算（与落轨一致）。
            // 视觉反转：最上行为最高轨（轨道 1），拖到其上半部 = 在上方新建最上层轨道。
            var trackCount = _project.TrackCount;
            var (targetTrack, newTop, bottomInsert) = ResolveDropTarget(blockTop, trackCount);
            var overlap = !newTop && !bottomInsert && !FitsOnTrack(targetTrack, clip.StartTime, clip.Duration, clip);
            var mode = newTop ? "newTop" : bottomInsert ? "bottom" : "lane";
            UpdateDropHighlight(targetTrack, trackCount, overlap, mode);
        };
        block.PointerReleased += (_, e) =>
        {
            if (_trimState is { } t && ReferenceEquals(t.Clip, clip))
            {
                _trimState = null;
                RefreshTimeline();
                FillPropertyPanel();
                ScheduleSave();
                return;
            }

            if (_moveDrag is { } md && ReferenceEquals(md.Clip, clip))
            {
                _moveDrag = null;
                // 记录块是否已浮到根画布（发生过实际拖动）；Capture(null) 会触发 PointerCaptureLost，
                // 其中可能重建时间轴，因此先记录再解绑。
                var wasFloating = block.Parent == _timelineRoot;
                StopDragAutoScroll();
                e.Pointer.Capture(null);
                if (wasFloating)
                {
                    // 释放：吸附 → 按块的视觉顶部落轨（与拖动时显示一致）。
                    // 同轨不允许堆叠：目标位置被占用时自动挪到最近空位（FitToTrack，排除自身）。
                    clip.StartTime = SnapTime(clip.StartTime);
                    var rootPos = e.GetPosition(_timelineRoot);
                    var blockTop = Math.Max(0, rootPos.Y - md.GrabY);
                    var trackCount = _project.TrackCount;
                    var (targetTrack, newTop, bottomInsert) = ResolveDropTarget(blockTop, trackCount);
                    EditorLog($"释放落轨 y={blockTop:0.#} → track={targetTrack} newTop={newTop} bottom={bottomInsert} " +
                              $"StartTime={clip.StartTime:0.##} 占用={!FitsOnTrack(targetTrack, clip.StartTime, clip.Duration, clip)}");
                    if (bottomInsert)
                    {
                        // 最底层插入新轨：现有全部下移一格。
                        foreach (var c in _project.Clips)
                        {
                            c.Track++;
                        }

                        clip.Track = 0;
                    }
                    else if (newTop)
                    {
                        clip.Track = trackCount; // 新最高轨（视觉「轨道 1」）。
                    }
                    else
                    {
                        clip.Track = Math.Clamp(targetTrack, 0, 32);
                    }

                    if (!FitsOnTrack(clip.Track, clip.StartTime, clip.Duration, clip))
                    {
                        clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
                        _statusText.Text = "目标位置与同轨素材重叠，已自动放到最近空位（素材不会堆叠）。";
                    }

                    EditorLog($"落轨完成 track={clip.Track} StartTime={clip.StartTime:0.##}");
                }

                ClearDropHighlight();
                // 空轨自动删除 + 轨道号压缩为连续；随后刷新并保存（同时清掉临时挂到根画布的块）。
                CompactTracks();
                RefreshTimeline();
                FillPropertyPanel();
                ScheduleSave();
            }
        };
        block.PointerCaptureLost += (_, _) =>
        {
            _moveDrag = null;
            block.Opacity = 1;
            ClearDropHighlight();
            // 若块仍悬浮在根画布上（拖拽异常中断 / 捕获被夺走），立即重建时间轴把片段恢复回泳道，
            // 避免块从时间轴上消失（只移除不重建会导致下次刷新前一直不可见）。
            if (block.Parent == _timelineRoot)
            {
                RefreshTimeline();
            }
        };
        return block;
    }

    /// <summary>直接更新片段块的选中样式（不重建整条时间轴，避免打断拖拽）。</summary>
    private void ApplyBlockSelected(Border block, Border leftHandle, Border rightHandle, VideoClip clip)
    {
        var selected = ReferenceEquals(clip, _selected);
        block.Background = selected
            ? ThemePalette.AccentBrushWithAlpha(170)
            : new SolidColorBrush(clip.Kind == "Filter" ? FilterBlockColor() : UnselectedBlockColor());
        block.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
        leftHandle.IsVisible = selected;
        rightHandle.IsVisible = selected;
    }

    /// <summary>拖拽裁剪：按拖动秒数增量调整入/出点，即时更新块宽与时长文本（不重建，避免丢失拖拽状态）。
    /// 入/出点被限制在同轨相邻素材边界内（防拉长堆叠）。</summary>
    private void ApplyTrim(VideoClip clip, bool isOut, double deltaSeconds, Border block, TextBlock durationText)
    {
        var (inLimit, outLimit) = TrimBounds(clip);
        if (isOut)
        {
            clip.OutPoint = Math.Clamp(clip.OutPoint + deltaSeconds, clip.InPoint + 0.1, outLimit);
        }
        else
        {
            clip.InPoint = Math.Clamp(clip.InPoint + deltaSeconds, Math.Max(0, inLimit), clip.OutPoint - 0.1);
        }

        block.Width = Math.Max(30, clip.Duration * _pxPerSecond);
        durationText.Text = $"{clip.StartTime:0.#}s · {clip.Duration:0.#}s";
        _clipNameText.Text = $"{ClipDisplayName(clip)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s";
        if (_playing && _player != null && clip.Track < _stageLayers.Count)
        {
            ApplyTransform(_stageLayers[clip.Track], clip);
        }
    }

    /// <summary>
    /// 裁剪边界：入点不能越过同轨左侧素材的末尾、出点不能越过右侧素材的开头（防堆叠），
    /// 同时受素材媒体时长限制。返回（入点下限, 出点上限）——均为媒体时间。
    /// </summary>
    private (double InLimit, double OutLimit) TrimBounds(VideoClip clip)
    {
        var maxOut = _assetDurations.TryGetValue(clip.SourcePath, out var d) && d > 0 ? d : double.MaxValue;
        var others = _project.Clips.Where(c => c.Track == clip.Track && !ReferenceEquals(c, clip)).ToList();
        var leftBound = clip.StartTime + clip.InPoint;  // 片段轨道起点
        var prevEnd = others.Where(c => c.StartTime + c.Duration <= leftBound + 0.01)
            .Select(c => c.StartTime + c.Duration).DefaultIfEmpty(0).Max();
        var rightStart = clip.StartTime + clip.OutPoint;  // 片段轨道末端
        var nextStart = others.Where(c => c.StartTime >= rightStart - 0.01)
            .Select(c => c.StartTime).DefaultIfEmpty(double.MaxValue).Min();
        return (Math.Max(0, prevEnd - clip.StartTime), Math.Min(maxOut, nextStart - clip.StartTime + clip.InPoint));
    }

    /// <summary>视觉行 → 数据轨号：轨道号越大（越上层）显示在越上面（图层面板习惯）。</summary>
    private static int RowOfTrack(int track, int trackCount) => trackCount - 1 - track;

    /// <summary>数据轨号 → 视觉行。</summary>
    private static int RowOfTrackInverse(int track, int trackCount) => trackCount - 1 - track;

    /// <summary>轨道显示名：最上层（数据轨号最大）= 「轨道 1」。</summary>
    private static string TrackName(int track, int trackCount) => $"轨道 {trackCount - track}";

    /// <summary>把泳道区内的纵向坐标解析为落点：返回（数据轨号, 是否新最高轨, 是否底部插入最底层）。</summary>
    private (int Track, bool NewTop, bool BottomInsert) ResolveDropTarget(double y, int trackCount)
    {
        var acc = 0.0;
        for (var row = 0; row < trackCount; row++)
        {
            var t = RowOfTrackInverse(row, trackCount);
            var h = LaneHeightOf(t);
            if (y < acc + h)
            {
                // 拖到最上层行的上半部 = 在「轨道 1」上方新建最上层轨道。
                if (row == 0 && y < acc + h / 2)
                {
                    return (trackCount, true, false);
                }

                return (t, false, false);
            }

            acc += h;
        }

        return (0, false, true); // 底部拖放区：在最底层插入新轨（现有全部下移一格）。
    }

    /// <summary>把泳道区内的纵向坐标解析为所在轨的视觉顶部（落点标签定位用）。</summary>
    private double VisualTopOfTrack(int track, int trackCount)
    {
        var row = RowOfTrack(track, trackCount);
        var acc = 0.0;
        for (var r = 0; r < row; r++)
        {
            acc += LaneHeightOf(RowOfTrackInverse(r, trackCount));
        }

        return acc;
    }

    /// <summary>拖拽调高：写入每轨高度并同步泳道/头部/总高（拖动中不重建时间轴，避免打断捕获）。</summary>
    private void SetLaneHeight(int track, double h)
    {
        _laneHeights[track] = h;
        if (track < _lanes.Count)
        {
            _lanes[track].Height = h;
            if (_lanes[track].Child is Canvas canvas)
            {
                canvas.Height = h;
            }
        }

        if (_headerByTrack.TryGetValue(track, out var header))
        {
            header.Height = h;
        }

        var trackCount = _project.TrackCount;
        var lanesHeight = 0.0;
        for (var t = 0; t < trackCount; t++)
        {
            lanesHeight += LaneHeightOf(t);
        }

        _timelineRoot.Height = lanesHeight + NewTrackZoneHeight + 8;
        _playhead.Height = lanesHeight + NewTrackZoneHeight + 8;
        if (_newTrackZone != null)
        {
            Canvas.SetTop(_newTrackZone, lanesHeight + 4);
        }
    }

    /// <summary>时间轴泳道/新建轨道区放置处理：素材 → 新增片段；clip:n → 移动既有片段。</summary>
    private void HandleTimelineDrop(DragEventArgs e, Border lane, int trackIndex)
    {
        StopDragAutoScroll(); // 系统拖放结束：先停自动滚动（否则 Drop 后仍按最后指针位置滚动）。
        if (!e.Data.Contains(DataFormats.Text))
        {
            return;
        }

        var text = e.Data.Get(DataFormats.Text)?.ToString() ?? "";
        PushUndo();
        // 落点区：-1 = 底部拖放区（在最底层插入新轨，现有全部下移一格）；-2 = 顶部区（新建最上层轨道）。
        if (trackIndex == -1)
        {
            foreach (var c in _project.Clips)
            {
                c.Track++;
            }

            trackIndex = 0;
        }
        else if (trackIndex == -2)
        {
            trackIndex = _project.TrackCount; // 新最高轨（视觉「轨道 1」）。
        }

        // 用时间轴根坐标计算落点（0 = 时间轴开头）；拖动既有片段时减去按下点偏移，让块跟手。
        var isMove = text.StartsWith("clip:", StringComparison.Ordinal);
        var rawX = e.GetPosition(_timelineRoot).X - (isMove ? _dragOffsetX : 0);
        var startTime = SnapTime(Math.Max(0, rawX / _pxPerSecond));

        if (isMove &&
            int.TryParse(text.AsSpan(5), out var index) &&
            index >= 0 && index < _project.Clips.Count)
        {
            // 移动既有片段到该轨道/位置（trackIndex 可等于当前 TrackCount = 新建轨道）。
            var clip = _project.Clips[index];
            clip.Track = Math.Max(0, trackIndex);
            clip.StartTime = startTime;
            // 同轨不允许堆叠：落点被占时自动挪到最近空位。
            if (!FitsOnTrack(clip.Track, clip.StartTime, clip.Duration, clip))
            {
                clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
                _statusText.Text = "目标位置与同轨素材重叠，已自动放到最近空位（素材不会堆叠）。";
            }

            _selected = clip;
        }
        else if (text.StartsWith("shape:", StringComparison.Ordinal))
        {
            // 形状库拖入：在目标轨道/位置新增形状覆盖层片段。
            var shape = text["shape:".Length..];
            var clip = new VideoClip
            {
                Kind = "Shape",
                Shape = shape,
                Color = "#FFFFEB3B",
                Track = Math.Max(0, trackIndex),
                StartTime = startTime,
                InPoint = 0,
                OutPoint = 5,
                Scale = 0.4,
                ScaleX = 1,
                ScaleY = 1
            };
            clip.StartTime = FitToTrack(clip, startTime, clip.Track);
            _project.Clips.Add(clip);
            _selected = clip;
        }
        else if (text.StartsWith("filter:", StringComparison.Ordinal))
        {
            // 滤镜库拖入：始终放到最高的新轨道（滤镜作用于其下方所有画面）。
            var filter = text["filter:".Length..];
            var clip = new VideoClip
            {
                Kind = "Filter",
                Filter = filter,
                Track = _project.TrackCount,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = 5
            };
            _project.Clips.Add(clip);
            _selected = clip;
            // 滤镜只作用于更低轨道的画面；下方没有视频片段时提前说明（否则"加了特效没反应"）。
            var affects = _project.Clips.Any(c => c.Kind == "Video" && c.Track < clip.Track &&
                                                  clip.StartTime < c.StartTime + c.Duration &&
                                                  clip.StartTime + clip.Duration > c.StartTime);
            _statusText.Text = affects
                ? $"已添加「{FilterName(clip.Filter)}」滤镜（作用于其下方轨道 {clip.StartTime:0.#}s 起的画面）。"
                : "提示：滤镜只作用于更低轨道的画面——先把视频片段放到更低的轨道，滤镜才会生效。";
        }
        else if (_assets.Contains(text))
        {
            // 素材拖入：目标轨道/位置新增片段（同轨不重叠，自动放到最近可用位置）。
            // 图片素材建 Kind=Image 覆盖层（固定 5 秒）；视频按素材时长建普通片段。
            var isImage = VideoTranscoder.IsImageFile(text);
            var duration = isImage ? 5 : GetAssetDuration(text);
            var clip = new VideoClip
            {
                Kind = isImage ? "Image" : "Video",
                SourcePath = text,
                Track = Math.Max(0, trackIndex),
                StartTime = startTime,
                InPoint = 0,
                OutPoint = isImage ? duration : duration > 0.5 ? duration : 10
            };
            clip.StartTime = FitToTrack(clip, startTime, clip.Track);
            _project.Clips.Add(clip);
            _selected = clip;
        }

        // 空轨自动删除 + 轨道号压缩为连续；随后刷新并保存。
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
        e.Handled = true;
    }

    /// <summary>时间吸附：距离时间轴开头、任意片段边缘（头/尾）或播放头约 8px 内自动对齐，便于头贴尾拼接/素材上下对齐。
    /// 阈值按像素换算（8px / pxPerSecond）：时间轴缩放后吸附手感一致。</summary>
    private double SnapTime(double time)
    {
        var best = time;
        var bestDist = 8.0 / Math.Max(1.0, _pxPerSecond);
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

        PushUndo();
        _selected.Track--;
        CompactTracks();
        RefreshTimeline();
        ScheduleSave();
    }

    private void MoveClipDown()
    {
        if (_selected == null)
        {
            return;
        }

        PushUndo();
        _selected.Track++;
        CompactTracks();
        RefreshTimeline();
        ScheduleSave();
    }

    private void DeleteSelectedClip()
    {
        if (_selected == null)
        {
            return;
        }

        PushUndo();
        _project.Clips.Remove(_selected);
        _selected = null;
        // 删片段可能留下空轨：自动删除并压缩轨道号。
        CompactTracks();
        RefreshTimeline();
        ClearSelection();
        ScheduleSave();
    }

    private void ClearTimeline()
    {
        PushUndo();
        _project.Clips.Clear();
        _selected = null;
        CompactTracks();
        RefreshTimeline();
        ClearSelection();
        StopPreview();
        ScheduleSave();
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

        PushUndo();
        foreach (var clip in toRemove)
        {
            _project.Clips.Remove(clip);
        }

        _project.Clips.AddRange(toAdd);
        if (ReferenceEquals(_selected, toRemove[0]))
        {
            _selected = toAdd[0]; // 选中左段。
        }

        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
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
                ? $"{ClipDisplayName(clip!)}\n{TrackName(clip!.Track, _project.TrackCount)} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s"
                : "未选中片段。在底部时间轴点击一个片段，或从左侧素材库添加。";
            _inSpin.DoubleValue = clip?.InPoint ?? 0;
            _outSpin.DoubleValue = clip?.OutPoint ?? 10;
            _scaleSpin.DoubleValue = clip?.Scale ?? 1;
            _scaleXSpin.DoubleValue = clip?.ScaleX ?? 1;
            _scaleYSpin.DoubleValue = clip?.ScaleY ?? 1;
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

            _inspectorTabs.IsEnabled = has;

            // 图片/文本/形状覆盖层编辑区：仅这三类显示（覆盖层 Tab）。
            var isOverlay = clip is { Kind: "Text" or "Shape" or "Image" };
            _overlayTab.IsVisible = isOverlay;
            if (isOverlay)
            {
                _overlayText.Text = clip!.Text;
                _overlayColor.Text = clip.Color;
                _overlayShape.SelectedItem = clip.Shape;
                _overlayText.IsEnabled = clip.Kind == "Text";
                _overlayShape.IsEnabled = clip.Kind == "Shape";
                _overlayColor.IsEnabled = clip.Kind != "Image";
                // 图片覆盖层：隐藏内容/颜色/形状行（变换与不透明度在通用属性里）。
                if (_overlayTextRow is { } textRow)
                {
                    textRow.IsVisible = clip.Kind == "Text";
                }

                if (_overlayShapeRow is { } shapeRow)
                {
                    shapeRow.IsVisible = clip.Kind == "Shape";
                }

                if (_overlayColorRow is { } colorRow)
                {
                    colorRow.IsVisible = clip.Kind != "Image";
                }
            }

            // 滤镜编辑区：仅 Kind=Filter 显示（滤镜 Tab）。
            var isFilter = clip is { Kind: "Filter" };
            _filterTab.IsVisible = isFilter;
            if (isFilter)
            {
                var fi = Array.FindIndex(FilterDefs, d => d.Key == clip!.Filter);
                _filterCombo.SelectedIndex = fi < 0 ? 0 : fi;
            }

            // 按片段类型自动切换到对应 Tab（仅选中片段变化时，避免用户手动切 Tab 被抢回）。
            if (!ReferenceEquals(_lastInspectorClip, clip))
            {
                _lastInspectorClip = clip;
                if (isOverlay)
                {
                    _inspectorTabs.SelectedItem = _overlayTab;
                }
                else if (isFilter)
                {
                    _inspectorTabs.SelectedItem = _filterTab;
                }
                else
                {
                    _inspectorTabs.SelectedItem = _transformTab;
                }
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

        // 数值框连续调整合并为一步撤销。
        PushUndo(true);
        var clip = _selected;
        // 入/出点限制在同轨相邻素材边界内（防堆叠）。
        var (inLimit, outLimit) = TrimBounds(clip);
        clip.InPoint = Math.Clamp(Math.Max(0, _inSpin.DoubleValue), Math.Max(0, inLimit), clip.OutPoint - 0.1);
        clip.OutPoint = Math.Clamp(Math.Max(clip.InPoint + 0.1, _outSpin.DoubleValue), clip.InPoint + 0.1, outLimit);
        clip.Scale = _scaleSpin.DoubleValue;
        clip.ScaleX = _scaleXSpin.DoubleValue;
        clip.ScaleY = _scaleYSpin.DoubleValue;
        clip.OffsetX = _offsetXSpin.DoubleValue;
        clip.OffsetY = _offsetYSpin.DoubleValue;
        clip.Rotation = _rotationSpin.DoubleValue;
        clip.Opacity = Math.Clamp(_opacitySpin.DoubleValue, 0, 1);
        clip.CropLeft = Math.Clamp(_cropLSpin.DoubleValue, 0, 1);
        clip.CropTop = Math.Clamp(_cropTSpin.DoubleValue, 0, 1);
        clip.CropRight = Math.Clamp(_cropRSpin.DoubleValue, 0, 1);
        clip.CropBottom = Math.Clamp(_cropBSpin.DoubleValue, 0, 1);
        RefreshTimeline();
        ScheduleSave();
        _clipNameText.Text = $"{ClipDisplayName(clip)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n入 {clip.InPoint:0.#}s → 出 {clip.OutPoint:0.#}s";
        // 舞台实时预览：只要该轨道已有画面（含暂停/未播放），立即应用变换并刷新八向手柄。
        // （之前用 _playing && _player 条件，暂停时调整缩放/偏移完全不更新舞台。）
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            ApplyTransform(_stageLayers[clip.Track], clip);
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

        // 文本/形状覆盖层：直接按舞台比例生成静态帧（无需解码）。
        var track = clip.Track;
        if (clip.Kind != "Video")
        {
            var frame = GenerateOverlayFrame(clip);
            if (frame != null)
            {
                UpdateStageLayer(track, ApplyActiveFilter(frame, clip, clip.StartTime), clip);
                UpdateStageHandles();
            }

            return;
        }

        var path = clip.SourcePath;
        var inPoint = clip.InPoint;
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

                    UpdateStageLayer(track, ApplyActiveFilter(new VideoFrame(pixels, w, h), clip, clip.StartTime), clip);
                    UpdateStageHandles();
                });
            }
            catch
            {
                // 解码失败静默忽略（不影响编辑）。
            }
        });
    }

    /// <summary>按舞台比例生成覆盖层预览帧（文本/形状；UI 线程调用）。</summary>
    private VideoFrame? GenerateOverlayFrame(VideoClip clip)
    {
        var w = (int)_stageBorder.Bounds.Width;
        var h = (int)_stageBorder.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            // 舞台尚未布局：按输出比例回退。
            var aspect = _project.OutputWidth / Math.Max(1.0, _project.OutputHeight);
            w = 800;
            h = Math.Max(2, (int)(800 / aspect));
        }

        return OverlayFrameGenerator.Render(clip, w, h);
    }

    /// <summary>
    /// 非播放时在舞台预览播放头所在时刻的画面：对每个轨道上覆盖该时刻的片段后台解码
    /// 对应帧（片段内媒体时间 = InPoint + (time - StartTime)），无覆盖片段的轨道隐藏。
    /// 用「代次 + 单 worker」合并高频 scrub：同一时刻只解最新一帧，不堆积任务。
    /// </summary>
    private void ShowFrameAt(double time)
    {
        if (_playing || _project.Clips.Count == 0)
        {
            return;
        }

        _seekFrameGen++;
        if (_seekFrameBusy)
        {
            // 上一帧还在解：只记录最新位置，解完后再补一帧。
            _seekFramePendingTime = time;
            _seekFrameHasPending = true;
            return;
        }

        StartSeekFrameDecode(time);
    }

    /// <summary>当前时刻最上层的滤镜片段（轨道号最大；同轨取起始最晚）。</summary>
    private VideoClip? FindActiveFilter(double time)
    {
        VideoClip? best = null;
        foreach (var clip in _project.Clips)
        {
            if (clip.Kind != "Filter")
            {
                continue;
            }

            if (time >= clip.StartTime && time < clip.StartTime + clip.Duration)
            {
                if (best == null || clip.Track > best.Track ||
                    (clip.Track == best.Track && clip.StartTime >= best.StartTime))
                {
                    best = clip;
                }
            }
        }

        return best;
    }

    /// <summary>滤镜结果缓冲（UI 线程专用；持续滤镜期间复用，避免每帧分配 ~1.4MB 触发 GC 抖动）。</summary>
    private byte[]? _filterBuffer;

    /// <summary>滤镜判定日志节流时间戳。</summary>
    private DateTimeOffset _lastFilterLog;

    /// <summary>对帧应用当前时刻更高轨道的滤镜（返回应用结果；无滤镜时原样返回）。
    /// 判定结果节流写入 video-editor.log（诊断「滤镜无显示」：无滤镜片段 / 轨道位置不生效 / 已应用）。</summary>
    private VideoFrame ApplyActiveFilter(VideoFrame frame, VideoClip clip, double time)
    {
        var filter = FindActiveFilter(time);
        var applied = filter != null && filter.Track > clip.Track && !string.IsNullOrEmpty(filter.Filter);
        if ((DateTimeOffset.Now - _lastFilterLog).TotalMilliseconds > 1500)
        {
            _lastFilterLog = DateTimeOffset.Now;
            EditorLog($"t={time:0.##}s 片段轨{clip.Track}({clip.Kind}) → " +
                      $"滤镜={(filter == null ? "无" : $"{filter.Filter}@轨{filter.Track} [{filter.StartTime:0.#}~{filter.StartTime + filter.Duration:0.#}s]")} " +
                      $"应用={applied}");
        }

        if (!applied)
        {
            return frame;
        }

        _filterBuffer ??= new byte[frame.Pixels.Length];
        if (_filterBuffer.Length != frame.Pixels.Length)
        {
            _filterBuffer = new byte[frame.Pixels.Length];
        }

        Buffer.BlockCopy(frame.Pixels, 0, _filterBuffer, 0, _filterBuffer.Length);
        FilterUtils.ApplyInPlace(_filterBuffer, frame.Width, frame.Height, filter!.Filter);
        return new VideoFrame(_filterBuffer, frame.Width, frame.Height);
    }

    private void StartSeekFrameDecode(double time)
    {
        _seekFrameBusy = true;
        // 覆盖层帧尺寸（保持输出比例，后台线程不可访问 UI）。
        var overlayAspect = _project.OutputWidth / Math.Max(1.0, _project.OutputHeight);
        var overlayW = PreviewMaxDimension;
        var overlayH = Math.Max(2, (int)(PreviewMaxDimension / overlayAspect));
        Task.Run(() =>
        {
            var results = new List<(int Track, VideoFrame Frame, VideoClip Clip)>();
            try
            {
                var maxTrack = _project.Clips.Max(c => c.Track);
                for (var t = 0; t <= maxTrack; t++)
                {
                    var clip = _project.Clips
                        .Where(c => c.Track == t && time >= c.StartTime && time < c.StartTime + c.Duration)
                        .OrderByDescending(c => c.StartTime)
                        .FirstOrDefault();
                    if (clip == null)
                    {
                        continue;
                    }

                    // 文本/形状覆盖层：直接生成静态帧。
                    if (clip.Kind != "Video")
                    {
                        var of = OverlayFrameGenerator.Render(clip, overlayW, overlayH);
                        if (of != null)
                        {
                            results.Add((t, of, clip));
                        }

                        continue;
                    }

                    var mediaTime = clip.InPoint + (time - clip.StartTime);
                    // 复用已打开的源；定位在 GetSeekSource 内部完成（微移=顺序读帧，大跳=seek）。
                    var source = GetSeekSource(t, clip, mediaTime);
                    if (source == null)
                    {
                        continue;
                    }

                    if (source.TryReadFrame(out var frame) && frame != null)
                    {
                        var pixels = new byte[frame.Pixels.Length];
                        Buffer.BlockCopy(frame.Pixels, 0, pixels, 0, pixels.Length);
                        results.Add((t, new VideoFrame(pixels, frame.Width, frame.Height), clip));
                    }
                }
            }
            catch
            {
                // 解码失败静默忽略（不影响 scrub）。
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // 单 worker 串行解码：结果按请求顺序到达且始终是最新完成的一帧。
                    // 拖动（scrub）时必须立即应用（哪怕稍旧），不能只等松手才显示。始终应用。
                    ApplySeekFrames(results);
                }
                catch
                {
                    // 位图释放等竞态：忽略。
                }
                finally
                {
                    _seekFrameBusy = false;
                    if (_seekFrameHasPending)
                    {
                        _seekFrameHasPending = false;
                        StartSeekFrameDecode(_seekFramePendingTime);
                    }
                    else if (!_scrubbing)
                    {
                        // 已结束 scrub 且没有后续请求：释放持久解码源。
                        DisposeSeekSources();
                    }
                }
            });
        });
    }

    /// <summary>
    /// 获取某轨道的 seek 解码源并定位到媒体时间（同轨同片段复用解码器）。
    /// 微移（±3 帧内）用顺序读帧推进——反复 SeekTo 会回退到关键帧并重解整段 GOP，
    /// 长视频 scrub 会越来越卡；大跳/后退才真正 seek。
    /// </summary>
    private VideoFrameSource? GetSeekSource(int track, VideoClip clip, double mediaTime)
    {
        if (_seekSources.TryGetValue(track, out var entry) && ReferenceEquals(entry.Clip, clip))
        {
            var fps = entry.Source.SourceFps;
            if (fps <= 0)
            {
                fps = 25;
            }

            var target = (long)Math.Round((mediaTime - clip.InPoint) * fps);
            var delta = target - entry.LastFrameIndex;
            if (delta >= 0 && delta <= 3)
            {
                // 顺序读帧推进到目标帧号（丢弃中间帧，单帧解码几毫秒）。
                for (var i = 0; i < delta; i++)
                {
                    if (!entry.Source.TryReadFrame(out _))
                    {
                        break;
                    }
                }

                entry.LastFrameIndex = target;
                return entry.Source;
            }

            // 大跳 / 后退：真正 seek。
            if (mediaTime > 0)
            {
                entry.Source.SeekTo(mediaTime);
            }

            entry.LastFrameIndex = target;
            return entry.Source;
        }

        // 换片段/换轨：关闭旧源。
        if (_seekSources.TryGetValue(track, out var old))
        {
            old.Source?.Dispose();
            _seekSources.Remove(track);
        }

        var source = new VideoFrameSource();
        try
        {
            if (!source.Open(clip.SourcePath, PreviewMaxDimension))
            {
                source.Dispose();
                return null;
            }
        }
        catch
        {
            source.Dispose();
            return null;
        }

        var state = new SeekSourceState { Clip = clip, Source = source };
        if (mediaTime > 0)
        {
            source.SeekTo(mediaTime);
            var sfps = source.SourceFps;
            state.LastFrameIndex = (long)Math.Round((mediaTime - clip.InPoint) * (sfps > 0 ? sfps : 25));
        }

        _seekSources[track] = state;
        return source;
    }

    /// <summary>释放 seek 解码持久源（scrub 结束 / 关闭窗口时调用）。</summary>
    private void DisposeSeekSources()
    {
        foreach (var entry in _seekSources.Values)
        {
            entry.Source?.Dispose();
        }

        _seekSources.Clear();
    }

    /// <summary>把 seek 解码出的各轨帧应用到舞台图层；无覆盖片段的轨道隐藏。</summary>
    private void ApplySeekFrames(List<(int Track, VideoFrame Frame, VideoClip Clip)> results)
    {
        var covered = new HashSet<int>();
        foreach (var (track, frame, clip) in results)
        {
            covered.Add(track);
            // 滤镜片段：对低于滤镜轨道的画面应用滤镜。
            UpdateStageLayer(track, ApplyActiveFilter(frame, clip, _playheadTime), clip);
        }

        // 隐藏没有覆盖片段的轨道图层，避免残留旧帧误导。
        for (var i = 0; i < _stageLayers.Count; i++)
        {
            if (!covered.Contains(i))
            {
                _stageLayers[i].Image.IsVisible = false;
            }
        }

        // 字幕跟随 seek 结果：有帧显示对应轨道，无帧清空（避免残留播放指示）。
        _stageCaption.Text = results.Count > 0
            ? $"轨道 {results[^1].Track + 1}: {Path.GetFileName(results[^1].Clip.SourcePath)}"
            : "";

        UpdateStageHandles();
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
        if (_playButton.Content is IconText icon)
        {
            icon.Glyph = _playing ? "\uEC91" : "\uEDB9";
        }
    }

    private void OnPreviewFrame(VideoFrame frame, VideoClip clip, int track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UpdateStageLayer(track, ApplyActiveFilter(frame, clip, _player?.CurrentTime ?? _playheadTime), clip);
                _stageCaption.Text = $"轨道 {track + 1}: {ClipDisplayName(clip)}";
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
        WriteFrameToImage(layer.Image, ref layer.Bitmap, frame, clip.Grayscale);
        ApplyTransform(layer, clip);
        layer.Image.IsVisible = true;
        UpdateStageHandles();
    }

    /// <summary>片段在时间轴/属性面板的显示名（视频/图片 = 文件名，文本 = 内容，形状 = 类型）。</summary>
    private static string ClipDisplayName(VideoClip clip) => clip.Kind switch
    {
        "Text" => clip.Text,
        "Shape" => ShapeName(clip.Shape),
        "Image" => Path.GetFileName(clip.SourcePath),
        "Filter" => FilterName(clip.Filter),
        _ => Path.GetFileName(clip.SourcePath)
    };

    /// <summary>把解码帧写入可复用的 WriteableBitmap 并挂到目标 Image（显式失效触发重绘）。
    /// grayscale&gt;0 时在写入时做灰度处理（0..1）。</summary>
    private static void WriteFrameToImage(Image image, ref WriteableBitmap? bitmap, VideoFrame frame, double grayscale = 0)
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
            if (grayscale > 0.001)
            {
                // 灰度：逐像素处理（帧为 BGRA）。
                for (var y = 0; y < h; y++)
                {
                    var si = y * srcStride;
                    var di = y * dstStride;
                    for (var x = 0; x < w; x++)
                    {
                        var b = src[si];
                        var g = src[si + 1];
                        var r = src[si + 2];
                        var gray = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                        var a = Math.Clamp(grayscale, 0, 1);
                        Marshal.WriteByte(dst, di, (byte)(gray * a + b * (1 - a)));
                        Marshal.WriteByte(dst, di + 1, (byte)(gray * a + g * (1 - a)));
                        Marshal.WriteByte(dst, di + 2, (byte)(gray * a + r * (1 - a)));
                        Marshal.WriteByte(dst, di + 3, src[si + 3]);
                        si += 4;
                        di += 4;
                    }
                }
            }
            else if (srcStride == dstStride)
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

    /// <summary>把片段变换应用到指定舞台 Image（与运行时 ApplyVideoClipTransform 同逻辑）。
    /// 播放中每帧调用：签名（画幅 + 全部变换值）未变化时直接跳过（不再每帧重建 TransformGroup
    /// 触发全舞台重排——静止片段播放时这里曾是持续卡顿来源）。</summary>
    private void ApplyTransform(StageTrackLayer layer, VideoClip clip)
    {
        var image = layer.Image;
        var w = _stageBorder.Bounds.Width;
        var h = _stageBorder.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var sigA = (w, h,
            clip.Scale * clip.ScaleX * (clip.FlipH ? -1 : 1),
            clip.Scale * clip.ScaleY * (clip.FlipV ? -1 : 1),
            clip.Rotation, clip.OffsetX, clip.OffsetY);
        var sigB = (Math.Clamp(clip.Opacity, 0, 1), clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom);
        if (layer.HasTransform && layer.LastTransformA == sigA && layer.LastTransformB == sigB)
        {
            return;
        }

        layer.LastTransformA = sigA;
        layer.LastTransformB = sigB;
        layer.HasTransform = true;
        var group = new TransformGroup();
        // 翻转用负缩放（绕中心镜像，与渲染器 uv 取镜像一致）。
        group.Children.Add(new ScaleTransform(
            clip.Scale * clip.ScaleX * (clip.FlipH ? -1 : 1),
            clip.Scale * clip.ScaleY * (clip.FlipV ? -1 : 1)));
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
            CaptureResizeStart(e.GetPosition(_stageBorder));
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
                HideStageGuides();
                e.Pointer.Capture(null);
                FillPropertyPanel();
            }
        };
        _stageHandleOverlay.Children.Add(_handleOutline);

        for (var i = 0; i < 8; i++)
        {
            // 宿主「编辑档案→时间表」时间点手柄样式：16px 白底圆形 + 2px 强调色描边；
            // 按下/拖动时变为实心强调色圆（无描边）。外层 24px 透明命中热区保证好点。
            var dot = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = Brushes.White,
                Stroke = ThemePalette.AccentBrush(),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            var handle = new Border
            {
                Width = 24,
                Height = 24,
                Background = Brushes.Transparent,
                IsHitTestVisible = true,
                Child = dot
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
                // 按下 → 实心强调色（宿主样式 pressed）。
                dot.Fill = ThemePalette.AccentBrush();
                dot.StrokeThickness = 0;
                CaptureResizeStart(e.GetPosition(_stageBorder));
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
                    HideStageGuides();
                    // 恢复默认样式：白底 + 强调色描边。
                    dot.Fill = Brushes.White;
                    dot.Stroke = ThemePalette.AccentBrush();
                    dot.StrokeThickness = 2;
                    e.Pointer.Capture(null);
                    FillPropertyPanel();
                }
            };
            handle.PointerCaptureLost += (_, _) =>
            {
                // 捕获意外丢失也恢复默认样式。
                dot.Fill = Brushes.White;
                dot.Stroke = ThemePalette.AccentBrush();
                dot.StrokeThickness = 2;
            };
            _handles[i] = handle;
            _stageHandleOverlay.Children.Add(handle);
        }

        _stageHandleOverlay.IsVisible = false;
    }

    /// <summary>按下手柄/选中框时捕获拖动起始状态（基准矩形 + 基准尺寸冻结在按下瞬间）。</summary>
    private void CaptureResizeStart(Point pos)
    {
        _resizeStart = pos;
        var clip = _selected;
        if (clip == null)
        {
            _resizeClip = null;
            return;
        }

        _resizeUndoPushed = false;
        _resizeStartRect = GetSelectedDisplayRect(clip);
        _resizeBaseW = _resizeStartRect.Width / Math.Max(0.01, clip.Scale * clip.ScaleX);
        _resizeBaseH = _resizeStartRect.Height / Math.Max(0.01, clip.Scale * clip.ScaleY);
        _resizeClip = clip;
        CaptureMagnetRefs();
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

        var sw = baseW * Math.Max(0.01, clip.Scale * clip.ScaleX);
        var sh = baseH * Math.Max(0.01, clip.Scale * clip.ScaleY);
        var x = W / 2.0 - sw / 2.0 + clip.OffsetX * W;
        var y = H / 2.0 - sh / 2.0 + clip.OffsetY * H;
        return new Rect(x, y, sw, sh);
    }

    /// <summary>
    /// 拖拽手柄/移动：更新选中片段的缩放与偏移，并实时应用到舞台。
    /// 锚点法：被拖动的角/边跟手，对边/对角固定，视频不再"飘"。
    /// 角（0/2/4/6）= 等比缩放；上下边（1/5）= 仅纵向拉伸；左右边（3/7）= 仅横向拉伸。
    /// </summary>
    private void ApplyResizeDrag(int handle, Point current, Point start, Rect startRect, VideoClip clip)
    {
        var W = _stageBorder.Bounds.Width;
        var H = _stageBorder.Bounds.Height;
        if (W <= 0 || H <= 0)
        {
            return;
        }

        // 首次实际拖动才压撤销（按下仅选中，不压栈）。
        if (!_resizeUndoPushed)
        {
            PushUndo();
            _resizeUndoPushed = true;
        }

        if (handle == 8)
        {
            // 移动：目标中心 = 按下时视频中心 + 指针位移（用按下时的矩形作绝对参考，
            // 不能用 OffsetX += 增量——那会随 PointerMoved 次数超线性累积，视频越拖越飘）。
            var newCx = startRect.Center.X + (current.X - start.X);
            var newCy = startRect.Center.Y + (current.Y - start.Y);
            clip.OffsetX = Math.Clamp((newCx - W / 2.0) / W, -2, 2);
            clip.OffsetY = Math.Clamp((newCy - H / 2.0) / H, -2, 2);
        }
        else
        {
            // 基准尺寸 = 按下时矩形 / 按下时的有效缩放（拖动中保持冻结，不随 clip.Scale 变化漂移）。
            var baseW = _resizeBaseW > 0 ? _resizeBaseW : 1;
            var baseH = _resizeBaseH > 0 ? _resizeBaseH : 1;
            var anchor = HandleAnchor(handle, startRect);
            var dx = current.X - anchor.X;
            var dy = current.Y - anchor.Y;
            var signX = dx >= 0 ? 1 : -1;
            var signY = dy >= 0 ? 1 : -1;

            if (handle is 0 or 2 or 4 or 6)
            {
                // 角：等比缩放（cover，被拖角在主轴上跟手），重置非等比拉伸。
                var scale = Math.Clamp(
                    Math.Max(Math.Abs(dx) / baseW, Math.Abs(dy) / baseH),
                    0.05, 10);
                clip.Scale = scale;
                clip.ScaleX = 1;
                clip.ScaleY = 1;
                var cx = anchor.X + signX * baseW * scale / 2.0;
                var cy = anchor.Y + signY * baseH * scale / 2.0;
                clip.OffsetX = (cx - W / 2.0) / W;
                clip.OffsetY = (cy - H / 2.0) / H;
            }
            else if (handle is 1 or 5)
            {
                // 上下边：仅纵向拉伸（横向宽度不变）。
                var sy = Math.Clamp(Math.Abs(dy) / baseH, 0.05, 10);
                clip.ScaleY = sy / Math.Max(0.01, clip.Scale);
                var cy = anchor.Y + signY * baseH * sy / 2.0;
                clip.OffsetY = (cy - H / 2.0) / H;
            }
            else
            {
                // 左右边：仅横向拉伸（纵向高度不变）。
                var sx = Math.Clamp(Math.Abs(dx) / baseW, 0.05, 10);
                clip.ScaleX = sx / Math.Max(0.01, clip.Scale);
                var cx = anchor.X + signX * baseW * sx / 2.0;
                clip.OffsetX = (cx - W / 2.0) / W;
            }
        }

        if (clip.Track < _stageLayers.Count)
        {
            ApplyTransform(_stageLayers[clip.Track], clip);
        }

        // 磁吸：在变换已计算后把被拖点/中心吸附到参考点（吸附距离 ≤6px，对边视觉偏移可忽略）。
        ApplyMagnet(clip, handle);
        UpdateStageHandles();
        _clipNameText.Text = $"{ClipDisplayName(clip)}\n轨道 {clip.Track + 1} · 开始 {clip.StartTime:0.#}s · 时长 {clip.Duration:0.#}s\n缩放 {clip.Scale:0.##}x · 拉伸 ({clip.ScaleX:0.##}, {clip.ScaleY:0.##}) · 偏移 ({clip.OffsetX:0.##}, {clip.OffsetY:0.##})";
    }

    // ============ 舞台磁吸 + 基准线 ============

    /// <summary>拖动开始时冻结磁吸参考点：其它片段显示中心 + 舞台边缘/中心。</summary>
    private void CaptureMagnetRefs()
    {
        _magnetRefsX.Clear();
        _magnetRefsY.Clear();
        var W = _stageBorder.Bounds.Width;
        var H = _stageBorder.Bounds.Height;
        if (W <= 0 || H <= 0)
        {
            return;
        }

        _magnetRefsX.AddRange([0, W / 2.0, W]);
        _magnetRefsY.AddRange([0, H / 2.0, H]);
        foreach (var other in _project.Clips)
        {
            if (ReferenceEquals(other, _selected) || other.Track >= _stageLayers.Count)
            {
                continue;
            }

            var rect = GetSelectedDisplayRect(other);
            if (rect.Width <= 0)
            {
                continue;
            }

            _magnetRefsX.Add(rect.Center.X);
            _magnetRefsY.Add(rect.Center.Y);
        }
    }

    /// <summary>找参考点集中最近的值（距离 < 阈值时返回，否则 null）。</summary>
    private static double? NearestRef(List<double> refs, double value)
    {
        double? best = null;
        var bestDist = MagnetThreshold;
        foreach (var r in refs)
        {
            var d = Math.Abs(r - value);
            if (d < bestDist)
            {
                bestDist = d;
                best = r;
            }
        }

        return best;
    }

    private void ShowGuideV(double x)
    {
        _guideV.StartPoint = new Point(x, 0);
        _guideV.EndPoint = new Point(x, Math.Max(1, _stageBorder.Bounds.Height));
        _guideV.IsVisible = true;
        _stageGuides.IsVisible = true;
    }

    private void ShowGuideH(double y)
    {
        _guideH.StartPoint = new Point(0, y);
        _guideH.EndPoint = new Point(Math.Max(1, _stageBorder.Bounds.Width), y);
        _guideH.IsVisible = true;
        _stageGuides.IsVisible = true;
    }

    private void HideStageGuides()
    {
        _stageGuides.IsVisible = false;
        _guideV.IsVisible = false;
        _guideH.IsVisible = false;
    }

    /// <summary>
    /// 舞台磁吸：移动（handle=8）吸中心到其它片段中心/舞台中心与边缘；缩放吸被拖角/边到参考点。
    /// 吸附通过微调偏移实现（≤6px，锚点视觉偏移可忽略），吸附时显示对应基准线。
    /// 返回是否发生了吸附。</summary>
    private bool ApplyMagnet(VideoClip clip, int handle)
    {
        HideStageGuides();
        var W = _stageBorder.Bounds.Width;
        var H = _stageBorder.Bounds.Height;
        if (W <= 0 || H <= 0 || handle < 0)
        {
            return false;
        }

        var rect = GetSelectedDisplayRect(clip);
        if (rect.Width <= 0)
        {
            return false;
        }

        var snapped = false;
        Point snapPoint;
        if (handle == 8)
        {
            snapPoint = rect.Center;
        }
        else
        {
            // 被拖角/边中点（与 UpdateStageHandles 的 pts 一致）。
            snapPoint = handle switch
            {
                0 => new Point(rect.Left, rect.Top),
                1 => new Point(rect.Center.X, rect.Top),
                2 => new Point(rect.Right, rect.Top),
                3 => new Point(rect.Right, rect.Center.Y),
                4 => new Point(rect.Right, rect.Bottom),
                5 => new Point(rect.Center.X, rect.Bottom),
                6 => new Point(rect.Left, rect.Bottom),
                _ => new Point(rect.Left, rect.Center.Y)
            };
        }

        var snapX = NearestRef(_magnetRefsX, snapPoint.X);
        var snapY = NearestRef(_magnetRefsY, snapPoint.Y);
        if (snapX != null)
        {
            clip.OffsetX += (snapX.Value - snapPoint.X) / W;
            ShowGuideV(snapX.Value);
            snapped = true;
        }

        if (snapY != null)
        {
            clip.OffsetY += (snapY.Value - snapPoint.Y) / H;
            ShowGuideH(snapY.Value);
            snapped = true;
        }

        return snapped;
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

    /// <summary>自定义画幅：修改工程输出宽高（决定舞台宽高比与渲染分辨率）。</summary>
    private async Task ChangeCanvasSizeAsync()
    {
        var wSpin = new EditorSpin(16, 4096, 1, "0");
        var hSpin = new EditorSpin(16, 4096, 1, "0");
        wSpin.DoubleValue = _project.OutputWidth;
        hSpin.DoubleValue = _project.OutputHeight;
        var dialog = new ContentDialog
        {
            Title = "自定义画幅",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "输出宽（像素）：" },
                    wSpin,
                    new TextBlock { Text = "输出高（像素）：" },
                    hSpin,
                    new TextBlock { Text = "舞台与渲染输出将按此宽高比显示。", Opacity = 0.6, FontSize = 11 }
                }
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        PushUndo();
        _project.OutputWidth = Math.Max(16, wSpin.DoubleValue);
        _project.OutputHeight = Math.Max(16, hSpin.DoubleValue);
        RefreshTimeline();
        ScheduleSave();
        // 按新比例重排舞台（host 是 _stageBorder 的直接父级 Border）。
        if (_stageBorder.Parent is Border host && host.Bounds.Width > 0 && host.Bounds.Height > 0)
        {
            LayoutStage(host.Bounds.Width, host.Bounds.Height);
        }
    }

    private async void RenderAndApply()
    {
        StopPreview();
        // 只清理缺失的「视频」片段；文本/形状覆盖层没有 SourcePath，必须保留。
        _project.Clips.RemoveAll(c =>
            c.Kind == "Video" && (string.IsNullOrWhiteSpace(c.SourcePath) || !File.Exists(c.SourcePath)));
        if (_project.Clips.Count == 0)
        {
            _statusText.Text = "时间轴为空，无法渲染。请先添加片段。";
            return;
        }

        if (!FFmpegRuntime.IsAvailable || !FFmpegRuntime.EnsureLoaded())
        {
            _statusText.Text = "缺少 FFmpeg 库，无法渲染。请先在设置页下载 FFmpeg。";
            return;
        }

        if (!FFmpegRuntime.EncoderAvailable)
        {
            // 当前是精简解码包（无 H.264 编码器）：引导升级完整包，升级完成自动继续渲染。
            if (!await EnsureFullPackageAsync("渲染视频剪辑需要 H.264 编码器（当前精简包仅支持解码）。"))
            {
                _statusText.Text = "已取消渲染：需要完整 FFmpeg 包才能编码输出。";
                return;
            }
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
        var (outW, outH, crf, hw) = options.Value;
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
                    (percent, msg) => progress.Report((percent, msg)),
                    crf <= 20 ? "medium" : crf <= 24 ? "faster" : "veryfast",
                    hw ? "auto" : null,
                    hw ? "h264_qsv" : null).Render();
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

    /// <summary>渲染选项对话框：分辨率（默认 270p）+ 画质（CRF）+ 硬件加速。返回 null 表示取消。</summary>
    private async Task<(int outW, int outH, int crf, bool hw)?> AskRenderOptionsAsync()
    {
        var resolutions = new[] { "270p", "360p", "480p", "720p", "原尺寸" };
        var qualities = new[] { "高", "中", "低" };
        var resCombo = new ComboBox { ItemsSource = resolutions, SelectedIndex = 0 };
        var qualityCombo = new ComboBox { ItemsSource = qualities, SelectedIndex = 1 };
        var hwToggle = new ToggleSwitch
        {
            IsChecked = InjectorRuntime.Settings.RenderHardwareAccelerated,
            OnContent = "硬件加速",
            OffContent = "软件"
        };
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
                    qualityCombo,
                    hwToggle,
                    new TextBlock
                    {
                        Text = "硬件加速：自动探测 Intel QSV / NVIDIA / AMD / Windows 硬件编码与解码，\n可用时大幅提速；失败自动回退软件编码。垃圾 CPU 机器建议开启。",
                        FontSize = 11,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.Wrap
                    }
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

        // 记住硬件加速偏好（下次渲染默认沿用）。
        if (InjectorRuntime.Settings.RenderHardwareAccelerated != (hwToggle.IsChecked == true))
        {
            InjectorRuntime.Settings.RenderHardwareAccelerated = hwToggle.IsChecked == true;
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
        return (outW, outH, crf, hwToggle.IsChecked == true);
    }

    /// <summary>封面卡右键菜单（闭包固定素材路径）。</summary>
    private MenuFlyout BuildAssetCoverMenu(string path)
    {
        var menu = new MenuFlyout();
        var info = new MenuItem { Header = "查看媒体信息" };
        info.Click += (_, _) => _ = ShowAssetInfoAsync(path);
        var remove = new MenuItem { Header = "删除" };
        remove.Click += (_, _) => _ = RemoveAssetAsync(path);
        menu.Items.Add(info);
        menu.Items.Add(remove);
        return menu;
    }

    /// <summary>选中素材路径（列表 SelectedItem 是文件名，由索引换算）。</summary>
    private string? SelectedAssetPath =>
        _assetList.SelectedIndex is >= 0 && _assetList.SelectedIndex < _assets.Count
            ? _assets[_assetList.SelectedIndex]
            : null;

    /// <summary>右键按下时先选中命中的素材行（让菜单作用于所右键的素材）。</summary>
    private void SelectAssetAt(ListBox list, PointerEventArgs e)
    {
        var pos = e.GetPosition(list);
        for (var i = 0; i < _assets.Count; i++)
        {
            if (list.ContainerFromIndex(i) is Control c && c.Bounds.Contains(pos))
            {
                list.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>删除素材：移除时间轴中引用该素材的片段 + 从素材库删除（不删磁盘文件）。需确认。</summary>
    private async Task RemoveAssetAsync(string path)
    {
        var refs = _project.Clips.Count(c => c.SourcePath == path);
        var dialog = new ContentDialog
        {
            Title = "删除素材",
            Content = new TextBlock
            {
                Text = refs > 0
                    ? $"「{Path.GetFileName(path)}」被时间轴中 {refs} 个片段引用。\n删除将同时移除这些片段（不删除磁盘文件）。"
                    : $"从素材库删除「{Path.GetFileName(path)}」？（不删除磁盘文件）",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        PushUndo();
        _project.Clips.RemoveAll(c => c.SourcePath == path);
        _assets.Remove(path);
        _assetDurations.Remove(path);
        if (_assetThumbs.Remove(path, out var thumb))
        {
            thumb.Dispose();
        }

        if (_selected?.SourcePath == path)
        {
            _selected = null;
        }

        CompactTracks();
        RefreshAssetList();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
        _statusText.Text = "已删除素材及其时间轴引用。";
    }

    /// <summary>查看媒体信息（视频：原始分辨率/帧率/时长；图片：像素尺寸；通用：大小/修改时间）。</summary>
    private async Task ShowAssetInfoAsync(string path)
    {
        string info;
        try
        {
            var size = new FileInfo(path);
            var head = $"文件：{path}\n大小：{size.Length / 1024.0 / 1024.0:0.##} MB\n修改：{size.LastWriteTime:yyyy-MM-dd HH:mm}";
            if (VideoTranscoder.IsImageFile(path))
            {
                using var stream = File.OpenRead(path);
                using var bmp = new Bitmap(stream);
                info = $"{head}\n类型：图片\n尺寸：{bmp.PixelSize.Width}×{bmp.PixelSize.Height}";
            }
            else
            {
                using var source = new VideoFrameSource();
                if (source.Open(path, 96))
                {
                    var (w, h) = source.SourceSize;
                    info = $"{head}\n类型：视频\n分辨率：{w}×{h}\n帧率：{source.SourceFps:0.##} fps\n时长：{FormatClock(source.Duration)}";
                }
                else
                {
                    info = head + "\n类型：视频（无法解码，FFmpeg 库未安装？）";
                }
            }
        }
        catch (Exception ex)
        {
            info = $"读取失败：{ex.Message}";
        }

        await ShowDialogAsync(new ContentDialog
        {
            Title = "媒体信息",
            Content = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭"
        });
    }

    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        return TopLevel.GetTopLevel(this) is Window host
            ? dialog.ShowAsync(host)
            : dialog.ShowAsync();
    }
}
