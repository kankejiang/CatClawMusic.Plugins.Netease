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
public class NetEaseMusicPlugin : IOnlineMusicPlugin, IViewContributorPlugin, ILyricsProviderPlugin, IQuickEntryPlugin
{
    private readonly NeteaseOpenApiClient _client = new();

    /// <summary>整页 VM 插件级单例（FM 电台常驻，页面开关不影响电台与补货）</summary>
    private static NeteaseOnlineMusicViewModel? _sharedVm;

    // ── 私人漫游推荐模式（DEFAULT/FAMILIAR/EXPLORE + 36 场景模式）──
    private static readonly Dictionary<string, string> FmModeLabels = new()
    {
        ["DEFAULT"] = "默认模式", ["FAMILIAR"] = "熟悉模式", ["EXPLORE"] = "探索模式",
        // 场景模式（36 种，经 mode=SCENE_RCMD&submode=CODE 生效）
        ["LATE_NIGHT_EMO"] = "伤感", ["EXERCISE"] = "运动", ["SLEEP_HELP"] = "助眠", ["RELAX"] = "放松",
        ["HAPPINESS"] = "欢快", ["LYRICAL"] = "抒情", ["CURE"] = "治愈", ["FOCUS"] = "专注",
        ["ROMANTIC"] = "情歌", ["RHYTHM_BLUES"] = "R&B", ["RAINY"] = "下雨天", ["GAMES"] = "打游戏",
        ["RAP"] = "说唱", ["K_POP"] = "K-Pop", ["ORIGINAL_MUSICIAL"] = "宝藏原创", ["ELECTRONIC"] = "电音",
        ["COMMUTE"] = "出行", ["BATH"] = "洗澡", ["COFFEE_SHOP"] = "咖啡馆", ["ROCK"] = "摇滚",
        ["INSPIRATIONAL"] = "励志", ["CHINESE"] = "华语", ["EUROPE_AMERICA"] = "欧美", ["CANTONESE"] = "粤语",
        ["DJ"] = "慢摇DJ", ["CLASSIC"] = "经典", ["LIGHT_MUSIC"] = "轻音乐", ["CHINESE_STYLE"] = "国风",
        ["FOLK"] = "民谣", ["ACG"] = "二次元", ["CLASSICAL"] = "古典", ["JAZZ"] = "爵士",
        ["JAPANESE"] = "日语", ["WORLD"] = "全球", ["FRENCH"] = "法语", ["BLUES"] = "蓝调",
    };

    /// <summary>当前 FM 推荐模式 code（DEFAULT/FAMILIAR/EXPLORE 或场景码）；GetPrivateFmAsync 传给 API</summary>
    private string _currentFmMode = "DEFAULT";

    // ── 场景模式：按关键词搜索填充（替代无效的 SCENE_RCMD 原生场景），内存+文件持久化去重 ──
    private readonly List<OnlineSong> _scenePool = new();
    private readonly HashSet<string> _scenePoolIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sceneUsedPlaylists = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sceneFillLock = new(1, 1);
    private string _sceneKeyword = "";
    private int _sceneSearchPage = 1;
    private int _scenePoolPos;
    private int _scenePlaylistCursor;

    /// <summary>场景已补齐的歌曲 id（内存 + 文件持久化，重启后依然去重；上限 2000 滚动淘汰）</summary>
    private static readonly string SceneHistoryFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_scene_served.txt");
    private static readonly HashSet<string> _sceneServedIds = new(StringComparer.Ordinal);
    private static bool _sceneServedLoaded;
    private const int SceneServedMax = 2000;

    /// <summary>音质档位持久化文件</summary>
    private static readonly string QualityFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_quality.txt");

    public string PluginId => "netEaseMusic";
    public string Name => "网易云音乐";
    public string Version => "0.3.5";  // 与 GitHub Release tag 同步；插件管理页显示此版本，便于用户确认装的版本
    public string Author => "CatClawMusic";
    public string Description => "网易云官方接口（搜索/歌单/歌手/排行榜/漫游/每日推荐/红心/播放/歌词）";
    public List<string> Capabilities => new() { "search", "play", "lyrics", "playlist", "fm", "daily", "artist", "album", "quality", "like" };

