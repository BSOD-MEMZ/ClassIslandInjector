using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using ClassIsland.Core.Models.Notification;
using System.Collections;
using System.Collections.Concurrent;
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
    private readonly Dictionary<Control, (double Width, double Height)> _originalDisplaySizes = [];
    private Styles? _loadedStyles;
    private Styles? _notificationStyles;
    private FileSystemWatcher? _styleSheetWatcher;
    private Grid? _windowRoot;
    private readonly List<Action> _decorationRestorers = [];
    private readonly Dictionary<Control, object?> _lineMasks = [];
    private readonly HashSet<Control> _observedLines = [];
    private readonly Dictionary<Control, object?> _nativeEffectPlayers = [];
    private object? _suppressingEffectPlayer;
    private readonly List<IslandRippleOverlay> _ripples = [];
    private readonly Dictionary<Control, PrepareOnClassOverlay> _prepareOnClassOverlays = [];
    /// <summary>「预览即将上课」的激活截止时间（5 秒）。</summary>
    private DateTime _prepareOnClassPreviewUntil = DateTime.MinValue;
    // A custom ripple normally lives in ClassIsland's full-screen topmost effect
    // window.  This map lets us remove it from the same host when it completes.
    private readonly Dictionary<IslandRippleOverlay, IList> _rippleHosts = [];
    private DateTime _visibilityStartedAt = DateTime.MinValue;
    private DateTime _emphasisStartedAt = DateTime.MinValue;
    private bool _lastContentVisible;
    private bool _dynamicColorsInitialized;
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
    private Border? _textureHost;
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
            _originalDisplaySizes.Clear();
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
            // WorkingRoot is the actual client surface in ClassIsland. The
            // previous implementation only sized descendants, which were then
            // measured back to the host's full client rectangle by this parent.
            CaptureDisplaySize(mainWindow.FindControl<Control>(HostContract.WorkingRoot));
            CaptureDisplaySize(_islandRoot);
            CaptureDisplaySize(mainWindow.FindControl<Control>(HostContract.RootLayoutTransformControl));
            CaptureDisplaySize(mainWindow.FindControl<Control>(HostContract.GridRoot));
            mainWindow.Classes.Add(HostContract.InjectorWindowClass);
            _islandRoot.Classes.Add(HostContract.InjectorRootClass);
        }

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
        ApplySize();
        ApplyTransform(0);
        ApplyDecorations();
        ApplyWallpaper();
        ApplyTextureHost();
        ReloadStyleSheet();
        ReloadNotificationTransitionStyles();
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

        var scale = _settings.Scale;
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
        var hasContinuousAnimation = _settings.AnimationEnabled && _settings.AnimationMode != IslandAnimationMode.None;
        var hasTransientAnimation = GetEffectProgress(_visibilityStartedAt, _settings.VisibilityDurationSeconds) < 1 ||
                                   GetEffectProgress(_emphasisStartedAt, _settings.EmphasisDurationSeconds) < 1 ||
                                   _ripples.Count > 0 || _prepareOnClassOverlays.Count > 0 ||
                                   _colorTransitionActive || _wallpaperTransitionActive;
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
            return;
        }

        // 一次全树遍历同时服务底图边界与 MainWindowLine 发现，避免每 tick 遍历两次。
        var descendants = _mainWindow.GetVisualDescendants().OfType<Control>().ToArray();
        if (_wallpaperHost != null)
        {
            UpdateWallpaperBounds(descendants);
        }

        if (_textureHost != null)
        {
            UpdateTextureBounds(descendants);
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
        UpdateAnimationTimer();
    }

    private void ApplySize()
    {
        if (_islandRoot == null)
        {
            return;
        }

        foreach (var (control, originalSize) in _originalDisplaySizes)
        {
            control.Width = _settings.CustomSizeEnabled ? _settings.MainWindowWidth : originalSize.Width;
            control.Height = _settings.CustomSizeEnabled ? _settings.MainWindowHeight : originalSize.Height;
        }
    }

    private void CaptureDisplaySize(Control? control)
    {
        if (control != null)
        {
            _originalDisplaySizes.TryAdd(control, (control.Width, control.Height));
        }
    }

    private bool IsAnyDynamicColorEnabled() =>
        (_settings.CustomBackgroundEnabled && _settings.DynamicBackgroundColorEnabled) ||
        (_settings.BorderEnabled && _settings.DynamicBorderColorEnabled) ||
        (_settings.ShadowEnabled && _settings.DynamicShadowColorEnabled);

    /// <summary>
    /// 由 <see cref="SmtcWatcher"/> 事件驱动调用（已调度到 UI 线程）。
    /// 媒体变化时应用动态取色与 SMTC 底图；暂停/停止时（若启用）恢复原始颜色。
    /// </summary>
    public void OnSmtcMediaChanged(AlbumAccentColors? colors, byte[]? thumbnailBytes, bool isPlaying)
    {
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

        if (_settings.WallpaperEnabled && _settings.WallpaperSource == WallpaperSource.SmtcAlbum)
        {
            if (isPlaying && thumbnailBytes is { Length: > 0 })
            {
                LoadWallpaperImage(thumbnailBytes);
            }
            else if (thumbnailBytes is not { Length: > 0 })
            {
                ClearWallpaperImage();
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

        var enabled = _settings.Enabled && _settings.WallpaperEnabled && _settings.WallpaperSource != WallpaperSource.None;
        if (!enabled)
        {
            RemoveWallpaper();
            return;
        }

        EnsureWallpaperHost();
        UpdateWallpaperTimer();
        ReloadWallpaperImageIfNeeded();
        UpdateWallpaperPresentation();
    }

    private void EnsureWallpaperHost()
    {
        if (_wallpaperHost != null)
        {
            return;
        }

        var islandGrid = _mainWindow?.FindControl<Grid>(HostContract.GridRoot);
        if (islandGrid == null)
        {
            return;
        }

        _wallpaperLayers.Clear();
        _wallpaperLayers.Add(new Border { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch });
        _wallpaperLayers.Add(new Border { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch });
        _wallpaperHost = new Border
        {
            IsHitTestVisible = false,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new Grid { IsHitTestVisible = false, Children = { _wallpaperLayers[0], _wallpaperLayers[1] } }
        };
        _wallpaperHost.SizeChanged += (_, _) => UpdateWallpaperClip();
        islandGrid.SizeChanged += (_, _) => UpdateWallpaperBounds();
        islandGrid.Children.Insert(0, _wallpaperHost);
        UpdateWallpaperClip();
        UpdateWallpaperBounds();
    }

    /// <summary>
    /// 把底图约束到岛屿的实际可见边界内（各行的 BackgroundBorder 并集），
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
    }

    private void UpdateTextureBounds(IEnumerable<Control>? descendants = null)
    {
        if (_textureHost == null)
        {
            return;
        }

        var controls = descendants ?? _mainWindow?.GetVisualDescendants().OfType<Control>();
        if (controls != null)
        {
            ApplyOverlayHostBounds(_textureHost, controls);
        }

        UpdateTextureClip();
    }

    /// <summary>
    /// 把覆盖层宿主（底图 / 纹理）约束到主界面各行的 BackgroundBorder 并集边界内。
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

        host.CornerRadius = _settings.Shape switch
        {
            IslandShape.Rectangle => new CornerRadius(0),
            IslandShape.Capsule => new CornerRadius(Math.Max(1, host.Bounds.Height / 2)),
            IslandShape.HostDefault => new CornerRadius(0),
            _ => new CornerRadius(_settings.CornerRadius)
        };
    }

    private void UpdateWallpaperClip() => ApplyOverlayClip(_wallpaperHost);

    private void UpdateTextureClip() => ApplyOverlayClip(_textureHost);

    private void RemoveWallpaper()
    {
        _wallpaperTimer.Stop();
        _wallpaperTransitionActive = false;
        if (_wallpaperHost != null && _wallpaperHost.Parent is Panel panel)
        {
            panel.Children.Remove(_wallpaperHost);
        }

        _wallpaperHost = null;
        _wallpaperLayers.Clear();
        _wallpaperSlideshow.Clear();
        _wallpaperSlideshowIndex = 0;
        _wallpaperLoadedSource = WallpaperSource.None;
        _wallpaperLoadedPath = string.Empty;
        DisposeWallpaperBitmap();
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
            RemoveTextureHost();
            return;
        }

        EnsureTextureHost();
        if (_textureHost == null)
        {
            return;
        }

        var color = TryParseColor(_settings.BackgroundTextureColor, out var parsed)
            ? parsed
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        _textureHost.Background = BuildTextureBrush(_settings.BackgroundTextureType, color, _settings.BackgroundTextureSize);
        UpdateTextureClip();
    }

    private void EnsureTextureHost()
    {
        if (_textureHost != null)
        {
            return;
        }

        var islandGrid = _mainWindow?.FindControl<Grid>(HostContract.GridRoot);
        if (islandGrid == null)
        {
            return;
        }

        _textureHost = new Border
        {
            IsHitTestVisible = false,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _textureHost.SizeChanged += (_, _) => UpdateTextureClip();
        // 层级：背景图片（index 0）之上、行背景色之下。
        islandGrid.Children.Insert(_wallpaperHost != null ? 1 : 0, _textureHost);
        UpdateTextureBounds();
        UpdateTextureClip();
    }

    private void RemoveTextureHost()
    {
        if (_textureHost != null && _textureHost.Parent is Panel panel)
        {
            panel.Children.Remove(_textureHost);
        }

        _textureHost = null;
    }

    /// <summary>
    /// 构建可平铺的纹理画刷（网格 / 点阵 / 斜线 / 十字）。
    /// </summary>
    private static IBrush BuildTextureBrush(BackgroundTexture type, Color color, double size)
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
        if (!_settings.Enabled || !_settings.WallpaperEnabled ||
            _settings.WallpaperSource != WallpaperSource.FolderSlideshow)
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
        if (!_settings.Enabled || !_settings.WallpaperEnabled ||
            _settings.WallpaperSource != WallpaperSource.FolderSlideshow)
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
        if (_prepareOnClassOverlays.Count == 0)
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
                return;
            }

            overlayHost.Children.Add(overlay);
            _prepareOnClassOverlays[line] = overlay;
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
        _ => false
    };

    private static PrepareOnClassOverlay? CreatePrepareOnClassOverlay(PrepareOnClassStyle style) => style switch
    {
        PrepareOnClassStyle.Arrows => new CountdownArrowOverlay(),
        PrepareOnClassStyle.PulseRing => new CountdownPulseRingOverlay(),
        PrepareOnClassStyle.Scanline => new CountdownScanlineOverlay(),
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
    }

    private void RemoveAllPrepareOnClassOverlays()
    {
        foreach (var line in _prepareOnClassOverlays.Keys.ToArray())
        {
            RemovePrepareOnClassOverlay(line);
        }
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
        foreach (var line in GetMainWindowLines())
        {
            PlayPreviewMask(line);
        }
    }

    /// <summary>「预览即将上课」：接下来 5 秒按即将上课状态显示所选样式。</summary>
    public void PreviewPrepareOnClass()
    {
        if (_mainWindow == null)
        {
            Attach();
        }

        _prepareOnClassPreviewUntil = DateTime.UtcNow.AddSeconds(5);
        // 立即创建覆盖层；5 秒后 OnStateTick 的轮询会自动移除。
        OnStateTick(null, EventArgs.Empty);
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
            Content = new TextBlock { Text = "提醒预览", FontSize = 18, FontWeight = FontWeight.SemiBold },
            Duration = TimeSpan.FromSeconds(1.2),
            Color = TryParseColor(_settings.RippleColor, out var rippleColor)
                ? new SolidColorBrush(rippleColor)
                : new SolidColorBrush(Colors.White)
        };
        // 设置遮罩内容会触发 LineOnPropertyChanged → 强调动画 + Ripple。
        maskProperty.SetValue(line, content);
        SetPseudoClass(line, ":mask-in", true);
        _ = ClearPreviewMaskAsync(line, maskProperty);
    }

    /// <summary>
    /// 反射设置宿主控件的伪类（StyledElement.PseudoClasses 对插件不可直接访问）。
    /// </summary>
    private static void SetPseudoClass(Control line, string name, bool value)
    {
        try
        {
            var property = line.GetType().GetProperty("PseudoClasses",
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
            SetPseudoClass(line, ":mask-in", false);
            SetPseudoClass(line, ":mask-out", true);
            await Task.Delay(300);
            if (line.IsAttachedToVisualTree())
            {
                SetPseudoClass(line, ":mask-out", false);
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
        var color = Colors.White;
        if (!isHanabi && !TryParseColor(_settings.RippleColor, out color))
        {
            return;
        }

        var effectControls = TryGetFullScreenEffectHost(out var effectWindow);
        // A Hanabi burst is intentionally much larger than the island. Do not
        // fall back to WindowRoot (which is island-sized) or the bloom will be
        // visibly cropped during early startup.
        if (_settings.RippleType == RippleType.Hanabi && effectControls == null)
        {
            return;
        }
        var center = GetRippleCenter(effectWindow);
        // 所有类型的 Ripple 都支持圆形约束扩散；半径 0 时按主界面大小自动计算。
        double? clipRadius = _settings.RippleConstraintEnabled
            ? (_settings.RippleConstraintRadius > 0 ? _settings.RippleConstraintRadius : GetAutomaticConstraintRadius())
            : null;
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
    /// 自动约束半径：包含主界面岛屿并留出舒适的扩散余量，同时确保全屏特效窗口里的
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

    private void RemoveRipple(IslandRippleOverlay ripple)
    {
        if (_rippleHosts.Remove(ripple, out var host))
        {
            host.Remove(ripple);
            return;
        }

        _windowRoot?.Children.Remove(ripple);
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

        foreach (var borderControl in _mainWindow.GetVisualDescendants().OfType<Border>()
                     .Where(x => x.Name is HostContract.BackgroundBorder or HostContract.BackgroundBorderOverlayMask or HostContract.OverlayMask))
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

            switch (_settings.Shape)
            {
                case IslandShape.Rectangle:
                    borderControl.CornerRadius = new CornerRadius(0);
                    break;
                case IslandShape.RoundedRectangle:
                case IslandShape.Capsule:
                    borderControl.CornerRadius = _settings.Shape == IslandShape.Capsule
                        ? new CornerRadius(Math.Max(1, borderControl.Bounds.Height / 2))
                        : new CornerRadius(_settings.CornerRadius);
                    break;
                case IslandShape.HostDefault:
                    // 全新安装默认不改动主界面的圆角（沿用 ClassIsland 原生圆角）。
                    // 只有用户通过可视化编辑器/设置页显式修改圆角时，
                    // Shape 才会被切换为 RoundedRectangle 并应用自定义圆角。
                    break;
            }

            IBrush? backgroundBrush = null;
            if (borderControl.Name == "BackgroundBorder" && _settings.CustomBackgroundEnabled)
            {
                backgroundBrush = _settings.GradientEnabled && TryParseColor(_settings.GradientEndColor, out var endColor)
                    ? BuildGradientBrush(background, endColor)
                    : new SolidColorBrush(background);
                borderControl.Background = backgroundBrush;
            }

            IBrush? borderBrush = null;
            if (_settings.BorderEnabled)
            {
                borderBrush = new SolidColorBrush(border);
                borderControl.BorderBrush = borderBrush;
                borderControl.BorderThickness = new Thickness(_settings.BorderThickness);
            }

            _decorations.Add((borderControl, backgroundBrush, borderBrush));
        }

        if (!_settings.ShadowEnabled)
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
        _colorTransitionActive = false;
        _dynamicColorsInitialized = false;
        RemoveWallpaper();
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

        foreach (var (control, originalSize) in _originalDisplaySizes)
        {
            control.Width = originalSize.Width;
            control.Height = originalSize.Height;
        }

        _mainWindow?.Classes.Remove(HostContract.InjectorWindowClass);
        _windowRoot = null;
        _styleHost = null;
    }

    public void Dispose()
    {
        RestoreHostState();
    }
}
