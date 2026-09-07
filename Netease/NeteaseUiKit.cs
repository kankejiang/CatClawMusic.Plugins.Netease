using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Input;
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

        /// <summary>相似歌曲 Command（参数即歌曲本身）</summary>
        public System.Windows.Input.ICommand? SimilarCommand { get; set; }

        /// <summary>MV 按钮 Command（仅歌曲有 MV 时可见）</summary>
        public System.Windows.Input.ICommand? MvCommand { get; set; }

        /// <summary>评论按钮 Command（参数即歌曲本身，打开评论区）</summary>
        public System.Windows.Input.ICommand? CommentCommand { get; set; }
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

        if (options?.SimilarCommand != null)
        {
            var similar = new Label
            {
                Text = "🎼",
                FontSize = 15,
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(4),
            };
            var similarTap = new TapGestureRecognizer();
            similarTap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(".", source: options.SimilarCommand));
            similarTap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            similar.GestureRecognizers.Add(similarTap);
            actionLayout.Children.Add(similar);
            hasAction = true;
        }

        if (options?.MvCommand != null)
        {
            var mv = new Label
            {
                Text = "🎬",
                FontSize = 15,
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(4),
            };
            mv.SetBinding(VisualElement.IsVisibleProperty,
                new Binding(nameof(OnlineSong.Internal), converter: MvVisibleConverter.Instance));
            var mvTap = new TapGestureRecognizer();
            mvTap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(".", source: options.MvCommand));
            mvTap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            mv.GestureRecognizers.Add(mvTap);
            actionLayout.Children.Add(mv);
            hasAction = true;
        }

        if (options?.CommentCommand != null)
        {
            var comment = new Label
            {
                Text = "💬",
                FontSize = 15,
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(4),
            };
            var commentTap = new TapGestureRecognizer();
            commentTap.SetBinding(TapGestureRecognizer.CommandProperty,
                new Binding(".", source: options.CommentCommand));
            commentTap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            comment.GestureRecognizers.Add(commentTap);
            actionLayout.Children.Add(comment);
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
    public static View CreatePlaylistItemTemplate(double? cardWidth = null, ICommand? tapCommand = null)
    {
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            HeightRequest = cardWidth is > 0 ? cardWidth.Value - 10 : 150,
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
            WidthRequest = cardWidth ?? -1,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardBackgroundColor");
        card.Content = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { coverBorder, nameLabel, countLabel },
        };

        // 分块网格模式下卡片以点击命令打开（外层行 SelectionMode=None，点击不落在列表选择上）
        if (tapCommand != null)
        {
            var tap = new TapGestureRecognizer { Command = tapCommand };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    // ── 相似/相关歌单卡片模板 ──

    /// <summary>相似歌单卡片：封面 + 名称 + 「n 首」+ 播放量</summary>
    public static View CreateSimilarPlaylistItemTemplate()
    {
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            HeightRequest = 150,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(SimilarPlaylistInfo.CoverUrl), converter: OnlineUrlToStreamImageConverter.Instance) { TargetNullValue = "ic_music_note" });
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
        nameLabel.SetBinding(Label.TextProperty, nameof(SimilarPlaylistInfo.Name));

        var countLabel = new Label
        {
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 6),
        };
        countLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        countLabel.SetBinding(Label.TextProperty, new Binding(nameof(SimilarPlaylistInfo.SongCount), stringFormat: "{0} 首"));

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

    /// <summary>入口卡片内部布局引用（横屏正方形 / 竖屏紧凑单行切换用）。</summary>
    public class EntryCardLayout
    {
        public required Label IconLabel { get; init; }
        public required Label TitleLabel { get; init; }
        public required Label SubtitleLabel { get; init; }
        public required VerticalStackLayout Stack { get; init; }
    }

    /// <summary>入口卡片（Border 子类，携带布局引用以便横竖屏切换）。</summary>
    public class EntryCard : Border
    {
        public EntryCardLayout? Layout { get; set; }

        /// <summary>Hero 卡封面图（异步加载到后显示并隐藏大图标；null = 渐变兜底）。</summary>
        public Image? HeroCover { get; set; }

        /// <summary>Hero 卡的大图标（封面加载成功后隐藏）。</summary>
        public Label? HeroIcon { get; set; }
    }

    /// <summary>
    /// 竖版大卡入口（仿网易云首页「每日推荐/心动模式/漫游」横滑卡）：
    /// 渐变背景 + 底部暗化渐变 + 左上大图标 + 底部标题/副标题叠层 + 可选右下播放键。
    /// 固定尺寸 150×210，放进横向滑动容器使用。
    /// </summary>
    public static EntryCard CreateHeroEntryCard(string icon, string title, string subtitle,
        string color1, string color2, bool showPlay = false)
    {
        var titleLabel = new Label
        {
            Text = title,
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            FontSize = 10,
            TextColor = Color.FromArgb("#CCFFFFFF"),
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var textStack = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(12, 0, 12, 12),
            Children = { titleLabel, subtitleLabel },
        };

        var iconLabel = new Label
        {
            Text = icon,
            FontSize = 32,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(14, 14, 0, 0),
            Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.35f, Radius = 6, Offset = new Point(0, 2) },
        };

        var card = new EntryCard
        {
            WidthRequest = 150,
            HeightRequest = 210,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(color1), 0f),
                    new GradientStop(Color.FromArgb(color2), 1f),
                },
            },
        };

        // 封面层（异步加载；加载成功后盖住渐变并隐藏大图标，加载失败保持渐变兜底）
        var heroCover = new Image
        {
            Aspect = Aspect.AspectFill,
            IsVisible = false,
            InputTransparent = true,
        };

        // 底部暗化渐变（文字可读性），官方图片卡同款
        var bottomFade = new BoxView
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0f),
                    new(Color.FromArgb("#B3000000"), 1f),
                },
                new Point(0, 0), new Point(0, 1)),
            VerticalOptions = LayoutOptions.End,
            HeightRequest = 110,
        };

        var content = new Grid();
        content.Add(heroCover);   // 封面（加载成功后显示）
        content.Add(bottomFade);  // 暗化渐变压在封面上，保证文字可读
        content.Add(iconLabel);
        content.Add(textStack);
        card.HeroCover = heroCover;
        card.HeroIcon = iconLabel;
        if (showPlay)
        {
            var play = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 13 },
                WidthRequest = 26, HeightRequest = 26,
                BackgroundColor = Color.FromArgb("#33FFFFFF"),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 0, 10, 12),
                Content = new Label
                {
                    Text = "▶", TextColor = Colors.White, FontSize = 11,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
                },
            };
            content.Add(play);
        }
        card.Content = content;
        return card;
    }

    /// <summary>渐变色功能入口卡片（私人漫游/每日推荐/排行榜等）：图标 + 标题 + 副标题，内容垂直居中。</summary>
    public static EntryCard CreateEntryCard(string icon, string title, string subtitle, string color1, string color2)
    {
        var iconLabel = new Label
        {
            Text = icon,
            FontSize = 26,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        var titleLabel = new Label
        {
            Text = title,
            FontSize = 13,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            FontSize = 10,
            TextColor = Color.FromArgb("#CCFFFFFF"),
            HorizontalTextAlignment = TextAlignment.Center,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Children = { iconLabel, titleLabel, subtitleLabel },
        };
        var card = new EntryCard
        {
            Padding = new Thickness(6, 4),
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
            Content = stack,
        };
        card.Layout = new EntryCardLayout { IconLabel = iconLabel, TitleLabel = titleLabel, SubtitleLabel = subtitleLabel, Stack = stack };
        return card;
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

    /// <summary>Internal["MvId"]（>0）→ MV 按钮可见性</summary>
    private sealed class MvVisibleConverter : IValueConverter
    {
        public static readonly MvVisibleConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not Dictionary<string, object> d || !d.TryGetValue("MvId", out var v)) return false;
            return v is long m && m > 0;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // ── 首页改版组件（精选/榜单/歌手 tab，仿官方首页）──

    /// <summary>播放量格式化：43364 → 4.3万，120450000 → 1.2亿；&lt;1 万显示原数。</summary>
    public static string FormatPlayCount(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{n / 10_000.0:0.#}万",
        _ => n.ToString("0"),
    };

    /// <summary>
    /// 横版封面入口卡（仿官方首页 150×104：封面铺满 + 左下角标题条，粗标题 + 细副题）。
    /// HeroCover/HeroIcon 语义与 HeroEntryCard 一致（异步贴封面成功后隐藏图标）。
    /// </summary>
    public static EntryCard CreateCoverEntryCard(string icon, string title, string subtitle,
        string color1, string color2, bool showPlay = false)
    {
        var iconLabel = new Label
        {
            Text = icon,
            FontSize = 12,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 4, 1),
        };
        var titleLabel = new Label
        {
            Text = title,
            FontSize = 12.5f,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.End,
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            FontSize = 9.5f,
            TextColor = Color.FromArgb("#CCFFFFFF"),
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var titleRow = new HorizontalStackLayout { Children = { iconLabel, titleLabel } };
        var textStack = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(9, 0, 9, 8),
            Children = { titleRow, subtitleLabel },
        };

        var card = new EntryCard
        {
            WidthRequest = 150,
            HeightRequest = 104,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(color1), 0f),
                    new GradientStop(Color.FromArgb(color2), 1f),
                },
            },
        };

        var heroCover = new Image { Aspect = Aspect.AspectFill, IsVisible = false, InputTransparent = true };
        var bottomFade = new BoxView
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0f),
                    new(Color.FromArgb("#D9000000"), 1f),
                },
                new Point(0, 0), new Point(0, 1)),
            VerticalOptions = LayoutOptions.End,
            HeightRequest = 56,
        };

        var content = new Grid();
        content.Add(heroCover);
        content.Add(bottomFade);
        content.Add(textStack);
        card.HeroCover = heroCover;
        card.HeroIcon = iconLabel;
        if (showPlay)
        {
            var play = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 11 },
                WidthRequest = 22, HeightRequest = 22,
                BackgroundColor = Color.FromArgb("#33FFFFFF"),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 0, 8, 8),
                Content = new Label
                {
                    Text = "▶", TextColor = Colors.White, FontSize = 9,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
                },
            };
            content.Add(play);
        }
        card.Content = content;
        return card;
    }

    /// <summary>
    /// 角标歌单卡（仿官方首页：正方形封面 + 左上播放量角标 + 底部两行标题）。
    /// 绑定 <see cref="NeteasePlaylist"/>（PlayCount 为子类扩展属性，运行时反射取值）。
    /// </summary>
    public static View CreatePlaylistCornerCard(double width, ICommand? tapCommand = null)
    {
        var coverBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            HeightRequest = width,
        };
        coverBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var coverImage = new Image { Aspect = Aspect.AspectFill, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlinePlaylist.CoverUrl),
            converter: OnlineUrlToStreamImageConverter.Instance)
        { TargetNullValue = "ic_music_note" });
        coverBorder.Content = coverImage;

        var countLabel = new Label
        {
            FontSize = 9.5f,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        countLabel.SetBinding(Label.TextProperty, new Binding(nameof(NeteasePlaylist.PlayCount),
            converter: PlayCountTextConverter.Instance));
        var countPill = new Border
        {
            BackgroundColor = Color.FromArgb("#66000000"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(5, 1),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(6, 5, 0, 0),
            Content = new HorizontalStackLayout { Spacing = 2, Children = { new Label { Text = "▶", FontSize = 8, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center }, countLabel } },
        };
        countPill.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(NeteasePlaylist.PlayCount),
            converter: PlayCountVisibleConverter.Instance));

        var coverGrid = new Grid { Children = { coverBorder, countPill } };

        var nameLabel = new Label
        {
            FontSize = 10.5f,
            LineHeight = 14,
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation,
            Padding = new Thickness(2, 5, 2, 0),
            HeightRequest = 34,
        };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, nameof(OnlinePlaylist.Name));

        var card = new Border
        {
            Padding = new Thickness(0),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            WidthRequest = width,
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardBackgroundColor");
        card.Content = new VerticalStackLayout { Spacing = 0, Children = { coverGrid, nameLabel } };

        if (tapCommand != null)
        {
            var tap = new TapGestureRecognizer { Command = tapCommand };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    /// <summary>榜单色块卡（横滑直达榜单；渐变按索引取固定色板，Binding 歌单名）。</summary>
    public static View CreateToplistColorCard(int index, ICommand? tapCommand = null)
    {
        (string C1, string C2)[] palette =
        {
            ("#FF8A80", "#E8443F"), ("#FFD180", "#F0A13A"), ("#FF9EC2", "#F2669E"),
            ("#8C9EFF", "#5348D4"), ("#B39DDB", "#5B46C9"), ("#82B1FF", "#3F51B5"),
        };
        var (c1, c2) = palette[index % palette.Length];
        var card = new Border
        {
            WidthRequest = 118,
            HeightRequest = 118,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(c1), 0f),
                    new GradientStop(Color.FromArgb(c2), 1f),
                },
            },
            Content = new Label
            {
                FontSize = 14.5f,
                FontFamily = "OpenSansSemibold",
                TextColor = Colors.White,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                MaxLines = 2,
                LineBreakMode = LineBreakMode.TailTruncation,
                Padding = new Thickness(8, 0),
            },
        };
        ((Label)card.Content!).SetBinding(Label.TextProperty, nameof(OnlinePlaylist.Name));
        if (tapCommand != null)
        {
            var tap = new TapGestureRecognizer { Command = tapCommand };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    /// <summary>
    /// 官方榜 Top3 卡：标题 + 更新频率 + 前三首（封面缩略 + 歌名 - 歌手）。
    /// 绑定 <see cref="ToplistBlock"/>；整卡点击打开榜单（复用歌单详情页链路）。
    /// </summary>
    public static View CreateToplistTop3Card(ICommand? tapCommand = null)
    {
        var nameLabel = new Label { FontSize = 14, FontFamily = "OpenSansSemibold", VerticalOptions = LayoutOptions.Center };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, new Binding(nameof(ToplistBlock.Playlist) + "." + nameof(OnlinePlaylist.Name)));

        var freqLabel = new Label { FontSize = 10, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End };
        freqLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        freqLabel.SetBinding(Label.TextProperty, nameof(ToplistBlock.UpdateFrequency));

        var rowTemplate = new DataTemplate(() =>
        {
            var cover = new Border
            {
                WidthRequest = 32, HeightRequest = 32,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                HorizontalOptions = LayoutOptions.Center,
            };
            cover.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
            var coverImage = new Image { Aspect = Aspect.AspectFill, WidthRequest = 32, HeightRequest = 32 };
            coverImage.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineSong.CoverUrl),
                converter: OnlineUrlToStreamImageConverter.Instance, converterParameter: 100)
            { TargetNullValue = "ic_music_note" });
            cover.Content = coverImage;
            var title = new Label { FontSize = 11, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
            title.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
            title.SetBinding(Label.TextProperty, new Binding(nameof(OnlineSong.Title)));
            var artist = new Label { FontSize = 10, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
            artist.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
            artist.SetBinding(Label.TextProperty, new Binding(nameof(OnlineSong.Artist)));
            var row = new Grid
            {
                HeightRequest = 38, // 行高固定：封面 32 + 上下留白；否则 Android 上 Auto 行会被流式封面图片撑爆
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new() { Width = GridLength.Auto },
                    new() { Width = new GridLength(2, GridUnitType.Star) },
                    new() { Width = new GridLength(1, GridUnitType.Star) },
                },
                ColumnSpacing = 8,
                Padding = new Thickness(0, 3),
            };
            // Grid.Children 初始化器不自动分配列，必须显式设置（详见 Page.Cell helper 注释）
            Grid.SetColumn(cover, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(artist, 2);
            row.Children.Add(cover);
            row.Children.Add(title);
            row.Children.Add(artist);
            return row;
        });
        var songsHost = new VerticalStackLayout { Spacing = 0 };
        songsHost.SetBinding(BindableLayout.ItemsSourceProperty, new Binding(nameof(ToplistBlock.TopSongs)));
        BindableLayout.SetItemTemplate(songsHost, rowTemplate);

        var card = new Border
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardBackgroundColor");
        card.Content = new VerticalStackLayout { Spacing = 6, Children = { new Grid { Children = { nameLabel, freqLabel } }, songsHost } };

        if (tapCommand != null)
        {
            var tap = new TapGestureRecognizer { Command = tapCommand };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding(nameof(ToplistBlock.Playlist)));
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    /// <summary>歌手圆头像卡（圆形头像 + 名字 + 单曲数），用于歌手 tab 双列网格。</summary>
    public static View CreateArtistAvatarCard(double width, ICommand? tapCommand = null)
    {
        var d = width - 24;
        var avatarBorder = new Border
        {
            WidthRequest = d,
            HeightRequest = d,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = d / 2 },
            HorizontalOptions = LayoutOptions.Center,
        };
        avatarBorder.SetDynamicResource(Border.BackgroundColorProperty, "SurfaceColor");
        var avatarImage = new Image { Aspect = Aspect.AspectFill };
        avatarImage.SetBinding(Image.SourceProperty, new Binding(nameof(NeteaseArtist.PicUrl),
            converter: OnlineUrlToStreamImageConverter.Instance, converterParameter: 300)
        { TargetNullValue = "ic_music_note" });
        avatarBorder.Content = avatarImage;

        var nameLabel = new Label { FontSize = 13, FontFamily = "OpenSansSemibold", HorizontalTextAlignment = TextAlignment.Center, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 8, 0, 0) };
        nameLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        nameLabel.SetBinding(Label.TextProperty, nameof(NeteaseArtist.Name));

        var songLabel = new Label { FontSize = 10.5f, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        songLabel.SetDynamicResource(Label.TextColorProperty, "TextHintColor");
        songLabel.SetBinding(Label.TextProperty, new Binding(nameof(NeteaseArtist.SongCount), stringFormat: "单曲: {0}"));

        var card = new VerticalStackLayout { Children = { avatarBorder, nameLabel, songLabel } };
        if (tapCommand != null)
        {
            var tap = new TapGestureRecognizer { Command = tapCommand };
            tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, new Binding("."));
            card.GestureRecognizers.Add(tap);
        }
        return card;
    }

    /// <summary>
    /// 在线 URL → 内存 Stream 封面（不落盘缓存）。在线歌曲封面下载到内存字节，
    /// 再用内存字典缓存避免重复下载；进程退出后自动释放，不会在本地堆积缓存文件或产生显示错误。
    /// </summary>
    internal sealed class OnlineUrlToStreamImageConverter : IValueConverter
    {
        public static readonly OnlineUrlToStreamImageConverter Instance = new();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, byte[]> _memCache = new();
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url)) return "ic_music_note";
            var sized = ApplyMaxSize(url, parameter);
            if (!ReferenceEquals(sized, url)) url = sized;
            return ImageSource.FromStream(ct => LoadAsync(url, ct));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>
        /// 按显示尺寸裁剪网易云封面（?param=WxH）。parameter 为 int 最大边长（如 150）；
        /// 已带 ?param= 的 URL 直接替换尺寸，带其它查询参数的用 &amp; 追加。
        /// 非网易云 CDN 的 URL 原样返回。缓存 key 用裁剪后的 URL，互不干扰。
        /// </summary>
        private static string ApplyMaxSize(string url, object? parameter)
        {
            var size = parameter switch
            {
                int i => i,
                string s when int.TryParse(s, out var n) => n,
                _ => 0,
            };
            if (size <= 0) return url;
            if (!url.Contains("music.126.net", StringComparison.OrdinalIgnoreCase)) return url;
            var param = $"param={size}y{size}";
            var idx = url.IndexOf("?param=", StringComparison.Ordinal);
            if (idx >= 0) return url.Substring(0, idx + 7) + param;
            return url + (url.Contains('?') ? "&" : "?") + param;
        }

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

    /// <summary>播放量角标文本：long? → 「▶ 4.3万」样式的数字部分；null → 空串。</summary>
    internal sealed class PlayCountTextConverter : IValueConverter
    {
        public static readonly PlayCountTextConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is long n && n > 0 ? FormatPlayCount(n) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>播放量角标可见性：有播放量（>0）才显示角标。</summary>
    internal sealed class PlayCountVisibleConverter : IValueConverter
    {
        public static readonly PlayCountVisibleConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is long n && n > 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
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