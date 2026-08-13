using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Assists;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Shared;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// The settings page deliberately follows the layout used by ClassIsland's built-in appearance page.
/// Settings are edited locally and applied together by the button at the bottom of the page.
/// </summary>
[SettingsPageInfo("classisland.injector", "样式注入器", "\uEC4A", "\uEC4A")]
public sealed class InjectorSettingsPage : SettingsPageBase
{
    private readonly ToggleSwitch _enabled = Toggle();
    private readonly Slider _opacity = Slider(0, 1, 0.05);
    private readonly Spin _rotation = Spinner(-360, 360, 1, "0");
    private readonly Spin _offsetX = Spinner(-2000, 2000, 1, "0");
    private readonly Spin _offsetY = Spinner(-2000, 2000, 1, "0");
    private readonly ToggleSwitch _animationEnabled = Toggle();
    private readonly ComboBox _animationMode = Combo(IslandAnimationModes);
    private readonly Slider _animationAmount = Slider(0, 1, 0.01);
    private readonly Spin _animationPeriod = Spinner(0.2, 60, 0.1);
    private readonly TextBox _styleSheetPath = new() { MinWidth = 280 };
    private readonly ToggleSwitch _watchStyleSheet = Toggle();

    private readonly Spin _cornerRadius = Spinner(0, 20, 1, "0");
    private readonly ToggleSwitch _customBackground = Toggle();
    private readonly ColorPicker _backgroundColor = ColorPicker();
    private readonly ToggleSwitch _dynamicBackgroundColor = Toggle();
    private readonly ToggleSwitch _dynamicBorderColor = Toggle();
    private readonly ToggleSwitch _dynamicShadowColor = Toggle();
    private readonly ToggleSwitch _revertColorsWhenPaused = Toggle();
    private readonly ToggleSwitch _dynamicThemeColor = Toggle();
    private readonly ToggleSwitch _mouseHoverKeepVisible = Toggle();
    private readonly ToggleSwitch _clickEffectEnabled = Toggle();
    private readonly ComboBox _clickEffectType = Combo(ClickEffectTypes);
    private readonly ToggleSwitch _fakeWeatherEnabled = Toggle();
    private readonly ComboBox _fakeWeatherCode = Combo(FakeWeatherCodes);
    private readonly Spin _fakeWeatherTemperature = Spinner(-60, 60, 1, "0");
    private readonly Spin _fakeWeatherFeelsLike = Spinner(-60, 60, 1, "0");
    private readonly Spin _fakeWeatherHumidity = Spinner(0, 100, 1, "0");
    private readonly Spin _fakeWeatherPressure = Spinner(800, 1200, 1, "0");
    private readonly Spin _fakeWeatherVisibility = Spinner(0, 100, 0.5, "0.##");
    private readonly TextBox _fakeWeatherWindDirection = new() { MinWidth = 160, Watermark = "如：东风" };
    private readonly TextBox _fakeWeatherWindScale = new() { MinWidth = 120, Watermark = "如：2级" };
    private readonly Spin _fakeWeatherAqi = Spinner(0, 500, 1, "0");
    private readonly ComboBox _fakeWeatherAlertIcon = Combo(FakeWeatherAlertIcons);
    private readonly TextBox _fakeWeatherAlertType = new() { MinWidth = 160, Watermark = "如：暴雨" };
    private readonly TextBox _fakeWeatherAlertLevel = new() { MinWidth = 160, Watermark = "如：蓝色预警" };
    private readonly TextBox _fakeWeatherAlertTitle = new() { MinWidth = 260, Watermark = "如：xx市气象台发布暴雨蓝色预警" };
    private readonly TextBox _fakeWeatherAlertDetail = new() { MinWidth = 260, Watermark = "如：预计未来 6 小时……（可留空）" };
    private readonly Spin _fakeWeatherRainRemainingMinutes = Spinner(-180, 180, 1, "0");
    private readonly ComboBox _startupOpenTarget = Combo(StartupOpenTargets);
    // 调试
    private readonly ToggleSwitch _reduceVisualBurden = Toggle();
    private readonly ToggleSwitch _disableVersionCheck = Toggle();
    private readonly ToggleSwitch _disableDegradationCheck = Toggle();
    private readonly ToggleSwitch _diagnosticLogging = Toggle();
    /// <summary>「降低视觉负担」需要隐藏说明的全部设置项/卡片及其原始说明。</summary>
    private readonly List<SettingsExpander> _allExpanders = [];
    private readonly List<SettingsExpanderItem> _allItems = [];
    private readonly Dictionary<SettingsExpander, string?> _savedExpanderDescriptions = [];
    private readonly Dictionary<SettingsExpanderItem, string?> _savedItemDescriptions = [];
    private readonly ComboBox _contractTableList = new()
    {
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _contractCurrent = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _contractStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    /// <summary>顶部「宿主点位失效」InfoBar（页面构建后按健康检查结果刷新）。</summary>
    private InfoBar? _degradationInfoBar;
    /// <summary>顶部「插件版本过低」InfoBar（按所选对照表的最低插件版本要求刷新）。</summary>
    private InfoBar? _pluginUpdateInfoBar;
    private readonly Spin _albumColorPollingInterval = Spinner(0.5, 120, 0.5);
    private readonly Spin _albumColorTransition = Spinner(0, 10, 0.1);
    /// <summary>SMTC 教学：教程定位/展开的分组（Name 供 TargetSelector 使用）。</summary>
    private SettingsExpander _smtcDynamicGroup = null!;
    private SettingsExpander _backgroundGroup = null!;
    private SettingsExpander _textureGroup = null!;
    private SettingsExpander _shadowGroup = null!;
    private SettingsExpander _borderGroup = null!;
    private SettingsExpander _wallpaperGroup = null!;
    private readonly ToggleSwitch _gradient = Toggle();
    private readonly ColorPicker _gradientEndColor = ColorPicker();
    private readonly ComboBox _gradientDirection = Combo(GradientDirections);
    private readonly ComboBox _backgroundTextureType = Combo(BackgroundTextures);
    private readonly ColorPicker _backgroundTextureColor = ColorPicker();
    private readonly Spin _backgroundTextureSize = Spinner(8, 80, 2, "0");
    private readonly Slider _backgroundTextureSpectrumSensitivity = Slider(0.1, 3, 0.05);
    private readonly Spin _backgroundTextureSpectrumBars = Spinner(4, 64, 1, "0");
    private readonly ToggleSwitch _backgroundTextureSpectrumMirrored = Toggle();
    private readonly ToggleSwitch _backgroundTextureSpectrumAutoWidth = Toggle();

    private readonly ToggleSwitch _shadow = Toggle();
    private readonly ColorPicker _shadowColor = ColorPicker();
    private readonly Spin _shadowBlur = Spinner(0, 200, 1, "0");
    private readonly Spin _shadowOffsetX = Spinner(-200, 200, 1, "0");
    private readonly Spin _shadowOffsetY = Spinner(-200, 200, 1, "0");
    private readonly Slider _shadowOpacity = Slider(0, 1, 0.05);
    private readonly ToggleSwitch _border = Toggle();
    private readonly ColorPicker _borderColor = ColorPicker();
    private readonly Spin _borderThickness = Spinner(0.25, 20, 0.25);

    private readonly ToggleSwitch _wallpaperEnabled = Toggle();
    /// <summary>背景图片编辑模式：基础模式（简单设置）或专家模式（图层编辑器）。</summary>
    private readonly ComboBox _wallpaperModeBox = Combo(WallpaperModes);
    private readonly ComboBox _wallpaperSource = Combo(WallpaperSources);
    private readonly TextBox _wallpaperPath = new() { MinWidth = 260, IsReadOnly = true };
    private readonly Slider _wallpaperOpacity = Slider(0, 1, 0.05);
    private readonly ComboBox _wallpaperDisplayMode = Combo(WallpaperDisplayModes);
    private readonly Spin _wallpaperScale = Spinner(1, 5, 0.1);
    private readonly Spin _wallpaperOffsetX = Spinner(-0.5, 0.5, 0.01);
    private readonly Spin _wallpaperOffsetY = Spinner(-0.5, 0.5, 0.01);
    private readonly Spin _wallpaperSlideshowInterval = Spinner(2, 3600, 1, "0");
    private readonly Spin _wallpaperBlur = Spinner(0, 60, 1);
    /// <summary>「打开图层编辑器」入口（仅专家模式显示）。</summary>
    private SettingsExpanderItem _wallpaperEditorItem = null!;
    /// <summary>基础模式专属设置项（专家模式时整体隐藏）。</summary>
    private SettingsExpanderItem _wallpaperSourceItem = null!;
    private SettingsExpanderItem _wallpaperPathItem = null!;
    private SettingsExpanderItem _wallpaperOpacityItem = null!;
    private SettingsExpanderItem _wallpaperDisplayModeItem = null!;
    private SettingsExpanderItem _wallpaperScaleItem = null!;
    private SettingsExpanderItem _wallpaperOffsetXItem = null!;
    private SettingsExpanderItem _wallpaperOffsetYItem = null!;
    private SettingsExpanderItem _wallpaperBlurItem = null!;
    private SettingsExpanderItem _wallpaperSlideshowItem = null!;
    /// <summary>图层式底图状态提示（专家模式时显示图层数）。</summary>
    private InfoBar? _wallpaperModeInfoBar;

    private readonly ComboBox _visibilityAnimation = Combo(VisibilityAnimations);
    private readonly Spin _visibilityDuration = Spinner(0.1, 10, 0.05);
    private readonly ComboBox _emphasisAnimation = Combo(EmphasisAnimations);
    private readonly Slider _emphasisAmount = Slider(0, 1, 0.01);
    private readonly Spin _emphasisDuration = Spinner(0.1, 10, 0.05);
    private readonly ComboBox _notificationTransition = Combo(NotificationTransitions);
    private readonly Spin _notificationTransitionDuration = Spinner(0.05, 5, 0.05);
    private readonly ToggleSwitch _carouselAnimation = Toggle();
    private readonly ComboBox _carouselAnimationType = Combo(CarouselAnimationTypes);
    private readonly Spin _carouselAnimationDuration = Spinner(0.05, 5, 0.05);
    private readonly Spin _carouselAnimationOffset = Spinner(0, 500, 5, "0");

    private readonly ComboBox _rippleType = Combo(RippleTypes);
    private readonly ColorPicker _rippleColor = ColorPicker();
    private readonly Spin _rippleDuration = Spinner(0.1, 10, 0.05);
    private readonly Spin _rippleThickness = Spinner(0.5, 40, 0.5);
    private readonly Slider _rippleOpacity = Slider(0.1, 1, 0.05);
    private readonly ToggleSwitch _rippleConstraint = Toggle();
    private readonly Spin _rippleConstraintRadius = Spinner(0, 2000, 10, "0");
    private readonly ToggleSwitch _marqueeEnabled = Toggle();
    private readonly ColorPicker _marqueeColor = ColorPicker();
    private readonly Spin _marqueeDuration = Spinner(0.1, 10, 0.05);
    private readonly Slider _marqueeOpacity = Slider(0.1, 1, 0.05);
    private readonly Spin _marqueeSpeed = Spinner(0.1, 8, 0.1);
    private readonly Spin _marqueeFrameThickness = Spinner(0.01, 0.15, 0.01);
    private readonly ComboBox _prepareOnClassStyle = Combo(PrepareOnClassStyles);
    private readonly ColorPicker _countdownArrowColor = ColorPicker();
    private readonly Spin _countdownArrowCount = Spinner(1, 24, 1, "0");
    private readonly Spin _countdownArrowPerGroup = Spinner(1, 12, 1, "0");
    private readonly Spin _countdownArrowSpacing = Spinner(0, 100, 1, "0");
    private readonly Spin _countdownArrowGroupSpacing = Spinner(0, 400, 1, "0");
    private readonly Spin _countdownArrowSpeed = Spinner(0.1, 12, 0.1);
    private readonly Spin _countdownArrowThickness = Spinner(0.5, 20, 0.5);
    private readonly ColorPicker _countdownPulseColor = ColorPicker();
    private readonly Spin _countdownPulseThickness = Spinner(0.5, 20, 0.5);
    private readonly Spin _countdownPulseSpeed = Spinner(0.1, 8, 0.1);
    private readonly Spin _countdownPulseMaxRadius = Spinner(0.1, 1, 0.05);
    private readonly ColorPicker _countdownScanColor = ColorPicker();
    private readonly Spin _countdownScanThickness = Spinner(0.5, 20, 0.5);
    private readonly Spin _countdownScanSpeed = Spinner(0.1, 8, 0.1);
    private readonly ComboBox _countdownScanDirection = Combo(ScanDirections);
    private readonly ToggleSwitch _countdownScanTailEnabled = Toggle();
    private readonly ColorPicker _countdownLightBandColor = ColorPicker();
    private readonly Spin _countdownLightBandThickness = Spinner(0.02, 0.5, 0.01, "0.##");
    private readonly Spin _countdownLightBandAngle = Spinner(-90, 90, 1, "0");
    private readonly Spin _countdownLightBandSpeed = Spinner(0.1, 8, 0.1);
    private readonly ToggleSwitch _prepareWarningEnabled = Toggle();
    private readonly ColorPicker _prepareWarningColor = ColorPicker();
    private readonly Spin _prepareWarningTriggerSeconds = Spinner(5, 600, 5, "0");
    private readonly Spin _prepareWarningFlashSpeed = Spinner(0.1, 10, 0.1);
    private readonly Slider _prepareWarningFlashAmount = Slider(0, 1, 0.05);
    private readonly Spin _prepareWarningFrameThickness = Spinner(0.005, 0.1, 0.005, "0.###");
    private readonly Slider _prepareWarningOpacity = Slider(0.1, 1, 0.05);
    private readonly Slider _cinematicShake = Slider(0, 60, 1);
    private readonly Slider _cinematicBlur = Slider(0, 40, 1);
    private readonly Slider _cinematicFlash = Slider(0, 1, 0.05);
    // 卡片总开关（页面归一化用，不持久化）：关闭时对应样式值写为 None / HostDefault。
    private readonly ToggleSwitch _backgroundTextureEnabled = Toggle();
    private readonly ToggleSwitch _visibilityAnimationEnabled = Toggle();
    private readonly ToggleSwitch _emphasisAnimationEnabled = Toggle();
    private readonly ToggleSwitch _notificationTransitionEnabled = Toggle();
    private readonly ToggleSwitch _rippleEnabled = Toggle();
    private readonly ToggleSwitch _prepareOnClassEnabled = Toggle();
    private readonly TextBox _presetName = new() { MinWidth = 200, Watermark = "预设名称" };
    private readonly ComboBox _userPresetList = new()
    {
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    /// <summary>实时预览开关：开启后设置项修改立即保存应用（可视化编辑器仍为手动保存）。默认开启。</summary>
    private readonly ToggleSwitch _livePreview = new()
    {
        IsChecked = true,
        OnContent = "开",
        OffContent = "关",
        VerticalAlignment = VerticalAlignment.Center
    };
    /// <summary>实时预览防抖定时器，避免拖动控件时高频写盘。</summary>
    private readonly DispatcherTimer _livePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    /// <summary>抑制实时预览（程序性修改控件时置 true，如加载 / 撤销 / 编辑器操作）。</summary>
    private bool _suppressLivePreview;
    private IslandVisualEditorWindow? _visualEditorWindow;
    private readonly List<IslandPreviewState> _editorUndo = [];
    private readonly List<IslandPreviewState> _editorRedo = [];
    private bool _editorDirty;

    /// <summary>背景图片编辑模式：基础模式 = 简单单图设置；专家模式 = Photoshop 风格图层编辑器。</summary>
    private static readonly Choice<bool>[] WallpaperModes =
    [
        new(false, "基础模式"),
        new(true, "专家模式"),
    ];

    private static readonly Choice<WallpaperSource>[] WallpaperSources =
    [
        new(WallpaperSource.LocalImage, "本地图片"),
        new(WallpaperSource.FolderSlideshow, "文件夹幻灯片"),
        new(WallpaperSource.SmtcAlbum, "SMTC 专辑封面"),
    ];

    private static readonly Choice<WallpaperDisplayMode>[] WallpaperDisplayModes =
    [
        new(WallpaperDisplayMode.Fill, "填充（裁剪）"),
        new(WallpaperDisplayMode.Fit, "适应（完整显示）"),
        new(WallpaperDisplayMode.Stretch, "拉伸（变形）"),
        new(WallpaperDisplayMode.Tile, "平铺"),
    ];

    private static readonly Choice<IslandAnimationMode>[] IslandAnimationModes =
    [
        new(IslandAnimationMode.Breathe, "呼吸"),
        new(IslandAnimationMode.Float, "浮动"),
        new(IslandAnimationMode.Wave, "波浪"),
    ];

    private static readonly Choice<VisibilityAnimation>[] VisibilityAnimations =
    [
        new(VisibilityAnimation.Fade, "淡入淡出"),
        new(VisibilityAnimation.Scale, "缩放"),
        new(VisibilityAnimation.SlideFromTop, "从上方滑入"),
        new(VisibilityAnimation.SlideFromBottom, "从下方滑入"),
    ];

    private static readonly Choice<EmphasisAnimation>[] EmphasisAnimations =
    [
        new(EmphasisAnimation.Pulse, "脉冲"),
        new(EmphasisAnimation.Bounce, "弹跳"),
        new(EmphasisAnimation.Shake, "摇晃"),
        new(EmphasisAnimation.Flash, "闪烁"),
    ];

    private static readonly Choice<NotificationTransition>[] NotificationTransitions =
    [
        new(NotificationTransition.Fade, "淡入淡出"),
        new(NotificationTransition.SlideDown, "向下滑动"),
        new(NotificationTransition.SlideUp, "向上滑动"),
        new(NotificationTransition.SlideLeft, "向左滑动"),
        new(NotificationTransition.SlideRight, "向右滑动"),
    ];

    private static readonly Choice<RippleType>[] RippleTypes =
    [
        new(RippleType.Ring, "单环"),
        new(RippleType.DoubleRing, "双环"),
        new(RippleType.Glow, "光晕"),
        new(RippleType.Square, "方框"),
        new(RippleType.Hanabi, "舞萌花火（高级）"),
        new(RippleType.Diamond, "菱形"),
        new(RippleType.Triangle, "三角"),
        new(RippleType.Star, "星形"),
        new(RippleType.Hexagon, "六边形"),
        new(RippleType.Burst, "放射"),
        new(RippleType.Explode, "爆炸（高级）"),
        new(RippleType.Particle, "粒子"),
        new(RippleType.Cinematic, "屏幕涟漪（高级）"),
    ];

    private static readonly Choice<ClickEffectType>[] ClickEffectTypes =
    [
        new(ClickEffectType.Ring, "扩散圆环"),
        new(ClickEffectType.Bounce, "轻微跳跃"),
    ];

    private static readonly Choice<int>[] FakeWeatherCodes =
    [
        new(0, "晴"),
        new(1, "多云"),
        new(2, "阴"),
        new(3, "阵雨"),
        new(4, "雷阵雨"),
        new(7, "小雨"),
        new(8, "中雨"),
        new(9, "大雨"),
        new(14, "小雪"),
        new(15, "中雪"),
        new(18, "雾"),
        new(53, "霾"),
        new(99, "未知"),
    ];

    private static readonly Choice<int>[] FakeWeatherAlertIcons =
    [
        new(0, "无"),
        new(1, "蓝色"),
        new(2, "黄色"),
        new(3, "橙色"),
        new(4, "红色"),
    ];

    private static readonly Choice<int>[] StartupOpenTargets =
    [
        new(0, "不打开"),
        new(1, "ClassIsland 应用设置"),
        new(2, "应用设置 → 样式注入器"),
        new(3, "应用设置 → 插件"),
    ];

    private static readonly Choice<CarouselAnimationType>[] CarouselAnimationTypes =
    [
        new(CarouselAnimationType.SlideUp, "上翻"),
        new(CarouselAnimationType.SlideDown, "下翻"),
        new(CarouselAnimationType.SlideLeft, "左滑"),
        new(CarouselAnimationType.SlideRight, "右滑"),
        new(CarouselAnimationType.Fade, "淡入淡出"),
    ];

    private static readonly Choice<PrepareOnClassStyle>[] PrepareOnClassStyles =
    [
        new(PrepareOnClassStyle.Arrows, "箭头"),
        new(PrepareOnClassStyle.PulseRing, "扩散光环"),
        new(PrepareOnClassStyle.Scanline, "扫描线"),
        new(PrepareOnClassStyle.LightBand, "光带"),
    ];

    private static readonly Choice<ScanlineDirection>[] ScanDirections =
    [
        new(ScanlineDirection.Horizontal, "横向（上下扫）"),
        new(ScanlineDirection.Vertical, "纵向（左右扫）"),
    ];

    private static readonly Choice<GradientDirection>[] GradientDirections =
    [
        new(GradientDirection.TopLeftToBottomRight, "左上 → 右下"),
        new(GradientDirection.TopToBottom, "上 → 下"),
        new(GradientDirection.LeftToRight, "左 → 右"),
        new(GradientDirection.BottomLeftToTopRight, "左下 → 右上"),
        new(GradientDirection.BottomToTop, "下 → 上"),
        new(GradientDirection.RightToLeft, "右 → 左"),
        new(GradientDirection.TopRightToBottomLeft, "右上 → 左下"),
        new(GradientDirection.BottomRightToTopLeft, "右下 → 左上"),
    ];

    private static readonly Choice<BackgroundTexture>[] BackgroundTextures =
    [
        new(BackgroundTexture.Grid, "网格线"),
        new(BackgroundTexture.Dots, "点阵"),
        new(BackgroundTexture.DiagonalLines, "斜线"),
        new(BackgroundTexture.Cross, "十字网格"),
        new(BackgroundTexture.Spectrum, "动态频谱"),
    ];

    public InjectorSettingsPage()
    {
        Content = BuildContent();
        WireVisualEditor();
        WireLivePreview();
        // 调试开关即时生效（须在 LoadFromSettings 之前挂接，加载持久化值时也会触发）。
        _reduceVisualBurden.PropertyChanged += (_, _) => ApplyVisualBurdenReduction();
        _disableVersionCheck.PropertyChanged += (_, _) => UpdatePluginUpdateInfoBar(_contractTableList.SelectedItem as ContractIndexEntry);
        _disableDegradationCheck.PropertyChanged += (_, _) => RefreshHealthInfoBar();
        LoadFromSettings();
        RefreshSmtcTutorialInfoBar();
        // 对照表被应用/切换后刷新本页状态（顶部降级提示、当前对照表、下拉选中）。
        ContractCatalogService.CatalogChanged += (_, _) => Dispatcher.UIThread.Post(RefreshContractUi);
        // 用户切换下拉选择时，按该对照表的最低插件版本要求刷新「插件版本过低」提示。
        _contractTableList.SelectionChanged += (_, _) => UpdatePluginUpdateInfoBar(_contractTableList.SelectedItem as ContractIndexEntry);
        WireSmtcTutorial();
    }

    /// <summary>SMTC 教学路径（教程 Id / 起始段落），改 JSON 里的教程 Id 时务必同步修改。</summary>
    private const string SmtcTutorialPath = "classislandInjector.tutorials.smtc/prologue";

    /// <summary>「SMTC 动态取色」进阶教学入口 InfoBar（未完成时显示，点击按钮开始教学）。</summary>
    private InfoBar _smtcTutorialInfoBar = null!;

    /// <summary>「放歌看效果」选择对话框是否正在显示（防重复弹出）。</summary>
    private bool _musicDialogShowing;
    /// <summary>「放歌看效果」选择对话框实例（点「我看到了」后自动关闭）。</summary>
    private ContentDialog? _musicDialog;
    /// <summary>当前打开的示例播放器窗口（点「我看到了」后自动关闭）。</summary>
    private FakePlayerWindow? _fakePlayerWindow;

    /// <summary>抑制教学推进（程序性修改教学控件时置 true，如 expand 回调里的开关复位）。</summary>
    private bool _suppressTutorialPush;

    /// <summary>教学句 Tag → 目标控件：句子一出现就自动滚到可视区（滚动守卫）。</summary>
    private readonly Dictionary<string, Control> _tutorialTargets = [];

    /// <summary>滚动守卫定时器：教程进行时周期性把当前句的目标滚进视野。</summary>
    private DispatcherTimer? _tutorialGuardTimer;

    /// <summary>
    /// 挂接 SMTC 教学的推进点：不自动开始（进阶教学，由设置页顶部 InfoBar 手动进入），
    /// 用户按教学指示操作对应控件时按 Tag 推进（改这里/JSON 里的 Tag 时务必两边同步）。
    /// 流程：莫奈取色（主题色）→ 背景动态取色 → 放歌看效果 → 动态频谱彩蛋 → 暂停恢复/过渡 → 阴影/边框。
    /// </summary>
    private void WireSmtcTutorial()
    {
        // 教程句 Tag → 目标控件：句子一出现就自动滚到可视区。
        // 滚动守卫在教程期间每 250ms 检查当前句 Tag，把对应目标 BringIntoView；
        // 这样无论句子是靠 push 还是按钮推进，目标都始终在视野内，用户无需手动滚动
        // （设置页无 TutorialBarrier，但 ModalTarget 的聚光灯会拦截滚轮，因此必须自动滚）。
        _tutorialTargets["toggle-theme"] = _dynamicThemeColor;
        _tutorialTargets["expand-bg"] = _backgroundGroup;
        _tutorialTargets["toggle-bg-dynamic"] = _dynamicBackgroundColor;
        _tutorialTargets["toggle-revert"] = _revertColorsWhenPaused;
        _tutorialTargets["transition"] = _albumColorTransition;
        _tutorialTargets["expand-shadow"] = _shadowGroup;
        _tutorialTargets["toggle-shadow"] = _dynamicShadowColor;
        _tutorialTargets["expand-border"] = _borderGroup;
        _tutorialTargets["toggle-border"] = _dynamicBorderColor;
        _tutorialTargets["smtc-tip"] = _smtcDynamicGroup;
        _tutorialTargets["wallpaper-tip"] = _wallpaperGroup;
        _tutorialTargets["expand-texture"] = _textureGroup;
        _tutorialTargets["set-spectrum"] = _backgroundTextureType;

        _tutorialGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tutorialGuardTimer.Tick += (_, _) => ScrollCurrentTutorialTarget();
        _tutorialGuardTimer.Start();

        // 第一步：莫奈取色（动态主题色）。
        WireTutorialToggle(_dynamicThemeColor, "toggle-theme");

        // 第二步：展开「底色填充」，为背景动态取色做准备。
        WireTutorialExpander(_backgroundGroup, "expand-bg", () =>
        {
            // 让组内「动态专辑封面取色」可点：打开底色填充；
            // 若该开关之前已开启则先复位为关，保证用户总是从「关 → 开」操作一遍，
            // 避免已开启时用户点一下反而关掉、教学无法推进（卡住）。
            _suppressTutorialPush = true;
            try
            {
                _customBackground.IsChecked = true;
                _dynamicBackgroundColor.IsChecked = false;
            }
            finally
            {
                _suppressTutorialPush = false;
            }
        });
        // 「放歌看效果」句由滚动守卫检测到后弹出播放器选择对话框（见 ScrollCurrentTutorialTarget）。
        WireTutorialToggle(_dynamicBackgroundColor, "toggle-bg-dynamic");

        // 暂停恢复 / 过渡时长。
        WireTutorialToggle(_revertColorsWhenPaused, "toggle-revert");
        _albumColorTransition.PropertyChanged += (_, e) =>
        {
            if (e.Property != NumericUpDown.ValueProperty || _suppressLivePreview)
            {
                return;
            }

            HostTutorial.PushToNextSentenceByTag("transition");
        };

        // 阴影 / 边框：先展开分组，再打开组内的「动态取色」。
        WireTutorialExpander(_shadowGroup, "expand-shadow", () => _shadow.IsChecked = true);
        WireTutorialToggle(_dynamicShadowColor, "toggle-shadow");
        WireTutorialExpander(_borderGroup, "expand-border", () => _border.IsChecked = true);
        WireTutorialToggle(_dynamicBorderColor, "toggle-border");

        // 动态频谱彩蛋：展开「底纹纹理」并自动打开开关（若已是频谱则重置为网格线，
        // 保证用户总是亲手把「纹理图案」改成「动态频谱」），再选「动态频谱」推进。
        WireTutorialExpander(_textureGroup, "expand-texture", () =>
        {
            _suppressTutorialPush = true;
            try
            {
                _backgroundTextureEnabled.IsChecked = true;
                if (Selected(_backgroundTextureType, BackgroundTexture.None) == BackgroundTexture.Spectrum)
                {
                    Select(_backgroundTextureType, BackgroundTextures, BackgroundTexture.Grid);
                }
            }
            finally
            {
                _suppressTutorialPush = false;
            }
        });
        _backgroundTextureType.SelectionChanged += (_, _) =>
        {
            if (_suppressLivePreview || _suppressTutorialPush)
            {
                return;
            }

            if (Selected(_backgroundTextureType, BackgroundTexture.None) == BackgroundTexture.Spectrum)
            {
                HostTutorial.PushToNextSentenceByTag("set-spectrum");
            }
        };
    }

    /// <summary>上一次滚动守卫看到的句子 Tag（用于只在句子刚切换时触发一次对话框）。</summary>
    private string? _previousGuardTag;

    /// <summary>
    /// 滚动守卫：把当前教学句的目标控件滚动到可视区域（无目标/无教学时无操作），
    /// 并在刚进入「放歌看效果」句时弹一次播放器选择对话框（用户取消后不重复弹）。
    /// </summary>
    private void ScrollCurrentTutorialTarget()
    {
        var tag = HostTutorial.GetCurrentSentenceTag();
        if (tag == null)
        {
            _previousGuardTag = null;
            // 教程结束/被跳过时兜底关闭播放器与对话框。
            if (_fakePlayerWindow != null || _musicDialog != null)
            {
                CloseFakePlayerAndDialog();
            }

            return;
        }

        // 离开「放歌/频谱」步骤块（放歌看效果 → 展开底纹 → 选动态频谱）时才关闭播放器与
        // 对话框；块内保持播放器开着，让动态频谱能实时看到歌声跳动的效果。
        if (IsMusicBlockTag(_previousGuardTag) && !IsMusicBlockTag(tag))
        {
            CloseFakePlayerAndDialog();
        }

        // 句子刚变成「放歌看效果」时弹一次选择对话框。
        if (tag == "play-music" && _previousGuardTag != "play-music")
        {
            ShowMusicChoiceDialog();
        }

        _previousGuardTag = tag;

        if (_tutorialTargets.TryGetValue(tag, out var target))
        {
            try
            {
                target.BringIntoView();
            }
            catch
            {
                // 控件已卸载等异常情况忽略。
            }
        }
    }

    /// <summary>是否属于「放歌/频谱」教学步骤块（块内保持示例播放器打开，让频谱实时演示）。</summary>
    private static bool IsMusicBlockTag(string? tag) =>
        tag is "play-music" or "expand-texture" or "set-spectrum";

    /// <summary>
    /// 开始 SMTC 进阶教学（设置页顶部 InfoBar 的「开始教学」按钮触发）：
    /// 先把教学要操作的开关全部复位为关（保证每个「打开 xxx」步骤都从关 → 开操作一遍），
    /// 再启动未完成的教程，并把「动态取色」组展开滚到视野内。
    /// 已有教程在进行中或教程已完成时不执行任何操作。
    /// </summary>
    private void StartSmtcTutorial()
    {
        if (HostTutorial.IsTutorialRunning() || HostTutorial.GetIsTutorialCompleted(SmtcTutorialPath))
        {
            return;
        }

        _suppressTutorialPush = true;
        try
        {
            _dynamicThemeColor.IsChecked = false;
            _revertColorsWhenPaused.IsChecked = false;
            _dynamicBackgroundColor.IsChecked = false;
            _dynamicShadowColor.IsChecked = false;
            _dynamicBorderColor.IsChecked = false;
        }
        finally
        {
            _suppressTutorialPush = false;
        }

        HostTutorial.BeginNotCompletedTutorials(SmtcTutorialPath);
        // 把「动态取色」组展开并滚到视野内，让教学气泡能指到它的设置项。
        _smtcDynamicGroup.IsExpanded = true;
        BringIntoViewLater(_smtcDynamicGroup);
        RefreshSmtcTutorialInfoBar();
    }

    /// <summary>刷新 SMTC 教学入口提示：系统支持 SMTC 且教程未完成时显示。</summary>
    private void RefreshSmtcTutorialInfoBar()
    {
        if (_smtcTutorialInfoBar == null)
        {
            return;
        }

        _smtcTutorialInfoBar.IsOpen =
            SystemCapabilities.SmtcAvailable && !HostTutorial.GetIsTutorialCompleted(SmtcTutorialPath);
    }

    /// <summary>
    /// 用户把开关切到「开」时按 Tag 推进当前教学句。
    /// 只在开启时推进：教学指示都是「打开 xxx」，若用户误关则不推进，
    /// 避免已开启的用户关掉开关后下一步效果无法演示。
    /// </summary>
    private void WireTutorialToggle(ToggleSwitch toggle, string tag, Action? after = null)
    {
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleSwitch.IsCheckedProperty || _suppressLivePreview || _suppressTutorialPush || toggle.IsChecked != true)
            {
                return;
            }

            HostTutorial.PushToNextSentenceByTag(tag);
            after?.Invoke();
        };
    }

