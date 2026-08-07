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
    Hanabi,
    Diamond,
    Triangle,
    Star,
    Hexagon,
    Burst,
    Explode,
    Particle,
    Cinematic
}

/// <summary>
/// 「即将上课」倒计时期间显示的特效样式。
/// </summary>
public enum PrepareOnClassStyle
{
    None,
    Arrows,
    PulseRing,
    Scanline
}

/// <summary>
/// 「即将上课样式 · 扫描线」的运动方向。
/// </summary>
public enum ScanlineDirection
{
    Horizontal,
    Vertical
}

/// <summary>
/// 「轮播容器」切换动画的类型。
/// </summary>
public enum CarouselAnimationType
{
    SlideUp,
    SlideDown,
    SlideLeft,
    SlideRight,
    Fade
}

/// <summary>
/// 自定义背景的渐变方向。
/// </summary>
public enum GradientDirection
{
    TopLeftToBottomRight,
    TopToBottom,
    LeftToRight,
    BottomLeftToTopRight,
    BottomToTop,
    RightToLeft,
    TopRightToBottomLeft,
    BottomRightToTopLeft
}

/// <summary>
/// 背景填充纹理类型（叠加在背景色之上，可与背景图片同时使用）。
/// </summary>
public enum BackgroundTexture
{
    None,
    Grid,
    Dots,
    DiagonalLines,
    Cross
}

/// <summary>
/// 主界面底图的图片来源。
/// </summary>
public enum WallpaperSource
{
    None,
    LocalImage,
    FolderSlideshow,
    SmtcAlbum
}

/// <summary>
/// 主界面底图的显示方式。
/// </summary>
public enum WallpaperDisplayMode
{
    Fill,
    Fit,
    Stretch,
    Tile
}

public sealed class InjectorSettings
{
    private bool _enabled = true;
    private double _opacity = 1;
    private double _rotation;
    private double _offsetX;
    private double _offsetY;
    private bool _animationEnabled;
    private IslandAnimationMode _animationMode = IslandAnimationMode.None;
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
    private GradientDirection _gradientDirection = GradientDirection.TopLeftToBottomRight;
    private BackgroundTexture _backgroundTextureType = BackgroundTexture.None;
    private string _backgroundTextureColor = "#2EFFFFFF";
    private double _backgroundTextureSize = 24;
    private bool _shadowEnabled;
    private string _shadowColor = "#99000000";
    private double _shadowBlur = 16;
    private double _shadowOffsetX;
    private double _shadowOffsetY = 6;
    private double _shadowOpacity = 0.8;
    private bool _borderEnabled;
    private string _borderColor = "#99FFFFFF";
    private double _borderThickness = 1;
    private VisibilityAnimation _visibilityAnimation = VisibilityAnimation.None;
    private double _visibilityDurationSeconds = 0.35;
    private EmphasisAnimation _emphasisAnimation = EmphasisAnimation.None;
    private double _emphasisAmount = 0.12;
    private double _emphasisDurationSeconds = 0.45;
    private NotificationTransition _notificationTransition = NotificationTransition.HostDefault;
    private double _notificationTransitionDurationSeconds = 0.22;
    private bool _carouselAnimationEnabled;
    private double _carouselAnimationDurationSeconds = 0.25;
    private double _carouselAnimationOffset = 40;
    private CarouselAnimationType _carouselAnimationType = CarouselAnimationType.SlideUp;
    private RippleType _rippleType = RippleType.None;
    private string _rippleColor = "#AA7DD3FC";
    private double _rippleDurationSeconds = 0.65;
    private double _rippleThickness = 3;
    private double _rippleOpacity = 1;
    private bool _rippleConstraintEnabled = true;
    private double _rippleConstraintRadius;
    private bool _marqueeEnabled;
    private string _marqueeColor = "#66FFFFFF";
    private double _marqueeDurationSeconds = 1.6;
    private double _marqueeOpacity = 0.85;
    private double _marqueeSpeed = 1;
    private double _marqueeFrameThickness = 0.05;
    private bool _dynamicBackgroundColorEnabled;
    private bool _dynamicBorderColorEnabled;
    private bool _dynamicShadowColorEnabled;
    private bool _revertColorsWhenPaused;
    private double _albumColorPollingIntervalSeconds = 10;
    private double _albumColorTransitionSeconds = 0.6;
    private bool _wallpaperEnabled;
    private WallpaperSource _wallpaperSource = WallpaperSource.None;
    private string _wallpaperPath = string.Empty;
    private double _wallpaperOpacity = 0.6;
    private WallpaperDisplayMode _wallpaperDisplayMode = WallpaperDisplayMode.Fill;
    private double _wallpaperScale = 1;
    private double _wallpaperOffsetX;
    private double _wallpaperOffsetY;
    private double _wallpaperSlideshowIntervalSeconds = 30;
    private double _wallpaperBlurRadius;
    private PrepareOnClassStyle _prepareOnClassStyle = PrepareOnClassStyle.None;
    private string _countdownArrowColor = "#BFF8FAFC";
    private int _countdownArrowCount = 5;
    private int _countdownArrowPerGroup = 2;
    private double _countdownArrowSpacing = 12;
    private double _countdownArrowGroupSpacing = 24;
    private double _countdownArrowSpeed = 2.4;
    private double _countdownArrowThickness = 8;
    private string _countdownPulseColor = "#BFF8FAFC";
    private double _countdownPulseThickness = 3;
    private double _countdownPulseSpeed = 1;
    private double _countdownPulseMaxRadius = 0.5;
    private string _countdownScanColor = "#BFF8FAFC";
    private double _countdownScanThickness = 2;
    private double _countdownScanSpeed = 1;
    private ScanlineDirection _countdownScanDirection = ScanlineDirection.Horizontal;
    private bool _countdownScanTailEnabled = true;
    private bool _prepareWarningEnabled;
    private string _prepareWarningColor = "#66FF0000";
    private double _prepareWarningTriggerSeconds = 30;
    private double _prepareWarningFlashSpeed = 3;
    private double _prepareWarningFlashAmount = 0.55;
    private double _prepareWarningFrameThickness = 0.02;
    private double _prepareWarningOpacity = 1;
    private double _cinematicShakeAmount = 14;
    private double _cinematicBlurRadius = 16;
    private double _cinematicFlashAmount = 0.8;
    private int _updateDepth;
    private bool _changePending;

