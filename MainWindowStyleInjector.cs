using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Weather;
using ClassIsland.Shared;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace ClassIslandInjector;

/// <summary>
/// This uses stable names from ClassIsland.MainWindow.axaml rather than patching binaries.
/// All host changes are confined to one visual subtree and are restored when disabled.
/// </summary>
internal sealed class MainWindowStyleInjector : IDisposable
{
    private readonly InjectorSettings _settings;
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _stateTimer;
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private Window? _mainWindow;
    private Control? _islandRoot;
    private Border? _styleHost;
    private ITransform? _originalTransform;
    private double _originalOpacity = 1;
    // 圆角接管宿主原生设置：记录原始值用于还原，_effectiveCornerRadius 供
    // 底图/纹理宿主裁切跟随宿主 RadiusX。
    private double _originalHostRadiusX;
    private double _originalHostRadiusY;
    private bool _hostShapeCaptured;
    private double _effectiveCornerRadius;
    private Type? _hostSettingsType;
    private PropertyInfo? _hostSettingsProperty;
    private Styles? _loadedStyles;
    private Styles? _notificationStyles;
    /// <summary>自定义「轮播容器」切换上翻动画时注入的样式。</summary>
    private Styles? _carouselStyles;
    private FileSystemWatcher? _styleSheetWatcher;
    private Grid? _windowRoot;
    private readonly List<Action> _decorationRestorers = [];
    private readonly Dictionary<Control, object?> _lineMasks = [];
    private readonly HashSet<Control> _observedLines = [];
    private readonly Dictionary<Control, object?> _nativeEffectPlayers = [];
    private object? _suppressingEffectPlayer;
    private readonly List<IRippleEffect> _ripples = [];
    private readonly Dictionary<Control, PrepareOnClassOverlay> _prepareOnClassOverlays = [];
    /// <summary>「即将上课 · 红色警告」全屏覆盖层（宿于流光专用全屏窗口，独立于行级覆盖层）。</summary>
    private PrepareOnClassWarningOverlay? _prepareWarningOverlay;
    /// <summary>预览期间被强制点亮（Opacity=1）的 GridOverlay 宿主，移除覆盖层时还原。</summary>
    private readonly Dictionary<Control, Grid> _prepareOnClassOverlayHosts = [];
    /// <summary>「预览即将上课」的激活截止时间（5 秒）。</summary>
    private DateTime _prepareOnClassPreviewUntil = DateTime.MinValue;
    private DateTime _lastOverlayDebugLog = DateTime.MinValue;

    /// <summary>临时诊断日志（定位「即将上课样式预览不生效」，定位后移除）。</summary>
    internal static void DebugLog(string message)
    {
        try
        {
            var dir = InjectorRuntime.ConfigDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var path = Path.Combine(dir, "preview-debug.log");
            lock (typeof(MainWindowStyleInjector))
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
        }
        catch
        {
            // 诊断日志失败不影响功能。
        }
    }
    // A custom ripple normally lives in ClassIsland's full-screen topmost effect
    // window.  This map lets us remove it from the same host when it completes.
    private readonly Dictionary<IRippleEffect, IList> _rippleHosts = [];
    /// <summary>流光跑马灯专用全屏覆盖窗口（覆盖整块屏幕含任务栏区域）。</summary>
    private MarqueeOverlayWindow? _marqueeWindow;
    private DateTime _visibilityStartedAt = DateTime.MinValue;
    private DateTime _emphasisStartedAt = DateTime.MinValue;
    private bool _lastContentVisible;
    private bool _dynamicColorsInitialized;
    /// <summary>动态修改 ClassIsland 全局主题色：最近一次应用的 SMTC 主色（宿主重置后重新应用用）。</summary>
    private Color? _lastDynamicThemeColor;
    /// <summary>是否已应用过动态主题色（关闭/卸载时恢复宿主配置用）。</summary>
    private bool _dynamicThemeColorApplied;
    /// <summary>鼠标悬停保持可见：覆写宿主「鼠标移入淡出」设置前的原值。</summary>
    private bool? _originalMouseInFadingEnabled;
    /// <summary>是否已覆写宿主「鼠标移入淡出」设置。</summary>
    private bool _mouseInFadingOverridden;
    /// <summary>主界面点击特效：是否已挂接指针按下事件。</summary>
    private bool _clickHandlerAttached;
    /// <summary>虚假天气：最近注入的 WeatherInfo 实例（用于引用比较避免注入死循环）。</summary>
    private WeatherInfo? _fakeWeatherInstance;
    /// <summary>虚假天气：最近一次注入的设置值签名（值变化时允许重新注入）。</summary>
    private string _fakeWeatherSignature = string.Empty;
    /// <summary>虚假天气：宿主 Settings 的 PropertyChanged 订阅。</summary>
    private PropertyChangedEventHandler? _hostWeatherHandler;
    /// <summary>点击特效：主界面轻微跳跃的开始时间。</summary>
    private DateTime _clickBounceStart = DateTime.MinValue;
    /// <summary>点击特效：主界面轻微跳跃是否进行中。</summary>
    private bool _clickBounceActive;
    private Color _dynamicBackgroundColor;
    private Color _dynamicBorderColor;
    private Color _dynamicShadowColor;
    private Color _bgTransitionFrom;
    private Color _bgTransitionTo;
    private Color _borderTransitionFrom;
    private Color _borderTransitionTo;
    private Color _shadowTransitionFrom;
    private Color _shadowTransitionTo;
    private DateTime _colorTransitionStart = DateTime.MinValue;
    private bool _colorTransitionActive;
    private readonly List<(Border Border, IBrush? Background, IBrush? BorderBrush)> _decorations = [];
    private DropShadowEffect? _shadowEffect;

    private readonly DispatcherTimer _wallpaperTimer;
    private Border? _wallpaperHost;
    private BlurEffect? _wallpaperBlur;
    /// <summary>每行主界面的底纹宿主（键为 MainWindowLine 模板 GridRoot），
    /// 插在底色填充之上、组件内容之下。</summary>
    private readonly Dictionary<Grid, Border> _textureHosts = [];
    /// <summary>当前底纹画刷（随设置变更重建）。</summary>
    private IBrush? _textureBrush;
    /// <summary>动态频谱底纹：系统声音输出回环捕获器（仅 Spectrum 纹理时启用）。</summary>
    private AudioSpectrumCapture? _spectrumCapture;
    /// <summary>频谱底纹激活状态（决定 16ms 动画计时器是否保持运行）。</summary>
    private bool _spectrumActive;
    /// <summary>各行底纹宿主上挂接的频谱覆盖层（逐帧 InvalidateVisual 重绘）。</summary>
    private readonly List<SpectrumTextureOverlay> _spectrumOverlays = [];
    /// <summary>频谱诊断日志节流时间戳。</summary>
    private DateTime _lastSpectrumLog = DateTime.MinValue;
    private readonly List<Border> _wallpaperLayers = [];
    private int _wallpaperFront;
    private Bitmap? _wallpaperBitmap;
    private MemoryStream? _wallpaperStream;
    private Bitmap? _wallpaperRetiredBitmap;
    private MemoryStream? _wallpaperRetiredStream;
    private WallpaperSource _wallpaperLoadedSource = WallpaperSource.None;
    private string _wallpaperLoadedPath = string.Empty;
    private readonly List<string> _wallpaperSlideshow = [];
    private int _wallpaperSlideshowIndex;
    private DateTime _wallpaperTransitionStart = DateTime.MinValue;
    private bool _wallpaperTransitionActive;
    /// <summary>底图宿主当前渲染模式（简单模式 = 双缓冲交叉淡化，图层模式 = 锚点画布）。</summary>
    private WallpaperHostMode _wallpaperHostMode = WallpaperHostMode.None;
    /// <summary>图层式底图的画布（宿主子项，图层图片按锚点相对定位）。</summary>
    private Canvas? _wallpaperCanvas;
    /// <summary>当前挂载的图层视图（按设置顺序，后面的在上层）。</summary>
    private readonly List<WallpaperLayerView> _wallpaperLayerViews = [];
    /// <summary>当前 SMTC 媒体标题（「显示媒体标题」图层用）。</summary>
    private string _smtcTitle = string.Empty;
    /// <summary>当前 SMTC 是否正在播放。</summary>
    private bool _smtcPlaying;
    /// <summary>「扩展到整个显示框架」的全屏宿主画布（覆盖整个 GridRoot，跨所有行）。</summary>
    private Canvas? _fullscreenCanvas;
    /// <summary>全屏图层对应的九宫格渲染控件（键为图层 Id）。</summary>
    private readonly Dictionary<string, WallpaperNineSliceVisual> _fullscreenVisuals = [];
    /// <summary>全屏图层位图缓存（键为图层 Id）。</summary>
    private readonly Dictionary<string, Bitmap> _fullscreenBitmaps = [];
    /// <summary>全屏图层位图签名（来源|路径，去重避免重复解码）。</summary>
    private readonly Dictionary<string, string> _fullscreenSignatures = [];

    /// <summary>底图宿主的渲染模式。</summary>
    private enum WallpaperHostMode
    {
        /// <summary>无宿主。</summary>
        None,
        /// <summary>旧版简单模式（双缓冲交叉淡化）。</summary>
        Simple,
        /// <summary>图层式底图（锚点画布）。</summary>
        Layers
    }

    /// <summary>图层式底图的一个图层视图：渲染控件（位图 Image 或 形状/文本 WallpaperLayerVisual）+ 来源/幻灯片状态。</summary>
    private sealed class WallpaperLayerView
    {
        public required WallpaperLayerItem Settings { get; set; }
        public required Control Control { get; init; }
        public Image? ImageControl => Control as Image;
        public Bitmap? Bitmap { get; set; }
        public MemoryStream? Stream { get; set; }
        public WallpaperSource LoadedSource { get; set; } = WallpaperSource.None;
        public string LoadedPath { get; set; } = string.Empty;
        public readonly List<string> SlideshowFiles = [];
        public int SlideshowIndex;
        public DateTime NextAdvance = DateTime.MinValue;
    }

    // 宿主反射元数据缓存：MainWindowLine 等宿主类型固定，避免每 50ms 轮询重复反射。
    private static readonly ConcurrentDictionary<Type, FieldInfo?> EffectPlayerFieldCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> MaskContentPropertyCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> CurrentNotificationRequestPropertyCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> ChannelIdPropertyCache = new();

