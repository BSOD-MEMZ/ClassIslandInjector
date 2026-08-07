using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace ClassIslandInjector.Views;

/// <summary>
/// A presentation-style direct-manipulation canvas for the subset of the island
/// that this plugin owns. ClassIsland still remains the authority for components.
/// </summary>
internal sealed class IslandVisualEditor : UserControl
{
    private const double PreviewOffsetScale = 0.1;
    private const double PreviewSizeScale = 0.58;
    private readonly Canvas _stage;
    private readonly EditorGridOverlay _grid = new() { IsHitTestVisible = false };
    private readonly SelectionOverlay _selection = new() { IsHitTestVisible = false };
    private readonly Border _island;
    private readonly Dictionary<Border, Vector> _resizeHandles = [];
    private readonly Border _rotationHandle;
    private readonly Border _cornerRadiusHandle;
    private IslandPreviewState _state;
    private Point _lastPointerPosition;
    private bool _isDragging;
    private bool _isRotating;
    private bool _isEditingCornerRadius;
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly TextBlock _unsupportedHint;
    private readonly Slider _zoomSlider = new()
    {
        Minimum = 0.5,
        Maximum = 2,
        Value = 1,
        Width = 140,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _zoomText = new()
    {
        Text = "100%",
        MinWidth = 44,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.8
    };

    public event EventHandler<IslandTransformEditedEventArgs>? TransformEdited;
    public event EventHandler<IslandValueEditedEventArgs>? CornerRadiusEdited;
    public event EventHandler? TransformEditCompleted;
    public event EventHandler? EditStarted;

    public IslandVisualEditor()
    {
        Focusable = true;
        _stage = new Canvas
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(29, 31, 35))
        };
        _stage.PointerPressed += StageOnPointerPressed;
        _stage.PointerMoved += StageOnPointerMoved;
        _stage.PointerReleased += StageOnPointerReleased;
        _stage.SizeChanged += (_, _) =>
        {
            _grid.Width = _stage.Bounds.Width;
            _grid.Height = _stage.Bounds.Height;
            _selection.Width = _stage.Bounds.Width;
            _selection.Height = _stage.Bounds.Height;
            Update(_state);
        };
        KeyDown += IslandVisualEditorOnKeyDown;