    /// <summary>用户展开分组时按 Tag 推进当前教学句。</summary>
    private void WireTutorialExpander(SettingsExpander expander, string tag, Action? after = null)
    {
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != SettingsExpander.IsExpandedProperty || !expander.IsExpanded || _suppressLivePreview || _suppressTutorialPush)
            {
                return;
            }

            HostTutorial.PushToNextSentenceByTag(tag);
            after?.Invoke();
        };
    }

    /// <summary>延迟把目标控件滚动到可视区域（等分组展开后的布局完成）。</summary>
    private static void BringIntoViewLater(Control target)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    target.BringIntoView();
                }
                catch
                {
                    // 控件已卸载等异常情况忽略。
                }
            };
            timer.Start();
        });
    }

    /// <summary>
    /// 「放歌看效果」步骤：弹出示例播放器选择对话框，让用户选择
    /// 自己放歌 / 播放示例音乐 / 播放示例音乐（静音）/ 跳过播放。
    /// 仅当教程正停在放歌句（play-music）时弹出，避免误触发。
    /// </summary>
    private void ShowMusicChoiceDialog()
    {
        if (_musicDialogShowing || HostTutorial.GetCurrentSentenceTag() != "play-music")
        {
            return;
        }

        _musicDialogShowing = true;
        ContentDialog? dialog = null;
        dialog = new ContentDialog
        {
            Title = "放首歌试试效果",
            Content = BuildMusicChoicePanel(dialog),
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Closed += (_, _) =>
        {
            _musicDialogShowing = false;
            if (_musicDialog == dialog)
            {
                _musicDialog = null;
            }
        };
        _musicDialog = dialog;
        // 显式指定设置窗口为宿主：无参 ShowAsync() 会选当前激活的窗口，
        // 而教学期间激活的通常是 ClassIsland 主界面窗口，对话框就会跑错窗口。
        if (TopLevel.GetTopLevel(this) is Window host)
        {
            _ = dialog.ShowAsync(host);
        }
        else
        {
            _ = dialog.ShowAsync();
        }
    }

    private Control BuildMusicChoicePanel(ContentDialog? dialog)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "大部分音乐播放器和浏览器都支持这个功能，不用特定软件。挑一种方式放歌：",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        });
        panel.Children.Add(MusicOption(dialog, "我自己放歌", "在你的音乐软件里放一首有封面的歌", () => { }));
        panel.Children.Add(MusicOption(dialog, "播放示例音乐", "用 ClassIsland 音乐播放器播放一些音乐片段", OpenFakePlayer));
        panel.Children.Add(MusicOption(dialog, "跳过播放音乐", "直接继续教学", HostTutorial.TryStartNextSentence));
        return panel;
    }

    private static Button MusicOption(ContentDialog? dialog, string title, string description, Action action)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        button.Click += (_, _) =>
        {
            dialog?.Hide();
            action();
        };
        return button;
    }

    /// <summary>打开示例播放器（同一时刻只开一个）。</summary>
    private void OpenFakePlayer()
    {
        if (_fakePlayerWindow != null)
        {
            _fakePlayerWindow.Activate();
            return;
        }

        var window = new FakePlayerWindow();
        window.Closed += (_, _) => _fakePlayerWindow = null;
        _fakePlayerWindow = window;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>关闭「放歌」选择对话框与示例播放器（用户点「我看到了」/教程离开放歌句时）。</summary>
    private void CloseFakePlayerAndDialog()
    {
        _musicDialog?.Hide();
        _musicDialog = null;
        _musicDialogShowing = false;
        _fakePlayerWindow?.Close();
        _fakePlayerWindow = null;
    }

    private Control BuildContent()
    {
        var panel = new StackPanel
        {
            Classes = { "settings-container", "animated-intro" },
            Spacing = 4
        };

        panel.Children.Add(new IconText { Glyph = "\uEC4A", Text = "样式注入器", Margin = new Thickness(0, 0, 0, 4) });
        if (MainWindowStyleInjector.IsSeparatedMode())
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "检测到分体主界面",
                Message = "本插件暂不支持 ClassIsland 的分体主界面模式，请关闭分体主界面。",
                IsOpen = true,
                IsClosable = false,
                ActionButton = Button("去关闭分体主界面", OpenAppearanceSettings)
            });
        }
        if (MainWindowStyleInjector.IsMultiLineMode())
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Title = "检测到多行主界面",
                Message = "本插件有极少数功能不支持多行主界面，但插件仍可继续运行。",
                IsOpen = true,
                IsClosable = false
            });
        }
        _degradationInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "检测到宿主点位失效",
            Message = string.Empty,
            IsOpen = false,
            IsClosable = true
        };
        panel.Children.Add(_degradationInfoBar);
        RefreshHealthInfoBar();
        _pluginUpdateInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "插件版本过低",
            Message = string.Empty,
            IsOpen = false,
            IsClosable = true,
            ActionButton = LinkButton("去 GitHub 更新", Plugin.Manifest?.Url ?? "https://github.com/BSOD-MEMZ/ClassIslandInjector")
        };
        panel.Children.Add(_pluginUpdateInfoBar);
        UpdatePluginUpdateInfoBar(null);
        panel.Children.Add(Setting("\uE813", "实时预览", "开启后，下方对设置项的修改会立即保存并应用到主界面。", _livePreview));
        if (!SystemCapabilities.SmtcAvailable)
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "当前系统不支持 SMTC 动态取色",
                Message = $"检测到 Windows 版本过低（当前 build {Environment.OSVersion.Version.Build}，SMTC 需要 Windows 10 1809 / build 17763 或更高）。动态专辑取色、暂停恢复原色与 SMTC 专辑封面底图将无法工作，其余功能不受影响。",
                IsOpen = true,
                IsClosable = false
            });
        }

        panel.Children.Add(Setting("\uEDC7", "运行时注入", "启用后由插件接管主界面根节点的视觉效果。", _enabled));

        // SMTC 进阶教学入口：不自动弹出，未完成时用 InfoBar 提示用户可查看。
        _smtcTutorialInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "进阶教学：SMTC 动态取色",
            Message = "让主界面的颜色跟着正在播放的音乐变，想试一试吗？",
            IsOpen = false,
            IsClosable = true,
            ActionButton = Button("开始教学", StartSmtcTutorial)
        };
        panel.Children.Add(_smtcTutorialInfoBar);

        AddSection(panel, "\uF42F", "用户预设");
        panel.Children.Add(Setting("\uF42F", "保存当前为预设", "把插件当前全部设置项保存为一个命名预设（同名覆盖）", PresetSaveFooter()));
        panel.Children.Add(Setting("\uF42F", "套用 / 删除预设", "套用会把全部设置项替换为该预设保存时的状态。", PresetManageFooter()));
        panel.Children.Add(Setting("\uE0BD", "恢复插件默认", "把全部设置恢复为插件默认（不会修改 Overrides.axaml）", Button("恢复默认", ResetToDefaults)));

        AddSection(panel, "\uE51F", "背景");
        var backgroundColorItem = Item("背景色", "支持透明度的主界面背景颜色。", _backgroundColor);
        _backgroundGroup = SwitchableGroup("\uE520", "底色填充", "关闭时保留 ClassIsland 自身的背景颜色。", _customBackground,
            backgroundColorItem,
            Item("动态专辑封面取色", "读取当前 SMTC 专辑封面，并使用 Material You（Monet）算法自动提取主题色。", _dynamicBackgroundColor),
            Item("线性渐变", "开启后会使用渐变终止色。", _gradient),
            Item("渐变方向", "线性渐变从起始色到终止色的方向。", _gradientDirection, _gradient),
            Item("渐变终止色", "线性渐变背景的结束颜色。", _gradientEndColor, _gradient));
        _backgroundGroup.Name = "BackgroundGroup";
        _dynamicBackgroundColor.Name = "BackgroundDynamicToggle";
        EnabledWhenManualColor(backgroundColorItem, _customBackground, _dynamicBackgroundColor);
        panel.Children.Add(_backgroundGroup);
        var textureSizeItem = Item("纹理大小", "单个纹理单元的大小（像素）。", _backgroundTextureSize);
        var spectrumSensitivityItem = Item("频谱灵敏度", "动态频谱柱条的放大倍率（越大跳动越剧烈）。", _backgroundTextureSpectrumSensitivity);
        var spectrumBarsItem = Item("频谱柱条数", "主界面约 400 像素宽时的柱条数，柱条宽度保持恒定。", _backgroundTextureSpectrumBars);
        var spectrumMirroredItem = Item("双面对称", "同时向上和向下绘制镜像频谱。", _backgroundTextureSpectrumMirrored);
        var spectrumAutoWidthItem = Item("自动匹配宽度", "开启后柱条数随主界面宽度自动增减（柱宽恒定）。", _backgroundTextureSpectrumAutoWidth);
        _textureGroup = SwitchableGroup("\uE92B", "底纹纹理", "在底色之上叠加可平铺的纹理图案。", _backgroundTextureEnabled,
            Item("纹理图案", "选择填充纹理的类型。", _backgroundTextureType),
            Item("纹理颜色", "支持透明度的纹理线条颜色。", _backgroundTextureColor),
            textureSizeItem,
            spectrumSensitivityItem,
            spectrumBarsItem,
            spectrumMirroredItem,
            spectrumAutoWidthItem);
        _textureGroup.Name = "TextureGroup";
        _backgroundTextureType.Name = "BackgroundTextureType";
        _backgroundTextureEnabled.Name = "BackgroundTextureEnabled";
        panel.Children.Add(_textureGroup);
        AutoSelectOnEnable(_backgroundTextureEnabled, _backgroundTextureType, BackgroundTextures);
        // 动态频谱不使用纹理单元大小：选中频谱时隐藏该项。
        VisibleWhenNotAny(textureSizeItem, _backgroundTextureType, BackgroundTexture.Spectrum);
        VisibleWhen(spectrumSensitivityItem, _backgroundTextureType, BackgroundTexture.Spectrum);
        VisibleWhen(spectrumBarsItem, _backgroundTextureType, BackgroundTexture.Spectrum);
        VisibleWhen(spectrumMirroredItem, _backgroundTextureType, BackgroundTexture.Spectrum);
        VisibleWhen(spectrumAutoWidthItem, _backgroundTextureType, BackgroundTexture.Spectrum);
        var wallpaperPathItem = Item("图片 / 文件夹", "底图文件或幻灯片文件夹的路径。", WallpaperPathFooter());
        var wallpaperSlideshowItem = Item("幻灯片间隔", "文件夹幻灯片切换间隔（秒）。", _wallpaperSlideshowInterval);
        _wallpaperModeInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "已启用专家模式！",
            Message = string.Empty,
            IsOpen = false,
            IsClosable = false,
            ActionButton = Button("恢复简单模式", DisableWallpaperDesigner)
        };
        panel.Children.Add(_wallpaperModeInfoBar);
        _wallpaperGroup = SwitchableGroup("\uF42D", "背景图片", "为 ClassIsland 主界面添加背景图片", _wallpaperEnabled,
            Item("编辑模式", "想象一下我们把 Photoshop 搬到 ClassIsland！试试全新专家模式！", _wallpaperModeBox),
            _wallpaperEditorItem = Item("打开图层编辑器", "给设计大师的超级编辑器", Button("打开编辑器", OpenWallpaperLayerEditor)),
            _wallpaperSourceItem = Item("图片来源", "选择底图的来源。", _wallpaperSource),
            _wallpaperPathItem = wallpaperPathItem,
            _wallpaperOpacityItem = Item("图片不透明度", "底图的整体透明度。", _wallpaperOpacity),
            _wallpaperDisplayModeItem = Item("显示方式", "图片在主界面内的显示方式。", _wallpaperDisplayMode),
            _wallpaperScaleItem = Item("缩放", "底图的缩放倍率（1 为按显示方式适应，大于 1 放大裁剪）", _wallpaperScale),
            _wallpaperOffsetXItem = Item("水平偏移", "底图的水平偏移（相对图片宽度，-0.5 到 0.5）", _wallpaperOffsetX),
            _wallpaperOffsetYItem = Item("垂直偏移", "底图的垂直偏移（相对图片高度，-0.5 到 0.5）", _wallpaperOffsetY),
            _wallpaperBlurItem = Item("模糊", "对底图应用高斯模糊（0 为关闭）", _wallpaperBlur),
            _wallpaperSlideshowItem = wallpaperSlideshowItem);
        _wallpaperGroup.Name = "WallpaperGroup";
        _wallpaperSource.Name = "WallpaperSource";
        // 图片来源决定「图片/文件夹」与「幻灯片间隔」行的显隐；编辑模式切换时整体显隐
        // 由 UpdateWallpaperModeVisibility 统一处理。
        _wallpaperSource.SelectionChanged += (_, _) => UpdateWallpaperModeVisibility();
        _wallpaperModeBox.SelectionChanged += (_, _) =>
        {
            if (!_suppressLivePreview)
            {
                var designer = Selected(_wallpaperModeBox, false);
                if (InjectorRuntime.Settings.WallpaperDesignerEnabled != designer)
                {
                    // 切换模式立即持久化并应用（触发 Changed → SaveAndApply）。
                    InjectorRuntime.Settings.WallpaperDesignerEnabled = designer;
                }
            }

            UpdateWallpaperModeVisibility();
        };
        panel.Children.Add(_wallpaperGroup);
        UpdateWallpaperModeVisibility();
        _smtcDynamicGroup = Group("\uE51E", "动态取色", "从音乐软件或浏览器获取 SMTC 信息，并进行莫奈取色",
            Item("暂停/停止时恢复原色", "媒体暂停或停止播放时，从专辑取色平滑恢复为原始颜色，恢复播放后再跟随专辑。", _revertColorsWhenPaused),
            Item("动态修改主题色", "从当前专辑封面取色并动态修改 ClassIsland 全局主题强调色。", _dynamicThemeColor),
            Item("兜底刷新间隔", "事件驱动失效时的兜底刷新间隔（秒）。", _albumColorPollingInterval),
            Item("颜色过渡时长", "专辑颜色变化时，背景、边框、阴影平滑过渡到新颜色的时长（秒），0 为立即切换。", _albumColorTransition),
            Item("这是什么？", "了解 SMTC 与动态取色是如何工作的。", Button("查看工作原理", ShowSmtcExplanation)));
        _smtcDynamicGroup.Name = "SmtcDynamicGroup";
        _revertColorsWhenPaused.Name = "SmtcRevertPausedToggle";
        _dynamicThemeColor.Name = "SmtcThemeColorToggle";
        _albumColorTransition.Name = "SmtcTransitionSpin";
        panel.Children.Add(_smtcDynamicGroup);

        AddSection(panel, "\uE254", "边框与阴影");
        var shadowColorItem = Item("阴影颜色", "支持透明度的阴影颜色。", _shadowColor);
        _shadowGroup = SwitchableGroup("\uE472", "阴影", "为 ClassIsland 添加投影效果。", _shadow,
            Item("动态取色", "阴影色调跟随专辑封面莫奈取色。", _dynamicShadowColor),
            shadowColorItem,
            Item("阴影模糊", "控制投影的柔和程度。", _shadowBlur),
            Item("阴影水平偏移", "控制投影向左或向右偏移。", _shadowOffsetX),
            Item("阴影垂直偏移", "控制投影向上或向下偏移。", _shadowOffsetY),
            Item("阴影不透明度", "控制投影的深浅。", _shadowOpacity));
        _shadowGroup.Name = "ShadowGroup";
        _dynamicShadowColor.Name = "ShadowDynamicToggle";
        EnabledWhenManualColor(shadowColorItem, _shadow, _dynamicShadowColor);
        panel.Children.Add(_shadowGroup);
        var borderColorItem = Item("边框颜色", "支持透明度的边框颜色。", _borderColor);
        _borderGroup = SwitchableGroup("\uE254", "边框", "为 ClassIsland 添加细边框。", _border,
            Item("动态取色", "边框色调跟随专辑封面莫奈取色。", _dynamicBorderColor),
            borderColorItem,
            Item("边框线宽", "控制边框的粗细。", _borderThickness));
        _borderGroup.Name = "BorderGroup";
        _dynamicBorderColor.Name = "BorderDynamicToggle";
        EnabledWhenManualColor(borderColorItem, _border, _dynamicBorderColor);
        panel.Children.Add(_borderGroup);

        AddSection(panel, "\uE82B", "动画");
        panel.Children.Add(SwitchableGroup("\uEDB9", "持续动画", "打开后才会使用下方的循环动画设置。", _animationEnabled,
            Item("动画类型", "选择循环动画的运动方式。", _animationMode),
            Item("动画幅度", "控制循环动画的强弱。", _animationAmount),
            Item("动画周期", "完成一次循环所需的时间（秒）。", _animationPeriod)));
        AutoSelectOnEnable(_animationEnabled, _animationMode, IslandAnimationModes);
        panel.Children.Add(SwitchableGroup("\uEFED", "主界面显示动画", "选择主界面出现或消失时使用的动画。", _visibilityAnimationEnabled,
            Item("动画类型", "选择主界面出现或消失时使用的动画。", _visibilityAnimation),
            Item("显示动画时长", "主界面显示动画的时长（秒）。", _visibilityDuration)));
        AutoSelectOnEnable(_visibilityAnimationEnabled, _visibilityAnimation, VisibilityAnimations);
        panel.Children.Add(SwitchableGroup("\uEFED", "列表翻页动画", "自定义 ClassIsland 列表/轮播容器的上翻切换动画（轮播容器、上课提醒横幅等）。", _carouselAnimation,
            Item("动画类型", "列表切换时的动画方式。", _carouselAnimationType),
            Item("动画时长", "单次翻页动画的时长（秒）。", _carouselAnimationDuration),
            Item("翻页距离", "滑动/上翻类动画的位移距离（像素）。", _carouselAnimationOffset)));

        AddSection(panel, "\uE025", "提醒");
        panel.Children.Add(Setting("\uEFFE", "预览提醒", "一次性预览强调动画、遮罩过渡与 Ripple 效果。", Button("预览提醒", PreviewNotification)));
        panel.Children.Add(SwitchableGroup("\uE02B", "提醒强调动画", "选择收到提醒时使用的强调效果。", _emphasisAnimationEnabled,
            Item("动画类型", "选择收到提醒时使用的强调效果。", _emphasisAnimation),
            Item("强调幅度", "控制强调动画的强弱。", _emphasisAmount),
            Item("强调时长", "提醒强调动画的时长（秒）。", _emphasisDuration)));
        AutoSelectOnEnable(_emphasisAnimationEnabled, _emphasisAnimation, EmphasisAnimations);
        panel.Children.Add(SwitchableGroup("\uE833", "提醒遮罩动画", "选择提醒遮罩出现和消失时的过渡效果。", _notificationTransitionEnabled,
            Item("过渡类型", "选择提醒遮罩出现和消失时的过渡效果。", _notificationTransition),
            Item("遮罩动画时长", "提醒遮罩动画的时长（秒）。", _notificationTransitionDuration)));
        AutoSelectOnEnable(_notificationTransitionEnabled, _notificationTransition, NotificationTransitions);
        var rippleColorItem = Item("Ripple 颜色", "支持透明度的提醒扩散颜色。", _rippleColor);
        var rippleDurationItem = Item("Ripple 时长", "扩散效果的播放时长（秒）。", _rippleDuration);
        var rippleThicknessItem = Item("Ripple 线宽", "线性 Ripple 的线条粗细。", _rippleThickness);
        var rippleOpacityItem = Item("全局不透明度", "全局降低 Ripple 效果的透明度，避免上课时分心。", _rippleOpacity);
        var rippleConstraintItem = Item("限制扩散范围", "以主界面中心为圆心创建圆形裁剪，约束所有类型 Ripple 的扩散范围。", _rippleConstraint);
        // 约束半径的显隐由下方 VisibleWhenNotAny 单一控制（约束开关开且类型非 Cinematic），
        // 不再用 Item 的 dependency 参数，避免两套逻辑同时写 IsEnabled/IsVisible 互相覆盖。
        var rippleConstraintRadiusItem = Item("约束半径", "Ripple 扩散的圆形约束半径（像素），0 为自动按主界面大小计算。", _rippleConstraintRadius);
        // 屏幕涟漪（高级）的专属设置，随 Ripple 组一起展开，仅选中「屏幕涟漪」时可用。
        var cinematicShakeItem = Item("晃动幅度", "提醒时画面晃动的最远位移（像素），0 为关闭晃动。", _cinematicShake);
        var cinematicBlurItem = Item("模糊半径", "起始模糊半径。", _cinematicBlur);
        var cinematicFlashItem = Item("闪光强度", "中心白光的亮度扩散强度，0 为关闭闪光。", _cinematicFlash);
        var rippleGroup = SwitchableGroup("\uEFFF", "提醒 Ripple", "选择提醒时的扩散效果，高级特效视觉效果更强。", _rippleEnabled,
            Item("Ripple 类型", "选择提醒时的扩散效果。", _rippleType),
            rippleColorItem, rippleDurationItem, rippleThicknessItem, rippleOpacityItem, rippleConstraintItem, rippleConstraintRadiusItem,
            cinematicShakeItem, cinematicBlurItem, cinematicFlashItem);
        VisibleWhenNotAny(rippleColorItem, _rippleType, RippleType.Hanabi, RippleType.Explode, RippleType.Cinematic);
        VisibleWhenNotAny(rippleThicknessItem, _rippleType, RippleType.Hanabi, RippleType.Explode, RippleType.Particle, RippleType.Cinematic);
        VisibleWhenNotAny(rippleConstraintItem, _rippleType, RippleType.Cinematic);
        VisibleWhenNotAny(rippleConstraintRadiusItem, _rippleType, _rippleConstraint, RippleType.Cinematic);
        VisibleWhen(cinematicShakeItem, _rippleType, RippleType.Cinematic);
        VisibleWhen(cinematicBlurItem, _rippleType, RippleType.Cinematic);
        VisibleWhen(cinematicFlashItem, _rippleType, RippleType.Cinematic);
        AutoSelectOnEnable(_rippleEnabled, _rippleType, RippleTypes);
        panel.Children.Add(rippleGroup);
        var hanabiInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "关于舞萌花火（Hanabi）效果",
            Message = "受当前技术限制，本插件无法实现类似 maimai でらっくす 的带光影的烟花效果，只能仿制经典旧版烟花效果。",
            IsOpen = true,
            IsClosable = false
        };
        VisibleWhen(hanabiInfoBar, _rippleType, RippleType.Hanabi);
        panel.Children.Add(hanabiInfoBar);
        panel.Children.Add(SwitchableGroup("\uE85E", "全屏流光", "仿照手机智慧识屏或语音助手激活时的全屏内发光效果，可与上方任意 Ripple 效果叠加播放。", _marqueeEnabled,
            Item("流光颜色", "流光的整体色调；纯白为完整彩虹，带色调会整体偏向该颜色。", _marqueeColor),
            Item("流光时长", "流光效果的播放时长（秒）。", _marqueeDuration),
            Item("流光不透明度", "流光效果的整体透明度。", _marqueeOpacity),
            Item("旋转速度", "彩虹沿边框旋转的速度（每秒圈数）。", _marqueeSpeed),
            Item("边框厚度", "发光边框的粗细（相对屏幕短边的比例）。", _marqueeFrameThickness)));
        AddSection(panel, "\uE4C4", "即将上课样式");
        panel.Children.Add(Setting("\uE4C4", "预览即将上课样式", "立即预览 5 秒即将上课动画，别忘了先在下面选中一个特效。", Button("预览", PreviewPrepareOnClass)));
        panel.Children.Add(SwitchableGroup("\uE4C4", "即将上课样式", "选择即将上课倒计时期间显示的特效。", _prepareOnClassEnabled,
            Item("样式", "选择即将上课倒计时期间显示的特效。", _prepareOnClassStyle)));
        AutoSelectOnEnable(_prepareOnClassEnabled, _prepareOnClassStyle, PrepareOnClassStyles);
        var arrowGroup = Group("\uE0F7", "箭头", "斜向箭头从右向左滑动。",
            Item("箭头颜色", "支持透明度的箭头颜色。", _countdownArrowColor),
            Item("箭头组数", "屏幕上同时滑动的箭头组数量。", _countdownArrowCount),
            Item("每组箭头数", "每组内包含的箭头数量。", _countdownArrowPerGroup),
            Item("组内箭头间距", "同一组内相邻箭头之间的距离（像素）。", _countdownArrowSpacing),
            Item("组间间距", "相邻箭头组之间的额外间距（像素）。", _countdownArrowGroupSpacing),
            Item("滑动速度", "箭头的移动速度。", _countdownArrowSpeed),
            Item("箭头线宽", "箭头的线条粗细。", _countdownArrowThickness));
        var pulseGroup = Group("\uEE35", "扩散光环", "从主界面中心向外扩散并淡出的圆环。",
            Item("光环颜色", "支持透明度的光环颜色。", _countdownPulseColor),
            Item("光环线宽", "光环的线条粗细。", _countdownPulseThickness),
            Item("扩散速度", "每秒扩散的圈数。", _countdownPulseSpeed),
            Item("最大半径", "光环最大半径占主界面宽高中较小值的比例。", _countdownPulseMaxRadius));
        var scanGroup = Group("\uEECD", "扫描线", "一道带渐变尾迹的光线扫过主界面。",
            Item("扫描方向", "横向为水平线上下扫，纵向为竖直线左右扫。", _countdownScanDirection),
            Item("渐变尾迹", "关闭后只显示一条主线，不带渐变尾迹。", _countdownScanTailEnabled),
            Item("扫描颜色", "支持透明度的扫描线颜色。", _countdownScanColor),
            Item("扫描线宽", "扫描线的粗细。", _countdownScanThickness),
            Item("扫描速度", "每秒扫描次数。", _countdownScanSpeed));
        var lightBandGroup = Group("\uE989", "光带", "一条柔和的、非线性运动的光带扫过主界面，如同光照反光。",
            Item("光带颜色", "支持透明度的光带颜色。", _countdownLightBandColor),
            Item("光带粗细", "光带厚度（相对主界面宽高较大者的比例）。", _countdownLightBandThickness),
            Item("光带角度", "光带的倾斜角度（度）。", _countdownLightBandAngle),
            Item("扫过速度", "每秒扫过主界面的次数。", _countdownLightBandSpeed));
        var warningGroup = SwitchableGroup("\uE024", "即将上课警告", "注意：本效果较为恐怖，且会阻断鼠标和触摸输入，请谨慎使用！", _prepareWarningEnabled,
            Item("警告颜色", "支持透明度的警告内发光颜色。", _prepareWarningColor),
            Item("提前触发秒数", "距上课剩余秒数小于该值时显示警告。", _prepareWarningTriggerSeconds),
            Item("闪动速度", "警告每秒闪动的次数。", _prepareWarningFlashSpeed),
            Item("闪动幅度", "闪动时亮度起伏的深度，0 为常亮。", _prepareWarningFlashAmount),
            Item("边框厚度", "发光边框的粗细（相对屏幕短边的比例）。", _prepareWarningFrameThickness),
            Item("透明度", "警告效果的整体透明度。", _prepareWarningOpacity));
        VisibleWhenEnabledAnd(arrowGroup, _prepareOnClassEnabled, _prepareOnClassStyle, PrepareOnClassStyle.Arrows);
        VisibleWhenEnabledAnd(pulseGroup, _prepareOnClassEnabled, _prepareOnClassStyle, PrepareOnClassStyle.PulseRing);
        VisibleWhenEnabledAnd(scanGroup, _prepareOnClassEnabled, _prepareOnClassStyle, PrepareOnClassStyle.Scanline);
        VisibleWhenEnabledAnd(lightBandGroup, _prepareOnClassEnabled, _prepareOnClassStyle, PrepareOnClassStyle.LightBand);
        panel.Children.Add(arrowGroup);
        panel.Children.Add(pulseGroup);
        panel.Children.Add(scanGroup);
        panel.Children.Add(lightBandGroup);
        panel.Children.Add(warningGroup);

        AddSection(panel, "\uE5C1", "交互");
        panel.Children.Add(Setting("\uE5C1", "鼠标悬停保持可见", "注意：此功能可能会破坏 ClassIsland。", _mouseHoverKeepVisible));
        var clickEffectGroup = SwitchableGroup("\uE5C1", "主界面点击特效", "注意：此功能可能会破坏 ClassIsland，且大概率不工作。", _clickEffectEnabled,
            Item("特效类型", "选择点击特效的样式。", _clickEffectType));
        AutoSelectOnEnable(_clickEffectEnabled, _clickEffectType, ClickEffectTypes);
        panel.Children.Add(clickEffectGroup);

        AddSection(panel, "\uE4DC", "虚假天气");
        panel.Children.Add(SwitchableGroup("\uE4DC", "虚假天气", "向 ClassIsland 注入自定义天气数据", _fakeWeatherEnabled,
            Item("天气类型", "选择要显示的天气。", _fakeWeatherCode),
            Item("温度", "虚假天气的温度（℃）。", _fakeWeatherTemperature),
            Item("体感温度", "体感温度（℃）。", _fakeWeatherFeelsLike),
            Item("湿度", "相对湿度（%）。", _fakeWeatherHumidity),
            Item("气压", "大气压（hPa）。", _fakeWeatherPressure),
            Item("能见度", "能见度（km）。", _fakeWeatherVisibility),
            Item("风向", "如「东风」「西北风」。", _fakeWeatherWindDirection),
            Item("风力", "如「2级」「3-4级」。", _fakeWeatherWindScale),
            Item("空气质量 AQI", "AQI 数值（0-500），越高污染越重。", _fakeWeatherAqi),
            Item("预警图标", "预警图标等级；选择后会显示「图标+类型」胶囊，无则不显示图标。", _fakeWeatherAlertIcon),
            Item("预警类型", "预警胶囊里显示的类型文字，如「暴雨」。", _fakeWeatherAlertType),
            Item("预警等级", "如「蓝色预警」，可留空。", _fakeWeatherAlertLevel),
            Item("预警标题", "用于天气规则匹配的完整标题，如「xx市气象台发布暴雨蓝色预警」。", _fakeWeatherAlertTitle),
            Item("预警详情", "预警详细内容，可留空。", _fakeWeatherAlertDetail),
            Item("降水提醒", "距降雨开始分钟数（正值）；负值表示正在下雨、预计该分钟后停；0 为无降水。", _fakeWeatherRainRemainingMinutes)));
        AutoSelectOnEnable(_fakeWeatherEnabled, _fakeWeatherCode, FakeWeatherCodes);

        AddSection(panel, "\uF263", "高级样式表");
        panel.Children.Add(Setting("\uF263", "覆盖样式表路径", "填写 .axaml 样式表的完整路径。", _styleSheetPath));
        panel.Children.Add(Setting("\uE161", "自动热重载", "保存样式表后自动重新加载。", _watchStyleSheet));
        panel.Children.Add(new IconText
        {
            Glyph = "\uF431",
            Text = "危险区域",
            Margin = new Thickness(0, 16, 0, 4)
        });
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "危险区域",
            Message = "以下操作会直接修改主界面外观或清除插件数据，请谨慎使用。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Setting("\uE288", "打开可视化编辑器", "在独立窗口中像做 PPT 一样编辑 ClassIsland 主界面样式，但存在严重兼容性问题，已被弃用。", Button("打开编辑器", OpenVisualEditor)));
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "与 ClassIsland 原生设置重叠",
            Message = "不透明度、缩放与位置可在 ClassIsland 的外观页修改，在此覆盖可能与原生设置产生少量兼容性问题。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Group("\uE113", "基础变形", "这些值会覆盖并叠加在 ClassIsland 的主界面外观设置之上。",
            Item("不透明度", "控制主界面的整体透明度。", _opacity),
            Item("水平偏移", "向左或向右移动主界面。", _offsetX),
            Item("垂直偏移", "向上或向下移动主界面。", _offsetY),
            Item("圆角半径", "注意：在此处修改圆角有时需要重启 ClassIsland 才会生效", _cornerRadius),
            Item("旋转角度", "以中心点旋转主界面。", _rotation)));

        panel.Children.Add(Setting("\uE61D", "删除所有数据", "一键清空插件全部数据并恢复主界面，让插件回到“全新安装”状态，之后可安全卸载。", Button("删除所有数据", DeleteAllData)));

        AddSection(panel, "\uE761", "宿主对照表");
        UpdateContractCurrent();
        panel.Children.Add(Setting("\uE761", "当前状态", "ClassIsland 宿主版本与当前生效的对照表。", _contractCurrent));
        panel.Children.Add(Setting("\uE761", "可用对照表", "刷新后连接到 xxtsoft，列出所有可用对照表。", ContractTableFooter()));
        panel.Children.Add(Setting("\uE905", "查看 xxtsoft 服务状态", "检查服务是否在线。", LinkButton("打开 xxtsoft.top", "https://xxtsoft.top")));
        panel.Children.Add(_contractStatus);

        AddSection(panel, "\uE2C8", "调试");
        panel.Children.Add(Setting("\uE2C8", "启动时自动打开", "调试用：ClassIsland 启动后自动打开指定位置。", _startupOpenTarget));
        panel.Children.Add(Setting("\uE817", "降低视觉负担", "隐藏设置项的说明文字，只保留名称，减少视觉干扰。", _reduceVisualBurden));
        panel.Children.Add(Setting("\uEF25", "关闭版本检查和提醒", "不再检查插件版本，也不提示去 GitHub 更新。", _disableVersionCheck));
        panel.Children.Add(Setting("\uEF4F", "关闭宿主定位点失效检查", "不再检测宿主点位失效，也不显示降级提示。", _disableDegradationCheck));
        panel.Children.Add(Setting("\uE480", "输出诊断日志", "写入日志文件用于排查问题。", _diagnosticLogging));

        AddSection(panel, "\uE9E4", "关于");
        var manifest = Plugin.Manifest;
        panel.Children.Add(new SettingsExpander
        {
            IconSource = new FluentIconSource("\uE9E4"),
            Header = manifest?.Name ?? "ClassIsland 样式注入器",
            Description = manifest?.Description ?? "以运行时注入和可热重载 Avalonia 样式表深度重塑 ClassIsland 主界面。",
            IsExpanded = true,
            Footer = new TextBlock
            {
                Text = $"版本 {manifest?.Version ?? "未知"}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            },
            Items =
            {
                new SettingsExpanderItem
                {
                    Content = "作者",
                    Description = manifest?.Author ?? "未知",
                    Footer = string.IsNullOrEmpty(manifest?.Url) ? null : LinkButton("项目主页", manifest.Url)
                },
                new SettingsExpanderItem
                {
                    Content = "依赖",
                    Description = $"插件 ID：{manifest?.Id ?? "未知"} · 目标 ClassIsland API：{manifest?.ApiVersion ?? "未知"}"
                },
                new SettingsExpanderItem
                {
                    Content = "对一切违规补课和提前开学致以最强烈的谴责",
                    Footer = LinkButton("加入我们的行动", "https://xxtsoft.top/support/sekai/rescue")                       
                }
            }
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Button("保存并应用", SaveAndApply));
        actions.Children.Add(Button("重载样式表", ReloadStyleSheet));
        actions.Children.Add(Button("重启 ClassIsland", RestartClassIsland));
        panel.Children.Add(actions);
        panel.Children.Add(_status);
        return new ScrollViewer { Content = panel };
    }

    private void ResetToDefaults()
    {
        InjectorRuntime.Settings.ResetToDefaults();
        LoadFromSettings();
        _status.Text = "已恢复插件默认设置。";
    }

    private void SaveCurrentPreset()
    {
        SaveAndApply();
        var name = _presetName.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _status.Text = "请输入预设名称。";
            return;
        }

        InjectorRuntime.SavePreset(name);
        RefreshUserPresets();
        _presetName.Text = string.Empty;
        _status.Text = $"已把当前全部设置保存为预设“{name}”。";
    }

    private void ApplyUserPreset()
    {
        if (_userPresetList.SelectedItem is not string name)
        {
            _status.Text = "请先选择一个预设。";
            return;
        }

        if (InjectorRuntime.ApplyPreset(name))
        {
            LoadFromSettings();
            _status.Text = $"已套用预设“{name}”。";
        }
        else
        {
            RefreshUserPresets();
            _status.Text = $"预设“{name}”不存在，已刷新列表。";
        }
    }

    private void DeleteUserPreset()
    {
        if (_userPresetList.SelectedItem is not string name)
        {
            _status.Text = "请先选择一个预设。";
            return;
        }

        InjectorRuntime.DeletePreset(name);
        RefreshUserPresets();
        _status.Text = $"已删除预设“{name}”。";
    }

    private void RefreshUserPresets()
    {
        var names = InjectorRuntime.GetPresetNames();
        var selected = _userPresetList.SelectedItem as string;
        _userPresetList.ItemsSource = names;
        _userPresetList.SelectedItem = names.FirstOrDefault(n => n == selected) ?? (names.Count > 0 ? names[0] : null);
    }

    // ============ 宿主对照表 ============

    private Control ContractTableFooter() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            _contractTableList,
            Button("刷新列表", RefreshContractTables),
            Button("下载并应用", DownloadAndApplyContract),
            Button("恢复内置", RestoreBuiltInContract)
        }
    };

    /// <summary>从插件网站索引获取可用对照表列表，并按当前宿主版本自动选中适配项（版本区间仅供参考）。</summary>
    private async void RefreshContractTables()
    {
        _contractStatus.Text = "正在获取对照表列表…";
        try
        {
            var index = await ContractCatalogService.FetchIndexAsync(ContractCatalogService.DefaultIndexUrl);
            if (index.Tables.Count == 0)
            {
                _contractTableList.ItemsSource = null;
                _contractStatus.Text = "索引中没有找到任何对照表。";
                return;
            }

            _contractTableList.ItemsSource = index.Tables;
            _contractTableList.SelectedItem = index.Tables.FirstOrDefault(EntryMatchesHost) ?? index.Tables[0];
            _contractStatus.Text = index.Tables.Any(EntryMatchesHost)
                ? $"共找到 {index.Tables.Count} 个对照表，已自动选中适配当前宿主（ClassIsland {InjectorRuntime.HostVersion}）的项目。"
                : $"共找到 {index.Tables.Count} 个对照表，没有适配当前宿主（ClassIsland {InjectorRuntime.HostVersion}）的项，请手动选择。";
            UpdatePluginUpdateInfoBar(_contractTableList.SelectedItem as ContractIndexEntry);
        }
        catch (Exception ex)
        {
            _contractStatus.Text = $"获取对照表列表失败：{ex.Message}";
        }
    }

    /// <summary>下载并应用选中的对照表，然后按新点位重新注入。</summary>
    private async void DownloadAndApplyContract()
    {
        if (_contractTableList.SelectedItem is not ContractIndexEntry entry)
        {
            _contractStatus.Text = "请先在列表中选择要下载的对照表。";
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            _contractStatus.Text = $"「{entry.Name}」缺少下载地址。";
            return;
        }

        _contractStatus.Text = $"正在下载「{entry.Name}」…";
        try
        {
            var catalog = await ContractCatalogService.DownloadAsync(entry.Url);
            SaveAndApply();
            InjectorRuntime.ApplyContractCatalog(catalog);
            RefreshContractUi();
            _contractStatus.Text = $"已应用对照表「{catalog.Name}」并重新注入，当前宿主版本 ClassIsland {InjectorRuntime.HostVersion}。";
        }
        catch (Exception ex)
        {
            _contractStatus.Text = $"下载或应用对照表失败：{ex.Message}";
        }
    }

    /// <summary>恢复内置对照表。</summary>
    private void RestoreBuiltInContract()
    {
        SaveAndApply();
        InjectorRuntime.ApplyContractCatalog(ContractCatalog.BuiltIn);
        RefreshContractUi();
        _contractStatus.Text = "已恢复内置对照表。";
    }

    /// <summary>刷新对照表区域显示（当前状态、下拉选中、顶部降级提示、插件版本过低提示）。</summary>
    private void RefreshContractUi()
    {
        UpdateContractCurrent();
        RefreshHealthInfoBar();
        UpdatePluginUpdateInfoBar(_contractTableList.SelectedItem as ContractIndexEntry);
        if (_contractTableList.ItemsSource is IEnumerable<ContractIndexEntry> tables)
        {
            _contractTableList.SelectedItem = tables.FirstOrDefault(t => t.Id == ContractCatalogService.Current.Id);
        }
    }

    private void UpdateContractCurrent()
    {
        var current = ContractCatalogService.Current;
        _contractCurrent.Text = ContractCatalogService.IsBuiltIn
            ? $"ClassIsland {InjectorRuntime.HostVersion} · 当前对照表：内置默认（随插件版本）"
            : $"ClassIsland {InjectorRuntime.HostVersion} · 当前对照表：{current.Name}（{current.Id}）· {current.Author}";
    }

    /// <summary>
    /// 降低视觉负担：隐藏全部设置项/卡片的说明文字，只保留名称；关闭时恢复原始说明。
    /// </summary>
    private void ApplyVisualBurdenReduction()
    {
        var reduced = _reduceVisualBurden.IsChecked == true;
        foreach (var expander in _allExpanders)
        {
            if (reduced)
            {
                _savedExpanderDescriptions.TryAdd(expander, expander.Description);
                expander.Description = null;
            }
            else if (_savedExpanderDescriptions.Remove(expander, out var saved))
            {
                expander.Description = saved;
            }
        }

        foreach (var item in _allItems)
        {
            if (reduced)
            {
                _savedItemDescriptions.TryAdd(item, item.Description);
                item.Description = null;
            }
            else if (_savedItemDescriptions.Remove(item, out var saved))
            {
                item.Description = saved;
            }
        }
    }

    /// <summary>按健康检查结果刷新顶部「宿主点位失效」InfoBar。</summary>
    private void RefreshHealthInfoBar()
    {
        if (_degradationInfoBar == null)
        {
            return;
        }

        // 用户关闭了宿主点位失效检查：隐藏提示且不重新检测。
        if (_disableDegradationCheck.IsChecked == true)
        {
            _degradationInfoBar.IsOpen = false;
            return;
        }

        // 每次刷新时重跑健康检查，反映最新对照表与宿主状态。
        ContractCatalogService.RunHealthCheck();
        var degradations = ContractCatalogService.Degradations;
        if (degradations.Count == 0)
        {
            _degradationInfoBar.IsOpen = false;
            return;
        }

        var features = string.Join("、", degradations.Select(d => d.Feature).Distinct().Take(8));
        _degradationInfoBar.Title = $"检测到 {degradations.Count} 处宿主点位失效";
        _degradationInfoBar.Message =
            $"当前对照表与 ClassIsland {InjectorRuntime.HostVersion} 不完全匹配，以下功能可能已降级：{features}。" +
            "请到下方「宿主对照表」下载适配当前版本的对照表；插件其余功能仍可继续运行。";
        _degradationInfoBar.IsOpen = true;
    }

    /// <summary>
    /// 按所选对照表的最低插件版本要求刷新顶部「插件版本过低」InfoBar；
    /// 仅提醒不拦截——用户不更新也可继续使用当前版本。留空表示无限制，不提示。
    /// </summary>
    private void UpdatePluginUpdateInfoBar(ContractIndexEntry? entry)
    {
        if (_pluginUpdateInfoBar == null)
        {
            return;
        }

        // 用户关闭了版本检查和提醒。
        if (_disableVersionCheck.IsChecked == true)
        {
            _pluginUpdateInfoBar.IsOpen = false;
            return;
        }

        var minText = entry?.MinPluginVersion;
        if (string.IsNullOrWhiteSpace(minText) || !Version.TryParse(minText, out var minVersion))
        {
            _pluginUpdateInfoBar.IsOpen = false;
            return;
        }

        var currentText = Plugin.Manifest?.Version;
        if (string.IsNullOrWhiteSpace(currentText) || !Version.TryParse(currentText, out var currentVersion))
        {
            _pluginUpdateInfoBar.IsOpen = false;
            return;
        }

        if (currentVersion >= minVersion)
        {
            _pluginUpdateInfoBar.IsOpen = false;
            return;
        }

        _pluginUpdateInfoBar.Message =
            $"当前插件版本 {currentText} 低于「{entry!.Name}」要求的最低版本 {minText}，部分功能可能无法正常工作。" +
            "前往 GitHub 更新插件即可，如果你愿意的话。";
        _pluginUpdateInfoBar.IsOpen = true;
    }

    /// <summary>索引条目是否适配当前宿主版本。</summary>
    private static bool EntryMatchesHost(ContractIndexEntry entry)
    {
        var host = InjectorRuntime.HostVersion;
        if (string.IsNullOrWhiteSpace(entry.MinHostVersion) && string.IsNullOrWhiteSpace(entry.MaxHostVersion))
        {
            return true;
        }

        if (!Version.TryParse(host, out var version))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.MinHostVersion) &&
            Version.TryParse(entry.MinHostVersion, out var min) && version < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.MaxHostVersion) &&
            Version.TryParse(entry.MaxHostVersion, out var max) && version > max)
        {
            return false;
        }

        return true;
    }

    private Control PresetSaveFooter() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        VerticalAlignment = VerticalAlignment.Center,
        Children = { _presetName, Button("保存", SaveCurrentPreset) }
    };

    private Control PresetManageFooter() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        VerticalAlignment = VerticalAlignment.Center,
        Children = { _userPresetList, Button("套用", ApplyUserPreset), Button("删除", DeleteUserPreset) }
    };

    private void ReloadStyleSheet()
    {
        InjectorRuntime.ReloadStyleSheet();
        _status.Text = "已请求重载样式表；若样式表存在语法错误，ClassIsland 会保留稳定运行状态。";
    }

    /// <summary>
    /// 以设置窗口为宿主弹出 ContentDialog。无参 ShowAsync() 会选当前激活的窗口，
    /// 而设置页打开时激活的常是 ClassIsland 主界面，对话框会挂到主界面窗口上
    /// （确认框卡在主界面、点不到），因此必须显式指定宿主。
    /// </summary>
    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog) =>
        TopLevel.GetTopLevel(this) is Window host ? dialog.ShowAsync(host) : dialog.ShowAsync();

    /// <summary>重启 ClassIsland（确认后经宿主公开 API AppBase.Current.Restart 拉起新进程并退出）。</summary>
    private async void RestartClassIsland()
    {
        SaveAndApply();
        var dialog = new ContentDialog
        {
            Title = "确认重启 ClassIsland？",
            Content = "重启后插件设置将立即生效；未保存的 ClassIsland 系统设置可能会丢失。",
            PrimaryButtonText = "重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            AppBase.Current.Restart();
        }
        catch (Exception ex)
        {
            _status.Text = $"重启失败：{ex.Message}";
        }
    }

    /// <summary>打开 ClassIsland 设置窗口并导航到「外观」页（含分体主界面开关）。</summary>
    private void OpenAppearanceSettings()
    {
        try
        {
            IAppHost.TryGetService<IUriNavigationService>()
                ?.Navigate(new Uri("classisland://app/settings/appearance"));
        }
        catch
        {
            _status.Text = "无法打开 ClassIsland 设置页。";
        }
    }

    /// <summary>弹出对话框，解释 SMTC 与动态取色的工作原理。</summary>
    private async void ShowSmtcExplanation()
    {
        var content = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 460
        };
        content.Children.Add(DialogParagraph("SMTC 是什么？", true));
        content.Children.Add(DialogParagraph("SMTC（System Media Transport Controls，系统媒体传输控件）是 Windows 10 起内置的媒体会话机制。音乐播放器、浏览器等应用会把当前正在播放的媒体交给系统，包括标题、歌手、专辑、封面缩略图和播放状态。"));
        content.Children.Add(DialogParagraph("取色原理", true));
        content.Children.Add(DialogParagraph("当媒体变化时，插件读取当前焦点会话的专辑封面缩略图，然后用安卓的莫奈取色算法从封面中提取主题强调色。"));
        content.Children.Add(DialogParagraph("应用与过渡", true));
        content.Children.Add(DialogParagraph("提取出的主题色会应用到主界面背景、边框与阴影。"));
        content.Children.Add(DialogParagraph("兜底机制是什么？", true));
        content.Children.Add(DialogParagraph("切歌、暂停、恢复等变化由系统事件即时推送，几乎无延迟，但假如事件驱动意外失效，会按兜底刷新间隔作为保底。"));
        content.Children.Add(DialogParagraph("限制", true));
        content.Children.Add(DialogParagraph("需要 Windows 10 1809（build 17763）或更高版本；媒体应用需要支持 SMTC，主流播放器（网易云音乐、QQ 音乐、酷狗音乐、PotPlayer 等）和浏览器（最新版 Edge 等）大多支持"));

        var dialog = new ContentDialog
        {
            Title = "SMTC 与动态取色原理",
            Content = content,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await ShowDialogAsync(dialog);
    }

    /// <summary>说明对话框用的段落（header 为小标题加粗，body 为正文）。</summary>
    private static TextBlock DialogParagraph(string text, bool header = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = header ? FontWeight.Bold : FontWeight.Normal,
        FontSize = header ? 14 : 13,
        Opacity = header ? 1 : 0.85
    };

    private void PreviewNotification()
    {
        SaveAndApply();
        InjectorRuntime.PreviewNotification();
        _status.Text = "正在预览提醒：强调动画、遮罩过渡与 Ripple 将依次演示。";
    }

    private void PreviewPrepareOnClass()
    {
        MainWindowStyleInjector.DebugLog($"设置页 PreviewPrepareOnClass 被调用，combo.SelectedItem={_prepareOnClassStyle.SelectedItem}, 当前设置样式={InjectorRuntime.Settings.PrepareOnClassStyle}");
        SaveAndApply();
        MainWindowStyleInjector.DebugLog($"SaveAndApply 后样式={InjectorRuntime.Settings.PrepareOnClassStyle}");
        if (InjectorRuntime.Settings.PrepareOnClassStyle == PrepareOnClassStyle.None &&
            !InjectorRuntime.Settings.PrepareWarningEnabled)
        {
            _status.Text = "请先在「即将上课样式」中选择一种特效，或开启「红色警告」，再点击预览。";
            MainWindowStyleInjector.DebugLog("样式为 None 且未开启红色警告，提前返回，未调用注入器预览");
            return;
        }

        InjectorRuntime.PreviewPrepareOnClass();
        _status.Text = "正在预览即将上课样式（约 5 秒）。";
    }

    private async void DeleteAllData()
    {
        var dialog = new ContentDialog
        {
            Title = "确认删除所有数据？",
            Content = "将清除本插件的全部配置与数据，并把主界面恢复为原生状态。此操作不可恢复，执行后即可安全卸载插件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        InjectorRuntime.DeleteAllData();
        LoadFromSettings();
        _status.Text = "已删除所有数据，主界面已恢复为原生状态；现在可以通过 ClassIsland 的插件管理安全卸载本插件。";
    }

    private void WireVisualEditor()
    {
        foreach (var control in new Control[]
                 {
                     _opacity, _rotation, _offsetX, _offsetY, _cornerRadius,
                     _customBackground, _backgroundColor, _dynamicBackgroundColor, _dynamicBorderColor, _dynamicShadowColor,
                     _albumColorPollingInterval, _albumColorTransition, _gradient, _gradientEndColor,
                     _shadow, _shadowColor, _shadowBlur, _shadowOffsetX, _shadowOffsetY, _shadowOpacity,
                     _border, _borderColor, _borderThickness
                 })
        {
            control.PropertyChanged += (_, _) => RefreshVisualEditor();
        }
    }

    /// <summary>
    /// 为全部设置输入控件挂接实时预览：开启开关时，任意控件值变化经防抖后立即保存应用。
    /// </summary>
    private void WireLivePreview()
    {
        _livePreviewTimer.Tick += (_, _) =>
        {
            _livePreviewTimer.Stop();
            SaveAndApply();
        };

        foreach (var control in new Control[]
                 {
                     _enabled, _opacity, _rotation, _offsetX, _offsetY, _cornerRadius,
                     _animationEnabled, _animationMode, _animationAmount, _animationPeriod,
                     _customBackground, _backgroundColor, _dynamicBackgroundColor, _dynamicBorderColor, _dynamicShadowColor,
                     _revertColorsWhenPaused, _dynamicThemeColor, _albumColorPollingInterval, _albumColorTransition,
                     _mouseHoverKeepVisible, _clickEffectEnabled, _clickEffectType,
                     _fakeWeatherEnabled, _fakeWeatherCode, _fakeWeatherTemperature,
                     _fakeWeatherFeelsLike, _fakeWeatherHumidity, _fakeWeatherPressure, _fakeWeatherVisibility,
                     _fakeWeatherWindDirection, _fakeWeatherWindScale, _fakeWeatherAqi, _fakeWeatherAlertIcon,
                     _fakeWeatherAlertType, _fakeWeatherAlertLevel, _fakeWeatherAlertTitle, _fakeWeatherAlertDetail,
                     _fakeWeatherRainRemainingMinutes, _startupOpenTarget,
                     _reduceVisualBurden, _disableVersionCheck, _disableDegradationCheck,
                     _gradient, _gradientEndColor, _gradientDirection, _backgroundTextureType, _backgroundTextureColor, _backgroundTextureSize,
                     _backgroundTextureSpectrumSensitivity, _backgroundTextureSpectrumBars, _backgroundTextureSpectrumMirrored,
                     _backgroundTextureSpectrumAutoWidth,
                     _backgroundTextureEnabled,
                     _shadow, _shadowColor, _shadowBlur, _shadowOffsetX, _shadowOffsetY, _shadowOpacity,
                     _border, _borderColor, _borderThickness,
                     _wallpaperEnabled, _wallpaperModeBox, _wallpaperSource, _wallpaperPath, _wallpaperOpacity, _wallpaperDisplayMode,
                     _wallpaperScale, _wallpaperOffsetX, _wallpaperOffsetY, _wallpaperSlideshowInterval, _wallpaperBlur,
                     _visibilityAnimation, _visibilityAnimationEnabled, _visibilityDuration,
                     _emphasisAnimation, _emphasisAnimationEnabled, _emphasisAmount, _emphasisDuration,
                     _notificationTransition, _notificationTransitionEnabled, _notificationTransitionDuration,
                     _rippleType, _rippleEnabled, _rippleColor, _rippleDuration, _rippleThickness, _rippleOpacity, _rippleConstraint, _rippleConstraintRadius,
                     _cinematicShake, _cinematicBlur, _cinematicFlash,
                     _marqueeEnabled, _marqueeColor, _marqueeDuration, _marqueeOpacity, _marqueeSpeed, _marqueeFrameThickness,
                     _prepareOnClassStyle, _prepareOnClassEnabled,
                     _countdownArrowColor, _countdownArrowCount, _countdownArrowPerGroup, _countdownArrowSpacing,
                     _countdownArrowGroupSpacing, _countdownArrowSpeed, _countdownArrowThickness,
                     _countdownPulseColor, _countdownPulseThickness, _countdownPulseSpeed, _countdownPulseMaxRadius,
                     _countdownScanColor, _countdownScanThickness, _countdownScanSpeed, _countdownScanDirection, _countdownScanTailEnabled,
                     _countdownLightBandColor, _countdownLightBandThickness, _countdownLightBandAngle, _countdownLightBandSpeed,
                     _prepareWarningEnabled, _prepareWarningColor, _prepareWarningTriggerSeconds, _prepareWarningFlashSpeed, _prepareWarningFlashAmount,
                     _prepareWarningFrameThickness, _prepareWarningOpacity,
                     _styleSheetPath, _watchStyleSheet
                 })
        {
            control.PropertyChanged += (_, _) => TriggerLivePreview();
        }
    }

    /// <summary>
    /// 触发实时预览保存（带防抖）。程序性修改（加载 / 撤销 / 编辑器操作）时被抑制。
    /// </summary>
    private void TriggerLivePreview()
    {
        if (!_livePreview.IsChecked == true || _suppressLivePreview)
        {
            return;
        }

        _livePreviewTimer.Stop();
        _livePreviewTimer.Start();
    }

    private void RefreshVisualEditor()
    {
        var state = new IslandPreviewState(
            _opacity.Value,
            _rotation.DoubleValue,
            _offsetX.DoubleValue,
            _offsetY.DoubleValue,
            _cornerRadius.DoubleValue,
            _customBackground.IsChecked == true,
            _backgroundColor.Color,
            _gradient.IsChecked == true,
            _gradientEndColor.Color,
            Selected(_gradientDirection, GradientDirection.TopLeftToBottomRight),
            _shadow.IsChecked == true,
            _shadowColor.Color,
            _shadowBlur.DoubleValue,
            _shadowOffsetX.DoubleValue,
            _shadowOffsetY.DoubleValue,
            _shadowOpacity.Value,
            _border.IsChecked == true,
            _borderColor.Color,
            _borderThickness.DoubleValue);
        _visualEditorWindow?.Editor.Update(state);
        _visualEditorWindow?.UpdateInspector(state);
    }

    private void OpenVisualEditor()
    {
        if (_visualEditorWindow is { IsVisible: true })
        {
            _visualEditorWindow.Activate();
            return;
        }

        var window = new IslandVisualEditorWindow();
        _visualEditorWindow = window;
        _editorUndo.Clear();
        _editorRedo.Clear();
        _editorDirty = false;
        window.UpdateUndoState(false, false);
        // 编辑器采用暂存式编辑：期间禁止实时预览，避免把未保存的预览直接应用到主界面。
        _suppressLivePreview = true;

        // 画布手势：手势开始时记录撤销快照；拖动期间只改控件做实时预览（不保存）。
        window.Editor.EditStarted += (_, _) => PushEditorUndo();
        window.Editor.TransformEdited += (_, e) =>
        {
            _offsetX.DoubleValue = e.OffsetX;
            _offsetY.DoubleValue = e.OffsetY;
            _rotation.DoubleValue = e.Rotation;
        };
        window.Editor.CornerRadiusEdited += (_, e) => _cornerRadius.DoubleValue = e.Value;

        // 顶部操作：保存 / 撤销 / 重做。
        window.SaveRequested += (_, _) => SaveEditor();
        window.UndoRequested += (_, _) => UndoEditorEdit();
        window.RedoRequested += (_, _) => RedoEditorEdit();

        // 检查器：每个编辑项作为一步可撤销更改（暂存，未保存前不写盘）。
        window.BackgroundColorEdited += color =>
        {
            PushEditorUndo();
            _customBackground.IsChecked = true;
            _dynamicBackgroundColor.IsChecked = false;
            _backgroundColor.Color = color;
        };
        window.GradientEdited += enabled => { PushEditorUndo(); _gradient.IsChecked = enabled; };
        window.GradientEndColorEdited += color => { PushEditorUndo(); _gradientEndColor.Color = color; };
        window.ShadowEdited += enabled => { PushEditorUndo(); _shadow.IsChecked = enabled; };
        window.ShadowColorEdited += color => { PushEditorUndo(); _dynamicShadowColor.IsChecked = false; _shadowColor.Color = color; };
        window.ShadowBlurEdited += value => { PushEditorUndo(); _shadowBlur.DoubleValue = value; };
        window.ShadowOpacityEdited += value => { PushEditorUndo(); _shadowOpacity.Value = value; };
        window.OpacityEdited += value => { PushEditorUndo(); _opacity.Value = value; };
        window.CornerRadiusEdited += value => { PushEditorUndo(); _cornerRadius.DoubleValue = value; };
        window.BackgroundEdited += enabled => { PushEditorUndo(); _customBackground.IsChecked = enabled; };
        window.RotationEdited += value => { PushEditorUndo(); _rotation.DoubleValue = value; };
        window.OffsetXEdited += value => { PushEditorUndo(); _offsetX.DoubleValue = value; };
        window.OffsetYEdited += value => { PushEditorUndo(); _offsetY.DoubleValue = value; };
        window.BorderEdited += enabled => { PushEditorUndo(); _border.IsChecked = enabled; };
        window.BorderColorEdited += color => { PushEditorUndo(); _border.IsChecked = true; _dynamicBorderColor.IsChecked = false; _borderColor.Color = color; };
        window.BorderThicknessEdited += value => { PushEditorUndo(); _border.IsChecked = true; _borderThickness.DoubleValue = value; };
        window.ShadowOffsetXEdited += value => { PushEditorUndo(); _shadowOffsetX.DoubleValue = value; };
        window.ShadowOffsetYEdited += value => { PushEditorUndo(); _shadowOffsetY.DoubleValue = value; };

        // 关闭前询问是否保存。
        window.Closing += OnEditorClosing;
        window.Closed += (_, _) =>
        {
            _visualEditorWindow = null;
            _suppressLivePreview = false;
        };
        RefreshVisualEditor();
        window.Show();
    }

    /// <summary>
    /// 捕获编辑器可编辑的全部设置项当前值（即撤销/重做的快照）。
    /// </summary>
    private IslandPreviewState CaptureEditorState() => new(
        _opacity.Value, _rotation.DoubleValue, _offsetX.DoubleValue, _offsetY.DoubleValue,
        _cornerRadius.DoubleValue,
        _customBackground.IsChecked == true, _backgroundColor.Color, _gradient.IsChecked == true, _gradientEndColor.Color,
        Selected(_gradientDirection, GradientDirection.TopLeftToBottomRight),
        _shadow.IsChecked == true, _shadowColor.Color, _shadowBlur.DoubleValue, _shadowOffsetX.DoubleValue, _shadowOffsetY.DoubleValue,
        _shadowOpacity.Value, _border.IsChecked == true, _borderColor.Color, _borderThickness.DoubleValue);

    private void PushEditorUndo()
    {
        _editorUndo.Add(CaptureEditorState());
        if (_editorUndo.Count > 100)
        {
            _editorUndo.RemoveAt(0);
        }

        _editorRedo.Clear();
        _editorDirty = true;
        _visualEditorWindow?.UpdateUndoState(true, false);
    }

    private void RestoreEditorState(IslandPreviewState state)
    {
        _opacity.Value = state.Opacity;
        _rotation.DoubleValue = state.Rotation;
        _offsetX.DoubleValue = state.OffsetX;
        _offsetY.DoubleValue = state.OffsetY;
        _cornerRadius.DoubleValue = state.CornerRadius;
        _customBackground.IsChecked = state.CustomBackground;
        _backgroundColor.Color = state.BackgroundColor;
        _gradient.IsChecked = state.Gradient;
        _gradientEndColor.Color = state.GradientEndColor;
        Select(_gradientDirection, GradientDirections, state.GradientDirection);
        _shadow.IsChecked = state.ShadowEnabled;
        _shadowColor.Color = state.ShadowColor;
        _shadowBlur.DoubleValue = state.ShadowBlur;
        _shadowOffsetX.DoubleValue = state.ShadowOffsetX;
        _shadowOffsetY.DoubleValue = state.ShadowOffsetY;
        _shadowOpacity.Value = state.ShadowOpacity;
        _border.IsChecked = state.BorderEnabled;
        _borderColor.Color = state.BorderColor;
        _borderThickness.DoubleValue = state.BorderThickness;
        // 撤销/重做后工作区与已保存状态不再一致，关闭时应再次询问。
        _editorDirty = true;
        RefreshVisualEditor();
    }

    private void UndoEditorEdit()
    {
        if (_editorUndo.Count == 0)
        {
            return;
        }

        _editorRedo.Add(CaptureEditorState());
        var state = _editorUndo[^1];
        _editorUndo.RemoveAt(_editorUndo.Count - 1);
        RestoreEditorState(state);
        _visualEditorWindow?.UpdateUndoState(_editorUndo.Count > 0, true);
    }

    private void RedoEditorEdit()
    {
        if (_editorRedo.Count == 0)
        {
            return;
        }

        _editorUndo.Add(CaptureEditorState());
        var state = _editorRedo[^1];
        _editorRedo.RemoveAt(_editorRedo.Count - 1);
        RestoreEditorState(state);
        _visualEditorWindow?.UpdateUndoState(true, _editorRedo.Count > 0);
    }

    private void SaveEditor()
    {
        _editorRedo.Clear();
        _editorDirty = false;
        SaveAndApply();
        _visualEditorWindow?.UpdateUndoState(_editorUndo.Count > 0, false);
        _status.Text = "已保存编辑器更改并应用到主界面。";
    }

    private void DiscardEditorEdits()
    {
        LoadFromSettings();
        RefreshVisualEditor();
        _editorUndo.Clear();
        _editorRedo.Clear();
        _editorDirty = false;
        _visualEditorWindow?.UpdateUndoState(false, false);
    }

    private async void OnEditorClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_visualEditorWindow == null || !_editorDirty)
        {
            return;
        }

        e.Cancel = true;
        var dialog = new ContentDialog
        {
            Title = "保存更改？",
            Content = "可视化编辑器中有尚未保存的更改。",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        // 宿主用正在关闭的编辑器窗口，而不是设置页/主界面。
        var result = sender is Window closingWindow
            ? await dialog.ShowAsync(closingWindow)
            : await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            SaveEditor();
            _editorDirty = false;
            if (sender is Window w)
            {
                w.Close();
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            DiscardEditorEdits();
            _editorDirty = false;
            if (sender is Window w)
            {
                w.Close();
            }
        }
    }

    private void LoadFromSettings()
    {
        _suppressLivePreview = true;
        try
        {
            LoadFromSettingsCore();
        }
        finally
        {
            _suppressLivePreview = false;
        }
    }

    private void LoadFromSettingsCore()
    {
        var settings = InjectorRuntime.Settings;
        _enabled.IsChecked = settings.Enabled;
        _opacity.Value = settings.Opacity;
        _rotation.DoubleValue = settings.Rotation;
        _offsetX.DoubleValue = settings.OffsetX;
        _offsetY.DoubleValue = settings.OffsetY;
        _animationEnabled.IsChecked = settings.AnimationEnabled;
        Select(_animationMode, IslandAnimationModes, settings.AnimationMode);
        _animationAmount.Value = settings.AnimationAmount;
        _animationPeriod.DoubleValue = settings.AnimationPeriodSeconds;
        _styleSheetPath.Text = settings.StyleSheetPath;
        _watchStyleSheet.IsChecked = settings.WatchStyleSheet;
        _cornerRadius.DoubleValue = settings.CornerRadius;
        _customBackground.IsChecked = settings.CustomBackgroundEnabled;
        _backgroundColor.Color = ReadColor(settings.BackgroundColor, Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
        _dynamicBackgroundColor.IsChecked = settings.DynamicBackgroundColorEnabled;
        _dynamicBorderColor.IsChecked = settings.DynamicBorderColorEnabled;
        _dynamicShadowColor.IsChecked = settings.DynamicShadowColorEnabled;
        _revertColorsWhenPaused.IsChecked = settings.RevertColorsWhenPaused;
        _dynamicThemeColor.IsChecked = settings.DynamicThemeColorEnabled;
        _mouseHoverKeepVisible.IsChecked = settings.MouseHoverKeepVisible;
        _clickEffectEnabled.IsChecked = settings.ClickEffectEnabled;
        Select(_clickEffectType, ClickEffectTypes, settings.ClickEffectType);
        _fakeWeatherEnabled.IsChecked = settings.FakeWeatherEnabled;
        Select(_fakeWeatherCode, FakeWeatherCodes, settings.FakeWeatherCode);
        _fakeWeatherTemperature.DoubleValue = settings.FakeWeatherTemperature;
        _fakeWeatherFeelsLike.DoubleValue = settings.FakeWeatherFeelsLike;
        _fakeWeatherHumidity.DoubleValue = settings.FakeWeatherHumidity;
        _fakeWeatherPressure.DoubleValue = settings.FakeWeatherPressure;
        _fakeWeatherVisibility.DoubleValue = settings.FakeWeatherVisibility;
        _fakeWeatherWindDirection.Text = settings.FakeWeatherWindDirection;
        _fakeWeatherWindScale.Text = settings.FakeWeatherWindScale;
        _fakeWeatherAqi.DoubleValue = settings.FakeWeatherAqi;
        Select(_fakeWeatherAlertIcon, FakeWeatherAlertIcons, settings.FakeWeatherAlertIcon);
        _fakeWeatherAlertType.Text = settings.FakeWeatherAlertType;
        _fakeWeatherAlertLevel.Text = settings.FakeWeatherAlertLevel;
        _fakeWeatherAlertTitle.Text = settings.FakeWeatherAlertTitle;
        _fakeWeatherAlertDetail.Text = settings.FakeWeatherAlertDetail;
        _fakeWeatherRainRemainingMinutes.DoubleValue = settings.FakeWeatherRainRemainingMinutes;
        Select(_startupOpenTarget, StartupOpenTargets, settings.StartupOpenTarget);
        _reduceVisualBurden.IsChecked = settings.ReduceVisualBurden;
        _disableVersionCheck.IsChecked = settings.DisableVersionCheck;
        _disableDegradationCheck.IsChecked = settings.DisableDegradationCheck;
        _diagnosticLogging.IsChecked = settings.DiagnosticLoggingEnabled;
        _albumColorPollingInterval.DoubleValue = settings.AlbumColorPollingIntervalSeconds;
        _albumColorTransition.DoubleValue = settings.AlbumColorTransitionSeconds;
        _gradient.IsChecked = settings.GradientEnabled;
        _gradientEndColor.Color = ReadColor(settings.GradientEndColor, Color.FromArgb(0xCC, 0x40, 0x40, 0xA0));
        Select(_gradientDirection, GradientDirections, settings.GradientDirection);
        Select(_backgroundTextureType, BackgroundTextures, settings.BackgroundTextureType);
        _backgroundTextureEnabled.IsChecked = settings.BackgroundTextureType != BackgroundTexture.None;
        _backgroundTextureColor.Color = ReadColor(settings.BackgroundTextureColor, Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        _backgroundTextureSize.DoubleValue = settings.BackgroundTextureSize;
        _backgroundTextureSpectrumSensitivity.Value = settings.BackgroundTextureSpectrumSensitivity;
        _backgroundTextureSpectrumBars.DoubleValue = settings.BackgroundTextureSpectrumBars;
        _backgroundTextureSpectrumMirrored.IsChecked = settings.BackgroundTextureSpectrumMirrored;
        _backgroundTextureSpectrumAutoWidth.IsChecked = settings.BackgroundTextureSpectrumAutoWidth;
        _shadow.IsChecked = settings.ShadowEnabled;
        _shadowColor.Color = ReadColor(settings.ShadowColor, Color.FromArgb(0x99, 0, 0, 0));
        _shadowBlur.DoubleValue = settings.ShadowBlur;
        _shadowOffsetX.DoubleValue = settings.ShadowOffsetX;
        _shadowOffsetY.DoubleValue = settings.ShadowOffsetY;
        _shadowOpacity.Value = settings.ShadowOpacity;
        _border.IsChecked = settings.BorderEnabled;
        _borderColor.Color = ReadColor(settings.BorderColor, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        _borderThickness.DoubleValue = settings.BorderThickness;
        _wallpaperEnabled.IsChecked = settings.WallpaperEnabled;
        Select(_wallpaperModeBox, WallpaperModes, settings.WallpaperDesignerEnabled);
        Select(_wallpaperSource, WallpaperSources, settings.WallpaperSource);
        _wallpaperPath.Text = settings.WallpaperPath;
        _wallpaperOpacity.Value = settings.WallpaperOpacity;
        Select(_wallpaperDisplayMode, WallpaperDisplayModes, settings.WallpaperDisplayMode);
        _wallpaperScale.DoubleValue = settings.WallpaperScale;
        _wallpaperOffsetX.DoubleValue = settings.WallpaperOffsetX;
        _wallpaperOffsetY.DoubleValue = settings.WallpaperOffsetY;
        _wallpaperSlideshowInterval.DoubleValue = settings.WallpaperSlideshowIntervalSeconds;
        _wallpaperBlur.DoubleValue = settings.WallpaperBlurRadius;
        UpdateWallpaperModeVisibility();
        Select(_visibilityAnimation, VisibilityAnimations, settings.VisibilityAnimation);
        _visibilityAnimationEnabled.IsChecked = settings.VisibilityAnimation != VisibilityAnimation.None;
        _visibilityDuration.DoubleValue = settings.VisibilityDurationSeconds;
        Select(_emphasisAnimation, EmphasisAnimations, settings.EmphasisAnimation);
        _emphasisAnimationEnabled.IsChecked = settings.EmphasisAnimation != EmphasisAnimation.None;
        _emphasisAmount.Value = settings.EmphasisAmount;
        _emphasisDuration.DoubleValue = settings.EmphasisDurationSeconds;
        Select(_notificationTransition, NotificationTransitions, settings.NotificationTransition);
        _notificationTransitionEnabled.IsChecked = settings.NotificationTransition != NotificationTransition.HostDefault;
        _notificationTransitionDuration.DoubleValue = settings.NotificationTransitionDurationSeconds;
        _carouselAnimation.IsChecked = settings.CarouselAnimationEnabled;
        Select(_carouselAnimationType, CarouselAnimationTypes, settings.CarouselAnimationType);
        _carouselAnimationDuration.DoubleValue = settings.CarouselAnimationDurationSeconds;
        _carouselAnimationOffset.DoubleValue = settings.CarouselAnimationOffset;
        Select(_rippleType, RippleTypes, settings.RippleType);
        _rippleEnabled.IsChecked = settings.RippleType != RippleType.None;
        _rippleColor.Color = ReadColor(settings.RippleColor, Color.FromArgb(0xAA, 0x7D, 0xD3, 0xFC));
        _rippleDuration.DoubleValue = settings.RippleDurationSeconds;
        _rippleThickness.DoubleValue = settings.RippleThickness;
        _rippleOpacity.Value = settings.RippleOpacity;
        _rippleConstraint.IsChecked = settings.RippleConstraintEnabled;
        _rippleConstraintRadius.DoubleValue = settings.RippleConstraintRadius;
        _marqueeEnabled.IsChecked = settings.MarqueeEnabled;
        _marqueeColor.Color = ReadColor(settings.MarqueeColor, Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        _marqueeDuration.DoubleValue = settings.MarqueeDurationSeconds;
        _marqueeOpacity.Value = settings.MarqueeOpacity;
        _marqueeSpeed.DoubleValue = settings.MarqueeSpeed;
        _marqueeFrameThickness.DoubleValue = settings.MarqueeFrameThickness;
        Select(_prepareOnClassStyle, PrepareOnClassStyles, settings.PrepareOnClassStyle);
        _prepareOnClassEnabled.IsChecked = settings.PrepareOnClassStyle != PrepareOnClassStyle.None;
        _countdownArrowColor.Color = ReadColor(settings.CountdownArrowColor, Color.FromArgb(0xBF, 0xF8, 0xFA, 0xFC));
        _countdownArrowCount.DoubleValue = settings.CountdownArrowCount;
        _countdownArrowPerGroup.DoubleValue = settings.CountdownArrowPerGroup;
        _countdownArrowSpacing.DoubleValue = settings.CountdownArrowSpacing;
        _countdownArrowGroupSpacing.DoubleValue = settings.CountdownArrowGroupSpacing;
        _countdownArrowSpeed.DoubleValue = settings.CountdownArrowSpeed;
        _countdownArrowThickness.DoubleValue = settings.CountdownArrowThickness;
        _countdownPulseColor.Color = ReadColor(settings.CountdownPulseColor, Color.FromArgb(0xBF, 0xF8, 0xFA, 0xFC));
        _countdownPulseThickness.DoubleValue = settings.CountdownPulseThickness;
        _countdownPulseSpeed.DoubleValue = settings.CountdownPulseSpeed;
        _countdownPulseMaxRadius.DoubleValue = settings.CountdownPulseMaxRadius;
        _countdownScanColor.Color = ReadColor(settings.CountdownScanColor, Color.FromArgb(0xBF, 0xF8, 0xFA, 0xFC));
        _countdownScanThickness.DoubleValue = settings.CountdownScanThickness;
        _countdownScanSpeed.DoubleValue = settings.CountdownScanSpeed;
        Select(_countdownScanDirection, ScanDirections, settings.CountdownScanDirection);
        _countdownScanTailEnabled.IsChecked = settings.CountdownScanTailEnabled;
        _countdownLightBandColor.Color = ReadColor(settings.CountdownLightBandColor, Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        _countdownLightBandThickness.DoubleValue = settings.CountdownLightBandThickness;
        _countdownLightBandAngle.DoubleValue = settings.CountdownLightBandAngle;
        _countdownLightBandSpeed.DoubleValue = settings.CountdownLightBandSpeed;
        _prepareWarningEnabled.IsChecked = settings.PrepareWarningEnabled;
        _prepareWarningColor.Color = ReadColor(settings.PrepareWarningColor, Color.FromArgb(0x66, 0xFF, 0, 0));
        _prepareWarningTriggerSeconds.DoubleValue = settings.PrepareWarningTriggerSeconds;
        _prepareWarningFlashSpeed.DoubleValue = settings.PrepareWarningFlashSpeed;
        _prepareWarningFlashAmount.Value = settings.PrepareWarningFlashAmount;
        _prepareWarningFrameThickness.DoubleValue = settings.PrepareWarningFrameThickness;
        _prepareWarningOpacity.Value = settings.PrepareWarningOpacity;
        _cinematicShake.Value = settings.CinematicShakeAmount;
        _cinematicBlur.Value = settings.CinematicBlurRadius;
        _cinematicFlash.Value = settings.CinematicFlashAmount;
        RefreshUserPresets();
    }

    private void SaveAndApply()
    {
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        try
        {
            settings.Enabled = _enabled.IsChecked == true;
            settings.Opacity = _opacity.Value;
            settings.Rotation = _rotation.DoubleValue;
            settings.OffsetX = _offsetX.DoubleValue;
            settings.OffsetY = _offsetY.DoubleValue;
            settings.AnimationEnabled = _animationEnabled.IsChecked == true;
            settings.AnimationMode = Selected(_animationMode, IslandAnimationMode.None);
            settings.AnimationAmount = _animationAmount.Value;
            settings.AnimationPeriodSeconds = _animationPeriod.DoubleValue;
            settings.StyleSheetPath = _styleSheetPath.Text ?? string.Empty;
            settings.WatchStyleSheet = _watchStyleSheet.IsChecked == true;
            var cornerRadiusChanged = settings.CornerRadius != _cornerRadius.DoubleValue;
            settings.CornerRadius = _cornerRadius.DoubleValue;
            // 用户显式修改圆角时，把形状切换为 RoundedRectangle，让自定义圆角生效；
            // 全新安装（HostDefault）保持不改动主界面原生圆角。
            if (cornerRadiusChanged && settings.Shape == IslandShape.HostDefault)
            {
                settings.Shape = IslandShape.RoundedRectangle;
            }

            settings.CustomBackgroundEnabled = _customBackground.IsChecked == true;
            settings.BackgroundColor = _backgroundColor.Color.ToString();
            settings.DynamicBackgroundColorEnabled = _dynamicBackgroundColor.IsChecked == true;
            settings.DynamicBorderColorEnabled = _dynamicBorderColor.IsChecked == true;
            settings.DynamicShadowColorEnabled = _dynamicShadowColor.IsChecked == true;
            settings.RevertColorsWhenPaused = _revertColorsWhenPaused.IsChecked == true;
            settings.DynamicThemeColorEnabled = _dynamicThemeColor.IsChecked == true;
            settings.MouseHoverKeepVisible = _mouseHoverKeepVisible.IsChecked == true;
            settings.ClickEffectEnabled = _clickEffectEnabled.IsChecked == true;
            settings.ClickEffectType = Selected(_clickEffectType, ClickEffectType.Ring);
            settings.FakeWeatherEnabled = _fakeWeatherEnabled.IsChecked == true;
            settings.FakeWeatherCode = Selected(_fakeWeatherCode, 0);
            settings.FakeWeatherTemperature = _fakeWeatherTemperature.DoubleValue;
            settings.FakeWeatherFeelsLike = _fakeWeatherFeelsLike.DoubleValue;
            settings.FakeWeatherHumidity = _fakeWeatherHumidity.DoubleValue;
            settings.FakeWeatherPressure = _fakeWeatherPressure.DoubleValue;
            settings.FakeWeatherVisibility = _fakeWeatherVisibility.DoubleValue;
            settings.FakeWeatherWindDirection = _fakeWeatherWindDirection.Text ?? string.Empty;
            settings.FakeWeatherWindScale = _fakeWeatherWindScale.Text ?? string.Empty;
            settings.FakeWeatherAqi = _fakeWeatherAqi.DoubleValue;
            settings.FakeWeatherAlertIcon = Selected(_fakeWeatherAlertIcon, 0);
            settings.FakeWeatherAlertType = _fakeWeatherAlertType.Text ?? string.Empty;
            settings.FakeWeatherAlertLevel = _fakeWeatherAlertLevel.Text ?? string.Empty;
            settings.FakeWeatherAlertTitle = _fakeWeatherAlertTitle.Text ?? string.Empty;
            settings.FakeWeatherAlertDetail = _fakeWeatherAlertDetail.Text ?? string.Empty;
            settings.FakeWeatherRainRemainingMinutes = (int)Math.Round(_fakeWeatherRainRemainingMinutes.DoubleValue);
            settings.StartupOpenTarget = Selected(_startupOpenTarget, 0);
            settings.ReduceVisualBurden = _reduceVisualBurden.IsChecked == true;
            settings.DisableVersionCheck = _disableVersionCheck.IsChecked == true;
            settings.DisableDegradationCheck = _disableDegradationCheck.IsChecked == true;
            settings.DiagnosticLoggingEnabled = _diagnosticLogging.IsChecked == true;
            settings.AlbumColorPollingIntervalSeconds = _albumColorPollingInterval.DoubleValue;
            settings.AlbumColorTransitionSeconds = _albumColorTransition.DoubleValue;
            settings.GradientEnabled = _gradient.IsChecked == true;
            settings.GradientEndColor = _gradientEndColor.Color.ToString();
            settings.GradientDirection = Selected(_gradientDirection, GradientDirection.TopLeftToBottomRight);
            settings.BackgroundTextureType = _backgroundTextureEnabled.IsChecked == true
                ? Selected(_backgroundTextureType, BackgroundTexture.None)
                : BackgroundTexture.None;
            settings.BackgroundTextureColor = _backgroundTextureColor.Color.ToString();
            settings.BackgroundTextureSize = _backgroundTextureSize.DoubleValue;
            settings.BackgroundTextureSpectrumSensitivity = _backgroundTextureSpectrumSensitivity.Value;
            settings.BackgroundTextureSpectrumBars = (int)Math.Round(_backgroundTextureSpectrumBars.DoubleValue);
            settings.BackgroundTextureSpectrumMirrored = _backgroundTextureSpectrumMirrored.IsChecked == true;
            settings.BackgroundTextureSpectrumAutoWidth = _backgroundTextureSpectrumAutoWidth.IsChecked == true;
            settings.ShadowEnabled = _shadow.IsChecked == true;
            settings.ShadowColor = _shadowColor.Color.ToString();
            settings.ShadowBlur = _shadowBlur.DoubleValue;
            settings.ShadowOffsetX = _shadowOffsetX.DoubleValue;
            settings.ShadowOffsetY = _shadowOffsetY.DoubleValue;
            settings.ShadowOpacity = _shadowOpacity.Value;
            settings.BorderEnabled = _border.IsChecked == true;
            settings.BorderColor = _borderColor.Color.ToString();
            settings.BorderThickness = _borderThickness.DoubleValue;
            settings.WallpaperEnabled = _wallpaperEnabled.IsChecked == true;
            settings.WallpaperDesignerEnabled = Selected(_wallpaperModeBox, settings.WallpaperDesignerEnabled);
            settings.WallpaperSource = Selected(_wallpaperSource, WallpaperSource.None);
            settings.WallpaperPath = _wallpaperPath.Text ?? string.Empty;
            settings.WallpaperOpacity = _wallpaperOpacity.Value;
            settings.WallpaperDisplayMode = Selected(_wallpaperDisplayMode, WallpaperDisplayMode.Fill);
            settings.WallpaperScale = _wallpaperScale.DoubleValue;
            settings.WallpaperOffsetX = _wallpaperOffsetX.DoubleValue;
            settings.WallpaperOffsetY = _wallpaperOffsetY.DoubleValue;
            settings.WallpaperSlideshowIntervalSeconds = _wallpaperSlideshowInterval.DoubleValue;
            settings.WallpaperBlurRadius = _wallpaperBlur.DoubleValue;
            settings.VisibilityAnimation = _visibilityAnimationEnabled.IsChecked == true
                ? Selected(_visibilityAnimation, VisibilityAnimation.None)
                : VisibilityAnimation.None;
            settings.VisibilityDurationSeconds = _visibilityDuration.DoubleValue;
            settings.EmphasisAnimation = _emphasisAnimationEnabled.IsChecked == true
                ? Selected(_emphasisAnimation, EmphasisAnimation.None)
                : EmphasisAnimation.None;
            settings.EmphasisAmount = _emphasisAmount.Value;
            settings.EmphasisDurationSeconds = _emphasisDuration.DoubleValue;
            settings.NotificationTransition = _notificationTransitionEnabled.IsChecked == true
                ? Selected(_notificationTransition, NotificationTransition.HostDefault)
                : NotificationTransition.HostDefault;
            settings.NotificationTransitionDurationSeconds = _notificationTransitionDuration.DoubleValue;
            settings.CarouselAnimationEnabled = _carouselAnimation.IsChecked == true;
            settings.CarouselAnimationType = Selected(_carouselAnimationType, CarouselAnimationType.SlideUp);
            settings.CarouselAnimationDurationSeconds = _carouselAnimationDuration.DoubleValue;
            settings.CarouselAnimationOffset = _carouselAnimationOffset.DoubleValue;
            settings.RippleType = _rippleEnabled.IsChecked == true
                ? Selected(_rippleType, RippleType.None)
                : RippleType.None;
            settings.RippleColor = _rippleColor.Color.ToString();
            settings.RippleDurationSeconds = _rippleDuration.DoubleValue;
            settings.RippleThickness = _rippleThickness.DoubleValue;
            settings.RippleOpacity = _rippleOpacity.Value;
            settings.RippleConstraintEnabled = _rippleConstraint.IsChecked == true;
            settings.RippleConstraintRadius = _rippleConstraintRadius.DoubleValue;
            settings.MarqueeEnabled = _marqueeEnabled.IsChecked == true;
            settings.MarqueeColor = _marqueeColor.Color.ToString();
            settings.MarqueeDurationSeconds = _marqueeDuration.DoubleValue;
            settings.MarqueeOpacity = _marqueeOpacity.Value;
            settings.MarqueeSpeed = _marqueeSpeed.DoubleValue;
            settings.MarqueeFrameThickness = _marqueeFrameThickness.DoubleValue;
            settings.PrepareOnClassStyle = _prepareOnClassEnabled.IsChecked == true
                ? Selected(_prepareOnClassStyle, PrepareOnClassStyle.None)
                : PrepareOnClassStyle.None;
            settings.CountdownArrowColor = _countdownArrowColor.Color.ToString();
            settings.CountdownArrowCount = (int)Math.Round(_countdownArrowCount.DoubleValue);
            settings.CountdownArrowPerGroup = (int)Math.Round(_countdownArrowPerGroup.DoubleValue);
            settings.CountdownArrowSpacing = _countdownArrowSpacing.DoubleValue;
            settings.CountdownArrowGroupSpacing = _countdownArrowGroupSpacing.DoubleValue;
            settings.CountdownArrowSpeed = _countdownArrowSpeed.DoubleValue;
            settings.CountdownArrowThickness = _countdownArrowThickness.DoubleValue;
            settings.CountdownPulseColor = _countdownPulseColor.Color.ToString();
            settings.CountdownPulseThickness = _countdownPulseThickness.DoubleValue;
            settings.CountdownPulseSpeed = _countdownPulseSpeed.DoubleValue;
            settings.CountdownPulseMaxRadius = _countdownPulseMaxRadius.DoubleValue;
            settings.CountdownScanColor = _countdownScanColor.Color.ToString();
            settings.CountdownScanThickness = _countdownScanThickness.DoubleValue;
            settings.CountdownScanSpeed = _countdownScanSpeed.DoubleValue;
            settings.CountdownScanDirection = Selected(_countdownScanDirection, ScanlineDirection.Horizontal);
            settings.CountdownScanTailEnabled = _countdownScanTailEnabled.IsChecked == true;
            settings.CountdownLightBandColor = _countdownLightBandColor.Color.ToString();
            settings.CountdownLightBandThickness = _countdownLightBandThickness.DoubleValue;
            settings.CountdownLightBandAngle = _countdownLightBandAngle.DoubleValue;
            settings.CountdownLightBandSpeed = _countdownLightBandSpeed.DoubleValue;
            settings.PrepareWarningEnabled = _prepareWarningEnabled.IsChecked == true;
            settings.PrepareWarningColor = _prepareWarningColor.Color.ToString();
            settings.PrepareWarningTriggerSeconds = _prepareWarningTriggerSeconds.DoubleValue;
            settings.PrepareWarningFlashSpeed = _prepareWarningFlashSpeed.DoubleValue;
            settings.PrepareWarningFlashAmount = _prepareWarningFlashAmount.Value;
            settings.PrepareWarningFrameThickness = _prepareWarningFrameThickness.DoubleValue;
            settings.PrepareWarningOpacity = _prepareWarningOpacity.Value;
            settings.CinematicShakeAmount = _cinematicShake.Value;
            settings.CinematicBlurRadius = _cinematicBlur.Value;
            settings.CinematicFlashAmount = _cinematicFlash.Value;
        }
        finally
        {
            settings.EndUpdate();
        }

        _status.Text = "已保存并应用。样式表有更改时会自动热重载。";
    }

    private SettingsExpander Setting(string glyph, string header, string description, Control footer)
    {
        var expander = new SettingsExpander
        {
            IconSource = new FluentIconSource(glyph),
            Header = header,
            Description = description,
            Footer = footer
        };
        _allExpanders.Add(expander);
        return expander;
    }

    private static void AddSection(Panel panel, string glyph, string title)
    {
        panel.Children.Add(new IconText { Glyph = glyph, Text = title, Margin = new Thickness(0, 16, 0, 4) });
    }

    private SettingsExpander Group(string glyph, string header, string description, params SettingsExpanderItem[] items)
    {
        var group = new SettingsExpander
        {
            IconSource = new FluentIconSource(glyph),
            Header = header,
            Description = description,
            IsExpanded = false
        };
        _allExpanders.Add(group);
        foreach (var item in items)
        {
            group.Items.Add(item);
        }

        return group;
    }

    private SettingsExpander SwitchableGroup(string glyph, string header, string description, ToggleSwitch toggle, params SettingsExpanderItem[] items)
    {
        var group = Group(glyph, header, description, items);
        group.Footer = toggle;
        foreach (var item in items)
        {
            ControlledBy(item, toggle);
        }

        return group;
    }

    private SettingsExpanderItem Item(string header, string description, Control footer, ToggleSwitch? dependency = null)
    {
        var item = new SettingsExpanderItem
        {
            Content = header,
            Description = description,
            Footer = footer
        };
        _allItems.Add(item);
        if (dependency != null)
        {
            ControlledBy(item, dependency);
        }

        return item;
    }

    private static void ControlledBy(Control target, ToggleSwitch controller)
    {
        void Sync() => target.IsEnabled = controller.IsChecked == true;
        controller.PropertyChanged += (_, _) => Sync();
        Sync();
    }

    private static void EnabledWhenManualColor(Control target, ToggleSwitch customBackground, ToggleSwitch dynamicColor)
    {
        void Sync() => target.IsEnabled = customBackground.IsChecked == true && dynamicColor.IsChecked != true;
        customBackground.PropertyChanged += (_, _) => Sync();
        dynamicColor.PropertyChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>仅当下拉框选中值不在指定集合内时显示目标（不匹配项直接隐藏）。</summary>
    private static void VisibleWhenNotAny<T>(Control target, ComboBox selector, params T[] hiddenValues)
    {
        void Sync()
        {
            var selected = selector.SelectedItem is Choice<T> choice ? choice.Value : default!;
            target.IsVisible = selected != null && Array.IndexOf(hiddenValues, selected) < 0;
        }

        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>总开关打开且下拉框选中值不在指定集合内时显示目标。</summary>
    private static void VisibleWhenNotAny<T>(Control target, ComboBox selector, ToggleSwitch masterToggle, params T[] hiddenValues)
    {
        void Sync() => target.IsVisible =
            masterToggle.IsChecked == true &&
            selector.SelectedItem is Choice<T> choice &&
            Array.IndexOf(hiddenValues, choice.Value) < 0;
        masterToggle.PropertyChanged += (_, _) => Sync();
        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>仅当总开关打开且下拉框选中指定值时显示目标（即将上课子样式卡片用）。</summary>
    private static void VisibleWhenEnabledAnd<T>(Control target, ToggleSwitch toggle, ComboBox selector, T value)
    {
        void Sync() => target.IsVisible =
            toggle.IsChecked == true &&
            EqualityComparer<T>.Default.Equals(Selected(selector, value), value);
        toggle.PropertyChanged += (_, _) => Sync();
        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>打开总开关且下拉框尚未选中任何项时，自动选第一个选项（处理全新安装/旧配置为 None 的情况）。</summary>
    private static void AutoSelectOnEnable<T>(ToggleSwitch toggle, ComboBox selector, IEnumerable<Choice<T>> choices)
    {
        void Sync()
        {
            if (toggle.IsChecked == true && selector.SelectedItem == null)
            {
                selector.SelectedItem = choices.FirstOrDefault();
            }
        }

        toggle.PropertyChanged += (_, _) => Sync();
        Sync();
    }

    private static void VisibleWhen<T>(Control target, ComboBox selector, T visibleValue)
    {
        void Sync() => target.IsVisible = EqualityComparer<T>.Default.Equals(Selected(selector, visibleValue), visibleValue);
        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    private static ToggleSwitch Toggle() => new()
    {
        OnContent = "开",
        OffContent = "关",
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Slider Slider(double minimum, double maximum, double tickFrequency)
    {
        var slider = new Slider
        {
            Width = 220,
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = tickFrequency,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "auto-tooltip" }
        };
        SliderDragTooltipAssist.SetStringFormat(slider, tickFrequency < 1 ? "F2" : "F0");
        return slider;
    }

    /// <summary>
    /// 适合需要显示精确数值的选项。基于标准 Avalonia <see cref="NumericUpDown"/>。
    /// 注意：必须把 StyleKey 指回 NumericUpDown，否则隐式主题查找会按派生类型
    /// （Spin）去找 ControlTheme，而 FluentAvalonia 只注册了 NumericUpDown 的主题，
    /// 会导致控件渲染为空（不可见）。
    /// </summary>
    private static Spin Spinner(double minimum, double maximum, double increment, string format = "0.##") => new(minimum, maximum, increment, format);

    private sealed class Spin : NumericUpDown
    {
        // 隐式主题按 StyleKey（= StyleKeyOverride）查找 ControlTheme；
        // FluentAvalonia 只注册了 NumericUpDown 的主题，若不覆写此属性，
        // 派生类型 Spin 找不到主题会渲染为空（不可见）。
        protected override Type StyleKeyOverride => typeof(NumericUpDown);

        public Spin(double minimum, double maximum, double increment, string format)
        {
            Minimum = (decimal)minimum;
            Maximum = (decimal)maximum;
            Increment = (decimal)increment;
            FormatString = format;
            Value = (decimal)minimum;
            Width = 220;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalContentAlignment = HorizontalAlignment.Right;
        }

        public double DoubleValue
        {
            get => (double)(Value ?? 0);
            set => Value = (decimal)Math.Clamp(value, (double)Minimum, (double)Maximum);
        }
    }

    private static ColorPicker ColorPicker() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static ComboBox Combo<T>(IEnumerable<Choice<T>> items) => new()
    {
        ItemsSource = items,
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };

    private static StackPanel ColorFooter(ColorPicker picker, ToggleSwitch toggle) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 2,
        Children = { picker, toggle }
    };

    private Control WallpaperPathFooter()
    {
        var pickButton = Button("选择…", () => _ = PickWallpaperPathAsync());
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _wallpaperPath, pickButton }
        };
    }

    private async Task PickWallpaperPathAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
        {
            return;
        }

        var source = Selected(_wallpaperSource, WallpaperSource.LocalImage);
        if (source == WallpaperSource.FolderSlideshow)
        {
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择幻灯片文件夹",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                _wallpaperPath.Text = folders[0].TryGetLocalPath() ?? string.Empty;
            }

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
        if (files.Count > 0)
        {
            _wallpaperPath.Text = files[0].TryGetLocalPath() ?? string.Empty;
        }
    }

    /// <summary>
    /// 打开 Photoshop 风格底图图层编辑器。编辑器关闭后刷新本页，
    /// 使「图层模式 / 层级」等状态与持久化设置保持一致。
    /// 同一时刻只允许一个编辑器窗口，已打开时聚焦现有窗口。
    /// </summary>
    private void OpenWallpaperLayerEditor()
    {
        if (WallpaperLayerEditorWindow.Current is { } existing)
        {
            existing.Activate();
            return;
        }

        SaveAndApply();
        var window = new WallpaperLayerEditorWindow();
        window.Closed += (_, _) => Dispatcher.UIThread.Post(LoadFromSettings);
        window.Show();
    }

    /// <summary>回退到旧版简单模式底图（清空图层并关闭图层式底图）。</summary>
    private void DisableWallpaperDesigner()
    {
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        settings.WallpaperDesignerEnabled = false;
        settings.WallpaperLayers = [];
        settings.EndUpdate();
        InjectorRuntime.SaveAndApply();
        LoadFromSettings();
        _status.Text = "已恢复简单模式底图设置。";
    }

    /// <summary>
    /// 按编辑模式同步背景图片组各行的显隐：专家模式隐藏全部基础模式设置项，
    /// 基础模式隐藏专家模式的编辑器进入按钮；同时刷新模式提示 InfoBar。
    /// </summary>
    private void UpdateWallpaperModeVisibility()
    {
        if (_wallpaperEditorItem == null)
        {
            return;
        }

        var designer = _wallpaperModeBox.SelectedItem is Choice<bool> mode
            ? mode.Value
            : InjectorRuntime.Settings.WallpaperDesignerEnabled;
        _wallpaperEditorItem.IsVisible = designer;
        _wallpaperSourceItem.IsVisible = !designer;
        _wallpaperOpacityItem.IsVisible = !designer;
        _wallpaperDisplayModeItem.IsVisible = !designer;
        _wallpaperScaleItem.IsVisible = !designer;
        _wallpaperOffsetXItem.IsVisible = !designer;
        _wallpaperOffsetYItem.IsVisible = !designer;
        _wallpaperBlurItem.IsVisible = !designer;
        var source = Selected(_wallpaperSource, WallpaperSource.None);
        _wallpaperPathItem.IsVisible = !designer &&
                                       (source == WallpaperSource.LocalImage || source == WallpaperSource.FolderSlideshow);
        _wallpaperSlideshowItem.IsVisible = !designer && source == WallpaperSource.FolderSlideshow;
        RefreshWallpaperModeInfo();
    }

    /// <summary>刷新「专家模式」状态提示（专家模式时显示 InfoBar，可一键回退基础模式）。</summary>
    private void RefreshWallpaperModeInfo()
    {
        if (_wallpaperModeInfoBar == null)
        {
            return;
        }

        var settings = InjectorRuntime.Settings;
        _wallpaperModeInfoBar.IsOpen = settings.WallpaperDesignerEnabled;
    }

    private static StackPanel Actions(string firstText, Action firstAction, string secondText, Action secondAction) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children = { Button(firstText, firstAction), Button(secondText, secondAction) }
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button LinkButton(string text, string url)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => OpenUrl(url);
        return button;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(AppBase.Current.MainWindow);
            if (topLevel != null)
            {
                _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
            }
        }
        catch
        {
            // 打开链接失败不应影响插件。
        }
    }

    private static Color ReadColor(string value, Color fallback)
    {
        try
        {
            return Color.Parse(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static void Select<T>(ComboBox comboBox, IEnumerable<Choice<T>> choices, T value)
    {
        comboBox.SelectedItem = choices.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value));
    }

    private static T Selected<T>(ComboBox comboBox, T fallback) =>
        comboBox.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private static string Display<T>(IEnumerable<Choice<T>> choices, T value) =>
        choices.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value))?.Text ?? value?.ToString() ?? string.Empty;

    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }
}
