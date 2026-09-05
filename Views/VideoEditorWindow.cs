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
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Controls;
using ClassIsland.Shared;
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
    /// <summary>多选集合（框选/点击多选；_selected 为主选中供检查器用）。</summary>
    private readonly HashSet<VideoClip> _selectedClips = [];
    /// <summary>片段块映射（clip → 时间轴块；拖动多选/更新位置用，RefreshTimeline 重建）。</summary>
    private readonly Dictionary<VideoClip, Border> _blockByClip = [];
    private VideoProjectPlayer? _player;
    private bool _playing;
    private bool _updatingUi;
    /// <summary>「添加到时间轴」的目标轨道（点击轨道头部切换）。</summary>
    private int _selectedTrack;
    /// <summary>播放头当前时间（秒，非播放时也保留位置）。</summary>
    private double _playheadTime;
    /// <summary>正在裁剪的片段（左右手柄拖动）状态：片段 / 是否出点 / 上次指针 X / 块 / 时长文本。</summary>
    private (VideoClip Clip, bool IsOut, double LastX, Border Block, TextBlock DurationText)? _trimState;
    /// <summary>是否正在拖动播放头（scrub）。</summary>
    private bool _scrubbing;
    /// <summary>吸附开关：拖动 seek / 素材时吸附元素头尾与 0 点。</summary>
    private bool _snapEnabled = true;
    /// <summary>seek 帧解码请求代次（每次播放头变化递增，只有最新代次才应用）。</summary>
    private int _seekFrameGen;
    /// <summary>seek 帧解码 worker 是否忙（忙时只记录最新 pending 时间，结束后再解最新一帧）。</summary>
    private bool _seekFrameBusy;
    private double _seekFramePendingTime;
    private bool _seekFrameHasPending;
    /// <summary>拖动片段块组的拖拽状态（多选组跟手实时移动，释放时落轨）。Pressed = 按下的主片段。</summary>
    private (VideoClip Pressed, double GrabX, double GrabY, Dictionary<VideoClip, double> Origins, Dictionary<VideoClip, int> Tracks)? _moveGroup;
    /// <summary>框选状态：起点（时间轴内容坐标）；null = 未框选。</summary>
    private Point? _marqueeStart;
    /// <summary>框选是否已移动超过阈值（区分点击 seek 与画框）。</summary>
    private bool _marqueeMoved;
    /// <summary>框选矩形（可见虚线框，_timelineRoot 内）。</summary>
    private readonly Avalonia.Controls.Shapes.Rectangle _marqueeRect = new()
    {
        Stroke = ThemePalette.AccentBrush(),
        StrokeThickness = 1,
        StrokeDashArray = [3, 2],
        Fill = ThemePalette.AccentBrushWithAlpha(35),
        IsVisible = false,
        IsHitTestVisible = false,
        ZIndex = 60
    };
    /// <summary>时间轴泳道列表（拖拽时高亮目标轨道用，RefreshTimeline 重建）。</summary>
    private readonly List<Border> _lanes = [];
    /// <summary>拖拽插入新轨道时的水平插入指示线（显示在两条轨道之间）。</summary>
    private readonly Border _insertIndicator = new()
    {
        Height = 2,
        CornerRadius = new CornerRadius(1),
        Background = ThemePalette.AccentBrush(),
        IsHitTestVisible = false,
        IsVisible = false,
        ZIndex = 25
    };
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
    /// <summary>空素材库占位（导入按钮 + 拖拽提示；无素材时显示）。</summary>
    private StackPanel _emptyState = null!;
    /// <summary>当前素材库视图：false=列表，true=封面（默认缩略图）。</summary>
    private bool _assetCoverView = true;
    /// <summary>封面缩略图缓存（路径 → 位图）。</summary>
    private readonly Dictionary<string, Bitmap> _assetThumbs = [];
    /// <summary>素材库视图切换按钮（列表 ⇄ 封面）。</summary>
    private readonly Button _assetViewToggle = new()
    {
        Content = new IconText { Glyph = "\uE929", Text = "" },
        Padding = new Thickness(4, 2),
        MinWidth = 24,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    /// <summary>内部剪贴板片段（Ctrl+C 复制 / Ctrl+V 粘贴到播放头）。</summary>
    private VideoClip? _clipboard;
    /// <summary>形状/滤镜库视图：true = 卡片缩略图，false = 紧凑列表。</summary>
    private bool _libraryCardsView = true;
    /// <summary>库视图切换按钮（仅形状/滤镜库模式显示）。</summary>
    private Button _libraryViewToggle = new();
    /// <summary>形状/滤镜卡片面板（库内容随工具切换：形状工具→形状卡片，效果工具→滤镜卡片）。</summary>
    private readonly ScrollViewer _libraryScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
    private readonly WrapPanel _libraryPanel = new();
    /// <summary>文字模板滚动/面板（文字工具时素材库显示预设文字模板）。</summary>
    private readonly ScrollViewer _textTemplatesScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
    private readonly WrapPanel _textTemplatesPanel = new();
    /// <summary>文字样式预设（名称 / 预览样例 / 字体 / 字号系数 / 加粗 / 填充色 / 描边色 / 描边宽）。
    /// 预设给的是「样式」不是文本内容——点击添加的片段内容为中性默认值，右侧可编辑。</summary>
    private sealed record TextStylePreset(
        string Name, string Sample, string FontFamily, double SizeFactor, bool Bold,
        string Color, string StrokeColor, double StrokeWidth);

    /// <summary>文字样式预设库（素材库文字选项卡）。</summary>
    private static readonly TextStylePreset[] TextStylePresets =
    [
        new("大标题", "Aa 文本", "Microsoft YaHei", 0.5, true, "#FFFFFFFF", "#FF000000", 0.02),
        new("正文", "Aa 文本", "Microsoft YaHei", 0.26, false, "#FFFFFFFF", "#00000000", 0),
        new("黑体加粗", "Aa 文本", "SimHei", 0.34, true, "#FFFFFFFF", "#FF000000", 0.012),
        new("黄色强调", "Aa 文本", "Microsoft YaHei", 0.3, true, "#FFFFEB3B", "#00000000", 0),
        new("红色警示", "Aa 文本", "Microsoft YaHei", 0.32, true, "#FFFF5252", "#FF000000", 0.01),
        new("描边大字", "Aa 文本", "Microsoft YaHei", 0.46, true, "#FFFFFFFF", "#FF000000", 0.03),
        new("宋体正文", "Aa 文本", "SimSun", 0.24, false, "#FFFFFFFF", "#00000000", 0),
        new("楷体手写", "Aa 文本", "KaiTi", 0.28, false, "#FFFFFFFF", "#00000000", 0),
        new("霓虹描边", "Aa 文本", "Microsoft YaHei", 0.3, true, "#FF00E5FF", "#FF0048BA", 0.018)
    ];
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
    /// <summary>旋转手柄（选中框顶部上方，与八向缩放手柄同款：白底圆点 + 强调色描边）与旋转臂。</summary>
    private Border _rotateHandle = null!;
    private Avalonia.Controls.Shapes.Line _rotateArm = null!;
    /// <summary>旋转手柄内层圆点（按下切换实心强调色，与八向缩放手柄一致）。</summary>
    private Ellipse _rotateDot = null!;
    /// <summary>是否正在拖动旋转手柄。</summary>
    private bool _rotating;
    /// <summary>旋转拖动起始：按下指针 / 基准显示矩形 / 起始旋转角。</summary>
    private Point _rotateStartPointer;
    private Rect _rotateStartRect;
    private double _rotateStartRotation;

    // ---- 属性 ----
    private readonly EditorSpin _inSpin = new(0, 36000, 0.1, "0.#");
    private readonly EditorSpin _outSpin = new(0.1, 36000, 0.1, "0.#");
    // 变换直接按像素编辑（X/Y = 相对输出画布中心的像素坐标，宽/高 = 像素）：
    // 内部仍以归一化数值为准，检查器读写时与「画布适配基准」互算（见 SetTransformPxFromClip / ApplyPropertyEdits）。
    private readonly EditorSpin _pxXSpin = new(-20000, 20000, 1, "0");
    private readonly EditorSpin _pxYSpin = new(-20000, 20000, 1, "0");
    private readonly EditorSpin _pxWSpin = new(1, 20000, 1, "0");
    private readonly EditorSpin _pxHSpin = new(1, 20000, 1, "0");
    private readonly EditorSpin _rotationSpin = new(-180, 180, 0.5, "0.#");
    private readonly EditorSpin _opacitySpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropLSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropTSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropRSpin = new(0, 1, 0.01, "0.##");
    private readonly EditorSpin _cropBSpin = new(0, 1, 0.01, "0.##");
    private Control[] _propertyControls = [];
    /// <summary>px 投影基准缓存：当前选中片段在输出画布内的“适配基准矩形”（缩放=1 时的像素尺寸）。</summary>
    private (double BaseW, double BaseH) _pxBaseCache = (16, 9);
    /// <summary>px 基准是否已建立（素材未解码时兜底用整幅画布，仍可编辑）。</summary>
    private bool _pxBaseReady;

    // ---- 时间轴（多轨）----
    private readonly Grid _timeline = new();
    // 头部与泳道行高一致（60）且均无行间距，才能逐行对齐。
    private readonly StackPanel _trackHeaders = new() { Orientation = Orientation.Vertical };
    /// <summary>时间轴像素/秒（横向缩放，可调 2..200；100% = _basePxPerSecond）。</summary>
    private double _pxPerSecond = 36;
    /// <summary>100% 缩放的像素/秒（用户要求 100% = 旧版 600%，即 6×6=36）。</summary>
    private const double BasePxPerSecond = 36;
    /// <summary>时间轴顶部标尺高度（与轨道头对齐）。</summary>
    private const double RulerHeight = 22;
    /// <summary>轨道头列宽（与 timelineContent/topBar 的 "92,*" 列定义一致）。</summary>
    private const double TimelineHeaderWidth = 92;
    /// <summary>轨道头列与泳道列的水平间距（ColumnSpacing）。</summary>
    private const double TimelineHeaderSpacing = 8;
    /// <summary>时间轴工具条近似高度（用于分割条拖拽时保证内容可见）。</summary>
    private const double TimelineToolbarHeight = 32;
    /// <summary>轨道泳道高度（可调，40..140；默认 60）。</summary>
    private double _laneHeight = 60;
    /// <summary>每轨独立高度（轨道头底缘拖拽调整；未设置时用 _laneHeight 滑块默认值）。</summary>
    private readonly Dictionary<int, double> _laneHeights = [];
    private double LaneHeightOf(int track) => _laneHeights.TryGetValue(track, out var h) ? h : _laneHeight;
    /// <summary>轨道头控件按数据轨号索引（视觉顺序反转后仍按轨号取）。</summary>
    private readonly Dictionary<int, Border> _headerByTrack = [];
    /// <summary>正在拖拽调整高度的轨道号（-1 = 无）。</summary>
    private int _resizeLane = -1;
    private double _resizeLaneStartY;
    private double _resizeLaneStartH;
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8, FontSize = 12, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>工程自动保存防抖计时器（编辑后延迟写盘，重开不丢）。</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // ---- 面板尺寸 / 分割条 ----
    private double _assetPanelWidth = 300;
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
    /// <summary>拖拽诊断：本次拖拽已打印的移动日志条数（只打前若干条防刷屏）。</summary>
    private int _dragLogMoves;
    private CommandBarButton _undoButton = null!;
    private CommandBarButton _redoButton = null!;
    private CommandBarButton _copyButton = null!;
    private CommandBarButton _pasteButton = null!;

    // ---- 时间轴标尺/缩放 ----
    private readonly TextBlock _zoomText = new()
    {
        FontSize = 11,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 40,
        TextAlignment = TextAlignment.Center
    };
    /// <summary>时间轴横向缩放滑块（1%~1000%，100% = BasePxPerSecond 36 px/秒，拖动实时缩放）。</summary>
    private readonly Slider _zoomSlider = new()
    {
        Minimum = 0.36,
        Maximum = 360,
        Value = 36,
        Width = 150,
        VerticalAlignment = VerticalAlignment.Center
    };
    /// <summary>缩放滑块程序化更新时抑制 ValueChanged 递归。</summary>
    private bool _updatingZoom;

    // ---- 工具（由素材库顶部 Tab 选项卡驱动：素材库/文字/形状/效果）----
    /// <summary>当前工具：select / text / shape / effect。</summary>
    private string _currentTool = "select";
    /// <summary>素材库顶部选项卡定义（键 = 工具，标签 = 显示名）。</summary>
    private static readonly (string Key, string Label)[] LibraryTabs =
    [
        ("select", "素材库"),
        ("text", "文字"),
        ("shape", "形状"),
        ("effect", "效果")
    ];

    // ---- 文本/形状覆盖层属性 ----
    private readonly TextBox _overlayText = new() { MinWidth = 140, Watermark = "文本内容" };
    // 填充 / 描边颜色不再手填 HEX：直接内嵌 FAUI ColorPicker（与设置页 / 图层编辑器同款），
    // 点击色块即可打开专门的调色板选色。
    private readonly ColorPicker _overlayColor = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _overlayShape = new() { MinWidth = 110 };
    private readonly EditorSpin _strokeWidthSpin = new(0, 0.2, 0.005, "0.###");
    private readonly ColorPicker _strokeColor = new() { VerticalAlignment = VerticalAlignment.Center };
    // 文本样式（仅 Text）：字体下拉（枚举系统字体，非写死）/ 字号（px）/ 加粗。
    // 内部仍存相对画布高度的系数（TextFontSize），检查器按输出画布高度换算成像素编辑。
    private readonly ComboBox _overlayFontBox = new() { MinWidth = 140, MaxDropDownHeight = 360 };
    private readonly EditorSpin _textSizeSpin = new(1, 4096, 1, "0");
    private readonly CheckBox _textBoldCheck = new() { Content = "加粗" };
    /// <summary>系统字体列表（FontManager 枚举，含预览字体渲染）。</summary>
    private FontFamily[] _systemFonts = [];
    private readonly StackPanel _overlayPanel = new() { Spacing = 4 };
    /// <summary>覆盖层编辑区的行（内容/颜色/形状/描边/文字样式），按片段类型显隐（图片只留通用变换）。</summary>
    private Control? _overlayTextRow;
    private Control? _overlayColorRow;
    private Control? _overlayShapeRow;
    private Control? _strokeWidthRow;
    private Control? _strokeColorRow;
    private Control? _overlayFontRow;
    private Control? _textSizeRow;
    private Control? _textBoldRow;
    // ---- 滤镜片段属性 ----
    private readonly ComboBox _filterCombo = new() { MinWidth = 110 };
    /// <summary>滤镜强度滑动条（比 spinbox 更直观地调节特效强度）。</summary>
    private readonly Slider _filterIntensitySlider = new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = 1,
        Width = 150,
        VerticalAlignment = VerticalAlignment.Center,
        IsSnapToTickEnabled = false
    };
    private readonly StackPanel _filterPanel = new() { Spacing = 4 };
    /// <summary>检查器分段条（变换/覆盖层/滤镜；ClassIsland 原生 TabStrip + compact 类，规则集同款）。</summary>
    private TabStrip _inspectorSegmented = null!;
    private ContentControl _inspectorContent = null!;
    private readonly Dictionary<string, TabStripItem> _segmentButtons = [];
    private readonly Dictionary<string, Control> _inspectorPages = [];
    private string _currentInspectorPage = "transform";
    /// <summary>上次检查器展示的片段（选中变化时才自动切分段）。</summary>
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
    /// <summary>舞台底部传输条：全屏按钮（把编辑器窗口切入 / 退出全屏）。</summary>
    private readonly Button _fullscreenButton = new()
    {
        Content = new IconText { Glyph = "\uE8D0", Text = "" },
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
    /// <summary>时间轴工具：false=选择工具(A)，true=分割工具(B)。</summary>
    private bool _splitTool;
    /// <summary>时间轴工具下拉（最左，默认选择工具）。</summary>
    private ComboBox _timelineToolCombo = null!;
    /// <summary>分割工具：跟随鼠标的虚线竖线（鼠标按下即分割）。</summary>
    private readonly Avalonia.Controls.Shapes.Line _splitCursorLine = new()
    {
        Stroke = ThemePalette.AccentBrush(),
        StrokeThickness = 1.5,
        StrokeDashArray = [4, 3],
        IsVisible = false,
        IsHitTestVisible = false
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
    /// <summary>上次时间轴强调色（切歌 / 动态主题变化时重绘 seek 线、标尺等强调色元素）。</summary>
    private Color _lastTimelineAccent;
    private readonly TextBlock _timeText = new()
    {
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
    /// <summary>标尺上的播放头抓取头（舞台八向手柄同款白圆点，点击/拖动 seek；X 与播放头竖线同步）。</summary>
    private Border _playheadHead = null!;
    /// <summary>播放头覆盖层（贯穿标尺 + 泳道全高的 Canvas，含竖线与圆头）。背景 null 不拦截空白点击。</summary>
    private Canvas _playheadOverlay = null!;
    /// <summary>时间轴根画布（泳道网格 + 播放头覆盖层）。左对齐保证时间轴 0 点在视口最左。</summary>
    private readonly Canvas _timelineRoot = new() { HorizontalAlignment = HorizontalAlignment.Left };
    /// <summary>外层纵向滚动容器（内容 = 左轨道头 + 右内层横向泳道，两者一起纵向滚动保证逐行对齐）。</summary>
    private ScrollViewer _timelineScroll = null!;
    /// <summary>时间轴滚动内容（列 0 轨道头 + 列 1 泳道；TEMPDBG 持有引用诊断用）。</summary>
    private Grid _timelineContent = null!;
    /// <summary>内层仅横向滚动的泳道容器（横向滚动 + 标尺平移同步）。</summary>
    private ScrollViewer _lanesScroll = null!;
    /// <summary>时间轴面板底部的横向滚动条（同步 _lanesScroll，滚动条固定在面板底端而非泳道底端）。</summary>
    private ScrollBar _timelineHScroll = null!;
    /// <summary>底部滚动条同步标志（避免回写循环）。</summary>
    private bool _syncingHScroll;
    /// <summary>固定标尺宿主（在滚动区上方，不随纵向滚动走）；标尺随内容横向滚动经 _rulerTranslate 同步。</summary>
    private Border _rulerHost = null!;
    private readonly TranslateTransform _rulerTranslate = new();
    /// <summary>标尺画布（RefreshTimeline 重建标尺内容，宽度 = 时间轴内容宽）。
    /// 必须左对齐：内容宽可能超过视口，若默认 Stretch/居中会被推到负偏移，导致标尺点击的
    /// GetPosition(_rulerCanvas) 整体偏移（seek「隔一段距离」）。</summary>
    private readonly Canvas _rulerCanvas = new() { HorizontalAlignment = HorizontalAlignment.Left };
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
        // 八向手柄覆盖层（最上层）；轨道图层插到最底。
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
                case Key.Left:
                    // 逐帧后退（1 帧 = 1/目标帧率）。
                    SetPlayhead(Math.Max(0, _playheadTime - 1.0 / _targetFps));
                    e.Handled = true;
                    break;
                case Key.Right:
                    // 逐帧前进。
                    SetPlayhead(_playheadTime + 1.0 / _targetFps);
                    e.Handled = true;
                    break;
                case Key.A:
                    // 选择工具。
                    SetSplitTool(false);
                    e.Handled = true;
                    break;
                case Key.B:
                    // 分割工具。
                    SetSplitTool(true);
                    e.Handled = true;
                    break;
                case Key.Delete:
                case Key.Back:
                    DeleteSelectedClip();
                    e.Handled = true;
                    break;
            }
        };
        // 播放时钟：轮询播放器当前时间驱动播放头与时间码；顺带监听动态主题强调色变化。
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            WatchTimelineAccent();
        };
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
        // 但 seek 线 18px 热区会盖住片段——若指针明显落在片段上则优先交给片段（选中/拖动/切割），
        // seek 线不抢焦点；只有点空白处才进入 scrub。
        _playhead.PointerPressed += (_, e) =>
        {
            var rootPos = e.GetPosition(_timelineRoot);
            var clip = ClipAtTimelinePoint(rootPos);
            if (clip != null)
            {
                if (_splitTool)
                {
                    var t = Math.Max(0, rootPos.X / _pxPerSecond);
                    EditorLog($"PLAYHEAD 上有点且为分割工具 → 在 {t:0.###}s 切割");
                    _statusText.Text = CutStatusText(t, CutClipsAt(t));
                }
                else if (_project.GetTrackState(clip.Track) is { Locked: true })
                {
                    EditorLog("PLAYHEAD 上有点但轨道锁定");
                    _statusText.Text = "该轨道已锁定，无法编辑。";
                }
                else
                {
                    EditorLog("PLAYHEAD 上有点 → 交给片段（不 scrub）");
                    BeginClipDrag(clip, e, rootPos);
                }

                e.Handled = true;
                return;
            }

            EditorLog("PLAYHEAD 点空白 → 进入 scrub");
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
        // 片段拖拽/裁剪不依赖指针捕获：用窗口级移动/释放驱动——指针在窗口内任何位置移动，
        // 拖拽都实时跟手（规避重挂载/捕获丢失导致“片段不跟手”）；无拖拽时这些处理器空转。
        PointerMoved += (_, e) =>
        {
            if (_moveGroup != null || _trimState != null)
            {
                OnTimelinePointerMoved(e);
            }
        };
        PointerReleased += (_, e) =>
        {
            if (_moveGroup != null || _trimState != null)
            {
                OnTimelinePointerReleased(e);
            }
        };
        // 拖拽诊断：记录时间轴内容区内谁收到了“按下”（未被子控件 Handled 的按下都会冒泡到窗口）。
        // 若一次拖动没有对应的「DRAG 按下」日志，看这里可知是哪个元素拦下了按下（叠层/滚动区等）。
        PointerPressed += (_, e) =>
        {
            if (_timelineRoot == null || _timelineRoot.Width <= 0)
            {
                return;
            }

            var p = e.GetPosition(_timelineRoot);
            if (p.X < 0 || p.Y < 0 || p.X > _timelineRoot.Width || p.Y > _timelineRoot.Height)
            {
                return;
            }

            var src = e.Source is Avalonia.Controls.Control ctl
                ? (ctl.Name ?? ctl.GetType().Name)
                : e.Source?.GetType().Name ?? "?";
            var hasClip = ClipAtTimelinePoint(p) != null;
            EditorLog($"PRESS-ROOT src={src} hasClip={hasClip} x={p.X:0.#} y={p.Y:0.#}");
        };
        Content = BuildContent();
        RefreshAssetList();
        RefreshTimeline();
        ClearSelection();
        // 默认封面缩略图视图：同步切换按钮图标（封面视图显示「列表」图标）。
        if (_assetViewToggle.Content is IconText assetIcon)
        {
            assetIcon.Glyph = _assetCoverView ? "\uEAC0" : "\uE929";
        }

        // 初始化素材库内容状态（默认素材模式）。
        UpdateLibraryContent();
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
        // 缩放滑块：拖动实时缩放（锚定播放头，防止视野乱跳）。
        _zoomSlider.ValueChanged += (_, e) =>
        {
            if (_updatingZoom)
            {
                return;
            }

            var oldPx = _pxPerSecond;
            var newPx = Math.Clamp(e.NewValue, 0.36, 360);
            if (Math.Abs(newPx - oldPx) < 0.01)
            {
                return;
            }

            var anchorTime = _playheadTime;
            var offsetX = _lanesScroll.Offset.X;
            _pxPerSecond = newPx;
            RefreshTimeline();
            var newOffset = Math.Max(0, anchorTime * (newPx - oldPx) + offsetX);
            _lanesScroll.Offset = new Vector(newOffset, 0);
            _zoomText.Text = $"{_pxPerSecond / BasePxPerSecond * 100:0}%";
        };
        ToolTip.SetTip(_zoomSlider, "缩放时间轴（也可 Ctrl+滚轮）");
        ToolTip.SetTip(_cutButton, "刀片切割：在播放头位置切割片段（有选中时只切选中的片段，否则切所有覆盖该时刻的片段）");
        ToolTip.SetTip(_deleteButton, "删除选中片段");
        ToolTip.SetTip(_canvasButton, "自定义画幅：修改输出宽高");
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
                CommandButton("\uEEB5", "渲染并应用", "保存工程并应用到主界面", RenderAndApply)
            }
        };

        // 素材库（左）：顶部 Tab 选项卡（素材库/文字/形状/效果）+ 视图切换按钮（列表 ⇄ 封面）。
        var assetScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _assetList
        };
        _assetCoverScroll.Content = _assetCoverPanel;
        _assetViewToggle.Click += (_, _) => ToggleAssetView();
        ToolTip.SetTip(_assetViewToggle, "切换素材库视图：列表 / 封面缩略图");
        _libraryViewToggle.Content = new IconText { Glyph = "\uE929", Text = "" };
        _libraryViewToggle.Padding = new Thickness(4, 2);
        _libraryViewToggle.MinWidth = 24;
        _libraryViewToggle.Background = Brushes.Transparent;
        _libraryViewToggle.BorderThickness = new Thickness(0);
        _libraryViewToggle.Click += (_, _) =>
        {
            _libraryCardsView = !_libraryCardsView;
            UpdateLibraryContent();
        };
        _libraryScroll.Content = _libraryPanel;
        _textTemplatesScroll.Content = _textTemplatesPanel;
        // 顶部小选项卡（档案编辑页「编辑科目」同款：TabControl + compact 类，仅用其选项卡条）。
        var libraryTabs = new TabControl { Padding = new Thickness(0) };
        libraryTabs.Classes.Add("compact");
        foreach (var (key, label) in LibraryTabs)
        {
            libraryTabs.Items.Add(new TabItem { Header = label });
        }

        libraryTabs.SelectionChanged += (_, _) =>
        {
            if (libraryTabs.SelectedIndex >= 0 && libraryTabs.SelectedIndex < LibraryTabs.Length)
            {
                SetTool(LibraryTabs[libraryTabs.SelectedIndex].Key);
            }
        };
        // 项添加完成后默认选中「素材库」。
        libraryTabs.SelectedIndex = 0;
        var togglePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _libraryViewToggle, _assetViewToggle }
        };
        _libraryViewToggle.Margin = new Thickness(0, 0, 6, 0);
        var assetHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { libraryTabs, togglePanel }
        };
        Grid.SetColumn(togglePanel, 1);
        // 空素材库占位：导入按钮 + 拖拽提示。
        var emptyImportButton = new Button
        {
            Content = new IconText { Glyph = "\uF3D1", Text = "导入文件" },
            HorizontalAlignment = HorizontalAlignment.Center
        };
        emptyImportButton.Click += (_, _) => _ = AddAssetAsync();
        _emptyState = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "素材库为空", FontSize = 13, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center },
                emptyImportButton,
                new TextBlock { Text = "可拖拽文件到此处导入", FontSize = 11, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Center }
            }
        };
        var assetPanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 8,
                Children = { assetHeader, assetScroll, _assetCoverScroll, _libraryScroll, _textTemplatesScroll, _emptyState }
            }
        };
        Grid.SetRow(assetHeader, 0);
        Grid.SetRow(assetScroll, 1);
        Grid.SetRow(_assetCoverScroll, 1);
        Grid.SetRow(_libraryScroll, 1);
        Grid.SetRow(_textTemplatesScroll, 1);
        Grid.SetRow(_emptyState, 1);
        // 支持从系统拖拽文件到素材库导入（空状态提示真实可用）。
        DragDrop.SetAllowDrop(assetPanel, true);
        assetPanel.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });
        assetPanel.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = e.Data.GetFiles()?.ToList() ?? [];
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            if (paths.Count == 0)
            {
                return;
            }

            await ImportAssetsAsync(paths);
            RefreshAssetList();
            _statusText.Text = $"已从拖拽导入 {paths.Count} 个素材。";
            e.Handled = true;
        });

        // 舞台（中，固定宽高比）：播放内容 + 底部传输条（播放/暂停 + 时间码）在**同一个容器**内，
        // 容器下半段是操作条（不再悬在容器外面）。
        _stageBorder.HorizontalAlignment = HorizontalAlignment.Center;
        _stageBorder.VerticalAlignment = VerticalAlignment.Center;
        _playButton.Click += (_, _) => TogglePreview();
        ToolTip.SetTip(_playButton, "播放 / 暂停（空格）");
        _fullscreenButton.Click += (_, _) => ToggleStageFullscreen();
        ToolTip.SetTip(_fullscreenButton, "全屏预览（再次点击退出全屏）");
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
        var transportBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        transportBar.Children.Add(_playButton);
        transportBar.Children.Add(_timeText);
        transportBar.Children.Add(_fullscreenButton);
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
        // 纵向允许滚动：轨道多 / 轨道高调大时内容可滚动到任意轨道。
        // 标尺固定在滚动区上方（不随纵向滚动走），横向随内容滚动同步（_rulerTranslate）。
        _rulerHost = new Border
        {
            ClipToBounds = true,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ThemePalette.AccentBrushWithAlpha(50),
            Child = _rulerCanvas
        };
        _rulerCanvas.RenderTransform = _rulerTranslate;
        // 内层：仅横向滚动的泳道容器（禁用自带横向滚动条，改由面板底部 _timelineHScroll 同步；
        // 轨道头列在泳道左侧，天然不随横向滚动移动）。
        _lanesScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _timelineRoot
        };
        // 面板底部横向滚动条：固定在时间轴面板底端（不随纵向滚动），驱动 _lanesScroll 横向滚动。
        _timelineHScroll = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Minimum = 0,
            Maximum = 1,
            SmallChange = 10,
            LargeChange = 100,
            ViewportSize = 200,
            Height = 16,
            IsVisible = false
        };
        _timelineHScroll.ValueChanged += (_, _) =>
        {
            if (_syncingHScroll)
            {
                return;
            }

            var maxX = Math.Max(0, _lanesScroll.Extent.Width - _lanesScroll.Viewport.Width);
            _lanesScroll.Offset = new Vector(Math.Clamp(_timelineHScroll.Value, 0, maxX), 0);
        };
        SyncHScrollToLanes();
        // 外层：仅纵向滚动（左轨道头 + 右内层泳道一起滚，天然逐行对齐，无需手动平移轨道头）。
        _timelineScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _lanesScroll.ScrollChanged += (_, _) =>
        {
            _rulerTranslate.X = -_lanesScroll.Offset.X;
            PositionPlayheadHead();
            PositionPlayheadLine();
            SyncHScrollToLanes();
        };
        // 拖拽自动滚动：指针贴近视口边缘时持续滚动（横向滚内层、纵向滚外层）。
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

            var maxX = Math.Max(0, _lanesScroll.Extent.Width - _lanesScroll.Viewport.Width);
            var maxY = Math.Max(0, _timelineScroll.Extent.Height - _timelineScroll.Viewport.Height);
            if (dx != 0)
            {
                _lanesScroll.Offset = new Vector(Math.Clamp(_lanesScroll.Offset.X + dx, 0, maxX), 0);
            }

            if (dy != 0)
            {
                _timelineScroll.Offset = new Vector(0, Math.Clamp(_timelineScroll.Offset.Y + dy, 0, maxY));
            }
        };
        var timelineScroll = _timelineScroll;
        // 时间轴滚轮：Ctrl+滚轮缩放（锚定鼠标下的时间），普通滚轮横向滚动。
        _timelineRoot.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ZoomTimeline(e.Delta.Y > 0 ? 1.25 : 1 / 1.25, e.GetPosition(_timelineRoot).X);
            }
            else
            {
                _lanesScroll.Offset = new Vector(
                    Math.Max(0, _lanesScroll.Offset.X - (e.Delta.Y + e.Delta.X) * 40), 0);
            }

            e.Handled = true;
        };
        // 片段拖拽/裁剪的指针捕获目标固定在稳定的 _timelineRoot（块会被浮到根画布或重建，
        // 捕获块本身会在重挂载时丢失捕获导致拖拽中断），移动/释放/中断统一在此处理。
        _timelineRoot.PointerMoved += (_, e) => OnTimelinePointerMoved(e);
        _timelineRoot.PointerReleased += (_, e) => OnTimelinePointerReleased(e);
        _timelineRoot.PointerCaptureLost += (_, _) => CancelTimelineDrag();
        // 视口宽度变化时记录（内层泳道视口宽），供时间轴内容铺满视口（防抖重建）。
        _lanesScroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0 || Math.Abs(e.NewSize.Width - _timelineViewportWidth) < 1)
            {
                return;
            }

            _timelineViewportWidth = e.NewSize.Width;
            _resizeTimer.Stop();
            _resizeTimer.Start();
            SyncHScrollToLanes();
        };
        // 布局：固定顶行 = 左占位（与标尺等高）+ 右标尺；外层仅纵向滚动，内容 = 左轨道头 + 右内层横向泳道。
        // 轨道头与泳道在同一纵向滚动容器内一起滚动，逐行对齐由布局天然保证（不再手动平移）。
        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("92,*"),
            ColumnSpacing = 8,
            Children = { new Border { Height = RulerHeight + 1 }, _rulerHost }
        };
        Grid.SetColumn(_rulerHost, 1);
        // 播放头抓取头：固定覆盖层（永不重建，缩放/滚动不脱节），舞台八向手柄同款白圆点。
        // 圆头本体移到时间轴全高覆盖层（_playheadOverlay，见 timelineRight 构建处），与竖线同层，
        // 保证竖线贯穿标尺后圆头直接盖在竖线上；headHost 只保留标尺空白点击 seek（透明背景拦截）。
        _playheadHead = BuildPlayheadHead();
        var headHost = new Canvas { ZIndex = 40, Background = Brushes.Transparent };
        Grid.SetColumn(headHost, 1);
        topBar.Children.Add(headHost);
        // 点击标尺区 = seek；分割工具下 = 在该处分割（headHost 覆盖标尺，逻辑移到这里）。
        headHost.PointerPressed += (_, e) =>
        {
            var t = Math.Max(0, e.GetPosition(_rulerCanvas).X / _pxPerSecond);
            if (_splitTool)
            {
                _statusText.Text = CutStatusText(t, CutClipsAt(t));
                e.Handled = true;
                return;
            }

            SetPlayhead(t);
            e.Handled = true;
        };
        var timelineContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("92,*"),
            ColumnSpacing = 8,
            Children = { _trackHeaders, _lanesScroll }
        };
        _timelineContent = timelineContent;
        Grid.SetColumn(_trackHeaders, 0);
        Grid.SetColumn(_lanesScroll, 1);
        _timelineScroll.Content = timelineContent;
        var timelineRight = new Grid
        {
            // 列：轨道头列(92) | 泳道列(*) —— 播放头覆盖层只放在泳道列，seek 线被轨道头遮住。
            ColumnDefinitions = new ColumnDefinitions("92,*"),
            ColumnSpacing = 8,
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { topBar, _timelineScroll }
        };
        Grid.SetColumnSpan(topBar, 2);
        Grid.SetColumnSpan(_timelineScroll, 2);
        Grid.SetRow(_timelineScroll, 1);
        // 分割工具：虚线覆盖层跨标尺与泳道，跟随鼠标（仅分割工具下显示）。
        var splitOverlay = new Canvas { IsHitTestVisible = false, ZIndex = 45 };
        Grid.SetRowSpan(splitOverlay, 2);
        Grid.SetColumnSpan(splitOverlay, 2);
        timelineRight.Children.Add(splitOverlay);
        splitOverlay.Children.Add(_splitCursorLine);
        // 播放头竖线 + 圆头：只覆盖泳道列（标尺 + 泳道全高，RowSpan=2，列=1），
        // 因此竖线不会伸到左侧轨道头列上（seek 线被轨道头遮住）。
        // 竖线贯穿标尺直下泳道，圆头盖在竖线顶部（同层后添加 → 圆头在上）；背景 null 不拦截
        // 空白点击，仅竖线 18px 热区与圆头拦截（拖动 scrub），标尺空白点击穿透到 headHost 做 seek。
        _playheadOverlay = new Canvas { ZIndex = 30, IsHitTestVisible = true };
        Grid.SetRowSpan(_playheadOverlay, 2);
        Grid.SetColumn(_playheadOverlay, 1);
        timelineRight.Children.Add(_playheadOverlay);
        _playheadOverlay.Children.Add(_playhead);
        _playheadOverlay.Children.Add(_playheadHead);
        PositionPlayheadLine();
        timelineRight.SizeChanged += (_, e) =>
        {
            _splitCursorLine.Height = Math.Max(0, e.NewSize.Height);
            // 竖线高度跟随整个时间轴面板（拖动分割条改变面板高度时保持贯穿）。
            _playhead.Height = Math.Max(0, e.NewSize.Height);
        };
        _splitCursorLine.Height = 0;
        timelineScroll.PointerMoved += (_, e) =>
        {
            if (!_splitTool)
            {
                _splitCursorLine.IsVisible = false;
                return;
            }

            var pos = e.GetPosition(timelineRight);
            _splitCursorLine.IsVisible = true;
            Canvas.SetLeft(_splitCursorLine, Math.Max(0, pos.X - 0.75));
            Canvas.SetTop(_splitCursorLine, 0);
        };
        timelineScroll.PointerExited += (_, _) => _splitCursorLine.IsVisible = false;
        // 分割工具：点击时间轴任意位置（含片段）即在该处分割。
        timelineScroll.PointerPressed += (_, e) =>
        {
            if (!_splitTool)
            {
                return;
            }

            var t = Math.Max(0, e.GetPosition(_timelineRoot).X / _pxPerSecond);
            _statusText.Text = CutStatusText(t, CutClipsAt(t));
            e.Handled = true;
        };
        var timelineArea = timelineRight;
        // 时间轴上方工具条：最左 = 工具下拉（选择 A / 分割 B，默认选择），左 = 刀片/删除/画幅/状态；右 = 缩放滑块 + 百分比。
        _timelineToolCombo = new ComboBox
        {
            Width = 138,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "选择工具 (A)", "分割工具 (B)" }
        };
        _timelineToolCombo.SelectedIndex = 0;
        _timelineToolCombo.SelectionChanged += TimelineToolComboOnSelectionChanged;
        ToolTip.SetTip(_timelineToolCombo, "选择工具（A）：点击选中/拖动移动；分割工具（B）：点击时间轴切分片段");
        _zoomText.Text = $"{_pxPerSecond / BasePxPerSecond * 100:0}%";
        var timelineLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _timelineToolCombo, _cutButton, _deleteButton, _canvasButton, _statusText }
        };
        var zoomControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _zoomSlider, _zoomText }
        };
        // 吸附开关：拖动 seek / 素材时吸附元素头尾与 0 点。
        var snapToggle = new CheckBox
        {
            Content = "吸附",
            FontSize = 11,
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(snapToggle, "拖动播放头 / 素材时吸附到片段头尾与 0 点");
        snapToggle.IsCheckedChanged += (_, _) => _snapEnabled = snapToggle.IsChecked == true;
        var timelineRightControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { snapToggle, zoomControls }
        };
        var timelineToolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { timelineLeft, timelineRightControls }
        };
        Grid.SetColumn(timelineRightControls, 1);
        var timelinePanel = new Border
        {
            Background = ThemePalette.MicaPanelBackground(),
            Padding = new Thickness(12),
            ClipToBounds = true,
            MaxHeight = 460,
            Child = new Grid
            {
                // 行：工具条 / 时间轴主体 / 底部横向滚动条（固定在面板底端）。
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 6,
                Children = { timelineToolbar, timelineArea, _timelineHScroll }
            }
        };
        Grid.SetRow(timelineToolbar, 0);
        Grid.SetRow(timelineArea, 1);
        Grid.SetRow(_timelineHScroll, 2);

        // 主体：素材库 | 分割条 | 舞台 | 分割条 | 属性，面板宽度可拖拽调整。
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{_assetPanelWidth},6,*,6,{_inspectorWidth}"),
            Children = { assetPanel, stageColumn, rightPanel }
        };
        // 注意：VerticalSplitter 的 columnIndex 是要改宽度的**列**（素材库列=0、检查器列=4），
        // 不是分割条自身所在的列（分割条在 1 / 3，若写成 1/3 会把分割条拉成面板宽造成大空隙）。
        var vSplitter1 = VerticalSplitter(body, 0, () => _assetPanelWidth, w => _assetPanelWidth = w, 300, 520);
        // 舞台与右侧检查器之间的分割条：分割条位于检查器左侧（也是舞台右边缘），向右拖应使检查器
        // 变窄（舞台变宽），因此方向取反（reverse: true）。
        var vSplitter2 = VerticalSplitter(body, 4, () => _inspectorWidth, w => _inspectorWidth = w, 240, 520, reverse: true);
        body.Children.Insert(1, vSplitter1);
        body.Children.Insert(3, vSplitter2);
        Grid.SetColumn(assetPanel, 0);
        Grid.SetColumn(vSplitter1, 1);
        Grid.SetColumn(stageColumn, 2);
        Grid.SetColumn(vSplitter2, 3);
        Grid.SetColumn(rightPanel, 4);

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

    private void SetTool(string tool)
    {
        _currentTool = tool;
        // 素材库内容随工具切换：形状工具→形状库，效果工具→滤镜库。
        UpdateLibraryContent();
        _statusText.Text = tool switch
        {
            "text" => "文本工具：在舞台点击放置文本覆盖层。",
            "shape" => "形状工具：在素材库选择形状，点击舞台放置或拖到时间轴。",
            "effect" => "效果工具：在素材库选择滤镜，拖到时间轴作为片段应用。",
            _ => "选择工具。"
        };
    }

    /// <summary>切换时间轴工具（选择/分割）；同步下拉与状态栏提示。</summary>
    private void SetSplitTool(bool split)
    {
        _splitTool = split;
        if (_timelineToolCombo != null)
        {
            _timelineToolCombo.SelectionChanged -= TimelineToolComboOnSelectionChanged;
            _timelineToolCombo.SelectedIndex = split ? 1 : 0;
            _timelineToolCombo.SelectionChanged += TimelineToolComboOnSelectionChanged;
        }

        _splitCursorLine.IsVisible = false;
        _statusText.Text = split
            ? "分割工具：在时间轴点击即可把片段在指针处切成两段（快捷键 B；A 切回选择工具）。"
            : "选择工具：点击选中片段、拖动移动（快捷键 A；B 切到分割工具）。";
    }

    /// <summary>时间轴工具下拉选择变化。</summary>
    private void TimelineToolComboOnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SetSplitTool(_timelineToolCombo.SelectedIndex == 1);
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
            Scale = 1,
            ScaleX = 1,
            ScaleY = 1
        };
        if (kind == "Shape")
        {
            // 形状默认 1:1（正方形，边长 = 输出画布短边的 60%），按画布像素换算。
            var outW = Math.Max(1, _project.OutputWidth);
            var outH = Math.Max(1, _project.OutputHeight);
            var side = Math.Max(24, Math.Min(outW, outH) * 0.6);
            clip.ScaleX = side / outW;
            clip.ScaleY = side / outH;
        }

        if (stagePos != null)
        {
            var W = _stageBorder.Bounds.Width;
            var H = _stageBorder.Bounds.Height;
            clip.OffsetX = Math.Clamp((stagePos.Value.X - W / 2.0) / Math.Max(1, W), -2, 2);
            clip.OffsetY = Math.Clamp((stagePos.Value.Y - H / 2.0) / Math.Max(1, H), -2, 2);
        }

        clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
        _project.Clips.Add(clip);
        SelectClip(clip);
        _currentTool = "select";
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
            _inSpin, _outSpin, _pxXSpin, _pxYSpin, _pxWSpin, _pxHSpin,
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
            if (e.Property == ColorPicker.ColorProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _overlayShape.SelectionChanged += (_, _) => ApplyOverlayEdits();
        _strokeWidthSpin.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _strokeColor.PropertyChanged += (_, e) =>
        {
            if (e.Property == ColorPicker.ColorProperty)
            {
                ApplyOverlayEdits();
            }
        };
        // 文本样式（仅 Text）：字体下拉（枚举系统字体）/ 字号 / 加粗。
        _systemFonts = FontManager.Current.SystemFonts
            .DistinctBy(font => font.Name)
            .OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _overlayFontBox.ItemsSource = _systemFonts;
        _overlayFontBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FontFamily>((font, _) =>
        {
            if (font == null)
            {
                return new TextBlock { Height = 24 };
            }

            return new TextBlock
            {
                Text = font.Name,
                FontFamily = font,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        });
        _overlayFontBox.SelectionChanged += (_, _) => ApplyOverlayEdits();
        _textSizeSpin.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _textBoldCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty)
            {
                ApplyOverlayEdits();
            }
        };
        _overlayTextRow = InspectorRow("内容", _overlayText);
        _overlayColorRow = InspectorRow("填充颜色", _overlayColor);
        _overlayShapeRow = InspectorRow("形状", _overlayShape);
        _strokeWidthRow = InspectorRow("描边宽度", _strokeWidthSpin);
        _strokeColorRow = InspectorRow("描边颜色", _strokeColor);
        _overlayFontRow = InspectorRow("字体", _overlayFontBox);
        _textSizeRow = InspectorRow("字号（px）", _textSizeSpin);
        _textBoldRow = InspectorRow("字重", _textBoldCheck);
        _overlayPanel.Children.Add(_overlayTextRow);
        _overlayPanel.Children.Add(_overlayFontRow);
        _overlayPanel.Children.Add(_textSizeRow);
        _overlayPanel.Children.Add(_textBoldRow);
        _overlayPanel.Children.Add(_overlayColorRow);
        _overlayPanel.Children.Add(_strokeWidthRow);
        _overlayPanel.Children.Add(_strokeColorRow);
        _overlayPanel.Children.Add(_overlayShapeRow);
        // 滤镜编辑（滤镜 Tab 内）：类型 + 强度（滤镜只有效果强度，无变换；强度用滑动条直观调节）。
        _filterCombo.ItemsSource = FilterDefs.Select(d => d.Name).ToList();
        _filterCombo.SelectionChanged += (_, _) => ApplyFilterEdits();
        _filterIntensitySlider.ValueChanged += (_, _) => ApplyFilterEdits();
        _filterPanel.Children.Add(InspectorRow("类型", _filterCombo));
        _filterPanel.Children.Add(InspectorRow("强度", _filterIntensitySlider));

        // Tab 选项卡分组：变换 / 覆盖层（仅 Text/Shape/Image）/ 滤镜（仅 Filter）。
        var transformPanel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                InspectorRow("入点（秒）", _inSpin),
                InspectorRow("出点（秒）", _outSpin),
                new TextBlock { Text = "变换", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                InspectorRow("X（px）", _pxXSpin),
                InspectorRow("Y（px）", _pxYSpin),
                InspectorRow("宽度（px）", _pxWSpin),
                InspectorRow("高度（px）", _pxHSpin),
                InspectorRow("旋转（度）", _rotationSpin),
                InspectorRow("不透明度", _opacitySpin),
                new TextBlock { Text = "边缘裁剪", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                InspectorRow("裁左", _cropLSpin),
                InspectorRow("裁上", _cropTSpin),
                InspectorRow("裁右", _cropRSpin),
                InspectorRow("裁下", _cropBSpin)
            }
        };
        _inspectorPages["transform"] = transformPanel;
        _inspectorPages["overlay"] = _overlayPanel;
        _inspectorPages["filter"] = _filterPanel;
        // 分段条：ClassIsland 原生 TabStrip（TabStripStyle + compact 类），与规则集
        // 「规则任一/全部满足时」同款外观；覆盖层/滤镜段按选中片段类型显隐。
        _inspectorContent = new ContentControl();
        _inspectorSegmented = new TabStrip { HorizontalAlignment = HorizontalAlignment.Left };
        if (ThemePalette.FindResource("TabStripStyle") is ControlTheme tabTheme)
        {
            _inspectorSegmented.Theme = tabTheme;
        }

        _inspectorSegmented.Classes.Add("compact");
        _segmentButtons.Clear();
        // 图标 + 文本：平时只显示图标，激活（选中）时展开为图标+文本（规则集 TabStrip 同款
        // AnimatedIconButton display-role；IsKeepingExpanded 跟随 TabStripItem.IsSelected）。
        foreach (var (key, label, glyph) in new[]
        {
            ("transform", "变换", "\uE0EC"),
            ("overlay", "覆盖层", "\uEA2E"),
            ("filter", "滤镜", "\uE832")
        })
        {
            var item = new TabStripItem();
            var button = new AnimatedIconButton { Glyph = glyph, Text = label };
            button.Classes.Add("display-role");
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == ListBoxItem.IsSelectedProperty)
                {
                    button.IsKeepingExpanded = item.IsSelected;
                }
            };
            item.Content = button;
            _segmentButtons[key] = item;
            _inspectorSegmented.Items.Add(item);
        }

        _inspectorSegmented.SelectionChanged += (_, _) =>
        {
            if (_inspectorSegmented.SelectedItem is not TabStripItem sel)
            {
                return;
            }

            foreach (var (key, item) in _segmentButtons)
            {
                // 同步各面板 IsVisible（否则点「变换」后内容还是上一页隐藏状态 → 空白）。
                if (!_inspectorPages.TryGetValue(key, out var page))
                {
                    continue;
                }

                var isSel = ReferenceEquals(item, sel);
                page.IsVisible = isSel;
                if (isSel)
                {
                    _inspectorContent.Content = page;
                    _currentInspectorPage = key;
                }
            }
        };
        // 默认选中变换段。
        SelectInspectorPage("transform");
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _inspectorSegmented,
                _inspectorContent
            }
        };
    }

    /// <summary>
    /// 切换检查器页面（TabStrip 驱动，同步选中项）。
    /// 同时把各面板 IsVisible 设为只有当前页可见（_overlayPanel/_filterPanel 曾初始隐藏且从未
    /// 置可见，导致覆盖层/滤镜页选中后内容空白）。
    /// </summary>
    private void SelectInspectorPage(string key)
    {
        if (!_inspectorPages.TryGetValue(key, out var page))
        {
            return;
        }

        foreach (var (k, p) in _inspectorPages)
        {
            p.IsVisible = k == key;
        }

        _inspectorContent.Content = page;
        _currentInspectorPage = key;
        if (_segmentButtons.TryGetValue(key, out var item))
        {
            _inspectorSegmented.SelectedItem = item;
        }
    }

    /// <summary>滤镜类型下拉修改应用到选中滤镜片段（连续修改合并为一步撤销）。</summary>
    private void ApplyFilterEdits()
    {
        if (_updatingUi || _selected is not { Kind: "Filter" } clip)
        {
            return;
        }

        if (_project.GetTrackState(clip.Track) is { Locked: true })
        {
            FillPropertyPanel();
            _statusText.Text = "该轨道已锁定，无法修改属性。";
            return;
        }

        var idx = _filterCombo.SelectedIndex;
        if (idx < 0 || idx >= FilterDefs.Length)
        {
            return;
        }

        PushUndo(true);
        clip.Filter = FilterDefs[idx].Key;
        clip.FilterIntensity = Math.Clamp(_filterIntensitySlider.Value, 0, 1);
        ScheduleSave();
        _statusText.Text = $"滤镜已改为「{FilterDefs[idx].Name}」（强度 {clip.FilterIntensity:P0}）。";
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

        if (_project.GetTrackState(clip.Track) is { Locked: true })
        {
            FillPropertyPanel();
            _statusText.Text = "该轨道已锁定，无法修改属性。";
            return;
        }

        PushUndo(true);
        clip.Text = _overlayText.Text ?? "";
        clip.Color = _overlayColor.Color.ToString();
        clip.Shape = _overlayShape.SelectedItem?.ToString() ?? "Rect";
        clip.StrokeWidth = Math.Clamp(_strokeWidthSpin.DoubleValue, 0, 0.2);
        clip.StrokeColor = _strokeColor.Color.ToString();
        // 文本样式：字体下拉（SelectedItem = FontFamily）/ 字号（px → 相对画布高的内部系数）/ 加粗。
        if (clip.Kind == "Text")
        {
            var canvasH = Math.Max(1, _project.OutputHeight);
            clip.TextFontFamily = (_overlayFontBox.SelectedItem as FontFamily)?.Name ?? "";
            clip.TextFontSize = Math.Clamp(_textSizeSpin.DoubleValue / canvasH, 0.02, 4);
            clip.TextBold = _textBoldCheck.IsChecked == true;
        }

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
        // 所有设置项（spinbox / combobox / 颜色选择器 / 按钮 / 滑条等）严格贴右列右对齐。
        control.HorizontalAlignment = HorizontalAlignment.Right;
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
            _project.TrackStates.Clear();
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

        // 轨道状态随轨道号压缩重排（缺失用默认，空轨状态丢弃）。
        var newStates = new List<TrackState>();
        for (var i = 0; i < used.Count; i++)
        {
            newStates.Add(_project.GetTrackState(used[i]) ?? new TrackState());
        }

        _project.TrackStates.Clear();
        _project.TrackStates.AddRange(newStates);

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
        var newPx = Math.Clamp(oldPx * factor, 0.36, 360);
        if (Math.Abs(newPx - oldPx) < 0.01)
        {
            return;
        }

        var offsetX = _lanesScroll.Offset.X;
        var anchorTime = anchorX != null ? Math.Max(0, anchorX.Value / oldPx) : _playheadTime;
        _pxPerSecond = newPx;
        RefreshTimeline();
        // 锚定：t*newPx - newOffsetX = t*oldPx - oldOffsetX → newOffsetX = t*(newPx-oldPx) + oldOffsetX。
        var newOffset = Math.Max(0, anchorTime * (newPx - oldPx) + offsetX);
        _lanesScroll.Offset = new Vector(newOffset, 0);
        _zoomText.Text = $"{_pxPerSecond / BasePxPerSecond * 100:0}%";
    }

    // ============ 撤销/重做 ============

    /// <summary>深拷贝工程快照（撤销栈用；片段用 Clone 保持独立性）。</summary>
    private static VideoProject CloneProject(VideoProject p) => new()
    {
        OutputWidth = p.OutputWidth,
        OutputHeight = p.OutputHeight,
        Clips = p.Clips.Select(c => c.Clone()).ToList(),
        TrackStates = p.TrackStates.Select(s => new TrackState
        {
            Locked = s.Locked,
            Hidden = s.Hidden
        }).ToList()
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
        // 若撤销发生在拖拽/裁剪途中（例如拖动时按 Ctrl+Z），先取消拖拽，避免旧片段引用残留。
        CancelTimelineDrag();
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
        // 与 Undo 一样：先取消在途拖拽/裁剪，防止旧片段对象引用残留到新工程。
        CancelTimelineDrag();
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
        // 工程片段被整体替换成了新对象：多选集必须同步重建，否则残留的旧对象引用
        // 在后续拖拽里找不到块（_blockByClip 键不匹配）→ “拖了不跟手 / KeyNotFound”。
        _selectedClips.Clear();
        if (_selected != null)
        {
            _selectedClips.Add(_selected);
        }

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
        // 面板可见性也要重算：从空素材库导入后 _assetList/_assetCoverScroll 仍是隐藏态
        // （它们的 IsVisible 只在本方法里按素材数量更新），必须立即显示新导入的素材，
        // 否则要切一次工具选项卡才能看到。
        UpdateLibraryContent();
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
        SelectClip(clip);
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
        SelectClip(clip);
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
    }

    /// <summary>打开素材探测时长（打开解码器后立即释放）。</summary>
    private static double ProbeDuration(string path)
    {
        try
        {
            // 探测前确保 FFmpeg 库已就绪：ffmpeg.RootPath 只有 EnsureLoaded() 会设置，
            // 若本进程从未加载过（如从未启用过视频填充/压缩），直接调 ffmpeg 会抛
            // DllNotFoundException → 时长探测失败 → 拖入片段只能落到 10 秒兜底长度。
            if (!FFmpegRuntime.IsAvailable || !FFmpegRuntime.EnsureLoaded())
            {
                return 0;
            }

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
        SelectClip(clip);
        _selectedTrack = clip.Track;
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        RefreshAssetList();
        ScheduleSave();
        _statusText.Text = "已导入底图图层快照（图片覆盖层，可在属性面板调整）。";
    }

    /// <summary>取素材时长（带缓存，供裁剪右边界上限）。探测失败（0）不缓存，下次拖入重试。</summary>
    private double GetAssetDuration(string path)
    {
        if (_assetDurations.TryGetValue(path, out var d) && d > 0)
        {
            return d;
        }

        d = ProbeDuration(path);
        if (d > 0)
        {
            _assetDurations[path] = d;
        }

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

    /// <summary>按当前工具更新素材库内容：选择→素材；文字→文字模板；形状→形状卡片；效果→滤镜卡片。</summary>
    private void UpdateLibraryContent()
    {
        var isLibraryMode = _currentTool is "shape" or "effect";
        var isAssetMode = _currentTool == "select";
        var isTextMode = _currentTool == "text";
        _assetList.IsVisible = isAssetMode && !_assetCoverView && _assets.Count > 0;
        _assetCoverScroll.IsVisible = isAssetMode && _assetCoverView && _assets.Count > 0;
        _libraryScroll.IsVisible = isLibraryMode;
        _textTemplatesScroll.IsVisible = isTextMode;
        _emptyState.IsVisible = isAssetMode && _assets.Count == 0;
        // 视图切换按钮只在素材模式下有意义。
        _assetViewToggle.IsVisible = isAssetMode;
        // 形状/滤镜库：卡片/列表切换。
        _libraryViewToggle.IsVisible = isLibraryMode;
        if (_libraryViewToggle.Content is IconText libIcon)
        {
            libIcon.Glyph = _libraryCardsView ? "\uEAC0" : "\uE929"; // 列表视图时显示「列表」图标
        }

        ToolTip.SetTip(_libraryViewToggle, _libraryCardsView ? "切换到列表视图" : "切换到缩略图视图");
        if (isLibraryMode)
        {
            RefreshLibraryCards();
        }

        if (isTextMode)
        {
            RefreshTextTemplates();
        }
    }

    /// <summary>重建文字样式预设面板（文字工具时：点击按样式添加文本；按住可拖到时间轴指定轨道/位置）。
    /// 预设 = 样式（字体 / 字号 / 加粗 / 颜色 / 描边），添加的内容为中性默认文本，不含预设内容。</summary>
    private void RefreshTextTemplates()
    {
        _textTemplatesPanel.Children.Clear();
        for (var index = 0; index < TextStylePresets.Length; index++)
        {
            var preset = TextStylePresets[index];
            var payload = $"textstyle:{index}";
            var preview = new TextBlock
            {
                Text = preset.Sample,
                FontFamily = new FontFamily(preset.FontFamily),
                FontSize = Math.Clamp(58 * preset.SizeFactor, 11, 30),
                FontWeight = preset.Bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = new SolidColorBrush(ThemePalette.ForegroundColor()),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 10, 8, 2)
            };
            var caption = new TextBlock
            {
                Text = preset.Name,
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 4)
            };
            // 卡片底部色条 = 该样式填充色（预览字用主题前景保证深浅主题都可见，颜色由色条表达）。
            var colorBar = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(ParseHexColor(preset.Color)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(10, 0, 10, 8)
            };
            var item = new Border
            {
                Width = 148,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Children = { preview, caption, colorBar } }
            };
            Point? press = null;
            var dragged = false;
            item.PointerPressed += (_, e) =>
            {
                press = e.GetPosition(item);
                dragged = false;
            };
            // 按住左键移动超过阈值 = 开始拖拽（载荷 textstyle:下标，时间轴放置时按样式创建文本片段）。
            item.PointerMoved += (_, e) =>
            {
                if (press is not { } p || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                if (!dragged)
                {
                    var cur = e.GetPosition(item);
                    if (Math.Abs(cur.X - p.X) < 4 && Math.Abs(cur.Y - p.Y) < 4)
                    {
                        return; // 尚未超过拖拽阈值
                    }
                }

                dragged = true;
                var data = new DataObject();
                data.Set(DataFormats.Text, payload);
                DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
            };
            // 无拖拽的轻点 = 点击按该样式添加；拖拽过的释放不再触发点击。
            item.PointerReleased += (_, _) =>
            {
                if (!dragged)
                {
                    AddTextStyleClip(index);
                }
            };
            _textTemplatesPanel.Children.Add(item);
        }
    }

    /// <summary>点击文字样式预设：在当前播放头按预设样式添加一个文本片段（内容为中性默认「文本」）。</summary>
    private void AddTextStyleClip(int index)
    {
        if (index < 0 || index >= TextStylePresets.Length)
        {
            return;
        }

        var preset = TextStylePresets[index];
        AddOverlayClip("Text", "Rect");
        if (_selected is { } clip)
        {
            clip.Text = "文本";
            ApplyTextStylePreset(clip, preset);
            RefreshTimeline();
            FillPropertyPanel();
            ScheduleSave();
        }

        // AddOverlayClip 已切回选择工具，刷新素材库回到素材视图。
        UpdateLibraryContent();
        _statusText.Text = $"已按「{preset.Name}」样式添加文本（右侧可编辑文字与颜色）。";
    }

    /// <summary>把文字样式预设应用到文本片段。</summary>
    private static void ApplyTextStylePreset(VideoClip clip, TextStylePreset preset)
    {
        clip.TextFontFamily = preset.FontFamily;
        clip.TextFontSize = preset.SizeFactor;
        clip.TextBold = preset.Bold;
        clip.Color = preset.Color;
        clip.StrokeColor = preset.StrokeColor;
        clip.StrokeWidth = preset.StrokeWidth;
    }

    /// <summary>解析 #AARRGGBB → Avalonia Color（缺 alpha 按 255；失败回退白色）。</summary>
    private static Color ParseHexColor(string text)
    {
        try
        {
            var s = text.Trim().TrimStart('#');
            if (s.Length < 6)
            {
                return Colors.White;
            }

            return Color.FromArgb(
                s.Length >= 8 ? Convert.ToByte(s[..2], 16) : (byte)255,
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16),
                Convert.ToByte(s.Substring(6, 2), 16));
        }
        catch
        {
            return Colors.White;
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

    /// <summary>轻量刷新形状/滤镜库选中高亮（不重建面板，避免打断卡片拖拽）。</summary>
    private void UpdateLibrarySelectionHighlight()
    {
        foreach (var child in _libraryPanel.Children)
        {
            if (child is not Border b || b.Tag is not string payload)
            {
                continue;
            }

            var selected = payload.StartsWith("shape:") && payload["shape:".Length..] == _currentShape;
            b.BorderBrush = selected ? ThemePalette.AccentBrush() : Brushes.Transparent;
            b.BorderThickness = new Thickness(selected ? 1.5 : 0);
        }
    }

    /// <summary>构建库列表行（紧凑：名称 + 拖到时间轴；点击选中与卡片一致）。</summary>
    private Border BuildLibraryRow(string name, string payload, bool selected)
    {
        var item = new Border
        {
            Tag = payload,
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
                UpdateLibrarySelectionHighlight();
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
            Tag = payload,
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
                UpdateLibrarySelectionHighlight();
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
        // 片段增删/拖拽/撤销后同步预览播放器：它若仍按构造时的片段快照调度，
        // 已删除的片段会继续出现在预览里（即使时间轴已不显示）。
        _player?.RefreshClips();
        var trackCount = _project.TrackCount;
        _lanes.Clear();
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
            var state = _project.TrackStateOf(trackIndex);
            // 轨道头：直接横向排列 锁定 / 隐藏 / 删除 三个按钮（FluentSystemIcons-Resizable 图标）。
            var btnLock = TrackHeaderButton("\uEAEF", "锁定/解锁该轨道（锁定后该轨片段不可编辑）", state.Locked,
                () => { state.Locked = !state.Locked; RefreshTimeline(); ScheduleSave(); });
            var btnHide = TrackHeaderButton(state.Hidden ? "\uE816" : "\uE812",
                "隐藏该轨道（编辑半透明，播放/渲染不显示）", state.Hidden,
                () => { state.Hidden = !state.Hidden; RefreshTimeline(); ScheduleSave(); });
            var btnDel = TrackHeaderButton("\uE61C", "删除该轨道（该轨全部片段）", false,
                () => DeleteTrack(trackIndex));
            var headerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { btnLock, btnHide, btnDel }
            };
            // 选中轨道用普通亮灰（不用主题强调色，也无白边框）。
            var header = new Border
            {
                Height = LaneHeightOf(t),
                CornerRadius = new CornerRadius(4),
                Background = trackIndex == _selectedTrack
                    ? new SolidColorBrush(TrackHeaderSelectedColor())
                    : new SolidColorBrush(state.Hidden ? TrackHeaderHiddenColor() : TrackHeaderIdleColor()),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Children = { headerButtons, resizeGrip }
                }
            };
            Grid.SetRow(headerButtons, 0);
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
        // ⚠️ _blockByClip 必须先于建块清空：BuildClipBlock 在下方循环里写入字典；
        // 若在末尾才 Clear（旧代码）会把刚填好的字典又清空 → 字典恒空 → 拖拽/选中高亮全部失效。
        _blockByClip.Clear();
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
            // DragOver 同时高亮目标泳道（或插入指示线）+ 更新落点标签 + 驱动边缘自动滚动。
            DragDrop.SetAllowDrop(lane, true);
            lane.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
                UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
                var (targetTrack, insertPos) = ResolveDropTarget(e.GetPosition(_timelineRoot).Y, _project.TrackCount);
                UpdateDropHighlight(targetTrack, _project.TrackCount, false, insertPos);
                e.Handled = true;
            });
            lane.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
            lane.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e));
            // 点击泳道空白处：把播放头移到该位置（播放中则跳转）；选择模式下按住拖动 = 框选多片段。
            // 分割工具下不移动播放头（分割由时间轴层 PointerPressed 统一处理）。
            lane.PointerPressed += (_, e) =>
            {
                if (_splitTool)
                {
                    return;
                }

                if (e.Source == lane || e.Source == canvas)
                {
                    SetPlayhead(Math.Max(0, e.GetPosition(_timelineRoot).X / _pxPerSecond));
                    // 记录框选起点并捕获（移动超阈值 = 画框；无移动 = 纯点击 seek）。
                    _marqueeStart = e.GetPosition(_timelineRoot);
                    _marqueeMoved = false;
                    e.Pointer.Capture(lane);
                }
            };
            lane.PointerMoved += (_, e) =>
            {
                if (_splitTool || _marqueeStart is not { } start)
                {
                    return;
                }

                var cur = e.GetPosition(_timelineRoot);
                if (Math.Abs(cur.X - start.X) < 4 && Math.Abs(cur.Y - start.Y) < 4)
                {
                    return;
                }

                _marqueeMoved = true;
                var x = Math.Min(start.X, cur.X);
                var y = Math.Min(start.Y, cur.Y);
                _marqueeRect.IsVisible = true;
                Canvas.SetLeft(_marqueeRect, x);
                Canvas.SetTop(_marqueeRect, y);
                _marqueeRect.Width = Math.Abs(cur.X - start.X);
                _marqueeRect.Height = Math.Abs(cur.Y - start.Y);
            };
            lane.PointerReleased += (_, e) =>
            {
                if (_splitTool || _marqueeStart is not { } start)
                {
                    return;
                }

                _marqueeStart = null;
                var wasMove = _marqueeMoved;
                _marqueeMoved = false;
                _marqueeRect.IsVisible = false;
                e.Pointer.Capture(null);
                if (wasMove)
                {
                    // 完成框选：选中与框相交的片段（保留锁定轨片段为只读选中）。
                    var cur = e.GetPosition(_timelineRoot);
                    _selectedClips.Clear();
                    foreach (var c in MarqueeSelect(start, cur))
                    {
                        _selectedClips.Add(c);
                    }

                    _selected = _selectedClips.FirstOrDefault();
                    RefreshTimeline(); // 重建块，按 isSelected 高亮（框选视觉反馈）。
                    FillPropertyPanel();
                    UpdateStageHandles();
                    _statusText.Text = _selectedClips.Count == 0
                        ? "未选中片段。"
                        : $"已框选 {_selectedClips.Count} 个片段（可拖动移动 / Delete 删除）。";
                }
                else
                {
                    // 点击空白（未形成框选）：取消选择（仅 seek）。
                    _selectedClips.Clear();
                    _selected = null;
                    UpdateAllBlockSelection();
                    FillPropertyPanel();
                    UpdateStageHandles();
                }
            };

            foreach (var clip in _project.Clips.Where(c => c.Track == trackIndex).OrderBy(c => c.StartTime))
            {
                canvas.Children.Add(BuildClipBlock(clip));
            }
        }

        // 无轨道时仍渲染一条占位泳道：保证素材库第一次拖入时有可拖放的目标（落点 = 新建轨道 1）。
        if (trackCount == 0)
        {
            var placeholder = new Border
            {
                Height = _laneHeight,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(LaneFillColor()),
                ClipToBounds = true,
                Child = new Canvas { Width = totalWidth, Height = _laneHeight }
            };
            _lanes.Add(placeholder);
            _timeline.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _timeline.Children.Add(placeholder);
            DragDrop.SetAllowDrop(placeholder, true);
            placeholder.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = DragDropEffects.Copy | DragDropEffects.Move;
                UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
                var (targetTrack, insertPos) = ResolveDropTarget(e.GetPosition(_timelineRoot).Y, _project.TrackCount);
                UpdateDropHighlight(targetTrack, _project.TrackCount, false, insertPos);
                e.Handled = true;
            });
            placeholder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
            placeholder.AddHandler(DragDrop.DropEvent, (_, e) => HandleTimelineDrop(e));
        }

        // 时间轴根画布：泳道网格 + 插入指示线 + 播放头（标尺已移出滚动区固定在面板顶部，见 _rulerHost）。
        var lanesHeight = 0.0;
        for (var t = 0; t < trackCount; t++)
        {
            lanesHeight += LaneHeightOf(t);
        }

        if (trackCount == 0)
        {
            lanesHeight = _laneHeight;
        }

        _timelineRoot.Children.Clear();
        _timelineRoot.Width = totalWidth;
        _timelineRoot.Height = lanesHeight;
        Canvas.SetTop(_timeline, 0);
        _timelineRoot.Children.Add(_timeline);
        // 轨道头/泳道容器高度严格一致 + 顶部对齐：同一滚动容器内两列高度锁定为 lanesHeight，
        // 防止 ScrollViewer 拉伸差异导致轨道头与泳道逐行错位（拖动分割条/调轨高后仍对齐）。
        if (_timelineContent != null)
        {
            _timelineContent.Height = lanesHeight;
        }
        _trackHeaders.VerticalAlignment = VerticalAlignment.Top;
        _lanesScroll.VerticalAlignment = VerticalAlignment.Top;
        // 标尺重建进固定宿主（宽度 = 内容宽；横向随滚动平移，纵向不动）。
        _rulerCanvas.Children.Clear();
        _rulerCanvas.Width = totalWidth;
        _rulerCanvas.Height = RulerHeight;
        _rulerCanvas.Children.Add(BuildRuler(totalWidth));
        _insertIndicator.IsVisible = false;
        _timelineRoot.Children.Add(_insertIndicator);
        _marqueeRect.IsVisible = false;
        _timelineRoot.Children.Add(_marqueeRect);
        // 播放头竖线/圆头已移到时间轴全高覆盖层（_playheadOverlay），这里只同步位置。
        PositionPlayheadLine();
        _timeText.Text = FormatTime(_playheadTime);

        _zoomText.Text = $"{_pxPerSecond / BasePxPerSecond * 100:0}%";
        _updatingZoom = true;
        _zoomSlider.Value = _pxPerSecond;
        _updatingZoom = false;

        // 时间轴面板行高：默认随内容自动；拖分割条后取用户高度（下限为内容所需，上限 460）。
        // 内容高 = 工具条 + 标尺（固定行）+ 泳道（滚动区）+ 底部横向滚动条(16) + 行间距(6)。
        _timelineChromeHeight = 24 + TimelineToolbarHeight + 6 + RulerHeight + lanesHeight + 22;
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

        // 定位标尺抓取头（固定层，缩放/滚动后仍与 seek 竖线同步）。
        PositionPlayheadHead();
        // 内容宽度变化后同步底部横向滚动条。
        SyncHScrollToLanes();
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
        var interval = NiceTickInterval(_pxPerSecond);
        var half = interval / 2;
        var total = Math.Max(_project.Duration + 5, 20);
        var count = (int)Math.Ceiling(total / half);
        // 主刻度用主题强调色（切歌 / 动态主题变化时随主题重绘）；次刻度用中性灰并随深浅主题。
        var majorBrush = new SolidColorBrush(ThemePalette.AccentColorWithAlpha(205));
        var minorBrush = new SolidColorBrush(ThemePalette.IsDarkTheme()
            ? Color.FromArgb(80, 190, 190, 200)
            : Color.FromArgb(80, 80, 80, 90));
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

    /// <summary>标尺上的播放头抓取头（舞台八向手柄同款白圆点 + 透明热区；点击/拖动 seek，缩放/滚动不脱节）。</summary>
    private Border BuildPlayheadHead()
    {
        var head = new Border
        {
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            ZIndex = 50
        };
        head.Child = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = Brushes.White,
            Stroke = ThemePalette.AccentBrush(),
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        head.PointerPressed += (_, e) =>
        {
            _scrubbing = true;
            e.Pointer.Capture(head);
            SeekPlayheadFromRuler(e);
            e.Handled = true; // 阻止冒泡到标尺（避免分割工具下又切一刀）。
        };
        head.PointerMoved += (_, e) =>
        {
            if (_scrubbing)
            {
                SeekPlayheadFromRuler(e);
            }
        };
        head.PointerReleased += (_, e) =>
        {
            if (_scrubbing)
            {
                _scrubbing = false;
                e.Pointer.Capture(null);
                if (!_seekFrameBusy)
                {
                    DisposeSeekSources();
                }
            }
        };
        head.PointerCaptureLost += (_, _) => _scrubbing = false;
        return head;
    }

    /// <summary>把底部横向滚动条同步到泳道视口/内容宽度（最大值 = 内容-视口，thumb 比例 = 视口/内容）。</summary>
    private void SyncHScrollToLanes()
    {
        if (_lanesScroll == null || _timelineHScroll == null)
        {
            return;
        }

        var maxX = Math.Max(0, _lanesScroll.Extent.Width - _lanesScroll.Viewport.Width);
        _syncingHScroll = true;
        _timelineHScroll.Maximum = Math.Max(1, maxX);
        _timelineHScroll.ViewportSize = Math.Max(1, _lanesScroll.Viewport.Width);
        _timelineHScroll.IsVisible = maxX > 0;
        _timelineHScroll.Value = Math.Clamp(_lanesScroll.Offset.X, 0, maxX);
        _syncingHScroll = false;
    }

    /// <summary>
    /// 定位播放头竖线（在 _playheadOverlay 覆盖层内，覆盖层已在泳道列，0 点与泳道/标尺对齐）。
    /// 2px 竖线在 18px 热区左缘，中心 = left + 1 = time*px - offsetX。
    /// </summary>
    private void PositionPlayheadLine()
    {
        if (_playhead == null)
        {
            return;
        }

        Canvas.SetLeft(_playhead, _playheadTime * _pxPerSecond - 1 - _lanesScroll.Offset.X);
        Canvas.SetTop(_playhead, 0);
    }

    /// <summary>定位标尺上的播放头抓取头。</summary>
    /// 与竖线同基准：圆头中心 = left + 9 = time*px - offsetX（与泳道/标尺 0 点对齐）。
    /// </summary>
    private void PositionPlayheadHead()
    {
        if (_playheadHead == null)
        {
            return;
        }

        Canvas.SetLeft(_playheadHead, _playheadTime * _pxPerSecond - 9 - _lanesScroll.Offset.X);
        Canvas.SetTop(_playheadHead, RulerHeight - 16);
    }

    /// <summary>从标尺坐标把播放头 seek 到指针处（内容坐标，含横向滚动）。</summary>
    private void SeekPlayheadFromRuler(PointerEventArgs e)
    {
        SetPlayhead(Math.Max(0, e.GetPosition(_rulerCanvas).X / _pxPerSecond));
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
        // 拖动 seek 且吸附开启时吸附到片段头尾 / 0 点。
        if (_scrubbing && _snapEnabled)
        {
            time = SnapTime(time);
        }
        _playheadTime = Math.Max(0, time);
        PositionPlayheadLine();
        PositionPlayheadHead();
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
        PositionPlayheadLine();
        PositionPlayheadHead();
        _timeText.Text = FormatTime(_playheadTime);
    }

    /// <summary>动态主题（切歌）强调色变化时，让时间轴的 seek 线 / 标尺等强调色元素一起切换。</summary>
    private void WatchTimelineAccent()
    {
        var accent = ThemePalette.AccentColor();
        if (accent == _lastTimelineAccent)
        {
            return;
        }

        _lastTimelineAccent = accent;
        // 拖拽 / 裁剪 / scrub / 旋转 / 缩放中不打断（等该手势结束后的下一拍再应用）。
        if (_moveGroup != null || _trimState != null || _scrubbing || _rotating || _resizeHandle >= 0)
        {
            return;
        }

        // 重建时间轴（标尺 / 轨道 / 选中块都会按新强调色重绘）+ 重设播放头等常驻强调色元素。
        RefreshTimeline();
        RestyleTimelineAccent();
    }

    /// <summary>按当前强调色重设时间轴 / 舞台里常驻的强调色元素（seek 线、播放头圈、插入指示线、手柄等）。</summary>
    private void RestyleTimelineAccent()
    {
        if (_playhead.Child is Border line)
        {
            line.Background = ThemePalette.AccentBrush();
        }

        if (_playheadHead?.Child is Ellipse headDot)
        {
            headDot.Stroke = ThemePalette.AccentBrush();
        }

        _splitCursorLine.Stroke = ThemePalette.AccentBrush();
        _insertIndicator.Background = ThemePalette.AccentBrush();
        _marqueeRect.Stroke = ThemePalette.AccentBrush();
        _marqueeRect.Fill = ThemePalette.AccentBrushWithAlpha(35);
        _handleOutline.BorderBrush = ThemePalette.AccentBrushWithAlpha(220);
        _handleOutline.Background = ThemePalette.AccentBrushWithAlpha(18);
        for (var i = 0; i < _handles.Length; i++)
        {
            if (_handles[i].Tag is Ellipse dot)
            {
                dot.Fill = Brushes.White;
                dot.Stroke = ThemePalette.AccentBrush();
                dot.StrokeThickness = 2;
            }
        }

        _rotateArm.Stroke = ThemePalette.AccentBrushWithAlpha(190);
        if (_rotateDot != null)
        {
            _rotateDot.Fill = Brushes.White;
            _rotateDot.Stroke = ThemePalette.AccentBrush();
            _rotateDot.StrokeThickness = 2;
        }
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

    /// <summary>轨道头选中底色（普通亮灰，不用主题强调色）。</summary>
    private static Color TrackHeaderSelectedColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(95, 120, 120, 130)
        : Color.FromArgb(120, 205, 205, 210);

    /// <summary>轨道头隐藏态底色（更暗，提示该轨被隐藏）。</summary>
    private static Color TrackHeaderHiddenColor() => ThemePalette.IsDarkTheme()
        ? Color.FromArgb(40, 55, 55, 62)
        : Color.FromArgb(45, 150, 150, 155);

    /// <summary>轨道头上的紧凑图标按钮（FluentSystemIcons-Resizable 图标字体，固定宽 19，横排对齐；active 时强调色高亮）。</summary>
    private static Button TrackHeaderButton(string glyph, string tooltip, bool active, Action onClick)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = AppBase.FluentIconsFontFamily,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            // 触摸友好命中尺寸（轨道头行高默认 60，足够容纳）；桌面鼠标同样更易点中。
            Width = 28,
            Height = 24,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (active)
        {
            b.Background = ThemePalette.AccentBrushWithAlpha(160);
            b.Foreground = Brushes.White;
        }

        ToolTip.SetTip(b, tooltip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>删除整个轨道（该轨全部片段），轨道号压缩。锁定轨不可删。</summary>
    private void DeleteTrack(int track)
    {
        if (_project.GetTrackState(track) is { Locked: true })
        {
            _statusText.Text = $"轨道 {track + 1} 已锁定，无法删除。";
            return;
        }

        PushUndo();
        var removed = _project.Clips.RemoveAll(c => c.Track == track);
        _selected = null;
        CompactTracks();
        RefreshTimeline();
        ClearSelection();
        ScheduleSave();
        _statusText.Text = removed > 0
            ? $"已删除轨道 {track + 1}（{removed} 个片段）。"
            : $"轨道 {track + 1} 已删除（无片段）。";
    }

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

    /// <summary>插入位置对应的水平边界 Y（0..trackCount；0 = 顶边，trackCount = 底边，中间 = 两轨之间）。</summary>
    private double BoundaryYOfInsert(int insertPos, int trackCount)
    {
        var acc = 0.0;
        for (var r = 0; r < Math.Min(insertPos, trackCount); r++)
        {
            acc += LaneHeightOf(RowOfTrackInverse(r, trackCount));
        }

        return acc;
    }

    /// <summary>拖拽反馈：insertPosition &gt;= 0 = 在两轨之间插入（显示水平插入指示线），否则高亮目标泳道。
    /// overlap = 目标位置被同轨素材占用，标签变红提示「释放后自动腾位」。</summary>
    private void UpdateDropHighlight(int targetTrack, int trackCount, bool overlap = false, int insertPosition = -1)
    {
        var badge = _dropTrackBadge ??= BuildDropTrackBadge();
        if (badge.Parent != _timelineRoot)
        {
            _timelineRoot.Children.Add(badge);
        }

        if (insertPosition >= 0)
        {
            // 插入模式：显示水平插入指示线（不整体高亮泳道）。
            foreach (var lane in _lanes)
            {
                lane.Background = new SolidColorBrush(LaneFillColor());
            }

            var y = BoundaryYOfInsert(insertPosition, trackCount);
            _insertIndicator.IsVisible = true;
            Canvas.SetTop(_insertIndicator, Math.Max(0, y - 1));
            Canvas.SetLeft(_insertIndicator, 4);
            _insertIndicator.Width = Math.Max(100, _timelineRoot.Width - 8);
            if (badge.Child is TextBlock text)
            {
                text.Text = insertPosition switch
                {
                    0 => "＋ 在顶部插入新轨道",
                    _ when insertPosition >= trackCount => "＋ 在底部插入新轨道",
                    _ => $"＋ 在轨道 {insertPosition + 1} 上方插入"
                };
            }

            badge.Background = ThemePalette.AccentBrushWithAlpha(235);
            Canvas.SetTop(badge, Math.Clamp(y - 18, 2, Math.Max(2, _timelineRoot.Height - 22)));
            Canvas.SetLeft(badge, 8);
            badge.IsVisible = true;
            return;
        }

        // 正常落轨：高亮目标泳道（_lanes 下标 = 视觉行，须由数据轨号换算行号）。
        _insertIndicator.IsVisible = false;
        var row = trackCount > 0 && _lanes.Count > 0
            ? Math.Clamp(RowOfTrack(Math.Clamp(targetTrack, 0, trackCount - 1), trackCount), 0, _lanes.Count - 1)
            : 0;
        for (var i = 0; i < _lanes.Count; i++)
        {
            _lanes[i].Background = i == row
                ? ThemePalette.AccentBrushWithAlpha((byte)(overlap ? 110 : 90))
                : new SolidColorBrush(LaneFillColor());
        }

        if (badge.Child is TextBlock text2)
        {
            text2.Text = overlap ? $"{TrackName(targetTrack, trackCount)}（占用）" : TrackName(targetTrack, trackCount);
        }

        badge.Background = overlap
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23))
            : ThemePalette.AccentBrushWithAlpha(235);
        var top = VisualTopOfTrack(Math.Clamp(targetTrack, 0, trackCount - 1), trackCount) + 2;
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

        _insertIndicator.IsVisible = false;
        if (_dropTrackBadge is { } badge)
        {
            badge.IsVisible = false;
        }

        StopDragAutoScroll();
    }

    /// <summary>构建一个时间轴片段块（绝对定位到泳道画布；支持点击/框选多选、按住拖拽移动多选组、左右边缘拖拽裁剪入/出点）。</summary>
    private Border BuildClipBlock(VideoClip clip)
    {
        var isSelected = _selectedClips.Contains(clip);
        var trackState = _project.GetTrackState(clip.Track);
        var isLocked = trackState is { Locked: true };
        var isHiddenTrack = trackState is { Hidden: true };
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
                    Text = ClipDisplayName(clip) + (isLocked ? " \uE72E" : ""),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150
                },
                durationText
            }
        };
        // 选中时显示左右裁剪手柄（拖左 = 改入点，拖右 = 改出点）。
        // 触摸友好：外层 18px 透明命中区（手指可轻松点到），内层 8px 白色可见条贴块边缘。
        var leftHandle = new Border
        {
            Width = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            IsVisible = isSelected,
            Child = new Border
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                CornerRadius = new CornerRadius(6, 0, 0, 6),
                IsHitTestVisible = false
            }
        };
        var rightHandle = new Border
        {
            Width = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            IsVisible = isSelected,
            Child = new Border
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                IsHitTestVisible = false
            }
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
            Opacity = isHiddenTrack ? 0.45 : 1,
            Child = new Grid { Children = { content, leftHandle, rightHandle } }
        };
        Canvas.SetLeft(block, clip.StartTime * _pxPerSecond);
        Canvas.SetTop(block, 6);
        _blockByClip[clip] = block;

        // 点击选中 + 按下准备拖拽。指针捕获固定在稳定的 _timelineRoot 上——块在拖动中会被浮到根
        // 画布（重挂载会令捕获丢失、拖拽中断，这是“片段不跟手”的根因），根画布从不重建/移动，
        // 因此捕获全程有效；移动/释放/中断统一由 OnTimelinePointerMoved/Released/CancelTimelineDrag 处理。
        // 同一段逻辑也会在「播放头 seek 线盖住片段」时由 _playhead 处理器合成调用（BeginClipDrag），
        // 保证 seek 线不抢片段点击/拖拽。
        block.PointerPressed += (_, e) =>
        {
            // 分割工具下点击片段 = 分割，不选中/不拖拽（由时间轴层 PointerPressed 统一处理）。
            if (_splitTool)
            {
                return;
            }

            if (isLocked)
            {
                _statusText.Text = "该轨道已锁定，无法编辑。";
                return;
            }

            BeginClipDrag(clip, e, e.GetPosition(_timelineRoot));
        };
        // 裁剪手柄按下：记录裁剪状态并阻止冒泡（避免触发块选中/拖拽）。锁定轨禁止裁剪。
        // 同样捕获到稳定的根画布：指针移出块外（向左拖左头 / 向右拖右头）裁剪仍跟手。
        leftHandle.PointerPressed += (_, e) =>
        {
            if (_splitTool || isLocked)
            {
                return;
            }

            _trimState = (clip, false, e.GetPosition(block).X, block, durationText);
            _trimUndoPushed = false;
            e.Handled = true;
        };
        rightHandle.PointerPressed += (_, e) =>
        {
            if (_splitTool || isLocked)
            {
                return;
            }

            _trimState = (clip, true, e.GetPosition(block).X, block, durationText);
            _trimUndoPushed = false;
            e.Handled = true;
        };
        return block;
    }

    /// <summary>片段按下统一入口：选中（多选保持）→ 记录拖拽基准 → 捕获到稳定根画布。
    /// 块自身按下与播放头 seek 线盖住片段时（合成）都走这里，保证「想点片段时 seek 不抢」、
    /// 拖拽始终跟手。rootPos 为 _timelineRoot 坐标。</summary>
    private void BeginClipDrag(VideoClip clip, PointerEventArgs e, Point rootPos)
    {
        // 点击 / 按下片段不移动播放头（seek 指针不“凑热闹”）：要定位播放位置请点标尺或泳道空白处。
        // 多选：点击已选中的片段保持选择（准备拖动整个组）；否则清空并单选。
        if (!_selectedClips.Contains(clip))
        {
            _selectedClips.Clear();
            _selectedClips.Add(clip);
            UpdateAllBlockSelection();
        }

        _selected = clip;
        // 按下点相对块：左缘 = StartTime*px；顶缘在根画布坐标 = 所在轨视觉顶部 + 泳道内 6px 边距。
        // （必须含轨道偏移 VisualTopOfTrack，否则从下方轨道拖起时块会比鼠标高一段、越靠下越偏。）
        var grabX = rootPos.X - clip.StartTime * _pxPerSecond;
        var blockRootTop = VisualTopOfTrack(clip.Track, _project.TrackCount) + 6;
        var grabY = Math.Max(0, rootPos.Y - blockRootTop);
        _dragOffsetX = grabX;
        var origins = new Dictionary<VideoClip, double>();
        var tracks = new Dictionary<VideoClip, int>();
        foreach (var c in _selectedClips)
        {
            origins[c] = c.StartTime;
            tracks[c] = c.Track;
        }

        _moveGroup = (clip, rootPos.X, grabY, origins, tracks);
        _moveUndoPushed = false;
        _dragLogMoves = 0;
        EditorLog($"DRAG 按下 clip={ClipDisplayName(clip)}@{clip.StartTime:0.###}s track={clip.Track} " +
                  $"rootX={rootPos.X:0.#} grabY={grabY:0.#} grabX={grabX:0.#} sel={_selectedClips.Count}");
        // 不做指针捕获：拖拽移动/释放统一由窗口级 PointerMoved/PointerReleased 驱动
        // （见构造函数注册），指针在窗口内任何位置移动片段都必定跟手，不受捕获丢失/重挂载影响。
        UpdateAllBlockSelection();
        FillPropertyPanel();
        UpdateStageHandles();
    }

    /// <summary>命中测试：_timelineRoot 坐标处是否有片段（时间落在片段区间且纵向在片段所在轨道泳道内）。
    /// 供播放头 seek 线按下时判断「用户是想点片段」——有片段就交给片段，不让 seek 抢。</summary>
    private VideoClip? ClipAtTimelinePoint(Point rootPos)
    {
        var trackCount = _project.TrackCount;
        if (trackCount <= 0)
        {
            return null;
        }

        var time = rootPos.X / _pxPerSecond;
        for (var t = trackCount - 1; t >= 0; t--)
        {
            var top = VisualTopOfTrack(t, trackCount);
            var bottom = top + LaneHeightOf(t);
            if (rootPos.Y < top || rootPos.Y >= bottom)
            {
                continue;
            }

            // 该泳道内覆盖该时刻的最上层（同轨起始最晚）片段。
            return _project.Clips
                .Where(c => c.Track == t && time >= c.StartTime && time < c.StartTime + c.Duration)
                .OrderByDescending(c => c.StartTime)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>把当前多选集合的所有块浮到根画布（跨泳道拖动用），保持相对布局。</summary>
    private void FloatSelectedBlocks()
    {
        foreach (var (c, b) in _blockByClip)
        {
            if (!_selectedClips.Contains(c) || b.Parent == _timelineRoot)
            {
                continue;
            }

            if (b.Parent is Panel p)
            {
                p.Children.Remove(b);
            }

            b.ZIndex = 30; // 高于播放头（20），拖动时浮在所有泳道上
            b.Opacity = 0.8; // 半透明：目标泳道的高亮/落点标签在块下仍可见
            _timelineRoot.Children.Add(b);
        }
    }

    /// <summary>根画布指针移动：优先裁剪，其次拖拽移动多选组（整组横向/纵向跟手 + 目标泳道高亮）。
    /// 捕获目标是稳定的根画布，块浮到根画布/重建都不会中断拖拽，片段始终跟手。</summary>
    private void OnTimelinePointerMoved(PointerEventArgs e)
    {
        if (_trimState is { } trim)
        {
            var x = e.GetPosition(trim.Block).X;
            var delta = (x - trim.LastX) / _pxPerSecond;
            _trimState = (trim.Clip, trim.IsOut, x, trim.Block, trim.DurationText);
            // 首次实际裁剪才压撤销（按下不压，避免空拖拽污染栈）。
            if (!_trimUndoPushed)
            {
                PushUndo();
                _trimUndoPushed = true;
            }

            if (_dragLogMoves < 5)
            {
                EditorLog($"TRIM 移动 isOut={trim.IsOut} x={x:0.#} delta={delta:0.###} (总 {_dragLogMoves + 1})");
            }

            ApplyTrim(trim.Clip, trim.IsOut, delta, trim.Block, trim.DurationText);
            _dragLogMoves++;
            return;
        }

        if (_moveGroup is not { } md)
        {
            return;
        }

        // 拖拽基准片段在时间轴里找不到对应块（多半是撤销/工程被整体替换后残留旧引用）
        // → 直接中止本次拖拽，避免“拖了不跟手”。
        if (!_blockByClip.ContainsKey(md.Pressed))
        {
            EditorLog("DRAG 中止：按下片段的时间轴块已不存在（引用失效）");
            _moveGroup = null;
            _moveUndoPushed = false;
            ClearDropHighlight();
            return;
        }

        if (!e.GetCurrentPoint(_timelineRoot).Properties.IsLeftButtonPressed)
        {
            EditorLog("DRAG 移动时左键已松开 → 终止拖拽状态");
            _moveGroup = null;
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

        // 开始拖动时才把整组块浮到根画布（跨泳道需要；纯点击不挪动）。
        if (_selectedClips.Any(c => _blockByClip.TryGetValue(c, out var b) && b.Parent != _timelineRoot))
        {
            FloatSelectedBlocks();
            if (_dragLogMoves == 0)
            {
                EditorLog("DRAG 首次移动 → 浮块到根画布");
            }
        }

        UpdateDragAutoScroll(e.GetPosition(_timelineScroll));
        // 横向：整组相对按下基准平移（时间增量 = 指针位移像素 / 像素每帧；跟手）。
        var timeDelta = (rootPos.X - md.GrabX) / _pxPerSecond;
        var blockTop = Math.Max(0, rootPos.Y - md.GrabY);
        foreach (var (c, orig) in md.Origins)
        {
            var moved = orig + timeDelta;
            c.StartTime = _snapEnabled ? Math.Max(0, SnapTime(moved)) : Math.Max(0, moved);
            if (_blockByClip.TryGetValue(c, out var b))
            {
                Canvas.SetLeft(b, c.StartTime * _pxPerSecond);
                Canvas.SetTop(b, blockTop);
            }
        }

        if (_dragLogMoves < 5)
        {
            var blockText = _blockByClip.TryGetValue(md.Pressed, out var pressedBlock)
                ? $"{Canvas.GetLeft(pressedBlock):0.#}px"
                : "无块!? (块丢失)";
            EditorLog($"DRAG 移动#{_dragLogMoves + 1} rootX={rootPos.X:0.#} rootY={rootPos.Y:0.#} " +
                      $"dx={(rootPos.X - md.GrabX):0.#}px → timeDelta={timeDelta:0.###}s → " +
                      $"start={md.Pressed.StartTime:0.###}s blockLeft={blockText}");
        }

        _dragLogMoves++;
        // 落点按指针 Y 判定：贴近轨界 / 轨道区外顶部底部 = 提示「新建轨道」（整组来自同一数据轨时）。
        var trackCount = _project.TrackCount;
        var (targetTrack, insertPos) = ResolveDropTarget(rootPos.Y, trackCount);
        var canInsertNewTrack = insertPos >= 0 && md.Tracks.Values.Distinct().Count() <= 1;
        UpdateDropHighlight(targetTrack, trackCount, false, canInsertNewTrack ? insertPos : -1);
    }

    /// <summary>根画布指针释放：完成裁剪（刷新并保存）或完成整组落轨。</summary>
    private void OnTimelinePointerReleased(PointerReleasedEventArgs e)
    {
        if (_trimState is { } t)
        {
            _trimState = null;
            _trimUndoPushed = false;
            StopDragAutoScroll();
            e.Pointer.Capture(null);
            RefreshTimeline();
            FillPropertyPanel();
            ScheduleSave();
            return;
        }

        if (_moveGroup is not { } md)
        {
            return;
        }

        _moveGroup = null;
        _moveUndoPushed = false;
        // 记录块是否已浮到根画布（发生过实际拖动）；先清状态再解绑，避免捕获丢失兜底重建。
        var wasFloating = _selectedClips.Any(c => _blockByClip.TryGetValue(c, out var b) && b.Parent == _timelineRoot);
        EditorLog($"DRAG 释放 wasFloating={wasFloating} 移动日志 {_dragLogMoves} 条");
        StopDragAutoScroll();
        e.Pointer.Capture(null);
        if (wasFloating)
        {
            // 释放：整组吸附 → 解析落点。贴近轨道边界 / 轨道区外顶部底部 = 建立新轨道
            // （整组来自同一数据轨时最直观：把这“一整行”挪到新建的空轨上）。
            var rootPos = e.GetPosition(_timelineRoot);
            var trackCount = _project.TrackCount;
            var (targetTrack, insertPos) = ResolveDropTarget(rootPos.Y, trackCount);
            var grabTrack = md.Tracks[md.Pressed];
            var singleSourceRow = md.Tracks.Values.Distinct().Count() <= 1;
            if (insertPos >= 0 && singleSourceRow)
            {
                // 在视觉位置 insertPos 插入一条新空轨：位于插入点上方的既有轨道数据轨号 +1
                // 为新轨腾位（组内片段先排除，稍后统一落到新轨上）。
                var n = trackCount;
                foreach (var c in _project.Clips)
                {
                    if (md.Tracks.ContainsKey(c) || c.Track <= n - 1 - insertPos)
                    {
                        continue;
                    }

                    c.Track++;
                }

                var newTrack = n - insertPos;
                foreach (var (c, _) in md.Tracks)
                {
                    c.Track = newTrack;
                    if (_snapEnabled)
                    {
                        c.StartTime = SnapTime(c.StartTime);
                    }

                    if (!FitsOnTrack(c.Track, c.StartTime, c.Duration, c))
                    {
                        c.StartTime = FitToTrack(c, c.StartTime, c.Track);
                    }
                }

                EditorLog($"组拖到轨道边界/外部 y={rootPos.Y:0.#} → 新建轨道（视觉位置 {insertPos}，新数据轨 {newTrack}） n={md.Origins.Count} 条");
            }
            else
            {
                // 落到既有轨道：整组相对偏移保持（多数据轨的组或多轨组落中段走这里）。
                var trackDelta = Math.Clamp(targetTrack, 0, 32) - grabTrack;
                foreach (var (c, origTrack) in md.Tracks)
                {
                    c.Track = Math.Clamp(origTrack + trackDelta, 0, 32);
                    if (_snapEnabled)
                    {
                        c.StartTime = SnapTime(c.StartTime);
                    }

                    if (!FitsOnTrack(c.Track, c.StartTime, c.Duration, c))
                    {
                        c.StartTime = FitToTrack(c, c.StartTime, c.Track);
                    }
                }

                EditorLog($"组落轨 y={rootPos.Y:0.#} → track={targetTrack} delta={trackDelta} n={md.Origins.Count}");
            }
        }

        ClearDropHighlight();
        // 空轨自动删除 + 轨道号压缩为连续；随后刷新并保存（同时清掉临时挂到根画布的块）。
        CompactTracks();
        RefreshTimeline();
        FillPropertyPanel();
        ScheduleSave();
    }

    /// <summary>拖拽/裁剪意外中断（捕获丢失等）：清状态并重建时间轴把浮动块收回归位。</summary>
    private void CancelTimelineDrag()
    {
        if (_trimState == null && _moveGroup == null)
        {
            return;
        }

        EditorLog("DRAG 取消（PointerCaptureLost）");
        _trimState = null;
        _moveGroup = null;
        _trimUndoPushed = false;
        _moveUndoPushed = false;
        StopDragAutoScroll();
        ClearDropHighlight();
        RefreshTimeline();
        FillPropertyPanel();
    }

    /// <summary>更新所有片段块的选中样式（不重建整条时间轴，避免打断拖拽/框选）。</summary>
    private void UpdateAllBlockSelection()
    {
        foreach (var (c, block) in _blockByClip)
        {
            var selected = _selectedClips.Contains(c);
            block.Background = selected
                ? ThemePalette.AccentBrushWithAlpha(170)
                : new SolidColorBrush(c.Kind == "Filter" ? FilterBlockColor() : UnselectedBlockColor());
            block.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            if (block.Child is Grid g && g.Children.Count >= 3)
            {
                g.Children[1].IsVisible = selected; // 左裁剪手柄
                g.Children[2].IsVisible = selected; // 右裁剪手柄
            }
        }
    }

    /// <summary>直接更新单个片段块的选中样式（不重建整条时间轴，避免打断拖拽）。</summary>
    private void ApplyBlockSelected(Border block, Border leftHandle, Border rightHandle, VideoClip clip)
    {
        var selected = _selectedClips.Contains(clip);
        block.Background = selected
            ? ThemePalette.AccentBrushWithAlpha(170)
            : new SolidColorBrush(clip.Kind == "Filter" ? FilterBlockColor() : UnselectedBlockColor());
        block.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
        leftHandle.IsVisible = selected;
        rightHandle.IsVisible = selected;
    }

    /// <summary>拖拽裁剪：按拖动秒数增量调整入/出点，即时更新块宽与时长文本（不重建，避免丢失拖拽状态）。
    /// 右头：只改出点（左缘不动，右缘跟手）。左头：入点与轨道起点同步移动（右缘 = StartTime+Duration
    /// 保持不动，左缘跟手向右压缩 / 向左回补素材），并受同轨前一素材与素材开头的钳制。
    /// 入/出点被限制在同轨相邻素材边界内（防拉长堆叠）。</summary>
    private void ApplyTrim(VideoClip clip, bool isOut, double deltaSeconds, Border block, TextBlock durationText)
    {
        var (_, outLimit) = TrimBounds(clip);
        if (isOut)
        {
            clip.OutPoint = Math.Clamp(clip.OutPoint + deltaSeconds, clip.InPoint + 0.1, outLimit);
        }
        else
        {
            // 左头：右缘保持不动 → 入点增 delta 时起点也增 delta（StartTime + Duration 恒定）。
            // 下限 = 素材开头(0) 且 新起点不越过同轨前一素材末尾(prevEnd)。
            var others = _project.Clips.Where(c => c.Track == clip.Track && !ReferenceEquals(c, clip)).ToList();
            var prevEnd = others.Where(c => c.StartTime + c.Duration <= clip.StartTime + 0.01)
                .Select(c => c.StartTime + c.Duration).DefaultIfEmpty(0).Max();
            var minIn = Math.Max(0, clip.InPoint + prevEnd - clip.StartTime);
            var newIn = Math.Clamp(clip.InPoint + deltaSeconds, minIn, clip.OutPoint - 0.1);
            var applied = newIn - clip.InPoint;
            clip.InPoint = newIn;
            clip.StartTime = Math.Max(0, clip.StartTime + applied);
            Canvas.SetLeft(block, clip.StartTime * _pxPerSecond);
        }

        block.Width = Math.Max(30, clip.Duration * _pxPerSecond);
        durationText.Text = $"{clip.StartTime:0.#}s · {clip.Duration:0.#}s";
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

    /// <summary>
    /// 把泳道区内的纵向坐标解析为落点：返回（数据轨号, 插入位置）。
    /// insertPosition = -1 = 直接落到该轨道；0..trackCount = 在视觉位置 p 插入新轨
    /// （0 = 最顶层上方，trackCount = 最底层下方，中间 = 两轨之间）。
    /// 判定碰撞体积较大：每轨顶部的判定带为 <see cref="DropBoundaryThreshold"/> 像素
    /// （贴近某轨顶部即视为在该轨上方新建），轨道区上方空白（y&lt;0）与最底轨底边
    /// 及更下方空白都视为新建轨道——因此“碰到轨道附近（含轨道外）区域”即可新建。
    /// </summary>
    private (int Track, int InsertPosition) ResolveDropTarget(double y, int trackCount)
    {
        if (trackCount <= 0)
        {
            // 无轨道：任意纵向位置都建成第一条新轨。
            return (0, 0);
        }

        var acc = 0.0;
        for (var row = 0; row < trackCount; row++)
        {
            var t = RowOfTrackInverse(row, trackCount);
            var h = LaneHeightOf(t);
            // 本轨顶部判定带（含轨道区上方空白，y<0 也命中这里 → 顶部新建）：
            // 前一轨底部的这一小段 + 本轨顶部这一小段都算「两轨之间新建」。
            if (y < acc + DropBoundaryThreshold)
            {
                return (t, row);
            }

            // 轨道中段 → 直接落到该轨。
            if (y <= acc + h - DropBoundaryThreshold)
            {
                return (t, -1);
            }

            acc += h;
        }

        // 超出底部（含最底轨底部判定带与更下方空白）：底部新建。
        return (0, trackCount);
    }

    /// <summary>新建轨道判定带的半带宽（像素）：贴近轨顶 / 轨外的这个范围内都视为“新建轨道”。</summary>
    private const double DropBoundaryThreshold = 14;

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

    /// <summary>框选：返回与矩形（时间轴内容坐标）相交的所有片段（含锁定轨，只读选中）。</summary>
    private List<VideoClip> MarqueeSelect(Point a, Point b)
    {
        var x1 = Math.Min(a.X, b.X);
        var y1 = Math.Min(a.Y, b.Y);
        var x2 = Math.Max(a.X, b.X);
        var y2 = Math.Max(a.Y, b.Y);
        var trackCount = _project.TrackCount;
        var result = new List<VideoClip>();
        foreach (var clip in _project.Clips)
        {
            var cx = clip.StartTime * _pxPerSecond;
            var cy = VisualTopOfTrack(clip.Track, trackCount);
            var cw = Math.Max(6, clip.Duration * _pxPerSecond);
            var ch = LaneHeightOf(clip.Track);
            if (cx < x2 && cx + cw > x1 && cy < y2 && cy + ch > y1)
            {
                result.Add(clip);
            }
        }

        return result;
    }

    /// <summary>拖拽调高：写入每轨高度并同步泳道/头部/总高（拖动中不重建时间轴，避免打断捕获）。
    /// 注意 _lanes 按下标 = 视觉行，须由数据轨号换算行号。</summary>
    private void SetLaneHeight(int track, double h)
    {
        _laneHeights[track] = h;
        var row = RowOfTrack(track, _project.TrackCount);
        if (row >= 0 && row < _lanes.Count)
        {
            _lanes[row].Height = h;
            if (_lanes[row].Child is Canvas canvas)
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

        _timelineRoot.Height = lanesHeight;
        // 滚动容器高度同步（轨道头/泳道两列锁定同一高度，保证逐行对齐）。
        if (_timelineContent != null)
        {
            _timelineContent.Height = lanesHeight;
        }
    }

    /// <summary>时间轴泳道放置处理：素材/形状 → 新增片段；clip:n → 移动既有片段；贴近轨道边界 → 插入新轨。</summary>
    private void HandleTimelineDrop(DragEventArgs e)
    {
        StopDragAutoScroll(); // 系统拖放结束：先停自动滚动（否则 Drop 后仍按最后指针位置滚动）。
        if (!e.Data.Contains(DataFormats.Text))
        {
            return;
        }

        var text = e.Data.Get(DataFormats.Text)?.ToString() ?? "";
        PushUndo();
        // 用时间轴根坐标计算落点（0 = 时间轴开头）；拖动既有片段时减去按下点偏移，让块跟手。
        var isMove = text.StartsWith("clip:", StringComparison.Ordinal);
        var isFilter = text.StartsWith("filter:", StringComparison.Ordinal);
        var pointer = e.GetPosition(_timelineRoot);
        var rawX = pointer.X - (isMove ? _dragOffsetX : 0);
        var rawStart = Math.Max(0, rawX / _pxPerSecond);
        var startTime = _snapEnabled ? SnapTime(rawStart) : rawStart;
        var trackCount = _project.TrackCount;
        var (dropTrack, insertPos) = ResolveDropTarget(pointer.Y, trackCount);

        VideoClip? clip = null;
        if (isMove &&
            int.TryParse(text.AsSpan(5), out var index) &&
            index >= 0 && index < _project.Clips.Count)
        {
            // 移动既有片段到该轨道/位置。
            clip = _project.Clips[index];
        }
        else if (text.StartsWith("shape:", StringComparison.Ordinal))
        {
            // 形状库拖入：在目标轨道/位置新增形状覆盖层片段（默认 1:1 正方形，边长 = 输出画布短边 60%）。
            var outW = Math.Max(1, _project.OutputWidth);
            var outH = Math.Max(1, _project.OutputHeight);
            var shapeSide = Math.Max(24, Math.Min(outW, outH) * 0.6);
            clip = new VideoClip
            {
                Kind = "Shape",
                Shape = text["shape:".Length..],
                Color = "#FFFFEB3B",
                Track = 0,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = 5,
                Scale = 1,
                ScaleX = shapeSide / outW,
                ScaleY = shapeSide / outH
            };
            _project.Clips.Add(clip);
        }
        else if (text.StartsWith("textstyle:", StringComparison.Ordinal) &&
                 int.TryParse(text.AsSpan("textstyle:".Length), out var styleIndex) &&
                 styleIndex >= 0 && styleIndex < TextStylePresets.Length)
        {
            // 文字样式预设拖入：在目标轨道/位置按预设样式新增文本覆盖层片段（内容中性，可编辑）。
            var preset = TextStylePresets[styleIndex];
            clip = new VideoClip
            {
                Kind = "Text",
                Shape = "Rect",
                Text = "文本",
                Color = "#FFFFEB3B",
                Track = 0,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = 5,
                Scale = 1,
                ScaleX = 1,
                ScaleY = 1
            };
            ApplyTextStylePreset(clip, preset);
            _project.Clips.Add(clip);
        }
        else if (isFilter)
        {
            // 滤镜库拖入：始终放到最高的新轨道（滤镜作用于其下方所有画面）。
            clip = new VideoClip
            {
                Kind = "Filter",
                Filter = text["filter:".Length..],
                Track = _project.TrackCount,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = 5
            };
            _project.Clips.Add(clip);
        }
        else if (_assets.Contains(text))
        {
            // 素材拖入：目标轨道/位置新增片段（同轨不重叠，自动放到最近可用位置）。
            // 图片素材建 Kind=Image 覆盖层（固定 5 秒）；视频按素材时长建普通片段。
            var isImage = VideoTranscoder.IsImageFile(text);
            var duration = isImage ? 5 : GetAssetDuration(text);
            clip = new VideoClip
            {
                Kind = isImage ? "Image" : "Video",
                SourcePath = text,
                Track = 0,
                StartTime = startTime,
                InPoint = 0,
                OutPoint = isImage ? duration : duration > 0.5 ? duration : 10
            };
            _project.Clips.Add(clip);
        }

        if (clip != null)
        {
            if (isFilter)
            {
                // 滤镜始终放最高新轨道，不参与插入。
                clip.Track = _project.TrackCount;
            }
            else if (insertPos >= 0)
            {
                // 在两轨之间插入新轨（视觉位置 insertPos），把片段放上新轨道：
                // 视觉行在插入点上方（数据轨号 &gt; n-1-insertPos）的轨道整体下移一格。
                var n = _project.TrackCount;
                foreach (var c in _project.Clips)
                {
                    if (!ReferenceEquals(c, clip) && c.Track > n - 1 - insertPos)
                    {
                        c.Track++;
                    }
                }

                clip.Track = n - insertPos;
            }
            else
            {
                clip.Track = Math.Max(0, dropTrack);
            }

            clip.StartTime = startTime;
            // 同轨不允许堆叠：落点被占时自动挪到最近空位。
            var overlapped = false;
            if (!FitsOnTrack(clip.Track, clip.StartTime, clip.Duration, clip))
            {
                clip.StartTime = FitToTrack(clip, clip.StartTime, clip.Track);
                overlapped = true;
                _statusText.Text = "目标位置与同轨素材重叠，已自动放到最近空位（素材不会堆叠）。";
            }

            _selected = clip;
            if (isFilter)
            {
                // 滤镜只作用于更低轨道的画面；下方没有视频片段时提前说明（否则"加了特效没反应"）。
                var affects = _project.Clips.Any(c => c.Kind == "Video" && c.Track < clip.Track &&
                                                      clip.StartTime < c.StartTime + c.Duration &&
                                                      clip.StartTime + clip.Duration > c.StartTime);
                _statusText.Text = affects
                    ? $"已添加「{FilterName(clip.Filter)}」滤镜（作用于其下方轨道 {clip.StartTime:0.#}s 起的画面）。"
                    : "提示：滤镜只作用于更低轨道的画面——先把视频片段放到更低的轨道，滤镜才会生效。";
            }
            else if (!isMove && !overlapped)
            {
                // 素材/形状/文字拖入成功：给出简明提示（不覆盖上面的重叠提示）。
                _statusText.Text = clip.Kind switch
                {
                    "Shape" => $"已添加「{ShapeName(clip.Shape)}」形状（右侧可改颜色/变换）。",
                    "Text" => "已添加文本覆盖层（右侧可编辑文字与颜色）。",
                    _ => $"已添加素材（时长 {clip.Duration:0.#}s）。"
                };
            }
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

        if (_project.GetTrackState(_selected.Track) is { Locked: true })
        {
            _statusText.Text = "该轨道已锁定，无法移动。";
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

        if (_project.GetTrackState(_selected.Track) is { Locked: true })
        {
            _statusText.Text = "该轨道已锁定，无法移动。";
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
        if (_selectedClips.Count == 0 && _selected != null)
        {
            _selectedClips.Add(_selected);
        }

        if (_selectedClips.Count == 0)
        {
            return;
        }

        // 锁定轨片段不可删除（其余照删）。
        var locked = _selectedClips.Count(c => _project.GetTrackState(c.Track) is { Locked: true });
        var deletable = _selectedClips.Where(c => _project.GetTrackState(c.Track) is not { Locked: true }).ToList();
        if (deletable.Count == 0)
        {
            _statusText.Text = "选中片段所在轨道已锁定，无法删除。";
            return;
        }

        PushUndo();
        foreach (var c in deletable)
        {
            _project.Clips.Remove(c);
        }

        _selected = null;
        _selectedClips.Clear();
        // 删片段可能留下空轨：自动删除并压缩轨道号。
        CompactTracks();
        RefreshTimeline();
        ClearSelection();
        ScheduleSave();
        _statusText.Text = locked > 0
            ? $"已删除 {deletable.Count} 个片段（跳过 {locked} 个锁定轨片段）。"
            : $"已删除 {deletable.Count} 个片段。";
    }

    /// <summary>刀片工具：在播放头位置切割（有选中片段时只切选中的，否则切所有覆盖该时刻的片段）。</summary>
    private void CutAtPlayhead()
    {
        var cut = CutClipsAt(_playheadTime);
        _statusText.Text = CutStatusText(_playheadTime, cut);
    }

    /// <summary>切割后的状态文案：有选中时按“选中片段”描述，无选中按“N 个片段”描述。</summary>
    private string CutStatusText(double t, int cut)
    {
        var hadSelection = _selected != null || _selectedClips.Count > 0;
        if (cut == 0)
        {
            return hadSelection
                ? $"{t:0.#}s 处选中片段不可分割（不在选中片段内）。"
                : $"{t:0.#}s 处没有片段，无法切割。";
        }

        return hadSelection
            ? $"已在 {t:0.#}s 切割选中片段。"
            : $"已在 {t:0.#}s 切割 {cut} 个片段。";
    }

    /// <summary>在指定时间切割片段，返回切割数量。有选中片段时只切选中的；无选中时切割所有覆盖该时刻的片段。</summary>
    private int CutClipsAt(double time)
    {
        // 主选中同步进多选集合（DeleteSelectedClip 同款）；有选中 = 只切选中片段。
        if (_selectedClips.Count == 0 && _selected != null)
        {
            _selectedClips.Add(_selected);
        }

        var restrictToSelection = _selectedClips.Count > 0;
        var toAdd = new List<VideoClip>();
        var toRemove = new List<VideoClip>();
        foreach (var clip in _project.Clips)
        {
            if (restrictToSelection && !_selectedClips.Contains(clip))
            {
                continue; // 只切割选中片段，其余片段保持不动。
            }

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
        // 选中跟随左半段：主选中与多选集合同步替换，切割后继续选中，便于继续编辑/再次切割。
        for (var i = 0; i < toRemove.Count; i++)
        {
            if (_selectedClips.Remove(toRemove[i]))
            {
                _selectedClips.Add(toAdd[i * 2]);
            }

            if (ReferenceEquals(_selected, toRemove[i]))
            {
                _selected = toAdd[i * 2]; // 选中左段。
            }
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
        _selectedClips.Clear();
        FillPropertyPanel();
        UpdateStageHandles();
    }

    /// <summary>设置主选中（单选模式：同步多选集合）。</summary>
    private void SelectClip(VideoClip clip)
    {
        _selected = clip;
        _selectedClips.Clear();
        _selectedClips.Add(clip);
    }

    // ============ 属性 ============

    private void FillPropertyPanel()
    {
        _updatingUi = true;
        try
        {
            var clip = _selected;
            var has = clip != null;
            _inSpin.DoubleValue = clip?.InPoint ?? 0;
            _outSpin.DoubleValue = clip?.OutPoint ?? 10;
            // 变换按像素显示：由画布适配基准 + 归一化数值换算回填（X/Y 为画布中心坐标）。
            if (clip != null)
            {
                SetTransformPxFromClip(clip);
            }

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

            _inspectorSegmented.IsEnabled = has;

            // 图片/文本/形状覆盖层编辑区：仅这三类显示（覆盖层分段）。
            var isOverlay = clip is { Kind: "Text" or "Shape" or "Image" };
            _segmentButtons["overlay"].IsVisible = isOverlay;
            if (isOverlay)
            {
                _overlayText.Text = clip!.Text;
                _overlayColor.Color = ParseHexColor(clip.Color);
                _overlayShape.SelectedItem = clip.Shape;
                _overlayText.IsEnabled = clip.Kind == "Text";
                _overlayShape.IsEnabled = clip.Kind == "Shape";
                _overlayColor.IsEnabled = clip.Kind != "Image";
                _strokeWidthSpin.DoubleValue = clip.StrokeWidth;
                _strokeColor.Color = ParseHexColor(clip.StrokeColor);
                // 文本样式：字体下拉（按系统字体名回填，空 = 微软雅黑）、字号系数、加粗。
                var wantFont = string.IsNullOrWhiteSpace(clip.TextFontFamily) ? "Microsoft YaHei" : clip.TextFontFamily;
                var selIdx = -1;
                for (var fi = 0; fi < _systemFonts.Length; fi++)
                {
                    if (string.Equals(_systemFonts[fi].Name, wantFont, StringComparison.OrdinalIgnoreCase))
                    {
                        selIdx = fi;
                        break;
                    }
                }

                _overlayFontBox.SelectedIndex = selIdx;
                // 字号按 px 显示：内部系数 × 输出画布高（生成/渲染都以画布高为基准）。
                _textSizeSpin.DoubleValue = Math.Max(1, Math.Round(clip.TextFontSize * Math.Max(1, _project.OutputHeight)));
                _textBoldCheck.IsChecked = clip.TextBold;
                // 各覆盖层行按片段类型显隐（图片只留通用变换）。
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

                // 描边文字/形状都有（图片无）。
                if (_strokeWidthRow is { } strokeWidthRow)
                {
                    strokeWidthRow.IsVisible = clip.Kind is "Shape" or "Text";
                }

                if (_strokeColorRow is { } strokeColorRow)
                {
                    strokeColorRow.IsVisible = clip.Kind is "Shape" or "Text";
                }

                // 字体 / 字号 / 加粗只有文本有。
                var isText = clip.Kind == "Text";
                _overlayFontBox.IsEnabled = isText;
                _textSizeSpin.IsEnabled = isText;
                _textBoldCheck.IsEnabled = isText;
                if (_overlayFontRow is { } fontRow)
                {
                    fontRow.IsVisible = isText;
                }

                if (_textSizeRow is { } sizeRow)
                {
                    sizeRow.IsVisible = isText;
                }

                if (_textBoldRow is { } boldRow)
                {
                    boldRow.IsVisible = isText;
                }
            }

            // 滤镜编辑区：仅 Kind=Filter 显示（滤镜分段）。滤镜没有变换，隐藏变换分段。
            var isFilter = clip is { Kind: "Filter" };
            _segmentButtons["filter"].IsVisible = isFilter;
            _segmentButtons["transform"].IsVisible = !isFilter;
            if (isFilter)
            {
                var fi = Array.FindIndex(FilterDefs, d => d.Key == clip!.Filter);
                _filterCombo.SelectedIndex = fi < 0 ? 0 : fi;
                _filterIntensitySlider.Value = Math.Clamp(clip!.FilterIntensity, 0, 1);
            }

            // 当前分段被隐藏（片段类型变化）时回退到变换；片段变化时自动切到对应分段。
            var pageVisible = _currentInspectorPage switch
            {
                "overlay" => isOverlay,
                "filter" => isFilter,
                "transform" => !isFilter,
                _ => true
            };
            if (!pageVisible)
            {
                SelectInspectorPage(isFilter ? "filter" : "transform");
            }

            if (!ReferenceEquals(_lastInspectorClip, clip))
            {
                _lastInspectorClip = clip;
                SelectInspectorPage(isOverlay ? "overlay" : isFilter ? "filter" : "transform");
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

        if (_project.GetTrackState(_selected.Track) is { Locked: true })
        {
            FillPropertyPanel(); // 锁定轨：回读原值，禁止修改。
            _statusText.Text = "该轨道已锁定，无法修改属性。";
            return;
        }

        // 数值框连续调整合并为一步撤销。
        PushUndo(true);
        var clip = _selected;
        // 入/出点限制在同轨相邻素材边界内（防堆叠）。
        var (inLimit, outLimit) = TrimBounds(clip);
        clip.InPoint = Math.Clamp(Math.Max(0, _inSpin.DoubleValue), Math.Max(0, inLimit), clip.OutPoint - 0.1);
        clip.OutPoint = Math.Clamp(Math.Max(clip.InPoint + 0.1, _outSpin.DoubleValue), clip.InPoint + 0.1, outLimit);
        // 像素 → 归一化：以选中片段的「画布适配基准」与内部基准缩放换算（基准缩放保持，只调 ScaleX/Y）。
        var (bw, bh) = _pxBaseReady ? _pxBaseCache : BaseFitPxFor(clip);
        var canvasW = Math.Max(1, _project.OutputWidth);
        var canvasH = Math.Max(1, _project.OutputHeight);
        var wPx = Math.Max(1, _pxWSpin.DoubleValue);
        var hPx = Math.Max(1, _pxHSpin.DoubleValue);
        var baseScale = Math.Max(0.05, clip.Scale);
        clip.Scale = baseScale;
        clip.ScaleX = Math.Max(0.05, wPx / Math.Max(1, bw * baseScale));
        clip.ScaleY = Math.Max(0.05, hPx / Math.Max(1, bh * baseScale));
        clip.OffsetX = Math.Clamp((_pxXSpin.DoubleValue - canvasW / 2.0) / canvasW, -5, 5);
        clip.OffsetY = Math.Clamp((_pxYSpin.DoubleValue - canvasH / 2.0) / canvasH, -5, 5);
        clip.Rotation = _rotationSpin.DoubleValue;
        clip.Opacity = Math.Clamp(_opacitySpin.DoubleValue, 0, 1);
        clip.CropLeft = Math.Clamp(_cropLSpin.DoubleValue, 0, 1);
        clip.CropTop = Math.Clamp(_cropTSpin.DoubleValue, 0, 1);
        clip.CropRight = Math.Clamp(_cropRSpin.DoubleValue, 0, 1);
        clip.CropBottom = Math.Clamp(_cropBSpin.DoubleValue, 0, 1);
        RefreshTimeline();
        ScheduleSave();
        // 舞台实时预览：只要该轨道已有画面（含暂停/未播放），立即应用变换并刷新八向手柄。
        // （之前用 _playing && _player 条件，暂停时调整缩放/偏移完全不更新舞台。）
        if (clip.Track < _stageLayers.Count && _stageLayers[clip.Track].Image.IsVisible)
        {
            ApplyTransform(_stageLayers[clip.Track], clip);
            UpdateStageHandles();
        }
    }

    /// <summary>
    /// 计算片段的「画布适配基准」（输出画布像素；缩放=1 时铺满/适配的矩形尺寸）。
    /// 视频按真实画面比例在画布内 letterbox 适配；文本/形状/图片覆盖层以整幅输出画布为基准。
    /// </summary>
    private (double BaseW, double BaseH) BaseFitPxFor(VideoClip clip)
    {
        var Wc = Math.Max(1, _project.OutputWidth);
        var Hc = Math.Max(1, _project.OutputHeight);
        if (clip.Kind == "Video")
        {
            var aspect = SourceLayerAspect(clip);
            var canvasAspect = Wc / Hc;
            if (aspect is { } a && a > 0)
            {
                return a >= canvasAspect
                    ? (Wc, Wc / a)
                    : (Hc * a, Hc);
            }
        }

        // 覆盖层（文本/形状/图片）及视频尚未解码时：以整幅画布为适配基准。
        return (Wc, Hc);
    }

    /// <summary>取选中视频片段当前舞台图层的真实画面比例（尚未解码时返回 null）。</summary>
    private double? SourceLayerAspect(VideoClip clip)
    {
        if (clip.Track >= 0 && clip.Track < _stageLayers.Count &&
            _stageLayers[clip.Track].Bitmap is { } bm &&
            bm.PixelSize.Width > 0 && bm.PixelSize.Height > 0)
        {
            return bm.PixelSize.Width / (double)bm.PixelSize.Height;
        }

        return null;
    }

    /// <summary>把片段的归一化变换换算成像素并回填检查器（同时缓存适配基准）。</summary>
    private void SetTransformPxFromClip(VideoClip clip)
    {
        _pxBaseCache = BaseFitPxFor(clip);
        _pxBaseReady = true;
        var Wc = Math.Max(1, _project.OutputWidth);
        var Hc = Math.Max(1, _project.OutputHeight);
        _pxWSpin.DoubleValue = Math.Max(1, Math.Round(_pxBaseCache.BaseW * clip.Scale * clip.ScaleX));
        _pxHSpin.DoubleValue = Math.Max(1, Math.Round(_pxBaseCache.BaseH * clip.Scale * clip.ScaleY));
        _pxXSpin.DoubleValue = Math.Round(Wc / 2.0 + clip.OffsetX * Wc);
        _pxYSpin.DoubleValue = Math.Round(Hc / 2.0 + clip.OffsetY * Hc);
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
                    // 拿到真实画面比例后，用真实适配基准回填 px 数值（此前未解码时用画布兜底）。
                    if (ReferenceEquals(_selected, clip))
                    {
                        SetTransformPxFromClip(clip);
                    }
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
        FilterUtils.ApplyInPlace(_filterBuffer, frame.Width, frame.Height, filter!.Filter, filter.FilterIntensity);
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

        // 保留当前播放头（seek 位置）：新建播放器从该处开始，而非强制回 0——
        // 这样按空格可以在当前 seek 处继续播放。
        _playheadTime = Math.Clamp(_playheadTime, 0, Math.Max(0, _project.Duration));
        PositionPlayheadLine();
        PositionPlayheadHead();
        _timeText.Text = FormatTime(_playheadTime);
        _player = new VideoProjectPlayer(_project, PreviewMaxDimension, _targetFps, OnPreviewFrame);
        // 某轨道不再有活跃片段（片段被删/播完/轨道隐藏）时隐藏该轨图层：
        // 否则最后一帧会一直冻结在舞台上，看起来像「删掉的片段还在预览里」。
        _player.TrackCleared += track => Dispatcher.UIThread.Post(() => HideStageTrack(track));
        _player.Start();
        if (_playheadTime > 0)
        {
            _player.Seek(_playheadTime);
        }

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

    /// <summary>切换编辑器窗口全屏（舞台右下播放条旁的全屏按钮）；退出时恢复普通窗口。</summary>
    private void ToggleStageFullscreen()
    {
        var target = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        WindowState = target;
        if (_fullscreenButton.Content is IconText fsIcon)
        {
            // 全屏最大化 / 全屏还原（退出）图标随状态切换。
            fsIcon.Glyph = target == WindowState.FullScreen ? "\uE8D2" : "\uE8D0";
        }
    }

    private void OnPreviewFrame(VideoFrame frame, VideoClip clip, int track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UpdateStageLayer(track, ApplyActiveFilter(frame, clip, _player?.CurrentTime ?? _playheadTime), clip);
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

    /// <summary>隐藏指定轨道的舞台预览图层（该轨当前时刻无活跃片段：片段已删/播完/轨道隐藏）。</summary>
    private void HideStageTrack(int track)
    {
        if (track < 0 || track >= _stageLayers.Count)
        {
            return;
        }

        var layer = _stageLayers[track];
        layer.Image.IsVisible = false;
        layer.Image.Source = null;
        UpdateStageHandles();
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

        // 旋转臂（选中框顶部中心向上）+ 旋转手柄：外观与八向缩放手柄一致（白底圆点 + 强调色描边），
        // 旋转臂用强调色细线把圆点接到选中框顶部中心——不再用突兀的紫色大圆点 + 白描边。
        // 位于八向手柄「北」上方：选中框顶部中心上方约 34px。
        _rotateArm = new Avalonia.Controls.Shapes.Line
        {
            Stroke = ThemePalette.AccentBrushWithAlpha(190),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            IsVisible = false
        };
        _stageHandleOverlay.Children.Add(_rotateArm);
        _rotateDot = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = Brushes.White,
            Stroke = ThemePalette.AccentBrush(),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _rotateHandle = new Border
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
            Child = _rotateDot
        };
        _rotateHandle.PointerPressed += RotateHandleOnPointerPressed;
        _rotateHandle.PointerMoved += RotateHandleOnPointerMoved;
        _rotateHandle.PointerReleased += RotateHandleOnPointerReleased;
        _rotateHandle.PointerCaptureLost += (_, _) =>
        {
            _rotating = false;
            // 捕获意外丢失也恢复默认样式（与八向缩放手柄一致）。
            _rotateDot.Fill = Brushes.White;
            _rotateDot.Stroke = ThemePalette.AccentBrush();
            _rotateDot.StrokeThickness = 2;
        };
        _stageHandleOverlay.Children.Add(_rotateHandle);

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

    /// <summary>按选中片段在舞台上的显示矩形（原比例基准 + 缩放/偏移；旋转时返回旋转后的轴对齐 AABB，
    /// 选框/八向手柄/旋转手柄都跟随该框）摆放手柄。</summary>
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
            HideRotateHandle();
            return;
        }

        var rect = GetSelectedDisplayRect(clip);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _stageHandleOverlay.IsVisible = false;
            HideRotateHandle();
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

        // 旋转臂与旋转手柄：位于八向手柄「北」的上方（顶部中心上 34px）。
        var top = rect.Top;
        var cx = rect.Center.X;
        _rotateArm.IsVisible = true;
        _rotateArm.StartPoint = new Point(cx, top);
        _rotateArm.EndPoint = new Point(cx, top - 34);
        _rotateHandle.IsVisible = true;
        Canvas.SetLeft(_rotateHandle, cx - 12);
        Canvas.SetTop(_rotateHandle, top - 34 - 12);
    }

    /// <summary>隐藏旋转臂与旋转手柄（取消选中/无可显示画面时）。</summary>
    private void HideRotateHandle()
    {
        if (_rotateArm != null)
        {
            _rotateArm.IsVisible = false;
        }

        if (_rotateHandle != null)
        {
            _rotateHandle.IsVisible = false;
        }
    }

    /// <summary>矩形绕中心旋转 deg 度后的轴对齐外接框（AABB）。</summary>
    private static Rect RotatedBounds(Rect r, double degrees)
    {
        if (Math.Abs(degrees) < 0.01)
        {
            return r;
        }

        var rad = degrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(rad));
        var sin = Math.Abs(Math.Sin(rad));
        var w = r.Width * cos + r.Height * sin;
        var h = r.Width * sin + r.Height * cos;
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;
        return new Rect(cx - w / 2, cy - h / 2, w, h);
    }

    /// <summary>旋转手柄按下：记录起始指针 / 基准矩形（中心为旋转中心）/ 起始角度。</summary>
    private void RotateHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_selected is not { } clip || clip.Track >= _stageLayers.Count || !_stageLayers[clip.Track].Image.IsVisible)
        {
            return;
        }

        if (_project.GetTrackState(clip.Track) is { Locked: true })
        {
            _statusText.Text = "该轨道已锁定，无法编辑。";
            return;
        }

        _rotating = true;
        _resizeUndoPushed = false;
        // 按下 → 实心强调色（与八向缩放手柄 pressed 同款）。
        _rotateDot.Fill = ThemePalette.AccentBrush();
        _rotateDot.Stroke = null;
        _rotateStartPointer = e.GetPosition(_stageBorder);
        _rotateStartRect = GetSelectedDisplayRect(clip);
        _rotateStartRotation = clip.Rotation;
        if (sender is Border b)
        {
            e.Pointer.Capture(b);
        }

        e.Handled = true;
    }

    /// <summary>旋转手柄拖动：绕基准矩形中心把片段旋转到指针方向（15° 吸附），实时应用并移动手柄。</summary>
    private void RotateHandleOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rotating || _selected is not { } clip)
        {
            return;
        }

        var center = new Point(_rotateStartRect.Center.X, _rotateStartRect.Center.Y);
        var v0 = _rotateStartPointer - center;
        var v1 = e.GetPosition(_stageBorder) - center;
        var baseAngle = Math.Atan2(v0.Y, v0.X) * 180 / Math.PI;
        var currentAngle = Math.Atan2(v1.Y, v1.X) * 180 / Math.PI;
        var angle = NormalizeAngle180(_rotateStartRotation + (currentAngle - baseAngle));
        // 吸附到 15° 倍（阈值 2.5°），与底图图层编辑器一致。
        var snapped = Math.Round(angle / 15.0) * 15.0;
        if (Math.Abs(snapped - angle) < 2.5)
        {
            angle = snapped;
        }

        if (Math.Abs(angle - clip.Rotation) < 0.01)
        {
            return;
        }

        // 首次实际旋转才压撤销。
        if (!_resizeUndoPushed)
        {
            PushUndo();
            _resizeUndoPushed = true;
        }

        clip.Rotation = angle;
        if (clip.Track < _stageLayers.Count)
        {
            ApplyTransform(_stageLayers[clip.Track], clip);
        }

        UpdateStageHandles();
        ScheduleSave();
    }

    /// <summary>旋转手柄释放：结束并回读属性面板。</summary>
    private void RotateHandleOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rotating)
        {
            return;
        }

        _rotating = false;
        // 恢复默认样式：白底 + 强调色描边。
        _rotateDot.Fill = Brushes.White;
        _rotateDot.Stroke = ThemePalette.AccentBrush();
        _rotateDot.StrokeThickness = 2;
        e.Pointer.Capture(null);
        FillPropertyPanel();
    }

    /// <summary>角度归一化到 [-180, 180)。</summary>
    private static double NormalizeAngle180(double angle)
    {
        angle %= 360;
        if (angle > 180)
        {
            angle -= 360;
        }

        if (angle < -180)
        {
            angle += 360;
        }

        return angle;
    }

    /// <summary>选中片段在舞台上的显示矩形（原比例 Uniform 基准 + 缩放 + 偏移；旋转时返回轴对齐外接框 AABB，
    /// 供选框/八向手柄定位与旋转手柄的旋转中心计算共用）。</summary>
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
        var r = new Rect(x, y, sw, sh);
        // 旋转时返回绕中心旋转后的轴对齐外接框（AABB），选框/手柄/拖拽数学一致跟随画面。
        return clip.Rotation == 0 ? r : RotatedBounds(r, clip.Rotation);
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

    /// <summary>渲染结果。</summary>
    private enum RenderOutcome
    {
        Succeeded,
        Cancelled,
        Background,
        Failed
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

        // 2. 分辨率 / 画质选项（默认横向 1280）。
        var options = await AskRenderOptionsAsync();
        if (options == null)
        {
            return;
        }

        // 3. 保存工程快照并弹出模态渲染对话框（进度条 + 剩余时间 + 取消 / 后台渲染）。
        var (outW, outH, crf, hw) = options.Value;
        VideoProjectStore.Save(_project, VideoProjectStore.DefaultPath);
        _statusText.Text = "正在渲染…";
        var cts = new CancellationTokenSource();
        var outcome = await ShowRenderProgressDialogAsync(outputPath, outW, outH, crf, hw, cts);
        switch (outcome)
        {
            case RenderOutcome.Succeeded:
                ApplyRenderedVideo(outputPath, outW, outH);
                Close();
                break;
            case RenderOutcome.Cancelled:
                _statusText.Text = "已取消渲染，未应用。";
                break;
            case RenderOutcome.Background:
                _statusText.Text = "已转入后台渲染，完成后会通知你。";
                Close();
                break;
            case RenderOutcome.Failed:
                _statusText.Text = "渲染失败，未应用。";
                break;
        }
    }

    /// <summary>
    /// 模态渲染进度对话框：进度条 + 状态 + 剩余时间估算，阻断用户操作；提供
    /// 「取消」（删半成品）与「后台渲染」（关闭编辑器后台跑，完成后 Toast 通知）。
    /// </summary>
    private async Task<RenderOutcome> ShowRenderProgressDialogAsync(
        string outputPath, int outW, int outH, int crf, bool hw, CancellationTokenSource cts)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, MinHeight = 4 };
        var statusText = new TextBlock
        {
            Text = "准备中…",
            Opacity = 0.8,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        var etaText = new TextBlock { Text = "", Opacity = 0.6, FontSize = 11 };
        var dialog = new ContentDialog
        {
            Title = "正在渲染视频…",
            Content = new StackPanel
            {
                Spacing = 8,
                Width = 320,
                Children =
                {
                    statusText,
                    bar,
                    etaText
                }
            },
            PrimaryButtonText = "取消",
            SecondaryButtonText = "后台渲染",
            DefaultButton = ContentDialogButton.Close
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IProgress<(double, string)> progress = new Progress<(double, string)>(p =>
        {
            var percent = p.Item1;
            var msg = p.Item2;
            bar.Value = Math.Clamp(percent, 0, 1);
            statusText.Text = msg;
            if (percent > 0.01)
            {
                var remaining = sw.Elapsed.TotalSeconds / percent * (1 - percent);
                etaText.Text = $"剩余约 {FormatEta(TimeSpan.FromSeconds(remaining))}";
            }

            UpdateTrayProgress(percent, msg);
        });

        // 后台渲染任务（进度回调跨线程 → Progress 封送到 UI 线程）。
        var renderTask = Task.Run(() =>
        {
            new VideoProjectRenderer(_project, outputPath, outW, outH, crf, _targetFps,
                (percent, msg) => progress.Report((percent, msg)),
                crf <= 20 ? "medium" : crf <= 24 ? "faster" : "veryfast",
                hw ? "auto" : null,
                hw ? "h264_qsv" : null,
                cts.Token).Render();
        });
        // 正常完成 → 自动关闭对话框；失败 → 在对话框里显示错误。
        _ = renderTask.ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    if (dialog.IsVisible)
                    {
                        dialog.Hide();
                    }
                }
                else if (t.IsFaulted)
                {
                    bar.Value = 0;
                    statusText.Text = $"渲染失败：{t.Exception?.GetBaseException().Message}";
                }
            });
        }, TaskScheduler.Default);

        var result = await ShowDialogAsync(dialog);
        if (renderTask.IsCompletedSuccessfully)
        {
            return RenderOutcome.Succeeded;
        }

        if (renderTask.IsFaulted)
        {
            DeleteFileIfExists(outputPath);
            RemoveTrayProgress();
            return RenderOutcome.Failed;
        }

        // 渲染仍在进行：用户点了按钮。
        if (result == ContentDialogResult.Secondary)
        {
            // 后台渲染：关闭编辑器继续跑，完成后应用 + Toast 通知 + 移除托盘进度。
            _ = RunBackgroundRenderAsync(renderTask, outputPath, outW, outH);
            return RenderOutcome.Background;
        }

        // 取消：终止任务并删除半成品。
        cts.Cancel();
        try
        {
            await renderTask;
        }
        catch
        {
            // 任务以 OperationCanceledException 结束。
        }

        DeleteFileIfExists(outputPath);
        RemoveTrayProgress();
        return RenderOutcome.Cancelled;
    }

    /// <summary>后台渲染收尾：等待任务完成后应用主界面并弹完成通知。</summary>
    private async Task RunBackgroundRenderAsync(Task renderTask, string outputPath, int outW, int outH)
    {
        try
        {
            await renderTask;
            ApplyRenderedVideo(outputPath, outW, outH);
            RemoveTrayProgress();
            ShowRenderCompletedToast("渲染完成", $"视频已渲染为 {outW}×{outH} 并应用到主界面。");
        }
        catch (Exception ex)
        {
            RemoveTrayProgress();
            DeleteFileIfExists(outputPath);
            ShowRenderCompletedToast("渲染失败", ex.GetBaseException().Message);
        }
    }

    /// <summary>把渲染出的 mp4 应用为主界面视频背景（单文件模式）。</summary>
    private void ApplyRenderedVideo(string outputPath, int outW, int outH)
    {
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        settings.VideoFillEnabled = true;
        settings.VideoFillPath = outputPath;
        settings.VideoFillLoop = true;
        settings.VideoProjectEnabled = false;
        settings.EndUpdate();
        InjectorRuntime.SaveAndApply();
        _statusText.Text = $"已渲染 {outW}×{outH} 并应用到主界面。";
    }

    /// <summary>渲染完成/失败通知：优先 Windows 系统 Toast，失败回退应用内右上角 Toast。</summary>
    private static void ShowRenderCompletedToast(string title, string message)
    {
        try
        {
            if (ShowWindowsToast(title, message))
            {
                return;
            }
        }
        catch
        {
            // 回退应用内 Toast。
        }

        try
        {
            if (AppBase.Current?.MainWindow is Window host)
            {
                Dispatcher.UIThread.Post(() =>
                    new ReminderToastWindow().ShowFor(host, message, InfoBarSeverity.Success, title));
            }
        }
        catch
        {
            // 通知失败不影响渲染结果。
        }
    }

    /// <summary>尝试 Windows 系统 Toast（WinRT；AUMID 未注册时返回 false 走应用内回退）。</summary>
    private static bool ShowWindowsToast(string title, string message)
    {
        try
        {
            var xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{title}</text>
                      <text>{message}</text>
                    </binding>
                  </visual>
                </toast>
                """;
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            var notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
            notifier.Show(new Windows.UI.Notifications.ToastNotification(doc));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>剩余时间格式。</summary>
    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalMinutes >= 60)
        {
            return $"{eta.TotalMinutes / 60:0.#} 小时";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{(int)eta.TotalMinutes} 分 {eta.Seconds} 秒";
        }

        return $"{Math.Max(0, (int)eta.TotalSeconds)} 秒";
    }

    /// <summary>删除文件（渲染取消/失败时清理半成品）。</summary>
    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    /// <summary>托盘菜单渲染进度项（后台渲染时注入宿主托盘「更多」菜单）。</summary>
    private NativeMenuItem? _trayProgressItem;
    private bool _trayItemRegistered;

    private void UpdateTrayProgress(double percent, string message)
    {
        try
        {
            if (IAppHost.TryGetService<ITaskBarIconService>() is not { } tray)
            {
                return;
            }

            _trayProgressItem ??= new NativeMenuItem { Header = "" };
            if (!_trayItemRegistered)
            {
                tray.MoreOptionsMenuItems.Add(_trayProgressItem);
                _trayItemRegistered = true;
            }

            _trayProgressItem.Header = $"{message}（{percent:P0}）";
        }
        catch
        {
            // 托盘不可用/注入失败时静默（不影响渲染）。
        }
    }

    private void RemoveTrayProgress()
    {
        try
        {
            if (_trayItemRegistered && _trayProgressItem != null &&
                IAppHost.TryGetService<ITaskBarIconService>() is { } tray)
            {
                tray.MoreOptionsMenuItems.Remove(_trayProgressItem);
            }
        }
        catch
        {
            // ignore
        }

        _trayProgressItem = null;
        _trayItemRegistered = false;
    }

    /// <summary>渲染选项对话框：横向分辨率（默认 1280）+ 画质（CRF）+ 硬件加速。返回 null 表示取消。</summary>
    private async Task<(int outW, int outH, int crf, bool hw)?> AskRenderOptionsAsync()
    {
        // 横向分辨率预设：主界面是超宽条（比例约 14:1），旧版按竖向分辨率（270p 等）
        // 换算会把宽度爆到 2K+（270p → 3888×270）。按横向算，1280 对课表展示足够。
        var resolutions = new[] { "640", "800", "1280", "1600", "1920", "原尺寸" };
        var qualities = new[] { "高", "中", "低" };
        var resCombo = new ComboBox { ItemsSource = resolutions, SelectedIndex = 2 };
        var qualityCombo = new ComboBox { ItemsSource = qualities, SelectedIndex = 1 };
        var resHint = new TextBlock { FontSize = 11, Opacity = 0.65 };
        resCombo.SelectionChanged += (_, _) =>
        {
            var aspect = _project.OutputWidth / Math.Max(1.0, _project.OutputHeight);
            var (w, h) = ComputeRenderSize(resCombo.SelectedItem?.ToString() ?? "1280", aspect);
            resHint.Text = $"输出 {w}×{h}（主界面比例 {aspect:0.##}:1）";
        };
        var hwToggle = new ToggleSwitch
        {
            IsChecked = InjectorRuntime.Settings.RenderHardwareAccelerated,
            OnContent = "硬件加速",
            OffContent = "软解模式"
        };
        var dialog = new ContentDialog
        {
            Title = "渲染设置",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "横向分辨率：" },
                    resCombo,
                    resHint,
                    new TextBlock { Text = "画质：" },
                    qualityCombo,
                    hwToggle,
                    new TextBlock
                    {
                        Text = "硬件加速：自动探测 Intel QSV/NVIDIA/AMD/Windows 硬件编码与解码。",
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
        resCombo.SelectedIndex = 2; // 默认横向 1280（触发尺寸提示）

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

        var aspect2 = _project.OutputWidth / Math.Max(1.0, _project.OutputHeight);
        var (outW, outH) = ComputeRenderSize(resCombo.SelectedItem?.ToString() ?? "1280", aspect2);
        var crf = (qualityCombo.SelectedItem?.ToString()) switch
        {
            "高" => 20,
            "低" => 28,
            _ => 24
        };
        return (outW, outH, crf, hwToggle.IsChecked == true);
    }

    /// <summary>按横向分辨率预设计算渲染尺寸（高度按主界面宽高比换算，偶数对齐）。</summary>
    private (int w, int h) ComputeRenderSize(string preset, double aspect)
    {
        if (preset == "原尺寸")
        {
            var w = Math.Max(2, (int)Math.Round(_project.OutputWidth / 2.0) * 2);
            var h = Math.Max(2, (int)Math.Round(_project.OutputHeight / 2.0) * 2);
            return (w, h);
        }

        var outW = int.TryParse(preset, out var width) ? Math.Max(2, width) : 1280;
        // 高度 = 宽度 ÷ 宽高比，偶数对齐（H.264 yuv420 要求）。
        var outH = Math.Max(2, (int)Math.Round(outW / Math.Max(0.05, aspect) / 2.0) * 2);
        return (outW & ~1, outH);
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
            _selectedClips.Clear();
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