        _stage.Children.Add(_grid);
        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = "正在上课", FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "数学  ·  08:00 – 08:45", Opacity = 0.8, FontSize = 12 });
        _island = new Border
        {
            Width = 240,
            Height = 70,
            Padding = new Thickness(18, 10),
            Child = content,
            RenderTransformOrigin = RelativePoint.Center
        };
        _stage.Children.Add(_island);
        _stage.Children.Add(_selection);

        _unsupportedHint = new TextBlock
        {
            Text = "固定显示大小已不再受支持：插件无法控制 ClassIsland 的主界面宽高",
            IsVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(220, 18, 20, 24)),
            Foreground = Brushes.White,
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _unsupportedHint.IsVisible = false;
        };
        _stage.Children.Add(_unsupportedHint);

        foreach (var (name, direction, cursor) in new[]
                 {
                     ("nw", new Vector(-1, -1), StandardCursorType.TopLeftCorner),
                     ("n", new Vector(0, -1), StandardCursorType.TopSide),
                     ("ne", new Vector(1, -1), StandardCursorType.TopRightCorner),
                     ("e", new Vector(1, 0), StandardCursorType.RightSide),
                     ("se", new Vector(1, 1), StandardCursorType.BottomRightCorner),
                     ("s", new Vector(0, 1), StandardCursorType.BottomSide),
                     ("sw", new Vector(-1, 1), StandardCursorType.BottomLeftCorner),
                     ("w", new Vector(-1, 0), StandardCursorType.LeftSide)
                 })
        {
            var handle = Handle(10, new SolidColorBrush(Color.FromRgb(0, 120, 212)), cursor);
            handle.Name = name;
            handle.PointerPressed += (_, e) => ResizeHandleOnPointerPressed(handle, e);
            _resizeHandles.Add(handle, direction);
            _stage.Children.Add(handle);
        }

        _rotationHandle = Handle(12, new SolidColorBrush(Color.FromRgb(121, 80, 242)), StandardCursorType.Hand);
        _rotationHandle.PointerPressed += RotationHandleOnPointerPressed;
        _rotationHandle.PointerMoved += RotationHandleOnPointerMoved;
        _rotationHandle.PointerReleased += RotationHandleOnPointerReleased;
        _stage.Children.Add(_rotationHandle);

        _cornerRadiusHandle = Handle(9, new SolidColorBrush(Color.FromRgb(22, 163, 74)), StandardCursorType.Hand);
        _cornerRadiusHandle.PointerPressed += CornerRadiusHandleOnPointerPressed;
        _cornerRadiusHandle.PointerMoved += CornerRadiusHandleOnPointerMoved;
        _cornerRadiusHandle.PointerReleased += CornerRadiusHandleOnPointerReleased;
        _stage.Children.Add(_cornerRadiusHandle);

        // 视图右下角的缩放滑动标尺：只改变舞台渲染（视图）大小，不改变主界面实际大小。
        _zoomSlider.ValueChanged += (_, _) => ApplyZoom();
        ApplyZoom();
        var zoomPanel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(150, 18, 20, 24)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "缩放", Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center },
                    _zoomSlider,
                    _zoomText
                }
            }
        };
        var stageHost = new Grid { Children = { _stage, zoomPanel } };

        // 底部操作区（替代原提示文本）。
        var operations = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                OperationButton("\uE033", "居中", Center),
                OperationButton("\uE0BD", "还原变形", ResetTransform),
                OperationButton("\uE12F", "重置视图缩放", ResetViewZoom),
                OperationButton("\uE929", "显示网格", ToggleGrid)
            }
        };

        // 舞台（中间视图）与底部操作区保持固定布局（仅右侧检查器可拖动手柄调整宽度）。
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 8,
            Children = { stageHost, operations }
        };
        Grid.SetRow(operations, 1);
    }

    public void Update(IslandPreviewState state)
    {
        _state = state;
        var (gradientStart, gradientEnd) = GradientGeometry.Points(state.GradientDirection);
        IBrush background = state.CustomBackground && state.Gradient
            ? new LinearGradientBrush
            {
                StartPoint = gradientStart,
                EndPoint = gradientEnd,
                GradientStops = [new GradientStop(state.BackgroundColor, 0), new GradientStop(state.GradientEndColor, 1)]
            }
            : new SolidColorBrush(state.CustomBackground ? state.BackgroundColor : Color.FromArgb(220, 48, 48, 48));

        var displayWidth = Math.Clamp(420 * PreviewSizeScale, 160, 700);
        var displayHeight = Math.Clamp(120 * PreviewSizeScale, 48, 260);
        _island.Width = displayWidth;
        _island.Height = displayHeight;
        _island.Background = background;
        _island.BorderBrush = state.BorderEnabled ? new SolidColorBrush(state.BorderColor) : null;
        _island.BorderThickness = state.BorderEnabled ? new Thickness(state.BorderThickness) : new Thickness(0);
        _island.CornerRadius = new CornerRadius(Math.Min(state.CornerRadius * PreviewSizeScale, Math.Min(displayWidth, displayHeight) / 2));
        _island.Opacity = state.Opacity;
        _island.Effect = state.ShadowEnabled
            ? new DropShadowEffect
            {
                Color = state.ShadowColor,
                BlurRadius = Math.Min(state.ShadowBlur, 50),
                OffsetX = state.ShadowOffsetX * 0.25,
                OffsetY = state.ShadowOffsetY * 0.25,
                Opacity = state.ShadowOpacity
            }
            : null;
        _island.RenderTransform = new RotateTransform(state.Rotation);

        var x = Math.Clamp((_stage.Bounds.Width - displayWidth) / 2 + state.OffsetX * PreviewOffsetScale,
            -displayWidth / 2, _stage.Bounds.Width - displayWidth / 2);
        var y = Math.Clamp((_stage.Bounds.Height - displayHeight) / 2 + state.OffsetY * PreviewOffsetScale,
            -displayHeight / 2, _stage.Bounds.Height - displayHeight / 2);
        Canvas.SetLeft(_island, x);
        Canvas.SetTop(_island, y);

        var scaledWidth = displayWidth;
        var scaledHeight = displayHeight;
        foreach (var (handle, direction) in _resizeHandles)
        {
            Canvas.SetLeft(handle, x + scaledWidth * (direction.X + 1) / 2 - handle.Width / 2);
            Canvas.SetTop(handle, y + scaledHeight * (direction.Y + 1) / 2 - handle.Height / 2);
        }

        Canvas.SetLeft(_rotationHandle, x + scaledWidth / 2 - _rotationHandle.Width / 2);
        Canvas.SetTop(_rotationHandle, y - 38);
        Canvas.SetLeft(_cornerRadiusHandle, x - 30);
        Canvas.SetTop(_cornerRadiusHandle, y - 20);

        // PowerPoint 风格：虚线选中框连接 8 个蓝色圆点，上中蓝点连到紫色旋转手柄。
        _selection.IslandBounds = new Rect(x, y, scaledWidth, scaledHeight);
        _selection.RotationStart = new Point(x + scaledWidth / 2, y);
        _selection.RotationEnd = new Point(x + scaledWidth / 2, y - 38);
        _selection.InvalidateVisual();
    }

    public void Center() => RaiseTransformEdited(0, 0, _state.Rotation, true);
    public void ResetTransform() => RaiseTransformEdited(0, 0, 0, true);

    /// <summary>
    /// 当前视图缩放倍率（仅影响编辑器视图，不影响主界面实际大小）。
    /// </summary>
    public double Zoom
    {
        get => _zoomSlider.Value;
        set => _zoomSlider.Value = value;
    }

    private void ApplyZoom()
    {
        var zoom = _zoomSlider.Value;
        _stage.RenderTransform = new ScaleTransform(zoom, zoom);
        _stage.RenderTransformOrigin = RelativePoint.Center;
        _zoomText.Text = $"{zoom:P0}";
    }

    private void ResetViewZoom() => _zoomSlider.Value = 1;

    private void ToggleGrid() => _grid.IsVisible = !_grid.IsVisible;

    private static Button OperationButton(string glyph, string text, Action action)
    {
        var button = new Button { Content = new IconText { Glyph = glyph, Text = text } };
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Handle(double size, IBrush background, StandardCursorType cursor) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = background,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 5, Color = Color.FromArgb(115, 0, 0, 0) }),
        Cursor = new Cursor(cursor)
    };

    private void StageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_stage);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
            return;
        Focus();
        _isDragging = true;
        EditStarted?.Invoke(this, EventArgs.Empty);
        _lastPointerPosition = point.Position;
        e.Pointer.Capture(_stage);
        e.Handled = true;
    }

    private void StageOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
            return;
        var position = e.GetPosition(_stage);
        var delta = position - _lastPointerPosition;
        _lastPointerPosition = position;
        RaiseTransformEdited(_state.OffsetX + delta.X / PreviewOffsetScale,
            _state.OffsetY + delta.Y / PreviewOffsetScale, _state.Rotation);
    }

    private void StageOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;
        _isDragging = false;
        e.Pointer.Capture(null);
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeHandleOnPointerPressed(Border handle, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;
        // 固定显示大小已不再受支持：插件无法控制 ClassIsland 的主界面宽高。
        e.Handled = true;
        ShowUnsupportedSizeHint();
    }

    private void ShowUnsupportedSizeHint()
    {
        _unsupportedHint.IsVisible = true;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void RotationHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_rotationHandle).Properties.IsLeftButtonPressed)
            return;
        _isRotating = true;
        _lastPointerPosition = e.GetPosition(_stage);
        EditStarted?.Invoke(this, EventArgs.Empty);
        e.Pointer.Capture(_rotationHandle);
        e.Handled = true;
    }

    private void RotationHandleOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isRotating)
            return;
        var center = new Point(Canvas.GetLeft(_island) + _island.Width / 2, Canvas.GetTop(_island) + _island.Height / 2);
        var vector = e.GetPosition(_stage) - center;
        var rotation = Math.Atan2(vector.Y, vector.X) * 180 / Math.PI + 90;
        RaiseTransformEdited(_state.OffsetX, _state.OffsetY, rotation);
        e.Handled = true;
    }

    private void RotationHandleOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isRotating)
            return;
        _isRotating = false;
        e.Pointer.Capture(null);
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void CornerRadiusHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_cornerRadiusHandle).Properties.IsLeftButtonPressed)
            return;
        _isEditingCornerRadius = true;
        _lastPointerPosition = e.GetPosition(_stage);
        EditStarted?.Invoke(this, EventArgs.Empty);
        e.Pointer.Capture(_cornerRadiusHandle);
        e.Handled = true;
    }

    private void CornerRadiusHandleOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isEditingCornerRadius)
            return;
        var position = e.GetPosition(_stage);
        var delta = position - _lastPointerPosition;
        _lastPointerPosition = position;
        CornerRadiusEdited?.Invoke(this, new IslandValueEditedEventArgs(Math.Clamp(_state.CornerRadius + (delta.X - delta.Y) / PreviewSizeScale, 0, 500)));
        e.Handled = true;
    }

    private void CornerRadiusHandleOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isEditingCornerRadius)
            return;
        _isEditingCornerRadius = false;
        e.Pointer.Capture(null);
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void IslandVisualEditorOnKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var x = _state.OffsetX;
        var y = _state.OffsetY;
        var rotation = _state.Rotation;
        switch (e.Key)
        {
            case Key.Left: x -= step; break;
            case Key.Right: x += step; break;
            case Key.Up: y -= step; break;
            case Key.Down: y += step; break;
            case Key.Q: rotation -= 1; break;
            case Key.E: rotation += 1; break;
            default: return;
        }
        EditStarted?.Invoke(this, EventArgs.Empty);
        RaiseTransformEdited(x, y, rotation, true);
        e.Handled = true;
    }

    private void RaiseTransformEdited(double offsetX, double offsetY, double rotation, bool isComplete = false)
    {
        TransformEdited?.Invoke(this, new IslandTransformEditedEventArgs(
            Math.Clamp(offsetX, -2000, 2000), Math.Clamp(offsetY, -2000, 2000),
            Math.Clamp(rotation, -360, 360)));
        if (isComplete)
            TransformEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private sealed class EditorGridOverlay : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var minor = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
            var major = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
            for (var x = 0d; x < Bounds.Width; x += 20)
                context.DrawLine(x % 100 == 0 ? major : minor, new Point(x, 0), new Point(x, Bounds.Height));
            for (var y = 0d; y < Bounds.Height; y += 20)
                context.DrawLine(y % 100 == 0 ? major : minor, new Point(0, y), new Point(Bounds.Width, y));
            var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 120, 212)), 1);
            context.DrawLine(centerPen, new Point(Bounds.Width / 2, 0), new Point(Bounds.Width / 2, Bounds.Height));
            context.DrawLine(centerPen, new Point(0, Bounds.Height / 2), new Point(Bounds.Width, Bounds.Height / 2));
        }
    }

    /// <summary>
    /// PowerPoint 风格的选中框：虚线矩形连接 8 个蓝色圆点，并画出连接上中蓝点与
    /// 紫色旋转手柄的旋转臂虚线。
    /// </summary>
    private sealed class SelectionOverlay : Control
    {
        public Rect IslandBounds { get; set; }
        public Point RotationStart { get; set; }
        public Point RotationEnd { get; set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var boxPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 212)), 1)
            {
                DashStyle = new DashStyle([4d, 3d], 0)
            };
            var b = IslandBounds;
            context.DrawRectangle(boxPen, new Rect(b.X + 0.5, b.Y + 0.5, b.Width - 1, b.Height - 1));
            var armPen = new Pen(new SolidColorBrush(Color.FromRgb(121, 80, 242)), 1)
            {
                DashStyle = new DashStyle([4d, 3d], 0)
            };
            context.DrawLine(armPen, RotationStart, RotationEnd);
        }
    }
}

