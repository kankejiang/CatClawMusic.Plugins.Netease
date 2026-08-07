using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云在线音乐「发现页子 tab」内嵌视图（C# 代码构建 UI，避免跨程序集 XAML 编译问题）。
/// <para>
/// 与 <see cref="NeteaseOnlineMusicPage"/> 共享同一套 ViewModel，
/// 但作为 <see cref="View"/>（非整页）内嵌到宿主发现页面板区。
/// 相比整页版本，去掉了返回按钮与账号头部（内嵌场景不需要），
/// 音质切换以内联小按钮形式保留在搜索行右侧。
/// </para>
/// </summary>
public class NeteaseDiscoverTabView : ContentView
{
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;

    private readonly CollectionView _playlistsView;
    private readonly GridItemsLayout _playlistsLayout;
    private readonly CollectionView _songsView;
    private readonly CollectionView _artistsView;
    private readonly ActivityIndicator _loadingIndicator;

    public NeteaseDiscoverTabView(NeteaseOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        // 透明背景，融入发现页背景
        BackgroundColor = Colors.Transparent;

        // ── 搜索框 + 音质切换 ──
        var searchEntry = new Entry { Placeholder = "搜索歌曲 / 歌单 / 歌手..." };
        searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        searchEntry.SetBinding(Entry.TextProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.Completed += async (_, _) => await _vm.SearchSongsAsync();

        var searchBorder = new Border
        {
            Content = searchEntry,
            Padding = new Thickness(14, 8),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        var qualityButton = new Border
        {
            Padding = new Thickness(10, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        qualityButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var qualityLabel = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        qualityLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        qualityLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.QualityText));
        qualityButton.Content = qualityLabel;
        var qualityTap = new TapGestureRecognizer();
        qualityTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.CycleQualityCommand));
        qualityButton.GestureRecognizers.Add(qualityTap);

        var searchRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 4),
            Children = { searchBorder, qualityButton },
        };
        Grid.SetColumn(qualityButton, 1);

        // ── 搜索类型 chips（歌曲/歌单/歌手）──
        var searchModesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(0, 0, 0, 6) };
        BindableLayout.SetItemsSource(searchModesLayout, _vm.SearchModes);
        BindableLayout.SetItemTemplate(searchModesLayout,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectSearchModeCommand), nameof(CategoryChipItem.Name)));
        var searchModesScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 36,
            Content = searchModesLayout,
        };

        // ── 功能入口（登录后可见：我的歌单 / 推荐歌单）──
        var fmCard = NeteaseUiKit.CreateEntryCard("🎧 私人漫游", "随机推荐", "#667eea", "#764ba2");
        var fmTap = new TapGestureRecognizer();
        fmTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadPrivateFmCommand));
        fmCard.GestureRecognizers.Add(fmTap);

        var dailyCard = NeteaseUiKit.CreateEntryCard("📅 每日推荐", "今天想听什么", "#f7971e", "#ffd200");
        var dailyTap = new TapGestureRecognizer();
        dailyTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadDailyRecommendCommand));
        dailyCard.GestureRecognizers.Add(dailyTap);

        var toplistCard = NeteaseUiKit.CreateEntryCard("🔥 排行榜", "飙升 · 新歌 · 热歌", "#f953c6", "#b91d73");
        var toplistTap = new TapGestureRecognizer();
        toplistTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadToplistsCommand));
        toplistCard.GestureRecognizers.Add(toplistTap);

        var myCard = NeteaseUiKit.CreateEntryCard("💛 我的歌单", "创建与收藏", "#11998e", "#38ef7d");
        myCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var myTap = new TapGestureRecognizer();
        myTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadMyPlaylistsCommand));
        myCard.GestureRecognizers.Add(myTap);

        var recommendCard = NeteaseUiKit.CreateEntryCard("✨ 推荐歌单", "每日为你精选", "#fc466b", "#3f5efb");
        recommendCard.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));
        var recommendTap = new TapGestureRecognizer();
        recommendTap.SetBinding(TapGestureRecognizer.CommandProperty, nameof(NeteaseOnlineMusicViewModel.LoadRecommendPlaylistsCommand));
        recommendCard.GestureRecognizers.Add(recommendTap);

        var entryRow1 = CreateEntryRow3(fmCard, dailyCard, toplistCard);
        var entryRow2 = CreateEntryRow3(myCard, recommendCard, null);
        entryRow2.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoggedIn));

        var entryContainer = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(0, 2, 0, 6),
            Children = { entryRow1, entryRow2 },
        };

        // ── 分类 chips（水平滚动，仅歌单广场可见）──
        var categoriesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(0, 4, 0, 6) };
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
            Margin = new Thickness(0, 6, 0, 0),
            RemainingItemsThreshold = 6,
        };
        _playlistsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        _playlistsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));
        _playlistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Playlists));
        _playlistsView.ItemTemplate = new DataTemplate(NeteaseUiKit.CreatePlaylistItemTemplate);
        _playlistsView.SelectionChanged += OnPlaylistSelected;

        // ── 歌手列表（搜索歌手模式）──
        _artistsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _artistsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowArtists));
        _artistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Artists));
        _artistsView.ItemTemplate = new DataTemplate(NeteaseUiKit.CreateArtistItemTemplate);
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
            Padding = new Thickness(0, 4, 0, 8),
            Children = { songsBackButton, songsTitleLabel, playAllButton },
        };
        Grid.SetColumn(songsTitleLabel, 1);
        Grid.SetColumn(playAllButton, 2);
        songsHeader.SetBinding(Grid.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));

        _songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
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

        // ── 轻提示条 ──
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
            Margin = new Thickness(8, 0, 8, 8),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Center,
            Content = tipLabel,
        };
        tipBorder.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.HasTip));

        // ── 组装（VerticalStackLayout 垂直堆叠：避免 Grid Star 单元格被压缩导致子 View 重叠）──
        // Grid 同单元叠加的 _playlistsView/_songsView/_loadingIndicator/tipBorder 在 Star 行被父约束
        // 压扁时会出现"封面与文字在同一压缩区域绘制"的重叠 bug。改用 VerticalStackLayout 让所有内容
        // 自然按 DesiredSize 撑开，IsVisible=false 的项不参与 measure，互斥显示天然不重叠。
        var body = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(0, 4, 0, 8),
            Children =
            {
                searchRow,
                searchModesScroll,
                entryContainer,
                categoriesScroll,
                _artistsView,
                _playlistsView,
                songsHeader,
                _songsView,
                _loadingIndicator,
                tipBorder
            },
        };

        Content = body;

        // 生命周期：内嵌视图挂载即加载数据，卸载即解绑事件避免悬挂
        Loaded += async (_, _) =>
        {
            AdjustPlaylistSpan();
            await _vm.OnAppearingAsync();
        };
        Unloaded += (_, _) => _vm.Detach();
        SizeChanged += (_, _) => AdjustPlaylistSpan();
    }

    /// <summary>三列入口行（第三个可为 null）</summary>
    private static Grid CreateEntryRow3(Border c1, Border? c2, Border? c3)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Star },
            },
            ColumnSpacing = 10,
            Children = { c1 },
        };
        if (c2 != null) { row.Children.Add(c2); Grid.SetColumn(c2, 1); }
        if (c3 != null) { row.Children.Add(c3); Grid.SetColumn(c3, 2); }
        return row;
    }

    private void AdjustPlaylistSpan()
    {
        var w = Width > 0 ? Width : ((Parent as VisualElement)?.Width ?? 0);
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
}
