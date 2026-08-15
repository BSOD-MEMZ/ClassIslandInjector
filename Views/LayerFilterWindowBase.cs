using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// 图层滤镜窗口基类：顶部可加预设下拉，每个数值行 = 「标签 + 数字框」一行 + 下方整行滑块，
/// 底部提供「预览」开关（默认开启）与「确定 / 取消」按钮。
/// 行为：
/// - 预览开：改动实时应用到选中的图片图层（画布即时看到效果，但不提交）。
/// - 确定：压一次撤销并把当前值提交到选中图层（标记编辑器脏）。
/// - 取消 / 关闭：恢复为打开时的快照值。
/// - 编辑器选中变化时由编辑器统一调用 <see cref="SyncFromEditor"/>。
/// </summary>
internal abstract class LayerFilterWindowBase : MyWindow
{
    protected readonly WallpaperLayerEditorWindow Editor;
    protected bool Updating;

    private readonly Dictionary<string, double[]> _snapshot = [];
    private bool _undoPushed;
    private bool _committed;
    private readonly Button _okButton = new() { Content = "确定", MinWidth = 76, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cancelButton = new() { Content = "取消", MinWidth = 76, HorizontalAlignment = HorizontalAlignment.Stretch };
    protected readonly ToggleSwitch PreviewToggle = new() { OnContent = "开", OffContent = "关", IsChecked = true };
    /// <summary>仅在没有选中图片图层时显示提示，其余情况保持空白。</summary>
    protected readonly TextBlock Hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };

    protected LayerFilterWindowBase(WallpaperLayerEditorWindow editor, string title, int width, int height)
    {
        Editor = editor;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Max(300, width - 60);
        MinHeight = Math.Max(260, height - 80);
        EditorMica.EnableMica(this);
        _okButton.Click += (_, _) => Ok();
        _cancelButton.Click += (_, _) => Cancel();
        PreviewToggle.PropertyChanged += (_, e) =>
        {
            if (!Updating && e.Property == ToggleSwitch.IsCheckedProperty)
            {
                OnValuesChanged();
            }
        };
        Closed += (_, _) =>
        {
            // 未点「确定」就关闭 = 视为取消，恢复打开时的快照。
            if (!_committed)
            {
                RestoreSnapshot();
            }

            if (ReferenceEquals(CurrentWindow, this))
            {
                CurrentWindow = null;
            }
        };
        BuildWindowContent();
    }

    /// <summary>当前打开的实例（派生类实现，禁止多开）。</summary>
    protected abstract LayerFilterWindowBase? CurrentWindow { get; set; }

    /// <summary>数值通道数（如色相/饱和度/明度为 3）。</summary>
    protected abstract int ValueCount { get; }

    protected abstract double GetValue(WallpaperLayerItem layer, int index);
    protected abstract void SetValue(WallpaperLayerItem layer, int index, double value);
    protected abstract double[] ReadControlsToValues();
    protected abstract void ReadValuesToControls(double[] values);
    protected abstract void BuildContentRows(StackPanel panel);
    protected abstract void SyncEnabled(bool hasImageLayer);

    private void BuildWindowContent()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        BuildContentRows(panel);
        var previewRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = "预览", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 },
                PreviewToggle
            }
        };
        Grid.SetColumn(PreviewToggle, 1);
        panel.Children.Add(previewRow);
        panel.Children.Add(Hint);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _okButton, _cancelButton }
        };
        panel.Children.Add(buttons);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
    }

    /// <summary>编辑器选中变化时同步（由编辑器统一调用；打开时也会调用一次）。</summary>
    public void SyncFromEditor()
    {
        Updating = true;
        try
        {
            var layer = Editor.FirstSelectedImageLayer;
            if (layer is not null)
            {
                foreach (var l in Editor.SelectedImageLayers)
                {
                    if (!_snapshot.ContainsKey(l.Id))
                    {
                        _snapshot[l.Id] = ReadLayerValues(l);
                    }
                }

                ReadValuesToControls(ReadLayerValues(layer));
                Hint.Text = string.Empty;
                SyncEnabled(true);
            }
            else
            {
                ReadValuesToControls(new double[ValueCount]);
                Hint.Text = "请先在编辑器中选中一个图片图层。";
                SyncEnabled(false);
            }
        }
        finally
        {
            Updating = false;
        }
    }

    private double[] ReadLayerValues(WallpaperLayerItem layer) =>
        Enumerable.Range(0, ValueCount).Select(i => GetValue(layer, i)).ToArray();

    /// <summary>控件值变化：开启预览时实时应用到选中图层。</summary>
    protected void OnValuesChanged()
    {
        if (Updating)
        {
            return;
        }

        if (PreviewToggle.IsChecked == true)
        {
            EnsureUndo();
            Editor.ApplyLayerFilter(ApplyValuesToLayers);
        }
    }

    private void ApplyValuesToLayers()
    {
        var values = ReadControlsToValues();
        foreach (var l in Editor.SelectedImageLayers)
        {
            for (var i = 0; i < ValueCount; i++)
            {
                SetValue(l, i, values[i]);
            }
        }
    }

    private void EnsureUndo()
    {
        if (!_undoPushed)
        {
            Editor.PushLayerFilterUndo();
            _undoPushed = true;
        }
    }

    private bool ValuesChanged()
    {
        var layer = Editor.FirstSelectedImageLayer;
        if (layer == null || !_snapshot.TryGetValue(layer.Id, out var snap))
        {
            return false;
        }

        var cur = ReadControlsToValues();
        for (var i = 0; i < ValueCount; i++)
        {
            if (Math.Abs(cur[i] - snap[i]) > 0.0001)
            {
                return true;
            }
        }

        return false;
    }

    private void Ok()
    {
        if (ValuesChanged())
        {
            EnsureUndo();
            ApplyValuesToLayers();
            Editor.CommitLayerFilter();
        }
        else if (PreviewToggle.IsChecked == true)
        {
            Editor.CommitLayerFilter();
        }

        _committed = true;
        Close();
    }

    private void Cancel()
    {
        _committed = true;
        RestoreSnapshot();
        Close();
    }

    private void RestoreSnapshot()
    {
        if (_snapshot.Count == 0)
        {
            return;
        }

        foreach (var l in Editor.SelectedImageLayers)
        {
            if (_snapshot.TryGetValue(l.Id, out var snap))
            {
                for (var i = 0; i < ValueCount; i++)
                {
                    SetValue(l, i, snap[i]);
                }
            }
        }

        Editor.RefreshAfterLayerFilter();
    }

    /// <summary>预设下拉行（标签 + 下拉）。</summary>
    protected static Control PresetRow(ComboBox box)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            MinHeight = 32,
            Children =
            {
                new TextBlock { Text = "预设", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9, Margin = new Thickness(0, 0, 10, 0), MinWidth = 64 },
                box
            }
        };
        Grid.SetColumn(box, 1);
        return row;
    }
}

/// <summary>
/// 滤镜数值行：标签 + 整行滑块（描述文本与滑动条同一行，无数字框）。
/// </summary>
internal sealed class FilterRowControl : Grid
{
    public Slider Slider { get; }

    public FilterRowControl(string label, double min, double max, double increment)
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        MinHeight = 32;
        Slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = increment,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9,
            Margin = new Thickness(0, 0, 10, 0),
            MinWidth = 64,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Children.Add(Slider);
        Grid.SetColumn(Slider, 1);
    }

    /// <summary>当前数值（读写滑块）。</summary>
    public double Value
    {
        get => Slider.Value;
        set => Slider.Value = value;
    }
}