internal sealed class IslandVisualEditorWindow : Window
{
    private bool _updatingInspector;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    public IslandVisualEditor Editor { get; } = new();
    public ColorPicker BackgroundColorPicker { get; } = new();
    public ToggleSwitch GradientToggle { get; } = new();
    public ColorPicker GradientEndColorPicker { get; } = new();
    public ToggleSwitch ShadowToggle { get; } = new();
    public ColorPicker ShadowColorPicker { get; } = new();
    public NumericUpDown ShadowBlurSpin { get; } = Spinner(0, 200, 1, "0");
    public NumericUpDown ShadowOpacitySpin { get; } = Spinner(0, 1, 0.05);
    public NumericUpDown OpacitySpin { get; } = Spinner(0, 1, 0.05);
    public NumericUpDown CornerRadiusSpin { get; } = Spinner(0, 500, 1, "0");
    public ToggleSwitch BackgroundToggle { get; } = new();
    public NumericUpDown RotationSpin { get; } = Spinner(-360, 360, 1, "0");
    public NumericUpDown OffsetXSpin { get; } = Spinner(-2000, 2000, 10, "0");
    public NumericUpDown OffsetYSpin { get; } = Spinner(-2000, 2000, 10, "0");
    public ToggleSwitch BorderToggle { get; } = new();
    public ColorPicker BorderColorPicker { get; } = new();
    public NumericUpDown BorderThicknessSpin { get; } = Spinner(0.25, 20, 0.25);
    public NumericUpDown ShadowOffsetXSpin { get; } = Spinner(-200, 200, 1, "0");
    public NumericUpDown ShadowOffsetYSpin { get; } = Spinner(-200, 200, 1, "0");

