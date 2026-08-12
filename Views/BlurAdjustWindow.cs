using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace ClassIslandInjector.Views;

/// <summary>
/// 高斯模糊设置窗口：模糊半径「数字框 + 整行滑块」+ 预览开关 + 确定 / 取消。
/// 非模态，实时预览 BlurEffect 效果。
/// </summary>
internal sealed class BlurAdjustWindow : LayerFilterWindowBase
{
    /// <summary>当前打开的实例（禁止多开）。</summary>
    public static BlurAdjustWindow? Current { get; private set; }

    private readonly FilterRowControl _blurRow = new("模糊半径", 0, 100, 0.5);

    public BlurAdjustWindow(WallpaperLayerEditorWindow editor)
        : base(editor, "高斯模糊", 420, 300)
    {
        Current = this;
        SyncFromEditor();
    }

    protected override LayerFilterWindowBase? CurrentWindow
    {
        get => Current;
        set => Current = (BlurAdjustWindow?)value;
    }

    protected override int ValueCount => 1;

    protected override double GetValue(WallpaperLayerItem layer, int index) => layer.BlurRadius;

    protected override void SetValue(WallpaperLayerItem layer, int index, double value) =>
        layer.BlurRadius = value;

    protected override double[] ReadControlsToValues() => [_blurRow.Value];

    protected override void ReadValuesToControls(double[] values) => _blurRow.Value = values[0];

    protected override void SyncEnabled(bool hasImageLayer) => _blurRow.IsEnabled = hasImageLayer;

    protected override void BuildContentRows(StackPanel panel)
    {
        panel.Children.Add(_blurRow);
        _blurRow.Slider.PropertyChanged += (_, e) =>
        {
            if (!Updating && e.Property == RangeBase.ValueProperty)
            {
                OnValuesChanged();
            }
        };
    }
}
