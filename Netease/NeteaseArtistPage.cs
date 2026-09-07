using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 歌手页（仿官方详情页）：头部（圆头像 + 名字 + 别名 + 统计 + 播放全部）
/// + 一级子 tab（歌曲 / 专辑 / MV / 歌手详情 / 相似歌手），tab 内容懒加载。
/// 歌曲 = 热门 50 首（可播放）；专辑 = 网格（点击进专辑页）；MV = 网格（点击浏览器打开）；
/// 歌手详情 = 简介 + 分节长文；相似歌手 = 圆头像网格（点击跳对应歌手页）。
/// </summary>
public class NeteaseArtistPage : ContentPage
{
    private readonly NeteaseArtist _artist;
    private readonly NetEaseMusicPlugin _plugin;
    private readonly IServiceProvider _services;

    // ── 数据 ──
    private readonly ObservableCollection<OnlineSong> _songs = new();
    private readonly ObservableCollection<NeteaseAlbum> _albums = new();
    private readonly ObservableCollection<NeteaseMv> _mvs = new();
    private ObservableCollection<NeteaseArtist> _similar = new();

    /// <summary>分块行（专辑/MV/相似歌手三个网格共用：一行 N 张定宽卡）</summary>
    private sealed class ChunkRow
    {
        public ObservableCollection<object> Items { get; } = new();
    }

    private readonly ObservableCollection<ChunkRow> _albumRows = new();
    private readonly ObservableCollection<ChunkRow> _mvRows = new();
    private readonly ObservableCollection<ChunkRow> _similarRows = new();
    private int _albumSpan = 2, _mvSpan = 2, _similarSpan = 3;

    private ArtistIntro? _intro;
    private bool _playing;

    // ── 状态 ──
    private int _tabIndex;
    private bool _mvsLoaded, _introLoaded, _similarLoaded;

    private readonly ActivityIndicator _loading;
    private readonly HorizontalStackLayout _tabsBar = new() { Spacing = 18, Padding = new Thickness(16, 0, 16, 0) };
    private readonly List<(Label Label, BoxView Underline)> _tabViews = new();
    private readonly List<string> _tabTitles = new() { "歌曲", "专辑", "MV", "歌手详情", "相似歌手" };

