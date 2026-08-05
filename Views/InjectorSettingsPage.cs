using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Assists;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
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
    private readonly Spin _albumColorPollingInterval = Spinner(0.5, 120, 0.5);
    private readonly Spin _albumColorTransition = Spinner(0, 10, 0.1);
    private readonly ToggleSwitch _gradient = Toggle();
    private readonly ColorPicker _gradientEndColor = ColorPicker();
    private readonly ComboBox _gradientDirection = Combo(GradientDirections);
    private readonly ComboBox _backgroundTextureType = Combo(BackgroundTextures);
    private readonly ColorPicker _backgroundTextureColor = ColorPicker();
    private readonly Spin _backgroundTextureSize = Spinner(8, 80, 2, "0");

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
    private readonly ComboBox _wallpaperSource = Combo(WallpaperSources);
    private readonly TextBox _wallpaperPath = new() { MinWidth = 260, IsReadOnly = true };
    private readonly Slider _wallpaperOpacity = Slider(0, 1, 0.05);
    private readonly ComboBox _wallpaperDisplayMode = Combo(WallpaperDisplayModes);
    private readonly Spin _wallpaperScale = Spinner(1, 5, 0.1);
    private readonly Spin _wallpaperOffsetX = Spinner(-0.5, 0.5, 0.01);
    private readonly Spin _wallpaperOffsetY = Spinner(-0.5, 0.5, 0.01);
    private readonly Spin _wallpaperSlideshowInterval = Spinner(2, 3600, 1, "0");
    private readonly Spin _wallpaperBlur = Spinner(0, 60, 1);

    private readonly ComboBox _visibilityAnimation = Combo(VisibilityAnimations);
    private readonly Spin _visibilityDuration = Spinner(0.1, 10, 0.05);
    private readonly ComboBox _emphasisAnimation = Combo(EmphasisAnimations);
    private readonly Slider _emphasisAmount = Slider(0, 1, 0.01);
    private readonly Spin _emphasisDuration = Spinner(0.1, 10, 0.05);
    private readonly ComboBox _notificationTransition = Combo(NotificationTransitions);
    private readonly Spin _notificationTransitionDuration = Spinner(0.05, 5, 0.05);

    private readonly ComboBox _rippleType = Combo(RippleTypes);
    private readonly ColorPicker _rippleColor = ColorPicker();
    private readonly Spin _rippleDuration = Spinner(0.1, 10, 0.05);
    private readonly Spin _rippleThickness = Spinner(0.5, 40, 0.5);
    private readonly Slider _rippleOpacity = Slider(0.1, 1, 0.05);
    private readonly ToggleSwitch _rippleConstraint = Toggle();
    private readonly Spin _rippleConstraintRadius = Spinner(0, 2000, 10, "0");
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
        new(IslandAnimationMode.None, "无"),
        new(IslandAnimationMode.Breathe, "呼吸"),
        new(IslandAnimationMode.Float, "浮动"),
        new(IslandAnimationMode.Wave, "波浪"),
    ];

    private static readonly Choice<VisibilityAnimation>[] VisibilityAnimations =
    [
        new(VisibilityAnimation.None, "无"),
        new(VisibilityAnimation.Fade, "淡入淡出"),
        new(VisibilityAnimation.Scale, "缩放"),
        new(VisibilityAnimation.SlideFromTop, "从上方滑入"),
        new(VisibilityAnimation.SlideFromBottom, "从下方滑入"),
    ];

    private static readonly Choice<EmphasisAnimation>[] EmphasisAnimations =
    [
        new(EmphasisAnimation.None, "无"),
        new(EmphasisAnimation.Pulse, "脉冲"),
        new(EmphasisAnimation.Bounce, "弹跳"),
        new(EmphasisAnimation.Shake, "摇晃"),
        new(EmphasisAnimation.Flash, "闪烁"),
    ];

    private static readonly Choice<NotificationTransition>[] NotificationTransitions =
    [
        new(NotificationTransition.HostDefault, "跟随 ClassIsland"),
        new(NotificationTransition.Fade, "淡入淡出"),
        new(NotificationTransition.SlideDown, "向下滑动"),
        new(NotificationTransition.SlideUp, "向上滑动"),
        new(NotificationTransition.SlideLeft, "向左滑动"),
        new(NotificationTransition.SlideRight, "向右滑动"),
    ];

    private static readonly Choice<RippleType>[] RippleTypes =
    [
        new(RippleType.None, "无"),
        new(RippleType.Ring, "单环"),
        new(RippleType.DoubleRing, "双环"),
        new(RippleType.Glow, "光晕"),
        new(RippleType.Square, "方框"),
        new(RippleType.Hanabi, "花火"),
    ];

    private static readonly Choice<PrepareOnClassStyle>[] PrepareOnClassStyles =
    [
        new(PrepareOnClassStyle.None, "无"),
        new(PrepareOnClassStyle.Arrows, "箭头"),
        new(PrepareOnClassStyle.PulseRing, "扩散光环"),
        new(PrepareOnClassStyle.Scanline, "扫描线"),
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
        new(BackgroundTexture.None, "无"),
        new(BackgroundTexture.Grid, "网格线"),
        new(BackgroundTexture.Dots, "点阵"),
        new(BackgroundTexture.DiagonalLines, "斜线"),
        new(BackgroundTexture.Cross, "十字网格"),
    ];

    public InjectorSettingsPage()
    {
        Content = BuildContent();
        WireVisualEditor();
        WireLivePreview();
        LoadFromSettings();
    }

    private Control BuildContent()
    {
        var panel = new StackPanel
        {
            Classes = { "settings-container", "animated-intro" },
            Spacing = 4
        };

        panel.Children.Add(new IconText { Glyph = "\uEC4A", Text = "样式注入器", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Setting("\uE813", "实时预览", "开启后，下方对设置项的修改会立即保存并应用到主界面；关闭时需手动点击「保存并应用」。可视化编辑器始终为手动保存，不受此开关影响。", _livePreview));
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

        AddSection(panel, "\uF42F", "用户预设");
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "可被 ClassIsland 自动化调用",
            Message = "把当前全部设置保存为命名预设后，可以在 ClassIsland 自动化中添加「切换用户预设」行动，按条件（时间、课程等）自动切换整套方案。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Setting("\uF42F", "保存当前为预设", "把插件当前全部设置项保存为一个命名预设（同名覆盖）。", PresetSaveFooter()));
        panel.Children.Add(Setting("\uF42F", "套用 / 删除预设", "套用会把全部设置项替换为该预设保存时的状态。", PresetManageFooter()));
        panel.Children.Add(Setting("\uE0BD", "恢复插件默认", "把全部设置恢复为插件默认（不会修改 Overrides.axaml）。", Button("恢复默认", ResetToDefaults)));

        AddSection(panel, "\uE288", "可视化编辑器");
        panel.Children.Add(Setting("\uE288", "打开可视化编辑器", "在独立窗口中像编辑演示文稿一样拖动、旋转、缩放岛屿，并即时应用到主界面。", Button("打开编辑器", OpenVisualEditor)));

        AddSection(panel, "\uE113", "基础变形");
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "与 ClassIsland 原生设置重叠",
            Message = "不透明度、缩放与位置可在 ClassIsland 的外观页修改，在此覆盖可能与原生设置产生少量兼容性问题。圆角已改为同步写入 ClassIsland 原生圆角设置（0-20），与宿主内容裁切一致。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Group("\uE113", "基础变形", "这些值会覆盖并叠加在 ClassIsland 的主界面外观设置之上。",
            Item("不透明度", "控制主界面的整体透明度。", _opacity),
            Item("水平偏移", "向左或向右移动主界面。", _offsetX),
            Item("垂直偏移", "向上或向下移动主界面。", _offsetY),
            Item("圆角半径", "岛屿边角的圆润程度（0-20，20 为半圆）。该值会同步写入 ClassIsland 原生圆角设置，与宿主内容裁切保持一致。", _cornerRadius),
            Item("旋转角度", "以中心点旋转主界面。", _rotation)));

        AddSection(panel, "\uE51F", "背景");
        var backgroundColorItem = Item("背景色", "支持透明度的主界面背景颜色。", _backgroundColor);
        var backgroundGroup = SwitchableGroup("\uE520", "底色填充", "关闭时保留 ClassIsland 自身的背景颜色。", _customBackground,
            backgroundColorItem,
            Item("动态专辑封面取色", "读取当前 SMTC 专辑封面，并使用 Material You（Monet）算法自动提取主题色。", _dynamicBackgroundColor),
            Item("线性渐变", "开启后会使用渐变终止色。", _gradient),
            Item("渐变方向", "线性渐变从起始色到终止色的方向。", _gradientDirection, _gradient),
            Item("渐变终止色", "线性渐变背景的结束颜色。", _gradientEndColor, _gradient));
        EnabledWhenManualColor(backgroundColorItem, _customBackground, _dynamicBackgroundColor);
        panel.Children.Add(backgroundGroup);
        panel.Children.Add(Group("\uE92B", "底纹纹理", "在底色之上叠加可平铺的纹理图案，可与背景图片、背景色同时使用；纹理不受动态取色影响。",
            Item("纹理图案", "选择填充纹理的类型，无 = 关闭纹理。", _backgroundTextureType),
            Item("纹理颜色", "支持透明度的纹理线条颜色。", _backgroundTextureColor),
            Item("纹理大小", "单个纹理单元的大小（像素）。", _backgroundTextureSize)));
        var wallpaperPathItem = Item("图片 / 文件夹", "底图文件或幻灯片文件夹的路径。", WallpaperPathFooter());
        var wallpaperSlideshowItem = Item("幻灯片间隔", "文件夹幻灯片切换间隔（秒）。", _wallpaperSlideshowInterval);
        var wallpaperGroup = SwitchableGroup("\uF42D", "背景图片", "层级：底图 → 底色 → 组件。SMTC 来源由事件驱动即时更新，兜底刷新与图片过渡时长沿用“动态取色”设置。", _wallpaperEnabled,
            Item("图片来源", "选择底图的来源：本地图片、文件夹幻灯片或 SMTC 专辑封面。", _wallpaperSource),
            wallpaperPathItem,
            Item("图片不透明度", "底图的整体透明度。", _wallpaperOpacity),
            Item("显示方式", "图片在岛屿内的显示方式。", _wallpaperDisplayMode),
            Item("缩放", "底图的缩放倍率（1 为按显示方式适应，大于 1 放大裁剪）。", _wallpaperScale),
            Item("水平偏移", "底图的水平偏移（相对图片宽度，-0.5 到 0.5）。", _wallpaperOffsetX),
            Item("垂直偏移", "底图的垂直偏移（相对图片高度，-0.5 到 0.5）。", _wallpaperOffsetY),
            Item("模糊", "对底图应用高斯模糊（0 为关闭）。模糊边缘会被岛屿边界裁剪。", _wallpaperBlur),
            wallpaperSlideshowItem);
        VisibleWhenAny(wallpaperPathItem, _wallpaperSource, WallpaperSource.LocalImage, WallpaperSource.FolderSlideshow);
        VisibleWhen(wallpaperSlideshowItem, _wallpaperSource, WallpaperSource.FolderSlideshow);
        panel.Children.Add(wallpaperGroup);
        panel.Children.Add(Group("\uF361", "动态取色", "SMTC 采用事件驱动：媒体变化（切歌/换封面）时即时更新；下方的间隔仅作为兜底刷新，应对个别应用事件不触发的情况。",
            Item("暂停/停止时恢复原色", "媒体暂停或停止播放时，把背景、边框、阴影从专辑取色平滑恢复为你配置的原始颜色，恢复播放后再跟随专辑。", _revertColorsWhenPaused),
            Item("兜底刷新间隔", "事件驱动失效时的兜底刷新间隔（秒）。", _albumColorPollingInterval),
            Item("颜色过渡时长", "专辑颜色变化时，背景、边框、阴影平滑过渡到新颜色的时长（秒），0 为立即切换。", _albumColorTransition)));

        AddSection(panel, "\uE254", "边框与阴影");
        var shadowColorItem = Item("阴影颜色", "支持透明度的阴影颜色。", _shadowColor);
        panel.Children.Add(SwitchableGroup("\uE472", "阴影", "为岛屿添加投影效果。", _shadow,
            Item("动态取色", "阴影色调跟随专辑封面，使用 Material You 深色中性色；透明度沿用你配置的阴影颜色透明度。", _dynamicShadowColor),
            shadowColorItem,
            Item("阴影模糊", "控制投影的柔和程度。", _shadowBlur),
            Item("阴影水平偏移", "控制投影向左或向右偏移。", _shadowOffsetX),
            Item("阴影垂直偏移", "控制投影向上或向下偏移。", _shadowOffsetY),
            Item("阴影不透明度", "控制投影的深浅。", _shadowOpacity)));
        EnabledWhenManualColor(shadowColorItem, _shadow, _dynamicShadowColor);
        var borderColorItem = Item("边框颜色", "支持透明度的边框颜色。", _borderColor);
        panel.Children.Add(SwitchableGroup("\uE254", "岛屿边框", "为岛屿添加细边框。", _border,
            Item("动态取色", "边框色调跟随专辑封面，使用 Material You 主色调；透明度沿用你配置的边框颜色透明度。", _dynamicBorderColor),
            borderColorItem,
            Item("边框线宽", "控制岛屿边框的粗细。", _borderThickness)));
        EnabledWhenManualColor(borderColorItem, _border, _dynamicBorderColor);

        AddSection(panel, "\uE82B", "动画");
        panel.Children.Add(SwitchableGroup("\uEDB9", "持续动画", "打开后才会使用下方的循环动画设置。", _animationEnabled,
            Item("动画类型", "选择循环动画的运动方式。", _animationMode),
            Item("动画幅度", "控制循环动画的强弱。", _animationAmount),
            Item("动画周期", "完成一次循环所需的时间（秒）。", _animationPeriod)));
        panel.Children.Add(ChoiceGroup("\uEFED", "主界面显示动画", "选择主界面出现或消失时使用的动画。", _visibilityAnimation, VisibilityAnimation.None,
            Item("显示动画时长", "主界面显示动画的时长（秒）。", _visibilityDuration)));

        AddSection(panel, "\uE025", "提醒");
        panel.Children.Add(Setting("\uEFFE", "预览提醒", "一次性预览强调动画、遮罩过渡与 Ripple 效果（持续约 2 秒）。", Button("预览提醒", PreviewNotification)));
        panel.Children.Add(ChoiceGroup("\uE02B", "提醒强调动画", "选择收到提醒时使用的强调效果。", _emphasisAnimation, EmphasisAnimation.None,
            Item("强调幅度", "控制强调动画的强弱。", _emphasisAmount),
            Item("强调时长", "提醒强调动画的时长（秒）。", _emphasisDuration)));
        panel.Children.Add(ChoiceGroup("\uE833", "提醒遮罩动画", "选择提醒遮罩出现和消失时的过渡效果。", _notificationTransition, NotificationTransition.HostDefault,
            Item("遮罩动画时长", "提醒遮罩动画的时长（秒）。", _notificationTransitionDuration)));
        var rippleColorItem = Item("Ripple 颜色", "支持透明度的提醒扩散颜色。", _rippleColor);
        var rippleDurationItem = Item("Ripple 时长", "扩散效果的播放时长（秒）。", _rippleDuration);
        var rippleThicknessItem = Item("Ripple 线宽", "环形或方框 Ripple 的线条粗细。", _rippleThickness);
        var rippleOpacityItem = Item("全局不透明度", "全局降低 Ripple 效果的透明度，避免上课时分心（1 为不降低）。", _rippleOpacity);
        var rippleConstraintItem = Item("限制扩散范围", "以主界面中心为圆心创建圆形裁剪，约束所有类型 Ripple 的扩散范围。", _rippleConstraint);
        var rippleConstraintRadiusItem = Item("约束半径", "Ripple 扩散的圆形约束半径（像素），0 为自动按主界面大小计算。", _rippleConstraintRadius, _rippleConstraint);
        var rippleGroup = ChoiceGroup("\uEFFF", "提醒 Ripple", "选择提醒时的扩散效果。花火使用固定的原始配色与线宽。", _rippleType, RippleType.None,
            rippleColorItem, rippleDurationItem, rippleThicknessItem, rippleOpacityItem, rippleConstraintItem, rippleConstraintRadiusItem);
        EnabledWhenNot(rippleColorItem, _rippleType, RippleType.Hanabi);
        EnabledWhenNot(rippleThicknessItem, _rippleType, RippleType.Hanabi);
        panel.Children.Add(rippleGroup);
        var hanabiInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "关于花火（Hanabi）效果",
            Message = "受当前技术限制，本插件无法实现类似 maimai でらっくす 的带光影的烟花效果，只能仿制经典旧版烟花效果。",
            IsOpen = true,
            IsClosable = false
        };
        VisibleWhen(hanabiInfoBar, _rippleType, RippleType.Hanabi);
        panel.Children.Add(hanabiInfoBar);
        AddSection(panel, "\uE4C4", "即将上课样式");
        panel.Children.Add(Setting("\uE4C4", "预览即将上课样式", "立即预览 5 秒即将上课动画，无需真的处于即将上课状态。", Button("预览", PreviewPrepareOnClass)));
        panel.Children.Add(Setting("\uE4C4", "即将上课样式", "选择即将上课倒计时期间显示的特效；选择「无」则不显示。", _prepareOnClassStyle));
        var arrowGroup = Group("\uE0F7", "箭头", "斜向箭头从右向左滑动。",
            Item("箭头颜色", "支持透明度的箭头颜色。", _countdownArrowColor),
            Item("箭头组数", "屏幕上同时滑动的箭头组数量。", _countdownArrowCount),
            Item("每组箭头数", "每组内包含的箭头数量，2 即经典的 >> 效果。", _countdownArrowPerGroup),
            Item("组内箭头间距", "同一组内相邻箭头之间的距离（像素）。", _countdownArrowSpacing),
            Item("组间间距", "相邻箭头组之间的额外间距（像素）。", _countdownArrowGroupSpacing),
            Item("滑动速度", "箭头的移动速度。", _countdownArrowSpeed),
            Item("箭头线宽", "箭头的线条粗细。", _countdownArrowThickness));
        var pulseGroup = Group("\uEE35", "扩散光环", "从主界面中心向外扩散并淡出的圆环。",
            Item("光环颜色", "支持透明度的光环颜色。", _countdownPulseColor),
            Item("光环线宽", "光环的线条粗细。", _countdownPulseThickness),
            Item("扩散速度", "每秒扩散的圈数。", _countdownPulseSpeed),
            Item("最大半径", "光环最大半径占主界面宽高中较小值的比例。", _countdownPulseMaxRadius));
        var scanGroup = Group("\uEECD", "扫描线", "一道带渐变尾迹的光线扫过主界面，进入 / 离开时自动渐显渐隐。",
            Item("扫描方向", "横向为水平线上下扫，纵向为竖直线左右扫。", _countdownScanDirection),
            Item("渐变尾迹", "关闭后只显示一条主线，不带渐变尾迹。", _countdownScanTailEnabled),
            Item("扫描颜色", "支持透明度的扫描线颜色。", _countdownScanColor),
            Item("扫描线宽", "扫描线的粗细。", _countdownScanThickness),
            Item("扫描速度", "每秒扫描次数。", _countdownScanSpeed));
        VisibleWhen(arrowGroup, _prepareOnClassStyle, PrepareOnClassStyle.Arrows);
        VisibleWhen(pulseGroup, _prepareOnClassStyle, PrepareOnClassStyle.PulseRing);
        VisibleWhen(scanGroup, _prepareOnClassStyle, PrepareOnClassStyle.Scanline);
        panel.Children.Add(arrowGroup);
        panel.Children.Add(pulseGroup);
        panel.Children.Add(scanGroup);

        AddSection(panel, "\uF263", "高级样式表");
        panel.Children.Add(Setting("\uF263", "覆盖样式表路径", "填写 .axaml 样式表的完整路径。", _styleSheetPath));
        panel.Children.Add(Setting("\uE161", "自动热重载", "保存样式表后自动重新加载。", _watchStyleSheet));

        AddSection(panel, "\uE61D", "卸载与数据清理");
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "删除所有数据",
            Message = "将清除本插件在 ClassIsland 中创建的全部配置与数据（设置、覆盖样式表、诊断日志等），并把主界面恢复为原生状态。此操作不可恢复，执行后即可安全卸载插件。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Setting("\uE61D", "删除所有数据", "一键清空插件全部数据并恢复主界面，让插件回到“全新安装”状态，之后可安全卸载。", Button("删除所有数据", DeleteAllData)));

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
                    Footer = LinkButton("加入我们的行动", "")                       
                }
            }
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Button("保存并应用", SaveAndApply));
        actions.Children.Add(Button("重载样式表", ReloadStyleSheet));
        panel.Children.Add(actions);
        panel.Children.Add(_status);
        return new ScrollViewer { Content = panel };
    }

    private void ResetToDefaults()
    {
        InjectorRuntime.Settings.ResetToDefaults();
        LoadFromSettings();
        _status.Text = "已恢复插件默认设置；Overrides.axaml 未被修改。";
    }

    private void SaveCurrentPreset()
    {
        SaveAndApply(); // 先把页面上未保存的改动提交到设置，再整体快照。
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

    private void PreviewNotification()
    {
        SaveAndApply();
        InjectorRuntime.PreviewNotification();
        _status.Text = "正在预览提醒：强调动画、遮罩过渡与 Ripple 将依次演示。";
    }

    private void PreviewPrepareOnClass()
    {
        SaveAndApply();
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

        var result = await dialog.ShowAsync();
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
                     _revertColorsWhenPaused, _albumColorPollingInterval, _albumColorTransition,
                     _gradient, _gradientEndColor, _gradientDirection, _backgroundTextureType, _backgroundTextureColor, _backgroundTextureSize,
                     _shadow, _shadowColor, _shadowBlur, _shadowOffsetX, _shadowOffsetY, _shadowOpacity,
                     _border, _borderColor, _borderThickness,
                     _wallpaperEnabled, _wallpaperSource, _wallpaperPath, _wallpaperOpacity, _wallpaperDisplayMode,
                     _wallpaperScale, _wallpaperOffsetX, _wallpaperOffsetY, _wallpaperSlideshowInterval, _wallpaperBlur,
                     _visibilityAnimation, _visibilityDuration, _emphasisAnimation, _emphasisAmount, _emphasisDuration,
                     _notificationTransition, _notificationTransitionDuration,
                     _rippleType, _rippleColor, _rippleDuration, _rippleThickness, _rippleOpacity, _rippleConstraint, _rippleConstraintRadius,
                     _prepareOnClassStyle,
                     _countdownArrowColor, _countdownArrowCount, _countdownArrowPerGroup, _countdownArrowSpacing,
                     _countdownArrowGroupSpacing, _countdownArrowSpeed, _countdownArrowThickness,
                     _countdownPulseColor, _countdownPulseThickness, _countdownPulseSpeed, _countdownPulseMaxRadius,
                     _countdownScanColor, _countdownScanThickness, _countdownScanSpeed, _countdownScanDirection, _countdownScanTailEnabled,
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
        var result = await dialog.ShowAsync();
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
        _albumColorPollingInterval.DoubleValue = settings.AlbumColorPollingIntervalSeconds;
        _albumColorTransition.DoubleValue = settings.AlbumColorTransitionSeconds;
        _gradient.IsChecked = settings.GradientEnabled;
        _gradientEndColor.Color = ReadColor(settings.GradientEndColor, Color.FromArgb(0xCC, 0x40, 0x40, 0xA0));
        Select(_gradientDirection, GradientDirections, settings.GradientDirection);
        Select(_backgroundTextureType, BackgroundTextures, settings.BackgroundTextureType);
        _backgroundTextureColor.Color = ReadColor(settings.BackgroundTextureColor, Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        _backgroundTextureSize.DoubleValue = settings.BackgroundTextureSize;
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
        Select(_wallpaperSource, WallpaperSources, settings.WallpaperSource);
        _wallpaperPath.Text = settings.WallpaperPath;
        _wallpaperOpacity.Value = settings.WallpaperOpacity;
        Select(_wallpaperDisplayMode, WallpaperDisplayModes, settings.WallpaperDisplayMode);
        _wallpaperScale.DoubleValue = settings.WallpaperScale;
        _wallpaperOffsetX.DoubleValue = settings.WallpaperOffsetX;
        _wallpaperOffsetY.DoubleValue = settings.WallpaperOffsetY;
        _wallpaperSlideshowInterval.DoubleValue = settings.WallpaperSlideshowIntervalSeconds;
        _wallpaperBlur.DoubleValue = settings.WallpaperBlurRadius;
        Select(_visibilityAnimation, VisibilityAnimations, settings.VisibilityAnimation);
        _visibilityDuration.DoubleValue = settings.VisibilityDurationSeconds;
        Select(_emphasisAnimation, EmphasisAnimations, settings.EmphasisAnimation);
        _emphasisAmount.Value = settings.EmphasisAmount;
        _emphasisDuration.DoubleValue = settings.EmphasisDurationSeconds;
        Select(_notificationTransition, NotificationTransitions, settings.NotificationTransition);
        _notificationTransitionDuration.DoubleValue = settings.NotificationTransitionDurationSeconds;
        Select(_rippleType, RippleTypes, settings.RippleType);
        _rippleColor.Color = ReadColor(settings.RippleColor, Color.FromArgb(0xAA, 0x7D, 0xD3, 0xFC));
        _rippleDuration.DoubleValue = settings.RippleDurationSeconds;
        _rippleThickness.DoubleValue = settings.RippleThickness;
        _rippleOpacity.Value = settings.RippleOpacity;
        _rippleConstraint.IsChecked = settings.RippleConstraintEnabled;
        _rippleConstraintRadius.DoubleValue = settings.RippleConstraintRadius;
        Select(_prepareOnClassStyle, PrepareOnClassStyles, settings.PrepareOnClassStyle);
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
            settings.AlbumColorPollingIntervalSeconds = _albumColorPollingInterval.DoubleValue;
            settings.AlbumColorTransitionSeconds = _albumColorTransition.DoubleValue;
            settings.GradientEnabled = _gradient.IsChecked == true;
            settings.GradientEndColor = _gradientEndColor.Color.ToString();
            settings.GradientDirection = Selected(_gradientDirection, GradientDirection.TopLeftToBottomRight);
            settings.BackgroundTextureType = Selected(_backgroundTextureType, BackgroundTexture.None);
            settings.BackgroundTextureColor = _backgroundTextureColor.Color.ToString();
            settings.BackgroundTextureSize = _backgroundTextureSize.DoubleValue;
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
            settings.WallpaperSource = Selected(_wallpaperSource, WallpaperSource.None);
            settings.WallpaperPath = _wallpaperPath.Text ?? string.Empty;
            settings.WallpaperOpacity = _wallpaperOpacity.Value;
            settings.WallpaperDisplayMode = Selected(_wallpaperDisplayMode, WallpaperDisplayMode.Fill);
            settings.WallpaperScale = _wallpaperScale.DoubleValue;
            settings.WallpaperOffsetX = _wallpaperOffsetX.DoubleValue;
            settings.WallpaperOffsetY = _wallpaperOffsetY.DoubleValue;
            settings.WallpaperSlideshowIntervalSeconds = _wallpaperSlideshowInterval.DoubleValue;
            settings.WallpaperBlurRadius = _wallpaperBlur.DoubleValue;
            settings.VisibilityAnimation = Selected(_visibilityAnimation, VisibilityAnimation.None);
            settings.VisibilityDurationSeconds = _visibilityDuration.DoubleValue;
            settings.EmphasisAnimation = Selected(_emphasisAnimation, EmphasisAnimation.None);
            settings.EmphasisAmount = _emphasisAmount.Value;
            settings.EmphasisDurationSeconds = _emphasisDuration.DoubleValue;
            settings.NotificationTransition = Selected(_notificationTransition, NotificationTransition.HostDefault);
            settings.NotificationTransitionDurationSeconds = _notificationTransitionDuration.DoubleValue;
            settings.RippleType = Selected(_rippleType, RippleType.None);
            settings.RippleColor = _rippleColor.Color.ToString();
            settings.RippleDurationSeconds = _rippleDuration.DoubleValue;
            settings.RippleThickness = _rippleThickness.DoubleValue;
            settings.RippleOpacity = _rippleOpacity.Value;
            settings.RippleConstraintEnabled = _rippleConstraint.IsChecked == true;
            settings.RippleConstraintRadius = _rippleConstraintRadius.DoubleValue;
            settings.PrepareOnClassStyle = Selected(_prepareOnClassStyle, PrepareOnClassStyle.None);
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
        }
        finally
        {
            settings.EndUpdate();
        }

        _status.Text = "已保存并应用。样式表有更改时会自动热重载。";
    }

    private static SettingsExpander Setting(string glyph, string header, string description, Control footer) => new()
    {
        IconSource = new FluentIconSource(glyph),
        Header = header,
        Description = description,
        Footer = footer
    };

    private static void AddSection(Panel panel, string glyph, string title)
    {
        panel.Children.Add(new IconText { Glyph = glyph, Text = title, Margin = new Thickness(0, 16, 0, 4) });
    }

    private static SettingsExpander Group(string glyph, string header, string description, params SettingsExpanderItem[] items)
    {
        var group = new SettingsExpander
        {
            IconSource = new FluentIconSource(glyph),
            Header = header,
            Description = description,
            IsExpanded = false
        };
        foreach (var item in items)
        {
            group.Items.Add(item);
        }

        return group;
    }

    private static SettingsExpander SwitchableGroup(string glyph, string header, string description, ToggleSwitch toggle, params SettingsExpanderItem[] items)
    {
        var group = Group(glyph, header, description, items);
        group.Footer = toggle;
        foreach (var item in items)
        {
            ControlledBy(item, toggle);
        }

        return group;
    }

    private static SettingsExpander ChoiceGroup<T>(string glyph, string header, string description, ComboBox selector, T disabledValue, params SettingsExpanderItem[] items)
    {
        var group = Group(glyph, header, description, items);
        group.Footer = selector;
        void Sync()
        {
            var isEnabled = !EqualityComparer<T>.Default.Equals(Selected(selector, disabledValue), disabledValue);
            foreach (var item in items)
            {
                item.IsEnabled = isEnabled;
            }
        }

        selector.SelectionChanged += (_, _) => Sync();
        Sync();
        return group;
    }

    private static SettingsExpanderItem Item(string header, string description, Control footer, ToggleSwitch? dependency = null)
    {
        var item = new SettingsExpanderItem
        {
            Content = header,
            Description = description,
            Footer = footer
        };
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

    private static void EnabledWhenNot<T>(Control target, ComboBox selector, T disabledValue)
    {
        void Sync() => target.IsEnabled = !EqualityComparer<T>.Default.Equals(Selected(selector, disabledValue), disabledValue);
        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    private static void VisibleWhen<T>(Control target, ComboBox selector, T visibleValue)
    {
        void Sync() => target.IsVisible = EqualityComparer<T>.Default.Equals(Selected(selector, visibleValue), visibleValue);
        selector.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    private static void VisibleWhenAny<T>(Control target, ComboBox selector, params T[] visibleValues)
    {
        void Sync()
        {
            var selected = selector.SelectedItem is Choice<T> choice ? choice.Value : default!;
            target.IsVisible = selected != null && visibleValues.Contains(selected);
        }

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
