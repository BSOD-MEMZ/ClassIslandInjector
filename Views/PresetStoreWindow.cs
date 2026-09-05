using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace ClassIslandInjector.Views;

/// <summary>
/// 预设商店：1:1 仿新版微软商店的联机预设浏览 / 下载窗口。
/// 布局 = 自定义标题栏（商店标识 + 居中胶囊搜索框 + 刷新，右上留系统 caption 按钮）+ 左侧窄导航
/// （首页 / 全部预设 / 热门 / 我的预设）+ 首页（Banner 轮播 + 右侧精选小卡 + 「热门预设」「最新上架」横向卡行
/// + 「编辑精选」大卡）+ 网格浏览页（搜索 / 排序）+ 热门网格页 + 已安装列表页 + 详情页。
/// 数据来自 <see cref="PresetStoreService"/>（索引 / 预览图 / 预设包联机下载），
/// 下载完成后经 <see cref="PresetInstallDialog"/> 确认导入用户预设列表（与「双击 .cizip 安装」共用流程）。
/// </summary>
internal sealed class PresetStoreWindow : MyWindow
{
    #region 图标码点（FluentSystemIcons-Resizable，映射见 tools\FluentSystemIcons-Resizable.json）

    private const char IconHome = (char)59796;          // home regular
    private const char IconHomeFilled = (char)59795;    // home filled
    private const char IconApps = (char)57455;          // apps regular
    private const char IconAppsFilled = (char)57454;    // apps filled
    private const char IconFire = (char)59453;          // fire regular
    private const char IconFireFilled = (char)59452;    // fire filled
    private const char IconLibrary = (char)60034;       // library regular
    private const char IconLibraryFilled = (char)60033; // library filled
    private const char IconSearch = (char)61171;        // search regular
    private const char IconRefresh = (char)57525;       // arrow_clockwise regular
    private const char IconChevronLeft = (char)58444;   // chevron_left regular
    private const char IconChevronRight = (char)58446;  // chevron_right regular
    private const char IconCheckCircle = (char)58406;   // checkmark_circle regular
    private const char IconBag = (char)61319;           // shopping_bag regular
    private const char IconPaintBrush = (char)60490;    // paint_brush regular

    #endregion

    /// <summary>导航页索引。</summary>
    internal enum PageKind { Home = 0, Browse = 1, Hot = 2, Mine = 3, Detail = 4 }

    /// <summary>排序方式。</summary>
    private enum StoreSort { Latest, Downloads, Name }

    /// <summary>当前打开的商店窗口（单实例）。</summary>
    public static PresetStoreWindow? Current { get; private set; }

    private StoreIndex? _index;
    private PageKind _page = PageKind.Home;
    private PageKind _detailReturnPage = PageKind.Home;
    private StorePresetEntry? _detailEntry;

    /// <summary>安装进行中的条目 Id（防重复点击）。</summary>
    private readonly HashSet<string> _installingIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已绑定的获取按钮 → 当前条目（banner 按钮会随轮播重绑不同条目）。</summary>
    private readonly Dictionary<Button, StorePresetEntry> _getButtonEntries = new();

    /// <summary>是否已给按钮挂过 Click（每个按钮只挂一次）。</summary>
    private readonly HashSet<Button> _boundButtons = [];

    // —— Banner 轮播（原生 Carousel）——
    private readonly List<StorePresetEntry> _bannerEntries = [];
    private readonly List<Border> _bannerDots = [];
    private StackPanel? _bannerDotsPanel;
    private DispatcherTimer? _bannerTimer;
    private Carousel? _carousel;

    // —— 页面容器与控件 ——
    private readonly Dictionary<PageKind, Control> _pages = [];

    /// <summary>微软商店风格侧边导航栏（QFluentWidgets NavigationBar 规格）。</summary>
    private StoreNavBar? _navBar;
    private readonly List<StoreCardSlot> _sideSlots = [];
    private ItemsRepeater? _browseRepeater;
    private TextBlock? _browseEmpty;
    private ItemsRepeater? _hotRepeater;

    /// <summary>页面容器（承载当前页与退场旧页，ClipToBounds 防滑动溢出）。</summary>
    private Grid? _pageContainer;

    /// <summary>当前页面（页面对象缓存于 _pages，切换时在容器内做双页滑动动画）。</summary>
    private Control? _currentPageContent;

    /// <summary>标题栏返回按钮（进入详情页时显示，照微软商店：返回在标题栏上）。</summary>
    private Button? _backButton;
    private StackPanel? _mineList;
    private StackPanel? _hotRowHost;
    private StackPanel? _latestRowHost;
    private ScrollViewer? _hotRowScroll;
    private ScrollViewer? _latestRowScroll;
    private StoreCardSlot? _featuredSlot;
    private ComboBox? _sortBox;
    private TextBox? _searchBox;
    private InfoBar? _statusBar;
    private ProgressRing? _loadingRing;

    // —— 详情页控件 ——
    private Border? _detailBanner;
    private Image? _detailIconImage;
    private TextBlock? _detailTitle;
    private TextBlock? _detailAuthor;
    private TextBlock? _detailMetaText;
    private TextBlock? _detailDescription;
    private TextBlock? _detailNote;
    private Button? _detailGetButton;
    private Shimmer? _detailShimmer;
    private Image? _detailPreviewImage;
    private StackPanel? _moreList;
    private StackPanel? _moreSection;

    public PresetStoreWindow()
    {
        Title = "预设商店";
        Width = 1128;
        Height = 768;
        MinWidth = 880;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 1:1 微软商店：内容延伸进标题栏，FA TitleBar 复杂命中测试（非透明控件可交互、空白拖动窗口），
        // 右上角系统 caption 按钮由 AppWindow 模板绘制（宿主 SettingsWindowNew 同款配置）。
        EditorMica.EnableMica(this);
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;
        TitleBar.Height = 48;

        Content = BuildRoot();
        Opened += (_, _) =>
        {
            Current = this;
            _ = LoadIndexAsync(forceRefresh: false);
        };
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }

