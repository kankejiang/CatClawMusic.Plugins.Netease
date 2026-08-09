using System.Text;
using System.Text.Json;
using CatClawMusic.Core.Models;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云开放接口客户端（老 web API：匿名优先 + 可选用户 Cookie 增强）。
/// 覆盖：搜索（歌曲/歌单/歌手）/ 歌单广场（分页）/ 歌单详情 / 排行榜 / 歌手热门歌曲 /
/// 歌手专辑 / 专辑歌曲 / 播放直链（音质三档 + 三级兜底 + 20 分钟直链缓存）/ 歌词 /
/// 私人漫游（radio.get）/ 每日推荐（歌曲 + 歌单）/ 登录增强（我的歌单/红心/FM 垃圾桶/打卡）。
/// 登录：由宿主 WebView 打开 music.163.com 登录页，提取 Cookie 后回传 SetCookie。
/// 播放直链/封面统一 https；封面 URL 带 ?param= 裁尺寸，节省流量。
/// </summary>
public class NeteaseOpenApiClient
{
    private readonly HttpClient _http;
    private string? _cookie;

    /// <summary>用户 Cookie 持久化文件（宿主与插件约定的路径）</summary>
    private static readonly string CookieFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_cookie.txt");

    private static readonly string NicknameFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_nickname.txt");

    private static readonly string UidFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_uid.txt");

    // ── 播放直链缓存（songId:quality → (url, 过期时间)）──
    private readonly Dictionary<string, (string Url, DateTime ExpireAt)> _urlCache = new();
    private readonly object _urlCacheLock = new();
    private static readonly TimeSpan UrlCacheTtl = TimeSpan.FromMinutes(20);

    // ── 登录态派生缓存 ──
    private long? _userId;
    private string? _likedPlaylistId;
    private HashSet<string>? _likedSongIds;

    public NeteaseOpenApiClient()
    {
        // 禁用自动 Cookie 管理：二维码登录/播放直链需精确控制携带的 Cookie（用户 Cookie 优先）
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    }

    /// <summary>设置用户 Cookie（增强推荐个性化/播放完整度；可空 = 匿名）</summary>
    public void SetCookie(string? cookie) => _cookie = cookie;

    public bool HasCookie => !string.IsNullOrWhiteSpace(_cookie);

    /// <summary>
    /// 浏览器登录配置：宿主 WebView 打开 music.163.com 登录页，
    /// 用户登录后从 WebView 提取 MUSIC_U 等 Cookie 回传 <see cref="ApplyLoginCookieAsync"/>。
    /// </summary>
    public Task<BrowserLoginInfo?> GetBrowserLoginInfoAsync()
    {
        return Task.FromResult<BrowserLoginInfo?>(new BrowserLoginInfo
        {
            LoginUrl = "https://music.163.com/#/login",
            CookieDomain = "music.163.com",
            SuccessCookieNames = new List<string> { "MUSIC_U" },
            // 登录成功后通常跳转到首页或个人页
            SuccessUrlPattern = "music.163.com/#/m/loginsuccess",
            Title = "网易云登录"
        });
    }

