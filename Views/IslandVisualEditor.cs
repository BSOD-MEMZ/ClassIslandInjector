using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;

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
    private readonly Border _island;
    private readonly Dictionary<Border, Vector> _resizeHandles = [];
    private readonly Border _rotationHandle;
    private readonly Border _cornerRadiusHandle;
    private IslandPreviewState _state;
    private Point _lastPointerPosition;
    private Vector _resizeDirection;
    private bool _isDragging;
    private bool _isRotating;
    private bool _isResizing;
    private bool _isEditingCornerRadius;

    public event EventHandler<IslandTransformEditedEventArgs>? TransformEdited;
    public event EventHandler<IslandSizeEditedEventArgs>? SizeEdited;
    public event EventHandler<IslandValueEditedEventArgs>? CornerRadiusEdited;
    public event EventHandler? TransformEditCompleted;

    public IslandVisualEditor(double stageHeight = 430)
    {
        Focusable = true;
        _stage = new Canvas
        {
            Height = stageHeight,
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(29, 31, 35))
        };
        _stage.PointerPressed += StageOnPointerPressed;
        _stage.PointerMoved += StageOnPointerMoved;
        _stage.PointerReleased += StageOnPointerReleased;
        _stage.PointerWheelChanged += StageOnPointerWheelChanged;
        _stage.SizeChanged += (_, _) =>
        {
            _grid.Width = _stage.Bounds.Width;
            _grid.Height = _stage.Bounds.Height;
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
            var handle = Handle(14, new SolidColorBrush(Color.FromRgb(0, 120, 212)), cursor);
            handle.Name = name;
            handle.PointerPressed += (_, e) => ResizeHandleOnPointerPressed(handle, direction, e);
            handle.PointerMoved += ResizeHandleOnPointerMoved;
            handle.PointerReleased += ResizeHandleOnPointerReleased;
            _resizeHandles.Add(handle, direction);
            _stage.Children.Add(handle);
        }

        _rotationHandle = Handle(16, new SolidColorBrush(Color.FromRgb(121, 80, 242)), StandardCursorType.Hand);
        _rotationHandle.PointerPressed += RotationHandleOnPointerPressed;
        _rotationHandle.PointerMoved += RotationHandleOnPointerMoved;
        _rotationHandle.PointerReleased += RotationHandleOnPointerReleased;
        _stage.Children.Add(_rotationHandle);

        _cornerRadiusHandle = Handle(13, new SolidColorBrush(Color.FromRgb(22, 163, 74)), StandardCursorType.Hand);
        _cornerRadiusHandle.PointerPressed += CornerRadiusHandleOnPointerPressed;
        _cornerRadiusHandle.PointerMoved += CornerRadiusHandleOnPointerMoved;
        _cornerRadiusHandle.PointerReleased += CornerRadiusHandleOnPointerReleased;
        _stage.Children.Add(_cornerRadiusHandle);

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _stage,
                new TextBlock
                {
                    Text = "拖动对象移动位置；八个蓝色手柄调整宽高；紫色上方手柄旋转；绿色手柄调整圆角。滚轮缩放，Ctrl + 滚轮旋转，方向键微调。",
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    public void Update(IslandPreviewState state)
    {
        _state = state;
        IBrush background = state.CustomBackground && state.Gradient
            ? new LinearGradientBrush
            {
                StartPoint = RelativePoint.TopLeft,
                EndPoint = RelativePoint.BottomRight,
                GradientStops = [new GradientStop(state.BackgroundColor, 0), new GradientStop(state.GradientEndColor, 1)]
            }
            : new SolidColorBrush(state.CustomBackground ? state.BackgroundColor : Color.FromArgb(220, 48, 48, 48));

        var displayWidth = Math.Clamp((state.CustomSize ? state.Width : 420) * PreviewSizeScale, 160, 700);
        var displayHeight = Math.Clamp((state.CustomSize ? state.Height : 120) * PreviewSizeScale, 48, 260);
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
        _island.RenderTransform = new TransformGroup
        {
            Children = [new ScaleTransform(state.Scale, state.Scale), new RotateTransform(state.Rotation)]
        };

        var x = Math.Clamp((_stage.Bounds.Width - displayWidth) / 2 + state.OffsetX * PreviewOffsetScale,
            -displayWidth / 2, _stage.Bounds.Width - displayWidth / 2);
        var y = Math.Clamp((_stage.Bounds.Height - displayHeight) / 2 + state.OffsetY * PreviewOffsetScale,
            -displayHeight / 2, _stage.Bounds.Height - displayHeight / 2);
        Canvas.SetLeft(_island, x);
        Canvas.SetTop(_island, y);

        var scaledWidth = displayWidth * state.Scale;
        var scaledHeight = displayHeight * state.Scale;
        foreach (var (handle, direction) in _resizeHandles)
        {
            Canvas.SetLeft(handle, x + scaledWidth * (direction.X + 1) / 2 - handle.Width / 2);
            Canvas.SetTop(handle, y + scaledHeight * (direction.Y + 1) / 2 - handle.Height / 2);
        }

        Canvas.SetLeft(_rotationHandle, x + scaledWidth / 2 - _rotationHandle.Width / 2);
        Canvas.SetTop(_rotationHandle, y - 38);
        Canvas.SetLeft(_cornerRadiusHandle, x - 30);
        Canvas.SetTop(_cornerRadiusHandle, y - 20);
    }

    public void Center() => RaiseTransformEdited(0, 0, _state.Scale, _state.Rotation, true);
    public void ResetTransform() => RaiseTransformEdited(0, 0, 1, 0, true);

    private static Border Handle(double size, IBrush background, StandardCursorType cursor) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = background,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(2),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 5, Color = Color.FromArgb(115, 0, 0, 0) }),
        Cursor = new Cursor(cursor)
    };

    private void StageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_stage).Properties.IsLeftButtonPressed)
            return;
        Focus();
        _isDragging = true;
        _lastPointerPosition = e.GetPosition(_stage);
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
            _state.OffsetY + delta.Y / PreviewOffsetScale, _state.Scale, _state.Rotation);
    }

    private void StageOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
            return;
        _isDragging = false;
        e.Pointer.Capture(null);
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeHandleOnPointerPressed(Border handle, Vector direction, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;
        Focus();
        _isResizing = true;
        _resizeDirection = direction;
        _lastPointerPosition = e.GetPosition(_stage);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void ResizeHandleOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing)
            return;
        var position = e.GetPosition(_stage);
        var delta = position - _lastPointerPosition;
        _lastPointerPosition = position;
        var currentWidth = _state.CustomSize ? _state.Width : 420;
        var currentHeight = _state.CustomSize ? _state.Height : 120;
        var widthChange = _resizeDirection.X * delta.X / PreviewSizeScale;
        var heightChange = _resizeDirection.Y * delta.Y / PreviewSizeScale;
        var width = Math.Clamp(currentWidth + widthChange, 160, 2000);
        var height = Math.Clamp(currentHeight + heightChange, 40, 800);
        SizeEdited?.Invoke(this, new IslandSizeEditedEventArgs(width, height));
        RaiseTransformEdited(_state.OffsetX + _resizeDirection.X * widthChange / 2,
            _state.OffsetY + _resizeDirection.Y * heightChange / 2, _state.Scale, _state.Rotation);
        e.Handled = true;
    }

    private void ResizeHandleOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizing)
            return;
        _isResizing = false;
        e.Pointer.Capture(null);
        TransformEditCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void RotationHandleOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_rotationHandle).Properties.IsLeftButtonPressed)
            return;
        _isRotating = true;
        _lastPointerPosition = e.GetPosition(_stage);
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
        RaiseTransformEdited(_state.OffsetX, _state.OffsetY, _state.Scale, rotation);
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

    private void StageOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            RaiseTransformEdited(_state.OffsetX, _state.OffsetY, _state.Scale, _state.Rotation + e.Delta.Y * 3, true);
        else
            RaiseTransformEdited(_state.OffsetX, _state.OffsetY, Math.Clamp(_state.Scale + e.Delta.Y * 0.05, 0.1, 5), _state.Rotation, true);
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
        RaiseTransformEdited(x, y, _state.Scale, rotation, true);
        e.Handled = true;
    }

    private void RaiseTransformEdited(double offsetX, double offsetY, double scale, double rotation, bool isComplete = false)
    {
        TransformEdited?.Invoke(this, new IslandTransformEditedEventArgs(
            Math.Clamp(offsetX, -2000, 2000), Math.Clamp(offsetY, -2000, 2000),
            Math.Clamp(scale, 0.1, 5), Math.Clamp(rotation, -360, 360)));
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
}

