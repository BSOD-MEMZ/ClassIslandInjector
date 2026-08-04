using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
[SettingsPageInfo("miku.classisland.injector", "样式注入器", "\uEC4A", "\uEC4A")]
public sealed class InjectorSettingsPage : SettingsPageBase
{
    private readonly ToggleSwitch _enabled = Toggle();
    private readonly Slider _opacity = Slider(0, 1, 0.05);
    private readonly Spin _scale = Spinner(0.1, 5, 0.05);
    private readonly Spin _rotation = Spinner(-360, 360, 1, "0");
    private readonly Spin _offsetX = Spinner(-2000, 2000, 1, "0");
    private readonly Spin _offsetY = Spinner(-2000, 2000, 1, "0");
    private readonly ToggleSwitch _animationEnabled = Toggle();
    private readonly ComboBox _animationMode = Combo(IslandAnimationModes);
    private readonly Slider _animationAmount = Slider(0, 1, 0.01);
    private readonly Spin _animationPeriod = Spinner(0.2, 60, 0.1);
    private readonly ComboBox _animationPreset = Combo(AnimationPresets);
    private readonly TextBox _styleSheetPath = new() { MinWidth = 280 };
    private readonly ToggleSwitch _watchStyleSheet = Toggle();

    private readonly Spin _cornerRadius = Spinner(0, 500, 1, "0");
    private readonly ToggleSwitch _customSize = Toggle();
    private readonly Spin _mainWindowWidth = Spinner(160, 2000, 10, "0");
    private readonly Spin _mainWindowHeight = Spinner(40, 800, 10, "0");
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
    private readonly ToggleSwitch _hanabiConstraint = Toggle();
    private readonly ToggleSwitch _countdownArrows = Toggle();
    private readonly ColorPicker _countdownArrowColor = ColorPicker();
    private readonly Spin _countdownArrowCount = Spinner(2, 24, 1, "0");
    private readonly Spin _countdownArrowSpeed = Spinner(0.1, 12, 0.1);
    private readonly Spin _countdownArrowThickness = Spinner(0.5, 8, 0.5);
    private readonly ComboBox _preset = Combo(StylePresets);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private IslandVisualEditorWindow? _visualEditorWindow;

