using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 背景效果窗口：罗列与设置页「底色填充 / 阴影 / 边框」完全一致的设置项，
/// 只作用于 ClassIsland 背景。改动实时保存并应用到运行时。
/// 同一时刻只允许一个实例（双击背景行或图层面板「效果选项」按钮打开）。
/// </summary>
internal sealed class BackgroundEffectsWindow : MyWindow
{
    /// <summary>当前打开的实例（禁止多开）。</summary>
    public static BackgroundEffectsWindow? Current { get; private set; }

    private bool _updating;

    // 形状
    private readonly ComboBox _shapeBox = new() { MinWidth = 140 };
    private readonly EffSpin _cornerRadiusSpin = new(0, 20, 1, "0");
    // 底色
    private readonly ToggleSwitch _customBackgroundToggle = Toggle();
    private readonly ColorPicker _backgroundColorPicker = Picker();
    private readonly ToggleSwitch _dynamicBackgroundToggle = Toggle();
    private readonly ToggleSwitch _gradientToggle = Toggle();
    private readonly ComboBox _gradientDirectionBox = new() { MinWidth = 140 };
    private readonly ColorPicker _gradientEndPicker = Picker();
    // 阴影
    private readonly ToggleSwitch _shadowToggle = Toggle();
    private readonly ToggleSwitch _dynamicShadowToggle = Toggle();
    private readonly ColorPicker _shadowColorPicker = Picker();
    private readonly EffSpin _shadowBlurSpin = new(0, 200, 1, "0");
    private readonly EffSpin _shadowOffsetXSpin = new(-200, 200, 1, "0");
    private readonly EffSpin _shadowOffsetYSpin = new(-200, 200, 1, "0");
    private readonly Slider _shadowOpacitySlider = new()
    {
        Minimum = 0,
        Maximum = 1,
        TickFrequency = 0.05,
        IsSnapToTickEnabled = true,
        Width = 140,
        VerticalAlignment = VerticalAlignment.Center
    };
    // 边框
    private readonly ToggleSwitch _borderToggle = Toggle();
    private readonly ToggleSwitch _dynamicBorderToggle = Toggle();
    private readonly ColorPicker _borderColorPicker = Picker();
    private readonly EffSpin _borderThicknessSpin = new(0.25, 20, 0.25, "0.##");

