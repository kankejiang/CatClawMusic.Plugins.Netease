using System.Collections.ObjectModel;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 相似/相关歌单页：展示某歌单的相关歌单（collections 网格），点击卡片回调主列表打开该歌单。
/// </summary>
public class NeteaseSimilarPlaylistsPage : ContentPage
{
    private readonly NetEaseMusicPlugin _plugin;
    private readonly string _playlistId;
    private readonly Func<OnlinePlaylist, Task> _onOpenPlaylist;

    private readonly ObservableCollection<SimilarPlaylistInfo> _items = new();
    private readonly ActivityIndicator _loading;
    private readonly CollectionView _gridView;

    public NeteaseSimilarPlaylistsPage(string playlistId, NetEaseMusicPlugin plugin, Func<OnlinePlaylist, Task> onOpenPlaylist)
    {
        _playlistId = playlistId;
        _plugin = plugin;
        _onOpenPlaylist = onOpenPlaylist;

        Title = "相似歌单";
        BackgroundColor = Application.Current?.Resources.TryGetValue("WindowBackgroundColor", out var bg) == true
            ? (Color)bg
            : Color.FromArgb("#0B0D20");

        var titleLabel = new Label
        {
            Text = "相似歌单",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        _loading = new ActivityIndicator
        {
            IsRunning = true,
            IsVisible = true,
            Scale = 1.1,
        };
        _loading.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");

        _gridView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _items,
            ItemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical) { HorizontalItemSpacing = 12, VerticalItemSpacing = 14 },
            ItemTemplate = new DataTemplate(NeteaseUiKit.CreateSimilarPlaylistItemTemplate),
            Margin = new Thickness(16, 8),
        };
        _gridView.SelectionChanged += OnGridSelectionChanged;

        var statusLabel = new Label
        {
            Text = "暂无相关歌单",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        statusLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");

        var statusOverlay = new Grid
        {
            IsVisible = false,
            Children = { statusLabel },
        };
        // 用状态文字可见性驱动空态
        _gridView.IsVisible = true;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
            },
            ColumnSpacing = 8,
            Padding = new Thickness(10, 10, 10, 6),
            Children = { CreateBackButton(), titleLabel },
        };
        Grid.SetColumn(titleLabel, 1);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new() { Height = GridLength.Auto },
                new() { Height = GridLength.Star },
            },
            Children = { headerGrid },
        };
        Grid.SetRow(_gridView, 1);
        root.Children.Add(_gridView);
        Grid.SetRow(_loading, 1);
        Grid.SetRowSpan(_loading, 1);
        _loading.VerticalOptions = LayoutOptions.Center;
        _loading.HorizontalOptions = LayoutOptions.Center;
        root.Children.Add(_loading);

        Content = root;
    }

    private View CreateBackButton()
    {
        var backLabel = new Label
        {
            Text = "‹",
            FontSize = 30,
            FontFamily = "OpenSansSemibold",
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            WidthRequest = 40,
            HeightRequest = 40,
        };
        backLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Shell.Current.Navigation.PopAsync();
        backLabel.GestureRecognizers.Add(tap);
        return backLabel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_items.Count > 0) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loading.IsVisible = true;
            _loading.IsRunning = true;
            var list = await _plugin.GetSimilarPlaylistsAsync(_playlistId, 10);
            _items.Clear();
            foreach (var p in list) _items.Add(p);
        }
        catch
        {
        }
        finally
        {
            _loading.IsVisible = false;
            _loading.IsRunning = false;
        }
    }

    private async void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CollectionView cv) cv.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not SimilarPlaylistInfo info) return;
        var playlist = new OnlinePlaylist
        {
            Id = info.Id,
            Platform = "netease",
            Name = info.Name,
            CoverUrl = info.CoverUrl,
            SongCount = info.SongCount,
        };
        try { await _onOpenPlaylist(playlist); }
        catch { }
    }
}