    private static readonly Choice<StylePreset>[] StylePresets =
    [
        new(StylePreset.GlassCapsule, "玻璃主题"),
        new(StylePreset.NeonPulse, "霓虹主题"),
        new(StylePreset.MaimaiHanabi, "花火主题"),
        new(StylePreset.Minimal, "极简主题"),
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

    private static readonly Choice<AnimationPreset>[] AnimationPresets =
    [
        new(AnimationPreset.Still, "静止"),
        new(AnimationPreset.SoftBreathe, "柔和呼吸"),
        new(AnimationPreset.GentleFloat, "轻柔浮动"),
        new(AnimationPreset.DynamicWave, "动态波浪"),
        new(AnimationPreset.AlertShake, "提醒摇晃"),
        new(AnimationPreset.HanabiCelebration, "花火庆祝"),
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

    public InjectorSettingsPage()
    {
        Content = BuildContent();
        WireVisualEditor();
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
        panel.Children.Add(Setting("\uE84F", "运行时注入", "启用后由插件接管主界面根节点的视觉效果。", _enabled));

        AddSection(panel, "\uF42F", "预设");
        panel.Children.Add(Setting("\uF42F", "样式预设", "一键套用形状、配色、阴影、边框与提醒效果；不会修改不透明度、缩放、位置、旋转与圆角半径等基础变形设置。", _preset));
        var presetActions = Actions("应用样式预设", ApplyStylePreset, "恢复插件默认", ResetToDefaults);
        panel.Children.Add(Setting("\uE161", "预设操作", "恢复默认不会修改 Overrides.axaml。", presetActions));

        AddSection(panel, "\uE288", "可视化编辑器");
        panel.Children.Add(Setting("\uE288", "打开可视化编辑器", "在独立窗口中像编辑演示文稿一样拖动、旋转、缩放岛屿，并即时应用到主界面。", Button("打开编辑器", OpenVisualEditor)));

        AddSection(panel, "\uE113", "基础变形");
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "与 ClassIsland 原生设置重叠",
            Message = "不透明度、缩放、位置与圆角均可在 ClassIsland 的外观页修改。在此再次覆盖可能与原生设置产生少量兼容性问题。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Group("\uE113", "基础变形", "这些值会覆盖并叠加在 ClassIsland 的主界面外观设置之上。",
            Item("不透明度", "控制主界面的整体透明度。", _opacity),
            Item("界面缩放", "控制主界面的显示大小。", _scale),
            Item("水平偏移", "向左或向右移动主界面。", _offsetX),
            Item("垂直偏移", "向上或向下移动主界面。", _offsetY),
            Item("圆角半径", "控制岛屿边角的圆润程度。", _cornerRadius),
            Item("旋转角度", "以中心点旋转主界面。", _rotation)));
        panel.Children.Add(SwitchableGroup("\uEE83", "固定显示大小", "启用后覆盖主界面根容器的宽度与高度；关闭时完全沿用 ClassIsland 原生布局。", _customSize,
            Item("显示宽度", "主界面显示区域的固定宽度。", _mainWindowWidth),
            Item("显示高度", "主界面显示区域的固定高度。", _mainWindowHeight)));

        AddSection(panel, "\uF265", "动画与提醒");
        panel.Children.Add(SwitchableGroup("\uEFFF", "持续动画", "打开后才会使用下方的循环动画设置。", _animationEnabled,
            Item("动画类型", "选择循环动画的运动方式。", _animationMode),
            Item("动画幅度", "控制循环动画的强弱。", _animationAmount),
            Item("动画周期", "完成一次循环所需的时间（秒）。", _animationPeriod)));
        panel.Children.Add(Setting("\uEFFF", "动画预设", "仅调整动效、提醒和 Ripple，不会改变形状、背景与阴影。", _animationPreset));
        panel.Children.Add(Setting("\uE161", "应用动画预设", "立即应用当前选中的动画预设。", Button("应用动画预设", ApplyAnimationPreset)));
        panel.Children.Add(ChoiceGroup("\uEFFF", "主界面显示动画", "选择主界面出现或消失时使用的动画。", _visibilityAnimation, VisibilityAnimation.None,
            Item("显示动画时长", "主界面显示动画的时长（秒）。", _visibilityDuration)));
        panel.Children.Add(ChoiceGroup("\uEFFF", "提醒强调动画", "选择收到提醒时使用的强调效果。", _emphasisAnimation, EmphasisAnimation.None,
            Item("强调幅度", "控制强调动画的强弱。", _emphasisAmount),
            Item("强调时长", "提醒强调动画的时长（秒）。", _emphasisDuration)));
        panel.Children.Add(ChoiceGroup("\uEFFF", "提醒遮罩动画", "选择提醒遮罩出现和消失时的过渡效果。", _notificationTransition, NotificationTransition.HostDefault,
            Item("遮罩动画时长", "提醒遮罩动画的时长（秒）。", _notificationTransitionDuration)));
        var rippleColorItem = Item("Ripple 颜色", "支持透明度的提醒扩散颜色。", _rippleColor);
        var rippleDurationItem = Item("Ripple 时长", "扩散效果的播放时长（秒）。", _rippleDuration);
        var rippleThicknessItem = Item("Ripple 线宽", "环形或方框 Ripple 的线条粗细。", _rippleThickness);
        var hanabiConstraintItem = Item("限制花火扩散", "以 ClassIsland 为圆心创建圆形裁剪遮罩，避免花火扩张至整个屏幕。", _hanabiConstraint);
        var rippleGroup = ChoiceGroup("\uEFFF", "提醒 Ripple", "选择提醒时的扩散效果。花火使用固定的原始配色与线宽。", _rippleType, RippleType.None,
            rippleColorItem, rippleDurationItem, rippleThicknessItem, hanabiConstraintItem);
        EnabledWhenNot(rippleColorItem, _rippleType, RippleType.Hanabi);
        EnabledWhenNot(rippleThicknessItem, _rippleType, RippleType.Hanabi);
        VisibleWhen(hanabiConstraintItem, _rippleType, RippleType.Hanabi);
        panel.Children.Add(rippleGroup);
        panel.Children.Add(SwitchableGroup("\uE4C4", "倒计时箭头", "即将上课时显示箭头滑动效果。", _countdownArrows,
            Item("箭头颜色", "支持透明度的倒计时箭头颜色。", _countdownArrowColor),
            Item("箭头组数", "每组显示为一对 >> 箭头。", _countdownArrowCount),
            Item("滑动速度", "倒计时箭头的移动速度。", _countdownArrowSpeed),
            Item("箭头线宽", "倒计时箭头的线条粗细。", _countdownArrowThickness)));

        AddSection(panel, "\uEC4A", "背景、阴影与边框");
        var backgroundColorItem = Item("背景色", "支持透明度的主界面背景颜色。", _backgroundColor);
        var backgroundGroup = SwitchableGroup("\uE520", "自定义背景色", "关闭时保留 ClassIsland 自身的背景颜色。", _customBackground,
            backgroundColorItem,
            Item("动态专辑封面取色", "读取当前 SMTC 专辑封面，并使用 Material You（Monet）算法自动提取主题色。", _dynamicBackgroundColor),
            Item("线性渐变", "开启后会使用渐变终止色。", _gradient),
            Item("渐变终止色", "线性渐变背景的结束颜色。", _gradientEndColor, _gradient));
        EnabledWhenManualColor(backgroundColorItem, _customBackground, _dynamicBackgroundColor);
        panel.Children.Add(backgroundGroup);
        panel.Children.Add(Group("\uF361", "动态取色轮询", "SMTC 采用事件驱动：媒体变化（切歌/换封面）时即时更新；下方的间隔仅作为兜底刷新，应对个别应用事件不触发的情况。",
            Item("暂停/停止时恢复原色", "媒体暂停或停止播放时，把背景、边框、阴影从专辑取色平滑恢复为你配置的原始颜色，恢复播放后再跟随专辑。", _revertColorsWhenPaused),
            Item("兜底刷新间隔", "事件驱动失效时的兜底刷新间隔（秒）。", _albumColorPollingInterval),
            Item("颜色过渡时长", "专辑颜色变化时，背景、边框、阴影平滑过渡到新颜色的时长（秒），0 为立即切换。", _albumColorTransition)));
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

        AddSection(panel, "\uF42D", "主界面底图");
        var wallpaperPathItem = Item("图片 / 文件夹", "底图文件或幻灯片文件夹的路径。", WallpaperPathFooter());
        var wallpaperSlideshowItem = Item("幻灯片间隔", "文件夹幻灯片切换间隔（秒）。", _wallpaperSlideshowInterval);
        var wallpaperGroup = SwitchableGroup("\uF42D", "主界面底图", "层级：底图 → 底色 → 组件。SMTC 来源由事件驱动即时更新，兜底刷新与图片过渡时长沿用上方“动态取色轮询”设置。", _wallpaperEnabled,
            Item("图片来源", "选择底图的来源：本地图片、文件夹幻灯片或 SMTC 专辑封面。", _wallpaperSource),
            wallpaperPathItem,
            Item("图片不透明度", "底图的整体透明度。", _wallpaperOpacity),
            Item("显示方式", "图片在岛屿内的显示方式。", _wallpaperDisplayMode),
            Item("缩放", "底图的缩放倍率（1 为按显示方式适应，大于 1 放大裁剪）。", _wallpaperScale),
            Item("水平偏移", "底图的水平偏移（相对图片宽度，-0.5 到 0.5）。", _wallpaperOffsetX),
            Item("垂直偏移", "底图的垂直偏移（相对图片高度，-0.5 到 0.5）。", _wallpaperOffsetY),
            wallpaperSlideshowItem);
        VisibleWhenAny(wallpaperPathItem, _wallpaperSource, WallpaperSource.LocalImage, WallpaperSource.FolderSlideshow);
        VisibleWhen(wallpaperSlideshowItem, _wallpaperSource, WallpaperSource.FolderSlideshow);
        panel.Children.Add(wallpaperGroup);

        AddSection(panel, "\uF263", "高级样式表");
        panel.Children.Add(Setting("\uF263", "覆盖样式表路径", "填写 .axaml 样式表的完整路径。", _styleSheetPath));
        panel.Children.Add(Setting("\uE161", "自动热重载", "保存样式表后自动重新加载。", _watchStyleSheet));

        AddSection(panel, "\uE74D", "卸载与数据清理");
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "删除所有数据",
            Message = "将清除本插件在 ClassIsland 中创建的全部配置与数据（设置、覆盖样式表、诊断日志等），并把主界面恢复为原生状态。此操作不可恢复，执行后即可安全卸载插件。",
            IsOpen = true,
            IsClosable = false
        });
        panel.Children.Add(Setting("\uE74D", "删除所有数据", "一键清空插件全部数据并恢复主界面，让插件回到“全新安装”状态，之后可安全卸载。", Button("删除所有数据", DeleteAllData)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Button("保存并应用", SaveAndApply));
        actions.Children.Add(Button("重载样式表", ReloadStyleSheet));
        actions.Children.Add(Button("预览 Ripple", PreviewRipple));
        panel.Children.Add(actions);
        panel.Children.Add(_status);
        return new ScrollViewer { Content = panel };
    }

    private void ApplyStylePreset()
    {
        var preset = Selected(_preset, StylePreset.GlassCapsule);
        InjectorRuntime.Settings.ApplyPreset(preset);
        LoadFromSettings();
        _status.Text = $"已应用“{Display(StylePresets, preset)}”预设；不透明度、缩放、位置、旋转与圆角半径保持不变。";
    }

    private void ApplyAnimationPreset()
    {
        var preset = Selected(_animationPreset, AnimationPreset.Still);
        InjectorRuntime.Settings.ApplyAnimationPreset(preset);
        LoadFromSettings();
        _status.Text = $"已应用“{Display(AnimationPresets, preset)}”动画预设；形状、背景和阴影保持不变。";
    }

    private void ResetToDefaults()
    {
        InjectorRuntime.Settings.ResetToDefaults();
        LoadFromSettings();
        _status.Text = "已恢复插件默认设置；Overrides.axaml 未被修改。";
    }

    private void ReloadStyleSheet()
    {
        InjectorRuntime.ReloadStyleSheet();
        _status.Text = "已请求重载样式表；若样式表存在语法错误，ClassIsland 会保留稳定运行状态。";
    }

    private void PreviewRipple()
    {
        SaveAndApply();
        InjectorRuntime.PreviewRipple();
        _status.Text = "正在主界面中心预览当前 Ripple。";
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
                     _opacity, _scale, _rotation, _offsetX, _offsetY, _cornerRadius, _customSize, _mainWindowWidth, _mainWindowHeight,
                     _customBackground, _backgroundColor, _dynamicBackgroundColor, _dynamicBorderColor, _dynamicShadowColor,
                     _albumColorPollingInterval, _albumColorTransition, _gradient, _gradientEndColor,
                     _shadow, _shadowColor, _shadowBlur, _shadowOffsetX, _shadowOffsetY, _shadowOpacity,
                     _border, _borderColor, _borderThickness
                 })
        {
            control.PropertyChanged += (_, _) => RefreshVisualEditor();
        }
    }

    private void RefreshVisualEditor()
    {
        var state = new IslandPreviewState(
            _opacity.Value,
            _scale.DoubleValue,
            _rotation.DoubleValue,
            _offsetX.DoubleValue,
            _offsetY.DoubleValue,
            _cornerRadius.DoubleValue,
            _customSize.IsChecked == true,
            _mainWindowWidth.DoubleValue,
            _mainWindowHeight.DoubleValue,
            _customBackground.IsChecked == true,
            _backgroundColor.Color,
            _gradient.IsChecked == true,
            _gradientEndColor.Color,
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
        window.Editor.TransformEdited += (_, e) =>
        {
            _offsetX.DoubleValue = e.OffsetX;
            _offsetY.DoubleValue = e.OffsetY;
            _scale.DoubleValue = e.Scale;
            _rotation.DoubleValue = e.Rotation;
        };
        window.Editor.SizeEdited += (_, e) =>
        {
            _customSize.IsChecked = true;
            _mainWindowWidth.DoubleValue = e.Width;
            _mainWindowHeight.DoubleValue = e.Height;
        };
        window.Editor.CornerRadiusEdited += (_, e) => _cornerRadius.DoubleValue = e.Value;
        window.Editor.TransformEditCompleted += (_, _) => SaveAndApply();
        window.ApplyRequested += (_, _) => SaveAndApply();
        window.CenterRequested += (_, _) => window.Editor.Center();
        window.ResetRequested += (_, _) => window.Editor.ResetTransform();
        window.BackgroundColorEdited += color =>
        {
            _customBackground.IsChecked = true;
            _dynamicBackgroundColor.IsChecked = false;
            _backgroundColor.Color = color;
            SaveAndApply();
        };
        window.GradientEdited += enabled => { _gradient.IsChecked = enabled; SaveAndApply(); };
        window.GradientEndColorEdited += color => { _gradientEndColor.Color = color; SaveAndApply(); };
        window.ShadowEdited += enabled => { _shadow.IsChecked = enabled; SaveAndApply(); };
        window.ShadowColorEdited += color => { _dynamicShadowColor.IsChecked = false; _shadowColor.Color = color; SaveAndApply(); };
        window.ShadowBlurEdited += value => { _shadowBlur.DoubleValue = value; SaveAndApply(); };
        window.ShadowOpacityEdited += value => { _shadowOpacity.Value = value; SaveAndApply(); };
        window.OpacityEdited += value => { _opacity.Value = value; SaveAndApply(); };
        window.CornerRadiusEdited += value => { _cornerRadius.DoubleValue = value; SaveAndApply(); };
        window.Closed += (_, _) => _visualEditorWindow = null;
        RefreshVisualEditor();
        window.Show();
    }

    private void LoadFromSettings()
    {
        var settings = InjectorRuntime.Settings;
        _enabled.IsChecked = settings.Enabled;
        _opacity.Value = settings.Opacity;
        _scale.DoubleValue = settings.Scale;
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
        _customSize.IsChecked = settings.CustomSizeEnabled;
        _mainWindowWidth.DoubleValue = settings.MainWindowWidth;
        _mainWindowHeight.DoubleValue = settings.MainWindowHeight;
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
        _hanabiConstraint.IsChecked = settings.HanabiConstraintEnabled;
        _countdownArrows.IsChecked = settings.CountdownArrowsEnabled;
        _countdownArrowColor.Color = ReadColor(settings.CountdownArrowColor, Color.FromArgb(0xBF, 0xF8, 0xFA, 0xFC));
        _countdownArrowCount.DoubleValue = settings.CountdownArrowCount;
        _countdownArrowSpeed.DoubleValue = settings.CountdownArrowSpeed;
        _countdownArrowThickness.DoubleValue = settings.CountdownArrowThickness;
        Select(_preset, StylePresets, StylePreset.GlassCapsule);
        Select(_animationPreset, AnimationPresets, AnimationPreset.Still);
    }

    private void SaveAndApply()
    {
        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        try
        {
            settings.Enabled = _enabled.IsChecked == true;
            settings.Opacity = _opacity.Value;
            settings.Scale = _scale.DoubleValue;
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

            settings.CustomSizeEnabled = _customSize.IsChecked == true;
            settings.MainWindowWidth = _mainWindowWidth.DoubleValue;
            settings.MainWindowHeight = _mainWindowHeight.DoubleValue;
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
            settings.HanabiConstraintEnabled = _hanabiConstraint.IsChecked == true;
            settings.CountdownArrowsEnabled = _countdownArrows.IsChecked == true;
            settings.CountdownArrowColor = _countdownArrowColor.Color.ToString();
            settings.CountdownArrowCount = (int)Math.Round(_countdownArrowCount.DoubleValue);
            settings.CountdownArrowSpeed = _countdownArrowSpeed.DoubleValue;
            settings.CountdownArrowThickness = _countdownArrowThickness.DoubleValue;
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
