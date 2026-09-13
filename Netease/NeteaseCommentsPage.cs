using System.Collections.ObjectModel;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 歌曲评论区（模态浮层）：顶部标题/关闭 + 「热门 / 最新」分段 + 评论列表（分页加载更多）。
/// 全部 C# 代码构建，DynamicResource 宿主主题；评论行模板内联于此页。
/// </summary>
public class NeteaseCommentsPage : ContentPage
{
    private readonly NetEaseMusicPlugin _plugin;
    private readonly ObservableCollection<SongComment> _comments = new();
    private readonly Label _countLabel = new() { FontSize = 12 };
    private readonly Label _hotSeg = new(), _newSeg = new();
    private readonly Button _loadMore;
    private readonly ActivityIndicator _loading = new() { IsRunning = true, IsVisible = true };
    private readonly Label _empty = new() { Text = "暂无评论", HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 40, 0, 0) };
    private int _offset;
    private bool _hot = true;
    private bool _busy;
    private readonly bool _isPlaylist;

    /// <param name="isPlaylist">true = 歌单评论（A_PL_0_ 资源族）；false = 歌曲评论（R_SO_4_）</param>
    public NeteaseCommentsPage(OnlineSong song, NetEaseMusicPlugin plugin, bool isPlaylist = false)
    {
        _plugin = plugin;
        _isPlaylist = isPlaylist;
        var songId = song.Id;

        // 统一不透明深色底（与歌单详情页一致）：宿主 WindowBackgroundColor 可能为全透明
        BackgroundColor = NeteaseUiKit.PageBackground;

        // ── 顶栏：返回 + 歌曲名 ──
        var back = new Label { Text = "‹", FontSize = 30, TextColor = Color.FromArgb("#888888"),
            Padding = new Thickness(12, 0, 12, 0), VerticalOptions = LayoutOptions.Center };
        var backTap = new TapGestureRecognizer();
        backTap.Tapped += async (_, _) => { try { await NeteaseNav.PopAsync(this); } catch { } };
        back.GestureRecognizers.Add(backTap);

        var title = new Label { Text = "评论 · " + song.Title, FontSize = 17, FontFamily = "OpenSansSemibold", MaxLines = 1,
            HorizontalOptions = LayoutOptions.StartAndExpand, VerticalOptions = LayoutOptions.Center };
        title.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");

        _countLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        _countLabel.VerticalOptions = LayoutOptions.Center;

        // ── 分段：热门 / 最新 ──
        // 注意：网易云对**歌单没有"热门评论"**（实测 /hotcomments/A_PL_0_xxx 返回 total=0 且列表为空），
        // 若默认停在热门页签，明明有几十万条评论也会显示"暂无评论"（用户反馈）。歌单模式默认「最新评论」。
        _hot = !isPlaylist;
        void StyleSeg(Label l, bool on) { l.FontSize = 14; l.FontFamily = "OpenSansSemibold";
            l.TextColor = on ? Color.FromArgb("#7c6cf0") : Color.FromArgb("#888888");
            l.Padding = new Thickness(14, 6); l.GestureRecognizers.Clear(); }
        StyleSeg(_hotSeg, _hot); _hotSeg.Text = "热门评论";
        StyleSeg(_newSeg, !_hot); _newSeg.Text = "最新评论";
        _hotSeg.GestureRecognizers.Add(MakeSegTap(hot: true));
        _newSeg.GestureRecognizers.Add(MakeSegTap(hot: false));
        var segRow = new HorizontalStackLayout { Children = { _hotSeg, _newSeg }, Margin = new Thickness(4, 4, 0, 4) };

        // ── 评论列表 ──
        var list = new CollectionView
        {
            ItemsSource = _comments,
            ItemTemplate = new DataTemplate(CreateCommentRow),
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
        };

        // ── 加载更多 ──
        _loadMore = new Button { Text = "加载更多…", BackgroundColor = Color.FromArgb("#22111122"), TextColor = Colors.White,
            HeightRequest = 40, Margin = new Thickness(12, 6) };
        _loadMore.Clicked += async (_, _) => await LoadNextAsync();

        _empty.IsVisible = false;

        var topBar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { back, title, _countLabel },
            Padding = new Thickness(8, 8, 16, 8),
        };
        // 必须显式分列：三个子元素默认都落在第 0 列，后加入的 title 会盖住 ‹ 返回按钮
        // （用户反馈"没有返回按钮"——按钮其实在，只是被标题压住了）
        Grid.SetColumn(title, 1);
        Grid.SetColumn(_countLabel, 2);

        Content = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star }, new RowDefinition { Height = GridLength.Auto } },
            Children =
            {
                topBar,
                segRow,
                list,
                _loadMore,
                _loading,
                _empty,
            }
        };
        Grid.SetRow(segRow, 1);
        Grid.SetRow(list, 2);
        Grid.SetRow(_loadMore, 3);
        Grid.SetRow(_loading, 2);
        Grid.SetRow(_empty, 2);

        _ = LoadAsync(songId);
    }

    private TapGestureRecognizer MakeSegTap(bool hot)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (hot == _hot || _busy) return;
            _hot = hot;
            _hotSeg.TextColor = hot ? Color.FromArgb("#7c6cf0") : Color.FromArgb("#888888");
            _newSeg.TextColor = hot ? Color.FromArgb("#888888") : Color.FromArgb("#7c6cf0");
            await LoadAsync(songId: CurrentSongId);
        };
        return tap;
    }

    private string CurrentSongId => _currentSongId ?? "";
    private string? _currentSongId;

    private View CreateCommentRow()
    {
        var avatar = BuildAvatar();
        var body = BuildCommentBody();
        var row = new Grid
        {
            Padding = new Thickness(16, 10),
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star } },
            ColumnSpacing = 12,
            Children = { avatar, body },
        };
        // 必须显式分列：两个子元素默认都在第 0 列，正文会叠在头像上（截图里"头像压字"就是这个）
        Grid.SetColumn(body, 1);
        return row;
    }

    private View BuildAvatar()
    {
        var img = new Image { WidthRequest = 36, HeightRequest = 36, Aspect = Aspect.AspectFill, Margin = new Thickness(0, 8, 0, 8) };
        img.SetBinding(Image.SourceProperty, new Binding(nameof(SongComment.AvatarUrl), converter: NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
        return img;
    }

    private View BuildCommentBody()
    {
        var user = new Label { FontSize = 12, FontFamily = "OpenSansSemibold", TextColor = Color.FromArgb("#7c6cf0"), MaxLines = 1 };
        user.SetBinding(Label.TextProperty, nameof(SongComment.User));
        var time = new Label { FontSize = 10 };
        time.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        time.SetBinding(Label.TextProperty, new Binding(nameof(SongComment.Time), converter: TimeConverter.Instance));
        var userRow = new HorizontalStackLayout { Spacing = 8, Children = { user, time } };
        var content = new Label { FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
        content.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        content.SetBinding(Label.TextProperty, nameof(SongComment.Content));
        var like = new Label { FontSize = 11, TextColor = Color.FromArgb("#888888") };
        like.SetBinding(Label.TextProperty, new Binding(nameof(SongComment.LikedCount), stringFormat: "♥ {0}"));
        var likeLay = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.End, Children = { like } };
        var body = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } },
            RowSpacing = 4,
            Children = { userRow, content, likeLay },
        };
        // 同上：三行必须显式分行，否则全挤在第 0 行重叠
        Grid.SetRow(content, 1);
        Grid.SetRow(likeLay, 2);
        return body;
    }

    private async Task LoadAsync(string songId)
    {
        _currentSongId = songId;
        _offset = 0;
        _comments.Clear();
        _countLabel.Text = "";
        _loading.IsVisible = _loading.IsRunning = true;
        _empty.IsVisible = false;
        _loadMore.IsVisible = false;
        try
        {
            var list = _hot
                ? (_isPlaylist
                    ? await _plugin.GetPlaylistHotCommentsAsync(songId, 20)
                    : await _plugin.GetSongHotCommentsAsync(songId, 20))
                : (_isPlaylist
                    ? await _plugin.GetPlaylistCommentsAsync(songId, 20, 0)
                    : await _plugin.GetSongCommentsAsync(songId, 20, 0));
            Append(list);
            // 服务端总数/是否还有更多（顶层 total / more）；拿不到时退回"本页条数"逻辑
            var api = _plugin.ApiClient;
            if (api.LastCommentsTotal > 0)
                _countLabel.Text = $"共 {api.LastCommentsTotal} 条";
            _loadMore.IsVisible = api.LastCommentsHasMore || list.Count >= 20;
            // 歌单的热门评论恒为空，若用户手点回"热门评论"给个明确指引；私有歌单则是真的没有评论
            _empty.Text = _isPlaylist
                ? (_hot ? "该歌单没有热门评论，点「最新评论」看看" : "该歌单暂无评论")
                : "暂无评论";
        }
        catch (Exception ex)
        {
            _empty.Text = $"评论加载失败：{ex.Message}";
        }
        finally
        {
            _loading.IsVisible = _loading.IsRunning = false;
            _empty.IsVisible = _comments.Count == 0;
        }
    }

    private async Task LoadNextAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _offset += 20;
            // 歌单模式必须走歌单评论接口：原实现恒定调 GetSongCommentsAsync（R_SO_4_ 族），
            // 用歌单 id 去查歌曲评论 → 拿不到数据
            var page = _isPlaylist
                ? await _plugin.GetPlaylistCommentsAsync(CurrentSongId, 20, _offset)
                : await _plugin.GetSongCommentsAsync(CurrentSongId, 20, _offset);
            Append(page);
            var api = _plugin.ApiClient;
            _loadMore.IsVisible = api.LastCommentsHasMore || page.Count >= 20;
            if (api.LastCommentsTotal > 0) _countLabel.Text = $"共 {api.LastCommentsTotal} 条";
            if (page.Count == 0) _loadMore.Text = "没有更多了";
        }
        finally { _busy = false; }
    }

    private void Append(List<SongComment> list)
    {
        foreach (var c in list) _comments.Add(c);
        _countLabel.Text = _comments.Count > 0 ? _comments.Count.ToString() : "";
    }

    private class TimeConverter : IValueConverter
    {
        public static readonly TimeConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is long ms && ms > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd");
            return "";
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}