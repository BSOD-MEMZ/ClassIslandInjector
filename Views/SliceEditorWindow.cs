using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassIsland.Core.Controls;
using System.Globalization;

namespace ClassIslandInjector.Views;

/// <summary>
/// 九宫格切图编辑器：直接在「那张图片」上框选上 / 下 / 左 / 右四条切边（相对图片像素），
/// 拖动参考线实时更新切边，底部实时预览九宫格拉伸效果。
/// </summary>
internal sealed class SliceEditorWindow : MyWindow
{
    private readonly WallpaperLayerItem _layer;
    private readonly Bitmap _bitmap;
    private readonly SliceOverlayControl _sliceOverlay;
    private readonly WallpaperNineSliceVisual _preview;

    public SliceEditorWindow(WallpaperLayerItem layer, Bitmap bitmap)
    {
        _layer = layer;
        _bitmap = bitmap;
        Title = "九宫格切图";
        Width = 680;
        Height = 600;
        MinWidth = 480;
        MinHeight = 420;
        Background = ThemePalette.WindowBackground();

        var bw = Math.Max(1, bitmap.PixelSize.Width);
        var bh = Math.Max(1, bitmap.PixelSize.Height);
        // 图片缩放到窗口可用空间（保持原始比例）。
        var scale = Math.Min(600.0 / bw, 380.0 / bh);
        var imgW = Math.Max(1, bw * scale);
        var imgH = Math.Max(1, bh * scale);

        var image = new Image
        {
            Source = bitmap,
            Width = imgW,
            Height = imgH,
            Stretch = Stretch.Fill
        };
        _sliceOverlay = new SliceOverlayControl
        {
            Width = imgW,
            Height = imgH,
            ImageWidth = bw,
            ImageHeight = bh,
            Scale = scale,
            SliceLeft = layer.SliceLeft,
            SliceTop = layer.SliceTop,
            SliceRight = layer.SliceRight,
            SliceBottom = layer.SliceBottom
        };
        _sliceOverlay.SliceChanged += () =>
        {
            layer.SliceLeft = _sliceOverlay.SliceLeft;
            layer.SliceTop = _sliceOverlay.SliceTop;
            layer.SliceRight = _sliceOverlay.SliceRight;
            layer.SliceBottom = _sliceOverlay.SliceBottom;
            UpdatePreview();
        };
        UpdatePreview();

        // 底部：九宫格拉伸实时预览 + 提示 + 完成。
        _preview = new WallpaperNineSliceVisual
        {
            Bitmap = bitmap,
            SliceEnabled = true,
            Height = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var hint = new TextBlock
        {
            Text = "在图片上拖动四条虚线调整切边：四角保持原样不变形，四边沿单轴拉伸，中间区域双轴拉伸铺满。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12
        };
        var doneButton = new Button
        {
            Content = "完成",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6)
        };
        doneButton.Click += (_, _) => Close();

        var imageHost = new Border
        {
            Background = ThemePalette.PanelBackground(),
            BorderBrush = ThemePalette.SurfaceBorder(),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Grid { Children = { image, _sliceOverlay } }
            }
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 10,
            Margin = new Thickness(14),
            Children =
            {
                imageHost,
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "拉伸预览", FontSize = 13, FontWeight = FontWeight.SemiBold },
                        new Border
                        {
                            Background = ThemePalette.PanelBackground(),
                            BorderBrush = ThemePalette.SurfaceBorder(),
                            BorderThickness = new Thickness(1),
                            Child = _preview
                        },
                        hint,
                        doneButton
                    }
                }
            }
        };
    }

    private void UpdatePreview()
    {
        _preview.SliceEnabled = true;
        _preview.SliceLeft = _layer.SliceLeft;
        _preview.SliceTop = _layer.SliceTop;
        _preview.SliceRight = _layer.SliceRight;
        _preview.SliceBottom = _layer.SliceBottom;
        _preview.InvalidateVisual();
    }

    /// <summary>图片上的切边参考线：坐标 = 图片显示区域，按 Scale 换算到图片像素。</summary>
    private sealed class SliceOverlayControl : Control
    {
        private const double HitRadius = 10;
        private int _dragIndex = -1;
        private Point _lastPos;

        public double SliceLeft { get; set; }
        public double SliceTop { get; set; }
        public double SliceRight { get; set; }
        public double SliceBottom { get; set; }
        public double ImageWidth { get; set; } = 1;
        public double ImageHeight { get; set; } = 1;
        public double Scale { get; set; } = 1;

        public event Action? SliceChanged;

        public SliceOverlayControl()
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                var pos = e.GetPosition(this);
                _dragIndex = FindNearest(pos);
                if (_dragIndex < 0)
                {
                    return;
                }

                _lastPos = pos;
                e.Pointer.Capture(this);
                e.Handled = true;
            };
            PointerMoved += (_, e) =>
            {
                if (_dragIndex < 0)
                {
                    return;
                }

                var pos = e.GetPosition(this);
                var dx = (pos.X - _lastPos.X) / Scale;
                var dy = (pos.Y - _lastPos.Y) / Scale;
                _lastPos = pos;
                switch (_dragIndex)
                {
                    case 0: SliceLeft = ClampSlice(SliceLeft + dx, ImageWidth); break;
                    case 1: SliceTop = ClampSlice(SliceTop + dy, ImageHeight); break;
                    case 2: SliceRight = ClampSlice(SliceRight - dx, ImageWidth); break;
                    case 3: SliceBottom = ClampSlice(SliceBottom - dy, ImageHeight); break;
                }

                SliceChanged?.Invoke();
                InvalidateVisual();
                e.Handled = true;
            };
            PointerReleased += (_, e) =>
            {
                if (_dragIndex < 0)
                {
                    return;
                }

                _dragIndex = -1;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        private static double ClampSlice(double value, double imageSize) =>
            Math.Clamp(value, 0, Math.Max(0, imageSize / 2 - 1));

        private int FindNearest(Point pos)
        {
            var lx = LineX(SliceLeft);
            var rx = LineX(SliceRight, true);
            var ty = LineY(SliceTop);
            var by = LineY(SliceBottom, true);
            var distances = new[] { Math.Abs(pos.X - lx), Math.Abs(pos.Y - ty), Math.Abs(pos.X - rx), Math.Abs(pos.Y - by) };
            var best = -1;
            var bestDist = HitRadius;
            for (var i = 0; i < distances.Length; i++)
            {
                if (distances[i] < bestDist)
                {
                    bestDist = distances[i];
                    best = i;
                }
            }

            return best;
        }

        private double LineX(double slice, bool fromRight = false)
        {
            var rel = slice / ImageWidth * Bounds.Width;
            return fromRight ? Bounds.Width - rel : rel;
        }

        private double LineY(double slice, bool fromBottom = false)
        {
            var rel = slice / ImageHeight * Bounds.Height;
            return fromBottom ? Bounds.Height - rel : rel;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }

            // 半透明遮罩（切边外的部分压暗，突出九宫格中心区域）。
            var lx = LineX(SliceLeft);
            var rx = LineX(SliceRight, true);
            var ty = LineY(SliceTop);
            var by = LineY(SliceBottom, true);
            var dim = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            context.DrawRectangle(dim, null, new Rect(0, 0, lx, Bounds.Height));
            context.DrawRectangle(dim, null, new Rect(rx, 0, Bounds.Width - rx, Bounds.Height));
            context.DrawRectangle(dim, null, new Rect(lx, 0, rx - lx, ty));
            context.DrawRectangle(dim, null, new Rect(lx, by, rx - lx, Bounds.Height - by));

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 61, 194)), 1.5)
            {
                DashStyle = new DashStyle([6, 4], 0)
            };
            context.DrawLine(pen, new Point(lx, 0), new Point(lx, Bounds.Height));
            context.DrawLine(pen, new Point(rx, 0), new Point(rx, Bounds.Height));
            context.DrawLine(pen, new Point(0, ty), new Point(Bounds.Width, ty));
            context.DrawLine(pen, new Point(0, by), new Point(Bounds.Width, by));

            // 四角标签（像素值）。
            var labelBrush = new SolidColorBrush(Color.FromRgb(255, 61, 194));
            DrawLabel(context, $"左 {SliceLeft:0}px", new Point(lx + 4, 4), labelBrush);
            DrawLabel(context, $"右 {SliceRight:0}px", new Point(Math.Min(Bounds.Width - 80, rx + 4), 4), labelBrush);
            DrawLabel(context, $"上 {SliceTop:0}px", new Point(4, ty + 4), labelBrush);
            DrawLabel(context, $"下 {SliceBottom:0}px", new Point(4, Math.Min(Bounds.Height - 20, by + 4)), labelBrush);
        }

        private static void DrawLabel(DrawingContext context, string text, Point pos, IBrush brush)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, brush);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 20, 20, 24)), null,
                new Rect(pos.X, pos.Y, ft.Width + 8, ft.Height + 3), 0, 0, default);
            context.DrawText(ft, new Point(pos.X + 4, pos.Y + 1.5));
        }
    }
}