internal sealed class IslandVisualEditorWindow : Window
{
    private bool _updatingInspector;
    public IslandVisualEditor Editor { get; } = new();
    public ColorPicker BackgroundColorPicker { get; } = new();
    public ToggleSwitch GradientToggle { get; } = new();
    public ColorPicker GradientEndColorPicker { get; } = new();
    public ToggleSwitch ShadowToggle { get; } = new();
    public ColorPicker ShadowColorPicker { get; } = new();
    public Slider ShadowBlurSlider { get; } = CreateSlider(0, 200, 1);
    public Slider ShadowOpacitySlider { get; } = CreateSlider(0, 1, .05);
    public Slider OpacitySlider { get; } = CreateSlider(0, 1, .05);
    public Slider CornerRadiusSlider { get; } = CreateSlider(0, 500, 1);

    public event EventHandler? ApplyRequested;
    public event EventHandler? CenterRequested;
    public event EventHandler? ResetRequested;
    public event Action<Color>? BackgroundColorEdited;
    public event Action<bool>? GradientEdited;
    public event Action<Color>? GradientEndColorEdited;
    public event Action<bool>? ShadowEdited;
    public event Action<Color>? ShadowColorEdited;
    public event Action<double>? ShadowBlurEdited;
    public event Action<double>? ShadowOpacityEdited;
    public event Action<double>? OpacityEdited;
    public event Action<double>? CornerRadiusEdited;

