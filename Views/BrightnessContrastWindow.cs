using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ClassIslandInjector.Views;

/// <summary>
/// 亮度 / 对比度调整窗口：两个「数字框 + 滑块」行 + 预设下拉 + 预览开关 + 确定 / 取消。
/// 非模态，实时预览逐像素处理效果。
/// </summary>
internal sealed class BrightnessContrastWindow : LayerFilterWindowBase
{
    /// <summary>当前打开的实例（禁止多开）。</summary>
    public static BrightnessContrastWindow? Current { get; private set; }

    private readonly FilterRowControl _brightnessRow = new("亮度", -100, 100, 1);
    private readonly FilterRowControl _contrastRow = new("对比度", -100, 100, 1);
    private readonly ComboBox _presetBox = new() { MinWidth = 200 };

    private static readonly (string Name, double Brightness, double Contrast)[] Presets =
    [
        ("默认值", 0, 0),
        ("提亮", 30, 0),
        ("压暗", -30, 0),
        ("明亮", 25, 15),
        ("高对比", 0, 40),
        ("低对比", 0, -40),
        ("柔光", -10, -10)
    ];

    public BrightnessContrastWindow(WallpaperLayerEditorWindow editor)
        : base(editor, "亮度 / 对比度", 420, 340)
    {
        Current = this;
        SyncFromEditor();
    }

    protected override LayerFilterWindowBase? CurrentWindow
    {
        get => Current;
        set => Current = (BrightnessContrastWindow?)value;
    }

    protected override int ValueCount => 2;

    protected override double GetValue(WallpaperLayerItem layer, int index) =>
        index == 0 ? layer.Brightness : layer.Contrast;

    protected override void SetValue(WallpaperLayerItem layer, int index, double value)
    {
        if (index == 0)
        {
            layer.Brightness = value;
        }
        else
        {
            layer.Contrast = value;
        }
    }

    protected override double[] ReadControlsToValues() =>
        [_brightnessRow.Value, _contrastRow.Value];

    protected override void ReadValuesToControls(double[] values)
    {
        _brightnessRow.Value = values[0];
        _contrastRow.Value = values[1];
    }

    protected override void SyncEnabled(bool hasImageLayer)
    {
        _presetBox.IsEnabled = hasImageLayer;
        _brightnessRow.IsEnabled = hasImageLayer;
        _contrastRow.IsEnabled = hasImageLayer;
    }

    protected override void BuildContentRows(StackPanel panel)
    {
        _presetBox.ItemsSource = Presets.Select(p => p.Name).ToList();
        _presetBox.SelectedIndex = 0;
        _presetBox.SelectionChanged += (_, _) =>
        {
            if (Updating)
            {
                return;
            }

            ApplyPreset();
        };
        panel.Children.Add(PresetRow(_presetBox));
        panel.Children.Add(_brightnessRow);
        panel.Children.Add(_contrastRow);
        WireRow(_brightnessRow);
        WireRow(_contrastRow);
    }

    private void WireRow(FilterRowControl row)
    {
        row.Slider.PropertyChanged += (_, e) =>
        {
            if (!Updating && e.Property == RangeBase.ValueProperty)
            {
                OnValuesChanged();
            }
        };
    }

    private void ApplyPreset()
    {
        if (_presetBox.SelectedIndex >= 0 && _presetBox.SelectedIndex < Presets.Length)
        {
            var p = Presets[_presetBox.SelectedIndex];
            _brightnessRow.Value = p.Brightness;
            _contrastRow.Value = p.Contrast;
            OnValuesChanged();
        }
    }
}
