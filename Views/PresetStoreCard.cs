using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Controls;

namespace ClassIslandInjector.Views;

/// <summary>商店卡片样式（对应微软商店的不同卡位）。</summary>
internal enum StoreCardStyle
{
    /// <summary>横向滚动行内的卡（热门 / 最新上架）：宽 200，预览图 16:9 + 名称 + 获取按钮。</summary>
    Row,

    /// <summary>网格浏览卡：与 Row 类似但显示两行描述。</summary>
    Grid,

    /// <summary>Banner 右侧精选小卡：预览图铺满 + 底部渐变遮罩白字，整卡可点进详情。</summary>
    Side
}

/// <summary>
/// 商店预设卡片（微软商店应用卡样式）：圆角白卡，顶部预览图、名称 / 作者、底部「获取」按钮。
/// 预览图异步加载（磁盘 / 联机），未加载时显示强调色渐变占位。点击卡片进入详情页。
/// </summary>
internal sealed class StoreCard
{
    private readonly StorePresetEntry _entry;
    private readonly PresetStoreWindow _window;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsVisible = false };
    private readonly Shimmer _shimmer;
    private readonly Button _getButton = new();

    /// <summary>卡片根 Border。</summary>
    public Border Root { get; }

    /// <param name="returnPage">点击卡片进详情后的返回页（即卡片所在页）。</param>
    public StoreCard(StorePresetEntry entry, StoreCardStyle style, PresetStoreWindow window,
        PresetStoreWindow.PageKind returnPage)
    {
        _entry = entry;
        _window = window;
        // 动态骨架屏（宿主原生 Shimmer，ComponentPresenter 同款呼吸动画）：
        // 预览图未到位时呼吸闪烁，加载完成后 0.2s 渐显真实图并停止动画。
        _shimmer = new Shimmer
        {
            AutoDetectContentLoadState = false,
            Content = _image
        };

        var previewHeight = style == StoreCardStyle.Row ? 112 : 118;
        var previewBorder = new Border
        {
            Height = previewHeight,
            Child = _shimmer
        };

        var body = new StackPanel { Spacing = 0 };

        // 预览图（圆角卡片顶部：外层 CornerRadius + ClipToBounds 裁剪）。
        body.Children.Add(previewBorder);

        // 信息区。
        var info = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 4),
            Spacing = 2
        };
        info.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(new TextBlock
        {
            Text = entry.AuthorText,
            FontSize = 11,
            Opacity = 0.65,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (style == StoreCardStyle.Grid && !string.IsNullOrWhiteSpace(entry.Description))
        {
            info.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 11,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 30,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        body.Children.Add(info);

        // 底部行：包大小（淡） + 获取按钮。
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions { new(GridLength.Star), new(GridLength.Auto) },
            Margin = new Thickness(12, 2, 12, 12),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var sizeText = new TextBlock
        {
            Text = entry.SizeText == "—" ? "" : entry.SizeText,
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(sizeText, 0);
        footer.Children.Add(sizeText);
        _getButton.MinWidth = 76;
        _getButton.Height = 28;
        _getButton.FontSize = 12.5;
        _getButton.Padding = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(_getButton, 1);
        footer.Children.Add(_getButton);
        body.Children.Add(footer);

        Root = new Border
        {
            Width = style == StoreCardStyle.Side ? double.NaN : 200,
            Background = PresetStoreWindow.CardBrush(),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = body
        };

        window.BindGetButton(_getButton, entry);

        // 点击卡片（非按钮）进详情。
        Root.PointerReleased += (_, e) =>
        {
            if (e.Source is not Button)
            {
                window.OpenDetail(entry, returnPage);
            }
        };
        Root.PointerEntered += (_, _) => Root.Background = PresetStoreWindow.HoverCardBrush();
        Root.PointerExited += (_, _) => Root.Background = PresetStoreWindow.CardBrush();

        LoadPreview();
    }

    /// <summary>异步加载预览图，成功则填充 Image 并结束骨架动画；失败则停止呼吸露出静态底色。</summary>
    private void LoadPreview()
    {
        _ = PresetStoreWindow.LoadPreview(_entry, bytes =>
        {
            if (PresetStoreWindow.TryCreateBitmap(bytes, out var bitmap))
            {
                _image.Source = bitmap;
                _image.IsVisible = true;
                _shimmer.IsContentLoaded = true;
            }
            else
            {
                _shimmer.IsContentLoaded = true;
            }
        });
    }
}

/// <summary>固定位置的单卡槽（Banner 右侧精选小卡 / 编辑精选）：Bind 时替换内容为一张新卡。</summary>
internal sealed class StoreCardSlot
{
    /// <summary>槽位根 Border。</summary>
    public Border Root { get; }

    private Control? _current;

    public StoreCardSlot()
    {
        Root = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = PresetStoreWindow.PlaceholderBrush()
        };
    }

    /// <summary>绑定一个预设（生成 Side 样式卡：图铺满 + 底部渐变白字）。</summary>
    public void Bind(StorePresetEntry entry, PresetStoreWindow window)
    {
        _current = BuildSideCard(entry, window);
        Root.Child = _current;
    }

    /// <summary>清空槽位（恢复占位渐变）。</summary>
    public void Clear()
    {
        _current = null;
        Root.Child = null;
    }

    /// <summary>Banner 侧卡：预览图（Shimmer 骨架）铺满 + 底部渐变遮罩 + 白字名称（整卡可点进详情）。</summary>
    private static Border BuildSideCard(StorePresetEntry entry, PresetStoreWindow window)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var shimmer = new Shimmer
        {
            AutoDetectContentLoadState = false,
            Content = image
        };
        var placeholder = new Border
        {
            Background = PresetStoreWindow.PlaceholderBrush(),
            IsVisible = false, // Shimmer 呼吸层就是骨架底，图片失败后先靠它充当静态底色
            Child = new TextBlock
            {
                Text = entry.Name[..Math.Min(1, entry.Name.Length)],
                FontSize = 30,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var overlay = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(190, 0, 0, 0), 1)
                }
            },
            Padding = new Thickness(12, 28, 12, 10)
        };
        overlay.Child = new TextBlock
        {
            Text = entry.Name,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap
        };

        var card = new Border
        {
            Background = PresetStoreWindow.CardBrush(),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { shimmer, placeholder, overlay } }
        };

        card.PointerReleased += (_, e) =>
        {
            if (e.Source is not Button)
            {
                window.OpenDetail(entry, PresetStoreWindow.PageKind.Home);
            }
        };
        card.PointerEntered += (_, _) => card.Opacity = 0.92;
        card.PointerExited += (_, _) => card.Opacity = 1;

        _ = PresetStoreWindow.LoadPreview(entry, bytes =>
        {
            if (PresetStoreWindow.TryCreateBitmap(bytes, out var bitmap))
            {
                // 成功：显示真实图（盖住骨架底层），渐显动画由 Shimmer 处理。
                image.Source = bitmap;
                image.IsVisible = true;
            }
            else
            {
                // 失败：露出首字母占位作静态底。
                placeholder.IsVisible = true;
            }

            // 无论成败都停止呼吸动画。
            shimmer.IsContentLoaded = true;
        });

        return card;
    }
}
