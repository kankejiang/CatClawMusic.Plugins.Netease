using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云独立搜索页：从主页 🔍 推入。搜索框（自动聚焦）+ 类型 chips（歌曲/歌单/歌手）
/// + 全屏遮罩热词/联想浮层 + 三类结果列表。
/// <para>
/// 与主页共享 <see cref="NeteaseOnlineMusicViewModel"/>（播放/红心/评论/MV 命令无缝衔接）；
/// 热词遮罩为整内容区不透明背景（聚焦时完全盖住下层结果），返回时经
/// <see cref="NeteaseOnlineMusicViewModel.CloseSearchAsync"/> 还原主页 tab 上下文。
/// </para>
/// </summary>
public class NeteaseSearchPage : ContentPage
{
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly IServiceProvider _services;
    private readonly Entry _searchEntry;

    public NeteaseSearchPage(NeteaseOnlineMusicViewModel vm, IServiceProvider services)
    {
        _vm = vm;
        _services = services;
        BindingContext = _vm;

        Title = "搜索";
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 顶部：返回 + 搜索框 ──
        var backButton = new Border
        {
            Padding = new Thickness(12, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new Label { Text = "‹", FontSize = 26, Margin = new Thickness(0, -4, 0, 0), VerticalOptions = LayoutOptions.Center },
        };
        backButton.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var backLabel = (Label)backButton.Content!;
        backLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) => await CloseAndPopAsync();
        backButton.GestureRecognizers.Add(backTap);

        _searchEntry = new Entry
        {
            Placeholder = "搜索歌曲 / 歌单 / 歌手...",
            ReturnType = ReturnType.Search,
        };
        _searchEntry.SetDynamicResource(Entry.TextColorProperty, "TextPrimaryColor");
        _searchEntry.SetBinding(Entry.TextProperty,
            new Binding(nameof(NeteaseOnlineMusicViewModel.SearchQuery), mode: BindingMode.TwoWay));
        _searchEntry.Completed += async (_, _) => { _searchEntry.Unfocus(); await _vm.SearchSongsAsync(); };
        _searchEntry.TextChanged += (_, e) => _ = _vm.OnSearchTextChangedAsync(e.NewTextValue);
        // 聚焦时弹全屏热词遮罩（无数据先预热热词）；失焦收起
        _searchEntry.Focused += async (_, _) =>
        {
            _vm.IsSearchFocused = true;
            if (_vm.SuggestItems.Count == 0) await _vm.OnSearchTextChangedAsync("");
        };
        _searchEntry.Unfocused += (_, _) => _vm.IsSearchFocused = false;

        var searchBorder = new Border
        {
            Content = _searchEntry,
            Padding = new Thickness(14, 8),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
        };
        searchBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } },
            ColumnSpacing = 10,
            Padding = new Thickness(16, 12, 16, 8),
            Children = { backButton, searchBorder },
        };
        Grid.SetColumn(searchBorder, 1);

        // ── 搜索类型 chips（歌曲/歌单/歌手）──
        var modesLayout = new HorizontalStackLayout { Spacing = 6, Padding = new Thickness(16, 0, 16, 6) };
        BindableLayout.SetItemsSource(modesLayout, _vm.SearchModes);
        BindableLayout.SetItemTemplate(modesLayout,
            NeteaseUiKit.CreateCategoryChipTemplate(_vm, nameof(NeteaseOnlineMusicViewModel.SelectSearchModeCommand), nameof(CategoryChipItem.Name)));
        var modesScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            HeightRequest = 36,
            Content = modesLayout,
        };

        // ── 结果区：歌曲（标题 + 全部播放 + 列表）──
        var titleLabel = new Label
        {
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(16, 4, 0, 8),
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        titleLabel.SetBinding(Label.TextProperty, nameof(NeteaseOnlineMusicViewModel.CurrentListTitle));

        var playAllButton = new Border
        {
            Padding = new Thickness(12, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(0, 4, 16, 0),
            VerticalOptions = LayoutOptions.Center,
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
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } },
            Padding = new Thickness(16, 2, 0, 4),
            Children = { titleLabel, playAllButton },
        };
        Grid.SetColumn(playAllButton, 1);

        var songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            RemainingItemsThreshold = 8,
        };
        songsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        songsView.SetBinding(CollectionView.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));
        songsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Songs));
        songsView.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate(new NeteaseUiKit.SongRowOptions
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
        songsView.SelectionChanged += OnSongSelected;

        var songsHost = new VerticalStackLayout { Spacing = 0 };
        songsHost.Add(songsHeader);
        songsHost.Add(songsView);
        songsHost.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowSongs));
        Opaque(songsHost);

        // ── 结果区：歌单（分页网格，与主页同款分块行方案）──
        var playlistsView = new CollectionView
        {
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            SelectionMode = SelectionMode.None,
            RemainingItemsThreshold = 6,
        };
        playlistsView.RemainingItemsThresholdReached += async (_, _) => await _vm.LoadMoreAsync();
        playlistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.PlaylistRows));
        playlistsView.ItemTemplate = new DataTemplate(() =>
        {
            // 行内 Items 为 ObservableCollection（分页增量追加），必须 HorizontalStackLayout（见主页同款注释）
            var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(16, 0, 16, 10) };
            BindableLayout.SetItemTemplate(row, new DataTemplate(() => NeteaseUiKit.CreatePlaylistItemTemplate(
                NeteaseOnlineMusicViewModel.PlaylistCardWidth, _vm.OpenPlaylistCardCommand)));
            row.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(NeteaseOnlineMusicViewModel.PlaylistGridRow.Items)));
            return row;
        });

        var playlistsHost = new VerticalStackLayout { Spacing = 0, Children = { playlistsView } };
        playlistsHost.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowPlaylists));
        Opaque(playlistsHost);

        // ── 结果区：歌手 ──
        var artistsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
        };
        artistsView.SetBinding(CollectionView.ItemsSourceProperty, nameof(NeteaseOnlineMusicViewModel.Artists));
        artistsView.ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateArtistItemTemplate());
        artistsView.SelectionChanged += OnArtistSelected;

        var artistsHost = new VerticalStackLayout { Spacing = 0, Children = { artistsView } };
        artistsHost.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.ShowArtists));
        Opaque(artistsHost);

        // ── 加载指示 + 状态文字 ──
        var loading = new ActivityIndicator
        {
            WidthRequest = 36,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
        };
        loading.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");
        loading.SetBinding(ActivityIndicator.IsRunningProperty, nameof(NeteaseOnlineMusicViewModel.IsLoading));
        loading.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsLoading));

        // ── 热词/联想 全屏遮罩：聚焦时整内容区不透明覆盖，热词 chips 居顶 ──
        var suggestFlex = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Start,
        };
        BindableLayout.SetItemsSource(suggestFlex, _vm.SuggestItems);
        BindableLayout.SetItemTemplate(suggestFlex, BuildSuggestChipTemplate());
        var suggestTitle = new Label
        {
            Text = "热门搜索 / 搜索建议",
            FontSize = 13,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(20, 10, 0, 2),
        };
        suggestTitle.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        var suggestMask = new VerticalStackLayout { Spacing = 0, Children = { suggestTitle, suggestFlex } };
        suggestMask.SetBinding(VisualElement.IsVisibleProperty, nameof(NeteaseOnlineMusicViewModel.IsSuggestVisible));
        Opaque(suggestMask);
        // 遮罩背景吃掉点击（防止透传到下层结果）；再点一次收起键盘
        var maskTap = new TapGestureRecognizer();
        maskTap.Tapped += (_, _) => { try { _searchEntry.Unfocus(); } catch { } };
        suggestMask.GestureRecognizers.Add(maskTap);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // 模式 chips
                new() { Height = GridLength.Star }, // 结果
            },
            Children = { modesScroll, songsHost, playlistsHost, artistsHost, loading, suggestMask },
        };
        Grid.SetRow(modesScroll, 0);
        Grid.SetRow(songsHost, 1);
        Grid.SetRow(playlistsHost, 1);
        Grid.SetRow(artistsHost, 1);
        Grid.SetRow(loading, 1);
        Grid.SetRow(suggestMask, 1); // 遮罩最后加入 → 顶层渲染

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // chips
                new() { Height = GridLength.Star }, // content
            },
            Children = { header, content },
        };
        Grid.SetRow(content, 2);
    }

    /// <summary>给容器铺不透明窗口背景（内容区视觉折叠：完全盖住下层元素）</summary>
    private static void Opaque(VisualElement view)
    {
        view.SetDynamicResource(VisualElement.BackgroundColorProperty, "WindowBackgroundColor");
    }

    /// <summary>热词/联想 chip 模板（点击回填并搜索）</summary>
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
                Margin = new Thickness(8, 4),
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

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // 进入即聚焦并预热热词（全屏遮罩浮出，覆盖可能残留的下层结果）
        try
        {
            _searchEntry.Focus();
            if (_vm.SuggestItems.Count == 0) await _vm.OnSearchTextChangedAsync("");
        }
        catch { }
    }

    /// <summary>返回：还原主页 tab 上下文后再出栈</summary>
    private async Task CloseAndPopAsync()
    {
        try { await _vm.CloseSearchAsync(); } catch { }
        try { await NeteaseNav.PopAsync(this, _services); } catch { }
    }

    /// <summary>安卓返回键/手势：与 ‹ 返回同路（先还原主页上下文再出栈）</summary>
    protected override bool OnBackButtonPressed()
    {
        try
        {
            if (Navigation.NavigationStack.Count > 0 && Navigation.NavigationStack.LastOrDefault() == this)
            {
                _ = CloseAndPopAsync();
                return true;
            }
        }
        catch { }
        return base.OnBackButtonPressed();
    }

    private async void OnSongSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (_vm.ConsumeSuppressSelection()) return;
        if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
        await _vm.PlaySongAsync(song);
    }

    private async void OnArtistSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not NeteaseArtist artist) return;
        try { await NeteaseNav.PushAsync(new NeteaseArtistPage(artist, _vm.Plugin, _services)); }
        catch { }
    }
}
