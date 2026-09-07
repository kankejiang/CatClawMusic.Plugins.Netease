using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云在线音乐页面（C# 代码构建 UI，避免跨程序集 XAML 编译问题）。
/// <para>
/// 顶部：返回 + 标题 + 搜索按钮 + 音质切换 + 账号（点击搜索按钮展开/收起搜索输入行）；
/// 功能入口（私人漫游/每日推荐/排行榜/我的歌单/推荐歌单）；分类 chips；
/// 歌单网格（分页加载）/歌手列表/歌曲列表三态切换；底部轻提示条。
/// 通过 DynamicResource 访问宿主应用的全局资源（颜色、样式）。
/// </para>
/// </summary>
public class NeteaseOnlineMusicPage : ContentPage
{
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;

    // 控件引用（事件处理需要）。三个 CollectionView 非 readonly：WinUI 运行时修改
    // ItemsLayout / Span 均不生效（ItemsPanel 在 handler 挂载时固化，只有构造期赋值被
    // 消费——宽屏实测停留初始 2 列巨型卡片），列数变化时须整建视图替换。
    private CollectionView _playlistsView;
    private CollectionView _songsView;
    private CollectionView _artistsView;
    private readonly ActivityIndicator _loadingIndicator;

    // 响应式布局引用的控件（宽屏/窄屏切换需要重排行列归属）
    private readonly Grid searchRowGrid;
    private readonly Border searchBorder;
    private readonly Entry searchEntry;
    private readonly Border searchButton;
    private bool _searchOpen; // 搜索输入行展开状态（顶部搜索按钮控制）
    private readonly ScrollView searchModesScroll;
    private readonly HorizontalStackLayout searchModesLayout;
    private readonly ScrollView entryContainer;
    private readonly NeteaseUiKit.EntryCard fmCard;
    private readonly NeteaseUiKit.EntryCard dailyCard;
    private readonly NeteaseUiKit.EntryCard toplistCard;
    private readonly NeteaseUiKit.EntryCard myCard;
    private readonly NeteaseUiKit.EntryCard recommendCard;

    // 首页 tab（精选/歌单广场/排行榜/歌手）
    private readonly HorizontalStackLayout tabsBar = new() { Spacing = 22, Padding = new Thickness(16, 0, 16, 0) };
    private readonly List<(Label Label, BoxView Underline)> _tabViews = new();
    private ScrollView? _featuredScroll;
    private View? _squareHost;
    private ScrollView? _toplistsScroll;
    private View? _artistsTabHost;
    private CollectionView? _tabArtistsView;

    // 响应式布局状态（宽屏 ≥900：搜索行合一）
    private bool _isWideLayout;

    // 搜索联想浮层（横竖屏切换需要重排行列归属）
    private readonly Border _suggestOverlay;

    // 歌曲列表头（含返回/标题/播放全部等）
    private readonly Grid _songsHeader;

    // 头部行控件（横屏时搜索框/chips 并入头部行，需要重排行列归属）
    private readonly Grid headerGrid;
    private readonly Label titleLabel;
    private readonly Border qualityButton;
    private readonly Border accountButton;
    private readonly Grid contentGrid;

