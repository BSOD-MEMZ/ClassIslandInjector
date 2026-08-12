using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 色相 / 饱和度调整窗口（仿 Photoshop 的「色相/饱和度」面板）：
/// 顶部预设下拉（默认值 / 增加饱和度等滤镜预设），下面每行 = 标签 + 数字框（精确）+ 滑块（快速），
/// 底部「预览」开关 + 「确定 / 取消」按钮。非模态，不阻塞主编辑窗口。
/// </summary>
internal sealed class HslAdjustWindow : LayerFilterWindowBase
{
    /// <summary>当前打开的实例（禁止多开）。</summary>
    public static HslAdjustWindow? Current { get; private set; }

    private readonly FilterRowControl _hueRow = new("色相", -180, 180, 1);
    private readonly FilterRowControl _satRow = new("饱和度", -100, 100, 1);
    private readonly FilterRowControl _lightRow = new("明度", -100, 100, 1);
    private readonly ComboBox _presetBox = new() { MinWidth = 200 };

    private static readonly (string Name, double Hue, double Sat, double Light)[] Presets =
    [
        ("默认值", 0, 0, 0),
        ("增加饱和度", 0, 40, 0),
        ("降低饱和度", 0, -40, 0),
        ("鲜艳", 0, 60, 5),
        ("褪色", 0, -50, 8),
        ("黑白", 0, -100, 0),
        ("复古", 18, -25, -8),
        ("暖色调", -30, 10, 5),
        ("冷色调", 145, 5, 5),
        ("反转色相", 180, 0, 0)
    ];

    public HslAdjustWindow(WallpaperLayerEditorWindow editor)
        : base(editor, "色相 / 饱和度", 420, 380)
    {
        Current = this;
        SyncFromEditor();
    }

    protected override LayerFilterWindowBase? CurrentWindow
    {
        get => Current;
        set => Current = (HslAdjustWindow?)value;
    }

    protected override int ValueCount => 3;

    protected override double GetValue(WallpaperLayerItem layer, int index) => index switch
    {
        0 => layer.HueShift,
        1 => layer.SaturationAdjust,
        _ => layer.LightnessAdjust
    };

    protected override void SetValue(WallpaperLayerItem layer, int index, double value)
    {
        switch (index)
        {
            case 0:
                layer.HueShift = value;
                break;
            case 1:
                layer.SaturationAdjust = value;
                break;
            default:
                layer.LightnessAdjust = value;
                break;
        }
    }

    protected override double[] ReadControlsToValues() =>
        [_hueRow.Value, _satRow.Value, _lightRow.Value];

    protected override void ReadValuesToControls(double[] values)
    {
        _hueRow.Value = values[0];
        _satRow.Value = values[1];
        _lightRow.Value = values[2];
    }

    protected override void SyncEnabled(bool hasImageLayer)
    {
        _presetBox.IsEnabled = hasImageLayer;
        _hueRow.IsEnabled = hasImageLayer;
        _satRow.IsEnabled = hasImageLayer;
        _lightRow.IsEnabled = hasImageLayer;
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
        panel.Children.Add(_hueRow);
        panel.Children.Add(_satRow);
        panel.Children.Add(_lightRow);
        WireRow(_hueRow);
        WireRow(_satRow);
        WireRow(_lightRow);
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
            _hueRow.Value = p.Hue;
            _satRow.Value = p.Sat;
            _lightRow.Value = p.Light;
            OnValuesChanged();
        }
    }
}
