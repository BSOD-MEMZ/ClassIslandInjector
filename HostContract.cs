namespace ClassIslandInjector;

/// <summary>
/// ClassIsland 宿主内部结构（命名控件、反射成员、魔法值）的稳定契约。
/// 插件通过名称/反射访问宿主私有结构是不可避免的；把这些魔法字符串集中到一处，
/// 升级 ClassIsland 版本时只需核对/更新对照表即可定位全部适配点。
///
/// 默认值编译进代码（与 ContractCatalog.BuiltIn 一致）；可通过联网下载的
/// 宿主对照表（ContractCatalog JSON）在运行时覆盖，使宿主更新后插件无需
/// 重新编译即可恢复工作。
/// </summary>
internal static class HostContract
{
    // ---- 主窗口命名控件（MainWindow.axaml 中的 Name）----

    /// <summary>主界面根容器（StackPanel）。</summary>
    public static string StackPanelRootContainer { get; private set; } = "StackPanelRootContainer";

    /// <summary>窗口根 Grid。</summary>
    public static string WindowRoot { get; private set; } = "WindowRoot";

    /// <summary>样式宿主 Border（其 Styles 集合用于注入覆盖样式表）。</summary>
    public static string ResourceLoaderBorder { get; private set; } = "ResourceLoaderBorder";

    /// <summary>实际客户区根控件。</summary>
    public static string WorkingRoot { get; private set; } = "WorkingRoot";

    /// <summary>根布局缩放控件（宿主整体缩放）。</summary>
    public static string RootLayoutTransformControl { get; private set; } = "RootLayoutTransformControl";

    /// <summary>岛屿内容根 Grid。</summary>
    public static string GridRoot { get; private set; } = "GridRoot";

    /// <summary>每行主界面的背景 Border。</summary>
    public static string BackgroundBorder { get; private set; } = "BackgroundBorder";

    /// <summary>Fluent 主题中包裹 BackgroundBorder 的容器 Border。</summary>
    public static string BackgroundBorderWrapper { get; private set; } = "BackgroundBorderWrapper";

    /// <summary>背景遮罩 Border（通知遮罩动画用）。</summary>
    public static string BackgroundBorderOverlayMask { get; private set; } = "BackgroundBorderOverlayMask";

    /// <summary>通知遮罩 Border。</summary>
    public static string OverlayMask { get; private set; } = "OverlayMask";

    /// <summary>倒计时箭头覆盖层宿主 Grid。</summary>
    public static string GridOverlay { get; private set; } = "GridOverlay";

    // ---- 类型与程序集 ----

    /// <summary>MainWindowLine 控件的完整类型名。</summary>
    public static string MainWindowLineTypeName { get; private set; } = "ClassIsland.Controls.MainWindowLine";

    /// <summary>「轮播容器」组件的完整类型名（自定义其切换上翻动画用）。</summary>
    public static string SlideComponentTypeName { get; private set; } = "ClassIsland.Controls.Components.SlideComponent";

    /// <summary>运行时加载器类型全名。</summary>
    public static string AvaloniaRuntimeXamlLoaderType { get; private set; } = "Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader";

    /// <summary>宿主已加载的 Avalonia 运行时加载器程序集名。</summary>
    public static string AvaloniaXamlLoaderAssembly { get; private set; } = "Avalonia.Markup.Xaml.Loader";

    // ---- 宿主设置（App.Settings）反射成员 ----

    /// <summary>宿主 App 上的 Settings 属性名。</summary>
    public static string SettingsProperty { get; private set; } = "Settings";

    /// <summary>分体主界面开关（注意宿主拼写 Seperated 单 p）。</summary>
    public static string IsIslandSeperatedProperty { get; private set; } = "IsIslandSeperated";

    /// <summary>宿主原生圆角 X。</summary>
    public static string RadiusXProperty { get; private set; } = "RadiusX";