    /// <summary>
    /// 接收宿主从 WebView 提取的完整 Cookie 字符串，持久化并刷新内存状态。
    /// </summary>
    public Task ApplyLoginCookieAsync(string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            _cookie = cookie;
            PersistCookie(cookie);
            // 新账号登录：清空上一个账号的派生缓存
            _userId = null;
            _likedPlaylistId = null;
            _likedSongIds = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>已登录账号昵称（/api/nuser/account/get 实名验证；失败回退本地缓存）</summary>
    public async Task<string?> GetAccountNameAsync()
    {
        if (string.IsNullOrWhiteSpace(_cookie)) return null;
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/nuser/account/get");
            if (doc != null && doc.RootElement.TryGetProperty("profile", out var p))
            {
                // 顺带缓存 uid（我的歌单/红心需要）
                if (p.TryGetProperty("userId", out var uidEl) && uidEl.TryGetInt64(out var uid) && uid > 0)
                {
                    _userId = uid;
                    try { File.WriteAllText(UidFilePath, uid.ToString()); } catch { }
                }
                if (p.TryGetProperty("nickname", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                {
                    var nickname = n.GetString();
                    if (!string.IsNullOrWhiteSpace(nickname))
                    {
                        try { File.WriteAllText(NicknameFilePath, nickname); } catch { }
                    }
                    return nickname;
                }
            }
        }
        catch { }
        // 兜底：读取登录时缓存的昵称
        try
        {
            if (File.Exists(NicknameFilePath))
                return File.ReadAllText(NicknameFilePath).Trim();
        }
        catch { }
        return null;
    }

    /// <summary>已登录用户 uid（内存 → 本地缓存 → 实时请求三级获取；未登录返回 null）</summary>
    public async Task<long?> GetUserIdAsync()
    {
        if (_userId is long cached) return cached;
        try
        {
            if (File.Exists(UidFilePath) && long.TryParse(File.ReadAllText(UidFilePath).Trim(), out var fuid) && fuid > 0)
            {
                _userId = fuid;
                return fuid;
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(_cookie))
        {
            await GetAccountNameAsync(); // 顺带解析 uid
            if (_userId is long uid) return uid;
        }
        return null;
    }

    /// <summary>退出登录：清空内存 Cookie 并删除持久化文件</summary>
    public async Task LogoutAsync()
    {
        _cookie = null;
        _userId = null;
        _likedPlaylistId = null;
        _likedSongIds = null;
        await Task.CompletedTask;
        try { if (File.Exists(CookieFilePath)) File.Delete(CookieFilePath); } catch { }
        try { if (File.Exists(NicknameFilePath)) File.Delete(NicknameFilePath); } catch { }
        try { if (File.Exists(UidFilePath)) File.Delete(UidFilePath); } catch { }
    }

    /// <summary>持久化登录 Cookie（供插件 InitializeAsync 重启后恢复）</summary>
    private void PersistCookie(string cookie)
    {
        try
        {
            var dir = Path.GetDirectoryName(CookieFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CookieFilePath, cookie);
        }
        catch { }
    }

    // ════════════════ 排行榜 / 搜索 / 歌单 ════════════════

    /// <summary>排行榜列表（/api/toplist，63 个榜单；榜单可当歌单打开）</summary>
    public async Task<List<OnlinePlaylist>> GetToplistsAsync()
    {
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/toplist");
            if (doc == null || !doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var result = new List<OnlinePlaylist>();
            foreach (var t in list.EnumerateArray())
            {
                result.Add(new OnlinePlaylist
                {
                    Id = t.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "",
                    Platform = "netease",
                    Name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    CoverUrl = CoverWithSize(ToHttps(t.TryGetProperty("coverImgUrl", out var c) ? c.GetString() : null), 500),
                    Description = t.TryGetProperty("description", out var d) ? d.GetString() : null,
                    SongCount = t.TryGetProperty("total", out var tc) ? tc.GetInt32() : 0,
                });
            }
            return result;
        }
        catch { return new List<OnlinePlaylist>(); }
    }

    /// <summary>歌单搜索（cloudsearch type=1000）</summary>
    public async Task<List<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = keyword, ["type"] = "1000", ["offset"] = "0", ["limit"] = limit.ToString()
            });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("playlists", out var pls) || pls.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            foreach (var pl in pls.EnumerateArray())
                list.Add(ParsePlaylist(pl));
            return list;
        }
        catch { return new List<OnlinePlaylist>(); }
    }

    /// <summary>歌手搜索（cloudsearch type=100）</summary>
    public async Task<List<NeteaseArtist>> SearchArtistsAsync(string keyword, int limit = 20)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = keyword, ["type"] = "100", ["offset"] = "0", ["limit"] = limit.ToString()
            });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                return new List<NeteaseArtist>();
            var list = new List<NeteaseArtist>();
            foreach (var a in artists.EnumerateArray())
            {
                if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                list.Add(new NeteaseArtist
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PicUrl = CoverWithSize(ToHttps(a.TryGetProperty("picUrl", out var p) ? p.GetString() : null), 300),
                    SongCount = a.TryGetProperty("musicSize", out var ms) && ms.TryGetInt32(out var msv) ? msv : 0,
                    AlbumCount = a.TryGetProperty("albumSize", out var abs) && abs.TryGetInt32(out var absv) ? absv : 0,
                });
            }
            return list;
        }
        catch { return new List<NeteaseArtist>(); }
    }

    /// <summary>歌手热门歌曲（搜歌手名 → 取第一个歌手 → 热门 50 首）</summary>
    public async Task<List<OnlineSong>?> GetArtistHotSongsAsync(string artistName)
    {
        try
        {
            var artists = await SearchArtistsAsync(artistName, 1);
            if (artists.Count == 0) return null;
            return await GetArtistTopSongsAsync(artists[0].Id);
        }
        catch { return null; }
    }

    /// <summary>歌手热门歌曲（/api/artist/top/song）</summary>
    public async Task<List<OnlineSong>?> GetArtistTopSongsAsync(string artistId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/artist/top/song?id={artistId}");
            if (doc == null || !doc.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>歌手专辑列表（/api/artist/albums/{id}）</summary>
    public async Task<List<NeteaseAlbum>> GetArtistAlbumsAsync(string artistId, int limit = 50)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/artist/albums/{artistId}?limit={limit}&offset=0");
            if (doc == null || !doc.RootElement.TryGetProperty("hotAlbums", out var albums) || albums.ValueKind != JsonValueKind.Array)
                return new List<NeteaseAlbum>();
            var list = new List<NeteaseAlbum>();
            foreach (var al in albums.EnumerateArray())
            {
                if (!al.TryGetProperty("id", out var idEl)) continue;
                string artistName = "";
                if (al.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var arn))
                    artistName = arn.GetString() ?? "";
                list.Add(new NeteaseAlbum
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = al.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PicUrl = CoverWithSize(ToHttps(al.TryGetProperty("picUrl", out var p) ? p.GetString() : null), 300),
                    SongCount = al.TryGetProperty("size", out var sz) && sz.TryGetInt32(out var szv) ? szv : 0,
                    ArtistName = artistName,
                    PublishYear = al.TryGetProperty("publishTime", out var pt) && pt.TryGetInt64(out var pts) && pts > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(pts).LocalDateTime.Year.ToString()
                        : null,
                });
            }
            return list;
        }
        catch { return new List<NeteaseAlbum>(); }
    }

    /// <summary>专辑内歌曲（/api/album/{id}）</summary>
    public async Task<List<OnlineSong>?> GetAlbumSongsAsync(string albumId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/album/{albumId}?id={albumId}");
            if (doc == null || !doc.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>搜索歌曲（/api/cloudsearch/pc，支持分页）</summary>
    public async Task<List<OnlineSong>?> SearchSongsAsync(string keyword, int page = 1, int pageSize = 20)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = keyword, ["type"] = "1", ["offset"] = offset.ToString(), ["limit"] = pageSize.ToString()
            });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>热门歌单（歌单广场 /api/playlist/list，支持分类 + 分页）</summary>
    public async Task<List<OnlinePlaylist>> GetPlaylistsAsync(string? category = null, int page = 1, int pageSize = 60)
    {
        try
        {
            var cat = string.IsNullOrWhiteSpace(category) || category == "全部" ? "全部" : category.Trim();
            var offset = (page - 1) * pageSize;
            var url = $"https://music.163.com/api/playlist/list?cat={Uri.EscapeDataString(cat)}&order=hot&limit={pageSize}&offset={offset}";
            using var doc = await GetJsonAsync(url);
            if (doc == null || !doc.RootElement.TryGetProperty("playlists", out var pls) || pls.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            foreach (var pl in pls.EnumerateArray())
                list.Add(ParsePlaylist(pl));
            return list;
        }
        catch { return new List<OnlinePlaylist>(); }
    }

    /// <summary>官方歌单分类（/api/playlist/catalogue；失败返回 null，调用方回退硬编码列表）</summary>
    public async Task<List<string>?> GetPlaylistCategoriesAsync()
    {
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/playlist/catalogue");
            if (doc == null || !doc.RootElement.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string> { "全部" };
            foreach (var c in sub.EnumerateArray())
            {
                if (c.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                    list.Add(n.GetString()!);
                if (list.Count >= 45) break; // 够用即可，避免 chips 过长
            }
            return list.Count > 1 ? list : null;
        }
        catch { return null; }
    }

    /// <summary>歌单内歌曲（v6 歌单详情，n=1000 一次拉全）</summary>
    public async Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 200)
    {
        if (string.IsNullOrWhiteSpace(playlist.Id)) return null;
        try
        {
            var url = $"https://music.163.com/api/v6/playlist/detail?id={playlist.Id}&n=1000&s=8";
            using var doc = await GetJsonAsync(url);
            if (doc == null || !doc.RootElement.TryGetProperty("playlist", out var pl) ||
                !pl.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                return null;
            var all = new List<OnlineSong>();
            foreach (var s in tracks.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) all.Add(song);
            }
            var start = (page - 1) * pageSize;
            if (start >= all.Count) return new List<OnlineSong>();
            return all.Skip(start).Take(pageSize).ToList();
        }
        catch { return null; }
    }

    // ════════════════ 私人漫游 / 每日推荐 ════════════════

    /// <summary>
    /// 私人漫游（随机推荐 /api/v1/radio/get）。
    /// 该接口一次通常只返回 1 首（网易云私人 FM 模型），故循环拉取并去重，
    /// 直到凑齐 <paramref name="num"/> 首或达到安全上限，模拟官方"无限电台"的首批缓冲。
    /// </summary>
    public async Task<List<OnlineSong>?> GetPrivateFmAsync(int num = 10)
    {
        try
        {
            var collected = new List<OnlineSong>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int attempts = 0;
            int maxAttempts = num * 3; // 安全上限，避免接口异常时死循环
            while (collected.Count < num && attempts < maxAttempts)
            {
                attempts++;
                using var doc = await GetJsonAsync("https://music.163.com/api/v1/radio/get");
                if (doc == null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    break;
            foreach (var s in data.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null && !string.IsNullOrWhiteSpace(song.Id) && seen.Add(song.Id))
                    collected.Add(song);
            }
        }
        if (collected.Count == 0) return null;
        // 私人 FM（/api/v1/radio/get）返回的 song.album 是推荐引擎的"上下文关联专辑"，
        // 其 picUrl 常与歌曲真实发行专辑不符 → 播放页显示错误封面/错歌词（已验证）。
        // 按 song.id 批量调 /api/song/detail 取标准 al.picUrl 覆盖。
        await CorrectFmMetadataAsync(collected);
        return collected;
    }
    catch { return null; }
}

    /// <summary>
    /// 修正私人 FM 歌曲的封面/专辑：FM 接口返回的 album 是"上下文关联专辑"，
    /// picUrl 常指向与歌曲真实发行专辑不符的图。按 song.id 批量调用
    /// /song/detail 取标准 al.picUrl 覆盖 CoverUrl（与 Album 名）。
    /// 三级兜底：①官方 music.163.com（cookie）→②公共 zm.wwoyun.cn →③公共 iwenwiki。
    /// 全部失败不影响播放，沿用 FM 原始封面。
    /// </summary>
    private async Task CorrectFmMetadataAsync(List<OnlineSong> songs)
    {
        if (songs == null || songs.Count == 0) return;
        // ① 官方 music.163.com/api/song/detail（依赖用户 Cookie；可能限流/被风控返回空 body）
        if (await TryCorrectSongCoversAsync(songs,
                $"https://music.163.com/api/song/detail?ids=[{string.Join(",", songs.Select(s => s.Id))}]",
                expectArrayKey: "songs"))
            return;
        // ② 公共 NeteaseCloudMusicApi 兜底（zm.wwoyun.cn / iwenwiki.com:3000 不需 Cookie）
        foreach (var baseUrl in PublicApiBases)
        {
            if (await TryCorrectSongCoversAsync(songs,
                    $"{baseUrl}/song/detail?ids={string.Join(",", songs.Select(s => s.Id))}",
                    expectArrayKey: "songs"))
                return;
        }
    }

    /// <summary>
    /// 通用单曲封面校正：从 URL 拉 JSON（形如 {songs:[...]}），按 id 匹配覆盖 CoverUrl/Album。
    /// 返回是否成功应用了任何更新。失败/解析错误返回 false（调用方继续尝试下一级）。
    /// </summary>
    private async Task<bool> TryCorrectSongCoversAsync(List<OnlineSong> songs, string url, string expectArrayKey)
    {
        try
        {
            using var doc = await GetJsonAsync(url);
            if (doc == null || !doc.RootElement.TryGetProperty(expectArrayKey, out var list) || list.ValueKind != JsonValueKind.Array)
                return false;
            return ApplySongCoverCorrection(songs, list);
        }
        catch { return false; }
    }

    /// <summary>把 /song/detail 的 songs 数组按 id 匹配，写回 CoverUrl/Album。返回是否改动过任一首。</summary>
    private static bool ApplySongCoverCorrection(List<OnlineSong> songs, JsonElement list)
    {
        bool any = false;
        foreach (var el in list.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
            var id = idEl.GetInt64().ToString();
            var song = songs.FirstOrDefault(s => s.Id == id);
            if (song == null) continue;
            JsonElement al;
            if (!el.TryGetProperty("al", out al) && !el.TryGetProperty("album", out al)) continue;
            if (al.ValueKind != JsonValueKind.Object) continue;
            if (al.TryGetProperty("picUrl", out var pic) && !string.IsNullOrWhiteSpace(pic.GetString()))
            {
                song.CoverUrl = CoverWithSize(ToHttps(pic.GetString()), 1000);
                any = true;
            }
            if (al.TryGetProperty("name", out var an) && !string.IsNullOrWhiteSpace(an.GetString()))
                song.Album = an.GetString()!;
        }
        return any;
    }

    /// <summary>私人漫游「垃圾桶」：不再推荐该歌曲（需登录；失败静默）</summary>
    public async Task<bool> FmTrashAsync(string songId)
    {
        if (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(_cookie)) return false;
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/radio/trash?songId={songId}&time=25");
            return doc != null && doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 200;
        }
        catch { return false; }
    }

    /// <summary>私人漫游歌曲红心/取消红心（/api/radio/like，需登录）</summary>
    public async Task<bool> FmLikeAsync(string songId, bool like)
    {
        if (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(_cookie)) return false;
        try
        {
            using var doc = await GetJsonAsync(
                $"https://music.163.com/api/radio/like?alg=itembased&songId={songId}&time=25&like={(like ? "true" : "false")}");
            return doc != null && doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 200;
        }
        catch { return false; }
    }

    /// <summary>每日推荐歌曲（/api/v3/discovery/recommend/songs；匿名可用，登录后个性化）</summary>
    public async Task<List<OnlineSong>?> GetDailyRecommendAsync(int num = 20)
    {
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/v3/discovery/recommend/songs");
            if (doc == null || !doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("dailySongs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list.Take(num).ToList();
        }
        catch { return null; }
    }

    /// <summary>推荐歌单（/api/personalized/playlist；匿名可用，登录后更个性化）。原 recommend/resource 需登录且匿名返回空</summary>
    public async Task<List<OnlinePlaylist>> GetRecommendPlaylistsAsync()
    {
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/personalized/playlist?limit=30");
            if (doc == null || !doc.RootElement.TryGetProperty("result", out var rec) || rec.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            foreach (var r in rec.EnumerateArray())
            {
                var pl = ParsePlaylist(r, coverField: "picUrl");
                if (!string.IsNullOrWhiteSpace(pl.Id)) list.Add(pl);
            }
            return list;
        }
        catch { return new List<OnlinePlaylist>(); }
    }

    // ════════════════ 登录增强：我的歌单 / 红心 ════════════════

    /// <summary>
    /// 我的歌单（/api/user/playlist；含"我喜欢的音乐"与收藏歌单，需登录）。
    /// Description 标注「创建/收藏」来源，UI 直接展示。
    /// </summary>
    public async Task<List<OnlinePlaylist>> GetUserPlaylistsAsync()
    {
        var uid = await GetUserIdAsync();
        if (uid == null) return new List<OnlinePlaylist>();
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/user/playlist?uid={uid}&offset=0&limit=200");
            if (doc == null || !doc.RootElement.TryGetProperty("playlist", out var pls) || pls.ValueKind != JsonValueKind.Array)
                return new List<OnlinePlaylist>();
            var list = new List<OnlinePlaylist>();
            bool first = true;
            foreach (var pl in pls.EnumerateArray())
            {
                var item = ParsePlaylist(pl);
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                bool subscribed = pl.TryGetProperty("subscribed", out var sb) && sb.ValueKind == JsonValueKind.True;
                item.Description = first ? "❤️ 我喜欢的音乐"
                    : subscribed ? "收藏的歌单" : "创建的歌单";
                if (first) _likedPlaylistId = item.Id; // 首个固定为「我喜欢的音乐」
                first = false;
                list.Add(item);
            }
            return list;
        }
        catch { return new List<OnlinePlaylist>(); }
    }

    /// <summary>「我喜欢的音乐」歌单 id（懒加载；未登录返回 null）</summary>
    private async Task<string?> GetLikedPlaylistIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(_likedPlaylistId)) return _likedPlaylistId;
        var list = await GetUserPlaylistsAsync();
        return list.Count > 0 ? _likedPlaylistId : null;
    }

    /// <summary>已红心歌曲 id 集合（用于列表 ❤ 状态展示；未登录返回空集合）</summary>
    public async Task<HashSet<string>> GetLikedSongIdsAsync()
    {
        if (_likedSongIds != null) return _likedSongIds;
        var set = new HashSet<string>(StringComparer.Ordinal);
        var pid = await GetLikedPlaylistIdAsync();
        if (pid == null) { _likedSongIds = set; return set; }
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/v6/playlist/detail?id={pid}&n=1000&s=0");
            if (doc != null && doc.RootElement.TryGetProperty("playlist", out var pl) &&
                pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tracks.EnumerateArray())
                {
                    if (t.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
                        set.Add(idEl.GetInt64().ToString());
                }
            }
        }
        catch { }
        _likedSongIds = set;
        return set;
    }

    /// <summary>红心/取消红心普通歌曲（/api/playlist/manipulate/tracks 增删「我喜欢的音乐」；需登录）</summary>
    public async Task<bool> LikeSongAsync(string songId, bool like)
    {
        if (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(_cookie)) return false;
        var pid = await GetLikedPlaylistIdAsync();
        if (pid == null) return false;
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                // 老接口必须携带 csrf_token（来自 Cookie 的 __csrf），缺失时服务器拒绝操作
                //（此前缺此参数：播放直链等 GET 接口不受影响，故"能播会员歌但红心失败"）
                ["csrf_token"] = ExtractCsrfToken(_cookie),
                ["op"] = like ? "add" : "del",
                ["trackId"] = songId,
                ["pid"] = pid,
                ["trackIds"] = $"[{songId}]",
                ["imme"] = "true",
            });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/playlist/manipulate/tracks");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 200;
            if (ok)
            {
                var set = await GetLikedSongIdsAsync();
                if (like) set.Add(songId); else set.Remove(songId);
            }
            return ok;
        }
        catch { return false; }
    }

    /// <summary>从 Cookie 字符串提取 csrf_token（__csrf 或 csrf_token 键；缺失返回空串，服务端宽松时也可通过）</summary>
    private static string ExtractCsrfToken(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "";
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() is "__csrf" or "csrf_token")
                return kv[1].Trim();
        }
        return "";
    }

    /// <summary>听歌打卡（/api/feedback/weblog；提升推荐精度，静默失败，需登录）</summary>
    public async Task ScrobbleAsync(string songId, long durationMs)
    {
        if (string.IsNullOrWhiteSpace(songId) || string.IsNullOrWhiteSpace(_cookie)) return;
        try
        {
            var seconds = Math.Max(1, durationMs / 1000);
            var logs = $"[{{\"action\":\"play\",\"json\":{{\"download\":0,\"end\":\"playend\",\"id\":\"{songId}\"," +
                       $"\"source\":\"list\",\"sourceId\":\"0\",\"time\":\"{seconds}\",\"type\":\"song\",\"wifi\":0}}}}]";
            var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["logs"] = logs });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/feedback/weblog");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            await resp.Content.ReadAsStringAsync(); // 忽略结果
        }
        catch { }
    }

    // ════════════════ 播放直链 / 歌词 ════════════════

    /// <summary>
    /// 播放直链（带音质 + 20 分钟缓存 + 三级兜底）。
    /// quality：0=标准 128k，1=高品 320k，2=无损 FLAC（需登录，匿名自动降级 320k）。
    /// </summary>
    public async Task<string?> GetPlayUrlAsync(string songId, int quality = 1)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        quality = Math.Clamp(quality, 0, 2);

        // 缓存命中
        var cacheKey = $"{songId}:{quality}";
        lock (_urlCacheLock)
        {
            if (_urlCache.TryGetValue(cacheKey, out var hit))
            {
                if (hit.ExpireAt > DateTime.UtcNow) return hit.Url;
                _urlCache.Remove(cacheKey);
            }
        }

        string? url = await ResolvePlayUrlAsync(songId, quality);
        if (!string.IsNullOrWhiteSpace(url))
        {
            lock (_urlCacheLock)
            {
                _urlCache[cacheKey] = (url, DateTime.UtcNow + UrlCacheTtl);
                // 简单防爆：缓存条目过多时整体清空（TTL 20 分钟，通常远达不到）
                if (_urlCache.Count > 2000) _urlCache.Clear();
            }
        }
        return url;
    }

    private static int QualityToBr(int quality) => quality switch
    {
        0 => 128000,
        2 => 999000,
        _ => 320000,
    };

    /// <summary>三级兜底取链（enhance 按音质 → outer 免登录外链 → 公共 API 实例）</summary>
    private async Task<string?> ResolvePlayUrlAsync(string songId, int quality)
    {
        // 无损需登录：匿名请求 br=999000 会被风控，直接降为 320k 流程
        int br = QualityToBr(quality == 2 && !HasCookie ? 1 : quality);

        // 方案1：enhance/player/url（按目标音质请求；静态 cookie 防风控，用户 cookie 提升完整度）
        var enhanceUrl = await GetEnhanceUrlAsync(songId, br);
        if (!string.IsNullOrWhiteSpace(enhanceUrl)) return enhanceUrl;

        // 方案2：免登录外链（302 到 CDN；标准/高品档可用）
        if (br <= 320000)
        {
            try
            {
                var outer = $"https://music.163.com/song/media/outer/url?id={songId}.mp3";
                using var resp = await _http.GetAsync(outer, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent)
                    return ToHttps(resp.RequestMessage?.RequestUri?.ToString() ?? outer);
            }
            catch { }
        }

        // 方案3：无损失败 → 降档 320k 再试 enhance
        if (br == 999000)
        {
            enhanceUrl = await GetEnhanceUrlAsync(songId, 320000);
            if (!string.IsNullOrWhiteSpace(enhanceUrl)) return enhanceUrl;
        }

        // 方案4：公共 NeteaseCloudMusicApi 实例兜底
        foreach (var api in PublicApiBases)
        {
            try
            {
                using var doc = await GetJsonAsync($"{api}/song/url?id={songId}");
                if (doc == null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var u))
                    {
                        var playUrl = u.GetString();
                        if (!string.IsNullOrWhiteSpace(playUrl)) return ToHttps(playUrl);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>enhance/player/url 按码率取链（静态 cookie 免风控）</summary>
    private async Task<string?> GetEnhanceUrlAsync(string songId, int br)
    {
        try
        {
            var req = Build(HttpMethod.Get, $"https://music.163.com/api/song/enhance/player/url?id={songId}&ids=[{songId}]&br={br}");
            // 用户 Cookie 优先（提升 VIP/无损完整度），未登录用静态 cookie 防风控
            if (string.IsNullOrWhiteSpace(_cookie))
                req.Headers.TryAddWithoutValidation("Cookie", "os=pc; appver=8.9.70");
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var u))
                    {
                        var playUrl = u.GetString();
                        if (!string.IsNullOrWhiteSpace(playUrl)) return ToHttps(playUrl);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>歌词（LRC + 翻译）。
    /// tv=0 请求原版歌词（tv=-1 为非标准取值）；官方接口对部分歌曲（风控/匿名限制/新歌）返回空 lrc 时，
    /// 回退到公共 NeteaseCloudMusicApi 实例兜底（播放直链已有同类兜底，歌词此前缺失）。</summary>
    public async Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(string songId)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        var result = await FetchLyricFromOfficialAsync(songId);
        if (result != null) return result;
        // 兜底：公共 NeteaseCloudMusicApi 实例
        foreach (var api in PublicApiBases)
        {
            try
            {
                using var doc = await GetJsonAsync($"{api}/lyric?id={songId}");
                if (doc == null) continue;
                string? lrc = null, tlyric = null;
                if (doc.RootElement.TryGetProperty("lrc", out var ln) && ln.TryGetProperty("lyric", out var lt))
                    lrc = lt.GetString();
                if (doc.RootElement.TryGetProperty("tlyric", out var tn) && tn.TryGetProperty("lyric", out var tt))
                    tlyric = tt.GetString();
                if (!string.IsNullOrWhiteSpace(lrc))
                    return (lrc, string.IsNullOrWhiteSpace(tlyric) ? null : tlyric);
            }
            catch { }
        }
        return null;
    }

    /// <summary>官方 /api/song/lyric 取词（lv=1 原版，tv=0 不请求翻译字段外的冗余版）</summary>
    private async Task<(string? Lrc, string? TLrc)?> FetchLyricFromOfficialAsync(string songId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/song/lyric?id={songId}&lv=1&tv=0");
            if (doc == null) return null;
            string? lrc = null, tlyric = null;
            if (doc.RootElement.TryGetProperty("lrc", out var ln) && ln.TryGetProperty("lyric", out var lt))
                lrc = lt.GetString();
            if (doc.RootElement.TryGetProperty("tlyric", out var tn) && tn.TryGetProperty("lyric", out var tt))
                tlyric = tt.GetString();
            if (string.IsNullOrWhiteSpace(lrc)) return null;
            return (lrc, string.IsNullOrWhiteSpace(tlyric) ? null : tlyric);
        }
        catch { return null; }
    }

    // ── 内部 ──

    /// <summary>公共 NeteaseCloudMusicApi 实例（播放直链兜底；实测 zm.wwoyun.cn / iwenwiki.com:3000 可用）</summary>
    private static readonly string[] PublicApiBases =
    {
        "https://zm.wwoyun.cn",
        "http://iwenwiki.com:3000",
    };

    private HttpRequestMessage Build(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_cookie))
            req.Headers.TryAddWithoutValidation("Cookie", _cookie);
        return req;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url)
    {
        try
        {
            using var resp = await _http.SendAsync(Build(HttpMethod.Get, url));
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(json);
        }
        catch { return null; }
    }

    /// <summary>解析歌单 JSON（兼容 coverImgUrl/picUrl 两种封面字段）</summary>
    private static OnlinePlaylist ParsePlaylist(JsonElement pl, string coverField = "coverImgUrl")
    {
        string? cover = null;
        if (pl.TryGetProperty(coverField, out var c1)) cover = c1.GetString();
        if (cover == null && pl.TryGetProperty("coverImgUrl", out var c2)) cover = c2.GetString();
        if (cover == null && pl.TryGetProperty("picUrl", out var c3)) cover = c3.GetString();
        return new OnlinePlaylist
        {
            Id = pl.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "",
            Platform = "netease",
            Name = pl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            CoverUrl = CoverWithSize(ToHttps(cover), 500),
            Description = pl.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            SongCount = pl.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0,
        };
    }

    /// <summary>解析标准歌曲 JSON（兼容 ar/artists、al/album、dt/duration 字段变体 + privilege 权益信息）</summary>
    private OnlineSong? ParseSong(JsonElement s)
    {
        try
        {
            if (!s.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null)
                return null;
            var artists = new List<string>();
            if (s.TryGetProperty("ar", out var ar))
                CollectArtistNames(ar, artists);
            if (artists.Count == 0 && s.TryGetProperty("artists", out var ats))
                CollectArtistNames(ats, artists);

            string? album = null, cover = null;
            if (s.TryGetProperty("al", out var al))
            {
                if (al.TryGetProperty("name", out var aln)) album = aln.GetString();
                if (al.TryGetProperty("picUrl", out var pic)) cover = pic.GetString();
            }
            if (album == null && s.TryGetProperty("album", out var alb))
            {
                if (alb.TryGetProperty("name", out var albn)) album = albn.GetString();
                if (cover == null && alb.TryGetProperty("picUrl", out var albp)) cover = albp.GetString();
            }

            long dur = 0;
            if (s.TryGetProperty("dt", out var dt)) dur = dt.GetInt64();
            if (dur == 0 && s.TryGetProperty("duration", out var du)) dur = du.GetInt64();
            if (dur == 0 && s.TryGetProperty("durationMs", out var dm)) dur = dm.GetInt64();

            // 权益信息：fee=1 VIP / fee=4 数字专辑需购买；st=-1 下架/无版权
            bool isVip = false, blocked = false;
            if (s.TryGetProperty("privilege", out var pv) && pv.ValueKind == JsonValueKind.Object)
            {
                if (pv.TryGetProperty("fee", out var fee) && fee.TryGetInt32(out var feeV))
                    isVip = feeV is 1 or 4;
                if (pv.TryGetProperty("st", out var st) && st.TryGetInt32(out var stV))
                    blocked = stV == -1;
            }

            var song = new OnlineSong
            {
                Id = idEl.GetInt64().ToString(),
                Platform = "netease",
                PlatformName = "网易云音乐",
                Title = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Artist = string.Join(" / ", artists),
                Album = album ?? string.Empty,
                DurationMs = dur,
                CoverUrl = CoverWithSize(ToHttps(cover), 1000),
            };
            song.Internal ??= new Dictionary<string, object>();
            song.Internal["Vip"] = isVip;
            song.Internal["Blocked"] = blocked;
            song.Internal["Liked"] = false;
            return song;
        }
        catch { return null; }
    }

    private static void CollectArtistNames(JsonElement arr, List<string> artists)
    {
        if (arr.ValueKind != JsonValueKind.Array) return;
        foreach (var a in arr.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name)) artists.Add(name);
        }
    }

    /// <summary>http 明文统一转 https（WinUI/Android 拒绝明文；网易云 CDN 支持 https）</summary>
    private static string? ToHttps(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring(7)
            : url;
    }

    /// <summary>网易云图片服务按尺寸裁剪（?param=WxH），节省流量与内存。
    /// 已带 ?param= 原样返回；已带其他查询参数时用 &amp; 追加，避免拼出非法双问号 URL（如 ...?x=1?param=...），
    /// 否则宿主按该 URL 下载失败会回落占位图/陈旧图，表现为"错误封面"。</summary>
    private static string? CoverWithSize(string? url, int size)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.Contains("?param=", StringComparison.Ordinal)) return url;
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}param={size}y{size}";
    }
}
