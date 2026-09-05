using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core;

namespace ClassIslandInjector.Views;

/// <summary>
/// 微软商店风格侧边导航栏：照 QFluentWidgets <c>NavigationBar</c> / <c>NavigationBarPushButton</c>
/// 的绘制规格用 Avalonia 原生控件组合实现 ——
/// 按钮 64x58、圆角 5；图标 20x20 顶部居中（y≈13）；文字 11px 底部（y≈32）；
/// 选中 = 白（浅）/ rgba(255,255,255,42)（深）底 + 左侧 4x24 圆角 2 强调色指示条 + 实心图标 + 强调色文字；
/// hover = rgba(0/255, 9)；按下 = rgba(0/255, 6)；未选中常态图标 0.6 透明度（hover 恢复 1）。
/// </summary>
internal sealed class StoreNavBar : StackPanel
{
    private readonly Dictionary<PresetStoreWindow.PageKind, StoreNavBarButton> _buttons = [];

    public StoreNavBar()
    {
        Orientation = Orientation.Vertical;
        Spacing = 4;
        Margin = new Thickness(4, 4, 4, 4);
    }

    /// <summary>添加一个导航项（regular / filled 实心图标码点成对，选中时切换实心）。</summary>
    public void AddItem(PresetStoreWindow.PageKind kind, char glyph, char glyphFilled, string text, Action onClick)
    {
        var button = new StoreNavBarButton(glyph, glyphFilled, text);
        ToolTip.SetTip(button, text);
        button.Click += onClick;
        _buttons[kind] = button;
        Children.Add(button);
    }

    /// <summary>设置选中项（其余全部取消）。</summary>
    public void SetSelected(PresetStoreWindow.PageKind kind)
    {
        foreach (var (k, button) in _buttons)
        {
            button.SetSelected(k == kind);
        }
    }
}

/// <summary>微软商店风格导航按钮（QFluentWidgets NavigationBarPushButton 规格，手绘状态）。</summary>
internal sealed class StoreNavBarButton : Border
{
    private readonly TextBlock _iconText;
    private readonly TextBlock _label;
    private readonly Border _indicator;
    private readonly char _glyph;
    private readonly char _glyphFilled;
    private bool _isSelected;
    private bool _isHover;
    private bool _isPressed;

    /// <summary>按钮被点击（按下并在按钮内释放）。</summary>
    public event Action? Click;

    public StoreNavBarButton(char glyph, char glyphFilled, string text)
    {
        _glyph = glyph;
        _glyphFilled = glyphFilled;

        Width = 64;
        Height = 58;
        CornerRadius = new CornerRadius(5);
        Background = Brushes.Transparent;

        // 左侧选中指示条：4x24、圆角 2、强调色、垂直居中（QFW drawRoundedRect(0, 16, 4, 24)）。
        _indicator = new Border
        {
            Width = 4,
            Height = 24,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(ThemePalette.AccentColor()),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };

        _iconText = new TextBlock
        {
            Text = glyph.ToString(),
            FontFamily = AppBase.FluentIconsFontFamily,
            FontSize = 20,
            // 固定 20px 宽 + 居中对齐（QFW 在固定 20x20 矩形内绘制图标）：regular/filled 字形
            // advance 略有差异，固定宽度才能保证两种态下图标都横向居中。
            Width = 20,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };
        content.Children.Add(_iconText);
        content.Children.Add(_label);

        var grid = new Grid();
        grid.Children.Add(_indicator);
        grid.Children.Add(content);
        Child = grid;

        PointerEntered += (_, _) =>
        {
            _isHover = true;
            UpdateVisual();
        };
        PointerExited += (_, _) =>
        {
            _isHover = false;
            _isPressed = false;
            UpdateVisual();
        };
        PointerPressed += (_, _) =>
        {
            _isPressed = true;
            UpdateVisual();
        };
        PointerReleased += (_, _) =>
        {
            var wasPressed = _isPressed;
            _isPressed = false;
            UpdateVisual();
            if (wasPressed)
            {
                Click?.Invoke();
            }
        };
    }

    /// <summary>设置选中态（切换背景 / 指示条 / 实心图标 / 文字颜色）。</summary>
    public void SetSelected(bool selected)
    {
        if (_isSelected == selected)
        {
            return;
        }

        _isSelected = selected;
        UpdateVisual();
    }

    /// <summary>按 QFluentWidgets 的规格刷新视觉（浅/深色各自的底色与透明度）。</summary>
    private void UpdateVisual()
    {
        try
        {
            UpdateVisualCore();
        }
        catch
        {
            // 视觉刷新失败不应把按钮从命中树上抹掉（曾出现 hover 时按钮消失的反馈），
            // 这里保底恢复可见状态。
            _isHover = false;
            _isPressed = false;
        }
    }

    /// <summary>按 QFluentWidgets 的规格刷新视觉的真实实现。</summary>
    private void UpdateVisualCore()
    {
        var dark = ThemePalette.IsDarkTheme();
        if (_isSelected)
        {
            // 选中：白（浅）/ rgba(255,255,255,42)（深）圆角底 + 强调色指示条 + 实心图标（主题色填充）+ 强调色文字。
            Background = dark
                ? new SolidColorBrush(Color.FromArgb(42, 255, 255, 255))
                : new SolidColorBrush(Colors.White);
            _indicator.IsVisible = true;
            _iconText.Text = _glyphFilled.ToString();
            _iconText.Opacity = 1;
            _iconText.Foreground = new SolidColorBrush(ThemePalette.AccentColor());
            _label.Foreground = new SolidColorBrush(ThemePalette.AccentColor());
            return;
        }

        _indicator.IsVisible = false;
        _iconText.Text = _glyph.ToString();
        // 未选中：常态图标 0.6 透明度（hover 恢复 1，QFW _drawIcon 规格），前景恢复主题默认。
        // 注意：Avalonia 中 `Foreground = null` 是设置局部值 null，会切断主题/继承
        // （图标文字直接不可见 = navbar 按钮消失的根因），必须用 ClearValue 恢复继承。
        _iconText.Opacity = _isHover ? 1 : 0.6;
        _iconText.ClearValue(TextBlock.ForegroundProperty);
        _label.ClearValue(TextBlock.ForegroundProperty);

        // 背景始终非 null（Transparent）：null 会让 Border 命中区塌缩到字形上，
        // 鼠标在按钮表面滑动时命中区反复出现/消失，指针事件抖动导致按钮视觉异常。
        Background = _isPressed
            ? PressedBrush(dark)
            : _isHover ? HoverBrush(dark) : Brushes.Transparent;
    }

    /// <summary>hover 底色：浅色 rgba(0,0,0,9) / 深色 rgba(255,255,255,9)。</summary>
    private static IBrush HoverBrush(bool dark) => new SolidColorBrush(
        dark ? Color.FromArgb(9, 255, 255, 255) : Color.FromArgb(9, 0, 0, 0));

    /// <summary>按下底色：浅色 rgba(0,0,0,6) / 深色 rgba(255,255,255,6)。</summary>
    private static IBrush PressedBrush(bool dark) => new SolidColorBrush(
        dark ? Color.FromArgb(6, 255, 255, 255) : Color.FromArgb(6, 0, 0, 0));
}
