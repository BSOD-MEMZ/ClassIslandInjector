namespace ClassIslandInjector.Automation;

/// <summary>
/// 设置项的值类型，决定自动化设置控件渲染哪种编辑器。
/// </summary>
public enum SettingValueKind
{
    Bool,
    Double,
    Int,
    String,
    Enum
}

/// <summary>
/// 单个插件设置项的元数据：属性名、显示名、分类、取值类型、取值范围与枚举选项。
/// 同时被自动化「添加行动」分组菜单与行动设置控件使用，保证插件的所有设置项都能
/// 通过 ClassIsland 自动化直接修改。
/// </summary>
public sealed class InjectorSettingSpec
{
    /// <summary>对应 <see cref="InjectorSettings"/> 的属性名（C# 属性名，区分大小写）。</summary>
    public required string PropertyName { get; init; }

    /// <summary>在自动化菜单与设置控件中显示的中文名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>所属菜单分组（中文名）。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>菜单图标（FluentSystemIcons-Resizable 码点，空心）。</summary>
    public required string IconGlyph { get; init; }

    /// <summary>取值类型。</summary>
    public required SettingValueKind Kind { get; init; }

    /// <summary>数值型最小值。</summary>
    public double Minimum { get; init; } = double.MinValue;

    /// <summary>数值型最大值。</summary>
    public double Maximum { get; init; } = double.MaxValue;

    /// <summary>数值型显示格式。</summary>
    public string NumberFormat { get; init; } = "0.##";

    /// <summary>枚举类型（Kind 为 Enum 时有效）。</summary>
    public Type? EnumType { get; init; }

    /// <summary>枚举选项（值 → 显示名）。</summary>
    public IReadOnlyList<KeyValuePair<object, string>> EnumOptions { get; init; } = [];

    /// <summary>设置控件中的简短说明。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 自动化「添加行动」菜单中的一个分组（对应设置页的一个分区）。
/// </summary>
public sealed class InjectorSettingGroup
{
    public required string Name { get; init; }

    public required string IconGlyph { get; init; }

    public required IReadOnlyList<InjectorSettingSpec> Settings { get; init; }
}

/// <summary>
/// 插件全部设置项的目录。新增设置属性时必须在此补充元数据，
/// 否则无法通过自动化修改该设置。
/// </summary>
public static class InjectorSettingCatalog
{
    /// <summary>自动化菜单的根分组名与图标。</summary>
    public const string RootGroupName = "样式注入器";
    public const string RootGroupIcon = "\uF42F";

    /// <summary>所有分组（顺序即菜单顺序）。</summary>
    public static readonly IReadOnlyList<InjectorSettingGroup> Groups = BuildGroups();

    /// <summary>所有设置项扁平列表，便于按属性名查找。</summary>
    public static readonly IReadOnlyList<InjectorSettingSpec> All = Groups.SelectMany(g => g.Settings).ToList();

    /// <summary>
    /// 按属性名查找设置项元数据。
    /// </summary>
    public static InjectorSettingSpec? Find(string propertyName) =>
        All.FirstOrDefault(s => s.PropertyName == propertyName);

