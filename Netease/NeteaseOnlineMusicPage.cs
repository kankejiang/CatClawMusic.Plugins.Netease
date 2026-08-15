using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云在线音乐页面（C# 代码构建 UI，避免跨程序集 XAML 编译问题）。
/// <para>
/// 顶部：返回 + 标题 + 音质切换 + 账号；搜索框 + 搜索类型（歌曲/歌单/歌手）；
/// 功能入口（私人漫游/每日推荐/排行榜/我的歌单/推荐歌单）；分类 chips；
/// 歌单网格（分页加载）/歌手列表/歌曲列表三态切换；底部轻提示条。
/// 通过 DynamicResource 访问宿主应用的全局资源（颜色、样式）。
/// </para>
/// </summary>
public class NeteaseOnlineMusicPage : ContentPage
{
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;

    // 控件引用（事件处理需要）
    private readonly CollectionView _playlistsView;
    private readonly GridItemsLayout _playlistsLayout;
    private readonly CollectionView _songsView;
    private readonly CollectionView _artistsView;
    private readonly ActivityIndicator _loadingIndicator;

    // 响应式布局引用的控件（宽屏/窄屏切换需要重排行列归属）
    private readonly Grid searchRowGrid;
    private readonly Border searchBorder;
    private readonly ScrollView searchModesScroll;
    private readonly HorizontalStackLayout searchModesLayout;
    private readonly Grid entryContainer;
    private readonly Border fmCard;
    private readonly Border dailyCard;
    private readonly Border toplistCard;
    private readonly Border myCard;
    private readonly Border recommendCard;

    // 响应式布局缓存（宽屏 ≥900：入口卡片一行、歌曲/歌手双列、搜索行合一）
    private readonly GridItemsLayout _songsGridLayout = new(2, ItemsLayoutOrientation.Vertical) { HorizontalItemSpacing = 10, VerticalItemSpacing = 10 };
    private readonly GridItemsLayout _artistsGridLayout = new(2, ItemsLayoutOrientation.Vertical) { HorizontalItemSpacing = 10, VerticalItemSpacing = 10 };
    private readonly LinearItemsLayout _songsLinearLayout = new(ItemsLayoutOrientation.Vertical);
    private readonly LinearItemsLayout _artistsLinearLayout = new(ItemsLayoutOrientation.Vertical);
    private bool _isWideLayout;
    private bool _isLandscape;

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

        headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 12, 16, 8),
            Children = { backButton, titleLabel, qualityButton, accountButton },
        };
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(qualityButton, 2);
        Grid.SetColumn(accountButton, 3);

        // ── 搜索框 ──
        var searchEntry = new Entry { Placeholder = "搜索歌曲 / 歌单 / 歌手..." };
        searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        searchEntry.SetBinding(Entry.TextProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.Completed += async (_, _) => await _vm.SearchSongsAsync();

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

        // ── 搜索行容器（窄屏：搜索框上、chips 下；宽屏：同一行右侧）──
        searchRowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star } },
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Auto },
            },
            Children = { searchBorder, searchModesScroll },
        };
        Grid.SetRow(searchBorder, 0);
        Grid.SetRow(searchModesScroll, 1);

        // ── 功能入口（登录后可见：我的歌单 / 推荐歌单）──
        fmCard = NeteaseUiKit.CreateEntryCard("🎧", "私人漫游", "随机推荐", "#667eea", "#764ba2");
        var fmTap = new TapGestureRecognizer();
        fmTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadPrivateFmCommand));
        fmCard.GestureRecognizers.Add(fmTap);

        dailyCard = NeteaseUiKit.CreateEntryCard("📅", "每日推荐", "今天想听什么", "#f7971e", "#ffd200");
        var dailyTap = new TapGestureRecognizer();
        dailyTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadDailyRecommendCommand));
        dailyCard.GestureRecognizers.Add(dailyTap);

        toplistCard = NeteaseUiKit.CreateEntryCard("🔥", "排行榜", "飙升 · 新歌 · 热歌", "#f953c6", "#b91d73");
        var toplistTap = new TapGestureRecognizer();
        toplistTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadToplistsCommand));
        toplistCard.GestureRecognizers.Add(toplistTap);

        myCard = NeteaseUiKit.CreateEntryCard("💛", "我的歌单", "创建与收藏", "#11998e", "#38ef7d");
        myCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var myTap = new TapGestureRecognizer();
        myTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadMyPlaylistsCommand));
        myCard.GestureRecognizers.Add(myTap);

        recommendCard = NeteaseUiKit.CreateEntryCard("✨", "推荐歌单", "每日为你精选", "#fc466b", "#3f5efb");
        recommendCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var recommendTap = new TapGestureRecognizer();
        recommendTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadRecommendPlaylistsCommand));
        recommendCard.GestureRecognizers.Add(recommendTap);

        // 入口卡片容器（窄屏两列三行；宽屏一行五列，由 ApplyWideLayout/ApplyNarrowLayout 重排行列归属）
        entryContainer = new Grid
        {
            Padding = new Thickness(16, 2, 16, 6),
            ColumnSpacing = 10,
            RowSpacing = 10,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Star },
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Auto },
            },
            Children = { fmCard, dailyCard, toplistCard, myCard, recommendCard },
        };
        Grid.SetRow(fmCard, 0); Grid.SetColumn(fmCard, 0);
        Grid.SetRow(dailyCard, 0); Grid.SetColumn(dailyCard, 1);
        Grid.SetRow(toplistCard, 1); Grid.SetColumn(toplistCard, 0);
        Grid.SetRow(myCard, 1); Grid.SetColumn(myCard, 1);
        Grid.SetRow(recommendCard, 2); Grid.SetColumn(recommendCard, 0);

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
            HeightRequest = 42,
            Content = categoriesLayout,
        };
        categoriesScroll.SetBinding(ScrollView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowCategories));

        // ── 歌单网格（分页加载）──
        _playlistsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical)
        {
            HorizontalItemSpacing = 10,
            VerticalItemSpacing = 10,
        };
        _playlistsView = new CollectionView
        {
            ItemsLayout = _playlistsLayout,
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(16, 6, 16, 0),
            RemainingItemsThreshold = 6,
        };
        _playlistsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        _playlistsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));
        _playlistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Playlists));
        _playlistsView.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreatePlaylistItemTemplate());
        _playlistsView.SelectionChanged += OnPlaylistSelected;

        // ── 歌手列表（搜索歌手模式）──
        _artistsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = _artistsLinearLayout,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _artistsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowArtists));
        _artistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Artists));
        _artistsView.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateArtistItemTemplate());
        _artistsView.SelectionChanged += OnArtistSelected;

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

        var songsHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } },
            ColumnSpacing = 8,
            Padding = new Thickness(16, 4, 16, 8),
            Children = { songsBackButton, songsTitleLabel, playAllButton },
        };
        Grid.SetColumn(songsTitleLabel, 1);
        Grid.SetColumn(playAllButton, 2);

        _songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = _songsLinearLayout,
            Margin = new Thickness(0, 6, 0, 0),
            RemainingItemsThreshold = 8,
        };
        _songsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        _songsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));
        _songsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Songs));
        _songsView.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate(new NeteaseUiKit.SongRowOptions
        {
            HeartCommand = _vm.ToggleLikeCommand,
            HeartVisibleSource = _vm,
            HeartVisibleProperty = nameof(NeteaseOnlineMusicViewModel.IsLoggedIn),
            TrashCommand = _vm.TrashFmSongCommand,
            TrashVisibleSource = _vm,
            TrashVisibleProperty = nameof(NeteaseOnlineMusicViewModel.IsFmMode),
        }));
        _songsView.SelectionChanged += OnSongSelected;
        // songsHeader 合并到 _songsView.Header：与 _songsView 同生死，避免 Grid Row 5 多元素重叠渲染（曾导致红条覆盖歌单列表）
        _songsView.Header = songsHeader;

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

        // ── 组装页面 ──
        contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // search row（搜索框 + chips）
                new() { Height = GridLength.Auto }, // entry cards
                new() { Height = GridLength.Auto }, // categories
                new() { Height = GridLength.Star }, // content
            },
            Children = { headerGrid, searchRowGrid, entryContainer, categoriesScroll, _playlistsView, _artistsView, _songsView, _loadingIndicator, tipBorder },
        };
        Grid.SetRow(searchRowGrid, 1);
        Grid.SetRow(entryContainer, 2);
        Grid.SetRow(categoriesScroll, 3);
        Grid.SetRow(_playlistsView, 4);
        Grid.SetRow(_artistsView, 4);
        Grid.SetRow(_songsView, 4);
        Grid.SetRow(_loadingIndicator, 4);
        Grid.SetRow(tipBorder, 4);

        Content = contentGrid;

        // 尺寸变化时调整响应式布局（桌面嵌入模式下页面 SizeChanged 不触发，改挂 contentGrid）
        contentGrid.SizeChanged += (_, _) => ApplyResponsiveLayout(contentGrid.Width);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyResponsiveLayout(Content?.Width ?? 0);
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
            // 跳转宿主的 WebView 登录页（通过 Shell 路由）。
            // 宿主 WebViewLoginViewModel 通过 platform 参数匹配 IOnlineMusicPlugin.PlatformName，
            // 网易云插件的 PlatformName 固定为 "netease"。
            if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync($"webviewlogin?platform=netease");
                return;
            }

            // 桌面端宿主无 Shell（窗口直连，不走 Shell 导航栈）：
            // 回退走宿主导航服务（NavigationService 桌面端会把路由解析为页面并嵌入主区域打开）。
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

    /// <summary>响应式布局：按可用宽度设置歌单列数、搜索行与歌曲/歌手列表列数；横屏时搜索框并入头部行。</summary>
    private void ApplyResponsiveLayout(double w)
    {
        if (w <= 0) return;

        int span = w switch
        {
            < 600 => 2,
            < 900 => 3,
            < 1200 => 4,
            < 1500 => 5,
            _ => 6,
        };
        if (_playlistsLayout.Span != span) _playlistsLayout.Span = span;

        // 横屏（宽明显大于高）：搜索框/chips 并入头部行，释放一整行纵向空间
        double h = contentGrid.Height;
        bool landscape = h > 0 && w > h * 1.2;
        if (landscape != _isLandscape)
        {
            _isLandscape = landscape;
            if (landscape) MergeSearchToHeader();
            else RestoreSearchRow();
        }

        // 横屏或宽屏（≥900）：入口卡片一行五列、歌曲/歌手双列通栏
        bool wide = w >= 900 || landscape;
        if (wide != _isWideLayout)
        {
            _isWideLayout = wide;
            if (wide) ApplyWideLayout();
            else ApplyNarrowLayout();
        }

        // 宽屏内歌曲/歌手列表按宽度自适应列数
        if (wide)
        {
            int listSpan = w switch { < 1100 => 2, < 1500 => 3, _ => 4 };
            if (_songsGridLayout.Span != listSpan) _songsGridLayout.Span = listSpan;
            if (_artistsGridLayout.Span != listSpan) _artistsGridLayout.Span = listSpan;
        }

        // 入口卡片保持正方形：高度 = 卡片列宽（窄屏两列 / 宽屏五列），超大屏封顶避免过高
        UpdateEntryCardHeights(w);
    }

    /// <summary>入口卡片设为正方形：高度 = 卡片列宽（窄屏两列 / 宽屏五列），超大屏封顶避免过高。</summary>
    private void UpdateEntryCardHeights(double w)
    {
        double cardWidth = _isWideLayout
            ? (w - 32 - 4 * 10) / 5   // 宽屏：5 列，4 个 10px 间距
            : (w - 32 - 1 * 10) / 2;  // 窄屏：2 列，1 个 10px 间距
        if (cardWidth <= 0) return;
        double cardHeight = Math.Min(cardWidth, 150);
        foreach (var card in new[] { fmCard, dailyCard, toplistCard, myCard, recommendCard })
            card.HeightRequest = cardHeight;
    }

    /// <summary>宽屏（≥900 或横屏）：搜索行合一、入口卡片一行五列、歌曲/歌手双列通栏。</summary>
    private void ApplyWideLayout()
    {
        // 搜索行：一行两列 [Entry | chips]（横屏时搜索元素在头部行，此配置供恢复竖屏使用）
        searchRowGrid.RowDefinitions.Clear();
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchRowGrid.ColumnDefinitions.Clear();
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!_isLandscape)
        {
            Grid.SetRow(searchBorder, 0);
            Grid.SetColumn(searchBorder, 0);
            searchBorder.Margin = new Thickness(16, 0, 8, 0);
            Grid.SetRow(searchModesScroll, 0);
            Grid.SetColumn(searchModesScroll, 1);
            searchModesScroll.Margin = new Thickness(0, 0, 16, 0);
            searchModesScroll.VerticalOptions = LayoutOptions.Center;
            searchModesLayout.Padding = new Thickness(0);
        }

        // 入口卡片：一行五列
        entryContainer.ColumnDefinitions.Clear();
        for (int i = 0; i < 5; i++)
            entryContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        entryContainer.RowDefinitions.Clear();
        entryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetEntryCardCell(fmCard, 0, 0);
        SetEntryCardCell(dailyCard, 0, 1);
        SetEntryCardCell(toplistCard, 0, 2);
        SetEntryCardCell(myCard, 0, 3);
        SetEntryCardCell(recommendCard, 0, 4);

        // 歌曲/歌手双列通栏
        _songsView.ItemsLayout = _songsGridLayout;
        _artistsView.ItemsLayout = _artistsGridLayout;
    }

    /// <summary>窄屏（&lt;900 且非横屏）：搜索框在上 chips 在下、入口卡片两列三行、列表单列。</summary>
    private void ApplyNarrowLayout()
    {
        // 搜索行：两行 [搜索框 / chips]
        searchRowGrid.ColumnDefinitions.Clear();
        searchRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        searchRowGrid.RowDefinitions.Clear();
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchRowGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!_isLandscape)
        {
            Grid.SetRow(searchBorder, 0);
            Grid.SetColumn(searchBorder, 0);
            searchBorder.Margin = new Thickness(16, 0, 16, 4);
            Grid.SetRow(searchModesScroll, 1);
            Grid.SetColumn(searchModesScroll, 0);
            searchModesScroll.Margin = new Thickness(0);
            searchModesScroll.VerticalOptions = LayoutOptions.Fill;
            searchModesLayout.Padding = new Thickness(16, 0, 16, 6);
        }

        // 入口卡片：两列三行
        entryContainer.ColumnDefinitions.Clear();
        entryContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        entryContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        entryContainer.RowDefinitions.Clear();
        entryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        entryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        entryContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetEntryCardCell(fmCard, 0, 0);
        SetEntryCardCell(dailyCard, 0, 1);
        SetEntryCardCell(toplistCard, 1, 0);
        SetEntryCardCell(myCard, 1, 1);
        SetEntryCardCell(recommendCard, 2, 0);

        // 列表单列
        _songsView.ItemsLayout = _songsLinearLayout;
        _artistsView.ItemsLayout = _artistsLinearLayout;
    }

    private static void SetEntryCardCell(Border card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
    }

    /// <summary>横屏：搜索框 + chips 并入头部行（隐藏标题腾位），释放 contentGrid 一整行纵向空间。</summary>
    private void MergeSearchToHeader()
    {
        // 从搜索行移除搜索元素，再放入头部行
        searchRowGrid.Children.Remove(searchBorder);
        searchRowGrid.Children.Remove(searchModesScroll);
        searchRowGrid.IsVisible = false;

        // 头部行重排：返回 | 标题(隐藏) | 搜索框(Star) | chips | 音质 | 账号
        headerGrid.ColumnDefinitions.Clear();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // back
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // title（隐藏）
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star }); // search
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chips
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // quality
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // account

        titleLabel.IsVisible = false;
        headerGrid.Children.Add(searchBorder);
        headerGrid.Children.Add(searchModesScroll);
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(searchBorder, 2);
        Grid.SetColumn(searchModesScroll, 3);
        Grid.SetColumn(qualityButton, 4);
        Grid.SetColumn(accountButton, 5);

        searchBorder.Margin = new Thickness(0, 0, 8, 0);
        searchBorder.VerticalOptions = LayoutOptions.Center;
        searchModesScroll.Margin = new Thickness(0);
        searchModesScroll.VerticalOptions = LayoutOptions.Center;
        searchModesLayout.Padding = new Thickness(0);
        headerGrid.Padding = new Thickness(16, 6, 16, 6);
    }

    /// <summary>恢复竖屏：搜索框/chips 回到搜索行，头部行恢复标题。</summary>
    private void RestoreSearchRow()
    {
        // 从头部行移除搜索元素
        headerGrid.Children.Remove(searchBorder);
        headerGrid.Children.Remove(searchModesScroll);

        // 头部行恢复：返回 | 标题(Star) | 音质 | 账号
        headerGrid.ColumnDefinitions.Clear();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleLabel, 1);
        Grid.SetColumn(qualityButton, 2);
        Grid.SetColumn(accountButton, 3);
        titleLabel.IsVisible = true;
        headerGrid.Padding = new Thickness(16, 12, 16, 8);

        // 搜索元素放回搜索行（行列归属与边距由 ApplyWideLayout/ApplyNarrowLayout 设置）
        searchRowGrid.Children.Add(searchBorder);
        searchRowGrid.Children.Add(searchModesScroll);
        searchRowGrid.IsVisible = true;
    }

    private async void OnPlaylistSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not OnlinePlaylist playlist) return;
        await _vm.OpenPlaylistAsync(playlist);
    }

    private async void OnArtistSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not NeteaseArtist artist) return;
        try
        {
            await Shell.Current.Navigation.PushAsync(new NeteaseArtistPage(artist, _vm.Plugin, _services));
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
            try
            {
                if (Shell.Current != null)
                    await Shell.Current.Navigation.PopAsync();
                else if (_services.GetService<INavigationService>() is { } nav)
                    await nav.GoBackAsync();
            }
            catch { }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }
}