            _bannerTimer?.Stop();
        };
    }

    #region 根布局

    /// <summary>
    /// MSFluentWindow 式根布局（照 QFluentWidgets 源码）：标题栏横跨全宽（48px，透出 Mica），
    /// 下方 = 侧边导航栏（72px，图标上文字下）+ 内容区；系统 caption 按钮由 AppWindow 浮动右上。
    /// </summary>
    private Control BuildRoot()
    {
        var root = new Grid { RowDefinitions = Rows(Px(48), Star) };
        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new Grid { ColumnDefinitions = Cols(Px(72), Star) };
        var nav = BuildNav();
        Grid.SetColumn(nav, 0);
        body.Children.Add(nav);
        var content = BuildContentArea();
        Grid.SetColumn(content, 1);
        body.Children.Add(content);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    /// <summary>
    /// 标题栏（照 QFluentWidgets CustomTitleBar 规格 + 微软商店详情页）：返回按钮（详情页时显示，
    /// 标题栏最左）+ 窗口图标 18x18 + 标题，中间搜索框固定 400 宽居中，右侧刷新 + caption 留白。
    /// </summary>
    private Grid BuildTitleBar()
    {
        var bar = new Grid { ColumnDefinitions = Cols(Px(8), Auto, Px(20), Auto, Star, Auto, Px(150)) };

        // 返回按钮（照微软商店：详情页时返回箭头出现在标题栏最左侧；平时隐藏）。
        _backButton = IconButton(IconChevronLeft, "返回");
        _backButton.IsVisible = false;
        _backButton.Click += (_, _) => Navigate(_detailReturnPage);
        Grid.SetColumn(_backButton, 0);
        bar.Children.Add(_backButton);

        // 左：窗口图标 + 标题。
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10
        };
        brand.Children.Add(GlyphIcon(IconBag, 18));
        brand.Children.Add(new TextBlock
        {
            Text = "预设商店",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(brand, 3);
        bar.Children.Add(brand);

        // 中：搜索框（QFW 规格：固定 400 宽，原生样式 + 左侧图标内嵌）。
        _searchBox = new TextBox
        {
            Watermark = "搜索预设、作者",
            Width = 400,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        _searchBox.InnerLeftContent = new Border
        {
            Padding = new Thickness(10, 0, 0, 0),
            Child = GlyphIcon(IconSearch, 13, 0.6)
        };
        _searchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text))
            {
                OnSearchChanged();
            }
        };
        Grid.SetColumn(_searchBox, 4);
        bar.Children.Add(_searchBox);

        var refresh = IconButton(IconRefresh, "重新获取商店数据");
        refresh.Margin = new Thickness(8, 0, 4, 0);
        refresh.Click += (_, _) => _ = LoadIndexAsync(forceRefresh: true);
        Grid.SetColumn(refresh, 5);
        bar.Children.Add(refresh);

        // 最右列留白：系统 caption 按钮（最小化 / 最大化 / 关闭）。
        var captionFiller = new Border();
        Grid.SetColumn(captionFiller, 6);
        bar.Children.Add(captionFiller);
        return bar;
    }

    /// <summary>内容区：页面容器（旧页上滑淡出、新页自下滑入的双页动画）+ 加载指示。</summary>
    private Control BuildContentArea()
    {
        var root = new Grid();

        // 页面容器（ClipToBounds 防滑动溢出到导航栏）。
        _pageContainer = new Grid { ClipToBounds = true };
        _pages[PageKind.Home] = BuildHomePage();
        _pages[PageKind.Browse] = BuildBrowsePage();
        _pages[PageKind.Hot] = BuildHotPage();
        _pages[PageKind.Mine] = BuildMinePage();
        _pages[PageKind.Detail] = BuildDetailPage();
        _currentPageContent = _pages[PageKind.Home];
        _pageContainer.Children.Add(_currentPageContent);
        root.Children.Add(_pageContainer);

        // 加载指示（覆盖整个内容区，任意页面刷新都可见）。
        _loadingRing = new ProgressRing
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        root.Children.Add(_loadingRing);
        return root;
    }

    /// <summary>侧边导航栏（QFluentWidgets NavigationBar 规格：图标上文字下）。</summary>
    private StoreNavBar BuildNav()
    {
        _navBar = new StoreNavBar();
        _navBar.AddItem(PageKind.Home, IconHome, IconHomeFilled, "首页", () => Navigate(PageKind.Home));
        _navBar.AddItem(PageKind.Browse, IconApps, IconAppsFilled, "全部预设", () => Navigate(PageKind.Browse));
        _navBar.AddItem(PageKind.Hot, IconFire, IconFireFilled, "热门", () => Navigate(PageKind.Hot));
        _navBar.AddItem(PageKind.Mine, IconLibrary, IconLibraryFilled, "我的预设", () => Navigate(PageKind.Mine));
        _navBar.SetSelected(PageKind.Home);
        return _navBar;
    }

    #endregion

    #region 首页

    /// <summary>首页：状态条 + Banner 区（左轮播 + 右三小卡）+ 「热门预设」行 + 底部（编辑精选 + 「最新上架」行）。</summary>
    private Grid BuildHomePage()
    {
        var content = new StackPanel { Margin = new Thickness(20, 10, 20, 24), Spacing = 22 };

        _statusBar = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = false, IsClosable = true };
        content.Children.Add(_statusBar);

        // Banner 区：左大轮播（2.2 星）+ 右列（上 1 大卡 / 下 2 小卡）。
        var bannerGrid = new Grid
        {
            ColumnDefinitions = Cols(StarOf(2.2), Star),
            ColumnSpacing = 12,
            Height = 300
        };
        var carousel = BuildBannerCarousel();
        Grid.SetColumn(carousel, 0);
        bannerGrid.Children.Add(carousel);

        var sideColumn = new Grid { RowDefinitions = Rows(StarOf(1.25), Star), RowSpacing = 12 };
        var sideTop = new Grid { ColumnDefinitions = Cols(Star) };
        _sideSlots.Add(AddSlot(sideTop, 0, 0));
        var sideBottom = new Grid { ColumnDefinitions = Cols(Star, Star), ColumnSpacing = 12 };
        _sideSlots.Add(AddSlot(sideBottom, 0, 0));
        _sideSlots.Add(AddSlot(sideBottom, 1, 0));
        Grid.SetRow(sideTop, 0);
        sideColumn.Children.Add(sideTop);
        Grid.SetRow(sideBottom, 1);
        sideColumn.Children.Add(sideBottom);
        Grid.SetColumn(sideColumn, 1);
        bannerGrid.Children.Add(sideColumn);
        content.Children.Add(bannerGrid);

        // 「热门预设」横向行。
        var hotSection = BuildHorizontalSection("热门预设", out var hotRowHost, out var hotRowScroll);
        _hotRowHost = hotRowHost;
        _hotRowScroll = hotRowScroll;
        content.Children.Add(hotSection);

        // 底部：编辑精选大卡 + 「最新上架」行。
        var bottom = new Grid { ColumnDefinitions = Cols(Px(360), Star), ColumnSpacing = 16 };
        _featuredSlot = new StoreCardSlot();
        // 卡槽在 StackPanel 内没有行高可拉伸，需显式定高（侧栏卡靠 Grid 星行自动填满，无需此设置）。
        _featuredSlot.Root.Height = 216;
        var featuredHost = new StackPanel { Spacing = 12 };
        featuredHost.Children.Add(new TextBlock
        {
            Text = "编辑精选",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold
        });
        featuredHost.Children.Add(_featuredSlot.Root);
        Grid.SetColumn(featuredHost, 0);
        bottom.Children.Add(featuredHost);

        var latestSection = BuildHorizontalSection("最新上架", out var latestRowHost, out var latestRowScroll);
        _latestRowHost = latestRowHost;
        _latestRowScroll = latestRowScroll;
        Grid.SetColumn(latestSection, 1);
        bottom.Children.Add(latestSection);
        content.Children.Add(bottom);

        var root = new Grid();
        root.Children.Add(new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        return root;
    }

    /// <summary>往 Grid 指定单元格放一个可绑定卡槽。</summary>
    private StoreCardSlot AddSlot(Grid grid, int column, int row)
    {
        var slot = new StoreCardSlot();
        Grid.SetColumn(slot.Root, column);
        Grid.SetRow(slot.Root, row);
        grid.Children.Add(slot.Root);
        return slot;
    }

    /// <summary>
    /// Banner 轮播（原生 <see cref="Carousel"/>，WinUI FlipView 的 Avalonia 对应物）：
    /// 每页 = 预览图（Shimmer 骨架）+ 底部渐变遮罩（标题 / 描述 / 获取按钮），自带 PageSlide 滑动过渡；
    /// 外层叠加圆点指示器与原生圆形翻页按钮。
    /// </summary>
    private Border BuildBannerCarousel()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = PlaceholderBrush()
        };

        _carousel = new Carousel
        {
            PageTransition = new PageSlide(TimeSpan.FromMilliseconds(400)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _carousel.SelectionChanged += (_, _) => UpdateBannerDots();

        _bannerDotsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var prev = IconButton(IconChevronLeft, "上一个");
        prev.HorizontalAlignment = HorizontalAlignment.Left;
        prev.VerticalAlignment = VerticalAlignment.Center;
        prev.Margin = new Thickness(10, 0, 0, 0);
        prev.Click += (_, _) => StepBanner(-1);
        var next = IconButton(IconChevronRight, "下一个");
        next.HorizontalAlignment = HorizontalAlignment.Right;
        next.VerticalAlignment = VerticalAlignment.Center;
        next.Margin = new Thickness(0, 0, 10, 0);
        next.Click += (_, _) => StepBanner(1);

        var grid = new Grid();
        grid.Children.Add(_carousel);
        grid.Children.Add(_bannerDotsPanel);
        grid.Children.Add(prev);
        grid.Children.Add(next);
        root.Child = grid;

        root.PointerEntered += (_, _) => _bannerTimer?.Stop();
        root.PointerExited += (_, _) => _bannerTimer?.Start();
        return root;
    }

    /// <summary>构建一页 banner：预览图（Shimmer 骨架）+ 渐变遮罩（标题 / 描述 / 获取）；点击页空白处进详情。</summary>
    private Border BuildBannerPage(StorePresetEntry entry)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var shimmer = new Shimmer
        {
            CornerRadius = new CornerRadius(8),
            AutoDetectContentLoadState = false,
            Content = image
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
                    new GradientStop(Color.FromArgb(210, 0, 0, 0), 1)
                }
            },
            Padding = new Thickness(24, 64, 24, 20)
        };
        var textColumn = new StackPanel { Spacing = 6 };
        textColumn.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Description) ? entry.AuthorText : entry.Description,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var action = new Button { MinWidth = 96, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
        action.Classes.Add("AccentButtonStyle");
        BindGetButton(action, entry);
        textColumn.Children.Add(action);
        overlay.Child = textColumn;

        var page = new Border
        {
            Background = PlaceholderBrush(),
            Child = new Grid { Children = { shimmer, overlay } },
            Tag = entry
        };
        page.PointerReleased += (_, e) =>
        {
            // 点击空白处（非按钮）打开该预设详情。
            if (e.Source is not Button)
            {
                OpenDetail(entry, PageKind.Home);
            }
        };

        // 预览图加载：成功则渐显真实图；失败则停止呼吸露出静态渐变底。
        _ = LoadPreview(entry, bytes =>
        {
            if (TryCreateBitmap(bytes, out var bitmap))
            {
                image.Source = bitmap;
                image.IsVisible = true;
                shimmer.IsContentLoaded = true;
            }
            else
            {
                shimmer.IsContentLoaded = true;
            }
        });
        return page;
    }

    /// <summary>按 Carousel 当前页刷新圆点指示器。</summary>
    private void UpdateBannerDots()
    {
        var index = _carousel?.SelectedIndex ?? -1;
        for (var i = 0; i < _bannerDots.Count; i++)
        {
            _bannerDots[i].Width = i == index ? 18 : 6;
            _bannerDots[i].Background = i == index
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        }
    }

    /// <summary>「标题 + 左右圆形翻页按钮 + 横向卡行」区块（微软商店「装机必备」样式）。</summary>
    private StackPanel BuildHorizontalSection(string title, out StackPanel rowHost, out ScrollViewer scroller)
    {
        var section = new StackPanel { Spacing = 12 };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(GlyphIcon(IconChevronRight, 14, 0.8));

        var prev = IconButton(IconChevronLeft, "向左翻页");
        var next = IconButton(IconChevronRight, "向右翻页");
        var headerGrid = new Grid { ColumnDefinitions = Cols(Auto, Star, Auto, Auto) };
        Grid.SetColumn(header, 0);
        headerGrid.Children.Add(header);
        var filler = new Border();
        Grid.SetColumn(filler, 1);
        headerGrid.Children.Add(filler);
        Grid.SetColumn(prev, 2);
        headerGrid.Children.Add(prev);
        Grid.SetColumn(next, 3);
        headerGrid.Children.Add(next);
        section.Children.Add(headerGrid);

        rowHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        scroller = new ScrollViewer
        {
            Content = rowHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        section.Children.Add(scroller);

        // out 参数不能被 lambda 捕获，拷贝到局部变量再订阅翻页事件。
        var scrollerLocal = scroller;
        prev.Click += (_, _) => PageScroll(scrollerLocal, -1);
        next.Click += (_, _) => PageScroll(scrollerLocal, 1);
        return section;
    }

    #endregion

    #region 浏览 / 热门 / 我的

    /// <summary>浏览页：标题 + 排序下拉 + 网格（搜索过滤）。</summary>
    private ScrollViewer BuildBrowsePage()
    {
        _sortBox = new ComboBox
        {
            MinWidth = 136,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        _sortBox.ItemsSource = new List<string> { "最新上架", "最多下载", "名称" };
        _sortBox.SelectedIndex = 0;
        _sortBox.SelectionChanged += (_, _) => RebuildBrowseGrid();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(0, 0, 0, 16)
        };
        header.Children.Add(new TextBlock
        {
            Text = "全部预设",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(_sortBox);

        _browseRepeater = MakeGridRepeater(PageKind.Browse);
        _browseEmpty = new TextBlock
        {
            Text = "没有匹配的预设。",
            Opacity = 0.6,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false
        };

        var content = new StackPanel { Margin = new Thickness(20, 12, 20, 24) };
        content.Children.Add(header);
        content.Children.Add(_browseRepeater);
        content.Children.Add(_browseEmpty);
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>
    /// 构建网格列表（FA 原生 <see cref="ItemsRepeater"/> + <see cref="UniformGridLayout"/>：
    /// 自动换行 + 虚拟化，微软商店网格观感）。
    /// </summary>
    private ItemsRepeater MakeGridRepeater(PageKind returnPage)
    {
        var layout = new UniformGridLayout
        {
            Orientation = Orientation.Horizontal,
            ItemsJustification = UniformGridLayoutItemsJustification.Start,
            MinItemWidth = 200,
            MinRowSpacing = 12,
            MinColumnSpacing = 12
        };
        return new ItemsRepeater
        {
            Layout = layout,
            ItemTemplate = new FuncDataTemplate<StorePresetEntry>((entry, _) =>
                new StoreCard(entry, StoreCardStyle.Grid, this, returnPage).Root)
        };
    }

    /// <summary>热门页：按下载次数（缺省按上架时间）排序的网格。</summary>
    private ScrollViewer BuildHotPage()
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 16)
        };
        header.Children.Add(GlyphIcon(IconFire, 20));
        header.Children.Add(new TextBlock
        {
            Text = "热门预设",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _hotRepeater = MakeGridRepeater(PageKind.Hot);
        var content = new StackPanel { Margin = new Thickness(20, 12, 20, 24) };
        content.Children.Add(header);
        content.Children.Add(_hotRepeater);
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>我的预设页：商店安装记录列表。</summary>
    private ScrollViewer BuildMinePage()
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 16)
        };
        header.Children.Add(GlyphIcon(IconLibrary, 20));
        header.Children.Add(new TextBlock
        {
            Text = "我的预设",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _mineList = new StackPanel { Spacing = 10 };
        var content = new StackPanel { Margin = new Thickness(20, 12, 20, 24) };
        content.Children.Add(header);
        content.Children.Add(_mineList);
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    #endregion

    #region 详情页

    /// <summary>
    /// 详情页（照微软商店产品详情页）：主色横幅（预览图提取主色渐变，图标 96x96 方形裁剪 + 白字排版 +
    /// 大获取按钮）+ 「预览」大图卡片 + 「发现更多」其他预设行。预设是主界面的“岛”，没有独立应用图标，
    /// 因此图标位直接用预览图中心方形裁剪代替。返回按钮在标题栏上（照截图）。
    /// </summary>
    private ScrollViewer BuildDetailPage()
    {
        // 主色横幅：渐变背景在 OpenDetail 时按预览图主色填充。
        _detailBanner = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Height = 330,
            Background = PlaceholderBrush(),
            Child = BuildBannerContent()
        };

        // 「预览」大图卡片。
        _detailPreviewImage = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        _detailShimmer = new Shimmer
        {
            CornerRadius = new CornerRadius(8),
            AutoDetectContentLoadState = false,
            Content = _detailPreviewImage
        };
        var previewCard = new Border
        {
            Background = CardBrush(),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 16),
            Child = new StackPanel { Spacing = 12, Children =
            {
                BuildCardSectionHeader("预览"),
                new Border
                {
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    Height = 300,
                    Child = _detailShimmer
                }
            } }
        };

        // 「发现更多」其他预设。
        _moreList = new StackPanel { Spacing = 8 };
        _moreSection = new StackPanel { Spacing = 12, Children =
        {
            BuildCardSectionHeader("发现更多"),
            _moreList
        } };

        var content = new StackPanel { Margin = new Thickness(20, 8, 20, 24), Spacing = 16 };
        content.Children.Add(_detailBanner);
        content.Children.Add(previewCard);
        content.Children.Add(_moreSection);
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>横幅内部内容：方形图标 + 名称 / 作者 / 元数据 + 描述 + 获取按钮 + 小字备注。</summary>
    private Control BuildBannerContent()
    {
        // 图标：预览图中心方形裁剪（预设是主界面的“岛”，没有独立方形图标）。
        _detailIconImage = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var iconBorder = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            Child = _detailIconImage,
            VerticalAlignment = VerticalAlignment.Center
        };

        _detailTitle = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _detailAuthor = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        _detailMetaText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var titleColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleColumn.Children.Add(_detailTitle);
        titleColumn.Children.Add(_detailAuthor);
        titleColumn.Children.Add(_detailMetaText);

        var headGrid = new Grid { ColumnDefinitions = Cols(Auto, Star), ColumnSpacing = 20 };
        Grid.SetColumn(iconBorder, 0);
        headGrid.Children.Add(iconBorder);
        Grid.SetColumn(titleColumn, 1);
        headGrid.Children.Add(titleColumn);

        _detailDescription = new TextBlock
        {
            FontSize = 13.5,
            Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 16, 0, 0)
        };

        _detailGetButton = new Button
        {
            MinWidth = 150,
            Height = 36,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _detailGetButton.Classes.Add("AccentButtonStyle");

        _detailNote = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var bannerGrid = new Grid { Margin = new Thickness(36, 28, 36, 28), RowDefinitions = Rows(Auto, Auto, Auto, Auto) };
        bannerGrid.Children.Add(headGrid);
        Grid.SetRow(headGrid, 0);
        bannerGrid.Children.Add(_detailDescription);
        Grid.SetRow(_detailDescription, 1);
        bannerGrid.Children.Add(_detailGetButton);
        Grid.SetRow(_detailGetButton, 2);
        bannerGrid.Children.Add(_detailNote);
        Grid.SetRow(_detailNote, 3);
        return bannerGrid;
    }

    /// <summary>卡片区块标题行（标题 + 装饰性 chevron）。</summary>
    private static StackPanel BuildCardSectionHeader(string title)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(GlyphIcon(IconChevronRight, 13, 0.8));
        return header;
    }

    /// <summary>应用横幅主色渐变（从预览图提取的主色；失败时用强调色）。</summary>
    private void ApplyBannerBackground(Color? color)
    {
        if (_detailBanner == null)
        {
            return;
        }

        var baseColor = color ?? ThemePalette.AccentColor();
        var hsl = new HslColor(baseColor);
        // 降饱和 + 提亮：商店横幅是柔和的产品色，不做高饱和撞色。
        var saturation = Math.Clamp(hsl.S * 1.1, 0.22, 0.55);
        var dark = ThemePalette.IsDarkTheme();
        var top = new HslColor(1, hsl.H, saturation, dark ? 0.44 : 0.74).ToRgb();
        var bottom = new HslColor(1, hsl.H, saturation, dark ? 0.3 : 0.56).ToRgb();
        _detailBanner.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(bottom, 1)
            }
        };
    }

    /// <summary>
    /// 从预览图提取主色调（采样平均并跳过低饱和像素，再按主题调整亮度保证白字可读）。
    /// </summary>
    private static Color? ExtractBannerColor(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            using var bitmap = new System.Drawing.Bitmap(stream);
            double r = 0, g = 0, b = 0;
            var count = 0;
            for (var y = 0; y < bitmap.Height; y += 4)
            {
                for (var x = 0; x < bitmap.Width; x += 4)
                {
                    var c = bitmap.GetPixel(x, y);
                    var max = Math.Max(c.R, Math.Max(c.G, c.B));
                    var min = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (max - min < 24)
                    {
                        continue; // 跳过灰白像素，避免把主色冲淡。
                    }

                    r += c.R;
                    g += c.G;
                    b += c.B;
                    count++;
                }
            }

            if (count == 0)
            {
                return null;
            }

            return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>重建「发现更多」其他预设行（最多 3 个）。</summary>
    private void RebuildDiscoverMore(StorePresetEntry current)
    {
        if (_moreList == null || _moreSection == null)
        {
            return;
        }

        _moreList.Children.Clear();
        var others = (_index?.Presets ?? []).Where(p => !string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        _moreSection.IsVisible = others.Count > 0;
        foreach (var other in others)
        {
            _moreList.Children.Add(BuildMoreRow(other));
        }
    }

    /// <summary>「发现更多」行：方形图标（预览裁剪）+ 名称 / 作者 + 获取按钮；点行进详情。</summary>
    private Border BuildMoreRow(StorePresetEntry entry)
    {
        var icon = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var iconBorder = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = PlaceholderBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };

        var textColumn = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Spacing = 1
        };
        textColumn.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = entry.AuthorText,
            FontSize = 11,
            Opacity = 0.65,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var getButton = new Button
        {
            MinWidth = 76,
            Height = 28,
            FontSize = 12.5,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        BindGetButton(getButton, entry);

        var grid = new Grid { ColumnDefinitions = Cols(Auto, Star, Auto), ColumnSpacing = 12 };
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);
        Grid.SetColumn(textColumn, 1);
        grid.Children.Add(textColumn);
        Grid.SetColumn(getButton, 2);
        grid.Children.Add(getButton);

        var row = new Border
        {
            Background = CardBrush(),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
        row.PointerReleased += (_, e) =>
        {
            if (e.Source is not Button)
            {
                OpenDetail(entry, PageKind.Detail);
            }
        };
        row.PointerEntered += (_, _) => row.Background = HoverCardBrush();
        row.PointerExited += (_, _) => row.Background = CardBrush();

        _ = LoadPreview(entry, bytes =>
        {
            if (TryCreateBitmap(bytes, out var bitmap))
            {
                icon.Source = bitmap;
                icon.IsVisible = true;
            }
        });
        return row;
    }

    #endregion

    #region 数据加载与重建

    /// <summary>加载（或强制刷新）商店索引并重建全部页面。</summary>
    private async Task LoadIndexAsync(bool forceRefresh)
    {
        if (_loadingRing != null)
        {
            _loadingRing.IsVisible = true;
        }

        if (_statusBar != null)
        {
            _statusBar.IsOpen = false;
        }

        try
        {
            var index = await Task.Run(() => PresetStoreService.FetchIndexAsync(forceRefresh));
            if (index == null || index.Presets.Count == 0)
            {
                ShowStatus("无法连接预设商店",
                    "请检查网络连接后，点击右上角刷新按钮重试；商店数据会优先使用 15 分钟内的本地缓存。");
                return;
            }

            _index = index;
            // 丢弃旧页面的按钮绑定，避免刷新后残留已移除控件的引用。
            _getButtonEntries.Clear();
            _boundButtons.Clear();
            RebuildAll();
        }
        catch (Exception ex)
        {
            ShowStatus("加载商店数据失败", ex.Message);
        }
        finally
        {
            if (_loadingRing != null)
            {
                _loadingRing.IsVisible = false;
            }
        }
    }

    /// <summary>显示首页顶部状态条。</summary>
    private void ShowStatus(string title, string message)
    {
        if (_statusBar == null)
        {
            return;
        }

        _statusBar.Title = title;
        _statusBar.Message = message;
        _statusBar.IsOpen = true;
    }

    /// <summary>重建全部页面内容。</summary>
    private void RebuildAll()
    {
        RebuildBanner();
        RebuildHomeRows();
        RebuildBrowseGrid();
        RebuildHotGrid();
        RebuildMine();
        RefreshAllGetButtons();
    }

    /// <summary>重建 Banner 轮播（前 5 条）与右侧精选小卡（第 6~8 条）。</summary>
    private void RebuildBanner()
    {
        _bannerEntries.Clear();
        _bannerDots.Clear();
        if (_bannerDotsPanel != null)
        {
            _bannerDotsPanel.Children.Clear();
        }

        var presets = _index?.Presets ?? [];
        _bannerEntries.AddRange(presets.Take(5));
        _carousel?.Items.Clear();

        if (_bannerEntries.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _bannerEntries.Count; i++)
        {
            var dot = new Border
            {
                Width = i == 0 ? 18 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = i == 0 ? Brushes.White : new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))
            };
            var idx = i;
            dot.PointerReleased += (_, _) =>
            {
                if (_carousel != null)
                {
                    _carousel.SelectedIndex = idx;
                }
            };
            _bannerDots.Add(dot);
            _bannerDotsPanel?.Children.Add(dot);
        }

        // 每页一张完整 banner 卡（图 + 遮罩 + 获取按钮），Carousel 提供滑动过渡。
        foreach (var entry in _bannerEntries)
        {
            _carousel?.Items.Add(BuildBannerPage(entry));
        }

        if (_carousel != null)
        {
            _carousel.SelectedIndex = 0;
        }

        UpdateBannerDots();

        _bannerTimer?.Stop();
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bannerTimer.Tick += (_, _) => StepBanner(1);
        _bannerTimer.Start();

        var sideEntries = presets.Skip(5).Take(3).ToList();
        for (var i = 0; i < _sideSlots.Count; i++)
        {
            if (i < sideEntries.Count)
            {
                _sideSlots[i].Bind(sideEntries[i], this);
            }
            else
            {
                _sideSlots[i].Clear();
            }
        }
    }

    /// <summary>banner 前进 / 后退（Carousel 自动应用滑动过渡）。</summary>
    private void StepBanner(int delta)
    {
        if (_carousel == null || _bannerEntries.Count == 0)
        {
            return;
        }

        _carousel.SelectedIndex =
            (((_carousel.SelectedIndex + delta) % _bannerEntries.Count) + _bannerEntries.Count) % _bannerEntries.Count;
    }

    /// <summary>重建首页横向行与编辑精选卡。</summary>
    private void RebuildHomeRows()
    {
        var presets = _index?.Presets ?? [];
        var byLatest = SortEntries(presets, StoreSort.Latest);
        var byHot = SortEntries(presets, StoreSort.Downloads);

        FillRow(_hotRowHost, byHot.Take(10));
        FillRow(_latestRowHost, byLatest.Skip(10).Take(10));

        var featured = byHot.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Description));
        if (_featuredSlot == null)
        {
            return;
        }

        if (featured != null)
        {
            _featuredSlot.Bind(featured, this);
        }
        else
        {
            _featuredSlot.Clear();
        }
    }

    /// <summary>填充横向卡行（首页来源，详情返回首页）。</summary>
    private void FillRow(StackPanel? host, IEnumerable<StorePresetEntry> entries)
    {
        if (host == null)
        {
            return;
        }

        host.Children.Clear();
        foreach (var entry in entries)
        {
            host.Children.Add(new StoreCard(entry, StoreCardStyle.Row, this, PageKind.Home).Root);
        }
    }

    #endregion

    #region 浏览 / 热门网格

    /// <summary>排序条目。</summary>
    private static List<StorePresetEntry> SortEntries(IEnumerable<StorePresetEntry> entries, StoreSort sort) => sort switch
    {
        StoreSort.Downloads => entries
            .OrderByDescending(p => p.Downloads)
            .ThenByDescending(p => p.Created ?? DateTime.MinValue)
            .ToList(),
        StoreSort.Name => entries.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList(),
        _ => entries.OrderByDescending(p => p.Created ?? DateTime.MinValue).ToList()
    };

    /// <summary>浏览页当前排序（下拉框）。</summary>
    private StoreSort CurrentSort => (_sortBox?.SelectedIndex) switch
    {
        1 => StoreSort.Downloads,
        2 => StoreSort.Name,
        _ => StoreSort.Latest
    };

    /// <summary>重建浏览页网格（排序 + 搜索过滤，ItemsRepeater 虚拟化）。</summary>
    private void RebuildBrowseGrid()
    {
        if (_browseRepeater == null)
        {
            return;
        }

        var keyword = _searchBox?.Text?.Trim() ?? "";
        var entries = _index?.Presets ?? [];
        if (keyword.Length > 0)
        {
            entries = entries.Where(p =>
                p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.Author.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        _browseRepeater.ItemsSource = SortEntries(entries, CurrentSort);

        if (_browseEmpty != null)
        {
            _browseEmpty.IsVisible = entries.Count == 0;
        }
    }

    /// <summary>重建热门页网格。</summary>
    private void RebuildHotGrid()
    {
        if (_hotRepeater == null)
        {
            return;
        }

        _hotRepeater.ItemsSource = SortEntries(_index?.Presets ?? [], StoreSort.Downloads);
    }

    #endregion

    #region 我的预设

    /// <summary>重建安装记录列表。</summary>
    private void RebuildMine()
    {
        if (_mineList == null)
        {
            return;
        }

        _mineList.Children.Clear();
        var records = PresetStoreService.LoadInstallRecords();
        if (records.Count == 0)
        {
            _mineList.Children.Add(new TextBlock
            {
                Text = "还没有从商店安装过预设。到「全部预设」或「热门」逛一逛吧！",
                Opacity = 0.6
            });
            return;
        }

        foreach (var record in records)
        {
            var entry = _index?.Presets.FirstOrDefault(p =>
                string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            _mineList.Children.Add(BuildMineRow(record, entry));
        }
    }

    /// <summary>构建一条安装记录行。</summary>
    private Border BuildMineRow(StoreInstallRecord record, StorePresetEntry? entry)
    {
        var hasUpdate = entry != null && PresetStoreService.HasUpdate(entry);
        var textCol = new StackPanel { Spacing = 2 };
        textCol.Children.Add(new TextBlock
        {
            Text = entry?.Name ?? record.InstalledName,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        });
        textCol.Children.Add(new TextBlock
        {
            FontSize = 11.5,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Text = (entry != null ? $"{entry.AuthorText} · " : "") +
                   $"安装于 {(DateTime.TryParse(record.InstalledAt, out var t) ? t.ToString("yyyy-MM-dd HH:mm") : record.InstalledAt)}"
        });

        var stateText = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Text = hasUpdate ? "商店端有更新" : "已安装",
            Foreground = hasUpdate
                ? new SolidColorBrush(ThemePalette.AccentColor())
                : new SolidColorBrush(Color.FromArgb(150, 128, 128, 128))
        };

        var viewButton = new Button { Content = "查看", MinWidth = 64, Height = 30, FontSize = 12.5 };
        viewButton.Click += (_, _) =>
        {
            if (entry != null)
            {
                OpenDetail(entry, PageKind.Mine);
            }
        };

        var grid = new Grid
        {
            ColumnDefinitions = Cols(Star, Auto, Auto),
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(textCol, 0);
        grid.Children.Add(textCol);
        Grid.SetColumn(stateText, 1);
        grid.Children.Add(stateText);
        Grid.SetColumn(viewButton, 2);
        grid.Children.Add(viewButton);

        return new Border
        {
            Background = CardBrush(),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid
        };
    }

    #endregion

    #region 导航与详情

    /// <summary>切换页面（旧页上滑淡出、新页自下滑入的 Fluent 垂直滑动动画）。</summary>
    private async void Navigate(PageKind page)
    {
        _page = page;

        // 同步侧边导航栏选中（详情页不属于导航项，保持原选中项不动）。
        if (page != PageKind.Detail)
        {
            _navBar?.SetSelected(page);
        }

        // 标题栏返回按钮：详情页时显示（照微软商店，返回在标题栏上）。
        if (_backButton != null)
        {
            _backButton.IsVisible = page == PageKind.Detail;
        }

        if (_pageContainer == null || !_pages.TryGetValue(page, out var newPage) ||
            ReferenceEquals(_currentPageContent, newPage))
        {
            switch (page)
            {
                case PageKind.Browse:
                    RebuildBrowseGrid();
                    break;
                case PageKind.Hot:
                    RebuildHotGrid();
                    break;
                case PageKind.Mine:
                    RebuildMine();
                    break;
            }

            return;
        }

        // 先把上一次动画中未完成的旧页清理掉（连点保护），再开始本次双页动画。
        if (_currentPageContent is { } current)
        {
            current.RenderTransform = null;
            current.Opacity = 1;
        }

        foreach (var child in _pageContainer.Children
                     .Where(c => !ReferenceEquals(c, newPage)).ToList())
        {
            _pageContainer.Children.Remove(child);
        }

        var oldPage = _currentPageContent;
        _currentPageContent = newPage;
        _pageContainer.Children.Add(newPage);

        try
        {
            await FluentSlideTransition.RunAsync(oldPage, newPage);
        }
        catch
        {
            // 动画中断（窗口关闭等）不影响最终状态。
        }
        finally
        {
            // 移除所有非当前页（动画完旧页退场）。
            foreach (var child in _pageContainer.Children
                         .Where(c => !ReferenceEquals(c, _currentPageContent)).ToList())
            {
                child.RenderTransform = null;
                child.Opacity = 1;
                _pageContainer.Children.Remove(child);
            }
        }

        switch (page)
        {
            case PageKind.Browse:
                RebuildBrowseGrid();
                break;
            case PageKind.Hot:
                RebuildHotGrid();
                break;
            case PageKind.Mine:
                RebuildMine();
                break;
        }
    }

    /// <summary>打开详情页（供卡片点击进入）。</summary>
    internal void OpenDetail(StorePresetEntry entry, PageKind returnPage)
    {
        _detailReturnPage = returnPage;
        _detailEntry = entry;

        if (_detailTitle != null)
        {
            _detailTitle.Text = entry.Name;
        }

        if (_detailAuthor != null)
        {
            _detailAuthor.Text = entry.AuthorText;
        }

        if (_detailDescription != null)
        {
            _detailDescription.Text = string.IsNullOrWhiteSpace(entry.Description) ? "（暂无描述）" : entry.Description;
        }

        // 元数据内联行（照商店评分/评数行位置）。
        if (_detailMetaText != null)
        {
            var meta = string.IsNullOrWhiteSpace(entry.PluginVersion) ? null : $"插件 v{entry.PluginVersion}";
            if (entry.SizeBytes > 0)
            {
                meta = meta == null ? entry.SizeText : $"{meta} · {entry.SizeText}";
            }

            if (entry.Created is { } created)
            {
                var date = $"上架 {created:yyyy-MM-dd}";
                meta = meta == null ? date : $"{meta} · {date}";
            }

            if (entry.Downloads > 0)
            {
                meta = $"{meta} · {entry.Downloads:N0} 次下载";
            }

            _detailMetaText.Text = meta ?? "—";
            _detailMetaText.IsVisible = !string.IsNullOrEmpty(meta);
        }

        if (_detailNote != null)
        {
            if (!PresetStoreService.IsCompatible(entry, out var minVersion))
            {
                _detailNote.Text = $"当前插件版本过低（需要 v{minVersion} 或更高），请先到 GitHub 更新插件后再安装。";
                _detailNote.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 120));
            }
            else
            {
                _detailNote.Text = $"由 {entry.AuthorText} 制作 · 安装前将展示预设详情供确认";
                _detailNote.Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            }
        }

        if (_detailGetButton != null)
        {
            BindGetButton(_detailGetButton, entry);
        }

        // 预览图：同一张图同时用于方形图标、大图卡片，并提取主色填充横幅渐变。
        if (_detailIconImage != null && _detailPreviewImage != null && _detailShimmer != null)
        {
            _detailIconImage.IsVisible = false;
            _detailPreviewImage.IsVisible = false;
            _detailShimmer.IsContentLoaded = false;
            ApplyBannerBackground(null);
            _ = LoadPreview(entry, bytes =>
            {
                if (_detailEntry != entry || _detailIconImage == null || _detailPreviewImage == null || _detailShimmer == null)
                {
                    return;
                }

                if (TryCreateBitmap(bytes, out var bitmap))
                {
                    _detailIconImage.Source = bitmap;
                    _detailIconImage.IsVisible = true;
                    _detailPreviewImage.Source = bitmap;
                    _detailPreviewImage.IsVisible = true;
                }

                _detailShimmer.IsContentLoaded = true;
                // 主色提取较重（GetPixel 采样），放后台线程。
                _ = Task.Run(() => ExtractBannerColor(bytes)).ContinueWith(t =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_detailEntry == entry)
                        {
                            ApplyBannerBackground(t.Result);
                        }
                    }));
            });
        }

        RebuildDiscoverMore(entry);
        Navigate(PageKind.Detail);
    }

    #endregion

    #region 获取按钮与下载安装

    /// <summary>
    /// 把按钮绑定为某条目的「获取」按钮（卡片 / banner / 详情共用）。
    /// 状态机：未安装 = 强调色「获取」；下载中 = 禁用 + 进度文本；已安装 = 禁用「已安装」；
    /// 有更新 = 强调色「更新」；版本不满足 = 禁用「需要插件 vX.Y.Z」。
    /// </summary>
    internal void BindGetButton(Button button, StorePresetEntry entry)
    {
        _getButtonEntries[button] = entry;
        if (_boundButtons.Add(button))
        {
            button.Click += (_, _) =>
            {
                if (_getButtonEntries.TryGetValue(button, out var bound) &&
                    !_installingIds.Contains(bound.Id))
                {
                    _ = InstallEntryAsync(bound);
                }
            };
        }

        RenderGetButton(button);
    }

    /// <summary>按当前状态渲染获取按钮。</summary>
    private void RenderGetButton(Button button)
    {
        if (!_getButtonEntries.TryGetValue(button, out var entry))
        {
            return;
        }

        if (_installingIds.Contains(entry.Id))
        {
            button.IsEnabled = false;
            button.Classes.Remove("AccentButtonStyle");
            button.Content = "准备下载…";
            return;
        }

        if (!PresetStoreService.IsCompatible(entry, out var minVersion))
        {
            button.IsEnabled = false;
            button.Classes.Remove("AccentButtonStyle");
            button.Content = $"需要插件 v{minVersion}";
            return;
        }

        if (PresetStoreService.IsInstalled(entry.Id))
        {
            if (PresetStoreService.HasUpdate(entry))
            {
                button.IsEnabled = true;
                button.Classes.Add("AccentButtonStyle");
                button.Content = "更新";
            }
            else
            {
                button.IsEnabled = false;
                button.Classes.Remove("AccentButtonStyle");
                button.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        GlyphIcon(IconCheckCircle, 13),
                        new TextBlock { Text = "已安装", VerticalAlignment = VerticalAlignment.Center }
                    }
                };
            }

            return;
        }

        button.IsEnabled = true;
        button.Classes.Add("AccentButtonStyle");
        button.Content = "获取";
    }

    /// <summary>刷新全部已注册的获取按钮。</summary>
    private void RefreshAllGetButtons()
    {
        foreach (var button in _getButtonEntries.Keys.ToList())
        {
            RenderGetButton(button);
        }
    }

    /// <summary>把 entry 的全部按钮显示为下载进度。</summary>
    private void UpdateProgressButtons(StorePresetEntry entry, long received, long? total)
    {
        var text = total is { } t && t > 0
            ? $"下载中 {received * 100 / t:0}%"
            : received switch
            {
                < 1024 => $"下载中 {received} B",
                < 1024 * 1024 => $"下载中 {received / 1024.0:0.#} KB",
                _ => $"下载中 {received / 1024.0 / 1024.0:0.#} MB"
            };

        foreach (var (button, bound) in _getButtonEntries)
        {
            if (bound.Id == entry.Id)
            {
                button.Content = text;
            }
        }
    }

    /// <summary>下载 → 确认 → 导入一条预设。</summary>
    private async Task InstallEntryAsync(StorePresetEntry entry)
    {
        if (_installingIds.Contains(entry.Id) || !PresetStoreService.IsCompatible(entry, out _))
        {
            return;
        }

        _installingIds.Add(entry.Id);
        RefreshAllGetButtons();
        try
        {
            var progress = new Progress<(long Received, long? Total)>(p =>
                UpdateProgressButtons(entry, p.Received, p.Total));

            var path = await Task.Run(() => PresetStoreService.DownloadPresetAsync(entry, progress));

            // 读包并弹确认对话框（与「双击 .cizip 安装」共用 PresetInstallDialog）。
            var result = await Task.Run(() => PresetExchange.Import(
                path, Path.Combine(InjectorRuntime.ConfigDirectory, "imported")));
            if (!result.Success || result.Preset == null)
            {
                ShowToast(result.Message, "安装失败", InfoBarSeverity.Error);
                return;
            }

            var confirm = await PresetInstallDialog.ShowAsync(this, result.Preset.Name, result.Metadata);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            var importedName = InjectorRuntime.ImportUserPreset(result.Preset);
            PresetStoreService.MarkInstalled(entry, importedName);
            ShowToast($"预设「{importedName}」已安装，可在设置页「用户预设」中套用。", "安装成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"下载或安装失败：{ex.Message}", "安装失败", InfoBarSeverity.Error);
        }
        finally
        {
            _installingIds.Remove(entry.Id);
            RefreshAllGetButtons();
            RebuildMine();
        }
    }

    /// <summary>右上角 Toast 提醒。</summary>
    private void ShowToast(string message, string title, InfoBarSeverity severity)
    {
        try
        {
            new ReminderToastWindow().ShowFor(this, message, severity, title);
        }
        catch
        {
            // Toast 失败不影响主流程。
        }
    }

    #endregion

    #region 搜索 / 预览图 / 通用小控件

    /// <summary>搜索框变化：浏览页直接过滤；首页 / 热门页输入时跳到浏览页。</summary>
    private void OnSearchChanged()
    {
        if (_page == PageKind.Browse)
        {
            RebuildBrowseGrid();
            return;
        }

        var keyword = _searchBox?.Text?.Trim() ?? "";
        if (keyword.Length > 0 && _page is PageKind.Home or PageKind.Hot)
        {
            Navigate(PageKind.Browse);
        }
    }

    /// <summary>异步加载预览图并回调（bytes 可能为 null 表示无图 / 下载失败）。供卡片组件共用。</summary>
    internal static async Task LoadPreview(StorePresetEntry entry, Action<byte[]?> onLoaded)
    {
        var bytes = await Task.Run(() => PresetStoreService.FetchPreviewAsync(entry));
        Dispatcher.UIThread.Post(() => onLoaded(bytes));
    }

    /// <summary>PNG 字节 → Bitmap；失败返回 false。供卡片组件共用。</summary>
    internal static bool TryCreateBitmap(byte[]? bytes, out Bitmap? bitmap)
    {
        bitmap = null;
        if (bytes is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            bitmap = new Bitmap(new MemoryStream(bytes));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>横向卡行翻页（± 视口宽度）。</summary>
    private void PageScroll(ScrollViewer scroller, int direction)
    {
        var step = Math.Max(scroller.Viewport.Width * 0.9, 320);
        var target = Math.Max(0, scroller.Offset.X + direction * step);
        scroller.Offset = new Vector(target, scroller.Offset.Y);
    }

    /// <summary>Fluent 图标（TextBlock + FluentSystemIcons-Resizable 字体）。</summary>
    private static TextBlock GlyphIcon(char glyph, double size, double opacity = 1) => new()
    {
        Text = glyph.ToString(),
        FontFamily = AppBase.FluentIconsFontFamily,
        FontSize = size,
        Opacity = opacity,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>小圆形图标按钮。</summary>
    private static Button IconButton(char glyph, string tooltip)
    {
        var button = new Button
        {
            Content = GlyphIcon(glyph, 14),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    /// <summary>卡片背景（深浅色适配）。</summary>
    internal static IBrush CardBrush() => ThemePalette.IsDarkTheme()
        ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))
        : new SolidColorBrush(Colors.White);

    /// <summary>卡片 hover 背景（带边框感，替代卡片底色）。</summary>
    internal static IBrush HoverCardBrush() => ThemePalette.IsDarkTheme()
        ? new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35))
        : new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

    /// <summary>占位渐变（强调色半透明）。</summary>
    internal static IBrush PlaceholderBrush()
    {
        var accent = ThemePalette.AccentColor();
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(70, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(32, accent.R, accent.G, accent.B), 1)
            }
        };
    }

    /// <summary>Grid 行定义辅助。</summary>
    private static RowDefinitions Rows(params GridLength[] lengths)
    {
        var definitions = new RowDefinitions();
        foreach (var length in lengths)
        {
            definitions.Add(new RowDefinition { Height = length });
        }

        return definitions;
    }

    /// <summary>Grid 列定义辅助。</summary>
    private static ColumnDefinitions Cols(params GridLength[] lengths)
    {
        var definitions = new ColumnDefinitions();
        foreach (var length in lengths)
        {
            definitions.Add(new ColumnDefinition { Width = length });
        }

        return definitions;
    }

    // 常用 GridLength 简写。
    private static GridLength Auto => GridLength.Auto;
    private static GridLength Star => new(1, GridUnitType.Star);
    private static GridLength StarOf(double value) => new(value, GridUnitType.Star);
    private static GridLength Px(double value) => new(value, GridUnitType.Pixel);

    #endregion
}
