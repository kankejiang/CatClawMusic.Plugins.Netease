using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 插件共享 UI 工具：歌曲行 / 歌单卡片 / 歌手行 / 入口卡片 / 分类 chip 模板与值转换器。
/// 整页、发现页子 tab、歌手页、专辑页统一从这里取模板，保证视觉一致。
/// 全部 C# 代码构建（不用 XAML，避免跨程序集编译问题）。
/// </summary>
public static class NeteaseUiKit
{
    // ── 歌曲行模板 ──

    /// <summary>歌曲行模板的可选项（红心/垃圾桶按钮与可见性绑定）</summary>
    public class SongRowOptions
    {
        /// <summary>红心按钮 Command（绑定到 OnlineSong 上下文，参数即歌曲本身）</summary>
        public System.Windows.Input.ICommand? HeartCommand { get; set; }
        /// <summary>红心按钮可见性绑定源（如 ViewModel）与属性名（如 IsLoggedIn）</summary>
        public object? HeartVisibleSource { get; set; }
        public string? HeartVisibleProperty { get; set; }

        /// <summary>垃圾桶 Command（FM 模式用）</summary>
        public System.Windows.Input.ICommand? TrashCommand { get; set; }
        /// <summary>垃圾桶可见性绑定源与属性名（如 IsFmMode）</summary>
        public object? TrashVisibleSource { get; set; }
        public string? TrashVisibleProperty { get; set; }
    }

