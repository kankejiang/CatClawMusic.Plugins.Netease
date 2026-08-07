using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云在线音乐「发现页子 tab」内嵌视图（C# 代码构建 UI，避免跨程序集 XAML 编译问题）。
/// <para>
/// 与 <see cref="NeteaseOnlineMusicPage"/> 共享同一套 ViewModel（<see cref="NeteaseOnlineMusicViewModel"/>），
/// 但作为 <see cref="View"/>（非整页）内嵌到宿主发现页面板区，复用 DynamicResource 访问宿主全局资源。
/// 相比整页版本，去掉了返回按钮与账号头部（内嵌场景不需要）。
/// </para>
/// </summary>
public class NeteaseDiscoverTabView : ContentView
{
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;

    private readonly CollectionView _playlistsView;
    private readonly GridItemsLayout _playlistsLayout;
    private readonly CollectionView _songsView;
    private readonly ActivityIndicator _loadingIndicator;

    public NeteaseDiscoverTabView(NeteaseOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        // 透明背景，融入发现页背景
        BackgroundColor = Colors.Transparent;

        // ── 搜索框 ──
        var searchEntry = new Entry { Placeholder = "搜索歌曲..." };
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
            Margin = new Thickness(0, 0, 0, 8),
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        // ── 私人漫游 / 每日推荐 / 排行榜 入口 ──
        var fmCard = CreateEntryCard("🎧 私人漫游", "随机推荐", "#667eea", "#764ba2");
        var fmTap = new TapGestureRecognizer();
        fmTap.Tapped += async (_, _) => await _vm.LoadPrivateFmAsync();
        fmCard.GestureRecognizers.Add(fmTap);

        var dailyCard = CreateEntryCard("📅 每日推荐", "今天想听什么", "#f7971e", "#ffd200");
        var dailyTap = new TapGestureRecognizer();
        dailyTap.Tapped += async (_, _) => await _vm.LoadDailyRecommendAsync();
        dailyCard.GestureRecognizers.Add(dailyTap);

        var toplistCard = CreateEntryCard("🔥 排行榜", "飙升 · 新歌 · 热歌", "#f953c6", "#b91d73");
        var toplistTap = new TapGestureRecognizer();
        toplistTap.Tapped += async (_, _) => await _vm.LoadToplistsAsync();
        toplistCard.GestureRecognizers.Add(toplistTap);

        var entryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Star }, new() { Width = GridLength.Star } },
            ColumnSpacing = 10,
            Padding = new Thickness(0, 2, 0, 6),
            Children = { fmCard, dailyCard, toplistCard },
        };
        Grid.SetColumn(dailyCard, 1);
        Grid.SetColumn(toplistCard, 2);

        // ── 分类 chips（水平滚动）──
        var categoriesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(0, 4, 0, 6) };
        categoriesLayout.SetBinding(HorizontalStackLayout.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));
        BindableLayout.SetItemsSource(categoriesLayout, _vm.Categories);
        BindableLayout.SetItemTemplate(categoriesLayout, new DataTemplate(() =>
        {
            var chip = new Border
            {
                Padding = new Thickness(10, 5),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
            };
            chip.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            var chipLabel = new Label { FontSize = 11, VerticalOptions = LayoutOptions.Center };
            chipLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
            chipLabel.SetBinding(Label.TextProperty, nameof(CategoryChipItem.Name));
            chip.Content = chipLabel;
            chip.Triggers.Add(new DataTrigger(typeof(Border))
            {
                Binding = new Binding(nameof(CategoryChipItem.IsSelected)),
                Value = true,
                Setters = { new Setter { Property = Border.BackgroundColorProperty, Value = Application.Current?.Resources["PrimaryColor"] } },
            });
            chipLabel.Triggers.Add(new DataTrigger(typeof(Label))
            {
                Binding = new Binding(nameof(CategoryChipItem.IsSelected)),
                Value = true,
                Setters = { new Setter { Property = Label.TextColorProperty, Value = Colors.White } },
            });
            var tap = new TapGestureRecognizer();
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(
                nameof(NeteaseOnlineMusicViewModel.SelectCategoryCommand), source: _vm));
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding(nameof(CategoryChipItem.Name)));
            chip.GestureRecognizers.Add(tap);
            return chip;
        }));

        var categoriesScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 42,
            Content = categoriesLayout,
        };
        categoriesScroll.SetBinding(ScrollView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));

        // ── 歌单网格 ──
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
        };
        _playlistsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));
        _playlistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Playlists));
        _playlistsView.ItemTemplate = new DataTemplate(CreatePlaylistItemTemplate);
        _playlistsView.SelectionChanged += OnPlaylistSelected;

        // ── 歌曲列表模式 ──
        var songsBackButton = new Border
        {
            Padding = new Thickness(10, 5),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = new Label { Text = "‹ 歌单", FontSize = 12 },
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
        };
        _songsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));
        _songsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Songs));
        _songsView.ItemTemplate = new DataTemplate(CreateSongItemTemplate);
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

        // ── 组装（垂直布局，发现页外层负责滚动）──
        var body = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(0, 4, 0, 8),
            Children = { searchBorder, entryRow, categoriesScroll, _playlistsView, songsHeader, _songsView, _loadingIndicator },
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

    private async void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
        await _vm.PlaySongAsync(song);
    }

    // ── UI 模板辅助（与 NeteaseOnlineMusicPage 一致的视觉）──

    private static Border CreateEntryCard(string title, string subtitle, string color1, string color2)
    {
        var titleLabel = new Label { Text = title, FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White };
        var subtitleLabel = new Label { Text = subtitle, FontSize = 11, TextColor = Color.FromArgb("#CCFFFFFF") };
        var content = new VerticalStackLayout
        {
            Spacing = 3,
            Children = { titleLabel, subtitleLabel },
        };
        return new Border
        {
            Padding = new Thickness(14, 12),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(color1), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(color2), Offset = 1 },
                },
            },
            Content = content,
        };
    }

    private static View CreatePlaylistItemTemplate()
    {
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            HeightRequest = 150,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl)) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var nameLabel = new Label
        {
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation,
            Padding = new Thickness(6, 0, 6, 6),
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, nameof(OnlinePlaylist.Name));

        var countLabel = new Label
        {
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 6),
        };
        countLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        countLabel.SetBinding(Label.TextProperty, new Binding(nameof(OnlinePlaylist.SongCount), stringFormat: "{0} 首"));

        var card = new Border
        {
            Padding = new Thickness(0),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardBackgroundColor");
        card.Content = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { coverBorder, nameLabel, countLabel },
        };
        return card;
    }

    private static View CreateSongItemTemplate()
    {
        var coverBorder = new Border
        {
            WidthRequest = 40,
            HeightRequest = 40,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            StrokeThickness = 0,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 40, HeightRequest = 40 };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl)) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var titleLabel = new Label { FontSize = 14, FontFamily = "OpenSansSemibold", MaxLines = 1 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        titleLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Title));

        var artistLabel = new Label { FontSize = 11, MaxLines = 1 };
        artistLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        artistLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Artist));

        var textLayout = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { titleLabel, artistLabel },
        };

        return new Grid
        {
            Padding = new Thickness(14, 8),
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } },
            ColumnSpacing = 12,
            Children = { coverBorder, textLayout },
        };
    }
}
