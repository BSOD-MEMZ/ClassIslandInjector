using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 色相 / 饱和度调整窗口（仿 Photoshop 的「色相/饱和度」面板）：
/// 色相、饱和度、明度三个滑块实时应用到编辑器当前选中的图片图层。
/// 模型窗口（不阻塞主编辑窗口），便于在画布上直接观察逐像素处理效果。
/// 同一时刻只允许一个实例。
/// </summary>
internal sealed class HslAdjustWindow : MyWindow
{
    /// <summary>当前打开的实例（禁止多开）。</summary>
    public static HslAdjustWindow? Current { get; private set; }

    private readonly WallpaperLayerEditorWindow _editor;
    private bool _updating;

    private readonly Slider _hueSlider = HslSlider(-180, 180);
    private readonly Slider _satSlider = HslSlider(-100, 100);
    private readonly Slider _lightSlider = HslSlider(-100, 100);
    private readonly TextBlock _hueValue = ValueLabel();
    private readonly TextBlock _satValue = ValueLabel();
    private readonly TextBlock _lightValue = ValueLabel();
    private readonly TextBlock _hint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        FontSize = 12
    };

    public HslAdjustWindow(WallpaperLayerEditorWindow editor)
    {
        Title = "色相 / 饱和度";
        Width = 380;
        Height = 340;
        MinWidth = 320;
        MinHeight = 300;
        Background = ThemePalette.WindowBackground();
        _editor = editor;
        BuildContent();
        WireEvents();
        SyncFromEditor();
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
            Text = "色相 / 饱和度",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(HslRow("色相", _hueSlider, _hueValue));
        panel.Children.Add(HslRow("饱和度", _satSlider, _satValue));
        panel.Children.Add(HslRow("明度", _lightSlider, _lightValue));
        var reset = new Button { Content = "重置为 0", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) =>
        {
            _hueSlider.Value = 0;
            _satSlider.Value = 0;
            _lightSlider.Value = 0;
        };
        panel.Children.Add(reset);
        panel.Children.Add(_hint);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
    }

    private void WireEvents()
    {
        _hueSlider.ValueChanged += (_, _) => { if (!_updating) Apply(); };
        _satSlider.ValueChanged += (_, _) => { if (!_updating) Apply(); };
        _lightSlider.ValueChanged += (_, _) => { if (!_updating) Apply(); };
    }

    /// <summary>把当前滑块值应用到编辑器选中的图片图层（实时预览）。</summary>
    private void Apply()
    {
        UpdateValues();
        _editor.ApplyHslToSelected(_hueSlider.Value, _satSlider.Value, _lightSlider.Value);
    }

    private void UpdateValues()
    {
        _hueValue.Text = $"{_hueSlider.Value:0}°";
        _satValue.Text = $"{_satSlider.Value:0}%";
        _lightValue.Text = $"{_lightSlider.Value:0}%";
    }

    /// <summary>从编辑器当前选中的图片图层读取数值（编辑器选中变化时调用）。</summary>
    public void SyncFromEditor()
    {
        _updating = true;
        try
        {
            var layer = _editor.FirstSelectedImageLayer;
            var enabled = layer != null;
            _hueSlider.IsEnabled = enabled;
            _satSlider.IsEnabled = enabled;
            _lightSlider.IsEnabled = enabled;
            if (layer != null)
            {
                _hueSlider.Value = layer.HueShift;
                _satSlider.Value = layer.SaturationAdjust;
                _lightSlider.Value = layer.LightnessAdjust;
            }

            UpdateValues();
            _hint.Text = enabled
                ? "调整会实时应用到选中的图片图层；关闭窗口后需点击「保存并应用」才会写入主界面。"
                : "请先在编辑器中选中一个图片图层。";
        }
        finally
        {
            _updating = false;
        }
    }

    private static Slider HslSlider(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        TickFrequency = 1,
        IsSnapToTickEnabled = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock ValueLabel() => new()
    {
        Width = 56,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.9
    };

    private static Control HslRow(string label, Slider slider, TextBlock value)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            MinHeight = 32,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9,
                    Margin = new Thickness(0, 0, 10, 0)
                },
                slider,
                value
            }
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        return row;
    }
}