    /// <summary>
    /// 歌曲行：封面 40 + 标题/艺术家 + （可选）VIP 角标 + 红心/垃圾桶操作列。
    /// VIP/下架歌曲整行降透明度提示；红心图标按 Internal["Liked"] 渲染。
    /// </summary>
    public static View CreateSongItemTemplate(SongRowOptions? options = null)
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
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var titleLabel = new Label { FontSize = 14, FontFamily = "OpenSansSemibold", MaxLines = 1 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        titleLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Title));

        // VIP 角标（fee=1/4 时显示）
        var vipBadge = new Label
        {
            Text = "VIP",
            FontSize = 8,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#FF5E62"),
            Padding = new Thickness(3, 1),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        vipBadge.SetBinding(VisualElement.IsVisibleProperty,
            new Binding(nameof(OnlineSong.Internal), converter: VipBadgeVisibleConverter.Instance));
        var vipFrame = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            Padding = 0,
            Content = vipBadge,
            VerticalOptions = LayoutOptions.Center,
        };
        vipFrame.SetBinding(VisualElement.IsVisibleProperty,
            new Binding(nameof(OnlineSong.Internal), converter: VipBadgeVisibleConverter.Instance));

        var artistLabel = new Label { FontSize = 11, MaxLines = 1 };
        artistLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        artistLabel.SetBinding(Label.TextProperty, nameof(OnlineSong.Artist));

        var titleRow = new HorizontalStackLayout
        {
            Spacing = 0,
            Children = { titleLabel, vipFrame },
        };
        // HorizontalStackLayout 不会自动撑满，标题过长时让 VIP 角标紧随文本后
        var textLayout = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Children = { titleRow, artistLabel },
        };

        var columns = new ColumnDefinitionCollection
        {
            new() { Width = GridLength.Auto },
            new() { Width = GridLength.Star },
        };
        var children = new List<View> { coverBorder, textLayout };
        Grid.SetColumn(textLayout, 1);

        // 操作列：红心 + 垃圾桶（各自按需可见）
        var actionLayout = new HorizontalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center };
        bool hasAction = false;

        if (options?.HeartCommand != null)
        {
            var heart = new Label
            {
                FontSize = 16,
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(4),
            };
            heart.SetBinding(Label.TextProperty,
                new Binding(nameof(OnlineSong.Internal), converter: LikedIconConverter.Instance));
            var heartTap = new TapGestureRecognizer();
            heartTap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(".", source: options.HeartCommand));
            heartTap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            heart.GestureRecognizers.Add(heartTap);
            if (options.HeartVisibleSource != null && options.HeartVisibleProperty != null)
                heart.SetBinding(VisualElement.IsVisibleProperty,
                    new Binding(options.HeartVisibleProperty, source: options.HeartVisibleSource));
            actionLayout.Children.Add(heart);
            hasAction = true;
        }

        if (options?.TrashCommand != null)
        {
            var trash = new Label
            {
                Text = "🗑",
                FontSize = 15,
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(4),
            };
            var trashTap = new TapGestureRecognizer();
            trashTap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(".", source: options.TrashCommand));
            trashTap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            trash.GestureRecognizers.Add(trashTap);
            if (options.TrashVisibleSource != null && options.TrashVisibleProperty != null)
                trash.SetBinding(VisualElement.IsVisibleProperty,
                    new Binding(options.TrashVisibleProperty, source: options.TrashVisibleSource));
            actionLayout.Children.Add(trash);
            hasAction = true;
        }

        Grid grid;
        if (hasAction)
        {
            columns.Add(new ColumnDefinition { Width = GridLength.Auto });
            children.Add(actionLayout);
            Grid.SetColumn(actionLayout, 2);
            grid = new Grid
            {
                Padding = new Thickness(14, 8),
                ColumnDefinitions = columns,
                ColumnSpacing = 12,
            };
        }
        else
        {
            grid = new Grid
            {
                Padding = new Thickness(14, 8),
                ColumnDefinitions = columns,
                ColumnSpacing = 12,
            };
        }
        foreach (var c in children) grid.Children.Add(c);

        // VIP/下架歌曲整行降透明度
        grid.SetBinding(VisualElement.OpacityProperty,
            new Binding(nameof(OnlineSong.Internal), converter: VipOpacityConverter.Instance));
        return grid;
    }

    // ── 歌单卡片模板 ──

    /// <summary>歌单卡片：封面 + 名称（两行）+ 首数/描述（一行）。封面已带裁尺寸参数。</summary>
    public static View CreatePlaylistItemTemplate()
    {
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            HeightRequest = 150,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var nameLabel = new Label
        {
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation,
            Padding = new Thickness(6, 0, 6, 0),
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

    // ── 歌手行模板 ──

    /// <summary>歌手行：圆形头像 + 名称 + 「n 首歌曲 · m 张专辑」</summary>
    public static View CreateArtistItemTemplate()
    {
        var avatarBorder = new Border
        {
            WidthRequest = 44,
            HeightRequest = 44,
            StrokeShape = new RoundRectangle { CornerRadius = 22 },
            StrokeThickness = 0,
        };
        avatarBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var avatarImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 44, HeightRequest = 44 };
        avatarImage.SetBinding(Image.SourceProperty, new Binding(nameof(NeteaseArtist.PicUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
        avatarBorder.Content = avatarImage;

        var nameLabel = new Label { FontSize = 14, FontFamily = "OpenSansSemibold", MaxLines = 1 };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, nameof(NeteaseArtist.Name));

        var infoLabel = new Label { FontSize = 11, MaxLines = 1 };
        infoLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        infoLabel.SetBinding(Label.TextProperty, new Binding(nameof(NeteaseArtist.SongCount),
            stringFormat: "{0} 首歌曲"));

        return new Grid
        {
            Padding = new Thickness(14, 8),
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto },
                new() { Width = GridLength.Star },
            },
            ColumnSpacing = 12,
            Children =
            {
                avatarBorder,
                new VerticalStackLayout
                {
                    Spacing = 2,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { nameLabel, infoLabel },
                }.TapGridSetColumn(1),
            },
        };
    }

    /// <summary>专辑卡片（歌手页横向滚动用）：封面 120 + 名称 + 年份</summary>
    public static View CreateAlbumCardTemplate()
    {
        var coverBorder = new Border
        {
            WidthRequest = 120,
            HeightRequest = 120,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 120, HeightRequest = 120 };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(NeteaseAlbum.PicUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var nameLabel = new Label
        {
            FontSize = 11,
            FontFamily = "OpenSansSemibold",
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation,
            WidthRequest = 120,
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, nameof(NeteaseAlbum.Name));

        var yearLabel = new Label { FontSize = 9, WidthRequest = 120 };
        yearLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        yearLabel.SetBinding(Label.TextProperty, nameof(NeteaseAlbum.PublishYear));

        return new VerticalStackLayout
        {
            Spacing = 4,
            Children = { coverBorder, nameLabel, yearLabel },
        };
    }

    // ── 入口卡片 ──

    /// <summary>渐变色功能入口卡片（私人漫游/每日推荐/排行榜等）</summary>
    public static Border CreateEntryCard(string title, string subtitle, string color1, string color2)
    {
        var titleLabel = new Label { Text = title, FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White };
        var subtitleLabel = new Label { Text = subtitle, FontSize = 11, TextColor = Color.FromArgb("#CCFFFFFF") };
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
            Content = new VerticalStackLayout
            {
                Spacing = 3,
                Children = { titleLabel, subtitleLabel },
            },
        };
    }

    // ── 分类 chip ──

    /// <summary>分类 chip 模板（Tap 命令绑定到指定源的命令，参数为 chip 名）</summary>
    public static DataTemplate CreateCategoryChipTemplate(object commandSource, string commandPropertyName, string namePropertyName)
    {
        return new DataTemplate(() =>
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
            chipLabel.SetBinding(Label.TextProperty, namePropertyName);
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
            tap.SetBinding(TapGestureRecognizer.CommandProperty, new Binding(commandPropertyName, source: commandSource));
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding(nameof(CategoryChipItem.Name)));
            chip.GestureRecognizers.Add(tap);
            return chip;
        });
    }

    // ── 值转换器 ──

    /// <summary>Internal["Liked"] → 红心图标</summary>
    private sealed class LikedIconConverter : IValueConverter
    {
        public static readonly LikedIconConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Dictionary<string, object> d && d.TryGetValue("Liked", out var v) && v is true ? "❤️" : "🤍";
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Internal["Blocked"]（下架/无版权）→ 整行降透明度</summary>
    private sealed class VipOpacityConverter : IValueConverter
    {
        public static readonly VipOpacityConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Dictionary<string, object> d && d.TryGetValue("Blocked", out var v) && v is true ? 0.45 : 1.0;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Internal["Vip"] → VIP 角标可见性</summary>
    private sealed class VipBadgeVisibleConverter : IValueConverter
    {
        public static readonly VipBadgeVisibleConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Dictionary<string, object> d && d.TryGetValue("Vip", out var v) && v is true;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 在线 URL → 内存 Stream 封面（不落盘缓存）。在线歌曲封面下载到内存字节，
    /// 再用内存字典缓存避免重复下载；进程退出后自动释放，不会在本地堆积缓存文件或产生显示错误。
    /// </summary>
    private sealed class OnlineUrlToStreamImageConverter : IValueConverter
    {
        public static readonly OnlineUrlToStreamImageConverter Instance = new();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, byte[]> _memCache = new();
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url)) return "ic_music_note";
            return ImageSource.FromStream(ct => LoadAsync(url, ct));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static async Task<Stream> LoadAsync(string url, CancellationToken ct)
        {
            if (!_memCache.TryGetValue(url, out var bytes))
            {
                var task = _inflight.GetOrAdd(url, _ => DownloadAsync(url));
                try { bytes = await task.ConfigureAwait(false); }
                finally { _inflight.TryRemove(url, out _); }
                if (bytes is { Length: > 0 }) _memCache[url] = bytes;
            }
            return new MemoryStream(bytes ?? Array.Empty<byte>());
        }

        private static async Task<byte[]?> DownloadAsync(string url)
        {
            try { return await Http.GetByteArrayAsync(url).ConfigureAwait(false); }
            catch { return null; }
        }
    }
}

/// <summary>小工具：链式设置 Grid.Column（让集合初始化器里的子视图也能设列号）</summary>
internal static class GridColumnExtensions
{
    public static T TapGridSetColumn<T>(this T view, int column) where T : BindableObject
    {
        Grid.SetColumn(view, column);
        return view;
    }
}