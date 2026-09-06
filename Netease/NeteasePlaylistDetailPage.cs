using CatClawMusic.Core.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 歌单详情独立页（仿网易云官方详情页）：
/// 封面沉浸背景 + 标题/创建者/描述 + 分享/评论/收藏三胶囊 + 「播放全部」条 + 歌曲列表。
/// 数据：歌曲走 <see cref="NeteaseOnlineMusicViewModel.LoadPlaylistSongsAsync"/>（复用 VM 播放队列上下文），
/// 动态信息走 <see cref="NetEaseMusicPlugin.GetPlaylistDynamicInfoAsync"/>。
/// </summary>
public class NeteasePlaylistDetailPage : ContentPage
{
    private readonly NetEaseMusicPlugin _plugin;
    private readonly NeteaseOnlineMusicViewModel _vm;
    private readonly OnlinePlaylist _playlist;
    private readonly IServiceProvider? _services;

    private PlaylistDynamicInfo? _dynamic;
    private bool _loaded;
    private bool _batchDownloading;

    private readonly ActivityIndicator _loading = new() { IsRunning = true, IsVisible = true, Color = Color.FromArgb("#EC4141") };
    private readonly Label _emptyLabel = new()
    {
        Text = "歌单为空", TextColor = Color.FromArgb("#99FFFFFF"), FontSize = 13,
        HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 40, 0, 0),
    };
    private readonly Label _countLabel = new()
    {
        Text = "", TextColor = Color.FromArgb("#B3FFFFFF"), FontSize = 11, FontFamily = "OpenSansRegular",
    };
    private Grid? _pillsGrid;
    private readonly Image _creatorAvatar = new()
    {
        WidthRequest = 18, HeightRequest = 18, Aspect = Aspect.AspectFill,
        IsVisible = false, InputTransparent = true,
    };
    private readonly Label _creatorLabel = new()
    {
        Text = "", TextColor = Color.FromArgb("#B3FFFFFF"), FontSize = 12, MaxLines = 1,
        LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center,
    };

    /// <summary>歌单 ID（VM 防重复推页用）</summary>
    public string PlaylistId => _playlist.Id;

    public NeteasePlaylistDetailPage(NetEaseMusicPlugin plugin, NeteaseOnlineMusicViewModel vm,
        OnlinePlaylist playlist, IServiceProvider? services)
    {
        _plugin = plugin;
        _vm = vm;
        _playlist = playlist;
        _services = services;

        BackgroundColor = Color.FromArgb("#15151A");
        BindingContext = _vm; // 歌曲列表 ItemsSource 绑定 VM.Songs

        Content = BuildRoot();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        // 歌曲列表（进 VM 队列上下文）与动态信息并行加载
        var songsTask = _vm.LoadPlaylistSongsAsync(_playlist);
        var dynamicTask = _plugin.GetPlaylistDynamicInfoAsync(_playlist.Id);
        await songsTask;
        _dynamic = await dynamicTask;
        ApplyDynamicInfo();
        _loading.IsVisible = _loading.IsRunning = false;
        UpdateCountLabel();
        _emptyLabel.IsVisible = _vm.Songs.Count == 0;
    }

    // ══════════════════ 布局构建 ══════════════════

    private View BuildRoot()
    {
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // 顶栏
                new RowDefinition { Height = GridLength.Auto }, // 头部（封面/标题/描述）
                new RowDefinition { Height = GridLength.Auto }, // 三胶囊
                new RowDefinition { Height = GridLength.Auto }, // 播放全部条
                new RowDefinition { Height = GridLength.Star }, // 歌曲列表
            },
        };

        // ── 歌曲列表（z 最低，Row 4）：即便滚动内容越过边界，也会被上层 headerHost 遮住 ──
        var listHost = new Grid { Children = { BuildSongsList(), _loading, _emptyLabel } };
        Grid.SetRow(listHost, 4);
        root.Children.Add(listHost);

        // ── 头部容器（z 最高，跨 Row 0-3）：内含封面背景 + 暗化遮罩，整块不透明，
        //    保证列表滚动内容从头部的「后面」经过而不是盖在头上（官方同款层级） ──
        var headerHost = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            },
            RowSpacing = 10,
            Padding = new Thickness(16, 10, 16, 0),
        };

        var bg = new Image
        {
            Aspect = Aspect.AspectFill,
            InputTransparent = true,
        };
        bg.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl),
            converter: NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance)
        { Source = _playlist });
        headerHost.Children.Add(bg);

        var overlay = new BoxView
        {
            InputTransparent = true,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb("#D9141418"), 0f),
                    new(Color.FromArgb("#F2141418"), 0.55f),
                    new(Color.FromArgb("#FA141418"), 1f),
                },
                new Point(0, 0), new Point(0, 1)),
        };
        headerHost.Children.Add(overlay);

        headerHost.Children.Add(Cell(BuildTopBar(), 0));
        headerHost.Children.Add(Cell(BuildHeader(), 1));
        headerHost.Children.Add(Cell(BuildActionPills(), 2));
        headerHost.Children.Add(Cell(BuildPlayAllBar(), 3));
        Grid.SetRowSpan(headerHost, 4);
        root.Children.Add(headerHost);

        return root;
    }

    private View BuildTopBar()
    {
        var back = WhiteGlyph("‹", 30);
        back.GestureRecognizers.Add(MakeTap(async () => await NeteaseNav.PopAsync(this, _services)));

        var title = new Label
        {
            Text = "歌单", TextColor = Color.FromArgb("#D9FFFFFF"), FontSize = 15,
            FontFamily = "OpenSansSemibold", HorizontalOptions = LayoutOptions.StartAndExpand,
            VerticalOptions = LayoutOptions.Center, Margin = new Thickness(8, 0, 0, 0),
        };

        var more = WhiteGlyph("⋮", 22);
        more.HorizontalOptions = LayoutOptions.End;
        more.GestureRecognizers.Add(MakeTap(async () => await ShowPageMenuAsync()));

        return new Grid
        {
            ColumnDefinitions = ColumnDefinitions("Auto,Star,Auto"),
            Children = { Cell(back), Cell(title, col: 1), Cell(more, col: 2) },
        };
    }

    private View BuildHeader()
    {
        // 左：封面
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            WidthRequest = 92, HeightRequest = 92,
            VerticalOptions = LayoutOptions.Start,
        };
        var coverImage = new Image { Aspect = Aspect.AspectFill };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl),
            converter: NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance)
        { Source = _playlist, TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        // 右：标题 / 创建者 / 描述
        var title = new Label
        {
            Text = _playlist.Name,
            TextColor = Color.FromArgb("#F2FFFFFF"), FontSize = 16, FontFamily = "OpenSansSemibold",
            MaxLines = 3, LineBreakMode = LineBreakMode.TailTruncation,
        };

        var creatorRow = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        creatorRow.Add(_creatorAvatar);
        creatorRow.Add(_creatorLabel);

        var desc = new Label
        {
            Text = _playlist.Description ?? "",
            TextColor = Color.FromArgb("#8CFFFFFF"), FontSize = 11, MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 4, 0, 0),
        };

        var right = new VerticalStackLayout { Spacing = 2, Children = { title, creatorRow, desc } };

        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions("Auto,Star"),
            ColumnSpacing = 14,
            Children = { Cell(coverBorder), Cell(right, col: 1) },
        };
        return header;
    }

    private View BuildActionPills()
    {
        Border MakePill(string glyph, string label, Func<Task> onTap)
        {
            var icon = new Label { Text = glyph, TextColor = Color.FromArgb("#E6FFFFFF"), FontSize = 13, VerticalOptions = LayoutOptions.Center };
            var text = new Label
            {
                Text = label, TextColor = Color.FromArgb("#B3FFFFFF"), FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
            };
            var row = new HorizontalStackLayout
            {
                Spacing = 6,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children = { icon, text },
            };
            var pill = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 19 },
                HeightRequest = 38,
                BackgroundColor = Color.FromArgb("#1FFFFFFF"),
                Content = row,
            };
            pill.GestureRecognizers.Add(MakeTap(onTap));
            return pill;
        }

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions("Star,Star,Star"),
            ColumnSpacing = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };
        grid.Add(MakePill("↗", "分享", SharePlaylistAsync), 0);
        grid.Add(MakePill("💬", "评论", OpenPlaylistCommentsAsync), 1);
        grid.Add(MakePill("＋", "收藏", CollectPlaylistAsync), 2);
        _pillsGrid = grid;
        return grid;
    }

    private View BuildPlayAllBar()
    {
        // 红圆播放键
        var playCircle = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 21 },
            WidthRequest = 42, HeightRequest = 42,
            BackgroundColor = Color.FromArgb("#EC4141"),
            Content = new Label
            {
                Text = "▶", TextColor = Colors.White, FontSize = 15,
                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(2, 0, 0, 0),
            },
        };
        var playTap = MakeTap(() => _vm.PlayAllAsync());
        playCircle.GestureRecognizers.Add(playTap);

        var playTitle = new Label
        {
            Text = "播放全部", TextColor = Color.FromArgb("#F2FFFFFF"), FontSize = 15,
            FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center,
        };
        _countLabel.VerticalOptions = LayoutOptions.Center;
        var textCol = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { playTitle, _countLabel } };

        var download = WhiteGlyph("⬇", 18);
        download.VerticalOptions = LayoutOptions.Center;
        download.GestureRecognizers.Add(MakeTap(DownloadPlaylistAsync));

        var bar = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = new Thickness(12, 8),
            BackgroundColor = Color.FromArgb("#1FFFFFFF"),
            Content = new Grid
            {
                ColumnDefinitions = ColumnDefinitions("Auto,Star,Auto"),
                ColumnSpacing = 12,
                Children = { Cell(playCircle), Cell(textCol, col: 1), Cell(download, col: 2) },
            },
        };
        return bar;
    }

    private View BuildSongsList()
    {
        // 用 BindableLayout（非虚拟化）：宿主 MAUI 版本的 CollectionView 在推入页内曾出现
        // ItemsSource 有数据但不实例化行的问题；歌单规模（≤数百行）可接受全量布局。
        var list = new VerticalStackLayout { Spacing = 0 };
        BindableLayout.SetItemsSource(list, _vm.Songs);
        BindableLayout.SetItemTemplate(list, new DataTemplate(() =>
        {
            var cover = new Image
            {
                WidthRequest = 44, HeightRequest = 44, Aspect = Aspect.AspectFill,
                VerticalOptions = LayoutOptions.Center,
            };
            cover.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl),
                converter: NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance)
            { TargetNullValue = "ic_music_note" });
            var coverBorder = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Content = cover,
            };

            var title = new Label
            {
                FontSize = 14, TextColor = Color.FromArgb("#F2FFFFFF"),
                MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation,
            };
            title.SetBinding(Label.TextProperty, nameof(OnlineSong.Title));

            var artist = new Label
            {
                FontSize = 11, TextColor = Color.FromArgb("#99FFFFFF"),
                MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 2, 0, 0),
            };
            artist.SetBinding(Label.TextProperty, nameof(OnlineSong.Artist));

            var more = WhiteGlyph("⋮", 18);
            more.VerticalOptions = LayoutOptions.Center;
            more.GestureRecognizers.Add(MakeTap(async () =>
            {
                if ((more.BindingContext as OnlineSong) is { } s) await ShowSongMenuAsync(s);
            }));

            return new Grid
            {
                ColumnDefinitions = ColumnDefinitions("Auto,Star,Auto"),
                ColumnSpacing = 12,
                Padding = new Thickness(0, 6),
                GestureRecognizers = { MakeTap(async () =>
                {
                    if ((more.BindingContext as OnlineSong) is { } s) await _vm.PlaySongAsync(s);
                }) },
                Children = { Cell(coverBorder), Cell(new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { title, artist } }, col: 1), Cell(more, col: 2) },
            };
        }));
        // Android：ScrollView 无背景时不裁剪滚动内容，列表会上滑溢出盖住头部；
        // 给不透明深色背景即可触发裁剪（观感与官方下半屏深色一致）
        return new ScrollView
        {
            Content = list,
            BackgroundColor = Color.FromArgb("#141418"),
        };
    }

    // ══════════════════ 行为 ══════════════════

    private void ApplyDynamicInfo()
    {
        if (_dynamic == null) return;
        // 三胶囊计数更新
        if (_pillsGrid != null)
        {
            UpdatePillCount(_pillsGrid, 0, FormatCount(_dynamic.ShareCount));
            UpdatePillCount(_pillsGrid, 1, FormatCount(_dynamic.CommentCount));
            UpdatePillCount(_pillsGrid, 2, FormatCount(_dynamic.SubscribedCount));
        }
        // 创建者行
        if (!string.IsNullOrWhiteSpace(_dynamic.CreatorName))
        {
            _creatorLabel.Text = _dynamic.CreatorName;
            _creatorLabel.IsVisible = true;
        }
        if (!string.IsNullOrWhiteSpace(_dynamic.CreatorAvatar))
        {
            try
            {
                _creatorAvatar.Source = NeteaseUiKit.OnlineUrlToStreamImageConverter.Instance.Convert(
                    _dynamic.CreatorAvatar, typeof(ImageSource), null,
                    System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
                _creatorAvatar.IsVisible = _creatorAvatar.Source != null;
            }
            catch { }
        }
    }

    private static void UpdatePillCount(Grid pills, int col, string count)
    {
        if (pills[col] is Border b && b.Content is HorizontalStackLayout row && row.Count > 1 && row[1] is Label l)
            l.Text = count;
    }

    private void UpdateCountLabel()
    {
        var count = _vm.Songs.Count;
        if (count == 0) { _countLabel.Text = ""; return; }
        var totalMs = 0L;
        foreach (var s in _vm.Songs) totalMs += s.DurationMs;
        var span = TimeSpan.FromMilliseconds(totalMs);
        var duration = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时{span.Minutes}分钟"
            : $"{span.Minutes}分{span.Seconds}秒";
        _countLabel.Text = $"{count} 首 · {duration}";
    }

    private async Task ShowSongMenuAsync(OnlineSong song)
    {
        var page = GetHostPage() ?? this;
        var pick = await page.DisplayActionSheetAsync(song.Title, "取消", null,
            "▶ 播放", "⬇ 下载音乐", "❤ 红心 / 取消红心", "💬 查看评论");
        switch (pick)
        {
            case "▶ 播放":
                await _vm.PlaySongAsync(song);
                break;
            case "⬇ 下载音乐":
                var q = await _plugin.PickDownloadQualityAsync();
                if (q != null)
                {
                    var ok = await _plugin.DownloadOnlineSongAsync(song, q, _services);
                    await ToastAsync(ok ? $"已加入下载队列：{song.Title}" : "下载失败：无法获取播放直链");
                }
                break;
            case "❤ 红心 / 取消红心":
                await _vm.ToggleLikeAsync(song);
                break;
            case "💬 查看评论":
                await _vm.OpenCommentsAsync(song);
                break;
        }
    }

    private async Task ShowPageMenuAsync()
    {
        var page = GetHostPage() ?? this;
        var pick = await page.DisplayActionSheetAsync(_playlist.Name, "取消", null, "🔗 复制歌单链接");
        if (pick == "🔗 复制歌单链接") await CopyPlaylistLinkAsync();
    }

    private async Task SharePlaylistAsync()
    {
        var link = PlaylistLink();
        try
        {
            await Share.RequestAsync(new ShareTextRequest
            {
                Title = _playlist.Name,
                Text = $"{_playlist.Name}\n{link}",
                Uri = link,
            });
        }
        catch
        {
            await CopyPlaylistLinkAsync();
        }
    }

    private async Task OpenPlaylistCommentsAsync()
    {
        var nav = NeteaseNav.TryGetShell()?.Navigation
            ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (nav == null) { await ToastAsync("无法打开评论区"); return; }
        await nav.PushModalAsync(new NeteaseCommentsPage(
            new OnlineSong { Id = _playlist.Id, Title = _playlist.Name, Platform = "netease" },
            _plugin, isPlaylist: true));
    }

    private async Task CollectPlaylistAsync()
    {
        // 收藏（订阅）写操作需登录 checkToken，风险控制：先复制链接并提示去官方 App 收藏
        await CopyPlaylistLinkAsync();
        await ToastAsync("链接已复制，可在网易云 App 中打开并收藏该歌单");
    }

    private async Task CopyPlaylistLinkAsync()
    {
        try
        {
            await Clipboard.SetTextAsync(PlaylistLink());
            await ToastAsync("歌单链接已复制");
        }
        catch { await ToastAsync("复制失败"); }
    }

    private string PlaylistLink() => $"https://music.163.com/playlist?id={_playlist.Id}";

    /// <summary>批量下载：音质选择 → 确认 → 逐首取链入队（上限 50，跳过取链失败项）。</summary>
    private async Task DownloadPlaylistAsync()
    {
        if (_batchDownloading) return;
        if (_vm.Songs.Count == 0) { await ToastAsync("歌单还没加载出歌曲"); return; }

        var quality = await _plugin.PickDownloadQualityAsync();
        if (quality == null) return;

        var page = GetHostPage() ?? this;
        var max = Math.Min(_vm.Songs.Count, 50);
        var ok = await page.DisplayAlertAsync("批量下载",
            $"将「{_playlist.Name}」中前 {max} 首加入下载队列（当前音质：{QualityText(quality.Value)}）？\n取链需要一点时间，进度可在下载中心查看。",
            "下载", "取消");
        if (!ok) return;

        _batchDownloading = true;
        try
        {
            var done = 0;
            foreach (var song in _vm.Songs.Take(max))
            {
                if (await _plugin.DownloadOnlineSongAsync(song, quality, _services)) done++;
            }
            await ToastAsync(done > 0 ? $"已加入下载队列 {done}/{max} 首" : "没有成功取到任何直链");
        }
        finally
        {
            _batchDownloading = false;
        }
    }

    private static string QualityText(int level) => level switch
    {
        0 => "标准 128k",
        2 => "无损 FLAC",
        3 => "Hires",
        4 => "高清臻音",
        _ => "极高 320k",
    };

    // ══════════════════ 工具 ══════════════════

    /// <summary>给视图设置 Grid 行列位置后原样返回（Children 初始化器不会自动分配行列）</summary>
    private static T Cell<T>(T view, int row = 0, int col = 0) where T : View
    {
        Grid.SetRow(view, row);
        Grid.SetColumn(view, col);
        return view;
    }

    private static string FormatCount(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{n / 10_000.0:0.#}万",
        _ => n.ToString("0"),
    };

    private static Label WhiteGlyph(string glyph, double size) => new()
    {
        Text = glyph, TextColor = Color.FromArgb("#E6FFFFFF"), FontSize = size,
        HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        Padding = new Thickness(6, 0),
    };

    private static TapGestureRecognizer MakeTap(Func<Task> action)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try { await action(); }
            catch { }
        };
        return tap;
    }

    private static ColumnDefinitionCollection ColumnDefinitions(string widths)
    {
        var defs = new ColumnDefinitionCollection();
        foreach (var w in widths.Split(','))
            defs.Add(new ColumnDefinition
            {
                Width = w switch
                {
                    "Auto" => GridLength.Auto,
                    "Star" => GridLength.Star,
                    _ => new GridLength(double.Parse(w)),
                },
            });
        return defs;
    }

    private Page? GetHostPage()
    {
        try
        {
            var app = Application.Current;
            return app != null && app.Windows.Count > 0 ? app.Windows[0].Page : null;
        }
        catch { return null; }
    }

    /// <summary>轻提示：优先宿主主窗口 DisplayAlert（无 Shell 限制），失败静默。</summary>
    private async Task ToastAsync(string message)
    {
        try
        {
            var page = GetHostPage() ?? this;
            await page.DisplayAlertAsync("网易云音乐", message, "好");
        }
        catch { }
    }
}