    /// <summary>宿主原生圆角 Y。</summary>
    public static string RadiusYProperty { get; private set; } = "RadiusY";

    /// <summary>鼠标移入淡出开关（悬停保持可见用）。</summary>
    public static string IsMouseInFadingEnabledProperty { get; private set; } = "IsMouseInFadingEnabled";

    /// <summary>天气数据（虚假天气用）。</summary>
    public static string LastWeatherInfoProperty { get; private set; } = "LastWeatherInfo";

    /// <summary>主题模式（动态主题色用）。</summary>
    public static string ThemeProperty { get; private set; } = "Theme";

    /// <summary>主题色来源（动态主题色用）。</summary>
    public static string ColorSourceProperty { get; private set; } = "ColorSource";

    /// <summary>自定义主色（动态主题色用）。</summary>
    public static string PrimaryColorProperty { get; private set; } = "PrimaryColor";

    /// <summary>壁纸/屏幕取色（动态主题色用）。</summary>
    public static string SelectedPlatteProperty { get; private set; } = "SelectedPlatte";

    // ---- MainWindowLine 反射成员 ----

    /// <summary>MaskContent 属性（内容遮罩，用于触发强调动画）。</summary>
    public static string MaskContentProperty { get; private set; } = "MaskContent";

    /// <summary>OverlayContent 属性（提醒覆盖内容）。</summary>
    public static string OverlayContentProperty { get; private set; } = "OverlayContent";

    /// <summary>CurrentNotificationRequest 属性（当前通知请求）。</summary>
    public static string CurrentNotificationRequestProperty { get; private set; } = "CurrentNotificationRequest";

    /// <summary>伪类集合属性（StyledElement.PseudoClasses，反射访问）。</summary>
    public static string PseudoClassesProperty { get; private set; } = "PseudoClasses";

    /// <summary>通知请求的 ChannelId 属性。</summary>
    public static string ChannelIdProperty { get; private set; } = "ChannelId";

    /// <summary>MainWindowLine 的全屏特效窗口属性。</summary>
    public static string TopmostEffectWindowProperty { get; private set; } = "TopmostEffectWindow";

    /// <summary>TopmostEffectWindow 自动属性的后备字段（用于反射替换 Ripple 播放器）。</summary>
    public static string TopmostEffectWindowBackingField { get; private set; } = "<TopmostEffectWindow>k__BackingField";

    /// <summary>特效窗口 ViewModel 属性。</summary>
    public static string ViewModelProperty { get; private set; } = "ViewModel";

    /// <summary>ViewModel 上的 EffectControls 集合属性。</summary>
    public static string EffectControlsProperty { get; private set; } = "EffectControls";

    // ---- 注入样式类名 ----

    /// <summary>注入时加到窗口的样式类名。</summary>
    public static string InjectorWindowClass { get; private set; } = "classisland-injector";

    /// <summary>注入时加到主界面根的样式类名。</summary>
    public static string InjectorRootClass { get; private set; } = "classisland-injector-root";

    // ---- 伪类 ----

    /// <summary>提醒遮罩进入伪类。</summary>
    public static string PseudoMaskIn { get; private set; } = ":mask-in";

    /// <summary>提醒遮罩退出伪类。</summary>
    public static string PseudoMaskOut { get; private set; } = ":mask-out";

    // ---- 魔法值 ----

    /// <summary>「即将上课」倒计时通知频道 ID（ClassIsland 内置频道）。</summary>
    public static Guid PrepareOnClassChannelId { get; private set; } = new("CDDFE7FF-B904-4C73-B458-82793B2F66E9");

