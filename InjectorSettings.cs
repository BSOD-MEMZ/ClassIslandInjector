using System.Text.Json;

namespace ClassIslandInjector;

public enum IslandAnimationMode
{
    None,
    Breathe,
    Float,
    Wave
}

public enum IslandShape
{
    HostDefault,
    Rectangle,
    RoundedRectangle,
    Capsule
}

public enum VisibilityAnimation
{
    None,
    Fade,
    Scale,
    SlideFromTop,
    SlideFromBottom
}

public enum EmphasisAnimation
{
    None,
    Pulse,
    Bounce,
    Shake,
    Flash
}

public enum NotificationTransition
{
    HostDefault,
    Fade,
    SlideDown,
    SlideUp,
    SlideLeft,
    SlideRight
}

public enum RippleType
{
    None,
    Ring,
    DoubleRing,
    Glow,
    Square,
    Hanabi
}

public enum StylePreset
{
    GlassCapsule,
    NeonPulse,
    MaimaiHanabi,
    Minimal
}

public sealed class InjectorSettings
{
    private bool _enabled = true;
    private double _opacity = 1;
    private double _scale = 1;
    private double _rotation;
    private double _offsetX;
    private double _offsetY;
    private bool _animationEnabled;
    private IslandAnimationMode _animationMode = IslandAnimationMode.Breathe;
    private double _animationAmount = 0.04;
    private double _animationPeriodSeconds = 2.5;
    private string _styleSheetPath = string.Empty;
    private bool _watchStyleSheet = true;
    private IslandShape _shape = IslandShape.HostDefault;
    private double _cornerRadius = 18;
    private bool _customBackgroundEnabled;
    private string _backgroundColor = "#CC202020";
    private bool _gradientEnabled;
    private string _gradientEndColor = "#CC4040A0";
    private bool _shadowEnabled;
    private string _shadowColor = "#99000000";
    private double _shadowBlur = 16;
    private double _shadowOffsetX;
    private double _shadowOffsetY = 6;
    private double _shadowOpacity = 0.8;
    private VisibilityAnimation _visibilityAnimation = VisibilityAnimation.Scale;
    private double _visibilityDurationSeconds = 0.35;
    private EmphasisAnimation _emphasisAnimation = EmphasisAnimation.Pulse;
    private double _emphasisAmount = 0.12;
    private double _emphasisDurationSeconds = 0.45;
    private NotificationTransition _notificationTransition = NotificationTransition.HostDefault;
    private double _notificationTransitionDurationSeconds = 0.22;
    private RippleType _rippleType = RippleType.Ring;
    private string _rippleColor = "#AA7DD3FC";
    private double _rippleDurationSeconds = 0.65;
    private double _rippleThickness = 3;
    private int _updateDepth;
    private bool _changePending;

