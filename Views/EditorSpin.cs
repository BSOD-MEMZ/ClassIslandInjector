using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ClassIslandInjector.Views;

/// <summary>
/// 检查器用 NumericUpDown（必须把 StyleKey 指回基类，否则 FluentAvalonia 隐式主题
/// 按派生类型查找不到，控件渲染为空不可见）。图层编辑器与视频编辑器共用。
/// </summary>
public sealed class EditorSpin : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public EditorSpin(double minimum, double maximum, double increment, string format)
    {
        Minimum = (decimal)minimum;
        Maximum = (decimal)maximum;
        Increment = (decimal)increment;
        FormatString = format;
        Value = (decimal)minimum;
        Width = 140;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Right;
    }

    public double DoubleValue
    {
        get => (double)(Value ?? 0);
        set => Value = (decimal)Math.Clamp(value, (double)Minimum, (double)Maximum);
    }
}