    /// <summary>
    /// 用对照表覆盖内置默认值（仅覆盖对照表中存在的条目；缺失条目保留内置默认）。
    /// </summary>
    public static void Apply(ContractCatalog catalog)
    {
        ApplyDefaults();
        if (catalog == null)
        {
            return;
        }

        ApplyString(catalog.ControlNames, nameof(StackPanelRootContainer), v => StackPanelRootContainer = v);
        ApplyString(catalog.ControlNames, nameof(WindowRoot), v => WindowRoot = v);
        ApplyString(catalog.ControlNames, nameof(ResourceLoaderBorder), v => ResourceLoaderBorder = v);
        ApplyString(catalog.ControlNames, nameof(WorkingRoot), v => WorkingRoot = v);
        ApplyString(catalog.ControlNames, nameof(RootLayoutTransformControl), v => RootLayoutTransformControl = v);
        ApplyString(catalog.ControlNames, nameof(GridRoot), v => GridRoot = v);
        ApplyString(catalog.ControlNames, nameof(BackgroundBorder), v => BackgroundBorder = v);
        ApplyString(catalog.ControlNames, nameof(BackgroundBorderWrapper), v => BackgroundBorderWrapper = v);
        ApplyString(catalog.ControlNames, nameof(BackgroundBorderOverlayMask), v => BackgroundBorderOverlayMask = v);
        ApplyString(catalog.ControlNames, nameof(OverlayMask), v => OverlayMask = v);
        ApplyString(catalog.ControlNames, nameof(GridOverlay), v => GridOverlay = v);

        ApplyString(catalog.TypeNames, nameof(MainWindowLineTypeName), v => MainWindowLineTypeName = v);
        ApplyString(catalog.TypeNames, nameof(SlideComponentTypeName), v => SlideComponentTypeName = v);
        ApplyString(catalog.TypeNames, nameof(AvaloniaRuntimeXamlLoaderType), v => AvaloniaRuntimeXamlLoaderType = v);

        ApplyString(catalog.AssemblyNames, nameof(AvaloniaXamlLoaderAssembly), v => AvaloniaXamlLoaderAssembly = v);

        ApplyString(catalog.MemberNames, nameof(SettingsProperty), v => SettingsProperty = v);
        ApplyString(catalog.MemberNames, nameof(IsIslandSeperatedProperty), v => IsIslandSeperatedProperty = v);
        ApplyString(catalog.MemberNames, nameof(RadiusXProperty), v => RadiusXProperty = v);
        ApplyString(catalog.MemberNames, nameof(RadiusYProperty), v => RadiusYProperty = v);
        ApplyString(catalog.MemberNames, nameof(IsMouseInFadingEnabledProperty), v => IsMouseInFadingEnabledProperty = v);
        ApplyString(catalog.MemberNames, nameof(LastWeatherInfoProperty), v => LastWeatherInfoProperty = v);
        ApplyString(catalog.MemberNames, nameof(ThemeProperty), v => ThemeProperty = v);
        ApplyString(catalog.MemberNames, nameof(ColorSourceProperty), v => ColorSourceProperty = v);
        ApplyString(catalog.MemberNames, nameof(PrimaryColorProperty), v => PrimaryColorProperty = v);
        ApplyString(catalog.MemberNames, nameof(SelectedPlatteProperty), v => SelectedPlatteProperty = v);
        ApplyString(catalog.MemberNames, nameof(MaskContentProperty), v => MaskContentProperty = v);
        ApplyString(catalog.MemberNames, nameof(OverlayContentProperty), v => OverlayContentProperty = v);
        ApplyString(catalog.MemberNames, nameof(CurrentNotificationRequestProperty), v => CurrentNotificationRequestProperty = v);
        ApplyString(catalog.MemberNames, nameof(PseudoClassesProperty), v => PseudoClassesProperty = v);
        ApplyString(catalog.MemberNames, nameof(ChannelIdProperty), v => ChannelIdProperty = v);
        ApplyString(catalog.MemberNames, nameof(TopmostEffectWindowProperty), v => TopmostEffectWindowProperty = v);
        ApplyString(catalog.MemberNames, nameof(TopmostEffectWindowBackingField), v => TopmostEffectWindowBackingField = v);
        ApplyString(catalog.MemberNames, nameof(ViewModelProperty), v => ViewModelProperty = v);
        ApplyString(catalog.MemberNames, nameof(EffectControlsProperty), v => EffectControlsProperty = v);

        ApplyString(catalog.ClassNames, nameof(InjectorWindowClass), v => InjectorWindowClass = v);
        ApplyString(catalog.ClassNames, nameof(InjectorRootClass), v => InjectorRootClass = v);

        ApplyString(catalog.PseudoClasses, nameof(PseudoMaskIn), v => PseudoMaskIn = v);
        ApplyString(catalog.PseudoClasses, nameof(PseudoMaskOut), v => PseudoMaskOut = v);

        ApplyGuid(catalog.Guids, nameof(PrepareOnClassChannelId), v => PrepareOnClassChannelId = v);
    }

