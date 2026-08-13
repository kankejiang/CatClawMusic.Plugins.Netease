using System.Collections.ObjectModel;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 歌手页：歌手头像/名称 + 热门歌曲列表（可播放）+ 专辑横向滚动（点击进专辑页）。
/// C# 代码构建 UI，复用 <see cref="NeteaseUiKit"/> 模板，DynamicResource 宿主主题。
/// </summary>
public class NeteaseArtistPage : ContentPage
{
    private readonly NeteaseArtist _artist;
    private readonly NetEaseMusicPlugin _plugin;
    private readonly IServiceProvider _services;

    private readonly ObservableCollection<OnlineSong> _songs = new();
    private readonly ObservableCollection<NeteaseAlbum> _albums = new();
    private readonly ActivityIndicator _loading;
    private bool _playing;

    public NeteaseArtistPage(NeteaseArtist artist, NetEaseMusicPlugin plugin, IServiceProvider services)
    {
        _artist = artist;
        _plugin = plugin;
        _services = services;

        Title = artist.Name;
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 头部：返回 + 歌手信息 ──
        // 用 Unicode 箭头 "←" + 固定半透明深色背景，跨主题可见（之前 "‹" + SurfaceColor 在深色头区与页面背景融合，按钮不可见。
        // 不用 ImageSourceHelper 是因为它在宿主 Maui 项目，插件不能引用避免循环依赖。）
        var backButton = new Border
        {
            Padding = new Thickness(12, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#66000000"),
            WidthRequest = 36,
            HeightRequest = 36,
            Content = new Label { Text = "←", FontSize = 22, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, -3, 0, 0) },
        };
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) =>
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
        backButton.GestureRecognizers.Add(backTap);

        var avatarBorder = new Border
        {
            WidthRequest = 64,
            HeightRequest = 64,
            StrokeShape = new RoundRectangle { CornerRadius = 32 },
            StrokeThickness = 0,
            VerticalOptions = LayoutOptions.Center,
        };
        avatarBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var avatarImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 64, HeightRequest = 64, Source = artist.PicUrl };
        avatarBorder.Content = avatarImage;

        var nameLabel = new Label
        {
            Text = artist.Name,
            FontSize = 18,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var infoLabel = new Label
        {
            Text = $"{artist.SongCount} 首歌曲 · {artist.AlbumCount} 张专辑",
            FontSize = 12,
        };
        infoLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");

        var playAllButton = new Border
        {
            Padding = new Thickness(12, 7),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Label { Text = "▶", FontSize = 11, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                    new Label { Text = "热门 50 首", FontSize = 12, FontFamily = "OpenSansSemibold", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                },
            },
        };
        playAllButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        var playAllTap = new TapGestureRecognizer();
        playAllTap.Tapped += async (_, _) =>
        {
            if (_playing || _songs.Count == 0) return;
            _playing = true;
            var played = await NeteasePlaybackHelper.PlayListAsync(_services, _plugin, _songs.ToList(), _songs[0]);
            _playing = false;
            if (played == 0) await ShowTipAsync("暂时取不到播放链接");
        };
        playAllButton.GestureRecognizers.Add(playAllTap);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
                new() { Width = GridLength.Auto },
            },
            ColumnSpacing = 12,
            Padding = new Thickness(16, 12, 16, 8),
            Children = { backButton, avatarBorder, new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center, Children = { nameLabel, infoLabel } }.TapGridSetColumn(2), playAllButton },
        };
        Grid.SetColumn(playAllButton, 3);

        // ── 热门歌曲 ──
        var songsTitle = new Label { Text = "热门歌曲", FontSize = 15, FontFamily = "OpenSansSemibold", Margin = new Thickness(16, 8, 16, 4) };
        songsTitle.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _songs,
            ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate()),
        };
        songsView.SelectionChanged += async (_, e) =>
        {
            songsView.SelectedItem = null;
            if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song || _playing) return;
            _playing = true;
            var played = await NeteasePlaybackHelper.PlayListAsync(_services, _plugin, _songs.ToList(), song);
            _playing = false;
            if (played == 0) await ShowTipAsync("暂时取不到播放链接");
        };

        // ── 专辑横向滚动 ──
        var albumsTitle = new Label { Text = "专辑", FontSize = 15, FontFamily = "OpenSansSemibold", Margin = new Thickness(16, 12, 16, 4) };
        albumsTitle.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var albumsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _albums,
            HeightRequest = 190,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal) { ItemSpacing = 10 },
            ItemTemplate = new DataTemplate(NeteaseUiKit.CreateAlbumCardTemplate),
            Margin = new Thickness(16, 0, 16, 0),
        };
        albumsView.SelectionChanged += async (_, e) =>
        {
            albumsView.SelectedItem = null;
            if (e.CurrentSelection.FirstOrDefault() is not NeteaseAlbum album) return;
            try { await Shell.Current.Navigation.PushAsync(new NeteaseAlbumPage(album, _plugin, _services)); } catch { }
        };

        _loading = new ActivityIndicator { WidthRequest = 32, HeightRequest = 32, IsRunning = true, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 24, 0, 0) };
        _loading.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");

        var emptyLabel = new Label { Text = "暂无数据", FontSize = 13, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 24, 0, 0), IsVisible = false };
        emptyLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Auto }, // 热门歌曲标题
                new() { Height = GridLength.Star }, // 歌曲列表
                new() { Height = GridLength.Auto }, // 专辑标题
                new() { Height = GridLength.Auto }, // 专辑横条
            },
            Children = { headerGrid, songsTitle, songsView, albumsTitle, albumsView, _loading, emptyLabel },
        };
        Grid.SetRow(songsTitle, 1);
        Grid.SetRow(songsView, 2);
        Grid.SetRow(albumsTitle, 3);
        Grid.SetRow(albumsView, 4);
        Grid.SetRow(_loading, 2);
        Grid.SetRow(emptyLabel, 2);

        _ = LoadAsync(emptyLabel);
    }

    private async Task LoadAsync(Label emptyLabel)
    {
        try
        {
            var songsTask = _plugin.GetArtistTopSongsAsync(_artist.Id);
            var albumsTask = _plugin.GetArtistAlbumsAsync(_artist.Id);
            await Task.WhenAll(songsTask, albumsTask);

            var songs = await songsTask;
            var albums = await albumsTask;
            foreach (var s in songs ?? new List<OnlineSong>()) _songs.Add(s);
            foreach (var a in albums ?? new List<NeteaseAlbum>()) _albums.Add(a);

            if (_songs.Count == 0 && _albums.Count == 0) emptyLabel.IsVisible = true;
        }
        catch { }
        finally
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
        }
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