    private static IReadOnlyList<InjectorSettingGroup> BuildGroups()
    {
        var groups = new List<InjectorSettingGroup>
        {
            new()
            {
                Name = "常规",
            IconGlyph = "\uE84F",
            Settings =
            [
                Spec("Enabled", "启用注入", "\uE84F", SettingValueKind.Bool,
                    "启用后由插件接管主界面根节点的视觉效果。"),
            ]
        },
        new()
        {
            Name = "基础变形",
            IconGlyph = "\uE113",
            Settings =
            [
                Spec("Opacity", "不透明度", "\uE113", SettingValueKind.Double, 0, 1, "0.##",
                    "控制主界面的整体透明度。"),
                Spec("Scale", "界面缩放", "\uE113", SettingValueKind.Double, 0.1, 5, "0.##",
                    "控制主界面的显示大小。"),
                Spec("Rotation", "旋转角度", "\uE113", SettingValueKind.Double, -360, 360, "0",
                    "以中心点旋转主界面。"),
                Spec("OffsetX", "水平偏移", "\uE113", SettingValueKind.Double, -2000, 2000, "0",
                    "向左或向右移动主界面。"),
                Spec("OffsetY", "垂直偏移", "\uE113", SettingValueKind.Double, -2000, 2000, "0",
                    "向上或向下移动主界面。"),
                Spec("Shape", "形状", "\uE113", SettingValueKind.Enum,
                    "主界面的整体形状（圆角半径、背景裁切等）。", typeof(IslandShape),
                    [KV(IslandShape.HostDefault, "跟随 ClassIsland"), KV(IslandShape.Rectangle, "矩形"),
                     KV(IslandShape.RoundedRectangle, "圆角矩形"), KV(IslandShape.Capsule, "胶囊")]),
                Spec("CornerRadius", "圆角半径", "\uE113", SettingValueKind.Double, 0, 500, "0",
                    "控制岛屿边角的圆润程度。"),
                Spec("CustomSizeEnabled", "固定显示大小", "\uEE83", SettingValueKind.Bool,
                    "启用后覆盖主界面根容器的宽度与高度。"),
                Spec("MainWindowWidth", "显示宽度", "\uEE83", SettingValueKind.Double, 160, 2000, "0",
                    "主界面显示区域的固定宽度。"),
                Spec("MainWindowHeight", "显示高度", "\uEE83", SettingValueKind.Double, 40, 800, "0",
                    "主界面显示区域的固定高度。"),
            ]
        },
        new()
        {
            Name = "动画与提醒",
            IconGlyph = "\uEFFF",
            Settings =
            [
                Spec("AnimationEnabled", "持续动画", "\uEFFF", SettingValueKind.Bool,
                    "打开后才会使用循环动画设置。"),
                Spec("AnimationMode", "动画类型", "\uEFFF", SettingValueKind.Enum,
                    "选择循环动画的运动方式。", typeof(IslandAnimationMode),
                    [KV(IslandAnimationMode.None, "无"), KV(IslandAnimationMode.Breathe, "呼吸"),
                     KV(IslandAnimationMode.Float, "浮动"), KV(IslandAnimationMode.Wave, "波浪")]),
                Spec("AnimationAmount", "动画幅度", "\uEFFF", SettingValueKind.Double, 0, 1, "0.##",
                    "控制循环动画的强弱。"),
                Spec("AnimationPeriodSeconds", "动画周期", "\uEFFF", SettingValueKind.Double, 0.2, 60, "0.##",
                    "完成一次循环所需的时间（秒）。"),
                Spec("VisibilityAnimation", "主界面显示动画", "\uEFFF", SettingValueKind.Enum,
                    "主界面出现或消失时使用的动画。", typeof(VisibilityAnimation),
                    [KV(VisibilityAnimation.None, "无"), KV(VisibilityAnimation.Fade, "淡入淡出"),
                     KV(VisibilityAnimation.Scale, "缩放"), KV(VisibilityAnimation.SlideFromTop, "从上方滑入"),
                     KV(VisibilityAnimation.SlideFromBottom, "从下方滑入")]),
                Spec("VisibilityDurationSeconds", "显示动画时长", "\uEFFF", SettingValueKind.Double, 0.1, 10, "0.##",
                    "主界面显示动画的时长（秒）。"),
                Spec("EmphasisAnimation", "提醒强调动画", "\uEFFF", SettingValueKind.Enum,
                    "收到提醒时使用的强调效果。", typeof(EmphasisAnimation),
                    [KV(EmphasisAnimation.None, "无"), KV(EmphasisAnimation.Pulse, "脉冲"),
                     KV(EmphasisAnimation.Bounce, "弹跳"), KV(EmphasisAnimation.Shake, "摇晃"),
                     KV(EmphasisAnimation.Flash, "闪烁")]),
                Spec("EmphasisAmount", "强调幅度", "\uEFFF", SettingValueKind.Double, 0, 1, "0.##",
                    "控制强调动画的强弱。"),
                Spec("EmphasisDurationSeconds", "强调时长", "\uEFFF", SettingValueKind.Double, 0.1, 10, "0.##",
                    "提醒强调动画的时长（秒）。"),
                Spec("NotificationTransition", "提醒遮罩动画", "\uEFFF", SettingValueKind.Enum,
                    "提醒遮罩出现和消失时的过渡效果。", typeof(NotificationTransition),
                    [KV(NotificationTransition.HostDefault, "跟随 ClassIsland"), KV(NotificationTransition.Fade, "淡入淡出"),
                     KV(NotificationTransition.SlideDown, "向下滑动"), KV(NotificationTransition.SlideUp, "向上滑动"),
                     KV(NotificationTransition.SlideLeft, "向左滑动"), KV(NotificationTransition.SlideRight, "向右滑动")]),
                Spec("NotificationTransitionDurationSeconds", "遮罩动画时长", "\uEFFF", SettingValueKind.Double, 0.05, 5, "0.##",
                    "提醒遮罩动画的时长（秒）。"),
                Spec("RippleType", "提醒 Ripple", "\uEFFF", SettingValueKind.Enum,
                    "提醒时的扩散效果。", typeof(RippleType),
                    [KV(RippleType.None, "无"), KV(RippleType.Ring, "单环"), KV(RippleType.DoubleRing, "双环"),
                     KV(RippleType.Glow, "光晕"), KV(RippleType.Square, "方框"), KV(RippleType.Hanabi, "花火")]),
                Spec("RippleColor", "Ripple 颜色", "\uEFFF", SettingValueKind.String,
                    "支持透明度的提醒扩散颜色（如 #AA7DD3FC）。"),
                Spec("RippleDurationSeconds", "Ripple 时长", "\uEFFF", SettingValueKind.Double, 0.1, 10, "0.##",
                    "扩散效果的播放时长（秒）。"),
                Spec("RippleThickness", "Ripple 线宽", "\uEFFF", SettingValueKind.Double, 0.5, 40, "0.##",
                    "环形或方框 Ripple 的线条粗细。"),
                Spec("RippleOpacity", "全局不透明度", "\uEFFF", SettingValueKind.Double, 0.1, 1, "0.##",
                    "全局降低 Ripple 效果的透明度（1 为不降低）。"),
                Spec("RippleConstraintEnabled", "限制扩散范围", "\uEFFF", SettingValueKind.Bool,
                    "以主界面中心为圆心创建圆形裁剪，约束所有类型 Ripple 的扩散范围。"),
                Spec("RippleConstraintRadius", "约束半径", "\uEFFF", SettingValueKind.Double, 0, 2000, "0",
                    "Ripple 扩散的圆形约束半径（像素），0 为自动按主界面大小计算。"),
                Spec("PrepareOnClassStyle", "即将上课样式", "\uE4C4", SettingValueKind.Enum,
                    "即将上课倒计时期间显示的特效样式。", typeof(PrepareOnClassStyle),
                    [KV(PrepareOnClassStyle.None, "无"), KV(PrepareOnClassStyle.Arrows, "箭头"),
                     KV(PrepareOnClassStyle.PulseRing, "扩散光环"), KV(PrepareOnClassStyle.Scanline, "扫描线")]),
                Spec("CountdownArrowColor", "箭头颜色", "\uE4C4", SettingValueKind.String,
                    "支持透明度的倒计时箭头颜色。"),
                Spec("CountdownArrowCount", "箭头组数", "\uE4C4", SettingValueKind.Int, 1, 24, "0",
                    "屏幕上同时滑动的箭头组数量。"),
                Spec("CountdownArrowPerGroup", "每组箭头数", "\uE4C4", SettingValueKind.Int, 1, 12, "0",
                    "每组内包含的箭头数量（2 即经典的 >> 效果）。"),
                Spec("CountdownArrowSpacing", "组内箭头间距", "\uE4C4", SettingValueKind.Double, 0, 100, "0",
                    "同一组内相邻箭头之间的间距（像素）。"),
                Spec("CountdownArrowGroupSpacing", "组间间距", "\uE4C4", SettingValueKind.Double, 0, 400, "0",
                    "相邻箭头组之间的额外间距（像素）。"),
                Spec("CountdownArrowSpeed", "滑动速度", "\uE4C4", SettingValueKind.Double, 0.1, 12, "0.##",
                    "倒计时箭头的移动速度。"),
                Spec("CountdownArrowThickness", "箭头线宽", "\uE4C4", SettingValueKind.Double, 0.5, 20, "0.##",
                    "倒计时箭头的线条粗细。"),
                Spec("CountdownPulseColor", "光环颜色", "\uE4C4", SettingValueKind.String,
                    "支持透明度的扩散光环颜色。"),
                Spec("CountdownPulseThickness", "光环线宽", "\uE4C4", SettingValueKind.Double, 0.5, 20, "0.##",
                    "扩散光环的线条粗细。"),
                Spec("CountdownPulseSpeed", "光环扩散速度", "\uE4C4", SettingValueKind.Double, 0.1, 8, "0.##",
                    "扩散光环每秒扩散的圈数。"),
                Spec("CountdownPulseMaxRadius", "光环最大半径", "\uE4C4", SettingValueKind.Double, 0.1, 1, "0.##",
                    "扩散光环最大半径占主界面宽高中较小值的比例。"),
                Spec("CountdownScanColor", "扫描颜色", "\uE4C4", SettingValueKind.String,
                    "支持透明度的扫描线颜色。"),
                Spec("CountdownScanThickness", "扫描线宽", "\uE4C4", SettingValueKind.Double, 0.5, 20, "0.##",
                    "扫描线的粗细。"),
                Spec("CountdownScanSpeed", "扫描速度", "\uE4C4", SettingValueKind.Double, 0.1, 8, "0.##",
                    "扫描线每秒扫描次数。"),
                Spec("CountdownScanDirection", "扫描方向", "\uE4C4", SettingValueKind.Enum,
                    "扫描线运动方向。", typeof(ScanlineDirection),
                    [KV(ScanlineDirection.Horizontal, "横向（上下扫）"), KV(ScanlineDirection.Vertical, "纵向（左右扫）")]),
                Spec("CountdownScanTailEnabled", "渐变尾迹", "\uE4C4", SettingValueKind.Bool,
                    "扫描线是否带渐变淡出的尾迹。"),
            ]
        },
        new()
        {
            Name = "背景、阴影与边框",
            IconGlyph = "\uE520",
            Settings =
            [
                Spec("CustomBackgroundEnabled", "自定义背景色", "\uE520", SettingValueKind.Bool,
                    "关闭时保留 ClassIsland 自身的背景颜色。"),
                Spec("BackgroundColor", "背景色", "\uE520", SettingValueKind.String,
                    "支持透明度的主界面背景颜色（如 #CC202020）。"),
                Spec("GradientEnabled", "线性渐变", "\uE520", SettingValueKind.Bool,
                    "开启后会使用渐变终止色。"),
                Spec("GradientEndColor", "渐变终止色", "\uE520", SettingValueKind.String,
                    "线性渐变背景的结束颜色（如 #CC4040A0）。"),
                Spec("GradientDirection", "渐变方向", "\uE520", SettingValueKind.Enum,
                    "线性渐变从起始色到终止色的方向。", typeof(GradientDirection),
                    [KV(GradientDirection.TopLeftToBottomRight, "左上 → 右下"), KV(GradientDirection.TopToBottom, "上 → 下"),
                     KV(GradientDirection.LeftToRight, "左 → 右"), KV(GradientDirection.BottomLeftToTopRight, "左下 → 右上"),
                     KV(GradientDirection.BottomToTop, "下 → 上"), KV(GradientDirection.RightToLeft, "右 → 左"),
                     KV(GradientDirection.TopRightToBottomLeft, "右上 → 左下"), KV(GradientDirection.BottomRightToTopLeft, "右下 → 左上")]),
                Spec("BackgroundTextureType", "背景纹理", "\uE7B5", SettingValueKind.Enum,
                    "背景填充纹理类型，可与背景图片、背景色叠加。", typeof(BackgroundTexture),
                    [KV(BackgroundTexture.None, "无"), KV(BackgroundTexture.Grid, "网格线"),
                     KV(BackgroundTexture.Dots, "点阵"), KV(BackgroundTexture.DiagonalLines, "斜线"),
                     KV(BackgroundTexture.Cross, "十字网格")]),
                Spec("BackgroundTextureColor", "纹理颜色", "\uE7B5", SettingValueKind.String,
                    "支持透明度的纹理线条颜色（如 #2EFFFFFF）。"),
                Spec("BackgroundTextureSize", "纹理大小", "\uE7B5", SettingValueKind.Double, 8, 80, "0",
                    "单个纹理单元的大小（像素）。"),
                Spec("DynamicBackgroundColorEnabled", "动态背景取色", "\uF361", SettingValueKind.Bool,
                    "读取当前 SMTC 专辑封面并自动提取背景主题色。"),
                Spec("RevertColorsWhenPaused", "暂停/停止时恢复原色", "\uF361", SettingValueKind.Bool,
                    "媒体暂停或停止播放时，把背景、边框、阴影平滑恢复为配置的原始颜色。"),
                Spec("AlbumColorPollingIntervalSeconds", "兜底刷新间隔", "\uF361", SettingValueKind.Double, 0.5, 120, "0.##",
                    "SMTC 事件驱动失效时的兜底刷新间隔（秒）。"),
                Spec("AlbumColorTransitionSeconds", "颜色过渡时长", "\uF361", SettingValueKind.Double, 0, 10, "0.##",
                    "专辑颜色变化时平滑过渡到新颜色的时长（秒），0 为立即切换。"),
                Spec("ShadowEnabled", "阴影", "\uE472", SettingValueKind.Bool,
                    "为岛屿添加投影效果。"),
                Spec("ShadowColor", "阴影颜色", "\uE472", SettingValueKind.String,
                    "支持透明度的阴影颜色（如 #99000000）。"),
                Spec("ShadowBlur", "阴影模糊", "\uE472", SettingValueKind.Double, 0, 200, "0",
                    "控制投影的柔和程度。"),
                Spec("ShadowOffsetX", "阴影水平偏移", "\uE472", SettingValueKind.Double, -200, 200, "0",
                    "控制投影向左或向右偏移。"),
                Spec("ShadowOffsetY", "阴影垂直偏移", "\uE472", SettingValueKind.Double, -200, 200, "0",
                    "控制投影向上或向下偏移。"),
                Spec("ShadowOpacity", "阴影不透明度", "\uE472", SettingValueKind.Double, 0, 1, "0.##",
                    "控制投影的深浅。"),
                Spec("DynamicShadowColorEnabled", "动态阴影取色", "\uF361", SettingValueKind.Bool,
                    "阴影色调跟随专辑封面，使用 Material You 深色中性色。"),
                Spec("BorderEnabled", "岛屿边框", "\uE254", SettingValueKind.Bool,
                    "为岛屿添加细边框。"),
                Spec("BorderColor", "边框颜色", "\uE254", SettingValueKind.String,
                    "支持透明度的边框颜色（如 #99FFFFFF）。"),
                Spec("BorderThickness", "边框线宽", "\uE254", SettingValueKind.Double, 0.25, 20, "0.##",
                    "控制岛屿边框的粗细。"),
                Spec("DynamicBorderColorEnabled", "动态边框取色", "\uF361", SettingValueKind.Bool,
                    "边框色调跟随专辑封面，使用 Material You 主色调。"),
            ]
        },
        new()
        {
            Name = "主界面底图",
            IconGlyph = "\uF42D",
            Settings =
            [
                Spec("WallpaperEnabled", "主界面底图", "\uF42D", SettingValueKind.Bool,
                    "层级：底图 → 底色 → 组件。"),
                Spec("WallpaperSource", "图片来源", "\uF42D", SettingValueKind.Enum,
                    "选择底图的来源。", typeof(WallpaperSource),
                    [KV(WallpaperSource.LocalImage, "本地图片"), KV(WallpaperSource.FolderSlideshow, "文件夹幻灯片"),
                     KV(WallpaperSource.SmtcAlbum, "SMTC 专辑封面")]),
                Spec("WallpaperPath", "图片 / 文件夹", "\uF42D", SettingValueKind.String,
                    "底图文件或幻灯片文件夹的完整路径。"),
                Spec("WallpaperOpacity", "图片不透明度", "\uF42D", SettingValueKind.Double, 0, 1, "0.##",
                    "底图的整体透明度。"),
                Spec("WallpaperDisplayMode", "显示方式", "\uF42D", SettingValueKind.Enum,
                    "图片在岛屿内的显示方式。", typeof(WallpaperDisplayMode),
                    [KV(WallpaperDisplayMode.Fill, "填充（裁剪）"), KV(WallpaperDisplayMode.Fit, "适应（完整显示）"),
                     KV(WallpaperDisplayMode.Stretch, "拉伸（变形）"), KV(WallpaperDisplayMode.Tile, "平铺")]),
                Spec("WallpaperScale", "缩放", "\uF42D", SettingValueKind.Double, 1, 5, "0.##",
                    "底图的缩放倍率（1 为按显示方式适应，大于 1 放大裁剪）。"),
                Spec("WallpaperOffsetX", "水平偏移", "\uF42D", SettingValueKind.Double, -0.5, 0.5, "0.##",
                    "底图的水平偏移（相对图片宽度，-0.5 到 0.5）。"),
                Spec("WallpaperOffsetY", "垂直偏移", "\uF42D", SettingValueKind.Double, -0.5, 0.5, "0.##",
                    "底图的垂直偏移（相对图片高度，-0.5 到 0.5）。"),
                Spec("WallpaperSlideshowIntervalSeconds", "幻灯片间隔", "\uF42D", SettingValueKind.Double, 2, 3600, "0",
                    "文件夹幻灯片切换间隔（秒）。"),
                Spec("WallpaperBlurRadius", "模糊", "\uF42D", SettingValueKind.Double, 0, 60, "0.##",
                    "对底图应用高斯模糊（0 为关闭）。"),
            ]
        },
        new()
        {
            Name = "样式表",
            IconGlyph = "\uF263",
            Settings =
            [
                Spec("StyleSheetPath", "覆盖样式表路径", "\uF263", SettingValueKind.String,
                    "填写 .axaml 样式表的完整路径。"),
                Spec("WatchStyleSheet", "自动热重载", "\uF263", SettingValueKind.Bool,
                    "保存样式表后自动重新加载。"),
            ]
        }
        };

        // 把每个设置项归入其所属分组，供自动化设置控件与菜单使用。
        foreach (var group in groups)
        {
            foreach (var spec in group.Settings)
            {
                spec.Category = group.Name;
            }
        }

        return groups;
    }

