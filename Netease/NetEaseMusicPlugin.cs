using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云音乐音源插件：官方接口（老 web API 匿名优先 + 可选 Cookie），
/// 覆盖搜索（歌曲/歌单/歌手）/ 歌单广场（分类+分页）/ 歌单内歌曲 / 排行榜 /
/// 歌手热门歌曲/专辑 / 专辑歌曲 / 播放直链（音质三档 + 三级兜底 + 缓存）/ 歌词 /
/// 私人漫游（无限电台 + 垃圾桶）/ 每日推荐（歌曲+歌单）/ 我的歌单 / 红心 / 听歌打卡。
/// <para>
/// 同时实现 <see cref="IViewContributorPlugin"/>：向宿主贡献一个完整的"网易云音乐"入口页面，
/// 由插件自治提供 UI 和业务逻辑。
/// </para>
/// </summary>
public class NetEaseMusicPlugin : IOnlineMusicPlugin, IViewContributorPlugin
{
    private readonly NeteaseOpenApiClient _client = new();

    /// <summary>音质档位持久化文件</summary>
    private static readonly string QualityFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_quality.txt");

    public string PluginId => "netEaseMusic";
    public string Name => "网易云音乐";
    public string Version => "0.1.9";  // 与 GitHub Release tag 同步（v0.1.x 补丁位）；插件管理页显示此版本，便于用户确认装的版本
    public string Author => "CatClawMusic";
    public string Description => "网易云官方接口（搜索/歌单/歌手/排行榜/漫游/每日推荐/红心/播放/歌词）";
    public List<string> Capabilities => new() { "search", "play", "lyrics", "playlist", "fm", "daily", "artist", "album", "quality", "like" };

    /// <summary>当前音质档位（0=标准 128k，1=高品 320k，2=无损 FLAC；登录增强）</summary>
    public int QualityLevel { get; private set; } = 1;

    /// <summary>设置音质档位并持久化（宿主/插件 UI 共用一份状态）</summary>
    public void SetQualityLevel(int level)
    {
        QualityLevel = Math.Clamp(level, 0, 2);
        try { File.WriteAllText(QualityFilePath, QualityLevel.ToString()); } catch { }
    }

    // ── IViewContributorPlugin：插件向宿主贡献完整入口页面 ──

    /// <summary>发现页入口显示标题</summary>
    public string EntryTitle => "网易云音乐";

    /// <summary>发现页入口图标：res:// 嵌入式资源（png 打包进 .ccp，宿主 ImageSource.FromResource 加载；emoji/文本也兼容）</summary>
    public string EntryIcon => "res://netease_icon.png";

    /// <summary>
    /// 创建入口页面实例。宿主在用户点击入口时调用此方法，
    /// 返回的 <see cref="NeteaseOnlineMusicPage"/> 会被 Push 到导航栈。
    /// </summary>
    /// <param name="services">宿主服务提供者，用于获取 PlayQueue、IAudioPlayerService 等</param>
    /// <returns>NeteaseOnlineMusicPage 实例</returns>
    public object CreateEntryPage(IServiceProvider services)
    {
        var queue = services.GetRequiredService<CatClawMusic.Core.Services.PlayQueue>();
        var audioPlayer = services.GetRequiredService<CatClawMusic.Core.Interfaces.IAudioPlayerService>();
        var vm = new NeteaseOnlineMusicViewModel(this, queue, audioPlayer);
        return new NeteaseOnlineMusicPage(vm, services);
    }

    // ── IDiscoverTabPlugin 子 tab 已移除：发现页子 tab 与整页入口功能重叠，统一走 IViewContributorPlugin 整页入口 ──

    /// <summary>来源平台标识</summary>
    public string PlatformName => "netease";

