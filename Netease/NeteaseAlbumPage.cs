using System.Collections.ObjectModel;
using CatClawMusic.Core.Models;
using CatClawMusic.Maui.Helpers;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 专辑页：专辑封面/名称/歌手/年份 + 专辑内歌曲列表（可整播/单曲播放）。
/// C# 代码构建 UI，复用 <see cref="NeteaseUiKit"/> 模板，DynamicResource 宿主主题。
/// </summary>
public class NeteaseAlbumPage : ContentPage
{
    private readonly NeteaseAlbum _album;
    private readonly NetEaseMusicPlugin _plugin;
    private readonly IServiceProvider _services;

    private readonly ObservableCollection<OnlineSong> _songs = new();
    private readonly ActivityIndicator _loading;
    private bool _playing;

    public NeteaseAlbumPage(NeteaseAlbum album, NetEaseMusicPlugin plugin, IServiceProvider services)
    {
        _album = album;
        _plugin = plugin;
        _services = services;

        Title = album.Name;
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        // ── 头部：返回 + 专辑信息 + 播放全部 ──
        // 用宿主 ic_arrow_back.svg 图标 + 半透明深色背景，跨主题可读（之前 "‹" 字符 + SurfaceColor 在深色头区与页面背景融合，按钮不可见）
        var backButton = new ImageButton
        {
            WidthRequest = 36,
            HeightRequest = 36,
            Padding = new Thickness(8),
            CornerRadius = 18,
            BackgroundColor = Color.FromArgb("#66000000"),
            Aspect = AspectFit,
            Source = ImageSourceHelper.FromNameOriginal("ic_arrow_back"),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
        };
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) => { try { await Shell.Current.Navigation.PopAsync(); } catch { } };
        backButton.GestureRecognizers.Add(backTap);

        var coverBorder = new Border
        {
            WidthRequest = 64,
            HeightRequest = 64,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 0,
            VerticalOptions = LayoutOptions.Center,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 64, HeightRequest = 64, Source = album.PicUrl };
        coverBorder.Content = coverImage;

        var nameLabel = new Label
        {
            Text = album.Name,
            FontSize = 16,
            FontFamily = "OpenSansSemibold",
            MaxLines = 1,
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        var subParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(album.ArtistName)) subParts.Add(album.ArtistName);
        if (!string.IsNullOrWhiteSpace(album.PublishYear)) subParts.Add(album.PublishYear);
        subParts.Add($"{album.SongCount} 首");
        var infoLabel = new Label { Text = string.Join(" · ", subParts), FontSize = 12, MaxLines = 1 };
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
                    new Label { Text = "播放全部", FontSize = 12, FontFamily = "OpenSansSemibold", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                },
            },
        };
        playAllButton.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryColor");
        var playAllTap = new TapGestureRecognizer();
        playAllTap.Tapped += async (_, _) => await PlayFromAsync(_songs.FirstOrDefault());
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
            Children =
            {
                backButton,
                coverBorder.TapGridSetColumn(1),
                new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center, Children = { nameLabel, infoLabel } }.TapGridSetColumn(2),
                playAllButton.TapGridSetColumn(3),
            },
        };

        // ── 歌曲列表 ──
        var songsView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _songs,
            ItemTemplate = new DataTemplate(() => NeteaseUiKit.CreateSongItemTemplate()),
        };
        songsView.SelectionChanged += async (_, e) =>
        {
            songsView.SelectedItem = null;
            if (e.CurrentSelection.FirstOrDefault() is not OnlineSong song) return;
            await PlayFromAsync(song);
        };

        _loading = new ActivityIndicator { WidthRequest = 32, HeightRequest = 32, IsRunning = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _loading.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");

        var emptyLabel = new Label { Text = "专辑为空", FontSize = 13, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, IsVisible = false };
        emptyLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        Content = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto }, // header
                new() { Height = GridLength.Star }, // 歌曲列表
            },
            Children = { headerGrid, songsView, _loading, emptyLabel },
        };
        Grid.SetRow(songsView, 1);
        Grid.SetRow(_loading, 1);
        Grid.SetRow(emptyLabel, 1);

        _ = LoadAsync(emptyLabel);
    }

    private async Task LoadAsync(Label emptyLabel)
    {
        try
        {
            var songs = await _plugin.GetAlbumSongsAsync(_album.Id);
            foreach (var s in songs ?? new List<OnlineSong>()) _songs.Add(s);
            if (_songs.Count == 0) emptyLabel.IsVisible = true;
        }
        catch { }
        finally
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
        }
    }

    private async Task PlayFromAsync(OnlineSong? start)
    {
        if (start == null || _playing || _songs.Count == 0) return;
        _playing = true;
        var played = await NeteasePlaybackHelper.PlayListAsync(_services, _plugin, _songs.ToList(), start);
        _playing = false;
        if (played == 0)
        {
            try { await DisplayAlertAsync("提示", "暂时取不到播放链接", "确定"); } catch { }
        }
    }
}