    private static InjectorSettingSpec Spec(string propertyName, string displayName, string iconGlyph,
        SettingValueKind kind, string? description = null) => new()
    {
        PropertyName = propertyName,
        DisplayName = displayName,
        IconGlyph = iconGlyph,
        Kind = kind,
        Description = description
    };

    private static InjectorSettingSpec Spec(string propertyName, string displayName, string iconGlyph,
        SettingValueKind kind, double minimum = double.MinValue, double maximum = double.MaxValue,
        string numberFormat = "0.##", string? description = null) => new()
    {
        PropertyName = propertyName,
        DisplayName = displayName,
        IconGlyph = iconGlyph,
        Kind = kind,
        Description = description,
        Minimum = minimum,
        Maximum = maximum,
        NumberFormat = numberFormat
    };

    private static InjectorSettingSpec Spec(string propertyName, string displayName, string iconGlyph,
        SettingValueKind kind, string? description, Type enumType, IReadOnlyList<KeyValuePair<object, string>> options) => new()
    {
        PropertyName = propertyName,
        DisplayName = displayName,
        IconGlyph = iconGlyph,
        Kind = kind,
        Description = description,
        EnumType = enumType,
        EnumOptions = options
    };

    private static KeyValuePair<object, string> KV<T>(T value, string text) where T : struct, Enum =>
        new(value, text);
}