    /// <summary>初始化：读取宿主写入的 Cookie 配置文件与音质偏好（可选，纯 .NET 路径避免 MAUI 依赖）</summary>
    public Task InitializeAsync()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatClawMusic.Maui");
            var cfg = Path.Combine(dir, "netease_cookie.txt");
            if (File.Exists(cfg))
            {
                var cookie = File.ReadAllText(cfg).Trim();
                if (!string.IsNullOrWhiteSpace(cookie)) _client.SetCookie(cookie);
            }
            if (File.Exists(QualityFilePath) && int.TryParse(File.ReadAllText(QualityFilePath).Trim(), out var q))
                QualityLevel = Math.Clamp(q, 0, 2);
        }
        catch { }
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    // ── IOnlineMusicPlugin（宿主聚合器使用的标准接口）──

    public Task<List<OnlineSong>?> SearchAsync(string keyword, int page = 1, int pageSize = 20)
        => _client.SearchSongsAsync(keyword, page, pageSize);

    public Task<List<OnlinePlaylist>> GetPlaylistsAsync(string? category = null)
        => _client.GetPlaylistsAsync(category, 1);

    public Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 200)
        => _client.GetPlaylistSongsAsync(playlist, page, pageSize);

    public Task<string?> GetPlayUrlAsync(OnlineSong song, int quality = 0)
        => _client.GetPlayUrlAsync(song.Id, quality > 0 ? quality : QualityLevel);

    public Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(OnlineSong song)
        => _client.GetLyricsAsync(song.Id);

    /// <summary>私人漫游（随机推荐）</summary>
    public Task<List<OnlineSong>?> GetPrivateFmAsync(int num = 10)
        => _client.GetPrivateFmAsync(num);

    /// <summary>每日推荐歌曲</summary>
    public Task<List<OnlineSong>?> GetDailyRecommendAsync(int num = 20)
        => _client.GetDailyRecommendAsync(num);

    /// <summary>排行榜列表</summary>
    public Task<List<OnlinePlaylist>> GetToplistsAsync()
        => _client.GetToplistsAsync();

    // ── 扩展能力（插件自有 UI 使用）──

    /// <summary>歌单广场（分页）</summary>
    public Task<List<OnlinePlaylist>> GetPlaylistsPageAsync(string? category, int page)
        => _client.GetPlaylistsAsync(category, page);

    /// <summary>官方歌单分类（失败返回 null，UI 回退硬编码）</summary>
    public Task<List<string>?> GetCategoriesAsync()
        => _client.GetPlaylistCategoriesAsync();

    /// <summary>歌单搜索</summary>
    public Task<List<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20)
        => _client.SearchPlaylistsAsync(keyword, limit);

    /// <summary>歌手搜索</summary>
    public Task<List<NeteaseArtist>> SearchArtistsAsync(string keyword, int limit = 20)
        => _client.SearchArtistsAsync(keyword, limit);

    /// <summary>歌手热门歌曲</summary>
    public Task<List<OnlineSong>?> GetArtistTopSongsAsync(string artistId)
        => _client.GetArtistTopSongsAsync(artistId);

    /// <summary>歌手专辑列表</summary>
    public Task<List<NeteaseAlbum>> GetArtistAlbumsAsync(string artistId)
        => _client.GetArtistAlbumsAsync(artistId);

    /// <summary>专辑内歌曲</summary>
    public Task<List<OnlineSong>?> GetAlbumSongsAsync(string albumId)
        => _client.GetAlbumSongsAsync(albumId);

    /// <summary>我的歌单（需登录）</summary>
    public Task<List<OnlinePlaylist>> GetUserPlaylistsAsync()
        => _client.GetUserPlaylistsAsync();

    /// <summary>每日推荐歌单（需登录）</summary>
    public Task<List<OnlinePlaylist>> GetRecommendPlaylistsAsync()
        => _client.GetRecommendPlaylistsAsync();

    /// <summary>已红心歌曲 id 集合</summary>
    public Task<HashSet<string>> GetLikedSongIdsAsync()
        => _client.GetLikedSongIdsAsync();

    /// <summary>红心/取消红心普通歌曲（需登录）</summary>
    public Task<bool> LikeSongAsync(string songId, bool like)
        => _client.LikeSongAsync(songId, like);

    /// <summary>私人漫游歌曲红心（需登录）</summary>
    public Task<bool> FmLikeAsync(string songId, bool like)
        => _client.FmLikeAsync(songId, like);

    /// <summary>私人漫游垃圾桶（需登录）</summary>
    public Task<bool> FmTrashAsync(string songId)
        => _client.FmTrashAsync(songId);

    /// <summary>听歌打卡（静默失败）</summary>
    public Task ScrobbleAsync(string songId, long durationMs)
        => _client.ScrobbleAsync(songId, durationMs);

    // ── 浏览器登录 ──

    /// <summary>获取浏览器登录配置（宿主 WebView 打开登录页）</summary>
    public Task<BrowserLoginInfo?> GetBrowserLoginInfoAsync()
        => _client.GetBrowserLoginInfoAsync();

    /// <summary>接收宿主从 WebView 提取的 Cookie，完成登录</summary>
    public Task SetLoginCookieAsync(string cookie)
        => _client.ApplyLoginCookieAsync(cookie);

    /// <summary>当前是否已登录</summary>
    public Task<bool> IsLoggedInAsync()
        => Task.FromResult(_client.HasCookie);

    /// <summary>已登录账号昵称</summary>
    public Task<string?> GetAccountNameAsync()
        => _client.GetAccountNameAsync();

    /// <summary>退出登录</summary>
    public Task LogoutAsync()
        => _client.LogoutAsync();
}