    public event EventHandler? Changed;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }
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
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 20)); }
    public bool CustomBackgroundEnabled { get => _customBackgroundEnabled; set => Set(ref _customBackgroundEnabled, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value.Trim()); }
    public bool GradientEnabled { get => _gradientEnabled; set => Set(ref _gradientEnabled, value); }
    public string GradientEndColor { get => _gradientEndColor; set => Set(ref _gradientEndColor, value.Trim()); }
    public GradientDirection GradientDirection { get => _gradientDirection; set => Set(ref _gradientDirection, value); }
    public BackgroundTexture BackgroundTextureType { get => _backgroundTextureType; set => Set(ref _backgroundTextureType, value); }
    public string BackgroundTextureColor { get => _backgroundTextureColor; set => Set(ref _backgroundTextureColor, value.Trim()); }
    public double BackgroundTextureSize { get => _backgroundTextureSize; set => Set(ref _backgroundTextureSize, Math.Clamp(value, 8, 80)); }
    public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }
    public string ShadowColor { get => _shadowColor; set => Set(ref _shadowColor, value.Trim()); }
    public double ShadowBlur { get => _shadowBlur; set => Set(ref _shadowBlur, Math.Clamp(value, 0, 200)); }
    public double ShadowOffsetX { get => _shadowOffsetX; set => Set(ref _shadowOffsetX, Math.Clamp(value, -200, 200)); }
    public double ShadowOffsetY { get => _shadowOffsetY; set => Set(ref _shadowOffsetY, Math.Clamp(value, -200, 200)); }
    public double ShadowOpacity { get => _shadowOpacity; set => Set(ref _shadowOpacity, Math.Clamp(value, 0, 1)); }
    public bool BorderEnabled { get => _borderEnabled; set => Set(ref _borderEnabled, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value.Trim()); }
    public double BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, Math.Clamp(value, 0.25, 20)); }
    public VisibilityAnimation VisibilityAnimation { get => _visibilityAnimation; set => Set(ref _visibilityAnimation, value); }
    public double VisibilityDurationSeconds { get => _visibilityDurationSeconds; set => Set(ref _visibilityDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public EmphasisAnimation EmphasisAnimation { get => _emphasisAnimation; set => Set(ref _emphasisAnimation, value); }
    public double EmphasisAmount { get => _emphasisAmount; set => Set(ref _emphasisAmount, Math.Clamp(value, 0, 1)); }
    public double EmphasisDurationSeconds { get => _emphasisDurationSeconds; set => Set(ref _emphasisDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public NotificationTransition NotificationTransition { get => _notificationTransition; set => Set(ref _notificationTransition, value); }
    public double NotificationTransitionDurationSeconds { get => _notificationTransitionDurationSeconds; set => Set(ref _notificationTransitionDurationSeconds, Math.Clamp(value, 0.05, 5)); }
    public bool CarouselAnimationEnabled { get => _carouselAnimationEnabled; set => Set(ref _carouselAnimationEnabled, value); }
    public double CarouselAnimationDurationSeconds { get => _carouselAnimationDurationSeconds; set => Set(ref _carouselAnimationDurationSeconds, Math.Clamp(value, 0.05, 5)); }
    public double CarouselAnimationOffset { get => _carouselAnimationOffset; set => Set(ref _carouselAnimationOffset, Math.Clamp(value, 0, 500)); }
    public CarouselAnimationType CarouselAnimationType { get => _carouselAnimationType; set => Set(ref _carouselAnimationType, value); }
    public RippleType RippleType { get => _rippleType; set => Set(ref _rippleType, value); }
    public string RippleColor { get => _rippleColor; set => Set(ref _rippleColor, value.Trim()); }
    public double RippleDurationSeconds { get => _rippleDurationSeconds; set => Set(ref _rippleDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public double RippleThickness { get => _rippleThickness; set => Set(ref _rippleThickness, Math.Clamp(value, 0.5, 40)); }
    public double RippleOpacity { get => _rippleOpacity; set => Set(ref _rippleOpacity, Math.Clamp(value, 0.1, 1)); }
    public bool RippleConstraintEnabled { get => _rippleConstraintEnabled; set => Set(ref _rippleConstraintEnabled, value); }
    public double RippleConstraintRadius { get => _rippleConstraintRadius; set => Set(ref _rippleConstraintRadius, Math.Clamp(value, 0, 2000)); }
    public bool MarqueeEnabled { get => _marqueeEnabled; set => Set(ref _marqueeEnabled, value); }
    public string MarqueeColor { get => _marqueeColor; set => Set(ref _marqueeColor, value.Trim()); }
    public double MarqueeDurationSeconds { get => _marqueeDurationSeconds; set => Set(ref _marqueeDurationSeconds, Math.Clamp(value, 0.1, 10)); }
    public double MarqueeOpacity { get => _marqueeOpacity; set => Set(ref _marqueeOpacity, Math.Clamp(value, 0.1, 1)); }
    public double MarqueeSpeed { get => _marqueeSpeed; set => Set(ref _marqueeSpeed, Math.Clamp(value, 0.1, 8)); }
    public double MarqueeFrameThickness { get => _marqueeFrameThickness; set => Set(ref _marqueeFrameThickness, Math.Clamp(value, 0.01, 0.15)); }
    public bool DynamicBackgroundColorEnabled { get => _dynamicBackgroundColorEnabled; set => Set(ref _dynamicBackgroundColorEnabled, value); }
    public bool DynamicBorderColorEnabled { get => _dynamicBorderColorEnabled; set => Set(ref _dynamicBorderColorEnabled, value); }
    public bool DynamicShadowColorEnabled { get => _dynamicShadowColorEnabled; set => Set(ref _dynamicShadowColorEnabled, value); }
    public bool RevertColorsWhenPaused { get => _revertColorsWhenPaused; set => Set(ref _revertColorsWhenPaused, value); }
    public double AlbumColorPollingIntervalSeconds { get => _albumColorPollingIntervalSeconds; set => Set(ref _albumColorPollingIntervalSeconds, Math.Clamp(value, 0.5, 120)); }
    public double AlbumColorTransitionSeconds { get => _albumColorTransitionSeconds; set => Set(ref _albumColorTransitionSeconds, Math.Clamp(value, 0, 10)); }
    public bool WallpaperEnabled { get => _wallpaperEnabled; set => Set(ref _wallpaperEnabled, value); }
    public WallpaperSource WallpaperSource { get => _wallpaperSource; set => Set(ref _wallpaperSource, value); }
    public string WallpaperPath { get => _wallpaperPath; set => Set(ref _wallpaperPath, value.Trim()); }
    public double WallpaperOpacity { get => _wallpaperOpacity; set => Set(ref _wallpaperOpacity, Math.Clamp(value, 0, 1)); }
    public WallpaperDisplayMode WallpaperDisplayMode { get => _wallpaperDisplayMode; set => Set(ref _wallpaperDisplayMode, value); }
    public double WallpaperScale { get => _wallpaperScale; set => Set(ref _wallpaperScale, Math.Clamp(value, 1, 5)); }
    public double WallpaperOffsetX { get => _wallpaperOffsetX; set => Set(ref _wallpaperOffsetX, Math.Clamp(value, -0.5, 0.5)); }
    public double WallpaperOffsetY { get => _wallpaperOffsetY; set => Set(ref _wallpaperOffsetY, Math.Clamp(value, -0.5, 0.5)); }
    public double WallpaperSlideshowIntervalSeconds { get => _wallpaperSlideshowIntervalSeconds; set => Set(ref _wallpaperSlideshowIntervalSeconds, Math.Clamp(value, 2, 3600)); }
    public double WallpaperBlurRadius { get => _wallpaperBlurRadius; set => Set(ref _wallpaperBlurRadius, Math.Clamp(value, 0, 60)); }
    public PrepareOnClassStyle PrepareOnClassStyle { get => _prepareOnClassStyle; set => Set(ref _prepareOnClassStyle, value); }
    public string CountdownArrowColor { get => _countdownArrowColor; set => Set(ref _countdownArrowColor, value.Trim()); }
    public int CountdownArrowCount { get => _countdownArrowCount; set => Set(ref _countdownArrowCount, Math.Clamp(value, 1, 24)); }
    public int CountdownArrowPerGroup { get => _countdownArrowPerGroup; set => Set(ref _countdownArrowPerGroup, Math.Clamp(value, 1, 12)); }
    public double CountdownArrowSpacing { get => _countdownArrowSpacing; set => Set(ref _countdownArrowSpacing, Math.Clamp(value, 0, 100)); }
    public double CountdownArrowGroupSpacing { get => _countdownArrowGroupSpacing; set => Set(ref _countdownArrowGroupSpacing, Math.Clamp(value, 0, 400)); }
    public double CountdownArrowSpeed { get => _countdownArrowSpeed; set => Set(ref _countdownArrowSpeed, Math.Clamp(value, 0.1, 12)); }
    public double CountdownArrowThickness { get => _countdownArrowThickness; set => Set(ref _countdownArrowThickness, Math.Clamp(value, 0.5, 20)); }
    public string CountdownPulseColor { get => _countdownPulseColor; set => Set(ref _countdownPulseColor, value.Trim()); }
    public double CountdownPulseThickness { get => _countdownPulseThickness; set => Set(ref _countdownPulseThickness, Math.Clamp(value, 0.5, 20)); }
    public double CountdownPulseSpeed { get => _countdownPulseSpeed; set => Set(ref _countdownPulseSpeed, Math.Clamp(value, 0.1, 8)); }
    public double CountdownPulseMaxRadius { get => _countdownPulseMaxRadius; set => Set(ref _countdownPulseMaxRadius, Math.Clamp(value, 0.1, 1)); }
    public string CountdownScanColor { get => _countdownScanColor; set => Set(ref _countdownScanColor, value.Trim()); }
    public double CountdownScanThickness { get => _countdownScanThickness; set => Set(ref _countdownScanThickness, Math.Clamp(value, 0.5, 20)); }
    public double CountdownScanSpeed { get => _countdownScanSpeed; set => Set(ref _countdownScanSpeed, Math.Clamp(value, 0.1, 8)); }
    public ScanlineDirection CountdownScanDirection { get => _countdownScanDirection; set => Set(ref _countdownScanDirection, value); }
    public bool CountdownScanTailEnabled { get => _countdownScanTailEnabled; set => Set(ref _countdownScanTailEnabled, value); }
    public bool PrepareWarningEnabled { get => _prepareWarningEnabled; set => Set(ref _prepareWarningEnabled, value); }
    public string PrepareWarningColor { get => _prepareWarningColor; set => Set(ref _prepareWarningColor, value.Trim()); }
    public double PrepareWarningTriggerSeconds { get => _prepareWarningTriggerSeconds; set => Set(ref _prepareWarningTriggerSeconds, Math.Clamp(value, 5, 600)); }
    public double PrepareWarningFlashSpeed { get => _prepareWarningFlashSpeed; set => Set(ref _prepareWarningFlashSpeed, Math.Clamp(value, 0.1, 10)); }
    public double PrepareWarningFlashAmount { get => _prepareWarningFlashAmount; set => Set(ref _prepareWarningFlashAmount, Math.Clamp(value, 0, 1)); }
    public double PrepareWarningFrameThickness { get => _prepareWarningFrameThickness; set => Set(ref _prepareWarningFrameThickness, Math.Clamp(value, 0.005, 0.1)); }
    public double PrepareWarningOpacity { get => _prepareWarningOpacity; set => Set(ref _prepareWarningOpacity, Math.Clamp(value, 0.1, 1)); }
    public double CinematicShakeAmount { get => _cinematicShakeAmount; set => Set(ref _cinematicShakeAmount, Math.Clamp(value, 0, 80)); }
    public double CinematicBlurRadius { get => _cinematicBlurRadius; set => Set(ref _cinematicBlurRadius, Math.Clamp(value, 0, 60)); }
    public double CinematicFlashAmount { get => _cinematicFlashAmount; set => Set(ref _cinematicFlashAmount, Math.Clamp(value, 0, 1)); }

    public void ResetToDefaults()
    {
        var styleSheetPath = StyleSheetPath;
        var watchStyleSheet = WatchStyleSheet;
        CopyFrom(new InjectorSettings { StyleSheetPath = styleSheetPath, WatchStyleSheet = watchStyleSheet });
    }

    /// <summary>
    /// 深拷贝当前全部设置，用于用户预设快照与自动化行动的“恢复”操作。
    /// </summary>
    public InjectorSettings Clone()
    {
        var clone = new InjectorSettings();
        clone.CopyFrom(this);
        return clone;
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

    internal void CopyFrom(InjectorSettings source)
    {
        BeginUpdate();
        Enabled = source.Enabled;
        Opacity = source.Opacity;
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
        GradientDirection = source.GradientDirection;
        BackgroundTextureType = source.BackgroundTextureType;
        BackgroundTextureColor = source.BackgroundTextureColor;
        BackgroundTextureSize = source.BackgroundTextureSize;
        ShadowEnabled = source.ShadowEnabled;
        ShadowColor = source.ShadowColor;
        ShadowBlur = source.ShadowBlur;
        ShadowOffsetX = source.ShadowOffsetX;
        ShadowOffsetY = source.ShadowOffsetY;
        ShadowOpacity = source.ShadowOpacity;
        BorderEnabled = source.BorderEnabled;
        BorderColor = source.BorderColor;
        BorderThickness = source.BorderThickness;
        VisibilityAnimation = source.VisibilityAnimation;
        VisibilityDurationSeconds = source.VisibilityDurationSeconds;
        EmphasisAnimation = source.EmphasisAnimation;
        EmphasisAmount = source.EmphasisAmount;
        EmphasisDurationSeconds = source.EmphasisDurationSeconds;
        NotificationTransition = source.NotificationTransition;
        NotificationTransitionDurationSeconds = source.NotificationTransitionDurationSeconds;
        CarouselAnimationEnabled = source.CarouselAnimationEnabled;
        CarouselAnimationDurationSeconds = source.CarouselAnimationDurationSeconds;
        CarouselAnimationOffset = source.CarouselAnimationOffset;
        CarouselAnimationType = source.CarouselAnimationType;
        RippleType = source.RippleType;
        RippleColor = source.RippleColor;
        DynamicBorderColorEnabled = source.DynamicBorderColorEnabled;
        DynamicShadowColorEnabled = source.DynamicShadowColorEnabled;
        RevertColorsWhenPaused = source.RevertColorsWhenPaused;
        AlbumColorPollingIntervalSeconds = source.AlbumColorPollingIntervalSeconds;
        AlbumColorTransitionSeconds = source.AlbumColorTransitionSeconds;
        RippleDurationSeconds = source.RippleDurationSeconds;
        RippleThickness = source.RippleThickness;
        RippleOpacity = source.RippleOpacity;
        RippleConstraintEnabled = source.RippleConstraintEnabled;
        RippleConstraintRadius = source.RippleConstraintRadius;
        MarqueeEnabled = source.MarqueeEnabled;
        MarqueeColor = source.MarqueeColor;
        MarqueeDurationSeconds = source.MarqueeDurationSeconds;
        MarqueeOpacity = source.MarqueeOpacity;
        MarqueeSpeed = source.MarqueeSpeed;
        MarqueeFrameThickness = source.MarqueeFrameThickness;
        DynamicBackgroundColorEnabled = source.DynamicBackgroundColorEnabled;
        WallpaperEnabled = source.WallpaperEnabled;
        WallpaperSource = source.WallpaperSource;
        WallpaperPath = source.WallpaperPath;
        WallpaperOpacity = source.WallpaperOpacity;
        WallpaperDisplayMode = source.WallpaperDisplayMode;
        WallpaperScale = source.WallpaperScale;
        WallpaperOffsetX = source.WallpaperOffsetX;
        WallpaperOffsetY = source.WallpaperOffsetY;
        WallpaperSlideshowIntervalSeconds = source.WallpaperSlideshowIntervalSeconds;
        WallpaperBlurRadius = source.WallpaperBlurRadius;
        PrepareOnClassStyle = source.PrepareOnClassStyle;
        CountdownArrowColor = source.CountdownArrowColor;
        CountdownArrowCount = source.CountdownArrowCount;
        CountdownArrowPerGroup = source.CountdownArrowPerGroup;
        CountdownArrowSpacing = source.CountdownArrowSpacing;
        CountdownArrowGroupSpacing = source.CountdownArrowGroupSpacing;
        CountdownArrowSpeed = source.CountdownArrowSpeed;
        CountdownArrowThickness = source.CountdownArrowThickness;
        CountdownPulseColor = source.CountdownPulseColor;
        CountdownPulseThickness = source.CountdownPulseThickness;
        CountdownPulseSpeed = source.CountdownPulseSpeed;
        CountdownPulseMaxRadius = source.CountdownPulseMaxRadius;
        CountdownScanColor = source.CountdownScanColor;
        CountdownScanThickness = source.CountdownScanThickness;
        CountdownScanSpeed = source.CountdownScanSpeed;
        CountdownScanDirection = source.CountdownScanDirection;
        CountdownScanTailEnabled = source.CountdownScanTailEnabled;
        PrepareWarningEnabled = source.PrepareWarningEnabled;
        PrepareWarningColor = source.PrepareWarningColor;
        PrepareWarningTriggerSeconds = source.PrepareWarningTriggerSeconds;
        PrepareWarningFlashSpeed = source.PrepareWarningFlashSpeed;
        PrepareWarningFlashAmount = source.PrepareWarningFlashAmount;
        PrepareWarningFrameThickness = source.PrepareWarningFrameThickness;
        PrepareWarningOpacity = source.PrepareWarningOpacity;
        CinematicShakeAmount = source.CinematicShakeAmount;
        CinematicBlurRadius = source.CinematicBlurRadius;
        CinematicFlashAmount = source.CinematicFlashAmount;
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