    public IslandVisualEditorWindow()
    {
        Title = "ClassIsland 可视化编辑器";
        Width = 1120;
        Height = 720;
        MinWidth = 820;
        MinHeight = 560;

        ConfigureInspectorEvents();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                IconButton("\uE161", "居中", () => CenterRequested?.Invoke(this, EventArgs.Empty)),
                IconButton("\uE161", "还原变形", () => ResetRequested?.Invoke(this, EventArgs.Empty)),
                IconButton("\uE424", "应用到主界面", () => ApplyRequested?.Invoke(this, EventArgs.Empty)),
                IconButton("\uE671", "关闭", Close)
            }
        };

        var inspector = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "格式", FontSize = 18, FontWeight = FontWeight.SemiBold },
                Label("整体不透明度", OpacitySlider),
                Label("圆角半径", CornerRadiusSlider),
                new TextBlock { Text = "背景", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Label("起始颜色", BackgroundColorPicker),
                Label("渐变", GradientToggle),
                Label("结束颜色", GradientEndColorPicker),
                new TextBlock { Text = "阴影", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Label("启用阴影", ShadowToggle),
                Label("阴影颜色", ShadowColorPicker),
                Label("模糊半径", ShadowBlurSlider),
                Label("阴影不透明度", ShadowOpacitySlider)
            }
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,280"),
            ColumnSpacing = 18,
            Children = { Editor, new ScrollViewer { Content = inspector } }
        };
        Grid.SetColumn(body.Children[1], 1);
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { toolbar, body }
        };
        Grid.SetRow(body, 1);
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
            ShadowBlurSlider.Value = state.ShadowBlur;
            ShadowOpacitySlider.Value = state.ShadowOpacity;
            OpacitySlider.Value = state.Opacity;
            CornerRadiusSlider.Value = state.CornerRadius;
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
        ShadowBlurSlider.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == Slider.ValueProperty) ShadowBlurEdited?.Invoke(ShadowBlurSlider.Value); };
        ShadowOpacitySlider.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == Slider.ValueProperty) ShadowOpacityEdited?.Invoke(ShadowOpacitySlider.Value); };
        OpacitySlider.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == Slider.ValueProperty) OpacityEdited?.Invoke(OpacitySlider.Value); };
        CornerRadiusSlider.PropertyChanged += (_, e) => { if (!_updatingInspector && e.Property == Slider.ValueProperty) CornerRadiusEdited?.Invoke(CornerRadiusSlider.Value); };
    }

    private static Control Label(string text, Control control) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = text, Opacity = .8 }, control }
    };

    private static Slider CreateSlider(double minimum, double maximum, double tick) => new()
    {
        Minimum = minimum, Maximum = maximum, TickFrequency = tick, IsSnapToTickEnabled = true
    };

    private static Button IconButton(string glyph, string text, Action action)
    {
        var button = new Button { Content = new IconText { Glyph = glyph, Text = text } };
        button.Click += (_, _) => action();
        return button;
    }
}

internal readonly record struct IslandPreviewState(
    double Opacity, double Scale, double Rotation, double OffsetX, double OffsetY, double CornerRadius,
    bool CustomSize, double Width, double Height, bool CustomBackground, Color BackgroundColor, bool Gradient,
    Color GradientEndColor, bool ShadowEnabled, Color ShadowColor, double ShadowBlur, double ShadowOffsetX,
    double ShadowOffsetY, double ShadowOpacity, bool BorderEnabled, Color BorderColor, double BorderThickness);

internal sealed class IslandTransformEditedEventArgs(double offsetX, double offsetY, double scale, double rotation) : EventArgs
{
    public double OffsetX { get; } = offsetX;
    public double OffsetY { get; } = offsetY;
    public double Scale { get; } = scale;
    public double Rotation { get; } = rotation;
}

internal sealed class IslandSizeEditedEventArgs(double width, double height) : EventArgs
{
    public double Width { get; } = width;
    public double Height { get; } = height;
}

internal sealed class IslandValueEditedEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}