    public event EventHandler? SaveRequested;
    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;
    public event Action<Color>? BackgroundColorEdited;
    public event Action<bool>? GradientEdited;
    public event Action<Color>? GradientEndColorEdited;
    public event Action<bool>? ShadowEdited;
    public event Action<Color>? ShadowColorEdited;
    public event Action<double>? ShadowBlurEdited;
    public event Action<double>? ShadowOpacityEdited;
    public event Action<double>? OpacityEdited;
    public event Action<double>? CornerRadiusEdited;
    public event Action<bool>? BackgroundEdited;
    public event Action<double>? RotationEdited;
    public event Action<double>? OffsetXEdited;
    public event Action<double>? OffsetYEdited;
    public event Action<bool>? BorderEdited;
    public event Action<Color>? BorderColorEdited;
    public event Action<double>? BorderThicknessEdited;
    public event Action<double>? ShadowOffsetXEdited;
    public event Action<double>? ShadowOffsetYEdited;

    public IslandVisualEditorWindow()
    {
        Title = "ClassIsland 可视化编辑器";
        Width = 1120;
        Height = 720;
        MinWidth = 820;
        MinHeight = 560;

        ConfigureInspectorEvents();
        _undoButton = IconButton("\uE195", "撤销", () => UndoRequested?.Invoke(this, EventArgs.Empty));
        _redoButton = IconButton("\uE121", "重做", () => RedoRequested?.Invoke(this, EventArgs.Empty));
        _undoButton.IsEnabled = false;
        _redoButton.IsEnabled = false;
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                IconButton("\uE74E", "保存", () => SaveRequested?.Invoke(this, EventArgs.Empty)),
                _undoButton,
                _redoButton,
                IconButton("\uE671", "关闭", Close)
            }
        };

        var inspector = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "检查器", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                SectionTitle("\uE113", "变换"),
                CompactRow("不透明度", OpacitySpin),
                CompactRow("旋转角度", RotationSpin),
                CompactRow("水平偏移", OffsetXSpin),
                CompactRow("垂直偏移", OffsetYSpin),
                SectionTitle("\uEE83", "圆角"),
                CompactRow("圆角半径", CornerRadiusSpin),
                SectionTitle("\uE520", "背景"),
                CompactRow("自定义背景", BackgroundToggle),
                CompactRow("起始颜色", BackgroundColorPicker, BackgroundToggle),
                CompactRow("线性渐变", GradientToggle, BackgroundToggle),
                CompactRow("渐变终止色", GradientEndColorPicker, GradientToggle),
                SectionTitle("\uE472", "阴影"),
                CompactRow("启用阴影", ShadowToggle),
                CompactRow("阴影颜色", ShadowColorPicker, ShadowToggle),
                CompactRow("模糊半径", ShadowBlurSpin, ShadowToggle),
                CompactRow("水平偏移", ShadowOffsetXSpin, ShadowToggle),
                CompactRow("垂直偏移", ShadowOffsetYSpin, ShadowToggle),
                CompactRow("阴影不透明度", ShadowOpacitySpin, ShadowToggle),
                SectionTitle("\uE254", "岛屿边框"),
                CompactRow("启用边框", BorderToggle),
                CompactRow("边框颜色", BorderColorPicker, BorderToggle),
                CompactRow("边框线宽", BorderThicknessSpin, BorderToggle)
            }
        };
        // 中间视图（编辑器）与右侧检查器之间加垂直分割手柄，用户可左右拖动调整检查器宽度。
        var columnSplitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        var inspectorScroll = new ScrollViewer { Content = inspector };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,300"),
            ColumnSpacing = 8,
            Children = { Editor, columnSplitter, inspectorScroll }
        };
        Grid.SetColumn(columnSplitter, 1);
        Grid.SetColumn(inspectorScroll, 2);
        var dangerInfo = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "危险操作提示",
            Message = "可视化编辑器会直接改动主界面的外观与变形，操作不当可能导致布局异常或视觉混乱，出于安全考虑已禁用大部分功能且停止维护。",
            IsOpen = true,
            IsClosable = true
        };
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { dangerInfo, toolbar, body }
        };
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(body, 2);
    }

    public void UpdateInspector(IslandPreviewState state)
    {
        _updatingInspector = true;
        try
        {
            BackgroundColorPicker.Color = state.BackgroundColor;
            GradientToggle.IsChecked = state.Gradient;
            GradientEndColorPicker.Color = state.GradientEndColor;
            ShadowToggle.IsChecked = state.ShadowEnabled;
            ShadowColorPicker.Color = state.ShadowColor;
            ShadowBlurSpin.Value = (decimal)state.ShadowBlur;
            ShadowOpacitySpin.Value = (decimal)state.ShadowOpacity;
            OpacitySpin.Value = (decimal)state.Opacity;
            CornerRadiusSpin.Value = (decimal)state.CornerRadius;
            BackgroundToggle.IsChecked = state.CustomBackground;
            RotationSpin.Value = (decimal)state.Rotation;
            OffsetXSpin.Value = (decimal)state.OffsetX;
            OffsetYSpin.Value = (decimal)state.OffsetY;
            BorderToggle.IsChecked = state.BorderEnabled;
            BorderColorPicker.Color = state.BorderColor;
            BorderThicknessSpin.Value = (decimal)state.BorderThickness;
            ShadowOffsetXSpin.Value = (decimal)state.ShadowOffsetX;
            ShadowOffsetYSpin.Value = (decimal)state.ShadowOffsetY;
        }
        finally { _updatingInspector = false; }
    }

    private void ConfigureInspectorEvents()
    {
        BackgroundColorPicker.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ColorPicker.ColorProperty) BackgroundColorEdited?.Invoke(BackgroundColorPicker.Color); };
        GradientToggle.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty) GradientEdited?.Invoke(GradientToggle.IsChecked == true); };
        GradientEndColorPicker.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ColorPicker.ColorProperty) GradientEndColorEdited?.Invoke(GradientEndColorPicker.Color); };
        ShadowToggle.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty) ShadowEdited?.Invoke(ShadowToggle.IsChecked == true); };
        ShadowColorPicker.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ColorPicker.ColorProperty) ShadowColorEdited?.Invoke(ShadowColorPicker.Color); };
        ShadowBlurSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ShadowBlurEdited?.Invoke((double)(ShadowBlurSpin.Value ?? 0)); };
        ShadowOpacitySpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ShadowOpacityEdited?.Invoke((double)(ShadowOpacitySpin.Value ?? 0)); };
        OpacitySpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) OpacityEdited?.Invoke((double)(OpacitySpin.Value ?? 0)); };
        CornerRadiusSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) CornerRadiusEdited?.Invoke((double)(CornerRadiusSpin.Value ?? 0)); };
        BackgroundToggle.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty) BackgroundEdited?.Invoke(BackgroundToggle.IsChecked == true); };
        RotationSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) RotationEdited?.Invoke((double)(RotationSpin.Value ?? 0)); };
        OffsetXSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) OffsetXEdited?.Invoke((double)(OffsetXSpin.Value ?? 0)); };
        OffsetYSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) OffsetYEdited?.Invoke((double)(OffsetYSpin.Value ?? 0)); };
        BorderToggle.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ToggleSwitch.IsCheckedProperty) BorderEdited?.Invoke(BorderToggle.IsChecked == true); };
        BorderColorPicker.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == ColorPicker.ColorProperty) BorderColorEdited?.Invoke(BorderColorPicker.Color); };
        BorderThicknessSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) BorderThicknessEdited?.Invoke((double)(BorderThicknessSpin.Value ?? 0)); };
        ShadowOffsetXSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ShadowOffsetXEdited?.Invoke((double)(ShadowOffsetXSpin.Value ?? 0)); };
        ShadowOffsetYSpin.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == NumericUpDown.ValueProperty) ShadowOffsetYEdited?.Invoke((double)(ShadowOffsetYSpin.Value ?? 0)); };
    }

    /// <summary>
    /// 更新撤销/重做按钮的可用状态。
    /// </summary>
    public void UpdateUndoState(bool canUndo, bool canRedo)
    {
        _undoButton.IsEnabled = canUndo;
        _redoButton.IsEnabled = canRedo;
    }

    /// <summary>
    /// 紧凑式检查器行：左侧标签 + 右侧控件，一行一个（不用卡片/展开分组）。
    /// </summary>
    private static Control CompactRow(string label, Control control, ToggleSwitch? dependency = null)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 },
                control
            }
        };
        Grid.SetColumn(control, 1);
        if (dependency != null)
        {
            void Sync() => row.IsEnabled = dependency.IsChecked == true;
            dependency.PropertyChanged += (_, _) => Sync();
            Sync();
        }

        return row;
    }

    private static IconText SectionTitle(string glyph, string text) => new()
    {
        Glyph = glyph,
        Text = text,
        Margin = new Thickness(0, 12, 0, 2)
    };

    /// <summary>
    /// 精确数值用 spinbox（NumericUpDown），替代样式异常的滑块。
    /// </summary>
    private static NumericUpDown Spinner(double minimum, double maximum, double increment, string format = "0.##") => new()
    {
        Minimum = (decimal)minimum,
        Maximum = (decimal)maximum,
        Increment = (decimal)increment,
        FormatString = format,
        Width = 110,
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private static Button IconButton(string glyph, string text, Action action)
    {
        var button = new Button { Content = new IconText { Glyph = glyph, Text = text } };
        button.Click += (_, _) => action();
        return button;
    }
}

internal readonly record struct IslandPreviewState(
    double Opacity, double Rotation, double OffsetX, double OffsetY, double CornerRadius,
    bool CustomBackground, Color BackgroundColor, bool Gradient,
    Color GradientEndColor, GradientDirection GradientDirection, bool ShadowEnabled, Color ShadowColor, double ShadowBlur, double ShadowOffsetX,
    double ShadowOffsetY, double ShadowOpacity, bool BorderEnabled, Color BorderColor, double BorderThickness);

internal sealed class IslandTransformEditedEventArgs(double offsetX, double offsetY, double rotation) : EventArgs
{
    public double OffsetX { get; } = offsetX;
    public double OffsetY { get; } = offsetY;
    public double Rotation { get; } = rotation;
}

internal sealed class IslandValueEditedEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}
