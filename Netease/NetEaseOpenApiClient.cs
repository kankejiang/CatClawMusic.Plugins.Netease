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

    /// <summary>登录态彻底失效（续期失败，需用户重新登录）。UI 订阅后提示。</summary>
    public event Action? LoginExpired;

    /// <summary>上次通知 UI「登录过期」的时间（30 秒节流，避免接口风暴时连弹）</summary>
    private DateTime _lastExpiredRaisedUtc = DateTime.MinValue;

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
    private List<OnlineSong>? _likedSongs;

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

    /// <summary>是否已登录：必须包含 MUSIC_U 登录 Cookie（仅匿名 Cookie 不算登录）</summary>
    public bool HasCookie => !string.IsNullOrWhiteSpace(_cookie)
        && _cookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase);

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
    /// 仅当包含 MUSIC_U 登录 Cookie 时才接受（防止未登录时的匿名 Cookie 覆盖已登录状态）。
    /// </summary>
    public Task ApplyLoginCookieAsync(string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie)
            && cookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase))
        {
            _cookie = cookie;
            PersistCookie(cookie);
            // 新账号登录：清空上一个账号的派生缓存
            _userId = null;
            _likedPlaylistId = null;
            _likedSongIds = null;
            _likedSongs = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>已登录账号昵称（/api/nuser/account/get 实名验证；失败回退本地缓存）</summary>
    public async Task<string?> GetAccountNameAsync()
    {
        if (!HasCookie) return null;
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
        _likedSongs = null;
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

    /// <summary>
    /// 登录态续期：POST /api/login/token/refresh（老明文 web 接口，带当前 Cookie 换发新 Cookie）。
    /// 仿 api-enhanced 的 login_refresh 实现：响应 Set-Cookie 中含新 MUSIC_U 即续期成功，
    /// 更新内存 <see cref="_cookie"/> 并持久化。会话有效期内定期/失效时调用可链式续期，
    /// 实现「一次登录长期有效」（官方 App 的静默续期同理）。
    /// </summary>
    public async Task<bool> RefreshLoginTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_cookie)) return false;
        try
        {
            using var req = Build(HttpMethod.Post, "https://music.163.com/api/login/token/refresh");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;

            var sets = CollectSetCookies(resp);
            if (sets.Count == 0) return false;
            var merged = string.Join("; ", sets);
            // 新会话必须仍含 MUSIC_U（登录凭据），否则只是匿名 Cookie 回显，判定失败
            if (!merged.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase))
                return false;

            _cookie = merged;
            PersistCookie(merged);
            // 同一账号会话续期：保留 userId/红心等派生态（不换账号，无需清空前缀缓存）
            return true;
        }
        catch { return false; }
    }

    /// <summary>从响应头提取 Set-Cookie 的 name=value 段（丢弃 Path=/ 等属性），无则空列表</summary>
    private static List<string> CollectSetCookies(HttpResponseMessage resp)
    {
        var list = new List<string>();
        if (resp.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var v in values)
            {
                var first = v.Split(';')[0].Trim();
                if (first.Length > 0) list.Add(first);
            }
        }
        return list;
    }

    /// <summary>通知 UI 登录已过期（30 秒节流，防接口风暴连弹）</summary>
    private void RaiseLoginExpired()
    {
        if ((DateTime.UtcNow - _lastExpiredRaisedUtc).TotalSeconds < 30) return;
        _lastExpiredRaisedUtc = DateTime.UtcNow;
        try { LoginExpired?.Invoke(); } catch { }
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
                    CoverUrl = CoverWithSize(ToHttps(t.TryGetProperty("coverImgUrl", out var c) ? c.GetString() : null), 300),
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

    /// <summary>歌单内歌曲。
    /// 优先 eapi 客户端身份（对齐 api-enhanced / NeteaseCloudMusicApi 实现）：
    /// ① /eapi/v6/playlist/detail 传 n=100000（网页版身份 n 上限 1000，客户端可拿全量曲目）
    ///    —— tracks 全量时直接内存分页；tracks 被 cap（少于 trackIds 总数）时
    ///    按 trackIds 切片 + /eapi/v3/song/detail 批量补全（playlist_track_all 同款两段式）；
    /// ② eapi 失败回退网页版 /api/v6/playlist/detail?n=1000（超千首歌单会被截断）。</summary>
    public async Task<List<OnlineSong>?> GetPlaylistSongsAsync(OnlinePlaylist playlist, int page = 1, int pageSize = 200)
    {
        if (string.IsNullOrWhiteSpace(playlist.Id)) return null;
        if (long.TryParse(playlist.Id, out var plId))
        {
            var songs = await GetPlaylistSongsViaEapiAsync(plId, page, pageSize);
            if (songs != null) return songs;
        }
        return await GetPlaylistSongsViaWebAsync(playlist, page, pageSize);
    }

    /// <summary>eapi v6 歌单详情取曲目（n=100000 全量；tracks 不全时走 trackIds + v3/song/detail 批量）。
    /// 返回 null = eapi 整体失败（调用方回退网页版）；空列表 = 歌单为空/页码越界（有效结果）。</summary>
    private async Task<List<OnlineSong>?> GetPlaylistSongsViaEapiAsync(long playlistId, int page, int pageSize)
    {
        try
        {
            var raw = await NeteaseEapi.RequestAsync(_http, "/eapi/v6/playlist/detail", new Dictionary<string, object>
            {
                ["id"] = playlistId,
                ["n"] = 100000,
                ["s"] = 8,
            }, _cookie);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("playlist", out var pl) || pl.ValueKind != JsonValueKind.Object)
                return null;

            List<OnlineSong>? all = null;
            if (pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                all = new List<OnlineSong>();
                foreach (var s in tracks.EnumerateArray())
                {
                    var song = ParseSong(s);
                    if (song != null) all.Add(song);
                }
            }
            var trackIds = new List<long>();
            if (pl.TryGetProperty("trackIds", out var tids) && tids.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tids.EnumerateArray())
                    if (t.TryGetProperty("id", out var tid) && tid.TryGetInt64(out var tv))
                        trackIds.Add(tv);
            }

            if (all != null && (trackIds.Count == 0 || all.Count >= trackIds.Count))
            {
                var start = (page - 1) * pageSize;
                if (start >= all.Count) return new List<OnlineSong>();
                return all.Skip(start).Take(pageSize).ToList();
            }
            if (trackIds.Count > 0)
            {
                var start = (page - 1) * pageSize;
                if (start >= trackIds.Count) return new List<OnlineSong>();
                return await FetchSongsByIdsAsync(trackIds.Skip(start).Take(pageSize).ToList());
            }
            return all;
        }
        catch { return null; }
    }

    /// <summary>网页版 v6 歌单详情取曲目（浏览器身份，n=1000 上限；eapi 失败时的兜底）</summary>
    private async Task<List<OnlineSong>?> GetPlaylistSongsViaWebAsync(OnlinePlaylist playlist, int page, int pageSize)
    {
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

    /// <summary>按 id 批量取歌曲详情（/eapi/v3/song/detail，c=[{"id":..}]，单批上限 1000）</summary>
    private async Task<List<OnlineSong>> FetchSongsByIdsAsync(List<long> ids)
    {
        var result = new List<OnlineSong>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var raw = await NeteaseEapi.FetchSongDetailRawAsync(_http, chunk, _cookie);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("songs", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in list.EnumerateArray())
                    {
                        var song = ParseSong(s);
                        if (song != null) result.Add(song);
                    }
                }
            }
            catch { }
        }
        return result;
    }

    // ════════════════ 私人漫游 / 每日推荐 ════════════════

    /// <summary>私人漫游场景模式码（须走 mode=SCENE_RCMD&amp;submode=&lt;code&gt;，不能直接当 mode 传）。</summary>
    public static readonly System.Collections.Generic.HashSet<string> FmSceneCodes = new()
    {
        "LATE_NIGHT_EMO", "EXERCISE", "SLEEP_HELP", "RELAX",
        "HAPPINESS", "LYRICAL", "CURE", "FOCUS",
        "ROMANTIC", "RHYTHM_BLUES", "RAINY", "GAMES",
        "RAP", "K_POP", "ORIGINAL_MUSICIAL", "ELECTRONIC",
        "COMMUTE", "BATH", "COFFEE_SHOP", "ROCK",
        "INSPIRATIONAL", "CHINESE", "EUROPE_AMERICA", "CANTONESE",
        "DJ", "CLASSIC", "LIGHT_MUSIC", "CHINESE_STYLE",
        "FOLK", "ACG", "CLASSICAL", "JAZZ",
        "JAPANESE", "WORLD", "FRENCH", "BLUES",
    };

    /// <summary>
    /// 私人漫游（随机推荐 /api/v1/radio/get）。
    /// 该接口一次通常只返回 1 首（网易云私人 FM 模型），故循环拉取并去重，
    /// 直到凑齐 <paramref name="num"/> 首或达到安全上限，模拟官方"无限电台"的首批缓冲。
    /// </summary>
    /// <param name="mode">推荐模式（DEFAULT/FAMILIAR/EXPLORE 或场景码如 ROCK）；空或 DEFAULT = 默认</param>
    public async Task<List<OnlineSong>?> GetPrivateFmAsync(int num = 10, string? mode = null)
    {
        try
        {
            var collected = new List<OnlineSong>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int attempts = 0;
            int maxAttempts = num * 3; // 安全上限，避免接口异常时死循环
            // mode 参数：DEFAULT 或空不带 query（保持原生行为）；场景码走 SCENE_RCMD+submode；其余（aidj/FAMILIAR/EXPLORE）直接作为 mode
            var urlBase = "https://music.163.com/api/v1/radio/get";
            string query;
            if (string.IsNullOrWhiteSpace(mode) || mode == "DEFAULT")
                query = "";
            else if (FmSceneCodes.Contains(mode))
                query = $"?mode=SCENE_RCMD&submode={Uri.EscapeDataString(mode)}";
            else
                query = $"?mode={Uri.EscapeDataString(mode)}";
            while (collected.Count < num && attempts < maxAttempts)
            {
                attempts++;
                using var doc = await GetJsonAsync(urlBase + query);
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
    /// 优先级：① eapi interface.music.163.com（桌面客户端伪装，不同源限流，最稳，NeteaseEapi.cs）
    /// → ② 官方 music.163.com/api（cookie，可能限流空 body）→ ③ 公共 zm.wwoyun.cn / iwenwiki。
    /// 全部失败不影响播放，沿用 FM 原始封面。
    /// </summary>
    private async Task CorrectFmMetadataAsync(List<OnlineSong> songs)
    {
        if (songs == null || songs.Count == 0) return;
        // ① eapi 桌面客户端接口（interface.music.163.com，与 music.163.com 不同源限流，实测最稳）
        var eapiIds = songs.Where(s => long.TryParse(s.Id, out _)).Select(s => long.Parse(s.Id)).ToArray();
        if (eapiIds.Length > 0)
        {
            var raw = await NeteaseEapi.FetchSongDetailRawAsync(_http, eapiIds, _cookie);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("songs", out var list) && list.ValueKind == JsonValueKind.Array
                        && ApplySongCoverCorrection(songs, list))
                        return;
                }
                catch { /* eapi 失败继续兜底 */ }
            }
        }
        // ② 官方 music.163.com/api/song/detail（依赖用户 Cookie；可能限流/被风控返回空 body）
        if (await TryCorrectSongCoversAsync(songs,
                $"https://music.163.com/api/song/detail?ids=[{string.Join(",", songs.Select(s => s.Id))}]",
                expectArrayKey: "songs"))
            return;
        // ③ 公共 NeteaseCloudMusicApi 兜底（zm.wwoyun.cn / iwenwiki.com:3000 不需 Cookie）
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

    /// <summary>
    /// 「我喜欢的音乐」完整歌曲列表（需登录；未登录返回空列表）。
    /// 首页 tracks 最多返回 1000 首；超过部分按 trackIds 逐批补齐，供宿主「我喜欢的」歌单合并展示。
    /// </summary>
    public async Task<List<OnlineSong>> GetLikedSongsAsync()
    {
        if (_likedSongs != null) return _likedSongs;
        var list = new List<OnlineSong>();
        var pid = await GetLikedPlaylistIdAsync();
        if (pid == null) { _likedSongs = list; return list; }
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/v6/playlist/detail?id={pid}&n=1000&s=0");
            if (doc != null && doc.RootElement.TryGetProperty("playlist", out var pl) &&
                pl.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tracks.EnumerateArray())
                    {
                        var s = ParseSong(t);
                        if (s == null) continue;
                        if (!seen.Add(s.Id)) continue;
                        s.Internal ??= new Dictionary<string, object>();
                        s.Internal["Liked"] = true;
                        list.Add(s);
                    }
                }
                // 超过 1000 首：trackIds 里有而 tracks 缺失的，批量 song/detail 补齐
                if (pl.TryGetProperty("trackIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    var missing = new List<long>();
                    foreach (var idEl in ids.EnumerateArray())
                    {
                        if (idEl.ValueKind == JsonValueKind.Null) continue;
                        long idv = 0;
                        if (idEl.TryGetProperty("id", out var i0) && i0.ValueKind != JsonValueKind.Null)
                            idv = i0.GetInt64();
                        else if (idEl.TryGetInt64(out var i1)) idv = i1;
                        if (idv > 0 && seen.Add(idv.ToString())) missing.Add(idv);
                    }
                    if (missing.Count > 0)
                    {
                        var extra = await FetchSongsByIdsAsync(missing);
                        foreach (var s in extra)
                        {
                            s.Internal ??= new Dictionary<string, object>();
                            s.Internal["Liked"] = true;
                        }
                        list.AddRange(extra);
                    }
                }
            }
        }
        catch { }
        _likedSongs = list;
        return list;
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
                _likedSongs = null; // 完整列表缓存失效，下次按最新红心状态重拉
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

        // 方案3：高品/无损受限 → 逐步降档再试 enhance（320k → 128k 标准档，规避部分 VIP/风控提升可播性）
        if (br >= 320000)
        {
            if (br == 999000)
            {
                enhanceUrl = await GetEnhanceUrlAsync(songId, 320000);
                if (!string.IsNullOrWhiteSpace(enhanceUrl)) return enhanceUrl;
            }
            enhanceUrl = await GetEnhanceUrlAsync(songId, 128000);
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

    /// <summary>歌词（LRC + 翻译 + 罗马音）。
    /// 优先级：① eapi /eapi/song/lyric/v1（interface.music.163.com 桌面客户端接口，与 music.163.com 不同源限流，
    /// Lyrico-Plugins 同款实现，实测最稳）→ ② 官方 /api/song/lyric（tv=1 译文 rv=1 罗马音）→ ③ 公共 NeteaseCloudMusicApi 实例。
    /// 官方接口对部分歌曲（风控/匿名限制/新歌）返回空 lrc 时由后两级顶上。</summary>
    public async Task<(string? Lrc, string? TLrc, string? RLrc)?> GetLyricsWithRomaAsync(string songId)
    {
        if (string.IsNullOrWhiteSpace(songId)) return null;
        // ① eapi 桌面客户端接口（interface.music.163.com，不同源限流；无需匿名会话，实测 song id 可直取）
        if (long.TryParse(songId, out var eapiId))
        {
            var result = await NeteaseEapi.FetchLyricAsync(_http, eapiId, _cookie);
            if (result != null) return result;
        }
        // ② 官方 /api/song/lyric（lv 原版 tv 译文 rv 罗马音）
        var official = await FetchLyricFromOfficialAsync(songId);
        if (official != null) return official;
        // 兜底 ③：公共 NeteaseCloudMusicApi 实例
        foreach (var api in PublicApiBases)
        {
            try
            {
                using var doc = await GetJsonAsync($"{api}/lyric?id={songId}");
                if (doc == null) continue;
                string? lrc = null, tlyric = null, rlrc = null;
                if (doc.RootElement.TryGetProperty("lrc", out var ln) && ln.TryGetProperty("lyric", out var lt))
                    lrc = lt.GetString();
                if (doc.RootElement.TryGetProperty("tlyric", out var tn) && tn.TryGetProperty("lyric", out var tt))
                    tlyric = tt.GetString();
                if (doc.RootElement.TryGetProperty("romalrc", out var rn) && rn.TryGetProperty("lyric", out var rt))
                    rlrc = rt.GetString();
                if (!string.IsNullOrWhiteSpace(lrc))
                    return (lrc,
                        string.IsNullOrWhiteSpace(tlyric) ? null : tlyric,
                        string.IsNullOrWhiteSpace(rlrc) ? null : rlrc);
            }
            catch { }
        }
        return null;
    }

    /// <summary>旧接口：歌词（LRC + 翻译，无罗马音），转发到三流版本丢弃 RLrc。</summary>
    public async Task<(string? Lrc, string? TLrc)?> GetLyricsAsync(string songId)
    {
        var r = await GetLyricsWithRomaAsync(songId);
        if (r == null) return null;
        return (r.Value.Lrc, r.Value.TLrc);
    }

    /// <summary>官方 /api/song/lyric 取词（lv=1 原版，tv=1 译文，rv=1 罗马音）</summary>
    private async Task<(string? Lrc, string? TLrc, string? RLrc)?> FetchLyricFromOfficialAsync(string songId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/song/lyric?id={songId}&lv=1&tv=1&rv=1");
            if (doc == null) return null;
            string? lrc = null, tlyric = null, rlrc = null;
            if (doc.RootElement.TryGetProperty("lrc", out var ln) && ln.TryGetProperty("lyric", out var lt))
                lrc = lt.GetString();
            if (doc.RootElement.TryGetProperty("tlyric", out var tn) && tn.TryGetProperty("lyric", out var tt))
                tlyric = tt.GetString();
            if (doc.RootElement.TryGetProperty("romalrc", out var rn) && rn.TryGetProperty("lyric", out var rt))
                rlrc = rt.GetString();
            if (string.IsNullOrWhiteSpace(lrc)) return null;
            return (lrc,
                string.IsNullOrWhiteSpace(tlyric) ? null : tlyric,
                string.IsNullOrWhiteSpace(rlrc) ? null : rlrc);
        }
        catch { return null; }
    }

    // ════════════════ 相似歌曲 / 历史推荐 / MV（weapi 加密接口）════════════════

    /// <summary>相似歌曲（/weapi/v1/discovery/simiSong，"喜欢这首歌的人还喜欢"）</summary>
    public async Task<List<OnlineSong>> GetSimilarSongsAsync(string songId, int limit = 20)
    {
        try
        {
            var raw = await NeteaseWeapi.RequestAsync(_http, "/api/v1/discovery/simiSong",
                new Dictionary<string, object> { ["songid"] = songId, ["limit"] = limit, ["offset"] = 0 }, _cookie);
            if (string.IsNullOrWhiteSpace(raw)) return new List<OnlineSong>();
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch { return new List<OnlineSong>(); }
    }

    /// <summary>历史每日推荐（/weapi/discovery/recommend/songs/history/recent，可回味历史日推）</summary>
    public async Task<List<OnlineSong>> GetHistoryRecommendSongsAsync()
    {
        try
        {
            var raw = await NeteaseWeapi.RequestAsync(_http, "/api/discovery/recommend/songs/history/recent",
                new Dictionary<string, object>(), _cookie);
            if (string.IsNullOrWhiteSpace(raw)) return new List<OnlineSong>();
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("dailySongs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return new List<OnlineSong>();
            var list = new List<OnlineSong>();
            foreach (var s in songs.EnumerateArray())
            {
                var song = ParseSong(s);
                if (song != null) list.Add(song);
            }
            return list;
        }
        catch { return new List<OnlineSong>(); }
    }

    /// <summary>MV 播放直链（/weapi/song/enhance/play/mv/url；r=清晰度 1080 默认）</summary>
    public async Task<string?> GetMvUrlAsync(string mvId, int r = 1080)
    {
        if (string.IsNullOrWhiteSpace(mvId)) return null;
        try
        {
            var raw = await NeteaseWeapi.RequestAsync(_http, "/api/song/enhance/play/mv/url",
                new Dictionary<string, object> { ["id"] = mvId, ["r"] = r }, _cookie);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("url", out var u))
                {
                    var url = u.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return ToHttps(url);
                }
            }
        }
        catch { }
        return null;
    }

    // ════════════════ 搜索联想 / 相似歌单 / 评论（个性化与内容延展）════════════════

    /// <summary>搜索建议（/api/search/suggest/web，输入联想）</summary>
    public async Task<List<SearchSuggestion>> GetSearchSuggestAsync(string keyword, int limit = 8)
    {
        var list = new List<SearchSuggestion>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;
        try
        {
            var url = $"https://music.163.com/api/search/suggest/web?s={Uri.EscapeDataString(keyword)}&limit={limit}";
            using var doc = await GetJsonAsync(url);
            if (doc == null) return list;
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return list;
            if (result.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in songs.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var artists = new List<string>();
                    if (s.TryGetProperty("artists", out var ats)) CollectArtistNames(ats, artists);
                    var word = artists.Count > 0 ? $"{name} - {string.Join(" / ", artists)}" : name;
                    if (list.Count < limit) list.Add(new SearchSuggestion { Word = word, Type = "song" });
                }
            }
            if (result.TryGetProperty("albums", out var als) && als.ValueKind == JsonValueKind.Array)
                foreach (var it in als.EnumerateArray())
                {
                    var name = it.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && list.Count < limit)
                        list.Add(new SearchSuggestion { Word = name, Type = "album" });
                }
            if (result.TryGetProperty("artists", out var rts) && rts.ValueKind == JsonValueKind.Array)
                foreach (var it in rts.EnumerateArray())
                {
                    var name = it.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && list.Count < limit)
                        list.Add(new SearchSuggestion { Word = name, Type = "artist" });
                }
        }
        catch { }
        return list;
    }

    /// <summary>热门搜索词（/weapi/hotsearchlist/get）</summary>
    public async Task<List<string>> GetSearchHotAsync(int limit = 10)
    {
        var list = new List<string>();
        try
        {
            var raw = await NeteaseWeapi.RequestAsync(_http, "/api/hotsearchlist/get", new Dictionary<string, object>(), _cookie);
            if (string.IsNullOrWhiteSpace(raw)) return list;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var it in data.EnumerateArray())
            {
                var word = it.TryGetProperty("searchWord", out var w) ? w.GetString() : null;
                if (!string.IsNullOrWhiteSpace(word)) list.Add(word);
                if (list.Count >= limit) break;
            }
        }
        catch { }
        return list;
    }

    /// <summary>相似歌单（/api/playlist/similar，相关歌单）</summary>
    public async Task<List<SimilarPlaylistInfo>> GetSimilarPlaylistsAsync(string playlistId, int limit = 10)
    {
        var list = new List<SimilarPlaylistInfo>();
        if (string.IsNullOrWhiteSpace(playlistId)) return list;
        try
        {
            var url = $"https://music.163.com/api/playlist/similar?id={playlistId}";
            using var doc = await GetJsonAsync(url);
            if (doc == null) return list;
            JsonElement body = default;
            if (doc.RootElement.TryGetProperty("playlists", out body)
                || (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("playlists", out body)))
            {
                if (body.ValueKind != JsonValueKind.Array) return list;
                foreach (var p in body.EnumerateArray())
                {
                    var item = ParseSimilarPlaylist(p);
                    if (item != null) list.Add(item);
                    if (list.Count >= limit) break;
                }
            }
        }
        catch { }
        return list;
    }

    private static SimilarPlaylistInfo? ParseSimilarPlaylist(JsonElement p)
    {
        try
        {
            var id = p.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null ? idEl.GetInt64().ToString() : "";
            if (string.IsNullOrWhiteSpace(id)) return null;
            var item = new SimilarPlaylistInfo
            {
                Id = id,
                Name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                CoverUrl = CoverWithSize(ToHttps(p.TryGetProperty("coverImgUrl", out var c) ? c.GetString() : null), 300),
                SongCount = p.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0,
                PlayCount = p.TryGetProperty("playCount", out var pc) && pc.TryGetInt32(out var pcv) ? pcv : 0,
            };
            if (p.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object &&
                cr.TryGetProperty("nickname", out var nn) && nn.ValueKind == JsonValueKind.String)
                item.Creator = nn.GetString() ?? "";
            return item;
        }
        catch { return null; }
    }

    /// <summary>热门评论（/api/v1/resource/hot/comments/R_SO_4_{id}）</summary>
    public Task<List<SongComment>> GetSongHotCommentsAsync(string songId, int limit = 20)
        => GetCommentsAsync(songId, limit, 0, hot: true);

    /// <summary>评论列表（/api/v1/resource/comments/R_SO_4_{id}，offset 翻页）</summary>
    public Task<List<SongComment>> GetSongCommentsAsync(string songId, int limit = 20, int offset = 0)
        => GetCommentsAsync(songId, limit, offset, hot: false);

    private async Task<List<SongComment>> GetCommentsAsync(string songId, int limit, int offset, bool hot)
    {
        var list = new List<SongComment>();
        if (string.IsNullOrWhiteSpace(songId)) return list;
        var rid = $"R_SO_4_{songId}";
        var url = hot
            ? $"https://music.163.com/api/v1/resource/hot/comments/{rid}?rid={rid}&limit={limit}"
            : $"https://music.163.com/api/v1/resource/comments/{rid}?rid={rid}&limit={limit}&offset={offset}";
        try
        {
            using var doc = await GetJsonAsync(url);
            if (doc == null) return list;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return list;
            var arrField = hot ? "hotComments" : "comments";
            if (!data.TryGetProperty(arrField, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var c in arr.EnumerateArray())
            {
                var item = new SongComment
                {
                    Id = c.TryGetProperty("commentId", out var cid) ? cid.GetInt64() : 0,
                    Content = c.TryGetProperty("content", out var ct) ? ct.GetString() ?? "" : "",
                    Time = c.TryGetProperty("time", out var tm) ? tm.GetInt64() : 0,
                    LikedCount = c.TryGetProperty("likedCount", out var lk) && lk.TryGetInt32(out var lkv) ? lkv : 0,
                };
                if (c.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    item.User = u.TryGetProperty("nickname", out var nn) ? nn.GetString() ?? "" : "";
                    item.AvatarUrl = CoverWithSize(ToHttps(u.TryGetProperty("avatarUrl", out var av) ? av.GetString() : null), 120);
                }
                if (item.Content.Length > 0 || item.User.Length > 0)
                    list.Add(item);
            }
        }
        catch { }
        return list;
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
            var doc = await SendJsonAsync(HttpMethod.Get, url);
            // 携带了登录 Cookie 却被判定"需要登录"(code=301)：会话已过期 →
            // 尝试静默续期（/api/login/token/refresh）一次并重试；续期失败再提示用户重登。
            if (doc != null && IsLoginRequired(doc.RootElement)
                && !string.IsNullOrWhiteSpace(_cookie))
            {
                if (await RefreshLoginTokenAsync())
                {
                    doc = await SendJsonAsync(HttpMethod.Get, url);
                }
                else
                {
                    RaiseLoginExpired();
                }
            }
            return doc;
        }
        catch { return null; }
    }

    private async Task<JsonDocument?> SendJsonAsync(HttpMethod method, string url)
    {
        using var resp = await _http.SendAsync(Build(method, url));
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(json); } catch { return null; }
    }

    /// <summary>网易云业务错误码 301 = 需要登录（Cookie 缺失或会话已失效）</summary>
    private static bool IsLoginRequired(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("code", out var code)
           && code.ValueKind == JsonValueKind.Number
           && code.TryGetInt32(out var c)
           && c == 301;

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
            CoverUrl = CoverWithSize(ToHttps(cover), 300),
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
            song.Internal["MvId"] = GetMvId(s);
            return song;
        }
        catch { return null; }
    }

    /// <summary>解析 MV id（优先 mv.id，其次顶层 mvid；无则 0）</summary>
    private static long GetMvId(JsonElement s)
    {
        if (s.TryGetProperty("mv", out var mv) && mv.ValueKind == JsonValueKind.Object &&
            mv.TryGetProperty("id", out var mid) && mid.ValueKind != JsonValueKind.Null && mid.TryGetInt64(out var v1))
            return v1;
        if (s.TryGetProperty("mvid", out var mvid) && mvid.ValueKind != JsonValueKind.Null && mvid.TryGetInt64(out var v2))
            return v2;
        return 0;
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
