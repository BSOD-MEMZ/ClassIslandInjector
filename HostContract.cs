namespace ClassIslandInjector;

/// <summary>
/// ClassIsland 宿主内部结构（命名控件、反射成员、魔法值）的稳定契约。
/// 插件通过名称/反射访问宿主私有结构是不可避免的；把这些魔法字符串集中到一处，
/// 升级 ClassIsland 版本时只需核对本文件即可定位全部适配点。
/// </summary>
internal static class HostContract
{
    // ---- 主窗口命名控件（MainWindow.axaml 中的 Name）----

    /// <summary>主界面根容器（StackPanel）。</summary>
    public const string StackPanelRootContainer = "StackPanelRootContainer";

    /// <summary>窗口根 Grid。</summary>
    public const string WindowRoot = "WindowRoot";

    /// <summary>样式宿主 Border（其 Styles 集合用于注入覆盖样式表）。</summary>
    public const string ResourceLoaderBorder = "ResourceLoaderBorder";

    /// <summary>实际客户区根控件。</summary>
    public const string WorkingRoot = "WorkingRoot";

    /// <summary>根布局缩放控件（宿主整体缩放）。</summary>
    public const string RootLayoutTransformControl = "RootLayoutTransformControl";

    /// <summary>岛屿内容根 Grid。</summary>
    public const string GridRoot = "GridRoot";

    /// <summary>每行主界面的背景 Border。</summary>
    public const string BackgroundBorder = "BackgroundBorder";

    /// <summary>Fluent 主题中包裹 BackgroundBorder 的容器 Border。</summary>
    public const string BackgroundBorderWrapper = "BackgroundBorderWrapper";

    /// <summary>背景遮罩 Border（通知遮罩动画用）。</summary>
    public const string BackgroundBorderOverlayMask = "BackgroundBorderOverlayMask";

    /// <summary>通知遮罩 Border。</summary>
    public const string OverlayMask = "OverlayMask";

    /// <summary>倒计时箭头覆盖层宿主 Grid。</summary>
    public const string GridOverlay = "GridOverlay";

    // ---- MainWindowLine 类型与反射成员 ----

    /// <summary>MainWindowLine 控件的完整类型名。</summary>
    public const string MainWindowLineTypeName = "ClassIsland.Controls.MainWindowLine";

    /// <summary>MaskContent 属性（内容遮罩，用于触发强调动画）。</summary>
    public const string MaskContentProperty = "MaskContent";

    /// <summary>CurrentNotificationRequest 属性（当前通知请求）。</summary>
    public const string CurrentNotificationRequestProperty = "CurrentNotificationRequest";

    /// <summary>通知请求的 ChannelId 属性。</summary>
    public const string ChannelIdProperty = "ChannelId";

    /// <summary>MainWindowLine 的全屏特效窗口属性。</summary>
    public const string TopmostEffectWindowProperty = "TopmostEffectWindow";

    /// <summary>TopmostEffectWindow 自动属性的后备字段（用于反射替换 Ripple 播放器）。</summary>
    public const string TopmostEffectWindowBackingField = "<TopmostEffectWindow>k__BackingField";

    /// <summary>特效窗口 ViewModel 属性。</summary>
    public const string ViewModelProperty = "ViewModel";

    /// <summary>ViewModel 上的 EffectControls 集合属性。</summary>
    public const string EffectControlsProperty = "EffectControls";

    // ---- 魔法值 ----

    /// <summary>「即将上课」倒计时通知频道 ID（ClassIsland 内置频道）。</summary>
    public static readonly Guid PrepareOnClassChannelId = new("CDDFE7FF-B904-4C73-B458-82793B2F66E9");

    /// <summary>注入时加到窗口的样式类名。</summary>
    public const string InjectorWindowClass = "classisland-injector";

    /// <summary>注入时加到主界面根的样式类名。</summary>
    public const string InjectorRootClass = "classisland-injector-root";

    // ---- Avalonia 运行时 XAML 加载器（由宿主提供，避免版本不匹配）----

    /// <summary>宿主已加载的 Avalonia 运行时加载器程序集名。</summary>
    public const string AvaloniaXamlLoaderAssembly = "Avalonia.Markup.Xaml.Loader";

    /// <summary>运行时加载器类型全名。</summary>
    public const string AvaloniaRuntimeXamlLoaderType = "Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader";
}