    public event EventHandler? Changed;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.1, 5)); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, Math.Clamp(value, -360, 360)); }
    public double OffsetX { get => _offsetX; set => Set(ref _offsetX, Math.Clamp(value, -2000, 2000)); }
    public double OffsetY { get => _offsetY; set => Set(ref _offsetY, Math.Clamp(value, -2000, 2000)); }
    public bool AnimationEnabled { get => _animationEnabled; set => Set(ref _animationEnabled, value); }
    public IslandAnimationMode AnimationMode { get => _animationMode; set => Set(ref _animationMode, value); }
    public double AnimationAmount { get => _animationAmount; set => Set(ref _animationAmount, Math.Clamp(value, 0, 1)); }
    public double AnimationPeriodSeconds { get => _animationPeriodSeconds; set => Set(ref _animationPeriodSeconds, Math.Clamp(value, 0.2, 60)); }
    public string StyleSheetPath { get => _styleSheetPath; set => Set(ref _styleSheetPath, value.Trim()); }
    public bool WatchStyleSheet { get => _watchStyleSheet; set => Set(ref _watchStyleSheet, value); }
    public IslandShape Shape { get => _shape; set => Set(ref _shape, value); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 500)); }
    public bool CustomBackgroundEnabled { get => _customBackgroundEnabled; set => Set(ref _customBackgroundEnabled, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value.Trim()); }
    public bool GradientEnabled { get => _gradientEnabled; set => Set(ref _gradientEnabled, value); }
    public string GradientEndColor { get => _gradientEndColor; set => Set(ref _gradientEndColor, value.Trim()); }
    public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }
    public string ShadowColor { get => _shadowColor; set => Set(ref _shadowColor, value.Trim()); }
    public double ShadowBlur { get => _shadowBlur; set => Set(ref _shadowBlur, Math.Clamp(value, 0, 200)); }
    public double ShadowOffsetX { get => _shadowOffsetX; set => Set(ref _shadowOffsetX, Math.Clamp(value, -200, 200)); }
    public double ShadowOffsetY { get => _shadowOffsetY; set => Set(ref _shadowOffsetY, Math.Clamp(value, -200, 200)); }
    public double ShadowOpacity { get => _shadowOpacity; set => Set(ref _shadowOpacity, Math.Clamp(value, 0, 1)); }
    public VisibilityAnimation VisibilityAnimation { get => _visibilityAnimation; set => Set(ref _visibilityAnimation, value); }
    public double VisibilityDurationSeconds { get => _visibilityDurationSeconds; set => Set(ref _visibilityDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public EmphasisAnimation EmphasisAnimation { get => _emphasisAnimation; set => Set(ref _emphasisAnimation, value); }
    public double EmphasisAmount { get => _emphasisAmount; set => Set(ref _emphasisAmount, Math.Clamp(value, 0, 1)); }
    public double EmphasisDurationSeconds { get => _emphasisDurationSeconds; set => Set(ref _emphasisDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public NotificationTransition NotificationTransition { get => _notificationTransition; set => Set(ref _notificationTransition, value); }
    public double NotificationTransitionDurationSeconds { get => _notificationTransitionDurationSeconds; set => Set(ref _notificationTransitionDurationSeconds, Math.Clamp(value, 0.05, 5)); }
    public RippleType RippleType { get => _rippleType; set => Set(ref _rippleType, value); }
    public string RippleColor { get => _rippleColor; set => Set(ref _rippleColor, value.Trim()); }
    public double RippleDurationSeconds { get => _rippleDurationSeconds; set => Set(ref _rippleDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public double RippleThickness { get => _rippleThickness; set => Set(ref _rippleThickness, Math.Clamp(value, 0.5, 40)); }

    public void ResetToDefaults()
    {
        var styleSheetPath = StyleSheetPath;
        var watchStyleSheet = WatchStyleSheet;
        CopyFrom(new InjectorSettings { StyleSheetPath = styleSheetPath, WatchStyleSheet = watchStyleSheet });
    }

    public void ApplyPreset(StylePreset preset)
    {
        BeginUpdate();
        ResetToDefaults();
        switch (preset)
        {
            case StylePreset.GlassCapsule:
                Shape = IslandShape.Capsule;
                CornerRadius = 28;
                CustomBackgroundEnabled = true;
                BackgroundColor = "#A81A2334";
                GradientEnabled = true;
                GradientEndColor = "#8A394D70";
                ShadowEnabled = true;
                ShadowColor = "#8839BDF8";
                ShadowBlur = 22;
                ShadowOffsetY = 5;
                VisibilityAnimation = VisibilityAnimation.Fade;
                EmphasisAnimation = EmphasisAnimation.Pulse;
                RippleType = RippleType.Glow;
                break;
            case StylePreset.NeonPulse:
                Shape = IslandShape.RoundedRectangle;
                CornerRadius = 20;
                CustomBackgroundEnabled = true;
                BackgroundColor = "#E20C1020";
                GradientEnabled = true;
                GradientEndColor = "#E21D0C36";
                ShadowEnabled = true;
                ShadowColor = "#D05B2CFF";
                ShadowBlur = 28;
                AnimationEnabled = true;
                AnimationMode = IslandAnimationMode.Breathe;
                AnimationAmount = 0.025;
                EmphasisAnimation = EmphasisAnimation.Pulse;
                EmphasisAmount = 0.16;
                RippleType = RippleType.DoubleRing;
                RippleColor = "#E05B2CFF";
                break;
            case StylePreset.MaimaiHanabi:
                Shape = IslandShape.Capsule;
                CornerRadius = 32;
                CustomBackgroundEnabled = true;
                BackgroundColor = "#D4141729";
                GradientEnabled = true;
                GradientEndColor = "#D5391458";
                ShadowEnabled = true;
                ShadowColor = "#B8FF4C9A";
                ShadowBlur = 30;
                VisibilityAnimation = VisibilityAnimation.Scale;
                EmphasisAnimation = EmphasisAnimation.Bounce;
                EmphasisAmount = 0.13;
                RippleType = RippleType.Hanabi;
                RippleColor = "#FFFF76B8";
                RippleDurationSeconds = 1.35;
                RippleThickness = 2.5;
                break;
            case StylePreset.Minimal:
                Shape = IslandShape.RoundedRectangle;
                CornerRadius = 12;
                ShadowEnabled = false;
                AnimationEnabled = false;
                VisibilityAnimation = VisibilityAnimation.Fade;
                EmphasisAnimation = EmphasisAnimation.None;
                RippleType = RippleType.None;
                break;
        }
        EndUpdate();
    }

    public void BeginUpdate()
    {
        _updateDepth++;
    }

    public void EndUpdate()
    {
        if (_updateDepth == 0 || --_updateDepth != 0 || !_changePending)
        {
            return;
        }

        _changePending = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CopyFrom(InjectorSettings source)
    {
        BeginUpdate();
        Enabled = source.Enabled;
        Opacity = source.Opacity;
        Scale = source.Scale;
        Rotation = source.Rotation;
        OffsetX = source.OffsetX;
        OffsetY = source.OffsetY;
        AnimationEnabled = source.AnimationEnabled;
        AnimationMode = source.AnimationMode;
        AnimationAmount = source.AnimationAmount;
        AnimationPeriodSeconds = source.AnimationPeriodSeconds;
        StyleSheetPath = source.StyleSheetPath;
        WatchStyleSheet = source.WatchStyleSheet;
        Shape = source.Shape;
        CornerRadius = source.CornerRadius;
        CustomBackgroundEnabled = source.CustomBackgroundEnabled;
        BackgroundColor = source.BackgroundColor;
        GradientEnabled = source.GradientEnabled;
        GradientEndColor = source.GradientEndColor;
        ShadowEnabled = source.ShadowEnabled;
        ShadowColor = source.ShadowColor;
        ShadowBlur = source.ShadowBlur;
        ShadowOffsetX = source.ShadowOffsetX;
        ShadowOffsetY = source.ShadowOffsetY;
        ShadowOpacity = source.ShadowOpacity;
        VisibilityAnimation = source.VisibilityAnimation;
        VisibilityDurationSeconds = source.VisibilityDurationSeconds;
        EmphasisAnimation = source.EmphasisAnimation;
        EmphasisAmount = source.EmphasisAmount;
        EmphasisDurationSeconds = source.EmphasisDurationSeconds;
        NotificationTransition = source.NotificationTransition;
        NotificationTransitionDurationSeconds = source.NotificationTransitionDurationSeconds;
        RippleType = source.RippleType;
        RippleColor = source.RippleColor;
        RippleDurationSeconds = source.RippleDurationSeconds;
        RippleThickness = source.RippleThickness;
        EndUpdate();
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        if (_updateDepth > 0)
        {
            _changePending = true;
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal static class InjectorSettingsStore
{
    private const string SettingsFileName = "settings.json";
    private const string DefaultStyleSheetName = "Overrides.axaml";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static InjectorSettings Load(string configDirectory, string pluginDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var defaultStyleSheet = Path.Combine(configDirectory, DefaultStyleSheetName);
        if (!File.Exists(defaultStyleSheet))
        {
            var packagedStyleSheet = Path.Combine(pluginDirectory, "Defaults", DefaultStyleSheetName);
            if (File.Exists(packagedStyleSheet))
            {
                File.Copy(packagedStyleSheet, defaultStyleSheet);
            }
        }

        var settingsPath = Path.Combine(configDirectory, SettingsFileName);
        try
        {
            if (File.Exists(settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<InjectorSettings>(File.ReadAllText(settingsPath), JsonOptions);
                if (loaded != null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.StyleSheetPath))
                    {
                        loaded.StyleSheetPath = defaultStyleSheet;
                    }

                    return loaded;
                }
            }
        }
        catch (JsonException)
        {
            var backupPath = settingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(settingsPath, backupPath, true);
        }

        var settings = new InjectorSettings { StyleSheetPath = defaultStyleSheet };
        Save(configDirectory, settings);
        return settings;
    }

    public static void Save(string configDirectory, InjectorSettings settings)
    {
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, SettingsFileName), JsonSerializer.Serialize(settings, JsonOptions));
    }
}
