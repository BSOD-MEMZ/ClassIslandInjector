using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;

namespace ClassIslandInjector.Automation;

/// <summary>
/// 下拉框选项（避免依赖设置页的私有 Choice&lt;T&gt;）。
/// </summary>
internal sealed class Option(object value, string text)
{
    public object Value { get; } = value;

    public string Text { get; } = text;

    public override string ToString() => Text;
}

/// <summary>
/// 「修改设置」行动的设置控件：根据设置项的元数据动态渲染对应的值编辑器
/// （开关/数字/文本/下拉框）。
/// </summary>
public sealed class SetInjectorSettingActionSettingsControl : ActionSettingsControlBase<SetInjectorSettingActionSettings>
{
    private readonly StackPanel _panel = new() { Spacing = 8 };

    public SetInjectorSettingActionSettingsControl()
    {
        Content = _panel;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        _panel.Children.Clear();

        if (string.IsNullOrEmpty(Settings.PropertyName))
        {
            _panel.Children.Add(new TextBlock
            {
                Text = "请先从「添加行动」菜单选择要修改的设置项。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            return;
        }

        var spec = InjectorSettingCatalog.Find(Settings.PropertyName);
        if (spec == null)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = $"未知设置项：{Settings.PropertyName}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            return;
        }

        // 让行动项显示为设置项的中文名（行动行本身已显示“强调幅度”之类，面板不再重复）。
        ChangeActionName(spec.DisplayName);

        // 直接显示值编辑器，说明文字收进悬停提示，避免重复标题与冗长描述。
        var editor = BuildEditor(spec);
        if (!string.IsNullOrEmpty(spec.Description))
        {
            ToolTip.SetTip(editor, spec.Description);
        }

        _panel.Children.Add(editor);
    }

    /// <summary>
    /// 把原始值（可能是 JsonElement）转换为设置项的目标 CLR 类型。
    /// </summary>
    private static object? Normalize(InjectorSettingSpec spec, object? raw)
    {
        if (raw == null)
        {
            return null;
        }

        var targetType = spec.Kind switch
        {
            SettingValueKind.Bool => typeof(bool),
            SettingValueKind.Double => typeof(double),
            SettingValueKind.Int => typeof(int),
            SettingValueKind.String => typeof(string),
            SettingValueKind.Enum => spec.EnumType,
            _ => null
        };
        return targetType == null ? raw : InjectorSettingReflection.ConvertTo(targetType, raw);
    }

    private Control BuildEditor(InjectorSettingSpec spec)
    {
        // 优先使用已保存的值，否则以插件当前值作为默认值并回写，
        // 保证新增行动即便不编辑也有明确语义。
        var current = Normalize(spec, Settings.Value) ?? InjectorSettingReflection.GetValue(spec.PropertyName);
        if (Settings.Value == null && current != null)
        {
            Settings.Value = current;
        }

        switch (spec.Kind)
        {
            case SettingValueKind.Bool:
            {
                var toggle = new ToggleSwitch
                {
                    OnContent = "开",
                    OffContent = "关",
                    IsChecked = current is true,
                    VerticalAlignment = VerticalAlignment.Center
                };
                toggle.IsCheckedChanged += (_, _) => Settings.Value = toggle.IsChecked == true;
                return toggle;
            }
            case SettingValueKind.Double:
            case SettingValueKind.Int:
            {
                var isInt = spec.Kind == SettingValueKind.Int;
                var spin = new NumericUpDown
                {
                    Minimum = (decimal)spec.Minimum,
                    Maximum = (decimal)spec.Maximum,
                    Increment = isInt ? 1 : (decimal)Math.Max((spec.Maximum - spec.Minimum) / 100, 0.01),
                    FormatString = spec.NumberFormat,
                    // 面板中只有这一个控件时，内容左对齐更贴近阅读习惯。
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Width = 220
                };
                var initial = current switch
                {
                    double d => (decimal)d,
                    int i => (decimal)i,
                    _ => (decimal)spec.Minimum
                };
                spin.Value = initial;
                spin.ValueChanged += (_, e) =>
                {
                    var value = e.NewValue;
                    Settings.Value = value.HasValue
                        ? isInt ? (int)Math.Round(value.Value) : (double)value.Value
                        : null;
                };
                return spin;
            }
            case SettingValueKind.String:
            {
                var box = new TextBox
                {
                    Text = current?.ToString() ?? string.Empty,
                    MinWidth = 220,
                    Watermark = "请输入值"
                };
                box.TextChanged += (_, _) => Settings.Value = box.Text ?? string.Empty;
                return box;
            }
            case SettingValueKind.Enum:
            {
                var options = spec.EnumOptions
                    .Select(kv => new Option(kv.Key, kv.Value))
                    .ToList();
                var combo = new ComboBox
                {
                    ItemsSource = options,
                    MinWidth = 220,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                combo.SelectedItem = options.FirstOrDefault(o =>
                    Equals(o.Value, current) || Equals(o.Value, Normalize(spec, current)));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is Option option)
                    {
                        Settings.Value = option.Value;
                    }
                };
                return combo;
            }
            default:
                return new TextBlock { Text = "不支持的设置类型。", Opacity = 0.7 };
        }
    }
}