    private readonly CollectionView _songsView;
    private readonly CollectionView _albumsView;
    private readonly CollectionView _mvsView;
    private readonly ScrollView _bioScroll;
    private readonly VerticalStackLayout _bioStack = new() { Padding = new Thickness(16, 4, 16, 24) };
    private readonly CollectionView _similarView;
    private readonly Label _aliasLabel = new() { FontSize = 11.5f, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 3, 0, 0) };

    private const double AlbumCardWidth = 168;
    private const double MvCardWidth = 172;
    private const double SimilarCardWidth = 120;

    public NeteaseArtistPage(NeteaseArtist artist, NetEaseMusicPlugin plugin, IServiceProvider services)
    {
        _artist = artist;
        _plugin = plugin;
        _services = services;

        Title = artist.Name;
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 头部 ──
        var backButton = new Border
        {
            Padding = new Thickness(12, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#66000000"),
            WidthRequest = 36,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Start,
            Content = new Label { Text = "←", FontSize = 22, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, -3, 0, 0) },
        };
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) =>
        {
            try { await NeteaseNav.PopAsync(this, _services); } catch { }
        };
        backButton.GestureRecognizers.Add(backTap);

        var avatarBorder = new Border
        {
            WidthRequest = 96,
            HeightRequest = 96,
            StrokeShape = new RoundRectangle { CornerRadius = 48 },
            StrokeThickness = 0,
            VerticalOptions = LayoutOptions.Start,
        };
        avatarBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var avatarImage = new Image { Aspect = Aspect.AspectFill, InputTransparent = true };
        avatarImage.SetBinding(Image.SourceProperty, new Binding(nameof(NeteaseArtist.PicUrl),
            converter: NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance, converterParameter: 300)
        { Source = artist, TargetNullValue = "ic_music_note" });
        avatarBorder.Content = avatarImage;

        var nameLabel = new Label
        {
            Text = artist.Name,
            FontSize = 20,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        _aliasLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");

        var statLabel = new Label { Text = $"单曲 {artist.SongCount} · 专辑 {artist.AlbumCount}", FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        statLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        var playAllButton = new Border
        {
            Padding = new Thickness(14, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            HorizontalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 10, 0, 0),
            Content = new HorizontalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Label { Text = "▶", FontSize = 11, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                    new Label { Text = "播放全部", FontSize = 12.5f, FontFamily = "OpenSansSemibold", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                },
            },
        };
        playAllButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        var playAllTap = new TapGestureRecognizer();
        playAllTap.Tapped += async (_, _) => await PlayAllAsync();
        playAllButton.GestureRecognizers.Add(playAllTap);

        var infoStack = new VerticalStackLayout { Children = { nameLabel, _aliasLabel, statLabel, playAllButton } };

        var heroGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto }, // back
                new() { Width = GridLength.Auto }, // avatar
                new() { Width = GridLength.Star }, // info
            },
            ColumnSpacing = 14,
        };
        Grid.SetColumn(avatarBorder, 1);
        Grid.SetColumn(infoStack, 2);
        heroGrid.Children.Add(backButton);
        heroGrid.Children.Add(avatarBorder);
        heroGrid.Children.Add(infoStack);

        var headerHost = new Grid { Padding = new Thickness(16, 12, 16, 10), Children = { heroGrid } };

        // ── 子 tab 栏 ──
        BuildTabs();
        RefreshTabStyles();

        // ── 歌曲（热门 50，可播放）──
        _songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _songs,
            ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate()),
        };
        _songsView.SelectionChanged += async (_, e) =>
        {
            _songsView.SelectedItem = null;
            if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
            await PlayFromAsync(song);
        };

        // ── 专辑网格（点进专辑页）──
        _albumsView = CreateChunkedView(_albumRows, () => NeteaseUiKit.CreateAlbumGridCard(AlbumCardWidth, alb => _ = OpenAlbum(alb)), AlbumCardWidth, 2);

        // ── MV 网格（点击浏览器打开）──
        _mvsView = CreateChunkedView(_mvRows, () => NeteaseUiKit.CreateArtistMvCard(MvCardWidth, mv => _ = OpenMv(mv)), MvCardWidth, 2);

        // ── 歌手详情 ──
        _bioScroll = new ScrollView { Content = _bioStack };

        // ── 相似歌手网格（点击跳对应歌手页）──
        _similarView = CreateChunkedView(_similarRows, () => NeteaseUiKit.CreateArtistAvatarCard(SimilarCardWidth, a => _ = OpenSimilarArtist(a)), SimilarCardWidth, 3);

        _loading = new ActivityIndicator { WidthRequest = 32, HeightRequest = 32, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 24, 0, 0), IsVisible = false };
        _loading.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // tabs
                new() { Height = GridLength.Star }, // content
            },
            Children = { headerHost, _tabsBar, _songsView, _albumsView, _mvsView, _bioScroll, _similarView, _loading },
        };
        Grid.SetRow(_tabsBar, 1);
        Grid.SetRow(_songsView, 2);
        Grid.SetRow(_albumsView, 2);
        Grid.SetRow(_mvsView, 2);
        Grid.SetRow(_bioScroll, 2);
        Grid.SetRow(_similarView, 2);
        Grid.SetRow(_loading, 2);

        ApplyTabVisibility();
        _ = LoadAsync();
    }

    private void BuildTabs()
    {
        for (var i = 0; i < _tabTitles.Count; i++)
        {
            var idx = i;
            var label = new Label { Text = _tabTitles[i], FontSize = 13.5f, FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.Center };
            label.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
            var underline = new BoxView { HeightRequest = 3, CornerRadius = 1.5f, WidthRequest = 20, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
            underline.SetDynamicResource(BoxView.ColorProperty, "PrimaryColor");
            var host = new VerticalStackLayout { Children = { label, underline }, HorizontalOptions = LayoutOptions.Center };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (_tabIndex == idx) return;
                _tabIndex = idx;
                RefreshTabStyles();
                ApplyTabVisibility();
                EnsureTabLoaded(idx);
            };
            host.GestureRecognizers.Add(tap);
            _tabViews.Add((label, underline));
            _tabsBar.Children.Add(host);
        }
    }

    private void RefreshTabStyles()
    {
        for (var i = 0; i < _tabViews.Count; i++)
        {
            var (label, underline) = _tabViews[i];
            var on = i == _tabIndex;
            label.TextColor = on
                ? (Application.Current?.Resources.TryGetValue("TextPrimaryColor", out var pc) == true ? (Color)pc : Colors.White)
                : (Application.Current?.Resources.TryGetValue("TextSecondaryColor", out var sc) == true ? (Color)sc : Colors.Gray);
            underline.IsVisible = on;
        }
    }

    private void ApplyTabVisibility()
    {
        _songsView.IsVisible = _tabIndex == 0;
        _albumsView.IsVisible = _tabIndex == 1;
        _mvsView.IsVisible = _tabIndex == 2;
        _bioScroll.IsVisible = _tabIndex == 3;
        _similarView.IsVisible = _tabIndex == 4;
    }

    private void EnsureTabLoaded(int idx)
    {
        switch (idx)
        {
            case 2 when !_mvsLoaded: _ = LoadMvsAsync(); break;
            case 3 when !_introLoaded: _ = LoadIntroAsync(); break;
            case 4 when !_similarLoaded: _ = LoadSimilarAsync(); break;
        }
    }

    /// <summary>
    /// 分块网格：外层 CollectionView 虚拟化「行」，行内横排定宽卡。
    /// 列数挂自身 SizeChanged 推导（contentGrid 联动在部分机型不可靠）；minSpan 保证手机不塌成过少列。
    /// </summary>
    private CollectionView CreateChunkedView(ObservableCollection<ChunkRow> rows, Func<View> cardFactory, double cardWidth, int minSpan)
    {
        var view = new CollectionView
        {
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical),
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            SelectionMode = SelectionMode.None,
            Margin = new Thickness(0, 4, 0, 0),
        };
        view.ItemsSource = rows;
        view.ItemTemplate = new DataTemplate(() =>
        {
            var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(16, 0, 16, 14) };
            row.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(ChunkRow.Items)));
            BindableLayout.SetItemTemplate(row, new DataTemplate(() => cardFactory()));
            return row;
        });
        view.SizeChanged += (_, _) =>
        {
            var avail = view.Width - 44;
            if (avail <= 0) return;
            var span = Math.Clamp((int)Math.Floor((avail + 10) / (cardWidth + 10)), minSpan, 6);
            var cur = rows == _albumRows ? _albumSpan : rows == _mvRows ? _mvSpan : _similarSpan;
            if (span == cur) return;
            SetSpan(rows, span);
            Rechunk(rows);
        };
        return view;
    }

    private void SetSpan(ObservableCollection<ChunkRow> rows, int span)
    {
        if (rows == _albumRows) _albumSpan = span;
        else if (rows == _mvRows) _mvSpan = span;
        else _similarSpan = span;
    }

    private int SpanOf(ObservableCollection<ChunkRow> rows)
        => rows == _albumRows ? _albumSpan : rows == _mvRows ? _mvSpan : _similarSpan;

    /// <summary>按当前列数整体重排（列数变化或数据变化后调用）</summary>
    private void Rechunk(ObservableCollection<ChunkRow> rows)
    {
        var span = SpanOf(rows);
        IEnumerable<object> items = rows == _albumRows ? _albums.Cast<object>()
            : rows == _mvRows ? _mvs.Cast<object>()
            : _similar.Cast<object>();
        var snapshot = items.ToList();
        rows.Clear();
        ChunkRow? row = null;
        foreach (var item in snapshot)
        {
            if (row == null || row.Items.Count >= span)
            {
                row = new ChunkRow();
                rows.Add(row);
            }
            row.Items.Add(item);
        }
    }

    // ── 数据加载 ──

    private async Task LoadAsync()
    {
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        try
        {
            var songsTask = _plugin.GetArtistTopSongsAsync(_artist.Id);
            var albumsTask = _plugin.GetArtistAlbumsAsync(_artist.Id);
            var aliasTask = _plugin.ApiClient.GetArtistAliasesAsync(_artist.Id);
            await Task.WhenAll(songsTask, albumsTask, aliasTask);

            foreach (var s in await songsTask ?? new List<OnlineSong>()) _songs.Add(s);
            foreach (var a in await albumsTask ?? new List<NeteaseAlbum>()) _albums.Add(a);
            Rechunk(_albumRows);

            var aliases = await aliasTask;
            if (aliases is { Count: > 0 })
                _aliasLabel.Text = string.Join(" / ", aliases);
            else
                _aliasLabel.IsVisible = false;
        }
        catch { }
        finally
        {
            _loading.IsVisible = false;
            _loading.IsRunning = false;
        }
    }

    private async Task LoadMvsAsync()
    {
        _mvsLoaded = true;
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        try
        {
            foreach (var m in await _plugin.ApiClient.GetArtistMvsAsync(_artist.Id, 40, 0)) _mvs.Add(m);
            Rechunk(_mvRows);
        }
        catch { }
        finally
        {
            _loading.IsVisible = false;
            _loading.IsRunning = false;
        }
    }

    private async Task LoadIntroAsync()
    {
        _introLoaded = true;
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        try
        {
            _intro = await _plugin.ApiClient.GetArtistIntroAsync(_artist.Id);
            _bioStack.Children.Clear();
            if (_intro == null)
            {
                var empty = new Label { Text = "暂无歌手详情", FontSize = 12.5f, Margin = new Thickness(0, 16, 0, 0) };
                empty.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
                _bioStack.Children.Add(empty);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_intro.BriefDesc))
            {
                _bioStack.Children.Add(MakeSectionTitle("简介"));
                foreach (var p in SplitParagraphs(_intro.BriefDesc))
                    _bioStack.Children.Add(MakeParagraph(p));
            }
            foreach (var sec in _intro.Sections)
            {
                _bioStack.Children.Add(MakeSectionTitle(sec.Title));
                foreach (var p in SplitParagraphs(sec.Text))
                    _bioStack.Children.Add(MakeParagraph(p));
            }
        }
        catch { }
        finally
        {
            _loading.IsVisible = false;
            _loading.IsRunning = false;
        }
    }

    private async Task LoadSimilarAsync()
    {
        _similarLoaded = true;
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        try
        {
            _similar = new ObservableCollection<NeteaseArtist>(await _plugin.ApiClient.GetSimilarArtistsAsync(_artist.Id, 30));
            Rechunk(_similarRows);
        }
        catch { }
        finally
        {
            _loading.IsVisible = false;
            _loading.IsRunning = false;
        }
    }

    private static View MakeSectionTitle(string title)
    {
        var l = new Label { Text = title, FontSize = 13.5f, FontFamily = "OpenSansSemibold", Margin = new Thickness(0, 12, 0, 6) };
        l.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        return l;
    }

    private static View MakeParagraph(string text)
    {
        var l = new Label { Text = text, FontSize = 12, LineHeight = 19, Margin = new Thickness(0, 0, 0, 8) };
        l.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        return l;
    }

    private static IEnumerable<string> SplitParagraphs(string text)
        => text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── 行为 ──

    private async Task PlayAllAsync()
    {
        if (_playing || _songs.Count == 0) return;
        _playing = true;
        var played = await NeteasePlaybackHelper.PlayListAsync(_services, _plugin, _songs.ToList(), _songs[0]);
        _playing = false;
        if (played == 0) await ShowTipAsync("暂时取不到播放链接");
    }

    private async Task PlayFromAsync(OnlineSong song)
    {
        if (_playing) return;
        _playing = true;
        var played = await NeteasePlaybackHelper.PlayListAsync(_services, _plugin, _songs.ToList(), song);
        _playing = false;
        if (played == 0) await ShowTipAsync("暂时取不到播放链接");
    }

    private async Task OpenAlbum(NeteaseAlbum album)
    {
        try { await NeteaseNav.PushAsync(new NeteaseAlbumPage(album, _plugin, _services)); } catch { }
    }

    private async Task OpenMv(NeteaseMv mv)
    {
        try { await Launcher.OpenAsync($"https://music.163.com/#/mv?id={mv.Id}"); }
        catch { }
    }

    private async Task OpenSimilarArtist(NeteaseArtist artist)
    {
        if (string.IsNullOrWhiteSpace(artist.Id) || artist.Id == _artist.Id) return;
        try { await NeteaseNav.PushAsync(new NeteaseArtistPage(artist, _plugin, _services)); } catch { }
    }

    private async Task ShowTipAsync(string message)
    {
        try
        {
            var dialog = _services.GetService<IDialogService>();
            if (dialog != null)
                await dialog.ShowAlertAsync("提示", message, "确定");
            else
                await DisplayAlertAsync("提示", message, "确定");
        }
        catch { }
    }
}