    public MainWindowStyleInjector(InjectorSettings settings)
    {
        _settings = settings;
        _animationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAnimationTick);
        _stateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnStateTick);
        _wallpaperTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, OnWallpaperTimerTick);
    }

    public void Attach()
    {
        var mainWindow = AppBase.Current.MainWindow;
        if (mainWindow == null)
        {
            return;
        }

        if (_mainWindow != mainWindow)
        {
            RestoreHostState();
            _mainWindow = mainWindow;
            _islandRoot = mainWindow.FindControl<Control>(HostContract.StackPanelRootContainer);
            _windowRoot = mainWindow.FindControl<Grid>(HostContract.WindowRoot);
            _styleHost = mainWindow.FindControl<Border>(HostContract.ResourceLoaderBorder);
            if (_islandRoot == null)
            {
                return;
            }

            _originalTransform = _islandRoot.RenderTransform;
            _originalOpacity = _islandRoot.Opacity;
            CaptureHostShape();
            mainWindow.Classes.Add(HostContract.InjectorWindowClass);
            _islandRoot.Classes.Add(HostContract.InjectorRootClass);
        }

        AttachClickHandler();
        Apply();
    }

    public void Apply()
    {
        if (_mainWindow == null || _islandRoot == null)
        {
            Attach();
            return;
        }

        if (!_settings.Enabled)
        {
            RestoreHostState();
            return;
        }

        _islandRoot.Opacity = _originalOpacity * _settings.Opacity;
        ApplyTransform(0);
        ApplyDecorations();
        ApplyShapeToHost();
        ApplyWallpaper();
        ApplyTextureHost();
        ApplyDynamicThemeColorState();
        ApplyMouseHoverKeepVisible();
        ApplyClickEffectState();
        ApplyFakeWeatherState();
        ReloadStyleSheet();
        ReloadNotificationTransitionStyles();
        ReloadCarouselAnimationStyles();
        ConfigureStyleSheetWatcher();
        _animationClock.Restart();
        _stateTimer.Start();
        UpdateAnimationTimer();
    }

    public void ReloadStyleSheet()
    {
        if (_mainWindow == null)
        {
            return;
        }

        if (_loadedStyles != null)
        {
            StyleHost.Remove(_loadedStyles);
            _loadedStyles = null;
        }

        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.StyleSheetPath) ||
            !File.Exists(_settings.StyleSheetPath))
        {
            return;
        }

        try
        {
            var uri = new Uri(Path.GetFullPath(_settings.StyleSheetPath));
                _loadedStyles = LoadExternalStyles(File.ReadAllText(_settings.StyleSheetPath), uri);
            if (_loadedStyles != null)
            {
                StyleHost.Add(_loadedStyles);
            }
        }
        catch
        {
            // A malformed user stylesheet must never stop the host application.
        }
    }

    private static Styles? LoadExternalStyles(string xaml, Uri uri)
    {
        // Avalonia's runtime loader is intentionally supplied by the host app rather
        // than by PluginSdk. Resolve it from the already-loaded host to keep the
        // plugin aligned with the exact Avalonia version ClassIsland is running.
        var loaderAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x =>
            x.GetName().Name == HostContract.AvaloniaXamlLoaderAssembly) ?? TryLoadHostRuntimeLoader();
        var loaderType = loaderAssembly?.GetType(HostContract.AvaloniaRuntimeXamlLoaderType);
        var loadMethod = loaderType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static,
            [typeof(string), typeof(Assembly), typeof(object), typeof(Uri), typeof(bool)]);
        return loadMethod?.Invoke(null, [xaml, typeof(Plugin).Assembly, null, uri, false]) as Styles;
    }

    private void ReloadNotificationTransitionStyles()
    {
        if (_mainWindow == null)
        {
            return;
        }

        if (_notificationStyles != null)
        {
            StyleHost.Remove(_notificationStyles);
            _notificationStyles = null;
        }

        if (!_settings.Enabled || _settings.NotificationTransition == NotificationTransition.HostDefault)
        {
            return;
        }

        var seconds = _settings.NotificationTransitionDurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var (fromX, fromY) = _settings.NotificationTransition switch
        {
            NotificationTransition.SlideDown => ("0", "-80"),
            NotificationTransition.SlideUp => ("0", "80"),
            NotificationTransition.SlideLeft => ("-120", "0"),
            NotificationTransition.SlideRight => ("120", "0"),
            _ => ("0", "0")
        };
        var xaml = $"""
                    <Styles xmlns="https://github.com/avaloniaui"
                            xmlns:controls="clr-namespace:ClassIsland.Controls;assembly=ClassIsland">
                      <Style Selector="controls|MainWindowLine:mask-in /template/ Border#OverlayMask, controls|MainWindowLine:mask-in /template/ Border#BackgroundBorderOverlayMask">
                        <Style.Animations>
                          <Animation Duration="0:0:{seconds}" FillMode="Both">
                            <KeyFrame Cue="0%"><Setter Property="Opacity" Value="0"/><Setter Property="TranslateTransform.X" Value="{fromX}"/><Setter Property="TranslateTransform.Y" Value="{fromY}"/></KeyFrame>
                            <KeyFrame Cue="100%"><Setter Property="Opacity" Value="1"/><Setter Property="TranslateTransform.X" Value="0"/><Setter Property="TranslateTransform.Y" Value="0"/></KeyFrame>
                          </Animation>
                        </Style.Animations>
                      </Style>
                      <Style Selector="controls|MainWindowLine:mask-out /template/ Border#OverlayMask, controls|MainWindowLine:mask-out /template/ Border#BackgroundBorderOverlayMask">
                        <Style.Animations>
                          <Animation Duration="0:0:{seconds}" FillMode="Forward">
                            <KeyFrame Cue="0%"><Setter Property="Opacity" Value="1"/><Setter Property="TranslateTransform.X" Value="0"/><Setter Property="TranslateTransform.Y" Value="0"/></KeyFrame>
                            <KeyFrame Cue="100%"><Setter Property="Opacity" Value="0"/><Setter Property="TranslateTransform.X" Value="{-double.Parse(fromX, System.Globalization.CultureInfo.InvariantCulture)}"/><Setter Property="TranslateTransform.Y" Value="{-double.Parse(fromY, System.Globalization.CultureInfo.InvariantCulture)}"/></KeyFrame>
                          </Animation>
                        </Style.Animations>
                      </Style>
                    </Styles>
                    """;
        try
        {
            _notificationStyles = LoadExternalStyles(xaml, new Uri("avares://ClassIslandInjector/GeneratedNotificationTransitions.axaml"));
            if (_notificationStyles != null)
            {
                StyleHost.Add(_notificationStyles);
            }
        }
        catch
        {
            // Keep ClassIsland's native transition when a host version rejects a selector.
        }
    }

    /// <summary>
    /// 自定义「轮播容器」（SlideComponent）切换时的上翻动画：宿主把 250ms / Y±40 / KeySpline
    /// 写死在组件 ControlTheme 里，这里注入更高优先级的 Style.Animations 覆盖为可配置参数。
    /// </summary>
    private void ReloadCarouselAnimationStyles()
    {
        if (_mainWindow == null)
        {
            return;
        }

        if (_carouselStyles != null)
        {
            StyleHost.Remove(_carouselStyles);
            _carouselStyles = null;
        }

        if (!_settings.Enabled || !_settings.CarouselAnimationEnabled)
        {
            return;
        }

        var seconds = _settings.CarouselAnimationDurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var offset = _settings.CarouselAnimationOffset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var negOffset = "-" + offset;
        // 新内容进场起点 (inX, inY) / 旧内容离场终点 (outX, outY)。宿主模板只有 TranslateTransform，
        // 因此支持滑动与淡入淡出，无法做缩放/旋转。
        var (inX, inY, outX, outY) = _settings.CarouselAnimationType switch
        {
            CarouselAnimationType.SlideDown => ("0", negOffset, "0", offset),
            CarouselAnimationType.SlideLeft => (offset, "0", negOffset, "0"),
            CarouselAnimationType.SlideRight => (negOffset, "0", offset, "0"),
            CarouselAnimationType.Fade => ("0", "0", "0", "0"),
            _ => ("0", negOffset, "0", offset) // SlideUp
        };
        var xaml = $"""
                    <Styles xmlns="https://github.com/avaloniaui"
                            xmlns:ci="clr-namespace:ClassIsland.Controls.Components;assembly=ClassIsland">
                      <Style Selector="ListBox.sliding ListBoxItem[IsSelected=True] /template/ ContentPresenter#ContentPresenter, ci|SlideComponent ListBoxItem[IsSelected=True] /template/ ContentPresenter#ContentPresenter">
                        <Style.Animations>
                          <Animation Duration="0:0:{seconds}" FillMode="Forward">
                            <KeyFrame Cue="0%"><Setter Property="IsVisible" Value="True"/><Setter Property="Opacity" Value="0"/><Setter Property="TranslateTransform.X" Value="{inX}"/><Setter Property="TranslateTransform.Y" Value="{inY}"/></KeyFrame>
                            <KeyFrame Cue="100%"><Setter Property="Opacity" Value="1"/><Setter Property="TranslateTransform.X" Value="0"/><Setter Property="TranslateTransform.Y" Value="0"/></KeyFrame>
                          </Animation>
                        </Style.Animations>
                      </Style>
                      <Style Selector="ListBox.sliding ListBoxItem[IsSelected=False] /template/ ContentPresenter#ContentPresenter, ci|SlideComponent ListBoxItem[IsSelected=False] /template/ ContentPresenter#ContentPresenter">
                        <Style.Animations>
                          <Animation Duration="0:0:{seconds}" FillMode="Forward">
                            <KeyFrame Cue="0%"><Setter Property="IsVisible" Value="True"/></KeyFrame>
                            <KeyFrame Cue="100%"><Setter Property="Opacity" Value="0"/><Setter Property="TranslateTransform.X" Value="{outX}"/><Setter Property="TranslateTransform.Y" Value="{outY}"/><Setter Property="IsVisible" Value="False"/></KeyFrame>
                          </Animation>
                        </Style.Animations>
                      </Style>
                    </Styles>
                    """;
        try
        {
            _carouselStyles = LoadExternalStyles(xaml, new Uri("avares://ClassIslandInjector/GeneratedCarouselAnimations.axaml"));
            if (_carouselStyles != null)
            {
                StyleHost.Add(_carouselStyles);
            }
        }
        catch
        {
            // 宿主版本拒绝选择器时保留原生轮播动画。
        }
    }

    private static Assembly? TryLoadHostRuntimeLoader()
    {
        try
        {
            var hostDirectory = Path.GetDirectoryName(typeof(Application).Assembly.Location);
            var loaderPath = hostDirectory == null ? null : Path.Combine(hostDirectory, HostContract.AvaloniaXamlLoaderAssembly + ".dll");
            return loaderPath is { } path && File.Exists(path)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_settings.Enabled)
        {
            _animationTimer.Stop();
            return;
        }

        if (_colorTransitionActive)
        {
            AdvanceColorTransition();
        }

        if (_wallpaperTransitionActive)
        {
            AdvanceWallpaperTransition();
        }

        if (_spectrumActive)
        {
            UpdateSpectrum();
        }

        var phase = _animationClock.Elapsed.TotalSeconds / _settings.AnimationPeriodSeconds * Math.Tau;
        ApplyTransform(Math.Sin(phase));
        AdvanceRipples();
        AdvancePrepareOnClassOverlays();
        UpdateAnimationTimer();
    }

    private void ApplyTransform(double wave)
    {
        if (_islandRoot == null)
        {
            return;
        }

        var scale = 1.0;
        var rotation = _settings.Rotation;
        var x = _settings.OffsetX;
        var y = _settings.OffsetY;

        var opacity = _settings.Opacity;
        if (_settings.AnimationEnabled)
        {
            switch (_settings.AnimationMode)
            {
                case IslandAnimationMode.Breathe:
                    scale *= 1 + _settings.AnimationAmount * wave;
                    break;
                case IslandAnimationMode.Float:
                    y += _settings.AnimationAmount * 100 * wave;
                    break;
                case IslandAnimationMode.Wave:
                    rotation += _settings.AnimationAmount * 30 * wave;
                    y += _settings.AnimationAmount * 30 * wave;
                    break;
            }
        }

        ApplyVisibilityAnimation(ref scale, ref y, ref opacity);
        ApplyEmphasisAnimation(ref scale, ref x, ref y, ref opacity);
        ApplyBounceToTransform(ref scale, ref y);

        var transforms = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(scale, scale),
                new RotateTransform(rotation),
                new TranslateTransform(x, y)
            }
        };
        if (_originalTransform is Transform hostTransform)
        {
            transforms.Children.Add(hostTransform);
        }
        _islandRoot.RenderTransform = transforms;
        _islandRoot.RenderTransformOrigin = RelativePoint.Center;
        _islandRoot.Opacity = Math.Clamp(_originalOpacity * opacity, 0, 1);
    }

    private void ApplyVisibilityAnimation(ref double scale, ref double y, ref double opacity)
    {
        var progress = GetEffectProgress(_visibilityStartedAt, _settings.VisibilityDurationSeconds);
        if (progress >= 1)
        {
            return;
        }

        switch (_settings.VisibilityAnimation)
        {
            case VisibilityAnimation.Fade:
                opacity *= progress;
                break;
            case VisibilityAnimation.Scale:
                scale *= 0.82 + 0.18 * progress;
                opacity *= progress;
                break;
            case VisibilityAnimation.SlideFromTop:
                y -= (1 - progress) * 45;
                opacity *= progress;
                break;
            case VisibilityAnimation.SlideFromBottom:
                y += (1 - progress) * 45;
                opacity *= progress;
                break;
        }
    }

    private void ApplyEmphasisAnimation(ref double scale, ref double x, ref double y, ref double opacity)
    {
        var progress = GetEffectProgress(_emphasisStartedAt, _settings.EmphasisDurationSeconds);
        if (progress >= 1)
        {
            return;
        }

        var wave = Math.Sin(progress * Math.PI);
        switch (_settings.EmphasisAnimation)
        {
            case EmphasisAnimation.Pulse:
                scale *= 1 + _settings.EmphasisAmount * wave;
                break;
            case EmphasisAnimation.Bounce:
                y -= _settings.EmphasisAmount * 85 * wave;
                break;
            case EmphasisAnimation.Shake:
                x += _settings.EmphasisAmount * 40 * Math.Sin(progress * Math.PI * 6) * (1 - progress);
                break;
            case EmphasisAnimation.Flash:
                opacity *= 0.55 + 0.45 * Math.Abs(Math.Sin(progress * Math.PI * 4));
                break;
        }
    }

    private static double GetEffectProgress(DateTime startedAt, double durationSeconds)
    {
        if (startedAt == DateTime.MinValue)
        {
            return 1;
        }

        return Math.Clamp((DateTime.UtcNow - startedAt).TotalSeconds / durationSeconds, 0, 1);
    }

    private void UpdateAnimationTimer()
    {
        var hasContinuousAnimation = (_settings.AnimationEnabled && _settings.AnimationMode != IslandAnimationMode.None) ||
                                     _spectrumActive;
        var hasTransientAnimation = GetEffectProgress(_visibilityStartedAt, _settings.VisibilityDurationSeconds) < 1 ||
                                   GetEffectProgress(_emphasisStartedAt, _settings.EmphasisDurationSeconds) < 1 ||
                                   _ripples.Count > 0 || _prepareOnClassOverlays.Count > 0 ||
                                   _prepareWarningOverlay != null ||
                                   _colorTransitionActive || _wallpaperTransitionActive || _clickBounceActive;
        if (hasContinuousAnimation || hasTransientAnimation)
        {
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
        }
    }

    private void OnStateTick(object? sender, EventArgs e)
    {
        if (!_settings.Enabled || _mainWindow == null || _islandRoot == null)
        {
            DebugLog($"OnStateTick 提前返回: enabled={_settings.Enabled}, mainWindow={_mainWindow != null}, islandRoot={_islandRoot != null}");
            return;
        }

        // 一次全树遍历同时服务底图边界与 MainWindowLine 发现，避免每 tick 遍历两次。
        var descendants = _mainWindow.GetVisualDescendants().OfType<Control>().ToArray();
        if (_wallpaperHost != null)
        {
            UpdateWallpaperBounds(descendants);
        }

        if (_textureHosts.Count > 0 ||
            (_settings.Enabled && _settings.BackgroundTextureType != BackgroundTexture.None))
        {
            UpdateTextureBounds(descendants);
        }

        // 频谱兜底刷新：即使 16ms 动画计时器未运行，50ms 状态计时器也能保持频谱重绘。
        if (_spectrumActive)
        {
            UpdateSpectrum();
        }

        var contentRoot = _mainWindow.FindControl<Control>(HostContract.GridRoot);
        var isVisible = contentRoot?.IsVisible == true;
        if (isVisible && !_lastContentVisible)
        {
            _visibilityStartedAt = DateTime.UtcNow;
            UpdateAnimationTimer();
        }
        _lastContentVisible = isVisible;

        var currentLines = descendants
            .Where(x => x.GetType().FullName == HostContract.MainWindowLineTypeName)
            .ToArray();
        foreach (var line in currentLines)
        {
            ConfigureNativeRipplePlayer(line);
            ObserveLine(line);
            UpdatePrepareOnClassOverlay(line);
            var mask = GetMaskContentProperty(line.GetType())?.GetValue(line);
            if (!_lineMasks.TryGetValue(line, out var previousMask))
            {
                _lineMasks[line] = mask;
                if (mask != null)
                {
                    TriggerEmphasis(mask);
                }
                continue;
            }

            if (!ReferenceEquals(previousMask, mask))
            {
                _lineMasks[line] = mask;
                if (mask != null)
                {
                    TriggerEmphasis(mask);
                }
            }
        }

        foreach (var line in _prepareOnClassOverlays.Keys.Except(currentLines).ToArray())
        {
            RemovePrepareOnClassOverlay(line);
        }
        UpdatePrepareWarningOverlay();
        UpdatePreviewOverlayHostVisibility();
        SyncPrepareOnClassOverlayHosts();
        UpdateAnimationTimer();
    }

    private bool IsAnyDynamicColorEnabled() =>
        (_settings.CustomBackgroundEnabled && _settings.DynamicBackgroundColorEnabled) ||
        (_settings.BorderEnabled && _settings.DynamicBorderColorEnabled) ||
        (_settings.ShadowEnabled && _settings.DynamicShadowColorEnabled);

    /// <summary>
    /// 由 <see cref="SmtcWatcher"/> 事件驱动调用（已调度到 UI 线程）。
    /// 媒体变化时应用动态取色与 SMTC 底图；暂停/停止时（若启用）恢复原始颜色。
    /// </summary>
    public void OnSmtcMediaChanged(AlbumAccentColors? colors, byte[]? thumbnailBytes, bool isPlaying, string? title, string? artist)
    {
        // 「文本内容 = 当前播放媒体标题」的图层：播放时显示标题，暂停/停止恢复原文本。
        _smtcPlaying = isPlaying;
        _smtcTitle = title ?? string.Empty;
        UpdateSmtcTitleLayers();

        // 动态修改 ClassIsland 全局主题强调色（FluentAvalonia CustomAccentColor）。
        if (_settings.DynamicThemeColorEnabled)
        {
            if (isPlaying && colors != null)
            {
                ApplyDynamicThemeColor(colors.Background);
            }
            else if (!isPlaying && _settings.RevertColorsWhenPaused)
            {
                RevertDynamicThemeColor();
            }
        }

        if (IsAnyDynamicColorEnabled())
        {
            if (isPlaying)
            {
                if (colors != null)
                {
                    StartColorTransition(colors);
                }
            }
            else if (_settings.RevertColorsWhenPaused)
            {
                RevertDynamicColors();
            }
        }

        if (_settings.WallpaperEnabled && _settings.WallpaperDesignerEnabled)
        {
            // 图层模式：把 SMTC 封面推送给所有来源为 SMTC 专辑封面的图层。
            var smtcLayers = _wallpaperLayerViews.Where(v => v.Settings.Source == WallpaperSource.SmtcAlbum).ToArray();
            if (smtcLayers.Length > 0)
            {
                if (isPlaying && thumbnailBytes is { Length: > 0 })
                {
                    foreach (var view in smtcLayers)
                    {
                        LoadLayerImage(view, thumbnailBytes);
                    }
                }
                else
                {
                    // 无真实封面（暂停/停止/无缩略图）时显示占位专辑封面，保持图层可见。
                    foreach (var view in smtcLayers)
                    {
                        LoadLayerPlaceholder(view);
                    }
                }
            }
        }
        else if (_settings.WallpaperEnabled && _settings.WallpaperSource == WallpaperSource.SmtcAlbum)
        {
            if (isPlaying && thumbnailBytes is { Length: > 0 })
            {
                LoadWallpaperImage(thumbnailBytes);
            }
            else if (!isPlaying && _settings.RevertColorsWhenPaused)
            {
                // 暂停/停止且“暂停恢复原色”开启：恢复原始颜色时一并移除底图封面。
                ClearWallpaperImage();
            }
            else if (thumbnailBytes is not { Length: > 0 })
            {
                ClearWallpaperImage();
            }
        }
    }

    /// <summary>把「显示媒体标题」的文本图层内容切换为当前播放标题；暂停/停止时恢复原文本。</summary>
    private void UpdateSmtcTitleLayers()
    {
        foreach (var view in _wallpaperLayerViews)
        {
            if (view.Control is WallpaperLayerVisual visual && view.Settings.TextUseSmtcTitle)
            {
                visual.OverrideText = _smtcPlaying && !string.IsNullOrEmpty(_smtcTitle) ? _smtcTitle : null;
            }
        }
    }

    private void EnsureDynamicColorsInitialized()
    {
        if (_dynamicColorsInitialized)
        {
            return;
        }

        _dynamicBackgroundColor = ParseColorOrDefault(_settings.BackgroundColor, Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
        _dynamicBorderColor = ParseColorOrDefault(_settings.BorderColor, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        _dynamicShadowColor = ParseColorOrDefault(_settings.ShadowColor, Color.FromArgb(0x99, 0, 0, 0));
        _dynamicColorsInitialized = true;
    }

    private void StartColorTransition(AlbumAccentColors colors)
    {
        EnsureDynamicColorsInitialized();

        // 边框与阴影保留用户在设置里配置的透明度，只替换色调。
        var borderAlpha = ParseColorOrDefault(_settings.BorderColor, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)).A;
        var shadowAlpha = ParseColorOrDefault(_settings.ShadowColor, Color.FromArgb(0x99, 0, 0, 0)).A;
        StartColorTransition(colors.Background, WithAlpha(colors.Border, borderAlpha), WithAlpha(colors.Shadow, shadowAlpha));
    }

    /// <summary>
    /// 暂停/停止播放时，把动态取色平滑过渡回用户在设置里配置的原始颜色。
    /// </summary>
    private void RevertDynamicColors()
    {
        EnsureDynamicColorsInitialized();
        StartColorTransition(
            ParseColorOrDefault(_settings.BackgroundColor, Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            ParseColorOrDefault(_settings.BorderColor, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            ParseColorOrDefault(_settings.ShadowColor, Color.FromArgb(0x99, 0, 0, 0)));
    }

    // ============ 动态修改 ClassIsland 全局主题色 ============

    /// <summary>
    /// 用 SMTC 专辑主色动态修改 ClassIsland 全局主题强调色（FluentAvalonia CustomAccentColor）。
    /// 宿主 IThemeService 为 DI 单例；任何失败静默降级，不影响其它功能。
    /// </summary>
    private void ApplyDynamicThemeColor(Color color)
    {
        try
        {
            ApplyDynamicThemeColorCore(color);
        }
        catch
        {
            // 宿主结构变化时忽略。
        }
    }

    private void ApplyDynamicThemeColorCore(Color color)
    {
        var themeService = IAppHost.TryGetService<IThemeService>();
        if (themeService == null)
        {
            return;
        }

        _lastDynamicThemeColor = color;
        _dynamicThemeColorApplied = true;
        themeService.SetTheme(ReadHostThemeMode(), color);
    }

    /// <summary>
    /// 恢复宿主在设置里配置的主题色（自定义 / 壁纸或屏幕取色 / 跟随系统）。
    /// </summary>
    private void RevertDynamicThemeColor()
    {
        try
        {
            RevertDynamicThemeColorCore();
        }
        catch
        {
            // 宿主结构变化时忽略。
        }
    }

    private void RevertDynamicThemeColorCore()
    {
        var themeService = IAppHost.TryGetService<IThemeService>();
        if (themeService == null)
        {
            return;
        }

        _lastDynamicThemeColor = null;
        _dynamicThemeColorApplied = false;
        themeService.SetTheme(ReadHostThemeMode(), ReadHostConfiguredThemeColor());
    }

    /// <summary>
    /// 每次 Apply 时同步动态主题色状态：开关关闭时恢复宿主配置；
    /// 开启时若已取到过专辑色则重新应用（宿主可能已按自身设置重置主题）。
    /// </summary>
    private void ApplyDynamicThemeColorState()
    {
        if (!_settings.DynamicThemeColorEnabled)
        {
            if (_dynamicThemeColorApplied)
            {
                RevertDynamicThemeColor();
            }

            return;
        }

        if (_lastDynamicThemeColor != null)
        {
            try
            {
                ApplyDynamicThemeColorCore(_lastDynamicThemeColor.Value);
            }
            catch
            {
                // 忽略。
            }
        }
    }

    /// <summary>
    /// 读取宿主当前主题模式（Settings.Theme：0=跟随系统 1=浅色 2=深色），
    /// 避免 SetTheme 时意外改变明暗模式。
    /// </summary>
    private int ReadHostThemeMode()
    {
        var settings = GetHostSettings();
        if (settings == null)
        {
            return 0;
        }

        try
        {
            return settings.GetType().GetProperty(HostContract.ThemeProperty)?.GetValue(settings) is int theme ? theme : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 按宿主 ColorSource 逻辑计算其当前应使用的主题主色：
    /// 0=自定义 PrimaryColor；1/3=壁纸/屏幕取色 SelectedPlatte；2=跟随系统(null)。
    /// </summary>
    private Color? ReadHostConfiguredThemeColor()
    {
        var settings = GetHostSettings();
        if (settings == null)
        {
            return null;
        }

        try
        {
            var colorSource = settings.GetType().GetProperty(HostContract.ColorSourceProperty)?.GetValue(settings) is int source ? source : 0;
            switch (colorSource)
            {
                case 0:
                    return settings.GetType().GetProperty(HostContract.PrimaryColorProperty)?.GetValue(settings) is Color primary ? primary : null;
                case 1:
                case 3:
                    return settings.GetType().GetProperty(HostContract.SelectedPlatteProperty)?.GetValue(settings) is Color platte ? platte : null;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    // ============ 交互：鼠标悬停保持可见 + 点击特效 ============

    /// <summary>
    /// 鼠标悬停保持可见：开启时覆写宿主「鼠标移入淡出」设置为关闭，
    /// 使鼠标移入主界面时主界面不会自动隐藏；禁用/卸载时恢复宿主原值。
    /// </summary>
    private void ApplyMouseHoverKeepVisible()
    {
        var settings = GetHostSettings();
        if (settings == null)
        {
            return;
        }

        try
        {
            var property = settings.GetType().GetProperty(HostContract.IsMouseInFadingEnabledProperty, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                return;
            }

            if (_settings.MouseHoverKeepVisible)
            {
                if (!_mouseInFadingOverridden)
                {
                    _originalMouseInFadingEnabled = property.GetValue(settings) is bool original ? original : null;
                    _mouseInFadingOverridden = true;
                }

                property.SetValue(settings, false);
            }
            else
            {
                RestoreMouseHoverKeepVisible();
            }
        }
        catch
        {
            // 宿主结构变化时忽略。
        }
    }

    private void RestoreMouseHoverKeepVisible()
    {
        if (!_mouseInFadingOverridden)
        {
            return;
        }

        if (_originalMouseInFadingEnabled != null)
        {
            try
            {
                var settings = GetHostSettings();
                settings?.GetType()
                    .GetProperty(HostContract.IsMouseInFadingEnabledProperty, BindingFlags.Instance | BindingFlags.Public)
                    ?.SetValue(settings, _originalMouseInFadingEnabled.Value);
            }
            catch
            {
                // 忽略。
            }
        }

        _mouseInFadingOverridden = false;
        _originalMouseInFadingEnabled = null;
    }

    private void AttachClickHandler()
    {
        if (_clickHandlerAttached || _islandRoot == null)
        {
            return;
        }

        _islandRoot.AddHandler(InputElement.PointerPressedEvent, IslandRootOnPointerPressed);
        _clickHandlerAttached = true;
    }

    private void DetachClickHandler()
    {
        if (_clickHandlerAttached && _islandRoot != null)
        {
            _islandRoot.RemoveHandler(InputElement.PointerPressedEvent, IslandRootOnPointerPressed);
        }

        _clickHandlerAttached = false;
    }

    private void ApplyClickEffectState()
    {
        if (_settings.ClickEffectEnabled)
        {
            AttachClickHandler();
        }
        else
        {
            DetachClickHandler();
        }
    }

    private void IslandRootOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_settings.Enabled || !_settings.ClickEffectEnabled || _settings.ClickEffectType == ClickEffectType.None ||
            _islandRoot == null || _mainWindow == null || _windowRoot == null)
        {
            return;
        }

        if (!e.GetCurrentPoint(_islandRoot).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var islandPoint = e.GetPosition(_islandRoot);
        switch (_settings.ClickEffectType)
        {
            case ClickEffectType.Bounce:
                TriggerClickBounce();
                break;
            case ClickEffectType.Ring:
                CreateClickRing(islandPoint);
                break;
        }
    }

    /// <summary>触发主界面轻微跳跃（点击特效）。</summary>
    private void TriggerClickBounce()
    {
        _clickBounceStart = DateTime.UtcNow;
        _clickBounceActive = true;
        UpdateAnimationTimer();
    }

    /// <summary>
    /// 在点击位置创建一个自绘的软边扩散圆环（点击特效，不复用提醒 Ripple 渲染）。
    /// </summary>
    private void CreateClickRing(Point islandPoint)
    {
        if (_mainWindow == null || _islandRoot == null || _windowRoot == null)
        {
            return;
        }

        if (!TryParseColor(_settings.RippleColor, out var color))
        {
            return;
        }

        var effectControls = TryGetFullScreenEffectHost(out var effectWindow);
        Point center;
        if (effectWindow != null)
        {
            var point = _islandRoot.TranslatePoint(islandPoint, _mainWindow) ?? islandPoint;
            try
            {
                center = effectWindow.PointToClient(_mainWindow.PointToScreen(point));
            }
            catch
            {
                center = new Point(effectWindow.Bounds.Width / 2, effectWindow.Bounds.Height / 2);
            }
        }
        else
        {
            center = _islandRoot.TranslatePoint(islandPoint, _windowRoot) ?? islandPoint;
        }

        var maxRadius = Math.Max(_islandRoot.Bounds.Width, _islandRoot.Bounds.Height) * 0.5;
        var ring = new ClickRingOverlay(center, color,
            TimeSpan.FromSeconds(Math.Max(0.1, _settings.RippleDurationSeconds)),
            maxRadius)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (effectControls != null)
        {
            effectControls.Add(ring);
            _rippleHosts[ring] = effectControls;
        }
        else
        {
            _windowRoot.Children.Add(ring);
        }

        _ripples.Add(ring);
    }

    /// <summary>
    /// 把点击「轻微跳跃」叠加到当前变形上：先微缩再回弹、轻微上移，约 0.4 秒内 ease-out 完成。
    /// </summary>
    private void ApplyBounceToTransform(ref double scale, ref double y)
    {
        if (!_clickBounceActive)
        {
            return;
        }

        var elapsed = (DateTime.UtcNow - _clickBounceStart).TotalSeconds;
        const double duration = 0.4;
        if (elapsed >= duration)
        {
            _clickBounceActive = false;
            return;
        }

        var p = elapsed / duration;
        var wave = Math.Sin(p * Math.PI); // 0 → 1 → 0
        scale *= 1 + 0.02 * wave;
        y -= 8 * wave;
    }

    // ============ 虚假天气 ============

    /// <summary>
    /// 虚假天气状态：开启时把伪造的 WeatherInfo 写入宿主 Settings.LastWeatherInfo，
    /// 并订阅宿主 Settings.PropertyChanged 在每次真实刷新后重新注入；关闭时取消并触发一次真实刷新。
    /// </summary>
    private void ApplyFakeWeatherState()
    {
        if (!_settings.FakeWeatherEnabled)
        {
            DisableFakeWeather();
            return;
        }

        var settings = GetHostSettings();
        if (settings != null && _hostWeatherHandler == null && settings is INotifyPropertyChanged notifier)
        {
            _hostWeatherHandler = OnHostSettingsPropertyChanged;
            notifier.PropertyChanged += _hostWeatherHandler;
        }

        InjectFakeWeather();
    }

    private void OnHostSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "LastWeatherInfo" && _settings.FakeWeatherEnabled)
        {
            InjectFakeWeather();
        }
    }

    private void InjectFakeWeather()
    {
        var settings = GetHostSettings();
        if (settings == null)
        {
            return;
        }

        try
        {
            var property = settings.GetType().GetProperty(HostContract.LastWeatherInfoProperty, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                return;
            }

            // 仅在「值未变化且当前已是本插件注入的实例」时跳过，避免注入→事件→再注入死循环；
            // 值变化（改温度/天气/湿度等）时必须重新注入，保证立即生效。
            var signature = $"{_settings.FakeWeatherCode}|{_settings.FakeWeatherTemperature}|{_settings.FakeWeatherFeelsLike}|" +
                            $"{_settings.FakeWeatherHumidity}|{_settings.FakeWeatherPressure}|{_settings.FakeWeatherVisibility}|" +
                            $"{_settings.FakeWeatherWindDirection}|{_settings.FakeWeatherWindScale}|{_settings.FakeWeatherAqi}|" +
                            $"{_settings.FakeWeatherAlertIcon}|{_settings.FakeWeatherAlertType}|{_settings.FakeWeatherAlertLevel}|" +
                            $"{_settings.FakeWeatherAlertTitle}|{_settings.FakeWeatherAlertDetail}|{_settings.FakeWeatherRainRemainingMinutes}";
            var current = property.GetValue(settings);
            if (_fakeWeatherInstance != null && ReferenceEquals(current, _fakeWeatherInstance) &&
                _fakeWeatherSignature == signature)
            {
                return;
            }

            var fake = BuildFakeWeatherInfo();
            _fakeWeatherInstance = fake;
            _fakeWeatherSignature = signature;
            property.SetValue(settings, fake);
            // 让天气组件/天气规则认为数据已刷新（否则部分显示与规则会等宿主自己刷新）。
            try
            {
                if (IAppHost.TryGetService<IWeatherService>() is { } weatherService)
                {
                    weatherService.IsWeatherRefreshed = true;
                }
            }
            catch
            {
                // 忽略。
            }
        }
        catch
        {
            // 宿主结构变化时忽略。
        }
    }

    private WeatherInfo BuildFakeWeatherInfo()
    {
        var temperature = _settings.FakeWeatherTemperature.ToString("0.#");
        var feelsLike = _settings.FakeWeatherFeelsLike.ToString("0.#");
        var humidity = _settings.FakeWeatherHumidity.ToString("0.#");
        var pressure = _settings.FakeWeatherPressure.ToString("0.#");
        var visibility = _settings.FakeWeatherVisibility.ToString("0.#");
        var weatherInfo = new WeatherInfo
        {
            UpdateTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Current = new CurrentWeather
            {
                Weather = _settings.FakeWeatherCode.ToString(),
                Temperature = new ValueUnitPair { Value = temperature, Unit = "℃" },
                FeelsLike = new ValueUnitPair { Value = feelsLike, Unit = "℃" },
                Humidity = new ValueUnitPair { Value = humidity, Unit = "%" },
                Pressure = new ValueUnitPair { Value = pressure, Unit = "hPa" },
                Visibility = new ValueUnitPair { Value = visibility, Unit = "km" },
                Wind = new WindInfo
                {
                    Direction = new ValueUnitPair { Value = _settings.FakeWeatherWindDirection, Unit = "" },
                    Speed = new ValueUnitPair { Value = _settings.FakeWeatherWindScale, Unit = "" }
                },
                PublishTime = DateTime.Now
            },
            Aqi = new AqiInfo { Aqi = _settings.FakeWeatherAqi.ToString("0.#") }
        };
        // 预警图标：宿主 WeatherComponent 用 Images[icon] 渲染图标，Type 渲染胶囊文字；
        // 四个等级 URL 是宿主内置的默认预警图标（触发 IsDefaultIcon 显示「图标+类型」胶囊）。
        var alertIconUrl = _settings.FakeWeatherAlertIcon switch
        {
            1 => "http://f5.market.xiaomi.com/download/Weather/0ac110d2ee20a454ab44f5df30f9fa6ff650e0b72/a.webp", // 蓝色
            2 => "http://f4.market.mi-img.com/download/Weather/072013febeb1944da85649e5e547ec5a8284816a2/a.webp", // 黄色
            3 => "http://f5.market.xiaomi.com/download/Weather/06db501333e6d4075a3364a66cdf23ba5733111b3/a.webp", // 橙色
            4 => "http://f3.market.xiaomi.com/download/Weather/03e3e096d3d9e485fa33bbf833fc3b3c96c23d014/a.webp", // 红色
            _ => ""
        };
        if (_settings.FakeWeatherAlertIcon != 0 ||
            !string.IsNullOrWhiteSpace(_settings.FakeWeatherAlertTitle) ||
            !string.IsNullOrWhiteSpace(_settings.FakeWeatherAlertType))
        {
            weatherInfo.Alerts.Add(new WeatherAlert
            {
                Title = _settings.FakeWeatherAlertTitle,
                Type = string.IsNullOrWhiteSpace(_settings.FakeWeatherAlertType)
                    ? _settings.FakeWeatherAlertTitle
                    : _settings.FakeWeatherAlertType,
                Level = _settings.FakeWeatherAlertLevel,
                Detail = _settings.FakeWeatherAlertDetail,
                PubTime = DateTime.Now,
                LocationKey = "fake",
                AlertId = "fake",
                Images = new Dictionary<string, string> { ["icon"] = alertIconUrl }
            });
        }

        // 降水提醒：宿主组件用 Minutely.Precipitation.Value（逐分钟降水强度列表）
        // 计算 RainRemainingMinutes（正值=距降雨开始，负值=正在下雨预计雨停）显示降水提醒。
        if (_settings.FakeWeatherRainRemainingMinutes != 0)
        {
            var rain = new List<double>();
            var minutes = Math.Abs(_settings.FakeWeatherRainRemainingMinutes);
            if (_settings.FakeWeatherRainRemainingMinutes > 0)
            {
                // 距降雨开始 minutes 分钟：先干后雨。
                for (var i = 0; i < minutes; i++)
                {
                    rain.Add(0);
                }

                rain.Add(1);
                for (var i = 0; i < 30; i++)
                {
                    rain.Add(0.5);
                }
            }
            else
            {
                // 正在下雨，预计 -minutes 分钟后停。
                for (var i = 0; i < minutes; i++)
                {
                    rain.Add(1);
                }

                rain.Add(0);
            }

            weatherInfo.Minutely.Precipitation.Value = rain;
        }

        return weatherInfo;
    }

    private void DisableFakeWeather()
    {
        if (_hostWeatherHandler != null && GetHostSettings() is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged -= _hostWeatherHandler;
        }

        _hostWeatherHandler = null;
        _fakeWeatherInstance = null;
        // 关闭后触发宿主立即拉取一次真实天气，尽快覆盖虚假数据。
        try
        {
            _ = IAppHost.TryGetService<IWeatherService>()?.QueryWeatherAsync();
        }
        catch
        {
            // 忽略。
        }
    }

    private void StartColorTransition(Color newBackground, Color newBorder, Color newShadow)
    {
        EnsureDynamicColorsInitialized();
        var duration = Math.Max(0, _settings.AlbumColorTransitionSeconds);
        if (duration <= 0)
        {
            _dynamicBackgroundColor = newBackground;
            _dynamicBorderColor = newBorder;
            _dynamicShadowColor = newShadow;
            _colorTransitionActive = false;
            RefreshDynamicColors();
            return;
        }

        _bgTransitionFrom = _dynamicBackgroundColor;
        _bgTransitionTo = newBackground;
        _borderTransitionFrom = _dynamicBorderColor;
        _borderTransitionTo = newBorder;
        _shadowTransitionFrom = _dynamicShadowColor;
        _shadowTransitionTo = newShadow;
        _colorTransitionStart = DateTime.UtcNow;
        _colorTransitionActive = true;
        UpdateAnimationTimer();
    }

    private void AdvanceColorTransition()
    {
        var duration = Math.Max(0.001, _settings.AlbumColorTransitionSeconds);
        var progress = Math.Clamp((DateTime.UtcNow - _colorTransitionStart).TotalSeconds / duration, 0, 1);
        // 三次缓出（ease-out cubic），让颜色切换更柔和。
        var eased = 1 - Math.Pow(1 - progress, 3);
        _dynamicBackgroundColor = Lerp(_bgTransitionFrom, _bgTransitionTo, eased);
        _dynamicBorderColor = Lerp(_borderTransitionFrom, _borderTransitionTo, eased);
        _dynamicShadowColor = Lerp(_shadowTransitionFrom, _shadowTransitionTo, eased);
        RefreshDynamicColors();
        if (progress >= 1)
        {
            _colorTransitionActive = false;
            UpdateAnimationTimer();
        }
    }

    private void RefreshDynamicColors()
    {
        EnsureDynamicColorsInitialized();

        var background = _settings.DynamicBackgroundColorEnabled
            ? _dynamicBackgroundColor
            : ParseColorOrDefault(_settings.BackgroundColor, _dynamicBackgroundColor);
        var border = _settings.DynamicBorderColorEnabled
            ? _dynamicBorderColor
            : ParseColorOrDefault(_settings.BorderColor, _dynamicBorderColor);
        var shadow = _settings.DynamicShadowColorEnabled
            ? _dynamicShadowColor
            : ParseColorOrDefault(_settings.ShadowColor, _dynamicShadowColor);

        foreach (var (borderControl, backgroundBrush, borderBrush) in _decorations)
        {
            if (borderControl.Name == "BackgroundBorder" && backgroundBrush != null)
            {
                UpdateBrushColor(backgroundBrush, background);
            }

            if (borderBrush != null && _settings.BorderEnabled)
            {
                UpdateBrushColor(borderBrush, border);
            }
        }

        if (_shadowEffect != null && _settings.ShadowEnabled)
        {
            _shadowEffect.Color = shadow;
        }
    }

    private static void UpdateBrushColor(IBrush brush, Color color)
    {
        switch (brush)
        {
            case SolidColorBrush solid:
                solid.Color = color;
                break;
            case LinearGradientBrush gradient when gradient.GradientStops.Count > 0:
                gradient.GradientStops[0].Color = color;
                break;
        }
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * t),
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color ParseColorOrDefault(string text, Color fallback) =>
        TryParseColor(text, out var color) ? color : fallback;

    // ============ 主界面底图 ============

    private void ApplyWallpaper()
    {
        if (_mainWindow == null)
        {
            return;
        }

        var enabled = _settings.Enabled && _settings.WallpaperEnabled;
        var designerActive = enabled && IsWallpaperDesignerActive();
        if (!enabled)
        {
            RemoveWallpaper();
            return;
        }

        if (designerActive)
        {
            EnsureWallpaperHost();
            if (_wallpaperHost == null)
            {
                return;
            }

            SyncWallpaperLayerViews();
            PositionWallpaperZOrder();
            ApplyWallpaperBlur();
            UpdateWallpaperTimer();
            ReloadWallpaperLayerImages();
            LayoutWallpaperLayers();
            UpdateFullscreenLayers();
            return;
        }

        // ---- 旧版简单模式（单图 / 幻灯片 / SMTC 封面，交叉淡化）----
        DisposeWallpaperLayerViews();
        DisposeFullscreenHost();
        if (_settings.WallpaperSource == WallpaperSource.None)
        {
            RemoveWallpaper();
            return;
        }

        EnsureWallpaperHost();
        PositionWallpaperZOrder();
        ApplyWallpaperBlur();
        UpdateWallpaperTimer();
        ReloadWallpaperImageIfNeeded();
        UpdateWallpaperPresentation();
    }

    /// <summary>当前是否运行图层式底图。</summary>
    private bool IsWallpaperDesignerActive() =>
        _settings.Enabled && _settings.WallpaperEnabled && _settings.WallpaperDesignerEnabled;

    private void EnsureWallpaperHost()
    {
        var islandGrid = _mainWindow?.FindControl<Grid>(HostContract.GridRoot);
        if (islandGrid == null)
        {
            return;
        }

        var mode = _settings.WallpaperDesignerEnabled ? WallpaperHostMode.Layers : WallpaperHostMode.Simple;
        if (_wallpaperHost != null)
        {
            if (_wallpaperHostMode == mode)
            {
                return;
            }

            // 模式切换（简单 <-> 图层）：重建宿主子内容并清空旧视图。
            _wallpaperLayers.Clear();
            DisposeWallpaperLayerViews();
            _wallpaperCanvas = null;
            _wallpaperHostMode = mode;
            _wallpaperHost.Child = BuildWallpaperHostChild(mode);
            UpdateWallpaperBounds();
            return;
        }

        _wallpaperLayers.Clear();
        _wallpaperHost = new Border
        {
            IsHitTestVisible = false,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = BuildWallpaperHostChild(mode)
        };
        _wallpaperHost.SizeChanged += (_, _) =>
        {
            UpdateWallpaperClip();
            LayoutWallpaperLayers();
        };
        islandGrid.SizeChanged += (_, _) => UpdateWallpaperBounds();
        islandGrid.Children.Insert(0, _wallpaperHost);
        _wallpaperHostMode = mode;
        ApplyWallpaperBlur();
        UpdateWallpaperClip();
        UpdateWallpaperBounds();
    }

    /// <summary>按当前模式构建宿主子内容（简单模式为双缓冲交叉淡化层，图层模式为锚点画布）。</summary>
    private Control BuildWallpaperHostChild(WallpaperHostMode mode)
    {
        if (mode == WallpaperHostMode.Layers)
        {
            _wallpaperCanvas = new Canvas
            {
                IsHitTestVisible = false,
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            return _wallpaperCanvas;
        }

        _wallpaperLayers.Clear();
        _wallpaperLayers.Add(new Border { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch });
        _wallpaperLayers.Add(new Border { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch });
        return new Grid { IsHitTestVisible = false, Children = { _wallpaperLayers[0], _wallpaperLayers[1] } };
    }

    /// <summary>
    /// 按设置对底图宿主应用高斯模糊（<see cref="InjectorSettings.WallpaperBlurRadius"/>，0 表示关闭）。
    /// </summary>
    private void ApplyWallpaperBlur()
    {
        if (_wallpaperHost == null)
        {
            return;
        }

        var radius = Math.Max(0, _settings.WallpaperBlurRadius);
        if (radius <= 0)
        {
            _wallpaperHost.Effect = null;
            return;
        }

        _wallpaperBlur ??= new BlurEffect();
        _wallpaperBlur.Radius = radius;
        _wallpaperHost.Effect = _wallpaperBlur;
    }

    /// <summary>
    /// 把底图约束到主界面的实际可见边界内（各行的 BackgroundBorder 并集），
    /// 避免底图溢出到整个窗口区域。
    /// </summary>
    private void UpdateWallpaperBounds(IEnumerable<Control>? descendants = null)
    {
        if (_wallpaperHost == null)
        {
            return;
        }

        var controls = descendants ?? _mainWindow?.GetVisualDescendants().OfType<Control>();
        if (controls != null)
        {
            ApplyOverlayHostBounds(_wallpaperHost, controls);
        }

        UpdateWallpaperClip();
        LayoutWallpaperLayers();
    }

    /// <summary>
    /// 按行同步/定位所有底纹宿主：为每个主界面行的模板 GridRoot 建立宿主，
    /// 约束到该行 BackgroundBorder 边界，并清理已消失行的宿主。
    /// </summary>
    private void UpdateTextureBounds(IEnumerable<Control>? descendants = null)
    {
        if (_mainWindow == null)
        {
            return;
        }

        if (!_settings.Enabled || _settings.BackgroundTextureType == BackgroundTexture.None)
        {
            StopSpectrum();
            RemoveTextureHost();
            return;
        }

        EnsureTextureBrush();

        var controls = (descendants ?? _mainWindow.GetVisualDescendants().OfType<Control>()).ToArray();
        var liveRoots = new HashSet<Grid>();
        foreach (var gridRoot in controls.OfType<Grid>()
                     .Where(x => x.Name == HostContract.GridRoot &&
                                 x.FindAncestorOfType<Control>()?.GetType().FullName == HostContract.MainWindowLineTypeName))
        {
            liveRoots.Add(gridRoot);
            var host = EnsureTextureHost(gridRoot);
            if (host != null)
            {
                PositionTextureHost(host, gridRoot);
            }
        }

        foreach (var stale in _textureHosts.Keys.Where(k => !liveRoots.Contains(k)).ToArray())
        {
            if (_textureHosts.Remove(stale, out var removed) && removed.Parent is Panel panel)
            {
                panel.Children.Remove(removed);
            }
        }
    }

    /// <summary>
    /// 把单行底纹宿主约束到该行 BackgroundBorder 的实际可见边界内（行模板坐标空间）。
    /// </summary>
    private void PositionTextureHost(Border host, Grid gridRoot)
    {
        var background = gridRoot.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(x => x.Name == HostContract.BackgroundBorder && x.IsVisible && x.Bounds.Width > 0 && x.Bounds.Height > 0);
        if (background == null)
        {
            host.IsVisible = false;
            return;
        }

        var topLeft = background.TranslatePoint(new Point(0, 0), gridRoot);
        if (topLeft == null)
        {
            host.IsVisible = false;
            return;
        }

        host.IsVisible = true;
        host.Width = background.Bounds.Width;
        host.Height = background.Bounds.Height;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.VerticalAlignment = VerticalAlignment.Top;
        host.Margin = new Thickness(topLeft.Value.X, topLeft.Value.Y, 0, 0);
        UpdateTextureClip(host);
    }

    /// <summary>
    /// 把覆盖层宿主（底图）约束到主界面各行的 BackgroundBorder 并集边界内。
    /// </summary>
    private void ApplyOverlayHostBounds(Border host, IEnumerable<Control> descendants)
    {
        if (host.Parent is not Visual parent)
        {
            return;
        }

        var borders = descendants.OfType<Border>()
            .Where(x => x.Name == HostContract.BackgroundBorder && x.IsVisible && x.Bounds.Width > 0 && x.Bounds.Height > 0)
            .ToArray();
        if (borders.Length == 0)
        {
            return;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var border in borders)
        {
            var topLeft = border.TranslatePoint(new Point(0, 0), parent);
            var bottomRight = border.TranslatePoint(new Point(border.Bounds.Width, border.Bounds.Height), parent);
            if (topLeft == null || bottomRight == null)
            {
                continue;
            }

            minX = Math.Min(minX, topLeft.Value.X);
            minY = Math.Min(minY, topLeft.Value.Y);
            maxX = Math.Max(maxX, bottomRight.Value.X);
            maxY = Math.Max(maxY, bottomRight.Value.Y);
        }

        if (minX > maxX || minY > maxY)
        {
            return;
        }

        host.Width = maxX - minX;
        host.Height = maxY - minY;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.VerticalAlignment = VerticalAlignment.Top;
        host.Margin = new Thickness(minX, minY, 0, 0);
    }

    private void ApplyOverlayClip(Border? host)
    {
        if (host == null)
        {
            return;
        }

        // 底图/纹理宿主的圆角跟随当前生效圆角（与宿主 RadiusX 保持一致），
        // 避免覆盖层与宿主内容裁切不一致。
        host.CornerRadius = new CornerRadius(_effectiveCornerRadius);
    }

    private void UpdateWallpaperClip() => ApplyOverlayClip(_wallpaperHost);

    private void UpdateTextureClip()
    {
        foreach (var host in _textureHosts.Values)
        {
            ApplyOverlayClip(host);
        }
    }

    private void UpdateTextureClip(Border host) => ApplyOverlayClip(host);

    private void RemoveWallpaper()
    {
        _wallpaperTimer.Stop();
        _wallpaperTransitionActive = false;
        if (_wallpaperHost != null && _wallpaperHost.Parent is Panel panel)
        {
            panel.Children.Remove(_wallpaperHost);
        }

        _wallpaperHost = null;
        _wallpaperHostMode = WallpaperHostMode.None;
        _wallpaperCanvas = null;
        _wallpaperLayers.Clear();
        DisposeWallpaperLayerViews();
        DisposeFullscreenHost();
        _wallpaperSlideshow.Clear();
        _wallpaperSlideshowIndex = 0;
        _wallpaperLoadedSource = WallpaperSource.None;
        _wallpaperLoadedPath = string.Empty;
        DisposeWallpaperBitmap();
    }

    // ============ 图层式底图（Photoshop 风格编辑器产物）============

    /// <summary>
    /// 同步图层视图与设置图层列表一致：为每个可见且有来源的图层建立 Image 视图，
    /// 移除已删除图层的视图（含释放位图），并保持渲染顺序（列表顺序即 z 序）。
    /// </summary>
    private void SyncWallpaperLayerViews()
    {
        if (_wallpaperCanvas == null)
        {
            return;
        }

        // 位图图层需要来源；形状 / 文本图层（Kind != Image）始终参与渲染；
        // 「扩展到整个显示框架」的图层由独立全屏宿主渲染，不参与主界面画布。
        var wanted = _settings.WallpaperLayers
            .Where(l => l.Visible && !l.FullscreenExtend &&
                        (l.Kind != WallpaperLayerKind.Image || l.Source != WallpaperSource.None))
            .ToList();
        var wantedIds = wanted.Select(l => l.Id).ToHashSet();
        foreach (var stale in _wallpaperLayerViews.Where(v => !wantedIds.Contains(v.Settings.Id)).ToArray())
        {
            _wallpaperCanvas.Children.Remove(stale.Control);
            DisposeLayerView(stale);
            _wallpaperLayerViews.Remove(stale);
        }

        var existing = _wallpaperLayerViews.ToDictionary(v => v.Settings.Id);
        foreach (var layer in wanted)
        {
            if (existing.TryGetValue(layer.Id, out var view))
            {
                // 防御：图层 Kind 变化（位图 ↔ 形状/文本）时重建对应控件。
                var kindMismatch = (layer.Kind == WallpaperLayerKind.Image) != (view.Control is Image);
                if (kindMismatch)
                {
                    _wallpaperCanvas.Children.Remove(view.Control);
                    DisposeLayerView(view);
                    _wallpaperLayerViews.Remove(view);
                }
                else
                {
                    view.Settings = layer;
                    if (view.Control is WallpaperLayerVisual visual)
                    {
                        visual.Layer = layer;
                    }

                    continue;
                }
            }

            Control control = layer.Kind == WallpaperLayerKind.Image
                ? new Image
                {
                    IsHitTestVisible = false,
                    Stretch = Stretch.Fill,
                    RenderTransformOrigin = RelativePoint.Center
                }
                : new WallpaperLayerVisual
                {
                    IsHitTestVisible = false,
                    RenderTransformOrigin = RelativePoint.Center,
                    Layer = layer
                };
            _wallpaperLayerViews.Add(new WallpaperLayerView { Settings = layer, Control = control });
            _wallpaperCanvas.Children.Add(control);
        }

        for (var i = 0; i < _wallpaperLayerViews.Count; i++)
        {
            _wallpaperLayerViews[i].Control.ZIndex = i;
        }
    }

    /// <summary>
    /// 按当前主界面尺寸与各图层设置重排图层矩形（锚点相对定位 + 尺寸模式 + 旋转）。
    /// 主界面尺寸变化（宿主 SizeChanged）与图片加载完成时调用。
    /// </summary>
    private void LayoutWallpaperLayers()
    {
        if (_wallpaperHost == null || _wallpaperCanvas == null)
        {
            return;
        }

        var w = _wallpaperHost.Bounds.Width;
        var h = _wallpaperHost.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        foreach (var view in _wallpaperLayerViews)
        {
            var layer = view.Settings;
            var control = view.Control;
            var aspect = view.Bitmap is { PixelSize.Width: > 0, PixelSize.Height: > 0 }
                ? (double)view.Bitmap.PixelSize.Width / view.Bitmap.PixelSize.Height
                : (double?)null;
            var rect = WallpaperLayerLayout.ComputeRect(layer, w, h, aspect);
            control.Width = rect.Width;
            control.Height = rect.Height;
            Canvas.SetLeft(control, rect.X);
            Canvas.SetTop(control, rect.Y);
            control.RenderTransform = new RotateTransform(layer.Rotation);
            control.Opacity = layer.Opacity;
            control.IsVisible = layer.Visible;
            if (control is Image image)
            {
                image.Stretch = WallpaperLayerLayout.ToStretch(layer.DisplayMode);
            }
            else if (control is WallpaperLayerVisual visual)
            {
                visual.Layer = layer;
            }
        }

        UpdateSmtcTitleLayers();
    }

    /// <summary>按来源重新加载各图层图片（本地图片 / 幻灯片；SMTC 封面由事件推送，无封面时用占位图）。</summary>
    private void ReloadWallpaperLayerImages()
    {
        foreach (var view in _wallpaperLayerViews)
        {
            // 形状 / 文本图层无需加载位图。
            if (view.Settings.Kind != WallpaperLayerKind.Image)
            {
                continue;
            }

            if (view.Settings.Source == WallpaperSource.SmtcAlbum)
            {
                if (view.LoadedSource == view.Settings.Source && view.LoadedPath == view.Settings.Path)
                {
                    continue;
                }

                view.LoadedSource = view.Settings.Source;
                view.LoadedPath = view.Settings.Path;
                LoadLayerPlaceholder(view);
                continue;
            }

            if (view.LoadedSource == view.Settings.Source && view.LoadedPath == view.Settings.Path)
            {
                continue;
            }

            view.LoadedSource = view.Settings.Source;
            view.LoadedPath = view.Settings.Path;
            switch (view.Settings.Source)
            {
                case WallpaperSource.LocalImage:
                    LoadLayerImage(view, view.Settings.Path);
                    break;
                case WallpaperSource.FolderSlideshow:
                    BuildLayerSlideshow(view);
                    if (view.SlideshowFiles.Count > 0)
                    {
                        view.SlideshowIndex = 0;
                        LoadLayerImage(view, view.SlideshowFiles[0]);
                    }
                    else
                    {
                        ClearLayerImage(view);
                    }
                    break;
                default:
                    ClearLayerImage(view);
                    break;
            }
        }
    }

    private void LoadLayerImage(WallpaperLayerView view, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearLayerImage(view);
            return;
        }

        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));
            stream.Position = 0;
            var bitmap = new Bitmap(stream);
            SetLayerImage(view, bitmap, stream);
        }
        catch
        {
            ClearLayerImage(view);
        }
    }

    private void LoadLayerImage(WallpaperLayerView view, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        try
        {
            var stream = new MemoryStream(bytes);
            stream.Position = 0;
            var bitmap = new Bitmap(stream);
            SetLayerImage(view, bitmap, stream);
        }
        catch
        {
            ClearLayerImage(view);
        }
    }

    /// <summary>SMTC 图层的占位封面路径（插件 Assets/album.png）。</summary>
    private static string? SmtcPlaceholderPath() =>
        Path.GetDirectoryName(typeof(MainWindowStyleInjector).Assembly.Location) is { } dir
            ? Path.Combine(dir, "Assets", "album.png")
            : null;

    /// <summary>为 SMTC 图层加载占位专辑封面（无真实封面时显示）。</summary>
    private void LoadLayerPlaceholder(WallpaperLayerView view)
    {
        var path = SmtcPlaceholderPath();
        if (path != null && File.Exists(path))
        {
            LoadLayerImage(view, path);
        }
        else
        {
            ClearLayerImage(view);
        }
    }

    private void SetLayerImage(WallpaperLayerView view, Bitmap bitmap, MemoryStream stream)
    {
        DisposeLayerBitmap(view);
        view.Bitmap = bitmap;
        view.Stream = stream;
        view.ImageControl!.Source = bitmap;
        LayoutWallpaperLayers();
    }

    private void ClearLayerImage(WallpaperLayerView view)
    {
        DisposeLayerBitmap(view);
        view.ImageControl!.Source = null;
        LayoutWallpaperLayers();
    }

    private void BuildLayerSlideshow(WallpaperLayerView view)
    {
        view.SlideshowFiles.Clear();
        var directory = view.Settings.Path;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        view.SlideshowFiles.AddRange(Directory.EnumerateFiles(directory)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void AdvanceLayerSlideshow(WallpaperLayerView view)
    {
        if (view.SlideshowFiles.Count == 0)
        {
            return;
        }

        view.SlideshowIndex = (view.SlideshowIndex + 1) % view.SlideshowFiles.Count;
        LoadLayerImage(view, view.SlideshowFiles[view.SlideshowIndex]);
    }

    private void DisposeLayerBitmap(WallpaperLayerView view)
    {
        view.Bitmap?.Dispose();
        view.Bitmap = null;
        view.Stream?.Dispose();
        view.Stream = null;
    }

    private void DisposeLayerView(WallpaperLayerView view)
    {
        DisposeLayerBitmap(view);
        if (view.ImageControl is { } image)
        {
            image.Source = null;
        }
    }

    // ============ 全屏底图扩展（整个 ClassIsland 显示框架）============

    /// <summary>是否有启用「扩展到整个显示框架」的可见图片图层。</summary>
    private bool HasFullscreenLayer() =>
        _settings.WallpaperLayers.Any(l => l.FullscreenExtend && l.Visible && l.Kind == WallpaperLayerKind.Image);

    /// <summary>
    /// 同步全屏底图宿主：为每个「全屏扩展」图层建立覆盖整个 GridRoot 的九宫格渲染控件，
    /// 无此类图层时销毁宿主。
    /// </summary>
    private void UpdateFullscreenLayers()
    {
        var wanted = _settings.WallpaperLayers
            .Where(l => l.FullscreenExtend && l.Visible && l.Kind == WallpaperLayerKind.Image)
            .ToList();
        if (wanted.Count == 0)
        {
            DisposeFullscreenHost();
            return;
        }

        var islandGrid = _mainWindow?.FindControl<Grid>(HostContract.GridRoot);
        if (islandGrid == null)
        {
            return;
        }

        if (_fullscreenCanvas == null)
        {
            _fullscreenCanvas = new Canvas
            {
                IsHitTestVisible = false,
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            // 插到最底（索引 0），跨所有行列铺满整个显示框架。
            islandGrid.Children.Insert(0, _fullscreenCanvas);
            Grid.SetRow(_fullscreenCanvas, 0);
            Grid.SetRowSpan(_fullscreenCanvas, Math.Max(1, islandGrid.RowDefinitions.Count));
            Grid.SetColumn(_fullscreenCanvas, 0);
            Grid.SetColumnSpan(_fullscreenCanvas, Math.Max(1, islandGrid.ColumnDefinitions.Count));
            _fullscreenCanvas.SizeChanged += (_, _) => LayoutFullscreenLayers();
        }

        // 同步九宫格渲染控件。
        foreach (var stale in _fullscreenVisuals.Keys.Where(id => wanted.All(l => l.Id != id)).ToArray())
        {
            _fullscreenCanvas.Children.Remove(_fullscreenVisuals[stale]);
            _fullscreenVisuals.Remove(stale);
        }

        foreach (var layer in wanted)
        {
            if (!_fullscreenVisuals.TryGetValue(layer.Id, out var visual))
            {
                visual = new WallpaperNineSliceVisual();
                _fullscreenVisuals[layer.Id] = visual;
                _fullscreenCanvas.Children.Add(visual);
            }

            visual.Bitmap = GetFullscreenBitmap(layer);
            visual.SliceEnabled = layer.SliceEnabled;
            visual.SliceLeft = layer.SliceLeft;
            visual.SliceTop = layer.SliceTop;
            visual.SliceRight = layer.SliceRight;
            visual.SliceBottom = layer.SliceBottom;
            visual.Opacity = layer.Opacity;
        }

        LayoutFullscreenLayers();
    }

    /// <summary>加载（并缓存）全屏图层的位图，签名去重避免重复解码。</summary>
    private Bitmap? GetFullscreenBitmap(WallpaperLayerItem layer)
    {
        var signature = $"{layer.Source}|{layer.Path}";
        if (_fullscreenSignatures.TryGetValue(layer.Id, out var loaded) && loaded == signature &&
            _fullscreenBitmaps.TryGetValue(layer.Id, out var cached))
        {
            return cached;
        }

        _fullscreenSignatures[layer.Id] = signature;
        if (_fullscreenBitmaps.TryGetValue(layer.Id, out var old))
        {
            old.Dispose();
            _fullscreenBitmaps.Remove(layer.Id);
        }

        if (layer.Source != WallpaperSource.LocalImage || string.IsNullOrWhiteSpace(layer.Path) || !File.Exists(layer.Path))
        {
            return null;
        }

        try
        {
            var bm = new Bitmap(layer.Path);
            _fullscreenBitmaps[layer.Id] = bm;
            return bm;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>让全屏渲染控件铺满宿主画布（宿主尺寸变化时由 SizeChanged 触发）。</summary>
    private void LayoutFullscreenLayers()
    {
        if (_fullscreenCanvas == null)
        {
            return;
        }

        var w = _fullscreenCanvas.Bounds.Width;
        var h = _fullscreenCanvas.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        foreach (var visual in _fullscreenVisuals.Values)
        {
            visual.Width = w;
            visual.Height = h;
            Canvas.SetLeft(visual, 0);
            Canvas.SetTop(visual, 0);
        }
    }

    /// <summary>销毁全屏宿主并释放其位图。</summary>
    private void DisposeFullscreenHost()
    {
        foreach (var bm in _fullscreenBitmaps.Values)
        {
            bm.Dispose();
        }

        _fullscreenBitmaps.Clear();
        _fullscreenSignatures.Clear();
        _fullscreenVisuals.Clear();
        if (_fullscreenCanvas != null)
        {
            (_fullscreenCanvas.Parent as Grid)?.Children.Remove(_fullscreenCanvas);
            _fullscreenCanvas = null;
        }
    }


    private void DisposeWallpaperLayerViews()
    {
        foreach (var view in _wallpaperLayerViews)
        {
            DisposeLayerView(view);
        }

        _wallpaperLayerViews.Clear();
    }

    /// <summary>按设置把底图宿主插入主界面 GridRoot 的对应层级（底色后 / 底色上组件下 / 组件上）。</summary>
    private void PositionWallpaperZOrder()
    {
        if (_wallpaperHost == null)
        {
            return;
        }

        var islandGrid = _mainWindow?.FindControl<Grid>(HostContract.GridRoot) ?? _wallpaperHost.Parent as Grid;
        if (islandGrid == null)
        {
            return;
        }

        var targetIndex = _settings.WallpaperZOrder switch
        {
            WallpaperLayerZOrder.BehindBackground => 0,
            WallpaperLayerZOrder.AboveBackground => FindTextureInsertIndex(islandGrid),
            WallpaperLayerZOrder.AboveComponents => islandGrid.Children.Count,
            _ => 0
        };
        var currentIndex = islandGrid.Children.IndexOf(_wallpaperHost);
        if (currentIndex < 0)
        {
            islandGrid.Children.Insert(Math.Clamp(targetIndex, 0, islandGrid.Children.Count), _wallpaperHost);
            return;
        }

        if (currentIndex == targetIndex)
        {
            return;
        }

        islandGrid.Children.Remove(_wallpaperHost);
        islandGrid.Children.Insert(Math.Clamp(targetIndex, 0, islandGrid.Children.Count), _wallpaperHost);
    }

    /// <summary>当前主界面的可见尺寸（供图层编辑器初始化预览画布；不可用返回 null）。</summary>
    public Size? GetIslandSize()
    {
        if (_wallpaperHost is { Bounds.Width: > 0, Bounds.Height: > 0 })
        {
            return _wallpaperHost.Bounds.Size;
        }

        if (_mainWindow == null)
        {
            return null;
        }

        var borders = _mainWindow.GetVisualDescendants().OfType<Border>()
            .Where(x => x.Name == HostContract.BackgroundBorder && x.IsVisible && x.Bounds.Width > 0 && x.Bounds.Height > 0)
            .ToArray();
        if (borders.Length == 0)
        {
            return null;
        }

        var minX = borders.Min(b => b.Bounds.X);
        var minY = borders.Min(b => b.Bounds.Y);
        var maxX = borders.Max(b => b.Bounds.X + b.Bounds.Width);
        var maxY = borders.Max(b => b.Bounds.Y + b.Bounds.Height);
        return new Size(maxX - minX, maxY - minY);
    }

    // ============ 背景填充纹理 ============

    private void ApplyTextureHost()
    {
        if (_mainWindow == null)
        {
            return;
        }

        var enabled = _settings.Enabled && _settings.BackgroundTextureType != BackgroundTexture.None;
        if (!enabled)
        {
            StopSpectrum();
            RemoveTextureHost();
            return;
        }

        // 动态频谱：由逐帧绘制的覆盖层渲染，宿主需重建以挂接覆盖层。
        if (_settings.BackgroundTextureType == BackgroundTexture.Spectrum)
        {
            StartSpectrum();
            if (_textureBrush != null)
            {
                RemoveTextureHost();
            }

            _textureBrush = null;
        }
        else
        {
            StopSpectrum();
            // 从频谱切回常规纹理时，宿主带着覆盖层子项，需重建。
            if (_spectrumOverlays.Count > 0)
            {
                RemoveTextureHost();
            }

            var color = TryParseColor(_settings.BackgroundTextureColor, out var parsed)
                ? parsed
                : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
            _textureBrush = BuildTextureBrush(_settings.BackgroundTextureType, color, _settings.BackgroundTextureSize);
            foreach (var host in _textureHosts.Values)
            {
                host.Background = _textureBrush;
            }
        }

        UpdateTextureBounds();
        UpdateTextureClip();
    }

    private void EnsureTextureBrush()
    {
        if (_textureBrush != null || _settings.BackgroundTextureType == BackgroundTexture.Spectrum)
        {
            return;
        }

        var color = TryParseColor(_settings.BackgroundTextureColor, out var parsed)
            ? parsed
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        _textureBrush = BuildTextureBrush(_settings.BackgroundTextureType, color, _settings.BackgroundTextureSize);
    }

    /// <summary>
    /// 为单个主界面行（MainWindowLine 模板 GridRoot）创建底纹宿主，
    /// 插入到该行底色 Border 之后，使其渲染在底色填充之上、组件内容之下。
    /// </summary>
    private Border? EnsureTextureHost(Grid gridRoot)
    {
        if (_textureHosts.TryGetValue(gridRoot, out var existing))
        {
            return existing;
        }

        var host = new Border
        {
            IsHitTestVisible = false,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (_settings.BackgroundTextureType == BackgroundTexture.Spectrum)
        {
            // 频谱覆盖层直接绘制柱条，挂为宿主子项（铺满宿主，柱条底部对齐）。
            StartSpectrum();
            if (_spectrumCapture != null)
            {
                var overlay = new SpectrumTextureOverlay(_spectrumCapture);
                _spectrumOverlays.Add(overlay);
                host.Child = overlay;
            }
        }
        else
        {
            EnsureTextureBrush();
            host.Background = _textureBrush;
        }

        gridRoot.Children.Insert(FindTextureInsertIndex(gridRoot), host);
        _textureHosts[gridRoot] = host;
        return host;
    }

    /// <summary>
    /// 在行模板 GridRoot 中定位底色 Border（或 Fluent 主题的包装层），
    /// 返回其后的插入索引，使底纹恰好位于底色之上、组件之下。
    /// </summary>
    private static int FindTextureInsertIndex(Grid gridRoot)
    {
        for (var i = 0; i < gridRoot.Children.Count; i++)
        {
            if (gridRoot.Children[i] is Border border &&
                (border.Name == HostContract.BackgroundBorder || border.Name == HostContract.BackgroundBorderWrapper))
            {
                return i + 1;
            }
        }

        return 1;
    }

    private void RemoveTextureHost()
    {
        foreach (var host in _textureHosts.Values)
        {
            if (host.Parent is Panel panel)
            {
                panel.Children.Remove(host);
            }
        }

        _textureHosts.Clear();
        _spectrumOverlays.Clear();
        _textureBrush = null;
    }

    /// <summary>
    /// 构建可平铺的纹理画刷（网格 / 点阵 / 斜线 / 十字）。
    /// 「动态频谱」不使用画刷，由 SpectrumTextureOverlay 逐帧绘制。
    /// </summary>
    private IBrush BuildTextureBrush(BackgroundTexture type, Color color, double size)
    {
        size = Math.Max(8, size);
        var pen = new Pen(new SolidColorBrush(color), Math.Max(0.5, size / 12));
        var group = new DrawingGroup();
        switch (type)
        {
            case BackgroundTexture.Grid:
                group.Children.Add(new GeometryDrawing { Geometry = new LineGeometry(new Point(0, 0), new Point(size, 0)), Pen = pen });
                group.Children.Add(new GeometryDrawing { Geometry = new LineGeometry(new Point(0, 0), new Point(0, size)), Pen = pen });
                break;
            case BackgroundTexture.Dots:
            {
                var dot = Math.Max(0.5, size / 12);
                group.Children.Add(new GeometryDrawing
                {
                    Geometry = new EllipseGeometry(new Rect(size / 2 - dot, size / 2 - dot, dot * 2, dot * 2)),
                    Brush = pen.Brush
                });
                break;
            }
            case BackgroundTexture.DiagonalLines:
                group.Children.Add(new GeometryDrawing { Geometry = new LineGeometry(new Point(0, size), new Point(size, 0)), Pen = pen });
                break;
            case BackgroundTexture.Cross:
                group.Children.Add(new GeometryDrawing { Geometry = new LineGeometry(new Point(0, size), new Point(size, 0)), Pen = pen });
                group.Children.Add(new GeometryDrawing { Geometry = new LineGeometry(new Point(0, 0), new Point(size, size)), Pen = pen });
                break;
        }

        return new DrawingBrush
        {
            Drawing = group,
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, size, size, RelativeUnit.Absolute)
        };
    }

    /// <summary>
    /// 启用动态频谱底纹：启动系统声音输出回环捕获，并保证 16ms 动画计时器保持运行。
    /// NAudio 加载/初始化失败时静默降级（频谱保持静止，不影响其它功能）。
    /// </summary>
    private void StartSpectrum()
    {
        if (_spectrumCapture == null)
        {
            try
            {
                _spectrumCapture = new AudioSpectrumCapture();
            }
            catch
            {
                _spectrumCapture = null;
                return;
            }
        }

        _spectrumCapture.Start();
        _spectrumActive = true;
        UpdateAnimationTimer();
    }

    /// <summary>
    /// 停用动态频谱底纹：停止回环捕获并释放动画计时器驱动。
    /// </summary>
    private void StopSpectrum()
    {
        if (!_spectrumActive && _spectrumCapture == null)
        {
            return;
        }

        _spectrumActive = false;
        _spectrumCapture?.Stop();
        UpdateAnimationTimer();
    }

    /// <summary>
    /// 每帧更新动态频谱底纹：把最新参数同步给各行频谱覆盖层并请求重绘。
    /// 柱条在覆盖层 Render 中读取回环电平直接绘制（底部对齐，可选上下镜像）。
    /// </summary>
    private void UpdateSpectrum()
    {
        if (!_spectrumActive)
        {
            return;
        }

        var color = TryParseColor(_settings.BackgroundTextureColor, out var parsed)
            ? parsed
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        var bars = Math.Clamp(_settings.BackgroundTextureSpectrumBars, 4, 64);
        var sensitivity = _settings.BackgroundTextureSpectrumSensitivity;
        var mirrored = _settings.BackgroundTextureSpectrumMirrored;
        var autoWidth = _settings.BackgroundTextureSpectrumAutoWidth;
        foreach (var overlay in _spectrumOverlays)
        {
            overlay.Update(color, bars, sensitivity, mirrored, autoWidth);
        }

        // 节流诊断日志：排查「频谱不动」时查看配置目录 preview-debug.log。
        if ((DateTime.UtcNow - _lastSpectrumLog).TotalSeconds >= 2)
        {
            _lastSpectrumLog = DateTime.UtcNow;
            var running = _spectrumCapture?.IsRunning == true;
            var maxLevel = 0f;
            if (running && _spectrumCapture != null)
            {
                var sample = new float[32];
                _spectrumCapture.GetLevels(sample);
                maxLevel = sample.Max();
            }

            DebugLog($"频谱诊断: active={_spectrumActive} running={running} overlays={_spectrumOverlays.Count} maxLevel={maxLevel:F3} timer={_animationTimer.IsEnabled}");
        }
    }

    private void DisposeWallpaperBitmap()
    {
        _wallpaperBitmap?.Dispose();
        _wallpaperBitmap = null;
        _wallpaperStream?.Dispose();
        _wallpaperStream = null;
        _wallpaperRetiredBitmap?.Dispose();
        _wallpaperRetiredBitmap = null;
        _wallpaperRetiredStream?.Dispose();
        _wallpaperRetiredStream = null;
    }

    private void UpdateWallpaperTimer()
    {
        if (!_settings.Enabled || !_settings.WallpaperEnabled)
        {
            _wallpaperTimer.Stop();
            return;
        }

        if (_settings.WallpaperDesignerEnabled)
        {
            // 图层模式：取所有幻灯片图层间隔的最小值为心跳频率，各图层按自身间隔推进。
            var intervals = _wallpaperLayerViews
                .Where(v => v.Settings.Source == WallpaperSource.FolderSlideshow && v.SlideshowFiles.Count > 1)
                .Select(v => Math.Clamp(v.Settings.SlideshowIntervalSeconds, 2, 3600))
                .ToArray();
            if (intervals.Length == 0)
            {
                _wallpaperTimer.Stop();
                return;
            }

            _wallpaperTimer.Interval = TimeSpan.FromSeconds(intervals.Min());
            _wallpaperTimer.Start();
            return;
        }

        if (_settings.WallpaperSource != WallpaperSource.FolderSlideshow)
        {
            _wallpaperTimer.Stop();
            return;
        }

        // SMTC 底图由 SmtcWatcher 事件驱动，无需定时轮询。
        _wallpaperTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.WallpaperSlideshowIntervalSeconds, 2, 3600));
        _wallpaperTimer.Start();
    }

    private void OnWallpaperTimerTick(object? sender, EventArgs e)
    {
        if (!_settings.Enabled || !_settings.WallpaperEnabled)
        {
            return;
        }

        if (_settings.WallpaperDesignerEnabled)
        {
            var now = DateTime.UtcNow;
            foreach (var view in _wallpaperLayerViews)
            {
                if (view.Settings.Source != WallpaperSource.FolderSlideshow || view.SlideshowFiles.Count <= 1)
                {
                    continue;
                }

                var interval = Math.Clamp(view.Settings.SlideshowIntervalSeconds, 2, 3600);
                if (view.NextAdvance == DateTime.MinValue)
                {
                    view.NextAdvance = now.AddSeconds(interval);
                    continue;
                }

                if (now >= view.NextAdvance)
                {
                    view.NextAdvance = now.AddSeconds(interval);
                    AdvanceLayerSlideshow(view);
                }
            }

            return;
        }

        if (_settings.WallpaperSource != WallpaperSource.FolderSlideshow)
        {
            return;
        }

        AdvanceSlideshow();
    }

    private void ReloadWallpaperImageIfNeeded()
    {
        if (_wallpaperLoadedSource == _settings.WallpaperSource && _wallpaperLoadedPath == _settings.WallpaperPath)
        {
            return;
        }

        _wallpaperLoadedSource = _settings.WallpaperSource;
        _wallpaperLoadedPath = _settings.WallpaperPath;
        switch (_settings.WallpaperSource)
        {
            case WallpaperSource.LocalImage:
                LoadWallpaperImage(_settings.WallpaperPath);
                break;
            case WallpaperSource.FolderSlideshow:
                BuildSlideshowList();
                if (_wallpaperSlideshow.Count > 0)
                {
                    _wallpaperSlideshowIndex = 0;
                    LoadWallpaperImage(_wallpaperSlideshow[0]);
                }
                break;
            case WallpaperSource.SmtcAlbum:
                // SMTC 底图由 SmtcWatcher 事件驱动推送，这里只需清空旧图。
                ClearWallpaperImage();
                break;
        }
    }

    private void BuildSlideshowList()
    {
        _wallpaperSlideshow.Clear();
        var directory = _settings.WallpaperPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
        _wallpaperSlideshow.AddRange(Directory.EnumerateFiles(directory)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    private void AdvanceSlideshow()
    {
        if (_wallpaperSlideshow.Count == 0)
        {
            return;
        }

        _wallpaperSlideshowIndex = (_wallpaperSlideshowIndex + 1) % _wallpaperSlideshow.Count;
        LoadWallpaperImage(_wallpaperSlideshow[_wallpaperSlideshowIndex]);
    }

    /// <summary>
    /// 清空当前底图（如 SMTC 切换到的媒体没有封面时）。
    /// </summary>
    private void ClearWallpaperImage()
    {
        _wallpaperTransitionActive = false;
        DisposeWallpaperBitmap();
        foreach (var layer in _wallpaperLayers)
        {
            layer.Background = null;
            layer.Opacity = 0;
        }
    }

    private void LoadWallpaperImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            DisposeWallpaperBitmap();
            return;
        }

        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));
            stream.Position = 0;
            var bitmap = new Bitmap(stream);
            SetWallpaperImage(bitmap, stream);
        }
        catch
        {
            DisposeWallpaperBitmap();
        }
    }

    private void LoadWallpaperImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        try
        {
            var stream = new MemoryStream(bytes);
            stream.Position = 0;
            var bitmap = new Bitmap(stream);
            SetWallpaperImage(bitmap, stream);
        }
        catch
        {
            DisposeWallpaperBitmap();
        }
    }

    private void SetWallpaperImage(Bitmap bitmap, MemoryStream stream)
    {
        var duration = Math.Max(0, _settings.AlbumColorTransitionSeconds);
        if (_wallpaperLayers.Count < 2 || duration <= 0)
        {
            // 立即切换：旧图不再被任何图层引用，可以安全释放。
            DisposeWallpaperBitmap();
            _wallpaperBitmap = bitmap;
            _wallpaperStream = stream;
            ApplyWallpaperLayer(_wallpaperFront, bitmap, _settings.WallpaperOpacity);
            return;
        }

        // 交叉淡化：旧图保留在前层（不可提前释放），新图放到背面从 0 淡入。
        var back = 1 - _wallpaperFront;
        ApplyWallpaperLayer(back, bitmap, 0);
        if (_wallpaperBitmap != null && _wallpaperBitmap != bitmap)
        {
            _wallpaperRetiredBitmap?.Dispose();
            _wallpaperRetiredStream?.Dispose();
            _wallpaperRetiredBitmap = _wallpaperBitmap;
            _wallpaperRetiredStream = _wallpaperStream;
        }

        _wallpaperBitmap = bitmap;
        _wallpaperStream = stream;
        _wallpaperTransitionStart = DateTime.UtcNow;
        _wallpaperTransitionActive = true;
        UpdateAnimationTimer();
    }

    private void ApplyWallpaperLayer(int index, Bitmap bitmap, double opacity)
    {
        if (_wallpaperLayers.Count <= index)
        {
            return;
        }

        var layer = _wallpaperLayers[index];
        layer.Background = BuildWallpaperBrush(bitmap);
        layer.Opacity = opacity;
    }

    private void AdvanceWallpaperTransition()
    {
        var duration = Math.Max(0.001, _settings.AlbumColorTransitionSeconds);
        var progress = Math.Clamp((DateTime.UtcNow - _wallpaperTransitionStart).TotalSeconds / duration, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var front = _wallpaperLayers[_wallpaperFront];
        var back = _wallpaperLayers[1 - _wallpaperFront];
        front.Opacity = _settings.WallpaperOpacity * (1 - eased);
        back.Opacity = _settings.WallpaperOpacity * eased;
        if (progress >= 1)
        {
            front.Opacity = 0;
            back.Opacity = _settings.WallpaperOpacity;
            front.Background = null;
            _wallpaperFront = 1 - _wallpaperFront;
            _wallpaperTransitionActive = false;
            _wallpaperRetiredBitmap?.Dispose();
            _wallpaperRetiredBitmap = null;
            _wallpaperRetiredStream?.Dispose();
            _wallpaperRetiredStream = null;
            UpdateAnimationTimer();
        }
    }

    private void UpdateWallpaperPresentation()
    {
        if (_wallpaperBitmap == null || _wallpaperLayers.Count < 2)
        {
            return;
        }

        var front = _wallpaperLayers[_wallpaperFront];
        front.Opacity = _settings.WallpaperOpacity;
        if (front.Background is ImageBrush brush)
        {
            brush.Source = _wallpaperBitmap;
            brush.Stretch = WallpaperStretch;
            brush.TileMode = _settings.WallpaperDisplayMode == WallpaperDisplayMode.Tile ? TileMode.Tile : TileMode.None;
            ApplyWallpaperBrushRegion(brush);
        }
        else
        {
            front.Background = BuildWallpaperBrush(_wallpaperBitmap);
        }
    }

    private Stretch WallpaperStretch => _settings.WallpaperDisplayMode switch
    {
        WallpaperDisplayMode.Stretch => Stretch.Fill,
        WallpaperDisplayMode.Fill => Stretch.UniformToFill,
        WallpaperDisplayMode.Fit => Stretch.Uniform,
        WallpaperDisplayMode.Tile => Stretch.None,
        _ => Stretch.UniformToFill
    };

    private ImageBrush BuildWallpaperBrush(Bitmap bitmap)
    {
        var brush = new ImageBrush
        {
            Source = bitmap,
            Stretch = WallpaperStretch,
            TileMode = _settings.WallpaperDisplayMode == WallpaperDisplayMode.Tile ? TileMode.Tile : TileMode.None,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        ApplyWallpaperBrushRegion(brush);
        return brush;
    }

    private void ApplyWallpaperBrushRegion(ImageBrush brush)
    {
        var scale = Math.Clamp(_settings.WallpaperScale, 1, 5);
        var ox = Math.Clamp(_settings.WallpaperOffsetX, -0.5, 0.5);
        var oy = Math.Clamp(_settings.WallpaperOffsetY, -0.5, 0.5);
        if (_settings.WallpaperDisplayMode == WallpaperDisplayMode.Tile)
        {
            brush.SourceRect = RelativeRect.Fill;
            brush.DestinationRect = new RelativeRect(0, 0, 1.0 / scale, 1.0 / scale, RelativeUnit.Relative);
            return;
        }

        var s = 1.0 / scale;
        var cx = 0.5 + ox * (0.5 - s / 2);
        var cy = 0.5 + oy * (0.5 - s / 2);
        brush.DestinationRect = RelativeRect.Fill;
        brush.SourceRect = new RelativeRect(
            Math.Clamp(cx - s / 2, 0, 1 - s),
            Math.Clamp(cy - s / 2, 0, 1 - s),
            s, s, RelativeUnit.Relative);
    }

    // ============ 主界面底图结束 ============

    /// <summary>
    /// 由 16ms 动画时钟驱动「即将上课」覆盖层：以当前时间按各自速度更新相位并重绘，
    /// 保证 60fps 平滑。OnStateTick 的 50ms 轮询只负责创建与参数同步，不再承担帧推进。
    /// </summary>
    private void AdvancePrepareOnClassOverlays()
    {
        if (_prepareOnClassOverlays.Count == 0 && _prepareWarningOverlay == null)
        {
            return;
        }

        var phase = DateTime.UtcNow.TimeOfDay.TotalSeconds;
        foreach (var (line, overlay) in _prepareOnClassOverlays.ToArray())
        {
            overlay.Phase = phase * overlay.Speed;
            // 进入主界面时淡入、离开时淡出。
            overlay.Opacity = overlay.FadeOpacity;
            overlay.InvalidateVisual();
            if (overlay.IsFadeComplete)
            {
                RemovePrepareOnClassOverlay(line);
            }
        }

        // 全屏红色警告覆盖层：与行级覆盖层同一时钟推进、淡入淡出、闪动由 Speed 驱动。
        if (_prepareWarningOverlay is { } warning)
        {
            warning.Phase = phase * warning.Speed;
            warning.Opacity = warning.FadeOpacity;
            warning.InvalidateVisual();
            if (warning.IsFadeComplete)
            {
                _marqueeWindow?.Host.Children.Remove(warning);
                _prepareWarningOverlay = null;
                _marqueeWindow?.HideWhenEmpty();
            }
        }

        // 每秒输出一次覆盖层渲染状态，避免刷屏。
        if (DateTime.UtcNow - _lastOverlayDebugLog > TimeSpan.FromSeconds(1))
        {
            _lastOverlayDebugLog = DateTime.UtcNow;
            var first = _prepareOnClassOverlays.Values.FirstOrDefault();
            DebugLog($"AdvancePrepareOnClassOverlays: 覆盖层数={_prepareOnClassOverlays.Count}, overlayOpacity={first?.Opacity}, overlayBounds={first?.Bounds.Width}x{first?.Bounds.Height}, hostCount={_prepareOnClassOverlayHosts.Count}");
        }
    }

    private void UpdatePrepareOnClassOverlay(Control line)
    {
        var style = _settings.PrepareOnClassStyle;
        if (style == PrepareOnClassStyle.None || !(IsPrepareOnClassCountdown(line) || IsPreviewingPrepareOnClass()))
        {
            // 离开即将上课状态：先淡出，淡出完成后由动画时钟移除。
            if (_prepareOnClassOverlays.TryGetValue(line, out var leaving) && !leaving.IsFadingOut)
            {
                leaving.BeginFadeOut();
            }
            return;
        }

        if (_prepareOnClassOverlays.TryGetValue(line, out var overlay) && !IsOverlayOfStyle(overlay, style))
        {
            // 样式类型变化时替换旧覆盖层。
            RemovePrepareOnClassOverlay(line);
            overlay = null;
        }

        if (overlay == null)
        {
            overlay = CreatePrepareOnClassOverlay(style);
            if (overlay == null)
            {
                return;
            }

            var overlayHost = line.GetVisualDescendants().OfType<Grid>()
                .FirstOrDefault(x => x.Name == HostContract.GridOverlay);
            if (overlayHost == null)
            {
                DebugLog($"UpdatePrepareOnClassOverlay: 未找到 GridOverlay（line={line.GetType().Name}）");
                return;
            }

            overlayHost.Children.Add(overlay);
            _prepareOnClassOverlays[line] = overlay;
            DebugLog($"UpdatePrepareOnClassOverlay: 已添加覆盖层 style={style}, hostOpacity={overlayHost.Opacity}");
            // 宿主模板里 GridOverlay 默认 Opacity=0（仅在宿主播放提醒时点亮）。
            // 预览时不播真实提醒，需强制点亮才能看到自绘覆盖层；移除覆盖层时还原。
            if (IsPreviewingPrepareOnClass() && overlayHost.Opacity == 0)
            {
                _prepareOnClassOverlayHosts[line] = overlayHost;
                overlayHost.Opacity = 1;
                DebugLog($"UpdatePrepareOnClassOverlay: 已强制点亮 GridOverlay");
            }
        }
        else if (overlay.IsFadingOut)
        {
            // 重新进入即将上课状态：取消淡出，重新淡入。
            overlay.CancelFadeOut();
        }

        ApplyPrepareOnClassOverlayParams(overlay, style);
        overlay.InvalidateVisual();
    }

    private static bool IsOverlayOfStyle(PrepareOnClassOverlay overlay, PrepareOnClassStyle style) => style switch
    {
        PrepareOnClassStyle.Arrows => overlay is CountdownArrowOverlay,
        PrepareOnClassStyle.PulseRing => overlay is CountdownPulseRingOverlay,
        PrepareOnClassStyle.Scanline => overlay is CountdownScanlineOverlay,
        PrepareOnClassStyle.LightBand => overlay is CountdownLightBandOverlay,
        _ => false
    };

    private static PrepareOnClassOverlay? CreatePrepareOnClassOverlay(PrepareOnClassStyle style) => style switch
    {
        PrepareOnClassStyle.Arrows => new CountdownArrowOverlay(),
        PrepareOnClassStyle.PulseRing => new CountdownPulseRingOverlay(),
        PrepareOnClassStyle.Scanline => new CountdownScanlineOverlay(),
        PrepareOnClassStyle.LightBand => new CountdownLightBandOverlay(),
        _ => null
    };

    private void ApplyPrepareOnClassOverlayParams(PrepareOnClassOverlay overlay, PrepareOnClassStyle style)
    {
        switch (overlay)
        {
            case CountdownArrowOverlay arrows:
                arrows.Speed = _settings.CountdownArrowSpeed;
                arrows.ArrowColor = TryParseColor(_settings.CountdownArrowColor, out var arrowColor) ? arrowColor : Colors.White;
                arrows.ArrowCount = _settings.CountdownArrowCount;
                arrows.ArrowsPerGroup = _settings.CountdownArrowPerGroup;
                arrows.ArrowSpacing = _settings.CountdownArrowSpacing;
                arrows.ArrowGroupSpacing = _settings.CountdownArrowGroupSpacing;
                arrows.ArrowThickness = _settings.CountdownArrowThickness;
                break;
            case CountdownPulseRingOverlay pulse:
                pulse.Speed = _settings.CountdownPulseSpeed;
                pulse.Color = TryParseColor(_settings.CountdownPulseColor, out var pulseColor) ? pulseColor : Colors.White;
                pulse.Thickness = _settings.CountdownPulseThickness;
                pulse.MaxRadius = _settings.CountdownPulseMaxRadius;
                break;
            case CountdownScanlineOverlay scan:
                scan.Speed = _settings.CountdownScanSpeed;
                scan.Color = TryParseColor(_settings.CountdownScanColor, out var scanColor) ? scanColor : Colors.White;
                scan.Thickness = _settings.CountdownScanThickness;
                scan.Direction = _settings.CountdownScanDirection;
                scan.TailEnabled = _settings.CountdownScanTailEnabled;
                break;
            case CountdownLightBandOverlay band:
                band.Speed = _settings.CountdownLightBandSpeed;
                band.Color = TryParseColor(_settings.CountdownLightBandColor, out var bandColor) ? bandColor : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
                band.Thickness = _settings.CountdownLightBandThickness;
                band.Angle = _settings.CountdownLightBandAngle;
                break;
        }
    }

    /// <summary>「预览即将上课」激活期间（5 秒）视为即将上课状态。</summary>
    private bool IsPreviewingPrepareOnClass() => DateTime.UtcNow < _prepareOnClassPreviewUntil;

    private static bool IsPrepareOnClassCountdown(Control line)
    {
        var request = GetCurrentNotificationRequestProperty(line.GetType())?.GetValue(line);
        if (request == null)
        {
            return false;
        }

        return GetChannelIdProperty(request.GetType())?.GetValue(request) is Guid channelId &&
               channelId == HostContract.PrepareOnClassChannelId;
    }

    private void RemovePrepareOnClassOverlay(Control line)
    {
        if (!_prepareOnClassOverlays.Remove(line, out var overlay))
        {
            return;
        }

        (overlay.Parent as Panel)?.Children.Remove(overlay);
        // 预览期间强制点亮的 GridOverlay：移除覆盖层后还原宿主默认不透明度。
        if (_prepareOnClassOverlayHosts.Remove(line, out var overlayHost))
        {
            overlayHost.Opacity = 0;
        }
    }

    private void RemoveAllPrepareOnClassOverlays()
    {
        foreach (var line in _prepareOnClassOverlays.Keys.ToArray())
        {
            RemovePrepareOnClassOverlay(line);
        }

        if (_prepareWarningOverlay is { } warning)
        {
            _marqueeWindow?.Host.Children.Remove(warning);
            _prepareWarningOverlay = null;
            _marqueeWindow?.HideWhenEmpty();
        }
    }

    /// <summary>
    /// 维护「即将上课 · 红色警告」全屏覆盖层的生命周期：距上课不足触发秒数
    /// （或预览期间）时创建并显示，离开后淡出并移除。由 OnStateTick 的 50ms 轮询驱动。
    /// </summary>
    private void UpdatePrepareWarningOverlay()
    {
        var shouldShow = _settings.PrepareWarningEnabled &&
                         (IsPreviewingPrepareOnClass() || IsWithinWarningWindow());
        if (!shouldShow)
        {
            if (_prepareWarningOverlay is { IsFadingOut: false } leaving)
            {
                leaving.BeginFadeOut();
            }

            return;
        }

        if (_prepareWarningOverlay == null)
        {
            if (_mainWindow == null)
            {
                return;
            }

            var overlay = new PrepareOnClassWarningOverlay
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var marqueeWindow = _marqueeWindow ??= new MarqueeOverlayWindow();
            var screen = _mainWindow.Screens.ScreenFromWindow(_mainWindow) ?? _mainWindow.Screens.Primary;
            marqueeWindow.ShowFullScreen(screen);
            marqueeWindow.Host.Children.Add(overlay);
            _prepareWarningOverlay = overlay;
            ApplyPrepareWarningParams(overlay);
        }
        else
        {
            _prepareWarningOverlay.CancelFadeOut();
            ApplyPrepareWarningParams(_prepareWarningOverlay);
        }
    }

    /// <summary>距上课剩余秒数是否已进入警告窗口（剩余 &gt; 0 且不超过触发阈值）。</summary>
    private bool IsWithinWarningWindow()
    {
        var left = TryGetOnClassLeftTime();
        return left is { } time && time > TimeSpan.Zero &&
               time.TotalSeconds <= _settings.PrepareWarningTriggerSeconds;
    }

    /// <summary>通过宿主公开服务读取距上课剩余时间（失败返回 null，不冒泡异常）。</summary>
    private static TimeSpan? TryGetOnClassLeftTime()
    {
        try
        {
            return IAppHost.TryGetService<ILessonsService>()?.OnClassLeftTime;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPrepareWarningParams(PrepareOnClassWarningOverlay overlay)
    {
        overlay.Speed = _settings.PrepareWarningFlashSpeed;
        overlay.FlashSpeed = _settings.PrepareWarningFlashSpeed;
        overlay.FlashAmount = _settings.PrepareWarningFlashAmount;
        overlay.FrameThickness = _settings.PrepareWarningFrameThickness;
        overlay.OpacityScale = _settings.PrepareWarningOpacity;
        overlay.Color = TryParseColor(_settings.PrepareWarningColor, out var color)
            ? color
            : Color.FromArgb(0x66, 0xFF, 0, 0);
    }

    private void ObserveLine(Control line)
    {
        if (_observedLines.Add(line))
        {
            line.PropertyChanged += LineOnPropertyChanged;
        }
    }

    private void LineOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_settings.Enabled || e.Property.Name != HostContract.MaskContentProperty || sender is not Control line)
        {
            return;
        }

        _lineMasks[line] = e.NewValue;
        if (e.NewValue != null)
        {
            TriggerEmphasis(e.NewValue);
        }
    }

    private void TriggerEmphasis(object mask)
    {
        _emphasisStartedAt = DateTime.UtcNow;
        CreateRipple();
        CreateMarquee();
        UpdateAnimationTimer();
    }

    private void ConfigureNativeRipplePlayer(Control line)
    {
        var field = GetEffectPlayerField(line.GetType());
        if (field == null)
        {
            return;
        }

        if (_settings.RippleType != RippleType.None)
        {
            if (_nativeEffectPlayers.ContainsKey(line))
            {
                return;
            }

            try
            {
                _nativeEffectPlayers[line] = field.GetValue(line);
                _suppressingEffectPlayer ??= CreateSuppressingEffectPlayer(field.FieldType);
                if (_suppressingEffectPlayer == null)
                {
                    _nativeEffectPlayers.Remove(line);
                    return;
                }
                field.SetValue(line, _suppressingEffectPlayer);
            }
            catch
            {
                _nativeEffectPlayers.Remove(line);
            }
            return;
        }

        RestoreNativeRipplePlayer(line, field);
    }

    private static object? CreateSuppressingEffectPlayer(Type interfaceType)
    {
        try
        {
            var createMethod = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(x => x.Name == nameof(DispatchProxy.Create) && x.IsGenericMethodDefinition &&
                             x.GetGenericArguments().Length == 2);
            return createMethod.MakeGenericMethod(interfaceType, typeof(SuppressingTopmostEffectPlayer))
                .Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }

    private void RestoreNativeRipplePlayers()
    {
        foreach (var (line, player) in _nativeEffectPlayers.ToArray())
        {
            var field = GetEffectPlayerField(line.GetType());
            RestoreNativeRipplePlayer(line, field, player);
        }
        _nativeEffectPlayers.Clear();
    }

    private void RestoreNativeRipplePlayer(Control line, FieldInfo? field, object? player = null)
    {
        if (!_nativeEffectPlayers.TryGetValue(line, out var capturedPlayer) && player == null)
        {
            return;
        }

        try
        {
            field?.SetValue(line, player ?? capturedPlayer);
        }
        catch
        {
            // The line may already be disposed while ClassIsland rebuilds its layout.
        }
        _nativeEffectPlayers.Remove(line);
    }

    /// <summary>
    /// 预览一次完整提醒：对每行注入临时遮罩内容，触发宿主遮罩过渡动画；
    /// 遮罩内容变化会经 <see cref="LineOnPropertyChanged"/> 同时触发强调动画与 Ripple。
    /// </summary>
    public void PreviewNotification()
    {
        if (_mainWindow == null)
        {
            Attach();
        }

        // 先跑一次状态轮询，确保 MainWindowLine 已被发现、原生 Ripple 播放器已被劫持，
        // 这样预览的 Ripple 才能进入全屏特效窗口。
        OnStateTick(null, EventArgs.Empty);
        // 首选：走宿主原生提醒系统推送一个真实提醒，完整播放宿主遮罩过渡 + 插件强调 + Ripple。
        if (TryPushNativeNotification())
        {
            return;
        }
        // 兜底：宿主提醒系统不可用时，退回旧的反射塞 MaskContent 路径。
        foreach (var line in GetMainWindowLines())
        {
            PlayPreviewMask(line);
        }
    }

    /// <summary>
    /// 通过宿主提醒系统推送一个真实提醒（原生提醒），让 MainWindowLine.ProcessNotification
    /// 完整播放遮罩进场/退场动画、置顶与 Ripple；插件自身经 LineOnPropertyChanged 同步触发
    /// 强调动画与自定义 Ripple。INotificationHostService.ShowNotification 在接口上声明为
    /// internal，此处反射调用具体实例上的公开方法。
    /// </summary>
    private bool TryPushNativeNotification()
    {
        try
        {
            var host = IAppHost.TryGetService<INotificationHostService>();
            if (host == null)
            {
                return false;
            }

            var content = new NotificationContent
            {
                // 预览只需演示特效（遮罩/强调/Ripple），不显示任何文本。
                Duration = TimeSpan.FromSeconds(1.2),
                Color = TryParseColor(_settings.RippleColor, out var rippleColor)
                    ? new SolidColorBrush(rippleColor)
                    : new SolidColorBrush(Colors.White)
            };
            var request = new NotificationRequest { MaskContent = content };
            var method = host.GetType().GetMethod("ShowNotification", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(NotificationRequest), typeof(Guid), typeof(Guid), typeof(bool), typeof(bool) }, null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(host, new object[] { request, Guid.Empty, Guid.Empty, true, false });
            return true;
        }
        catch
        {
            // 宿主提醒系统不可用（如全局提醒被关闭、接口变化）时回退旧路径。
            return false;
        }
    }

    /// <summary>「预览即将上课」：接下来 5 秒按即将上课状态显示所选样式。</summary>
    public void PreviewPrepareOnClass()
    {
        DebugLog($"PreviewPrepareOnClass 进入: mainWindow={_mainWindow != null}, islandRoot={_islandRoot != null}, enabled={_settings.Enabled}, style={_settings.PrepareOnClassStyle}, lines={GetMainWindowLines().Length}");
        if (_mainWindow == null)
        {
            Attach();
        }

        _prepareOnClassPreviewUntil = DateTime.UtcNow.AddSeconds(5);
        // 立即点亮所有行的 GridOverlay 并创建覆盖层；5 秒后 OnStateTick 的轮询会自动移除。
        UpdatePreviewOverlayHostVisibility();
        OnStateTick(null, EventArgs.Empty);
        UpdateAnimationTimer();
    }

    /// <summary>
    /// 预览期间宿主模板里的 Grid#GridOverlay 默认 Opacity=0（仅在宿主播放真实提醒时点亮）。
    /// 预览不播真实提醒，需对所有行强制点亮才能看到自绘覆盖层；预览结束且无覆盖层时还原。
    /// </summary>
    private void UpdatePreviewOverlayHostVisibility()
    {
        if (!IsPreviewingPrepareOnClass() || _settings.PrepareOnClassStyle == PrepareOnClassStyle.None)
        {
            return;
        }

        foreach (var line in GetMainWindowLines())
        {
            var host = line.GetVisualDescendants().OfType<Grid>()
                .FirstOrDefault(x => x.Name == HostContract.GridOverlay);
            if (host != null && host.Opacity == 0)
            {
                _prepareOnClassOverlayHosts[line] = host;
                host.Opacity = 1;
                DebugLog($"UpdatePreviewOverlayHostVisibility: 强制点亮 host");
            }
        }
    }

    /// <summary>预览结束且对应行没有覆盖层时，还原被强制点亮的 GridOverlay 不透明度。</summary>
    private void SyncPrepareOnClassOverlayHosts()
    {
        foreach (var (line, host) in _prepareOnClassOverlayHosts.ToArray())
        {
            if (!IsPreviewingPrepareOnClass() && !_prepareOnClassOverlays.ContainsKey(line))
            {
                _prepareOnClassOverlayHosts.Remove(line);
                host.Opacity = 0;
            }
        }
    }

    private Control[] GetMainWindowLines() =>
        _mainWindow?.GetVisualDescendants().OfType<Control>()
            .Where(x => x.GetType().FullName == HostContract.MainWindowLineTypeName)
            .ToArray() ?? [];

    private void PlayPreviewMask(Control line)
    {
        var maskProperty = GetMaskContentProperty(line.GetType());
        if (maskProperty == null || maskProperty.GetValue(line) != null)
        {
            return;
        }

        var content = new NotificationContent
        {
            // 预览只演示特效，不显示文本。
            Duration = TimeSpan.FromSeconds(1.2),
            Color = TryParseColor(_settings.RippleColor, out var rippleColor)
                ? new SolidColorBrush(rippleColor)
                : new SolidColorBrush(Colors.White)
        };
        // 设置遮罩内容会触发 LineOnPropertyChanged → 强调动画 + Ripple。
        maskProperty.SetValue(line, content);
        SetPseudoClass(line, HostContract.PseudoMaskIn, true);
        _ = ClearPreviewMaskAsync(line, maskProperty);
    }

    /// <summary>
    /// 反射设置宿主控件的伪类（StyledElement.PseudoClasses 对插件不可直接访问）。
    /// </summary>
    private static void SetPseudoClass(Control line, string name, bool value)
    {
        try
        {
            var property = line.GetType().GetProperty(HostContract.PseudoClassesProperty,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(line) is Classes classes)
            {
                classes.Set(name, value);
            }
        }
        catch
        {
            // 宿主版本变化时忽略伪类设置（仅影响预览动画完整性）。
        }
    }

    private static async Task ClearPreviewMaskAsync(Control line, PropertyInfo maskProperty)
    {
        try
        {
            await Task.Delay(1200);
            if (!line.IsAttachedToVisualTree())
            {
                return;
            }

            maskProperty.SetValue(line, null);
            SetPseudoClass(line, HostContract.PseudoMaskIn, false);
            SetPseudoClass(line, HostContract.PseudoMaskOut, true);
            await Task.Delay(300);
            if (line.IsAttachedToVisualTree())
            {
                SetPseudoClass(line, HostContract.PseudoMaskOut, false);
            }
        }
        catch
        {
            // 预览期间宿主重建布局等异常一律忽略。
        }
    }

    private void CreateRipple()
    {
        if (_settings.RippleType == RippleType.None || _windowRoot == null || _islandRoot == null)
        {
            return;
        }

        var isHanabi = _settings.RippleType == RippleType.Hanabi;
        // 使用自带配色的类型不读取用户颜色设置。
        var ignoresColor = _settings.RippleType is RippleType.Hanabi or RippleType.Explode or RippleType.Cinematic;
        var color = Colors.White;
        if (!ignoresColor && !TryParseColor(_settings.RippleColor, out color))
        {
            return;
        }

        var effectControls = TryGetFullScreenEffectHost(out var effectWindow);
        // 花火/爆炸/屏幕涟漪比主界面大得多，必须进全屏特效窗口，否则早期启动会被裁切。
        if (_settings.RippleType is RippleType.Hanabi or RippleType.Explode or RippleType.Cinematic &&
            effectControls == null)
        {
            return;
        }
        var center = GetRippleCenter(effectWindow);
        // 所有类型的 Ripple 都支持圆形约束扩散；半径 0 时按主界面大小自动计算。
        double? clipRadius = _settings.RippleConstraintEnabled
            ? (_settings.RippleConstraintRadius > 0 ? _settings.RippleConstraintRadius : GetAutomaticConstraintRadius())
            : null;
        if (_settings.RippleType == RippleType.Explode)
        {
            // 爆炸：在 Ripple 中心播放一次 explode.gif（由 16ms 时钟推进、播完自动移除）。
            // 原图仅 310x310，按原生尺寸渲染并限制不超过主界面，避免过大。
            var islandMax = Math.Max(_islandRoot.Bounds.Width, _islandRoot.Bounds.Height);
            var size = islandMax > 0 ? Math.Min(310d, islandMax) : 310d;
            var explosion = new ExplosionOverlay(center, size, _settings.RippleOpacity)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            if (effectControls != null)
            {
                effectControls.Add(explosion);
                _rippleHosts[explosion] = effectControls;
            }
            else
            {
                _windowRoot.Children.Add(explosion);
            }

            _ripples.Add(explosion);
            return;
        }

        if (_settings.RippleType == RippleType.Cinematic)
        {
            // 屏幕涟漪：抓取当前全屏画面（含任务栏与其它窗口），叠加晃动/涟漪/闪光/模糊的电影感特效。
            var frame = CaptureFullScreen();
            if (frame == null)
            {
                return;
            }

            var cinematic = new CinematicRippleOverlay(frame,
                TimeSpan.FromSeconds(_settings.RippleDurationSeconds),
                _settings.RippleOpacity,
                _settings.CinematicShakeAmount,
                _settings.CinematicBlurRadius,
                _settings.CinematicFlashAmount)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            if (effectControls != null)
            {
                effectControls.Add(cinematic);
                _rippleHosts[cinematic] = effectControls;
            }
            else
            {
                _windowRoot.Children.Add(cinematic);
            }

            _ripples.Add(cinematic);
            return;
        }

        var ripple = new IslandRippleOverlay(center, _settings.RippleType,
            isHanabi ? Colors.White : color,
            TimeSpan.FromSeconds(_settings.RippleDurationSeconds),
            isHanabi ? 2.5 : _settings.RippleThickness,
            clipRadius,
            _settings.RippleOpacity);
        ripple.HorizontalAlignment = HorizontalAlignment.Stretch;
        ripple.VerticalAlignment = VerticalAlignment.Stretch;

        if (effectControls != null)
        {
            // TopmostEffectWindow owns a borderless, monitor-sized window. Adding
            // directly to its EffectControls makes the ripple genuinely full-screen
            // and also lets its collection change handler show the effect window.
            effectControls.Add(ripple);
            _rippleHosts[ripple] = effectControls;
        }
        else
        {
            // A safe fallback for early startup, before ClassIsland has created its
            // topmost effect window. This path retains the previous behavior.
            _windowRoot.Children.Add(ripple);
        }
        _ripples.Add(ripple);
    }

    /// <summary>
    /// 全屏流光（跑马灯）覆盖层：仿 Gemini 等语音助手激活时的全屏内发光效果。
    /// 独立于 <see cref="RippleType"/>，可与任意 Ripple 类型叠加播放。
    /// 渲染在专用全屏覆盖窗口里（覆盖任务栏区域），由 16ms 时钟推进，播完自动移除。
    /// </summary>
    private void CreateMarquee()
    {
        if (!_settings.MarqueeEnabled || _mainWindow == null)
        {
            return;
        }

        if (!TryParseColor(_settings.MarqueeColor, out var color))
        {
            return;
        }

        var marqueeWindow = _marqueeWindow ??= new MarqueeOverlayWindow();
        var marquee = new MarqueeOverlay(
            _settings.MarqueeDurationSeconds,
            _settings.MarqueeSpeed,
            _settings.MarqueeOpacity,
            _settings.MarqueeFrameThickness,
            color)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // 全屏覆盖到任务栏之下（覆盖屏幕底边），并置顶压过任务栏。
        var screen = _mainWindow.Screens.ScreenFromWindow(_mainWindow) ?? _mainWindow.Screens.Primary;
        marqueeWindow.ShowFullScreen(screen);
        marqueeWindow.Host.Children.Add(marquee);
        _rippleHosts[marquee] = marqueeWindow.Host.Children;
        _ripples.Add(marquee);
    }

    private IList? TryGetFullScreenEffectHost(out Window? effectWindow)
    {
        effectWindow = null;
        foreach (var player in _nativeEffectPlayers.Values)
        {
            if (TryGetEffectControls(player, out effectWindow) is { } controls)
            {
                return controls;
            }
        }

        // The public MainWindow property gives us a reliable path before the
        // per-line player has been observed, avoiding the island-sized fallback
        // window that used to crop the Hanabi centre ball.
        var topmostEffectWindow = _mainWindow?.GetType()
            .GetProperty(HostContract.TopmostEffectWindowProperty, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(_mainWindow);
        if (TryGetEffectControls(topmostEffectWindow, out effectWindow) is { } controlsFromMainWindow)
        {
            return controlsFromMainWindow;
        }

        return null;
    }

    private static IList? TryGetEffectControls(object? player, out Window? effectWindow)
    {
        effectWindow = player as Window;
        if (effectWindow == null)
        {
            return null;
        }

        var viewModel = player!.GetType().GetProperty(HostContract.ViewModelProperty, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(player);
        if (viewModel?.GetType().GetProperty(HostContract.EffectControlsProperty, BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(viewModel) is IList controls)
        {
            return controls;
        }

        effectWindow = null;
        return null;
    }

    private Point GetRippleCenter(Window? effectWindow)
    {
        var islandRoot = _islandRoot;
        var mainWindow = _mainWindow;
        var windowRoot = _windowRoot;
        if (islandRoot == null || mainWindow == null || windowRoot == null)
        {
            return new Point();
        }

        var islandCenterInMainWindow = islandRoot.TranslatePoint(
            new Point(islandRoot.Bounds.Width / 2, islandRoot.Bounds.Height / 2), mainWindow) ??
            new Point(mainWindow.Bounds.Width / 2, mainWindow.Bounds.Height / 2);

        if (effectWindow != null)
        {
            try
            {
                return effectWindow.PointToClient(mainWindow.PointToScreen(islandCenterInMainWindow));
            }
            catch
            {
                // The effect window can be recreating while monitor topology changes.
            }

            return new Point(effectWindow.Bounds.Width / 2, effectWindow.Bounds.Height / 2);
        }

        return islandRoot.TranslatePoint(
            new Point(islandRoot.Bounds.Width / 2, islandRoot.Bounds.Height / 2), windowRoot) ??
            new Point(windowRoot.Bounds.Width / 2, windowRoot.Bounds.Height / 2);
    }

    /// <summary>
    /// 检测宿主是否开启了「分体主界面」（反射读宿主 Settings.IsIslandSeperated，注意宿主拼写）。
    /// 分体模式下本插件的背景/边框/圆角/底图注入对独立组件基本失效，设置页据此提示用户。
    /// </summary>
    public static bool IsSeparatedMode()
    {
        try
        {
            var app = AppBase.Current;
            var appType = app?.GetType();
            var settings = appType?.GetProperty(HostContract.SettingsProperty, BindingFlags.Instance | BindingFlags.Public)?.GetValue(app);
            var separated = settings?.GetType()
                .GetProperty(HostContract.IsIslandSeperatedProperty, BindingFlags.Instance | BindingFlags.Public)?.GetValue(settings);
            return separated is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检测宿主是否处于「多行主界面」模式（主界面包含多行 MainWindowLine）。
    /// 多行模式下本插件仅极少数功能无法生效，插件整体仍可继续正常运行，设置页据此提示用户。
    /// </summary>
    public static bool IsMultiLineMode()
    {
        try
        {
            var mainWindow = AppBase.Current.MainWindow;
            if (mainWindow == null)
            {
                return false;
            }

            var lineCount = mainWindow.GetVisualDescendants()
                .OfType<Control>()
                .Count(x => x.GetType().FullName == HostContract.MainWindowLineTypeName);
            return lineCount > 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 抓取当前全屏画面（物理像素，含任务栏与其它窗口），供「屏幕涟漪」特效使用。
    /// 用 System.Drawing.Graphics.CopyFromScreen 抓取主窗口所在显示器；
    /// 失败时回退抓取主窗口内容；仍失败则返回 null（放弃本次特效）。
    /// </summary>
    private Bitmap? CaptureFullScreen()
    {
        try
        {
            var window = _mainWindow;
            var screen = window?.Screens.ScreenFromWindow(window) ?? window?.Screens.Primary;
            if (screen == null)
            {
                return null;
            }

            // Screen.Bounds 为物理像素且含任务栏区域（虚拟桌面坐标系）。
            var bounds = screen.Bounds;
            using var source = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(source))
            {
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, source.Size);
            }

            // 转成 Avalonia 位图（PNG 中转，与 GifFrameLoader 同款做法）。
            using var stream = new MemoryStream();
            source.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch
        {
            return CaptureMainWindowFrame();
        }
    }

    /// <summary>
    /// 抓取主窗口当前渲染帧（含主界面全部内容），作为全屏抓屏失败时的兜底。
    /// 失败时再回退抓取主界面根节点；仍失败则返回 null。
    /// </summary>
    private Bitmap? CaptureMainWindowFrame()
    {
        try
        {
            var window = _mainWindow;
            if (window == null)
            {
                return null;
            }

            var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
            var width = Math.Max(1, (int)(window.Bounds.Width * scaling));
            var height = Math.Max(1, (int)(window.Bounds.Height * scaling));
            var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(window);
            return bitmap;
        }
        catch
        {
            try
            {
                if (_islandRoot == null)
                {
                    return null;
                }

                var width = Math.Max(1, (int)_islandRoot.Bounds.Width);
                var height = Math.Max(1, (int)_islandRoot.Bounds.Height);
                var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                bitmap.Render(_islandRoot);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 自动约束半径：包含主界面并留出舒适的扩散余量，同时确保全屏特效窗口里的
    /// Ripple 不会扩散到整块桌面。
    /// </summary>
    private double GetAutomaticConstraintRadius()
    {
        var root = _islandRoot;
        if (root == null)
        {
            return 220;
        }

        return Math.Clamp(Math.Max(root.Bounds.Width, root.Bounds.Height) * 1.4, 180, 560);
    }

    private void AdvanceRipples()
    {
        foreach (var ripple in _ripples.ToArray())
        {
            ripple.Advance();
            if (!ripple.IsCompleted)
            {
                continue;
            }

            RemoveRipple(ripple);
            _ripples.Remove(ripple);
        }
    }

    private void RemoveRipple(IRippleEffect ripple)
    {
        if (ripple is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_rippleHosts.Remove(ripple, out var host))
        {
            host.Remove(ripple);
            // 流光专用窗口的覆盖层全部移除后隐藏窗口。
            if (_marqueeWindow != null && ReferenceEquals(host, _marqueeWindow.Host.Children))
            {
                _marqueeWindow.HideWhenEmpty();
            }

            return;
        }

        if (ripple is Control control)
        {
            _windowRoot?.Children.Remove(control);
        }
    }

    // ============ 圆角绑定到 ClassIsland 原生设置 ============

    /// <summary>
    /// 反射获取宿主 App 的 Settings 对象（宿主主程序集插件无法直接引用，故用反射）。
    /// 缓存类型与属性信息，避免重复反射。
    /// </summary>
    private object? GetHostSettings()
    {
        try
        {
            var app = AppBase.Current;
            if (app == null)
            {
                return null;
            }

            var appType = app.GetType();
            if (_hostSettingsType != appType || _hostSettingsProperty == null)
            {
                _hostSettingsType = appType;
                _hostSettingsProperty = appType.GetProperty(HostContract.SettingsProperty, BindingFlags.Instance | BindingFlags.Public);
            }

            return _hostSettingsProperty?.GetValue(app);
        }
        catch
        {
            return null;
        }
    }

    private static double ReadHostRadius(object settings, string name)
    {
        try
        {
            return settings.GetType().GetProperty(name)?.GetValue(settings) is double d ? d : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteHostRadius(object settings, string name, double value)
    {
        try
        {
            settings.GetType().GetProperty(name)?.SetValue(settings, value);
        }
        catch
        {
            // 忽略：宿主结构变化时圆角回退为不接管。
        }
    }

    /// <summary>在接管前记录宿主原生圆角，用于禁用/卸载时还原。</summary>
    private void CaptureHostShape()
    {
        var settings = GetHostSettings();
        if (settings == null)
        {
            _hostShapeCaptured = false;
            return;
        }

        _hostShapeCaptured = true;
        _originalHostRadiusX = ReadHostRadius(settings, HostContract.RadiusXProperty);
        _originalHostRadiusY = ReadHostRadius(settings, HostContract.RadiusYProperty);
        _effectiveCornerRadius = _originalHostRadiusX;
    }

    /// <summary>
    /// 把插件圆角写入宿主原生 Settings.RadiusX/RadiusY，使宿主的背景样式、
    /// 内容 Clip（ContentClipBorder）与遮罩全部统一到同一圆角，修复
    /// “插件圆角不工作 / 裁切不一致”的问题。
    /// 宿主圆角安全上限为 20（默认行高 40 的一半），与宿主外观设置一致；
    /// 超过该值会让宿主 RectangleGeometry 内容裁切几何异常。
    /// </summary>
    private void ApplyShapeToHost()
    {
        var radius = _settings.Shape switch
        {
            IslandShape.Rectangle => 0.0,
            IslandShape.Capsule => 20.0, // 半圆
            IslandShape.HostDefault => -1.0, // 不接管，沿用宿主原生圆角
            _ => Math.Clamp(_settings.CornerRadius, 0, 20)
        };

        var settings = GetHostSettings();
        if (settings == null)
        {
            // 宿主访问失败时的降级：仍按形状给出合理圆角（不写宿主）。
            _effectiveCornerRadius = radius < 0 ? 0 : radius;
            return;
        }

        if (radius < 0)
        {
            _effectiveCornerRadius = ReadHostRadius(settings, HostContract.RadiusXProperty);
            return;
        }

        WriteHostRadius(settings, HostContract.RadiusXProperty, radius);
        WriteHostRadius(settings, HostContract.RadiusYProperty, radius);
        _effectiveCornerRadius = radius;
    }

    /// <summary>禁用/卸载时把宿主原生圆角还原为插件接管前的值。</summary>
    private void RestoreHostShape()
    {
        if (!_hostShapeCaptured)
        {
            return;
        }

        var settings = GetHostSettings();
        if (settings != null)
        {
            WriteHostRadius(settings, "RadiusX", _originalHostRadiusX);
            WriteHostRadius(settings, "RadiusY", _originalHostRadiusY);
            _effectiveCornerRadius = _originalHostRadiusX;
        }

        _hostShapeCaptured = false;
    }

    private void ApplyDecorations()
    {
        RestoreDecorations();
        _decorations.Clear();
        _shadowEffect = null;
        if (_mainWindow == null)
        {
            return;
        }

        EnsureDynamicColorsInitialized();
        var background = _settings.DynamicBackgroundColorEnabled
            ? _dynamicBackgroundColor
            : ParseColorOrDefault(_settings.BackgroundColor, _dynamicBackgroundColor);
        var border = _settings.DynamicBorderColorEnabled
            ? _dynamicBorderColor
            : ParseColorOrDefault(_settings.BorderColor, _dynamicBorderColor);
        var shadow = _settings.DynamicShadowColorEnabled
            ? _dynamicShadowColor
            : ParseColorOrDefault(_settings.ShadowColor, _dynamicShadowColor);
        // 全屏底图模式：删除底色、边框与阴影，让全屏图片完全接管背景。
        var fullscreenActive = HasFullscreenLayer();

        foreach (var borderControl in _mainWindow.GetVisualDescendants().OfType<Border>()
                     .Where(x => x.Name == HostContract.BackgroundBorder ||
                                 x.Name == HostContract.BackgroundBorderOverlayMask ||
                                 x.Name == HostContract.OverlayMask))
        {
            var originalCornerRadius = borderControl.CornerRadius;
            var originalBackground = borderControl.Background;
            var originalBorderBrush = borderControl.BorderBrush;
            var originalBorderThickness = borderControl.BorderThickness;
            _decorationRestorers.Add(() =>
            {
                borderControl.CornerRadius = originalCornerRadius;
                borderControl.Background = originalBackground;
                borderControl.BorderBrush = originalBorderBrush;
                borderControl.BorderThickness = originalBorderThickness;
            });

            // 圆角不再直接修改宿主 Border（会与宿主 Settings.RadiusX 驱动的内容 Clip
            // 裁切不一致）。统一由 ApplyShapeToHost() 写入宿主原生 RadiusX/RadiusY，
            // 让背景样式、内容裁切与遮罩全部同步到同一圆角。

            IBrush? backgroundBrush = null;
            if (borderControl.Name == "BackgroundBorder")
            {
                if (fullscreenActive)
                {
                    borderControl.Background = Brushes.Transparent;
                }
                else if (_settings.CustomBackgroundEnabled)
                {
                    backgroundBrush = _settings.GradientEnabled && TryParseColor(_settings.GradientEndColor, out var endColor)
                        ? BuildGradientBrush(background, endColor)
                        : new SolidColorBrush(background);
                    borderControl.Background = backgroundBrush;
                }
            }

            IBrush? borderBrush = null;
            if (fullscreenActive)
            {
                borderControl.BorderBrush = Brushes.Transparent;
                borderControl.BorderThickness = new Thickness(0);
            }
            else if (_settings.BorderEnabled)
            {
                borderBrush = new SolidColorBrush(border);
                borderControl.BorderBrush = borderBrush;
                borderControl.BorderThickness = new Thickness(_settings.BorderThickness);
            }

            _decorations.Add((borderControl, backgroundBrush, borderBrush));
        }

        if (!_settings.ShadowEnabled || fullscreenActive)
        {
            return;
        }

        foreach (var grid in _mainWindow.GetVisualDescendants().OfType<Grid>()
                     .Where(x => x.Name == HostContract.GridRoot && x.FindAncestorOfType<Control>()?.GetType().FullName == HostContract.MainWindowLineTypeName))
        {
            var originalEffect = grid.Effect;
            _decorationRestorers.Add(() => grid.Effect = originalEffect);
            _shadowEffect = new DropShadowEffect
            {
                Color = shadow,
                BlurRadius = _settings.ShadowBlur,
                OffsetX = _settings.ShadowOffsetX,
                OffsetY = _settings.ShadowOffsetY,
                Opacity = _settings.ShadowOpacity
            };
            grid.Effect = _shadowEffect;
        }

        UpdateWallpaperClip();
    }

    private void RestoreDecorations()
    {
        foreach (var restore in _decorationRestorers)
        {
            restore();
        }
        _decorationRestorers.Clear();
    }

    /// <summary>按用户配置的渐变方向构建线性渐变画刷。</summary>
    private LinearGradientBrush BuildGradientBrush(Color start, Color end)
    {
        var (startPoint, endPoint) = GradientGeometry.Points(_settings.GradientDirection);
        return new LinearGradientBrush
        {
            StartPoint = startPoint,
            EndPoint = endPoint,
            GradientStops = [new GradientStop(start, 0), new GradientStop(end, 1)]
        };
    }

    private static bool TryParseColor(string text, out Color color)
    {
        return Color.TryParse(text, out color);
    }

    // ============ 宿主反射元数据缓存 ============

    /// <summary>对照表切换后清空宿主反射元数据缓存，使新的成员名立即生效。</summary>
    public static void ClearReflectionCaches()
    {
        EffectPlayerFieldCache.Clear();
        MaskContentPropertyCache.Clear();
        CurrentNotificationRequestPropertyCache.Clear();
        ChannelIdPropertyCache.Clear();
    }

    private static FieldInfo? GetEffectPlayerField(Type type) =>
        EffectPlayerFieldCache.GetOrAdd(type, static t => t.GetField(
            HostContract.TopmostEffectWindowBackingField, BindingFlags.Instance | BindingFlags.NonPublic));

    private static PropertyInfo? GetMaskContentProperty(Type type) =>
        MaskContentPropertyCache.GetOrAdd(type, static t => t.GetProperty(
            HostContract.MaskContentProperty, BindingFlags.Instance | BindingFlags.Public));

    private static PropertyInfo? GetCurrentNotificationRequestProperty(Type type) =>
        CurrentNotificationRequestPropertyCache.GetOrAdd(type, static t => t.GetProperty(
            HostContract.CurrentNotificationRequestProperty, BindingFlags.Instance | BindingFlags.Public));

    private static PropertyInfo? GetChannelIdProperty(Type type) =>
        ChannelIdPropertyCache.GetOrAdd(type, static t => t.GetProperty(
            HostContract.ChannelIdProperty, BindingFlags.Instance | BindingFlags.Public));

    private void ConfigureStyleSheetWatcher()
    {
        _styleSheetWatcher?.Dispose();
        _styleSheetWatcher = null;

        if (!_settings.Enabled || !_settings.WatchStyleSheet || string.IsNullOrWhiteSpace(_settings.StyleSheetPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(_settings.StyleSheetPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null || !Directory.Exists(directory))
        {
            return;
        }

        _styleSheetWatcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _styleSheetWatcher.Changed += OnStyleSheetChanged;
        _styleSheetWatcher.Created += OnStyleSheetChanged;
        _styleSheetWatcher.Renamed += OnStyleSheetChanged;
    }

    private void OnStyleSheetChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(ReloadStyleSheet, DispatcherPriority.Background);
    }

    private Styles StyleHost => _styleHost?.Styles ?? _mainWindow!.Styles;

    private void RestoreHostState()
    {
        _animationTimer.Stop();
        _stateTimer.Stop();
        _styleSheetWatcher?.Dispose();
        _styleSheetWatcher = null;
        RestoreDecorations();
        _decorations.Clear();
        _shadowEffect = null;
        RestoreHostShape();
        _colorTransitionActive = false;
        _dynamicColorsInitialized = false;
        RemoveWallpaper();
        DisposeFullscreenHost();
        StopSpectrum();
        RevertDynamicThemeColor();
        RestoreMouseHoverKeepVisible();
        DetachClickHandler();
        DisableFakeWeather();
        RemoveTextureHost();
        RemoveAllPrepareOnClassOverlays();
        _lineMasks.Clear();
        foreach (var line in _observedLines)
        {
            line.PropertyChanged -= LineOnPropertyChanged;
        }
        _observedLines.Clear();
        RestoreNativeRipplePlayers();

        if (_mainWindow != null && _loadedStyles != null)
        {
            StyleHost.Remove(_loadedStyles);
            _loadedStyles = null;
        }

        if (_mainWindow != null && _notificationStyles != null)
        {
            StyleHost.Remove(_notificationStyles);
            _notificationStyles = null;
        }

        if (_mainWindow != null && _carouselStyles != null)
        {
            StyleHost.Remove(_carouselStyles);
            _carouselStyles = null;
        }

        foreach (var ripple in _ripples.ToArray())
        {
            RemoveRipple(ripple);
        }
        _ripples.Clear();
        _rippleHosts.Clear();

        if (_islandRoot != null)
        {
            _islandRoot.RenderTransform = _originalTransform;
            _islandRoot.Opacity = _originalOpacity;
            _islandRoot.Classes.Remove(HostContract.InjectorRootClass);
        }

        _mainWindow?.Classes.Remove(HostContract.InjectorWindowClass);
        _windowRoot = null;
        _styleHost = null;
    }

    public void Dispose()
    {
        _marqueeWindow?.Close();
        _marqueeWindow = null;
        RestoreHostState();
        _spectrumCapture?.Dispose();
        _spectrumCapture = null;
    }
}