/// <summary>
/// 「切换用户预设」行动的设置控件：从已保存的用户预设列表中选择一个。
/// </summary>
public sealed class SwitchPresetActionSettingsControl : ActionSettingsControlBase<SwitchPresetActionSettings>
{
    private readonly StackPanel _panel = new() { Spacing = 8 };
    private readonly ComboBox _combo = new() { MinWidth = 220, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _hint = new() { Text = "尚未保存任何用户预设，请先在插件设置页保存。", Opacity = 0.7 };

    public SwitchPresetActionSettingsControl()
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var saveButton = new Button { Content = "把当前状态保存为新预设" };
        var refreshButton = new Button { Content = "刷新列表" };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(refreshButton);

        saveButton.Click += (_, _) => SaveCurrentAsPresetAsync();
        refreshButton.Click += (_, _) => Refresh();

        _combo.SelectionChanged += (_, _) =>
        {
            if (_combo.SelectedItem is string name)
            {
                Settings.PresetName = name;
                ChangeActionName($"切换预设：{name}");
            }
        };

        _panel.Children.Add(_combo);
        _panel.Children.Add(_hint);
        _panel.Children.Add(buttons);
        Content = _panel;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InjectorRuntime.PresetsChanged += OnPresetsChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        InjectorRuntime.PresetsChanged -= OnPresetsChanged;
    }

    private void OnPresetsChanged(object? sender, EventArgs e)
    {
        // 预设列表可能在设置页中被修改，回到 UI 线程刷新。
        Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var names = InjectorRuntime.GetPresetNames();
        _combo.ItemsSource = names;

        if (names.Count == 0)
        {
            _combo.SelectedItem = null;
            _hint.IsVisible = true;
            return;
        }

        _hint.IsVisible = false;
        var selected = _combo.SelectedItem as string;
        if (string.IsNullOrEmpty(selected) || !names.Contains(selected))
        {
            _combo.SelectedItem = names.FirstOrDefault(n => n == Settings.PresetName) ?? names[0];
        }
    }

    private async void SaveCurrentAsPresetAsync()
    {
        var name = await PromptPresetNameAsync();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        InjectorRuntime.SavePreset(name);
        Refresh();
        if (_combo.SelectedItem is not string)
        {
            _combo.SelectedItem = name;
        }
    }

    private async Task<string?> PromptPresetNameAsync()
    {
        var input = new TextBox { Watermark = "预设名称", MinWidth = 260 };
        var dialog = new FluentAvalonia.UI.Controls.ContentDialog
        {
            Title = "保存为预设",
            Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "把当前全部设置保存为命名预设：" }, input } },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = FluentAvalonia.UI.Controls.ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result != FluentAvalonia.UI.Controls.ContentDialogResult.Primary)
        {
            return null;
        }

        return input.Text;
    }
}

/// <summary>
/// 「应用样式预设」行动的设置控件。
/// </summary>
public sealed class ApplyStylePresetActionSettingsControl : ActionSettingsControlBase<ApplyStylePresetActionSettings>
{
    private static readonly Option[] Options =
    [
        new(StylePreset.GlassCapsule, "玻璃主题"),
        new(StylePreset.NeonPulse, "霓虹主题"),
        new(StylePreset.MaimaiHanabi, "花火主题"),
        new(StylePreset.Minimal, "极简主题"),
    ];

    private readonly ComboBox _combo = new()
    {
        ItemsSource = Options,
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };

    public ApplyStylePresetActionSettingsControl()
    {
        _combo.SelectionChanged += (_, _) =>
        {
            if (_combo.SelectedItem is Option option && option.Value is StylePreset preset)
            {
                Settings.Preset = preset;
                ChangeActionName($"样式预设：{option.Text}");
            }
        };

        Content = _combo;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 注意：Settings 只能在附加到可视化树后访问（构造函数中访问会抛异常）。
        _combo.SelectedItem = Options.FirstOrDefault(o => Equals(o.Value, Settings.Preset)) ?? Options[0];
    }
}

/// <summary>
/// 「应用动画预设」行动的设置控件。
/// </summary>
public sealed class ApplyAnimationPresetActionSettingsControl : ActionSettingsControlBase<ApplyAnimationPresetActionSettings>
{
    private static readonly Option[] Options =
    [
        new(AnimationPreset.Still, "静止"),
        new(AnimationPreset.SoftBreathe, "柔和呼吸"),
        new(AnimationPreset.GentleFloat, "轻柔浮动"),
        new(AnimationPreset.DynamicWave, "动态波浪"),
        new(AnimationPreset.AlertShake, "提醒摇晃"),
        new(AnimationPreset.HanabiCelebration, "花火庆祝"),
    ];

    private readonly ComboBox _combo = new()
    {
        ItemsSource = Options,
        MinWidth = 220,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };

    public ApplyAnimationPresetActionSettingsControl()
    {
        _combo.SelectionChanged += (_, _) =>
        {
            if (_combo.SelectedItem is Option option && option.Value is AnimationPreset preset)
            {
                Settings.Preset = preset;
                ChangeActionName($"动画预设：{option.Text}");
            }
        };

        Content = _combo;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 注意：Settings 只能在附加到可视化树后访问（构造函数中访问会抛异常）。
        _combo.SelectedItem = Options.FirstOrDefault(o => Equals(o.Value, Settings.Preset)) ?? Options[0];
    }
}
