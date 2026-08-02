using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace ClassIslandInjector.Views;

[SettingsPageInfo("miku.classisland.injector", "样式注入器")]
public sealed class InjectorSettingsPage : SettingsPageBase
{
    private readonly CheckBox _enabled = new() { Content = "启用运行时注入" };
    private readonly TextBox _opacity = new();
    private readonly TextBox _scale = new();
    private readonly TextBox _rotation = new();
    private readonly TextBox _offsetX = new();
    private readonly TextBox _offsetY = new();
    private readonly CheckBox _animationEnabled = new() { Content = "启用动画" };
    private readonly ComboBox _animationMode = new() { ItemsSource = Enum.GetValues<IslandAnimationMode>() };
    private readonly TextBox _animationAmount = new();
    private readonly TextBox _animationPeriod = new();
    private readonly TextBox _styleSheetPath = new();
    private readonly CheckBox _watchStyleSheet = new() { Content = "保存样式表后自动热重载" };
    private readonly ComboBox _shape = new() { ItemsSource = Enum.GetValues<IslandShape>() };
    private readonly TextBox _cornerRadius = new();
    private readonly CheckBox _customBackground = new() { Content = "覆盖岛屿背景色" };
    private readonly TextBox _backgroundColor = new();
    private readonly CheckBox _gradient = new() { Content = "使用线性渐变背景" };
    private readonly TextBox _gradientEndColor = new();
    private readonly CheckBox _shadow = new() { Content = "启用阴影" };
    private readonly TextBox _shadowColor = new();
    private readonly TextBox _shadowBlur = new();
    private readonly TextBox _shadowOffsetX = new();
    private readonly TextBox _shadowOffsetY = new();
    private readonly TextBox _shadowOpacity = new();
    private readonly ComboBox _visibilityAnimation = new() { ItemsSource = Enum.GetValues<VisibilityAnimation>() };
    private readonly ComboBox _emphasisAnimation = new() { ItemsSource = Enum.GetValues<EmphasisAnimation>() };
    private readonly TextBox _emphasisAmount = new();
    private readonly TextBox _emphasisDuration = new();
    private readonly TextBox _visibilityDuration = new();
    private readonly ComboBox _notificationTransition = new() { ItemsSource = Enum.GetValues<NotificationTransition>() };
    private readonly TextBox _notificationTransitionDuration = new();
    private readonly ComboBox _rippleType = new() { ItemsSource = Enum.GetValues<RippleType>() };
    private readonly TextBox _rippleColor = new();
    private readonly TextBox _rippleDuration = new();
    private readonly TextBox _rippleThickness = new();
    private readonly ComboBox _preset = new() { ItemsSource = Enum.GetValues<StylePreset>() };
    private readonly ComboBox _animationPreset = new() { ItemsSource = Enum.GetValues<AnimationPreset>() };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public InjectorSettingsPage()
    {
        Content = BuildContent();
        LoadFromSettings();
    }