    /// <summary>当前音质档位（0=标准 128k，1=高品 320k，2=无损 FLAC；登录增强）</summary>
    public int QualityLevel { get; private set; } = 1;

    // ── IQuickEntryPlugin：宿主发现页 HeroTrack 快捷入口（通用机制，任何插件可注册）──

    /// <summary>注册的快捷入口卡片（当前：私人漫游 → 点击直接开播 FM 电台，不进插件页面）</summary>
    public IReadOnlyList<QuickEntryInfo> QuickEntries => new[]
    {
        new QuickEntryInfo
        {
            Id = "fm",
            Title = "私人漫游",
            Icon = "🎧",
            Subtitle = "随机推荐 · 电台",
            Color1 = "#f953c6",
            Color2 = "#b91d73",
            SortOrder = 0, // 排在各插件快捷入口最前（并列时按注册顺序，先注册在前）
        },
    };

    /// <summary>执行快捷入口动作：私人漫游 → 直接启动 FM 电台播放（复用整页 VM 单例，不进页面）</summary>
    public void ExecuteQuickEntry(string entryId, IServiceProvider services)
    {
        if (entryId != "fm") return;
        try
        {
            var vm = GetSharedVm(services);
            _ = vm.LoadPrivateFmAsync();
        }
        catch (Exception ex)
        {
            Log.Debug("NeteasePlugin", $"[QuickEntry] 启动私人漫游失败: {ex.Message}");
        }
    }

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
        var vm = GetSharedVm(services);
        return new NeteaseOnlineMusicPage(vm, services);
    }

    /// <summary>获取插件级单例 VM（快捷入口与入口页面共用；首次创建时解析宿主播放服务）</summary>
    private NeteaseOnlineMusicViewModel GetSharedVm(IServiceProvider services)
    {
        if (_sharedVm != null) return _sharedVm;
        var queue = services.GetRequiredService<CatClawMusic.Core.Services.PlayQueue>();
        var audioPlayer = services.GetRequiredService<CatClawMusic.Core.Interfaces.IAudioPlayerService>();
        _sharedVm = new NeteaseOnlineMusicViewModel(this, queue, audioPlayer, services);
        return _sharedVm;
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

    /// <summary>跨源解灰开关（默认关闭）：网易云取链全部失败时用酷我同名曲补播</summary>
    public static bool UnblockEnabled { get; set; }

    public async Task<string?> GetPlayUrlAsync(OnlineSong song, int quality = 0)
    {
        var url = await _client.GetPlayUrlAsync(song.Id, quality > 0 ? quality : QualityLevel);
        if (!string.IsNullOrWhiteSpace(url) || !UnblockEnabled) return url;
        // 跨源解灰：仅当歌曲有标题（播放/队列场景）时用「歌名+歌手」在酷我补播
        if (string.IsNullOrWhiteSpace(song.Title)) return null;
        try { return await KuwoUnblock.ResolveAsync(song.Title, song.Artist); }
        catch { return null; }
    }

    public Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(OnlineSong song)
        => _client.GetLyricsAsync(song.Id);

    // ── ILyricsProviderPlugin：让宿主歌词链（Navidrome > 同名.lrc > 嵌入 > 插件）能消费在线歌词 ──
    // 宿主按 RemoteId("netease:{id}") 路由；当前构建未接 IOnlineMusicPlugin 歌词链，
    // 但 ILyricsProviderPlugin 已是宿主兜底链末级，实现它即可在当前宿主直接显示在线歌词。

    /// <summary>歌词服务可用（仅需登录态由 Cookie 增强，匿名也能取大部分词）</summary>
    public bool IsAvailable => true;

    /// <summary>
    /// 宿主歌词兜底链调用：从 <paramref name="song"/>.RemoteId 解析网易云 songId，
    /// 拉取 LRC（+翻译）并解析为结构化 <see cref="LrcLyrics"/> 返回。
    /// </summary>
    public async Task<LrcLyrics?> GetLyricsAsync(Song song)
    {
        if (song?.RemoteId == null) return null;
        var id = ExtractNeteaseId(song.RemoteId);
        if (string.IsNullOrWhiteSpace(id)) return null;
        var pair = await _client.GetLyricsAsync(id);
        if (pair == null || string.IsNullOrWhiteSpace(pair.Value.Lrc)) return null;
        return ParseNeteaseLyrics(pair.Value.Lrc, pair.Value.TLrc);
    }

    /// <summary>从 "netease:12345" 形式 RemoteId 提取 songId</summary>
    private static string? ExtractNeteaseId(string remoteId)
    {
        var idx = remoteId.IndexOf(':', StringComparison.Ordinal);
        return idx >= 0 ? remoteId.Substring(idx + 1) : remoteId;
    }

    /// <summary>
    /// 解析网易云 LRC（+翻译）为 <see cref="LrcLyrics"/>。
    /// lrc/tlyric 是两份独立标准 LRC，按时间戳把翻译挂到对应原词行；元数据行（[ti:]/[ar:]）忽略。
    /// 自带最小解析器，避免依赖宿主 LyricsService 实例（插件取不到其服务）。
    /// </summary>
    private static LrcLyrics? ParseNeteaseLyrics(string lrc, string? tlyric)
    {
        var main = ParseRawLrc(lrc);
        if (main == null || main.Count == 0) return null;
        Dictionary<TimeSpan, string>? trans = null;
        if (!string.IsNullOrWhiteSpace(tlyric))
        {
            var t = ParseRawLrc(tlyric);
            if (t != null)
            {
                trans = new Dictionary<TimeSpan, string>();
                foreach (var (ts, text) in t)
                    if (!trans.ContainsKey(ts)) trans[ts] = text; // 重复时间戳以首个为准，避免 ToDictionary 抛异常
            }
        }
        var lines = new List<LrcLyricLine>();
        foreach (var (ts, text) in main)
        {
            var line = new LrcLyricLine { Timestamp = ts, Text = text };
            if (trans != null && trans.TryGetValue(ts, out var tr) && !string.IsNullOrWhiteSpace(tr))
                line.Translation = tr;
            lines.Add(line);
        }
        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return new LrcLyrics { Lines = lines };
    }

    /// <summary>解析单份 LRC 文本为 (时间戳, 文本) 列表（同一行多时间戳会展开为多行）</summary>
    private static List<(TimeSpan Ts, string Text)>? ParseRawLrc(string lrc)
    {
        var result = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var timestamps = new List<TimeSpan>();
            while (line.StartsWith("[", StringComparison.Ordinal))
            {
                var close = line.IndexOf(']');
                if (close < 0) break;
                var tsStr = line.Substring(1, close - 1).Trim();
                // 支持 mm:ss.xx / mm:ss.xxx / mm:ss
                if (TimeSpan.TryParseExact(tsStr, new[] { @"mm\:ss\.fff", @"mm\:ss\.ff", @"mm\:ss" },
                        System.Globalization.CultureInfo.InvariantCulture, out var ts))
                    timestamps.Add(ts);
                line = line.Substring(close + 1);
            }
            var text = line.Trim();
            if (timestamps.Count > 0 && !string.IsNullOrEmpty(text))
                foreach (var ts in timestamps)
                    result.Add((ts, text));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>私人漫游（随机推荐）。推荐模式走原生 FM；场景模式改为按关键词搜索填充。</summary>
    public async Task<List<OnlineSong>?> GetPrivateFmAsync(int num = 10)
    {
        if (!NeteaseOpenApiClient.FmSceneCodes.Contains(_currentFmMode))
            return await _client.GetPrivateFmAsync(num, _currentFmMode);
        return await FillSceneBatchAsync(num);
    }

    /// <summary>场景播放补货：从关键词搜索结果池顺序取 <paramref name="batchSize"/> 首（过滤已补齐），池空则增量拉取。</summary>
    private async Task<List<OnlineSong>?> FillSceneBatchAsync(int batchSize)
    {
        await _sceneFillLock.WaitAsync();
        try
        {
            // 切换场景时重置节目池，换用新关键词
            var keyword = FmModeLabels.TryGetValue(_currentFmMode, out var lbl) ? lbl : _currentFmMode;
            if (keyword != _sceneKeyword)
            {
                _scenePool.Clear(); _scenePoolIds.Clear(); _sceneUsedPlaylists.Clear();
                _sceneSearchPage = 1; _scenePoolPos = 0; _scenePlaylistCursor = 0; _sceneKeyword = keyword;
            }
            EnsureSceneHistoryLoaded();
            // 池子不够就持续拉取，直到凑齐或拉尽（每次循环无进展即停，避免死循环）
            int attempts = 0;
            while (_scenePoolPos + batchSize > _scenePool.Count && attempts < 6)
            {
                int before = _scenePool.Count;
                await TryExpandScenePoolAsync(keyword);
                if (_scenePool.Count == before) break;
                attempts++;
            }
            var batch = new List<OnlineSong>();
            while (batch.Count < batchSize && _scenePoolPos < _scenePool.Count)
            {
                var s = _scenePool[_scenePoolPos++];
                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                batch.Add(s);
                MarkSceneServed(s.Id);
            }
            return batch.Count > 0 ? batch : null;
        }
        finally { _sceneFillLock.Release(); }
    }

    /// <summary>扩大场景节目池：先按关键词搜歌曲（翻页）；量不足再兜底搜歌单并展开其曲目。</summary>
    private async Task TryExpandScenePoolAsync(string keyword)
    {
        var songs = await _client.SearchSongsAsync(keyword, _sceneSearchPage++, 50);
        if (songs != null)
            foreach (var s in songs)
                if (s != null && !string.IsNullOrWhiteSpace(s.Id)
                    && !_sceneServedIds.Contains(s.Id) && _scenePoolIds.Add(s.Id))
                    _scenePool.Add(s);
        var playlists = await _client.SearchPlaylistsAsync(keyword, 12);
        if (playlists != null)
            while (_scenePlaylistCursor < playlists.Count && _scenePool.Count < 300)
            {
                var pl = playlists[_scenePlaylistCursor++];
                if (pl == null || pl.Id == null || !_sceneUsedPlaylists.Add(pl.Id)) continue;
                var tracks = await _client.GetPlaylistSongsAsync(pl, 1, 100);
                if (tracks == null) continue;
                foreach (var s in tracks)
                    if (s != null && !string.IsNullOrWhiteSpace(s.Id)
                        && !_sceneServedIds.Contains(s.Id) && _scenePoolIds.Add(s.Id))
                        _scenePool.Add(s);
            }
    }

    private static void EnsureSceneHistoryLoaded()
    {
        if (_sceneServedLoaded) return;
        _sceneServedLoaded = true;
        try
        {
            if (File.Exists(SceneHistoryFile))
                foreach (var line in File.ReadAllLines(SceneHistoryFile))
                    if (_sceneServedIds.Count < SceneServedMax && line.Trim().Length > 0)
                        _sceneServedIds.Add(line.Trim());
        }
        catch { /* 历史加载失败仅失去跨会话去重能力，不影响播放 */ }
    }

    private static void MarkSceneServed(string id)
    {
        if (!_sceneServedIds.Add(id)) return;
        try { File.AppendAllText(SceneHistoryFile, id + "\n"); }
        catch { }
        // 超过上限滚动淘汰最旧的记录，避免黑名单塞满导致场景无歌可播
        if (_sceneServedIds.Count > SceneServedMax)
        {
            try
            {
                var lines = File.ReadAllLines(SceneHistoryFile).Skip(_sceneServedIds.Count - SceneServedMax).ToList();
                File.WriteAllLines(SceneHistoryFile, lines);
            }
            catch { }
        }
    }

    /// <summary>每日推荐歌曲</summary>
    public Task<List<OnlineSong>?> GetDailyRecommendAsync(int num = 20)
        => _client.GetDailyRecommendAsync(num);

    /// <summary>相似歌曲（weapi）</summary>
    public Task<List<OnlineSong>> GetSimilarSongsAsync(string songId, int limit = 20)
        => _client.GetSimilarSongsAsync(songId, limit);

    /// <summary>历史每日推荐（weapi）</summary>
    public Task<List<OnlineSong>> GetHistoryRecommendSongsAsync()
        => _client.GetHistoryRecommendSongsAsync();

    /// <summary>MV 播放直链（weapi）</summary>
    public Task<string?> GetMvUrlAsync(string mvId, int r = 1080)
        => _client.GetMvUrlAsync(mvId, r);

    /// <summary>排行榜列表</summary>
    public Task<List<OnlinePlaylist>> GetToplistsAsync()
        => _client.GetToplistsAsync();

    // ── 内容延展：搜索联想 / 相似歌单 / 评论（插件自有 UI 展示）──

    /// <summary>搜索建议（输入联想）</summary>
    public Task<List<SearchSuggestion>> GetSearchSuggestAsync(string keyword, int limit = 8)
        => _client.GetSearchSuggestAsync(keyword, limit);

    /// <summary>热门搜索词</summary>
    public Task<List<string>> GetSearchHotAsync(int limit = 10)
        => _client.GetSearchHotAsync(limit);

    /// <summary>相似歌单（相关歌单）</summary>
    public Task<List<SimilarPlaylistInfo>> GetSimilarPlaylistsAsync(string playlistId, int limit = 10)
        => _client.GetSimilarPlaylistsAsync(playlistId, limit);

    /// <summary>歌曲热门评论</summary>
    public Task<List<SongComment>> GetSongHotCommentsAsync(string songId, int limit = 20)
        => _client.GetSongHotCommentsAsync(songId, limit);

    /// <summary>歌曲评论列表（offset 翻页）</summary>
    public Task<List<SongComment>> GetSongCommentsAsync(string songId, int limit = 20, int offset = 0)
        => _client.GetSongCommentsAsync(songId, limit, offset);

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

    /// <summary>返回私人漫游可用的推荐模式 + 场景模式列表（供宿主渲染抽屉）</summary>
    public Task<List<FmModeCategory>> GetFmModesAsync()
    {
        var list = new List<FmModeCategory>();
        // 推荐模式（3 种，官方 mode 参数）
        list.Add(new FmModeCategory { Type = "mode", Code = "DEFAULT", Title = "默认模式", SubTitle = "沿着目前喜好继续聆听", Icon = "🎵" });
        list.Add(new FmModeCategory { Type = "mode", Code = "FAMILIAR", Title = "熟悉模式", SubTitle = "喜欢过的歌曲与相似推荐", Icon = "❤️" });
        list.Add(new FmModeCategory { Type = "mode", Code = "EXPLORE", Title = "探索模式", SubTitle = "多元曲风与小众佳作", Icon = "🧭" });
        // 场景模式（36 种，4 列 9 行；经 mode=SCENE_RCMD&submode=CODE 生效）
        foreach (var code in NeteaseOpenApiClient.FmSceneCodes)
        {
            if (FmModeLabels.TryGetValue(code, out var title))
                list.Add(new FmModeCategory { Type = "scene", Code = code, Title = title });
        }
        return Task.FromResult(list);
    }

    /// <summary>切换到指定推荐模式/场景模式并重新加载电台；返回新模式显示名</summary>
    public async Task<string?> TrySetFmModeAsync(string modeCode)
    {
        if (!FmModeLabels.ContainsKey(modeCode)) return null;
        _currentFmMode = modeCode;
        if (_sharedVm != null)
            await _sharedVm.LoadPrivateFmAsync();
        return FmModeLabels[modeCode];
    }

    /// <summary>当前 FM 推荐模式显示名；不在 FM 模式返回 null</summary>
    public Task<string?> GetFmModeLabelAsync()
    {
        if (_sharedVm?.IsFmMode != true) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(FmModeLabels.TryGetValue(_currentFmMode, out var label) ? label : _currentFmMode);
    }

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