    public NeteaseOnlineMusicPage(NeteaseOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        Title = "网易云音乐";
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 顶部：返回 + 标题 + 音质 + 账号 ──
        var backButton = CreateBackButton();
        titleLabel = new Label
        {
            Text = "网易云音乐",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        qualityButton = new Border
        {
            Padding = new Thickness(10, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
        };
        qualityButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var qualityLabel = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        qualityLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        qualityLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.QualityText));
        qualityButton.Content = qualityLabel;
        var qualityTap = new TapGestureRecognizer();
        qualityTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.CycleQualityCommand));
        qualityButton.GestureRecognizers.Add(qualityTap);

        accountButton = new Border
        {
            Padding = new Thickness(12, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
        };
        accountButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var accountLabel = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        accountLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        accountLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.AccountButtonText));
        accountButton.Content = accountLabel;
        var accountTap = new TapGestureRecognizer();
        accountTap.Tapped += OnAccountTapped;
        accountButton.GestureRecognizers.Add(accountTap);

        // ── 顶部搜索按钮：点击展开/收起搜索行（常驻搜索框取消，省一行纵向空间）──
        searchButton = new Border
        {
            Padding = new Thickness(11, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = new Label { Text = "🔍", FontSize = 13, VerticalOptions = LayoutOptions.Center },
        };
        searchButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var searchTap = new TapGestureRecognizer();
        searchTap.Tapped += (_, _) => _ = ToggleSearchOpenAsync();
        searchButton.GestureRecognizers.Add(searchTap);

        headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 12, 16, 8),
            Children = { backButton, titleLabel, searchButton, qualityButton, accountButton },
        };
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(searchButton, 2);
        Grid.SetColumn(qualityButton, 3);
        Grid.SetColumn(accountButton, 4);

        // ── 搜索输入行（默认隐藏，顶部 🔍 按钮展开；打开时聚焦并预热热词）──
        searchEntry = new Entry { Placeholder = "搜索歌曲 / 歌单 / 歌手..." };
        searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        searchEntry.SetBinding(Entry.TextProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.Completed += async (_, _) => { searchEntry.Unfocus(); await _vm.SearchSongsAsync(); };
        searchEntry.TextChanged += (_, e) => _ = _vm.OnSearchTextChangedAsync(e.NewTextValue);
        // 联想/热词浮层仅聚焦时显示（桌面空输入不再常驻热词占一整行）；聚焦无数据时预热热词
        searchEntry.Focused += async (_, _) =>
        {
            _vm.IsSearchFocused = true;
            if (_vm.SuggestItems.Count == 0) await _vm.OnSearchTextChangedAsync("");
        };
        searchEntry.Unfocused += (_, _) => _vm.IsSearchFocused = false;

        searchBorder = new Border
        {
            Content = searchEntry,
            Padding = new Thickness(14, 8),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(16, 0, 16, 4),
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        // ── 搜索类型 chips（歌曲/歌单/歌手）──
        searchModesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 0, 16, 6) };
        BindableLayout.SetItemsSource(searchModesLayout, _vm.SearchModes);
        BindableLayout.SetItemTemplate(searchModesLayout,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectSearchModeCommand), nameof(CategoryChipItem.Name)));
        searchModesScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 36,
            Content = searchModesLayout,
        };

        // ── 搜索行容器（默认隐藏；窄屏：搜索框上、chips 下；宽屏：同一行右侧）──
        searchRowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star } },
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Auto },
            },
            Children = { searchBorder, searchModesScroll },
            IsVisible = false,
        };
        Grid.SetRow(searchBorder, 0);
        Grid.SetRow(searchModesScroll, 1);

        // ── 功能入口（登录后可见：我的歌单 / 推荐歌单）──
        // 官方首页同款横版封面卡（150×104，封面 + 左下角标题条；封面异步贴图，失败保持渐变兜底）
        fmCard = NeteaseUiKit.CreateCoverEntryCard("🎧", "私人漫游", "多样频道无限听", "#667eea", "#764ba2", showPlay: true);
        var fmTap = new TapGestureRecognizer();
        fmTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadPrivateFmCommand));
        fmCard.GestureRecognizers.Add(fmTap);

        dailyCard = NeteaseUiKit.CreateCoverEntryCard("📅", "每日推荐", "今日限定好歌推荐", "#f7971e", "#ffd200", showPlay: true);
        var dailyTap = new TapGestureRecognizer();
        dailyTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadDailyRecommendCommand));
        dailyCard.GestureRecognizers.Add(dailyTap);

        toplistCard = NeteaseUiKit.CreateCoverEntryCard("🔥", "排行榜", "飙升 · 新歌 · 热歌", "#f953c6", "#b91d73");
        var toplistTap = new TapGestureRecognizer();
        toplistTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadToplistsCommand));
        toplistCard.GestureRecognizers.Add(toplistTap);

        myCard = NeteaseUiKit.CreateCoverEntryCard("💛", "我的歌单", "创建与收藏", "#11998e", "#38ef7d");
        myCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var myTap = new TapGestureRecognizer();
        myTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadMyPlaylistsCommand));
        myCard.GestureRecognizers.Add(myTap);

        recommendCard = NeteaseUiKit.CreateCoverEntryCard("✨", "推荐歌单", "每日为你精选", "#fc466b", "#3f5efb");
        recommendCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var recommendTap = new TapGestureRecognizer();
        recommendTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadRecommendPlaylistsCommand));
        recommendCard.GestureRecognizers.Add(recommendTap);

        // 异步加载各卡背景封面（官方首页同款：取推荐列表第一项封面；失败保持渐变兜底）
        _ = LoadEntryCoverAsync(fmCard, "fm");
        _ = LoadEntryCoverAsync(dailyCard, "daily");
        _ = LoadEntryCoverAsync(toplistCard, "toplist");
        _ = LoadEntryCoverAsync(myCard, "my");
        _ = LoadEntryCoverAsync(recommendCard, "recommend");

        // 入口卡片容器：竖版大卡横向滑动（仿网易云首页横滑卡），宽窄屏通用
        entryContainer = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = new HorizontalStackLayout
            {
                Spacing = 12,
                Padding = new Thickness(16, 2, 16, 6),
                Children = { fmCard, dailyCard, toplistCard, myCard, recommendCard },
            },
        };

        // ── 分类 chips（水平滚动，仅歌单广场可见）──
        var categoriesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 4, 16, 6) };
        categoriesLayout.SetBinding(HorizontalStackLayout.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowCategories));
        BindableLayout.SetItemsSource(categoriesLayout, _vm.Categories);
        BindableLayout.SetItemTemplate(categoriesLayout,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectCategoryCommand), nameof(CategoryChipItem.Name)));

        var categoriesScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 36,
            Content = categoriesLayout,
        };
        categoriesScroll.SetBinding(ScrollView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowCategories));

        // ── 歌单网格（分页加载；分块行方案：外层虚拟化「行」，每行 N 张定宽卡片，
        //    列数由 VM 按可用宽度推导。WinUI 上 GridItemsLayout.Span 不可靠，
        //    集合 Reset/重新虚拟化后项按整窗宽测量、单张占满一行）──
        _playlistsView = CreatePlaylistsView();

        // ── 歌手列表（搜索歌手模式；纯线性）──
        _artistsView = CreateArtistsView();

        // ── 歌曲列表模式 ──
        var songsBackButton = new Border
        {
            Padding = new Thickness(10, 5),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = new Label { Text = "‹ 返回", FontSize = 12 },
        };
        songsBackButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var songsBackLabel = (Label)songsBackButton.Content!;
        songsBackLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var songsBackTap = new TapGestureRecognizer();
        songsBackTap.Tapped += async (_, _) => await _vm.BackToPlaylistsAsync();
        songsBackButton.GestureRecognizers.Add(songsBackTap);

        var songsTitleLabel = new Label
        {
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
        };
        songsTitleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        songsTitleLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.CurrentListTitle));

        var playAllButton = new Border
        {
            Padding = new Thickness(12, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new HorizontalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Label { Text = "▶", FontSize = 11, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                    new Label { Text = "全部播放", FontSize = 12, FontFamily = "OpenSansSemibold", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                },
            },
        };
        playAllButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        playAllButton.SetBinding(Border.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.HasPlaylistSongs));
        var playAllTap = new TapGestureRecognizer();
        playAllTap.Tapped += async (_, _) => await _vm.PlayAllAsync();
        playAllButton.GestureRecognizers.Add(playAllTap);

        // 每日推荐列表内的"历史每日推荐"入口（仅 ShowHistoryDaily 上下文可见）
        var historyDailyButton = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new Label { Text = "历史日推", FontSize = 12, FontFamily = "OpenSansSemibold" },
        };
        historyDailyButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        var historyDailyLabel = (Label)historyDailyButton.Content!;
        historyDailyLabel.TextColor = Colors.White;
        historyDailyButton.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowHistoryDaily));
        var historyDailyTap = new TapGestureRecognizer();
        historyDailyTap.Tapped += async (_, _) => await _vm.LoadHistoryRecommendCommand.ExecuteAsync(null);
        historyDailyButton.GestureRecognizers.Add(historyDailyTap);

        // 歌单上下文的"相似歌单"入口（仅 ShowSimilarPlaylists 可见）
        var similarButton = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new Label { Text = "相似歌单", FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center },
        };
        var similarLabel = (Label)similarButton.Content!;
        similarLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        similarButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        similarButton.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSimilarPlaylists));
        var similarTap = new TapGestureRecognizer();
        similarTap.Tapped += async (_, _) => await OpenSimilarPlaylistsAsync();
        similarButton.GestureRecognizers.Add(similarTap);

        var songsHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 4, 16, 8),
            Children = { songsBackButton, songsTitleLabel, playAllButton, historyDailyButton, similarButton },
        };
        Grid.SetColumn(songsTitleLabel, 1);
        Grid.SetColumn(playAllButton, 2);
        Grid.SetColumn(historyDailyButton, 3);
        Grid.SetColumn(similarButton, 4);
        _songsHeader = songsHeader;

        // 歌曲列表（纯线性；Header 与视图同生命周期）
        _songsView = CreateSongsView();

        // ── 加载指示器 ──
        _loadingIndicator = new ActivityIndicator
        {
            WidthRequest = 36,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
        };
        _loadingIndicator.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");
        _loadingIndicator.SetBinding(ActivityIndicator.IsRunningProperty, nameof(NeteaseOnlineMusicViewModel.IsLoading));
        _loadingIndicator.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoading));

        // ── 轻提示条（播放失败/操作反馈）──
        var tipLabel = new Label
        {
            FontSize = 12,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            MaxLines = 2,
            Padding = new Thickness(14, 8),
        };
        tipLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.TipMessage));
        var tipBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Color.FromArgb("#CC000000"),
            Margin = new Thickness(24, 0, 24, 12),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Center,
            Content = tipLabel,
        };
        tipBorder.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.HasTip));

        // ── 搜索联想/热词浮层（覆盖搜索结果顶部，输入联想与热门搜索）──
        var suggestFlex = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Start,
            VerticalOptions = LayoutOptions.Fill,
        };
        BindableLayout.SetItemsSource(suggestFlex, _vm.SuggestItems);
        BindableLayout.SetItemTemplate(suggestFlex, BuildSuggestChipTemplate());
        _suggestOverlay = new Border
        {
            Content = suggestFlex,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = new Thickness(12, 8),
            Margin = new Thickness(12, 2, 12, 8),
            VerticalOptions = LayoutOptions.Start,
            MaximumHeightRequest = 320,
        };
        _suggestOverlay.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsSuggestVisible));
        _suggestOverlay.SetDynamicResource(Border.BackgroundColorProperty, "WindowBackgroundColor");

        // ── 首页一级 tab 栏（精选/歌单广场/排行榜/歌手）──
        for (var i = 0; i < _vm.HomeTabs.Count; i++)
        {
            var idx = i;
            var label = new Label { Text = _vm.HomeTabs[i], FontSize = 14, FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.Center };
            label.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
            var underline = new BoxView { HeightRequest = 3, CornerRadius = 1.5f, WidthRequest = 22, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
            underline.SetDynamicResource(BoxView.ColorProperty, "PrimaryColor");
            var tabHost = new VerticalStackLayout { Children = { label, underline }, HorizontalOptions = LayoutOptions.Center };
            var tabTap = new TapGestureRecognizer();
            tabTap.Tapped += (_, _) =>
            {
                _vm.SelectedTabIndex = idx;
                RefreshTabStyles();
            };
            tabHost.GestureRecognizers.Add(tabTap);
            _tabViews.Add((label, underline));
            tabsBar.Children.Add(tabHost);
        }
        RefreshTabStyles();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NeteaseOnlineMusicViewModel.SelectedTabIndex)) RefreshTabStyles();
        };

        // ── 精选 tab：横滑入口卡 + 三排角标歌单（官方首页同款结构）──
        var featuredRoot = new VerticalStackLayout { Spacing = 0, Children = { entryContainer } };
        featuredRoot.Add(BuildSectionHeader("推荐歌单 ›", "换一批 ↻", nameof(NeteaseOnlineMusicViewModel.ShuffleFeaturedCommand)));
        featuredRoot.Add(CreateHScroll(nameof(NeteaseOnlineMusicViewModel.FeaturedPlaylists),
            () => NeteaseUiKit.CreatePlaylistCornerCard(120, _vm.OpenPlaylistCardCommand)));
        featuredRoot.Add(BuildSectionHeader("音乐新发现 ›", null, null));
        featuredRoot.Add(CreateHScroll(nameof(NeteaseOnlineMusicViewModel.DiscoveryPlaylists),
            () => NeteaseUiKit.CreatePlaylistCornerCard(150, _vm.OpenPlaylistCardCommand)));
        featuredRoot.Add(BuildSectionHeader("你可能喜欢 ›", "换一批 ↻", nameof(NeteaseOnlineMusicViewModel.ShuffleFeaturedCommand)));
        featuredRoot.Add(CreateHScroll(nameof(NeteaseOnlineMusicViewModel.DailyPlaylists),
            () => NeteaseUiKit.CreatePlaylistCornerCard(120, _vm.OpenPlaylistCardCommand)));
        _featuredScroll = new ScrollView { Content = featuredRoot };
        _featuredScroll.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowFeatured));

        // ── 歌单广场 tab：分类 chips + 分块网格（网格自带虚拟化与分页）──
        // 必须 Grid 星号行（不能 VerticalStackLayout）：CollectionView 嵌无界高度容器会失去
        // 虚拟化与滚动（以为自身全可见，超出屏幕的行被直接裁掉——歌手 tab 8 个不可滚的同款问题）
        _squareHost = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Star },
            },
            Children = { categoriesScroll, _playlistsView },
        };
        Grid.SetRow(categoriesScroll, 0);
        Grid.SetRow(_playlistsView, 1);
        _squareHost.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSquare));

        // ── 排行榜 tab：榜单色块横滑 + 官方榜 Top3 卡 ──
        var colorCardsHost = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(16, 2, 16, 6) };
        _vm.ToplistColors.CollectionChanged += (_, _) => colorCardsHost.Dispatcher?.Dispatch(() =>
        {
            colorCardsHost.Children.Clear();
            for (var i = 0; i < _vm.ToplistColors.Count; i++)
            {
                // 色块不在 BindableLayout 内（要按索引取渐变色板），需手动给 BindingContext 才能让 Name 绑定生效
                var card = NeteaseUiKit.CreateToplistColorCard(i, _vm.OpenToplistCommand);
                card.BindingContext = _vm.ToplistColors[i];
                colorCardsHost.Children.Add(card);
            }
        });
        var blocksHost = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(14, 0, 14, 0) };
        BindableLayout.SetItemsSource(blocksHost, _vm.ToplistBlocks);
        BindableLayout.SetItemTemplate(blocksHost, new DataTemplate(() =>
            NeteaseUiKit.CreateToplistTop3Card(_vm.OpenToplistCommand)));
        var toplistsRoot = new VerticalStackLayout { Spacing = 0 };
        toplistsRoot.Add(BuildSectionHeader("榜单推荐", null, null));
        toplistsRoot.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = colorCardsHost });
        toplistsRoot.Add(BuildSectionHeader("官方榜", null, null));
        toplistsRoot.Add(blocksHost);
        _toplistsScroll = new ScrollView { Content = toplistsRoot };
        _toplistsScroll.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowToplists));

        // ── 歌手 tab：地区/性别 chips + 圆头像双列网格（分页加载）──
        var regionChips = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 8, 16, 2) };
        BindableLayout.SetItemsSource(regionChips, _vm.ArtistRegions);
        BindableLayout.SetItemTemplate(regionChips,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectArtistRegionCommand), nameof(CategoryChipItem.Name)));
        var genderChips = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 2, 16, 4) };
        BindableLayout.SetItemsSource(genderChips, _vm.ArtistGenders);
        BindableLayout.SetItemTemplate(genderChips,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectArtistGenderCommand), nameof(CategoryChipItem.Name)));
        _tabArtistsView = new CollectionView
        {
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            SelectionMode = SelectionMode.None,
            Margin = new Thickness(0, 4, 0, 0),
            RemainingItemsThreshold = 4,
        };
        _tabArtistsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreArtistsAsync();
        // 列数直接挂网格自身 SizeChanged：contentGrid.SizeChanged 在部分机型上不可靠，
        // 会导致歌手网格恒为初始 2 列（列数推导从未拿到有效宽度）
        _tabArtistsView.SizeChanged += (_, _) => _vm.SetArtistGridWidth(_tabArtistsView.Width - 44);
        _tabArtistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.TabArtistRows));
        _tabArtistsView.ItemTemplate = new DataTemplate(() =>
        {
            var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(16, 0, 16, 14) };
            row.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.ArtistGridRow.Items)));
            BindableLayout.SetItemTemplate(row, new DataTemplate(() =>
                NeteaseUiKit.CreateArtistAvatarCard(NeteaseOnlineMusicViewModel.ArtistCardWidth, _vm.OpenTabArtistCommand)));
            return row;
        });
        _artistsTabHost = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Star }, // 网格必须占星号行拿到有界高度（虚拟化 + 滚动），不能放 StackLayout
            },
            Children = { regionChips, genderChips, _tabArtistsView },
        };
        Grid.SetRow(regionChips, 0);
        Grid.SetRow(genderChips, 1);
        Grid.SetRow(_tabArtistsView, 2);
        _artistsTabHost.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowArtistTab));

        // ── 组装页面：header / tab 栏 / 搜索行（默认隐藏）/ 内容区 ──
        contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // 一级 tab 栏
                new() { Height = GridLength.Auto }, // search row（默认隐藏，🔍 按钮展开）
                new() { Height = GridLength.Star }, // content
            },
            Children = { headerGrid, tabsBar, searchRowGrid, _featuredScroll!, _squareHost!, _toplistsScroll!, _artistsTabHost!, _artistsView, _songsView, _loadingIndicator, tipBorder, _suggestOverlay },
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(tabsBar, 1);
        Grid.SetRow(searchRowGrid, 2);
        Grid.SetRow(_featuredScroll!, 3);
        Grid.SetRow(_squareHost!, 3);
        Grid.SetRow(_toplistsScroll!, 3);
        Grid.SetRow(_artistsTabHost!, 3);
        Grid.SetRow(_artistsView, 3);
        Grid.SetRow(_songsView, 3);
        Grid.SetRow(_loadingIndicator, 3);
        Grid.SetRow(tipBorder, 3);
        // 联想浮层覆盖内容区，置于顶层最后渲染
        Grid.SetRow(_suggestOverlay, 3);

        Content = contentGrid;

        // 尺寸变化时调整响应式布局（桌面嵌入模式下页面 SizeChanged 不触发，改挂 contentGrid）。
        // WinUI 上 SizeChanged 触发时 contentGrid.Width 常仍为 0（布局未完成），
        // Dispatch 到下一帧再读 Width，此时布局确定完成
        contentGrid.SizeChanged += (_, _) => contentGrid.Dispatcher?.Dispatch(() => ApplyResponsiveLayout(contentGrid.Width));
        // handler 挂载后延迟重试（首次布局完成后 Width 才有值，覆盖 SizeChanged 拿到 0 的场景）
        contentGrid.HandlerChanged += (_, _) => contentGrid.Dispatcher?.Dispatch(async () =>
        {
            ApplyResponsiveLayout(contentGrid.Width);
            if (contentGrid.Width <= 0)
            {
                await Task.Delay(300);
                ApplyResponsiveLayout(contentGrid.Width);
            }
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // 嵌入模式下 Content.Width 此时多为 0，用 contentGrid.Width + 延迟重试兜底
        ApplyResponsiveLayout(contentGrid.Width);
        if (contentGrid.Width <= 0)
        {
            await Task.Delay(300);
            ApplyResponsiveLayout(contentGrid.Width);
        }
        await _vm.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Detach();
    }

    /// <summary>点击账号按钮：已登录则二次确认后退出，未登录则跳转 WebView 登录页</summary>
    private async void OnAccountTapped(object? sender, EventArgs e)
    {
        if (_vm.IsLoggedIn)
        {
            // 二级确认：防止误触右上角用户名直接退出登录。
            // 桌面嵌入模式下本页无 Window，Page.DisplayAlert 不弹窗，必须走宿主 IDialogService（MainPage 有 Window）。
            var dialog = _services.GetService<IDialogService>();
            bool ok = dialog != null
                ? await dialog.ShowConfirmAsync("退出登录", "确定要退出网易云账号吗？", "退出", "取消")
                : await DisplayAlertAsync("退出登录", "确定要退出网易云账号吗？", "退出", "取消");
            if (ok) await _vm.LogoutAsync();
            return;
        }
        if (!_vm.SupportsLogin || _vm.CurrentLoginInfo == null) return;

        try
        {
            // 跳转宿主的 WebView 登录页。
            // 宿主 WebViewLoginViewModel 通过 platform 参数匹配 IOnlineMusicPlugin.PlatformName，
            // 网易云插件的 PlatformName 固定为 "netease"。
            // 注意：本插件页由 OpenPluginEntryAsync 经 shell.Navigation.PushAsync 推入导航栈，
            // 当前页不是 Shell 路由节点——Shell.Current.GoToAsync 会在 GetOrCreateFromRoute
            // 找不到正确父节点而 NRE。因此统一走 NavigationService（桌面嵌入/Shell 都由宿主处理）。
            var nav = _services.GetService<INavigationService>();
            if (nav != null)
            {
                await nav.NavigateToAsync($"webviewlogin?platform=netease");
                return;
            }

            _vm.ShowTip("当前界面不支持登录，请在宿主设置页完成登录");
        }
        catch (Exception ex)
        {
            Log.Debug("NeteasePlugin", $"[Login] 打开登录页失败: {ex.Message}");
            _vm.ShowTip("打开登录页失败");
        }
    }

    /// <summary>搜索联想/热词 chip 模板（点击回填并搜索）</summary>
    private DataTemplate BuildSuggestChipTemplate()
    {
        return new DataTemplate(() =>
        {
            var word = new Label { FontSize = 13, FontFamily = "OpenSansSemibold", MaxLines = 1 };
            word.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
            word.SetBinding(Label.TextProperty, new Binding(nameof(SearchSuggestion.Word)));
            var chip = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                BackgroundColor = Color.FromArgb("#24111122"),
                Padding = new Thickness(12, 6),
                Margin = new Thickness(4, 3),
                Content = word,
            };
            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(nameof(NeteaseOnlineMusicViewModel.SelectSuggestCommand), source: _vm));
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            chip.GestureRecognizers.Add(tap);
            return chip;
        });
    }

    /// <summary>区块标题行：左标题 + 右侧可选动作（如「换一批 ↻」，绑 VM 命令名）</summary>
    private View BuildSectionHeader(string title, string? actionText, string? actionCommandName)
    {
        var t = new Label { Text = title, FontSize = 15, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center, Margin = new Thickness(16, 14, 0, 2) };
        t.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } },
            Children = { t },
        };
        if (actionText != null && actionCommandName != null)
        {
            var more = new Label { Text = actionText, FontSize = 12, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 14, 16, 2) };
            more.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(actionCommandName, source: _vm));
            more.GestureRecognizers.Add(tap);
            Grid.SetColumn(more, 1);
            grid.Children.Add(more);
        }
        return grid;
    }

    /// <summary>横滑卡容器：绑定 VM 集合路径，卡片由工厂生成（每张定宽）</summary>
    private ScrollView CreateHScroll(string itemsPath, Func<View> cardFactory)
    {
        var layout = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(16, 2, 16, 6) };
        layout.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(itemsPath, source: _vm));
        BindableLayout.SetItemTemplate(layout, new DataTemplate(() => cardFactory()));
        return new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = layout,
        };
    }

    /// <summary>刷新一级 tab 样式（选中：主题色下划线 + 主文字色；未选中：次级文字色）</summary>
    private void RefreshTabStyles()
    {
        for (var i = 0; i < _tabViews.Count; i++)
        {
            var (label, underline) = _tabViews[i];
            var on = i == _vm.SelectedTabIndex;
            label.TextColor = on
                ? (Application.Current?.Resources.TryGetValue("TextPrimaryColor", out var pc) == true ? (Color)pc : Colors.White)
                : (Application.Current?.Resources.TryGetValue("TextSecondaryColor", out var sc) == true ? (Color)sc : Colors.Gray);
            underline.IsVisible = on;
        }
    }

    /// <summary>响应式布局：按可用宽度推导歌单网格列数、横竖屏搜索行与入口卡片排布。</summary>
    private void ApplyResponsiveLayout(double w)
    {
        if (w <= 0) return;

        // ① 歌单/歌手分块网格列数（VM 按卡片定宽推导，跨档重新分块；预留 44 = 左右 margin 32 + 滚动条 12）
        _vm.SetPlaylistGridWidth(w - 44);
        _vm.SetArtistGridWidth(w - 44);

        // ② 宽屏（≥900 或横屏）：搜索行合一；搜索行本身默认隐藏，由顶部 🔍 按钮展开
        double h = contentGrid.Height > 0 ? contentGrid.Height : contentGrid.Window?.Height ?? 0;
        bool landscape = h > 0 && w > h * 1.05;
        bool wide = w >= 900 || landscape;
        if (wide != _isWideLayout)
        {
            _isWideLayout = wide;
            if (wide) ApplyWideLayout(w);
            else ApplyNarrowLayout();
        }
    }

    /// <summary>歌单网格视图：外层 CollectionView 虚拟化「行」（LinearItemsLayout），
    /// 每行 HorizontalStackLayout 水平排 N 张定宽卡片。列数由 VM.SetPlaylistGridWidth 按宽度推导。
    /// 卡片挂点击命令打开歌单（行 SelectionMode=None）。</summary>
    private CollectionView CreatePlaylistsView()
    {
        var view = new CollectionView
        {
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            SelectionMode = SelectionMode.None,
            // 左右 16 边距不能放这里：Header（入口大卡）在 Margin 内侧会被二次缩进，
            // 大卡/chips 自带 Padding 16，行模板的 row 自带 Margin 16
            Margin = new Thickness(0, 6, 0, 0),
            RemainingItemsThreshold = 6,
        };
        view.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        view.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.PlaylistRows));
        view.ItemTemplate = new DataTemplate(CreatePlaylistRowTemplate);
        return view;
    }

    /// <summary>歌单分块行模板：一行 HorizontalStackLayout + BindableLayout 装定宽卡片。
    /// 不能用 FlexLayout：行内 Items 为 ObservableCollection（分页增量追加），
    /// BindableLayout 响应 Add 在布局中途插入子项，FlexLayout 测量节点缓存失步 → Layout NRE；
    /// HorizontalStackLayout 每趟直接遍历 Children，中途增删安全。</summary>
    private View CreatePlaylistRowTemplate()
    {
        var row = new HorizontalStackLayout
        {
            Spacing = 10,
            Margin = new Thickness(16, 0, 16, 10), // 左右 16 由行自身承担（网格 Margin 已归零，见 CreatePlaylistsView）
        };
        BindableLayout.SetItemTemplate(row, new DataTemplate(() => NeteaseUiKit.CreatePlaylistItemTemplate(
            NeteaseOnlineMusicViewModel.PlaylistCardWidth, _vm.OpenPlaylistCardCommand)));
        row.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.PlaylistGridRow.Items)));
        return row;
    }

    /// <summary>歌手列表视图（纯线性，SelectionChanged 打开歌手页）</summary>
    private CollectionView CreateArtistsView()
    {
        var view = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            Margin = new Thickness(0, 6, 0, 0),
        };
        view.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowArtists));
        view.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Artists));
        view.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateArtistItemTemplate());
        view.SelectionChanged += OnArtistSelected;
        return view;
    }

    /// <summary>歌曲列表视图（纯线性，Header 随视图）</summary>
    private CollectionView CreateSongsView()
    {
        var view = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            Margin = new Thickness(0, 6, 0, 0),
            RemainingItemsThreshold = 8,
        };
        view.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        view.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));
        view.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Songs));
        view.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate(new NeteaseUiKit.SongRowOptions
        {
            HeartCommand = _vm.ToggleLikeCommand,
            HeartVisibleSource = _vm,
            HeartVisibleProperty = nameof(NeteaseOnlineMusicViewModel.IsLoggedIn),
            TrashCommand = _vm.TrashFmSongCommand,
            TrashVisibleSource = _vm,
            TrashVisibleProperty = nameof(NeteaseOnlineMusicViewModel.IsFmMode),
            SimilarCommand = _vm.LoadSimilarSongsCommand,
            MvCommand = _vm.OpenMvCommand,
            CommentCommand = _vm.OpenCommentsCommand,
        }));
        view.SelectionChanged += OnSongSelected;
        // Header 与视图同生死，避免 Grid Row 5 多元素重叠渲染（曾导致红条覆盖歌单列表）
        view.Header = _songsHeader;
        return view;
    }

    /// <summary>入口卡片为横滑固定尺寸大卡，不再随宽度调整（历史响应式逻辑随网格布局移除）。</summary>

    /// <summary>异步拉取入口大卡背景封面：成功后显示封面并隐藏大图标（渐变兜底）。</summary>
    private async Task LoadEntryCoverAsync(NeteaseUiKit.EntryCard card, string entryId)
    {
        try
        {
            var url = await _vm.Plugin.GetEntryCoverUrlAsync(entryId);
            if (string.IsNullOrWhiteSpace(url) || card.HeroCover == null) return;
            card.HeroCover.Source = NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance.Convert(
                url, typeof(ImageSource), null, System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
            card.HeroCover.IsVisible = card.HeroCover.Source != null;
            if (card.HeroCover.Source != null && card.HeroIcon != null)
                card.HeroIcon.IsVisible = false;
        }
        catch { }
    }

    /// <summary>宽屏（≥900 或横屏）：搜索行合一（入口卡为横滑容器，无需重排）。</summary>
    private void ApplyWideLayout(double w)
    {
        // 搜索行：一行两列 [Entry | chips]
        searchRowGrid.RowDefinitions.Clear();
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchRowGrid.ColumnDefinitions.Clear();
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(searchBorder, 0);
        Grid.SetColumn(searchBorder, 0);
        searchBorder.Margin = new Thickness(16, 0, 8, 0);
        Grid.SetRow(searchModesScroll, 0);
        Grid.SetColumn(searchModesScroll, 1);
        searchModesScroll.Margin = new Thickness(0, 0, 16, 0);
        searchModesScroll.VerticalOptions = LayoutOptions.Center;
        searchModesLayout.Padding = new Thickness(0);
    }

    /// <summary>窄屏（&lt;900 且非横屏）：搜索框在上 chips 在下、列表单列。</summary>
    private void ApplyNarrowLayout()
    {
        // 搜索行：两行 [搜索框 / chips]
        searchRowGrid.ColumnDefinitions.Clear();
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        searchRowGrid.RowDefinitions.Clear();
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(searchBorder, 0);
        Grid.SetColumn(searchBorder, 0);
        searchBorder.Margin = new Thickness(16, 0, 16, 4);
        Grid.SetRow(searchModesScroll, 1);
        Grid.SetColumn(searchModesScroll, 0);
        searchModesScroll.Margin = new Thickness(0);
        searchModesScroll.VerticalOptions = LayoutOptions.Fill;
        searchModesLayout.Padding = new Thickness(16, 0, 16, 6);
    }

    /// <summary>
    /// 顶部 🔍 按钮：展开/收起搜索输入行。展开时聚焦输入框（联想浮层随之挂出）；
    /// 收起时清空关键词并回到歌单广场上下文（退出搜索结果/榜单浏览态）。
    /// </summary>
    private async Task ToggleSearchOpenAsync()
    {
        _searchOpen = !_searchOpen;
        searchRowGrid.IsVisible = _searchOpen;
        if (_searchOpen)
        {
            searchEntry.Focus();
            return;
        }
        try
        {
            searchEntry.Unfocus();
            if (!string.IsNullOrEmpty(_vm.SearchQuery))
            {
                _vm.SearchQuery = ""; // 触发 TextChanged 清空联想
                await _vm.BackToPlaylistsAsync();
            }
        }
        catch { }
    }

    private async Task OpenSimilarPlaylistsAsync()
    {
        var id = _vm.CurrentPlaylistId;
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            NeteaseSimilarPlaylistsPage page = null!;
            page = new NeteaseSimilarPlaylistsPage(id, _vm.Plugin, async (pl) =>
            {
                await _vm.OpenPlaylistAsync(pl);
                await NeteaseNav.PopAsync(page, _services);
            });
            await NeteaseNav.PushAsync(page);
        }
        catch { }
    }

    private async void OnArtistSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not NeteaseArtist artist) return;
        try
        {
            await NeteaseNav.PushAsync(new NeteaseArtistPage(artist, _vm.Plugin, _services));
        }
        catch { }
    }

    private async void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (_vm.ConsumeSuppressSelection()) return;
        if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
        await _vm.PlaySongAsync(song);
    }

    // ── UI 构建辅助方法 ──

    private Border CreateBackButton()
    {
        var border = new Border
        {
            Padding = new Thickness(12, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new Label { Text = "‹", FontSize = 26, Margin = new Thickness(0, -4, 0, 0), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        };
        border.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var backLabel = (Label)border.Content!;
        backLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            // 桌面无 Shell：本页不在任何导航栈（嵌入模式）→ 走宿主 GoBackAsync 关闭嵌入
            try { await NeteaseNav.PopAsync(this, _services); } catch { }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }
}