    private Control BuildContent()
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(20),
            MaxWidth = 820
        };

        panel.Children.Add(new TextBlock
        {
            Text = "ClassIsland 样式注入器",
            FontSize = 24,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        panel.Children.Add(Section("预设与恢复"));
        panel.Children.Add(Field("样式预设", _preset));
        var presetActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var applyPreset = new Button { Content = "应用预设" };
        applyPreset.Click += (_, _) =>
        {
            if (_preset.SelectedItem is not StylePreset preset)
            {
                return;
            }
            InjectorRuntime.Settings.ApplyPreset(preset);
            LoadFromSettings();
            _status.Text = $"已应用 {preset} 预设。";
        };
        var reset = new Button { Content = "恢复插件默认" };
        reset.Click += (_, _) =>
        {
            InjectorRuntime.Settings.ResetToDefaults();
            LoadFromSettings();
            _status.Text = "已恢复插件默认设置；高级 Overrides.axaml 文件未修改。";
        };
        presetActions.Children.Add(applyPreset);
        presetActions.Children.Add(reset);
        panel.Children.Add(presetActions);
        panel.Children.Add(new TextBlock
        {
            Text = "运行时层直接接管主界面根节点；XAML 覆盖层可使用 Avalonia 选择器重写任意已暴露控件。保存后立即生效，无需重启。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        panel.Children.Add(_enabled);
        panel.Children.Add(Field("不透明度 (0–1)", _opacity));
        panel.Children.Add(Field("缩放 (0.1–5)", _scale));
        panel.Children.Add(Field("旋转角度", _rotation));
        panel.Children.Add(Field("X 偏移", _offsetX));
        panel.Children.Add(Field("Y 偏移", _offsetY));
        panel.Children.Add(_animationEnabled);
        panel.Children.Add(Field("动画类型", _animationMode));
        panel.Children.Add(Field("动画幅度 (0–1)", _animationAmount));
        panel.Children.Add(Field("动画周期（秒）", _animationPeriod));
        panel.Children.Add(Field("动画预设", _animationPreset));
        var animationPresetActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var applyAnimationPreset = new Button { Content = "应用动画预设" };
        applyAnimationPreset.Click += (_, _) =>
        {
            if (_animationPreset.SelectedItem is not AnimationPreset preset)
            {
                return;
            }
            InjectorRuntime.Settings.ApplyAnimationPreset(preset);
            LoadFromSettings();
            _status.Text = $"已应用 {preset} 动画预设；形状、背景和阴影保持不变。";
        };
        animationPresetActions.Children.Add(applyAnimationPreset);
        panel.Children.Add(animationPresetActions);
        panel.Children.Add(Field("覆盖样式表 (.axaml) 的完整路径", _styleSheetPath));
        panel.Children.Add(_watchStyleSheet);
        panel.Children.Add(Section("形状与背景"));
        panel.Children.Add(Field("岛屿形状", _shape));
        panel.Children.Add(Field("圆角半径", _cornerRadius));
        panel.Children.Add(_customBackground);
        panel.Children.Add(Field("背景色 (#AARRGGBB)", _backgroundColor));
        panel.Children.Add(_gradient);
        panel.Children.Add(Field("渐变终止色 (#AARRGGBB)", _gradientEndColor));
        panel.Children.Add(Section("阴影"));
        panel.Children.Add(_shadow);
        panel.Children.Add(Field("阴影颜色 (#AARRGGBB)", _shadowColor));
        panel.Children.Add(Field("阴影模糊", _shadowBlur));
        panel.Children.Add(Field("阴影 X 偏移", _shadowOffsetX));
        panel.Children.Add(Field("阴影 Y 偏移", _shadowOffsetY));
        panel.Children.Add(Field("阴影不透明度 (0–1)", _shadowOpacity));
        panel.Children.Add(Section("出现、消失与强调"));
        panel.Children.Add(Field("主界面显示动画", _visibilityAnimation));
        panel.Children.Add(Field("显示动画时长（秒）", _visibilityDuration));
        panel.Children.Add(Field("提醒强调动画", _emphasisAnimation));
        panel.Children.Add(Field("强调幅度 (0–1)", _emphasisAmount));
        panel.Children.Add(Field("强调时长（秒）", _emphasisDuration));
        panel.Children.Add(Field("提醒遮罩出现/消失动画", _notificationTransition));
        panel.Children.Add(Field("遮罩动画时长（秒）", _notificationTransitionDuration));
        panel.Children.Add(Section("提醒 Ripple"));
        panel.Children.Add(Field("Ripple 类型", _rippleType));
        panel.Children.Add(Field("Ripple 颜色 (#AARRGGBB)", _rippleColor));
        panel.Children.Add(Field("Ripple 时长（秒）", _rippleDuration));
        panel.Children.Add(Field("Ripple 线宽", _rippleThickness));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "保存并应用" };
        apply.Click += (_, _) => SaveAndApply();
        var reload = new Button { Content = "立即重载样式表" };
        reload.Click += (_, _) =>
        {
            InjectorRuntime.ReloadStyleSheet();
            _status.Text = "已请求重载。若样式表有语法错误，ClassIsland 会保留稳定运行状态。";
        };
        actions.Children.Add(apply);
        actions.Children.Add(reload);
        var previewRipple = new Button { Content = "预览 Ripple" };
        previewRipple.Click += (_, _) =>
        {
            SaveAndApply();
            InjectorRuntime.PreviewRipple();
            _status.Text = "正在主界面中心预览当前 Ripple。";
        };
        actions.Children.Add(previewRipple);
        panel.Children.Add(actions);
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock
        {
            Text = "提示：默认样式表及 settings.json 都在此插件的配置目录。可编辑样式表中的 Grid#GridRoot、Panel#WorkingRoot、StackPanel#StackPanelRootContainer 和 Window.classisland-injector 等选择器。详细示例见插件 README。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.75
        });
        return new ScrollViewer { Content = panel };
    }

    private static Control Field(string label, Control value)
    {
        value.MinWidth = 260;
        value.HorizontalAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,*")
        };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static Control Section(string text) => new TextBlock
    {
        Text = text,
        FontSize = 18,
        FontWeight = Avalonia.Media.FontWeight.SemiBold,
        Margin = new Thickness(0, 14, 0, 0)
    };

    private void LoadFromSettings()
    {
        var settings = InjectorRuntime.Settings;
        _enabled.IsChecked = settings.Enabled;
        _opacity.Text = settings.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _scale.Text = settings.Scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _rotation.Text = settings.Rotation.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _offsetX.Text = settings.OffsetX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _offsetY.Text = settings.OffsetY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _animationEnabled.IsChecked = settings.AnimationEnabled;
        _animationMode.SelectedItem = settings.AnimationMode;
        _animationAmount.Text = settings.AnimationAmount.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _animationPeriod.Text = settings.AnimationPeriodSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _styleSheetPath.Text = settings.StyleSheetPath;
        _watchStyleSheet.IsChecked = settings.WatchStyleSheet;
        _shape.SelectedItem = settings.Shape;
        _cornerRadius.Text = Number(settings.CornerRadius);
        _customBackground.IsChecked = settings.CustomBackgroundEnabled;
        _backgroundColor.Text = settings.BackgroundColor;
        _gradient.IsChecked = settings.GradientEnabled;
        _gradientEndColor.Text = settings.GradientEndColor;
        _shadow.IsChecked = settings.ShadowEnabled;
        _shadowColor.Text = settings.ShadowColor;
        _shadowBlur.Text = Number(settings.ShadowBlur);
        _shadowOffsetX.Text = Number(settings.ShadowOffsetX);
        _shadowOffsetY.Text = Number(settings.ShadowOffsetY);
        _shadowOpacity.Text = Number(settings.ShadowOpacity);
        _visibilityAnimation.SelectedItem = settings.VisibilityAnimation;
        _visibilityDuration.Text = Number(settings.VisibilityDurationSeconds);
        _emphasisAnimation.SelectedItem = settings.EmphasisAnimation;
        _emphasisAmount.Text = Number(settings.EmphasisAmount);
        _emphasisDuration.Text = Number(settings.EmphasisDurationSeconds);
        _notificationTransition.SelectedItem = settings.NotificationTransition;
        _notificationTransitionDuration.Text = Number(settings.NotificationTransitionDurationSeconds);
        _rippleType.SelectedItem = settings.RippleType;
        _rippleColor.Text = settings.RippleColor;
        _rippleDuration.Text = Number(settings.RippleDurationSeconds);
        _rippleThickness.Text = Number(settings.RippleThickness);
    }

    private void SaveAndApply()
    {
        if (!TryNumber(_opacity, out var opacity) || !TryNumber(_scale, out var scale) ||
            !TryNumber(_rotation, out var rotation) || !TryNumber(_offsetX, out var offsetX) ||
            !TryNumber(_offsetY, out var offsetY) || !TryNumber(_animationAmount, out var animationAmount) ||
            !TryNumber(_animationPeriod, out var animationPeriod) || !TryNumber(_cornerRadius, out var cornerRadius) ||
            !TryNumber(_shadowBlur, out var shadowBlur) || !TryNumber(_shadowOffsetX, out var shadowOffsetX) ||
            !TryNumber(_shadowOffsetY, out var shadowOffsetY) || !TryNumber(_shadowOpacity, out var shadowOpacity) ||
            !TryNumber(_emphasisAmount, out var emphasisAmount) || !TryNumber(_emphasisDuration, out var emphasisDuration) ||
            !TryNumber(_visibilityDuration, out var visibilityDuration) ||
            !TryNumber(_notificationTransitionDuration, out var notificationTransitionDuration) ||
            !TryNumber(_rippleDuration, out var rippleDuration) || !TryNumber(_rippleThickness, out var rippleThickness))
        {
            _status.Text = "请输入有效数字后再保存。";
            return;
        }

        var settings = InjectorRuntime.Settings;
        settings.BeginUpdate();
        try
        {
        settings.Enabled = _enabled.IsChecked == true;
        settings.Opacity = opacity;
        settings.Scale = scale;
        settings.Rotation = rotation;
        settings.OffsetX = offsetX;
        settings.OffsetY = offsetY;
        settings.AnimationEnabled = _animationEnabled.IsChecked == true;
        settings.AnimationMode = _animationMode.SelectedItem is IslandAnimationMode mode ? mode : IslandAnimationMode.None;
        settings.AnimationAmount = animationAmount;
        settings.AnimationPeriodSeconds = animationPeriod;
        settings.StyleSheetPath = _styleSheetPath.Text ?? string.Empty;
        settings.WatchStyleSheet = _watchStyleSheet.IsChecked == true;
        settings.Shape = _shape.SelectedItem is IslandShape shape ? shape : IslandShape.HostDefault;
        settings.CornerRadius = cornerRadius;
        settings.CustomBackgroundEnabled = _customBackground.IsChecked == true;
        settings.BackgroundColor = _backgroundColor.Text ?? string.Empty;
        settings.GradientEnabled = _gradient.IsChecked == true;
        settings.GradientEndColor = _gradientEndColor.Text ?? string.Empty;
        settings.ShadowEnabled = _shadow.IsChecked == true;
        settings.ShadowColor = _shadowColor.Text ?? string.Empty;
        settings.ShadowBlur = shadowBlur;
        settings.ShadowOffsetX = shadowOffsetX;
        settings.ShadowOffsetY = shadowOffsetY;
        settings.ShadowOpacity = shadowOpacity;
        settings.VisibilityAnimation = _visibilityAnimation.SelectedItem is VisibilityAnimation visibilityAnimation ? visibilityAnimation : VisibilityAnimation.None;
        settings.VisibilityDurationSeconds = visibilityDuration;
        settings.EmphasisAnimation = _emphasisAnimation.SelectedItem is EmphasisAnimation emphasisAnimation ? emphasisAnimation : EmphasisAnimation.None;
        settings.EmphasisAmount = emphasisAmount;
        settings.EmphasisDurationSeconds = emphasisDuration;
        settings.NotificationTransition = _notificationTransition.SelectedItem is NotificationTransition notificationTransition ? notificationTransition : NotificationTransition.HostDefault;
        settings.NotificationTransitionDurationSeconds = notificationTransitionDuration;
        settings.RippleType = _rippleType.SelectedItem is RippleType rippleType ? rippleType : RippleType.None;
        settings.RippleColor = _rippleColor.Text ?? string.Empty;
        settings.RippleDurationSeconds = rippleDuration;
        settings.RippleThickness = rippleThickness;
        }
        finally
        {
            settings.EndUpdate();
        }
        _status.Text = "已保存并应用。样式表有更改时会自动热重载。";
    }

    private static bool TryNumber(TextBox textBox, out double value)
    {
        return double.TryParse(textBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value) ||
            double.TryParse(textBox.Text, out value);
    }

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
