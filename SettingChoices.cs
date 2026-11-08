namespace ClassIslandInjector;

/// <summary>下拉框的一个候选项：值 + 显示名。</summary>
internal sealed record SettingChoice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 设置页各下拉框的候选项（顺序即显示顺序）。设置页与 RemoteCI 远程设置页共用这一份，
/// 保证 WebUI 里的选项、显示名与本机设置页完全一致；纯数据，不引用任何 UI 类型。
/// </summary>
internal static class SettingChoices
{
    public static readonly SettingChoice<VideoFillFit>[] VideoFillFits =
    [
        new(VideoFillFit.Fill, "填充（裁剪）"),
        new(VideoFillFit.Fit, "适应（完整显示）"),
        new(VideoFillFit.Stretch, "拉伸（变形）"),
    ];

    public static readonly SettingChoice<IslandAnimationMode>[] IslandAnimationModes =
    [
        new(IslandAnimationMode.Breathe, "呼吸"),
        new(IslandAnimationMode.Float, "浮动"),
        new(IslandAnimationMode.Wave, "波浪"),
    ];

    public static readonly SettingChoice<VisibilityAnimation>[] VisibilityAnimations =
    [
        new(VisibilityAnimation.Fade, "淡入淡出"),
        new(VisibilityAnimation.Scale, "缩放"),
        new(VisibilityAnimation.SlideFromTop, "从上方滑入"),
        new(VisibilityAnimation.SlideFromBottom, "从下方滑入"),
    ];

    public static readonly SettingChoice<EmphasisAnimation>[] EmphasisAnimations =
    [
        new(EmphasisAnimation.Pulse, "脉冲"),
        new(EmphasisAnimation.Bounce, "弹跳"),
        new(EmphasisAnimation.Shake, "摇晃"),
        new(EmphasisAnimation.Flash, "闪烁"),
    ];

    public static readonly SettingChoice<NotificationTransition>[] NotificationTransitions =
    [
        new(NotificationTransition.Fade, "淡入淡出"),
        new(NotificationTransition.SlideDown, "向下滑动"),
        new(NotificationTransition.SlideUp, "向上滑动"),
        new(NotificationTransition.SlideLeft, "向左滑动"),
        new(NotificationTransition.SlideRight, "向右滑动"),
    ];

    public static readonly SettingChoice<RippleType>[] RippleTypes =
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
        new(RippleType.Pjsk, "PJSK判定（高级）"),
    ];

    public static readonly SettingChoice<PjskRippleDirection>[] PjskRippleDirections =
    [
        new(PjskRippleDirection.Up, "向上"),
        new(PjskRippleDirection.Down, "向下"),
    ];

    public static readonly SettingChoice<PjskNoteStyle>[] PjskNoteStyles =
    [
        new(PjskNoteStyle.Critical, "绝赞"),
        new(PjskNoteStyle.Normal, "普通"),
    ];

    public static readonly SettingChoice<ClickEffectType>[] ClickEffectTypes =
    [
        new(ClickEffectType.Ring, "扩散圆环"),
        new(ClickEffectType.Bounce, "轻微跳跃"),
    ];

    public static readonly SettingChoice<int>[] FakeWeatherCodes =
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

    public static readonly SettingChoice<int>[] FakeWeatherAlertIcons =
    [
        new(0, "无"),
        new(1, "蓝色"),
        new(2, "黄色"),
        new(3, "橙色"),
        new(4, "红色"),
    ];

    public static readonly SettingChoice<int>[] StartupOpenTargets =
    [
        new(0, "不打开"),
        new(1, "ClassIsland 应用设置"),
        new(2, "应用设置 → 样式注入器"),
        new(3, "应用设置 → 插件"),
    ];

    public static readonly SettingChoice<CarouselAnimationType>[] CarouselAnimationTypes =
    [
        new(CarouselAnimationType.SlideUp, "上翻"),
        new(CarouselAnimationType.SlideDown, "下翻"),
        new(CarouselAnimationType.SlideLeft, "左滑"),
        new(CarouselAnimationType.SlideRight, "右滑"),
        new(CarouselAnimationType.Fade, "淡入淡出"),
    ];

    public static readonly SettingChoice<PrepareOnClassStyle>[] PrepareOnClassStyles =
    [
        new(PrepareOnClassStyle.Arrows, "箭头"),
        new(PrepareOnClassStyle.PulseRing, "扩散光环"),
        new(PrepareOnClassStyle.Scanline, "扫描线"),
        new(PrepareOnClassStyle.LightBand, "光带"),
    ];

    public static readonly SettingChoice<ScanlineDirection>[] ScanDirections =
    [
        new(ScanlineDirection.Horizontal, "横向（上下扫）"),
        new(ScanlineDirection.Vertical, "纵向（左右扫）"),
    ];

    public static readonly SettingChoice<GradientDirection>[] GradientDirections =
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

    public static readonly SettingChoice<BackgroundTexture>[] BackgroundTextures =
    [
        new(BackgroundTexture.Grid, "网格线"),
        new(BackgroundTexture.Dots, "点阵"),
        new(BackgroundTexture.DiagonalLines, "斜线"),
        new(BackgroundTexture.Cross, "十字网格"),
        new(BackgroundTexture.Spectrum, "动态频谱"),
        new(BackgroundTexture.Aero, "Aero 玻璃条纹"),
    ];

    /// <summary>字色来源（文字美化组）。</summary>
    public static readonly SettingChoice<TextColorMode>[] TextColorModes =
    [
        new(TextColorMode.KeepHost, "保持宿主原字色"),
        new(TextColorMode.Fixed, "固定字色"),
        new(TextColorMode.AutoInvert, "跟随背景自动反色"),
    ];

    /// <summary>主界面形状（设置页没有这个下拉，修改圆角时自动切为圆角矩形；远程设置页提供）。</summary>
    public static readonly SettingChoice<IslandShape>[] IslandShapes =
    [
        new(IslandShape.HostDefault, "跟随 ClassIsland"),
        new(IslandShape.Rectangle, "矩形"),
        new(IslandShape.RoundedRectangle, "圆角矩形"),
        new(IslandShape.Capsule, "胶囊"),
    ];
}