    /// <summary>恢复为内置默认（等价于 Apply(ContractCatalog.BuiltIn)）。</summary>
    public static void Reset() => Apply(ContractCatalog.BuiltIn);

    /// <summary>把全部点位恢复为内置默认字面量（与属性初始化器一致）。</summary>
    private static void ApplyDefaults()
    {
        StackPanelRootContainer = "StackPanelRootContainer";
        WindowRoot = "WindowRoot";
        ResourceLoaderBorder = "ResourceLoaderBorder";
        WorkingRoot = "WorkingRoot";
        RootLayoutTransformControl = "RootLayoutTransformControl";
        GridRoot = "GridRoot";
        BackgroundBorder = "BackgroundBorder";
        BackgroundBorderWrapper = "BackgroundBorderWrapper";
        BackgroundBorderOverlayMask = "BackgroundBorderOverlayMask";
        OverlayMask = "OverlayMask";
        GridOverlay = "GridOverlay";
        MainWindowLineTypeName = "ClassIsland.Controls.MainWindowLine";
        SlideComponentTypeName = "ClassIsland.Controls.Components.SlideComponent";
        AvaloniaRuntimeXamlLoaderType = "Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader";
        AvaloniaXamlLoaderAssembly = "Avalonia.Markup.Xaml.Loader";
        SettingsProperty = "Settings";
        IsIslandSeperatedProperty = "IsIslandSeperated";
        RadiusXProperty = "RadiusX";
        RadiusYProperty = "RadiusY";
        IsMouseInFadingEnabledProperty = "IsMouseInFadingEnabled";
        LastWeatherInfoProperty = "LastWeatherInfo";
        ThemeProperty = "Theme";
        ColorSourceProperty = "ColorSource";
        PrimaryColorProperty = "PrimaryColor";
        SelectedPlatteProperty = "SelectedPlatte";
        MaskContentProperty = "MaskContent";
        OverlayContentProperty = "OverlayContent";
        CurrentNotificationRequestProperty = "CurrentNotificationRequest";
        PseudoClassesProperty = "PseudoClasses";
        ChannelIdProperty = "ChannelId";
        TopmostEffectWindowProperty = "TopmostEffectWindow";
        TopmostEffectWindowBackingField = "<TopmostEffectWindow>k__BackingField";
        ViewModelProperty = "ViewModel";
        EffectControlsProperty = "EffectControls";
        InjectorWindowClass = "classisland-injector";
        InjectorRootClass = "classisland-injector-root";
        PseudoMaskIn = ":mask-in";
        PseudoMaskOut = ":mask-out";
        PrepareOnClassChannelId = new Guid("CDDFE7FF-B904-4C73-B458-82793B2F66E9");
    }

    private static void ApplyString(ContractGroup group, string key, Action<string> setter)
    {
        if (group.TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry.Value))
        {
            setter(entry.Value);
        }
    }

    private static void ApplyGuid(ContractGroup group, string key, Action<Guid> setter)
    {
        if (group.TryGetValue(key, out var entry) && Guid.TryParse(entry.Value, out var guid))
        {
            setter(guid);
        }
    }
}