    public BackgroundEffectsWindow()
    {
        Title = "背景效果";
        Width = 380;
        Height = 600;
        MinWidth = 320;
        MinHeight = 420;
        EditorMica.EnableMica(this);

        _shapeBox.ItemsSource = ShapeChoices;
        _shapeBox.SelectedItem = ShapeChoices[0];
        _gradientDirectionBox.ItemsSource = GradientDirectionChoices;
        _gradientDirectionBox.SelectedItem = GradientDirectionChoices[0];

        BuildContent();
        WireEvents();
        LoadFromSettings();

        Current = this;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        };
    }

    private void BuildContent()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "背景效果",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });

        panel.Children.Add(Subtitle("\uE62F", "形状"));
        panel.Children.Add(Row("形状", _shapeBox));
        panel.Children.Add(Row("圆角半径", _cornerRadiusSpin));

        panel.Children.Add(Subtitle("\uE520", "底色填充"));
        panel.Children.Add(Row("启用自定义底色", _customBackgroundToggle));
        panel.Children.Add(Row("背景色", _backgroundColorPicker));
        panel.Children.Add(Row("动态专辑封面取色", _dynamicBackgroundToggle));
        panel.Children.Add(Row("线性渐变", _gradientToggle));
        panel.Children.Add(Row("渐变方向", _gradientDirectionBox));
        panel.Children.Add(Row("渐变终止色", _gradientEndPicker));

        panel.Children.Add(Subtitle("\uE472", "阴影"));
        panel.Children.Add(Row("启用阴影", _shadowToggle));
        panel.Children.Add(Row("动态取色", _dynamicShadowToggle));
        panel.Children.Add(Row("阴影颜色", _shadowColorPicker));
        panel.Children.Add(Row("模糊", _shadowBlurSpin));
        panel.Children.Add(Row("水平偏移", _shadowOffsetXSpin));
        panel.Children.Add(Row("垂直偏移", _shadowOffsetYSpin));
        panel.Children.Add(Row("不透明度", _shadowOpacitySlider));

        panel.Children.Add(Subtitle("\uE254", "边框"));
        panel.Children.Add(Row("启用边框", _borderToggle));
        panel.Children.Add(Row("动态取色", _dynamicBorderToggle));
        panel.Children.Add(Row("边框颜色", _borderColorPicker));
        panel.Children.Add(Row("线宽", _borderThicknessSpin));

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
    }

    private void WireEvents()
    {
        _shapeBox.SelectionChanged += (_, _) => Apply(s => s.Shape = Selected(_shapeBox, IslandShape.HostDefault));
        _cornerRadiusSpin.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == NumericUpDown.ValueProperty)
            {
                Apply(s => s.CornerRadius = _cornerRadiusSpin.DoubleValue);
            }
        };
        _customBackgroundToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.CustomBackgroundEnabled = _customBackgroundToggle.IsChecked == true);
            }
        };
        _backgroundColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property?.Name == "Color")
            {
                Apply(s => s.BackgroundColor = _backgroundColorPicker.Color.ToString());
            }
        };
        _dynamicBackgroundToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.DynamicBackgroundColorEnabled = _dynamicBackgroundToggle.IsChecked == true);
            }
        };
        _gradientToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.GradientEnabled = _gradientToggle.IsChecked == true);
            }
        };
        _gradientDirectionBox.SelectionChanged += (_, _) => Apply(s => s.GradientDirection = Selected(_gradientDirectionBox, GradientDirection.TopLeftToBottomRight));
        _gradientEndPicker.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property?.Name == "Color")
            {
                Apply(s => s.GradientEndColor = _gradientEndPicker.Color.ToString());
            }
        };
        _shadowToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.ShadowEnabled = _shadowToggle.IsChecked == true);
            }
        };
        _dynamicShadowToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.DynamicShadowColorEnabled = _dynamicShadowToggle.IsChecked == true);
            }
        };
        _shadowColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property?.Name == "Color")
            {
                Apply(s => s.ShadowColor = _shadowColorPicker.Color.ToString());
            }
        };
        _shadowBlurSpin.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == NumericUpDown.ValueProperty)
            {
                Apply(s => s.ShadowBlur = _shadowBlurSpin.DoubleValue);
            }
        };
        _shadowOffsetXSpin.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == NumericUpDown.ValueProperty)
            {
                Apply(s => s.ShadowOffsetX = _shadowOffsetXSpin.DoubleValue);
            }
        };
        _shadowOffsetYSpin.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == NumericUpDown.ValueProperty)
            {
                Apply(s => s.ShadowOffsetY = _shadowOffsetYSpin.DoubleValue);
            }
        };
        _shadowOpacitySlider.ValueChanged += (_, _) => Apply(s => s.ShadowOpacity = _shadowOpacitySlider.Value);
        _borderToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.BorderEnabled = _borderToggle.IsChecked == true);
            }
        };
        _dynamicBorderToggle.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                Apply(s => s.DynamicBorderColorEnabled = _dynamicBorderToggle.IsChecked == true);
            }
        };
        _borderColorPicker.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property?.Name == "Color")
            {
                Apply(s => s.BorderColor = _borderColorPicker.Color.ToString());
            }
        };
        _borderThicknessSpin.PropertyChanged += (_, e) =>
        {
            if (!_updating && e.Property == NumericUpDown.ValueProperty)
            {
                Apply(s => s.BorderThickness = _borderThicknessSpin.DoubleValue);
            }
        };
    }

    /// <summary>把设置改动批量应用到运行时并保存。</summary>
    private void Apply(Action<InjectorSettings> edit)
    {
        var s = InjectorRuntime.Settings;
        s.BeginUpdate();
        edit(s);
        s.EndUpdate();
        InjectorRuntime.SaveAndApply();
        SyncEnabled();
    }

    private void LoadFromSettings()
    {
        _updating = true;
        try
        {
            var s = InjectorRuntime.Settings;
            _shapeBox.SelectedItem = ShapeChoices.FirstOrDefault(c => c.Value == s.Shape) ?? ShapeChoices[0];
            _cornerRadiusSpin.DoubleValue = s.CornerRadius;
            _customBackgroundToggle.IsChecked = s.CustomBackgroundEnabled;
            _backgroundColorPicker.Color = ReadColor(s.BackgroundColor, Color.FromArgb(0xCC, 0x20, 0x20, 0x20));
            _dynamicBackgroundToggle.IsChecked = s.DynamicBackgroundColorEnabled;
            _gradientToggle.IsChecked = s.GradientEnabled;
            _gradientDirectionBox.SelectedItem = GradientDirectionChoices.FirstOrDefault(c => c.Value == s.GradientDirection) ?? GradientDirectionChoices[0];
            _gradientEndPicker.Color = ReadColor(s.GradientEndColor, Color.FromArgb(0xCC, 0x40, 0x40, 0xA0));
            _shadowToggle.IsChecked = s.ShadowEnabled;
            _dynamicShadowToggle.IsChecked = s.DynamicShadowColorEnabled;
            _shadowColorPicker.Color = ReadColor(s.ShadowColor, Colors.Black);
            _shadowBlurSpin.DoubleValue = s.ShadowBlur;
            _shadowOffsetXSpin.DoubleValue = s.ShadowOffsetX;
            _shadowOffsetYSpin.DoubleValue = s.ShadowOffsetY;
            _shadowOpacitySlider.Value = s.ShadowOpacity;
            _borderToggle.IsChecked = s.BorderEnabled;
            _dynamicBorderToggle.IsChecked = s.DynamicBorderColorEnabled;
            _borderColorPicker.Color = ReadColor(s.BorderColor, Colors.White);
            _borderThicknessSpin.DoubleValue = s.BorderThickness;
            SyncEnabled();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>按开关启停从属控件（与设置页一致：手动颜色在「动态取色」开启时禁用等）。</summary>
    private void SyncEnabled()
    {
        var bg = _customBackgroundToggle.IsChecked == true;
        _backgroundColorPicker.IsEnabled = bg && _dynamicBackgroundToggle.IsChecked != true;
        _dynamicBackgroundToggle.IsEnabled = bg;
        _gradientToggle.IsEnabled = bg;
        _gradientDirectionBox.IsEnabled = bg && _gradientToggle.IsChecked == true;
        _gradientEndPicker.IsEnabled = bg && _gradientToggle.IsChecked == true;
        var sh = _shadowToggle.IsChecked == true;
        _dynamicShadowToggle.IsEnabled = sh;
        _shadowColorPicker.IsEnabled = sh && _dynamicShadowToggle.IsChecked != true;
        _shadowBlurSpin.IsEnabled = sh;
        _shadowOffsetXSpin.IsEnabled = sh;
        _shadowOffsetYSpin.IsEnabled = sh;
        _shadowOpacitySlider.IsEnabled = sh;
        var bd = _borderToggle.IsChecked == true;
        _dynamicBorderToggle.IsEnabled = bd;
        _borderColorPicker.IsEnabled = bd && _dynamicBorderToggle.IsChecked != true;
        _borderThicknessSpin.IsEnabled = bd;
    }

    private static Color ReadColor(string text, Color fallback) => ColorUtil.Parse(text, fallback);

    private static T Selected<T>(ComboBox box, T fallback) => box.SelectedItem is Pick<T> choice ? choice.Value : fallback;

    private static ToggleSwitch Toggle() => new() { OnContent = "开", OffContent = "关" };

    private static ColorPicker Picker() => new() { VerticalAlignment = VerticalAlignment.Center };

    private static IconText Subtitle(string glyph, string text) => new()
    {
        Glyph = glyph,
        Text = text,
        Margin = new Thickness(0, 10, 0, 2),
        Opacity = 0.85
    };

    /// <summary>设置项行：左标签 + 右控件（统一行高）。</summary>
    private static Control Row(string label, Control control)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 34,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9,
                    Margin = new Thickness(0, 0, 10, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                },
                control
            }
        };
        Grid.SetColumn(control, 1);
        return row;
    }

    private static readonly Pick<IslandShape>[] ShapeChoices =
    [
        new(IslandShape.HostDefault, "跟随 ClassIsland 默认"),
        new(IslandShape.Rectangle, "直角矩形"),
        new(IslandShape.RoundedRectangle, "圆角矩形"),
        new(IslandShape.Capsule, "胶囊")
    ];

    private static readonly Pick<GradientDirection>[] GradientDirectionChoices =
    [
        new(GradientDirection.TopLeftToBottomRight, "左上 → 右下"),
        new(GradientDirection.TopToBottom, "上 → 下"),
        new(GradientDirection.LeftToRight, "左 → 右"),
        new(GradientDirection.BottomLeftToTopRight, "左下 → 右上"),
        new(GradientDirection.BottomToTop, "下 → 上"),
        new(GradientDirection.RightToLeft, "右 → 左"),
        new(GradientDirection.TopRightToBottomLeft, "右上 → 左下"),
        new(GradientDirection.BottomRightToTopLeft, "右下 → 左上")
    ];

    private sealed record Pick<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>效果窗口用的数值框（必须把 StyleKey 指回基类，否则 FAUI 隐式主题找不到导致不可见）。</summary>
    private sealed class EffSpin : NumericUpDown
    {
        protected override Type StyleKeyOverride => typeof(NumericUpDown);

        public EffSpin(double minimum, double maximum, double increment, string format)
        {
            Minimum = (decimal)minimum;
            Maximum = (decimal)maximum;
            Increment = (decimal)increment;
            FormatString = format;
            Value = (decimal)minimum;
            Width = 130;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalContentAlignment = HorizontalAlignment.Right;
        }

        public double DoubleValue
        {
            get => (double)(Value ?? 0);
            set => Value = (decimal)Math.Clamp(value, (double)Minimum, (double)Maximum);
        }
    }
}
