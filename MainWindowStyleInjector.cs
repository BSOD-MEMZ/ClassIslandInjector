using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using System.Collections;
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
    // A custom ripple normally lives in ClassIsland's full-screen topmost effect
    // window.  This map lets us remove it from the same host when it completes.
    private readonly Dictionary<IslandRippleOverlay, IList> _rippleHosts = [];
    private DateTime _visibilityStartedAt = DateTime.MinValue;
    private DateTime _emphasisStartedAt = DateTime.MinValue;
    private bool _lastContentVisible;

    public MainWindowStyleInjector(InjectorSettings settings)
    {
        _settings = settings;
        _animationTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAnimationTick);
        _stateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnStateTick);
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
            _islandRoot = mainWindow.FindControl<Control>("StackPanelRootContainer");
            _windowRoot = mainWindow.FindControl<Grid>("WindowRoot");
            _styleHost = mainWindow.FindControl<Border>("ResourceLoaderBorder");
            if (_islandRoot == null)
            {
                return;
            }

            _originalTransform = _islandRoot.RenderTransform;
            _originalOpacity = _islandRoot.Opacity;
            mainWindow.Classes.Add("classisland-injector");
            _islandRoot.Classes.Add("classisland-injector-root");
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
        ApplyTransform(0);
        ApplyDecorations();
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
            x.GetName().Name == "Avalonia.Markup.Xaml.Loader") ?? TryLoadHostRuntimeLoader();
        var loaderType = loaderAssembly?.GetType("Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader");
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
            var loaderPath = hostDirectory == null ? null : Path.Combine(hostDirectory, "Avalonia.Markup.Xaml.Loader.dll");
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

        var phase = _animationClock.Elapsed.TotalSeconds / _settings.AnimationPeriodSeconds * Math.Tau;
        ApplyTransform(Math.Sin(phase));
        AdvanceRipples();
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
                                   _ripples.Count > 0;
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

        var contentRoot = _mainWindow.FindControl<Control>("GridRoot");
        var isVisible = contentRoot?.IsVisible == true;
        if (isVisible && !_lastContentVisible)
        {
            _visibilityStartedAt = DateTime.UtcNow;
            UpdateAnimationTimer();
        }
        _lastContentVisible = isVisible;

        foreach (var line in _mainWindow.GetVisualDescendants().OfType<Control>()
                     .Where(x => x.GetType().FullName == "ClassIsland.Controls.MainWindowLine"))
        {
            ConfigureNativeRipplePlayer(line);
            ObserveLine(line);
            var mask = line.GetType().GetProperty("MaskContent")?.GetValue(line);
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
        if (!_settings.Enabled || e.Property.Name != "MaskContent" || sender is not Control line)
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
        var field = line.GetType().GetField("<TopmostEffectWindow>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
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
            var field = line.GetType().GetField("<TopmostEffectWindow>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
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

    public void PreviewRipple()
    {
        if (_mainWindow == null)
        {
            Attach();
        }

        // Capture the original full-screen effect player before creating the
        // preview. It may not have been discovered by the regular 50 ms poll yet.
        OnStateTick(null, EventArgs.Empty);
        CreateRipple();
    }

    private void CreateRipple()
    {
        if (_settings.RippleType == RippleType.None || _windowRoot == null || _islandRoot == null ||
            !TryParseColor(_settings.RippleColor, out var color))
        {
            return;
        }

        var effectControls = TryGetFullScreenEffectHost(out var effectWindow);
        var center = GetRippleCenter(effectWindow);
        var ripple = new IslandRippleOverlay(center, _settings.RippleType, color,
            TimeSpan.FromSeconds(_settings.RippleDurationSeconds), _settings.RippleThickness);
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
            if (player is not Window window)
            {
                continue;
            }

            var viewModel = player.GetType().GetProperty("ViewModel", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(player);
            if (viewModel?.GetType().GetProperty("EffectControls", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(viewModel) is not IList controls)
            {
                continue;
            }

            effectWindow = window;
            return controls;
        }

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
        if (_mainWindow == null)
        {
            return;
        }

        foreach (var border in _mainWindow.GetVisualDescendants().OfType<Border>()
                     .Where(x => x.Name is "BackgroundBorder" or "BackgroundBorderOverlayMask"))
        {
            var originalCornerRadius = border.CornerRadius;
            var originalBackground = border.Background;
            _decorationRestorers.Add(() =>
            {
                border.CornerRadius = originalCornerRadius;
                border.Background = originalBackground;
            });

            switch (_settings.Shape)
            {
                case IslandShape.Rectangle:
                    border.CornerRadius = new CornerRadius(0);
                    break;
                case IslandShape.RoundedRectangle:
                    border.CornerRadius = new CornerRadius(_settings.CornerRadius);
                    break;
                case IslandShape.Capsule:
                    border.CornerRadius = new CornerRadius(Math.Max(1, border.Bounds.Height / 2));
                    break;
            }

            if (border.Name == "BackgroundBorder" && _settings.CustomBackgroundEnabled &&
                TryParseColor(_settings.BackgroundColor, out var startColor))
            {
                border.Background = _settings.GradientEnabled && TryParseColor(_settings.GradientEndColor, out var endColor)
                    ? new LinearGradientBrush
                    {
                        StartPoint = RelativePoint.TopLeft,
                        EndPoint = RelativePoint.BottomRight,
                        GradientStops = [new GradientStop(startColor, 0), new GradientStop(endColor, 1)]
                    }
                    : new SolidColorBrush(startColor);
            }
        }

        if (!_settings.ShadowEnabled || !TryParseColor(_settings.ShadowColor, out var shadowColor))
        {
            return;
        }

        foreach (var grid in _mainWindow.GetVisualDescendants().OfType<Grid>()
                     .Where(x => x.Name == "GridRoot" && x.FindAncestorOfType<Control>()?.GetType().FullName == "ClassIsland.Controls.MainWindowLine"))
        {
            var originalEffect = grid.Effect;
            _decorationRestorers.Add(() => grid.Effect = originalEffect);
            grid.Effect = new DropShadowEffect
            {
                Color = shadowColor,
                BlurRadius = _settings.ShadowBlur,
                OffsetX = _settings.ShadowOffsetX,
                OffsetY = _settings.ShadowOffsetY,
                Opacity = _settings.ShadowOpacity
            };
        }
    }

    private void RestoreDecorations()
    {
        foreach (var restore in _decorationRestorers)
        {
            restore();
        }
        _decorationRestorers.Clear();
    }

    private static bool TryParseColor(string text, out Color color)
    {
        return Color.TryParse(text, out color);
    }

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
            _islandRoot.Classes.Remove("classisland-injector-root");
        }

        _mainWindow?.Classes.Remove("classisland-injector");
        _windowRoot = null;
        _styleHost = null;
    }

    public void Dispose()
    {
        RestoreHostState();
    }
}
