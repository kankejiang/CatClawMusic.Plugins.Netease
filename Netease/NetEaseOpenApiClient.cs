using System.Security.Cryptography;
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
    private readonly Dictionary<string, (string Url, string? Ext, string? Level, DateTime ExpireAt)> _urlCache = new();
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
            // 统一规范化（去重/去属性/按名合并），扫码返回的是 Set-Cookie 串，可能带 Path 等属性
            _cookie = string.Join("; ", ParseCookieJar(cookie).Select(kv => $"{kv.Key}={kv.Value}"));
            PersistCookie(_cookie);
            // 新账号登录：清空上一个账号的派生缓存
            _userId = null;
            _likedPlaylistId = null;
            _likedSongIds = null;
            _likedSongs = null;
            NeteaseLoginLog.Write($"登录成功：已保存会话（{_cookie.Length} 字符，{ParseCookieJar(_cookie).Count} 个字段）");
            // 登录后立刻起定时续期（应用态会话支持续期，网页版会话不支持）
            StartAutoRefresh();
            // 另起一次"登录后立即探活"：让 netease_login.log 尽快给出"这个会话能否续期"的结论，
            // 同时把服务端可能新下发的会话落盘（失败不影响本次登录成功）。
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(3000).ConfigureAwait(false); await RefreshLoginTokenAsync().ConfigureAwait(false); }
                catch { }
            });
        }
        else
        {
            NeteaseLoginLog.Write("收到的登录 Cookie 不含 MUSIC_U，已忽略（避免匿名 Cookie 覆盖已登录状态）");
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
        // 兜底一：account/get 实测恒返回 {code:200, account:null, profile:null}（该接口对当前客户端形态
        // 已不可用），改用 user/detail 取昵称，避免昵称永远只能靠本地缓存文件撑着。
        try
        {
            var uid = await GetUserIdAsync();
            if (uid is long u && u > 0)
            {
                using var doc = await GetJsonAsync($"https://music.163.com/api/v1/user/detail/{u}");
                if (doc != null && doc.RootElement.TryGetProperty("profile", out var p)
                    && p.TryGetProperty("nickname", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                {
                    var nickname = n.GetString();
                    if (!string.IsNullOrWhiteSpace(nickname))
                    {
                        try { File.WriteAllText(NicknameFilePath, nickname); } catch { }
                        return nickname;
                    }
                }
            }
        }
        catch { }
        // 兜底二：读取登录时缓存的昵称
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
        NeteaseLoginLog.Write("已退出登录（本地会话与昵称/uid 缓存已清除）");
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
    /// 登录态续期：走 **eapi** 通道 POST /eapi/login/token/refresh。
    /// <para>
    /// 为什么改成 eapi：官方 login_refresh 实现（api-enhanced/module/login_refresh.js）用
    /// <c>createOption(query)</c>，而 <c>util/config.json</c> 里 <c>encrypt=true</c> → crypto 回落到 **eapi**，
    /// 即请求实际发往 <c>{eapiDomain}/eapi/login/token/refresh</c>。历史实现是**明文** POST
    /// <c>music.163.com/api/login/token/refresh</c> —— 实测**恒返回 code 301**（即便换用完全有效的
    /// Cookie，甚至补上 csrf_token 也一样），因为该接口只接受加密通道；于是"静默续期"从未生效，
    /// 登录态只能等会话自然过期（用户反馈"网页版登录容易过期"）。
    /// </para>
    /// <para>
    /// 另注：**网页版登录的 MUSIC_U 属于 Web 会话，本接口不认**（实测有效 web cookie 亦 301；
    /// 同一条 eapi 通道调 /eapi/v3/song/detail 却正常返回，证明通道与 Cookie 都没问题）。
    /// 只有扫码/手机号这类**应用态会话**才可续期，故登录入口已改为扫码（见 GetQrKeyAsync）。
    /// </para>
    /// </summary>
    public async Task<bool> RefreshLoginTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_cookie)) return false;
        try
        {
            var res = await NeteaseEapi.RequestDetailedAsync(_http, "/eapi/login/token/refresh",
                new Dictionary<string, object>(), _cookie, rawCipherResponse: true).ConfigureAwait(false);
            var code = ReadCode(res.Body);
            var gotNewSession = MergeSetCookies(res.SetCookies);
            if (code == 200)
            {
                if (gotNewSession)
                {
                    PersistCookie(_cookie!);
                    NeteaseLoginLog.Write($"续期成功：code=200，服务端轮换了会话（Set-Cookie {res.SetCookies.Count} 条，已保存新 MUSIC_U）");
                }
                else
                {
                    // 实测手机号验证码登录的会话走这里：code=200 表示会话被接受、有效期顺延，
                    // 服务端本次只是不轮换 MUSIC_U —— 这同样是成功，别误判成失败。
                    NeteaseLoginLog.Write($"续期成功：code=200（服务端返回 {res.SetCookies.Count} 条 Cookie，本次未轮换 MUSIC_U，会话有效期已顺延）");
                }
                return true;
            }
            NeteaseLoginLog.Write($"续期失败：code={(code?.ToString() ?? "无 body")}（Set-Cookie {res.SetCookies.Count} 条，含 MUSIC_U={gotNewSession}）");
            return false;
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"续期异常：{ex.Message}");
            return false;
        }
    }

    // ── 定时续期（续期时机之二；之一为插件初始化，之三为接口 301 重试）──

    private Timer? _refreshTimer;

    /// <summary>启动定时续期：30 秒后首检，之后每 6 小时一次。幂等。</summary>
    public void StartAutoRefresh()
    {
        if (_refreshTimer != null) return;
        _refreshTimer = new Timer(_ => _ = AutoRefreshTickAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
        NeteaseLoginLog.Write("定时续期已启动（30 秒后首检，之后每 6 小时）");
    }

    private async Task AutoRefreshTickAsync()
    {
        try
        {
            if (!HasCookie) return;
            await RefreshLoginTokenAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { NeteaseLoginLog.Write($"定时续期异常：{ex.Message}"); }
    }

    // ── 二维码登录（应用态会话；网页版会话无法续期，故登录入口改用扫码）──

    /// <summary>申请登录二维码 key（eapi /eapi/login/qrcode/unikey）；失败返回 null。</summary>
    public async Task<string?> GetQrKeyAsync(int type = 3)
    {
        try
        {
            var res = await NeteaseEapi.RequestDetailedAsync(_http, "/eapi/login/qrcode/unikey",
                new Dictionary<string, object> { ["type"] = type }, _cookie, rawCipherResponse: true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(res.Body)) return null;
            using var doc = JsonDocument.Parse(res.Body);
            if (doc.RootElement.TryGetProperty("unikey", out var k))
            {
                var key = k.GetString();
                if (!string.IsNullOrWhiteSpace(key)) return key;
            }
            NeteaseLoginLog.Write($"申请二维码 key 返回异常：{res.Body}");
        }
        catch (Exception ex) { NeteaseLoginLog.Write($"获取二维码 key 失败：{ex.Message}"); }
        return null;
    }

    /// <summary>
    /// 轮询扫码结果（eapi /eapi/login/qrcode/client/login）。
    /// code：801 等待扫码 / 802 已扫码待确认 / 803 授权成功 / 800 二维码过期 / 8821 等为环境风控。
    /// 803 时从 Set-Cookie（或 body.cookie 兜底）取出应用态会话串。
    /// </summary>
    public async Task<(int Code, string? Cookie)> CheckQrLoginAsync(string key, int type = 3)
    {
        try
        {
            var res = await NeteaseEapi.RequestDetailedAsync(_http, "/eapi/login/qrcode/client/login",
                new Dictionary<string, object> { ["key"] = key, ["type"] = type }, _cookie, rawCipherResponse: true).ConfigureAwait(false);
            var code = ReadCode(res.Body) ?? -1;
            string? cookie = null;
            if (code == 803)
            {
                if (res.SetCookies.Count > 0)
                    cookie = string.Join("; ", res.SetCookies
                        .Select(x => x.Split(';')[0].Trim())
                        .Where(x => x.Contains('=')));
                if (string.IsNullOrWhiteSpace(cookie)) cookie = ReadBodyCookie(res.Body);
                NeteaseLoginLog.Write(cookie == null
                    ? "扫码授权成功（803）但未取到 Cookie"
                    : $"扫码授权成功（803），已取到会话 Cookie（{cookie.Length} 字符）");
            }
            return (code, cookie);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"扫码轮询异常：{ex.Message}");
            return (-1, null);
        }
    }

    /// <summary>部分实现把登录 Cookie 放在 body.cookie 字段，做一层兜底</summary>
    private static string? ReadBodyCookie(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("cookie", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
        }
        catch { }
        return null;
    }

    // ── 手机号验证码登录（国内 App 默认方式；三步都走 weapi）──

    /// <summary>
    /// 第一步：发送短信验证码。<c>POST /api/sms/captcha/sent</c>
    /// （<c>secrete=music_middleuser_pclogin</c> 是官方"PC 端登录"场景标识，缺了会被拒）
    /// </summary>
    public async Task<(int Code, string? Message)> SendSmsCodeAsync(string phone, string countryCode = "86")
    {
        var p = (phone ?? string.Empty).Trim();
        if (p.Length < 6) return (-1, "请填写正确的手机号");
        var res = await NeteaseWeapi.RequestDetailedAsync(_http, "/api/sms/captcha/sent", new Dictionary<string, object>
        {
            ["ctcode"] = string.IsNullOrWhiteSpace(countryCode) ? "86" : countryCode.Trim(),
            ["secrete"] = "music_middleuser_pclogin",
            ["cellphone"] = p,
        }, null).ConfigureAwait(false);
        var code = ReadCode(res.Body) ?? -1;
        var message = ReadMessage(res.Body);
        NeteaseLoginLog.Write($"发送短信验证码（{Mask(p)}）：code={code}{(string.IsNullOrWhiteSpace(message) ? "" : $"，{message}")}");
        return (code, message);
    }

    /// <summary>第二步：校验验证码 <c>POST /api/sms/captcha/verify</c>（官方登录流程里登录前先校验一次，便于给出"验证码错误"）</summary>
    public async Task<(int Code, string? Message)> VerifySmsCodeAsync(string phone, string captcha, string countryCode = "86")
    {
        var res = await NeteaseWeapi.RequestDetailedAsync(_http, "/api/sms/captcha/verify", new Dictionary<string, object>
        {
            ["ctcode"] = string.IsNullOrWhiteSpace(countryCode) ? "86" : countryCode.Trim(),
            ["cellphone"] = (phone ?? string.Empty).Trim(),
            ["captcha"] = (captcha ?? string.Empty).Trim(),
        }, null).ConfigureAwait(false);
        var code = ReadCode(res.Body) ?? -1;
        var message = ReadMessage(res.Body);
        NeteaseLoginLog.Write($"校验短信验证码（{Mask(phone ?? "")}）：code={code}{(string.IsNullOrWhiteSpace(message) ? "" : $"，{message}")}");
        return (code, message);
    }

    /// <summary>
    /// 第三步：验证码登录。官方流程为 校验 → 登录，这里合并成一次调用。
    /// <c>POST /api/w/login/cellphone</c>（captcha 与 password 互斥）。
    /// </summary>
    public async Task<(int Code, string? Cookie, string? Message)> LoginWithSmsAsync(string phone, string captcha, string countryCode = "86")
    {
        var p = (phone ?? string.Empty).Trim();
        var c = (captcha ?? string.Empty).Trim();
        if (p.Length < 6) return (-1, null, "请填写正确的手机号");
        if (c.Length == 0) return (-1, null, "请填写短信验证码");

        var (verifyCode, verifyMsg) = await VerifySmsCodeAsync(p, c, countryCode).ConfigureAwait(false);
        if (verifyCode != 200)
            return (verifyCode == -1 ? -1 : (verifyCode is 502 or 503 ? 502 : verifyCode), null,
                    string.IsNullOrWhiteSpace(verifyMsg) ? "验证码校验失败" : verifyMsg);

        return await WeapiLoginAsync("/api/w/login/cellphone", new Dictionary<string, object>
        {
            ["type"] = "1",
            ["https"] = "true",
            ["phone"] = p,
            ["countrycode"] = string.IsNullOrWhiteSpace(countryCode) ? "86" : countryCode.Trim(),
            ["captcha"] = c,
            ["remember"] = "true",
            ["secureCaptcha"] = "",
        }, $"手机号验证码登录（{Mask(p)}）").ConfigureAwait(false);
    }

    /// <summary>weapi 登录类接口的共用收尾：读 code、取 Set-Cookie 里的会话、写日志</summary>
    private async Task<(int Code, string? Cookie, string? Message)> WeapiLoginAsync(
        string path, Dictionary<string, object> data, string logWhat)
    {
        try
        {
            var res = await NeteaseWeapi.RequestDetailedAsync(_http, path, data, null).ConfigureAwait(false);
            var code = ReadCode(res.Body) ?? -1;
            var message = ReadMessage(res.Body);
            var cookie = code == 200 ? ExtractLoginCookie(res.SetCookies, res.Body) : null;
            NeteaseLoginLog.Write($"{logWhat}：code={code}"
                + (code == 200
                    ? $"，取到 Cookie {cookie?.Length ?? 0} 字符，含 MUSIC_U={cookie?.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase) == true}"
                    : (string.IsNullOrWhiteSpace(message) ? "" : $"，{message}")));
            return (code, cookie, message);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"{logWhat} 异常：{ex.Message}");
            return (-1, null, ex.Message);
        }
    }

    /// <summary>登录响应里取会话：优先 Set-Cookie，其次 body.cookie 兜底</summary>
    private static string? ExtractLoginCookie(IReadOnlyList<string> sets, string? body)
    {
        if (sets.Count > 0)
        {
            var joined = string.Join("; ", sets.Select(x => x.Split(';')[0].Trim()).Where(x => x.Contains('=')));
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }
        return ReadBodyCookie(body);
    }

    // ── 邮箱 + 密码登录（手机号密码登录见下）──

    /// <summary>
    /// 账号密码登录。手机号走 <c>/api/w/login/cellphone</c>（官方该模块用 **weapi**，type=1），
    /// 邮箱走 <c>/api/w/login</c>（官方用 eapi，type=0）；密码须为 **MD5 小写十六进制**。
    /// <para>常见 code：501 账号不存在 / 502 账号或密码错误 / 503 操作过于频繁 / 8810、8821 风控需图形或短信验证。</para>
    /// </summary>
    /// <returns>(code, cookie, message)：code==200 且 cookie 含 MUSIC_U 才算登录成功</returns>
    public async Task<(int Code, string? Cookie, string? Message)> LoginWithPasswordAsync(
        string account, string password, string countryCode = "86")
    {
        var acc = (account ?? string.Empty).Trim();
        if (acc.Length == 0 || string.IsNullOrEmpty(password))
            return (-1, null, "请填写账号和密码");

        var md5 = Md5HexLower(password);
        var isPhone = acc.All(char.IsDigit);
        try
        {
            string? body;
            IReadOnlyList<string> sets;
            if (isPhone)
            {
                var res = await NeteaseWeapi.RequestDetailedAsync(_http, "/api/w/login/cellphone",
                    new Dictionary<string, object>
                    {
                        ["type"] = "1",
                        ["https"] = "true",
                        ["phone"] = acc,
                        ["countrycode"] = string.IsNullOrWhiteSpace(countryCode) ? "86" : countryCode.Trim(),
                        ["password"] = md5,
                        ["remember"] = "true",
                        ["secureCaptcha"] = "",
                    }, null).ConfigureAwait(false);
                body = res.Body;
                sets = res.SetCookies;
            }
            else
            {
                var res = await NeteaseEapi.RequestDetailedAsync(_http, "/eapi/w/login",
                    new Dictionary<string, object>
                    {
                        ["type"] = "0",
                        ["https"] = "true",
                        ["username"] = acc,
                        ["password"] = md5,
                        ["rememberLogin"] = "true",
                    }, null, rawCipherResponse: true).ConfigureAwait(false);
                body = res.Body;
                sets = res.SetCookies;
            }

            var code = ReadCode(body) ?? -1;
            var message = ReadMessage(body);
            var cookie = code == 200 ? ExtractLoginCookie(sets, body) : null;

            NeteaseLoginLog.Write($"账号密码登录（{(isPhone ? "手机号" : "邮箱")} {Mask(acc)}）：code={code}"
                + (code == 200
                    ? $"，取到 Cookie {cookie?.Length ?? 0} 字符，含 MUSIC_U={cookie?.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase) == true}"
                    : (string.IsNullOrWhiteSpace(message) ? "" : $"，{message}")));
            return (code, cookie, message);
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"账号密码登录异常：{ex.Message}");
            return (-1, null, ex.Message);
        }
    }

    /// <summary>账号打码后再进日志（避免明文手机号/邮箱落盘）</summary>
    private static string Mask(string s)
        => s.Length <= 4 ? "***" : s.Substring(0, 3) + "***" + s.Substring(s.Length - 2);

    private static string Md5HexLower(string s)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static string? ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "message", "msg" })
            {
                if (doc.RootElement.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.String)
                {
                    var text = m.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>从响应 body 读业务 code（非 JSON / 缺字段返回 null）</summary>
    private static int? ReadCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var v)) return v;
        }
        catch { }
        return null;
    }

    // ── Cookie 合并（修复"续期后丢失 __csrf 等字段"）──

    /// <summary>
    /// 把响应 Set-Cookie 合并进当前 Cookie 串：同名替换、新名追加、值为空视为删除。
    /// <para>
    /// 修复点：旧实现是 <c>_cookie = string.Join("; ", 响应里那几个 Cookie)</c> —— 用**新下发的少数几条
    /// 覆盖整串**，于是未被重发的 <c>__csrf</c>、<c>_ntes_nnid</c>、<c>JSESSIONID-WYYY</c> 等全部丢失；
    /// 而老 /api/* 接口（红心 manipulate/tracks 等）依赖 <c>__csrf</c>，续期后这些功能会莫名失效。
    /// </para>
    /// </summary>
    /// <returns>本次是否收到新的 MUSIC_U（登录凭据）</returns>
    private bool MergeSetCookies(IReadOnlyList<string> setCookies)
    {
        if (setCookies.Count == 0) return false;
        var jar = ParseCookieJar(_cookie ?? "");
        var gotMusicU = false;
        foreach (var raw in setCookies)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var first = raw.Split(';')[0].Trim();
            var idx = first.IndexOf('=');
            if (idx <= 0) continue;
            var name = first.Substring(0, idx).Trim();
            var value = first.Substring(idx + 1).Trim();
            if (name.Length == 0) continue;
            if (value.Length == 0) { jar.Remove(name); continue; }
            jar[name] = value;
            if (string.Equals(name, "MUSIC_U", StringComparison.OrdinalIgnoreCase)) gotMusicU = true;
        }
        _cookie = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
        return gotMusicU;
    }

    /// <summary>解析 Cookie 串为字典（同名以最后一个为准）</summary>
    private static Dictionary<string, string> ParseCookieJar(string cookie)
    {
        var jar = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(cookie)) return jar;
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            if (kv.Length == 0) continue;
            var idx = kv.IndexOf('=');
            if (idx <= 0) continue;
            jar[kv.Substring(0, idx).Trim()] = kv.Substring(idx + 1).Trim();
        }
        return jar;
    }

    /// <summary>通知 UI 登录已过期（30 秒节流，防接口风暴连弹）</summary>
    private void RaiseLoginExpired()
    {
        if ((DateTime.UtcNow - _lastExpiredRaisedUtc).TotalSeconds < 30) return;
        _lastExpiredRaisedUtc = DateTime.UtcNow;
        NeteaseLoginLog.Write("登录态已失效且续期失败 → 通知界面提示重新登录");
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

    /// <summary>
    /// 歌手分类列表（GET /api/artist/list?categoryCode=…，实测匿名可用）。
    /// categoryCode 编码：华语 1001男/1002女/1003组合，欧美 2001/2002/2003，
    /// 日本 6001/6002/6003，韩国 7001/7002/7003，其他 4001/4002/4003（-1 全部实测不可用）。
    /// initial 首字母参数在老 web GET 下不生效，暂不做字母索引。
    /// </summary>
    public async Task<List<NeteaseArtist>> GetArtistsByCategoryAsync(int categoryCode, int limit = 30, int offset = 0)
    {
        try
        {
            var url = $"https://music.163.com/api/artist/list?categoryCode={categoryCode}&limit={limit}&offset={offset}";
            using var doc = await GetJsonAsync(url);
            if (doc == null || !doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                return new List<NeteaseArtist>();
            var list = new List<NeteaseArtist>();
            foreach (var a in artists.EnumerateArray())
            {
                if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                list.Add(new NeteaseArtist
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PicUrl = CoverWithSize(ToHttps(a.TryGetProperty("img1v1Url", out var p) ? p.GetString() : null), 300),
                    SongCount = a.TryGetProperty("musicSize", out var ms) && ms.TryGetInt32(out var msv) ? msv : 0,
                    AlbumCount = a.TryGetProperty("albumSize", out var abs) && abs.TryGetInt32(out var absv) ? absv : 0,
                });
            }
            return list;
        }
        catch { return new List<NeteaseArtist>(); }
    }

    /// <summary>热门歌手（GET /api/artist/top，作为歌手页「热门」分类）</summary>
    public async Task<List<NeteaseArtist>> GetTopArtistsAsync(int limit = 30, int offset = 0)
    {
        try
        {
            var url = $"https://music.163.com/api/artist/top?limit={limit}&offset={offset}";
            using var doc = await GetJsonAsync(url);
            if (doc == null || !doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                return new List<NeteaseArtist>();
            var list = new List<NeteaseArtist>();
            foreach (var a in artists.EnumerateArray())
            {
                if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                list.Add(new NeteaseArtist
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PicUrl = CoverWithSize(ToHttps(a.TryGetProperty("img1v1Url", out var p) ? p.GetString() : null), 300),
                    SongCount = a.TryGetProperty("musicSize", out var ms) && ms.TryGetInt32(out var msv) ? msv : 0,
                    AlbumCount = a.TryGetProperty("albumSize", out var abs) && abs.TryGetInt32(out var absv) ? absv : 0,
                });
            }
            return list;
        }
        catch { return new List<NeteaseArtist>(); }
    }

    /// <summary>
    /// 排行榜聚合详情（GET /api/toplist/detail）：全部榜单 + 更新频率，一次请求。
    /// 匿名下 tracks 预览多为空壳，Top3 由调用方按需经 GetPlaylistSongsAsync 补齐。
    /// 当天结果落盘缓存（netease_toplists_cache.json）：跨启动秒开，榜单每日更新按日期失效。
    /// </summary>
    private static readonly string ToplistCachePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CatClawMusic.Maui", "netease_toplists_cache.json");
    private List<ToplistBlock>? _toplistCache;
    private string? _toplistCacheDate;

    private sealed class ToplistCacheDto
    {
        public string Date { get; set; } = "";
        public List<ToplistBlock> Blocks { get; set; } = new();
    }

    public async Task<List<ToplistBlock>> GetToplistBlocksAsync()
    {
        // 三级：内存 → 当天磁盘缓存 → 网络（榜单每日更新，按日期失效）
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (_toplistCacheDate == today && _toplistCache is { Count: > 0 }) return _toplistCache;
        var fromDisk = await LoadToplistCacheAsync(today).ConfigureAwait(false);
        if (fromDisk is { Count: > 0 })
        {
            _toplistCache = fromDisk;
            _toplistCacheDate = today;
            return fromDisk;
        }

        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/toplist/detail").ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return _toplistCache ?? new List<ToplistBlock>();
            var result = new List<ToplistBlock>();
            foreach (var t in list.EnumerateArray())
            {
                if (!t.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                var block = new ToplistBlock
                {
                    Playlist = new OnlinePlaylist
                    {
                        Id = idEl.GetInt64().ToString(),
                        Platform = "netease",
                        Name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        CoverUrl = CoverWithSize(ToHttps(t.TryGetProperty("coverImgUrl", out var c) ? c.GetString() : null), 300),
                        Description = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                        SongCount = t.TryGetProperty("total", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0,
                    },
                    UpdateFrequency = t.TryGetProperty("updateFrequency", out var uf) ? uf.GetString() ?? "" : "",
                };
                if (t.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in tracks.EnumerateArray())
                    {
                        var song = ParseSong(s);
                        if (song != null) block.TopSongs.Add(song);
                        if (block.TopSongs.Count >= 3) break;
                    }
                }
                result.Add(block);
            }
            _toplistCache = result;
            _toplistCacheDate = today;
            await SaveToplistCacheAsync(today, result).ConfigureAwait(false);
            return result;
        }
        catch { return _toplistCache ?? new List<ToplistBlock>(); }
    }

    private static async Task<List<ToplistBlock>?> LoadToplistCacheAsync(string date)
    {
        try
        {
            if (!File.Exists(ToplistCachePath)) return null;
            var json = await File.ReadAllTextAsync(ToplistCachePath).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<ToplistCacheDto>(json);
            if (dto?.Date != date || dto.Blocks is not { Count: > 0 }) return null;
            return dto.Blocks;
        }
        catch { return null; }
    }

    private static async Task SaveToplistCacheAsync(string date, List<ToplistBlock> blocks)
    {
        try
        {
            var dir = Path.GetDirectoryName(ToplistCachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new ToplistCacheDto { Date = date, Blocks = blocks };
            await File.WriteAllTextAsync(ToplistCachePath, JsonSerializer.Serialize(dto)).ConfigureAwait(false);
        }
        catch { }
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

    /// <summary>歌手别名（GET /api/artist/{id} 的 alias 数组，如 ["JJ Lin","Wayne Lin"]；失败返回 null）</summary>
    public async Task<List<string>?> GetArtistAliasesAsync(string artistId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/artist/{artistId}");
            if (doc == null || !doc.RootElement.TryGetProperty("artist", out var a) || a.ValueKind != JsonValueKind.Object)
                return null;
            if (!a.TryGetProperty("alias", out var alias) || alias.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var s in alias.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    list.Add(s.GetString()!);
            return list;
        }
        catch { return null; }
    }

    /// <summary>
    /// 歌手详情（GET /api/artist/introduction?id=，匿名可用）：一句话简介 + 分节长文
    /// （演艺经历/代表作品/重要里程碑…每节 txt 为多行文本，按行拆段展示）。
    /// </summary>
    public async Task<ArtistIntro?> GetArtistIntroAsync(string artistId)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/artist/introduction?id={artistId}");
            if (doc == null) return null;
            var intro = new ArtistIntro();
            if (doc.RootElement.TryGetProperty("briefDesc", out var bd) && bd.ValueKind == JsonValueKind.String)
                intro.BriefDesc = bd.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("introduction", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sections.EnumerateArray())
                {
                    var title = s.TryGetProperty("ti", out var ti) ? ti.GetString() ?? "" : "";
                    var text = s.TryGetProperty("txt", out var tx) ? tx.GetString() ?? "" : "";
                    if (title.Length == 0 && text.Length == 0) continue;
                    intro.Sections.Add(new IntroSection { Title = title, Text = text });
                }
            }
            return (intro.BriefDesc.Length > 0 || intro.Sections.Count > 0) ? intro : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 歌手 MV 列表（cloudsearch type=1004 按歌手名搜 MV；weapi /api/artist/mv 实测 400 不可用）。
    /// 字段：id/name/duration(ms)/playCount/cover/artists[]。
    /// </summary>
    public async Task<List<NeteaseMv>> GetArtistMvsAsync(string artistName, int limit = 40, int offset = 0)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = artistName, ["type"] = "1004", ["offset"] = offset.ToString(), ["limit"] = limit.ToString()
            });
            var req = Build(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc");
            req.Content = body;
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("mvs", out var mvs) || mvs.ValueKind != JsonValueKind.Array)
                return new List<NeteaseMv>();
            var list = new List<NeteaseMv>();
            foreach (var m in mvs.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                list.Add(new NeteaseMv
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    CoverUrl = CoverWithSize(ToHttps(m.TryGetProperty("cover", out var img) ? img.GetString() : null), 400),
                    PlayCount = m.TryGetProperty("playCount", out var pc) && pc.TryGetInt64(out var pcv) ? pcv : 0,
                    DurationMs = m.TryGetProperty("duration", out var du) && du.TryGetInt32(out var duv) ? duv : 0,
                });
            }
            return list;
        }
        catch { return new List<NeteaseMv>(); }
    }

    /// <summary>
    /// 相似歌手（GET /api/discovery/simiArtist?artistid=…，参数名全小写 artistid；需 Cookie 登录态，返回约 20 个）。
    /// weapi /api/v1/discovery/simiArtist 实测 404 不可用。
    /// </summary>
    public async Task<List<NeteaseArtist>> GetSimilarArtistsAsync(string artistId, int limit = 30)
    {
        try
        {
            using var doc = await GetJsonAsync($"https://music.163.com/api/discovery/simiArtist?artistid={artistId}&limit={limit}");
            if (doc == null || !doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                return new List<NeteaseArtist>();
            var list = new List<NeteaseArtist>();
            foreach (var a in artists.EnumerateArray())
            {
                if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                list.Add(new NeteaseArtist
                {
                    Id = idEl.GetInt64().ToString(),
                    Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PicUrl = CoverWithSize(ToHttps(a.TryGetProperty("img1v1Url", out var p) ? p.GetString() : null), 300),
                    SongCount = a.TryGetProperty("musicSize", out var ms) && ms.TryGetInt32(out var msv) ? msv : 0,
                    AlbumCount = a.TryGetProperty("albumSize", out var abs) && abs.TryGetInt32(out var absv) ? absv : 0,
                });
            }
            return list;
        }
        catch { return new List<NeteaseArtist>(); }
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

    /// <summary>
    /// 官方歌单分类（**分组**版，供「更多分类」使用）：/api/playlist/catalogue 返回
    /// <c>categories = { 组索引: 组名 }</c> 与 <c>sub[] = { name, category(组索引) }</c>，
    /// 官方 App 的「更多分类」正是按此分组展示（语种/风格/场景/情感/主题，组内再列分类）。
    /// 失败返回 null，调用方回退扁平列表。
    /// </summary>
    public async Task<List<(string Group, List<string> Names)>?> GetPlaylistCategoryGroupsAsync()
    {
        try
        {
            using var doc = await GetJsonAsync("https://music.163.com/api/playlist/catalogue");
            if (doc == null) return null;
            var root = doc.RootElement;
            if (!root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.Array) return null;

            var groupNames = new Dictionary<int, string>();
            if (root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cats.EnumerateObject())
                    if (int.TryParse(p.Name, out var idx) && p.Value.ValueKind == JsonValueKind.String)
                        groupNames[idx] = p.Value.GetString() ?? "";
            }

            var grouped = new Dictionary<int, List<string>>();
            foreach (var c in sub.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var gi = c.TryGetProperty("category", out var cg) && cg.TryGetInt32(out var g) ? g : 0;
                if (!grouped.TryGetValue(gi, out var list)) grouped[gi] = list = new List<string>();
                list.Add(name!);
            }

            var result = new List<(string Group, List<string> Names)>();
            foreach (var kv in grouped.OrderBy(k => k.Key))
                result.Add((groupNames.TryGetValue(kv.Key, out var gn) && !string.IsNullOrWhiteSpace(gn) ? gn : "其他", kv.Value));
            return result.Count > 0 ? result : null;
        }
        catch { return null; }
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
            }, _cookie, rawCipherResponse: true); // 实测此接口响应为裸密文（非 base64），解密路径错了会静默回退网页版
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

    /// <summary>
    /// 榜单/歌单第一页快速预览（服务于「榜单 Top N」这类只要前几首的场景）：
    /// eapi v6 detail n=count 只取头部完整曲目（tracks 与列表顺序一致），单请求毫秒级返回；
    /// 不走 trackIds 补全（那是全量歌单的分页链路）。eapi 失败回退网页版同参数。
    /// </summary>
    public async Task<List<OnlineSong>> GetPlaylistSongsFirstPageAsync(string playlistId, int count)
    {
        var result = new List<OnlineSong>();
        try
        {
            if (!long.TryParse(playlistId, out var id)) return result;
            var raw = await NeteaseEapi.RequestAsync(_http, "/eapi/v6/playlist/detail", new Dictionary<string, object>
            {
                ["id"] = id, ["n"] = count, ["s"] = 8,
            }, _cookie, rawCipherResponse: true);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("playlist", out var pl) &&
                    pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in tracks.EnumerateArray())
                    {
                        var song = ParseSong(s);
                        if (song != null) result.Add(song);
                        if (result.Count >= count) return result;
                    }
                }
            }
        }
        catch { }
        try
        {
            var url = $"https://music.163.com/api/v6/playlist/detail?id={playlistId}&n={count}&s=8";
            using var doc = await GetJsonAsync(url);
            if (doc != null && doc.RootElement.TryGetProperty("playlist", out var pl) &&
                pl.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in tracks.EnumerateArray())
                {
                    var song = ParseSong(s);
                    if (song != null) result.Add(song);
                    if (result.Count >= count) break;
                }
            }
        }
        catch { }
        return result;
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
    /// <summary>
    /// 歌单动态信息（创建者/收藏数/评论数/分享数/播放数）。
    /// eapi /eapi/playlist/detail/dynamic（裸密文响应，参数与 v6/detail 同族；api-enhanced 同款实现）。
    /// 歌单详情页头部三操作胶囊数据来源。
    /// </summary>
    public async Task<PlaylistDynamicInfo?> GetPlaylistDetailDynamicAsync(string playlistId)
    {
        try
        {
            if (!long.TryParse(playlistId, out var id)) return null;
            var raw = await NeteaseEapi.RequestAsync(_http, "/eapi/playlist/detail/dynamic", new Dictionary<string, object>
            {
                ["id"] = id,
                ["n"] = 100000,
                ["s"] = 8,
            }, _cookie, rawCipherResponse: true);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("playlist", out var pl) || pl.ValueKind != JsonValueKind.Object)
                return null;
            var info = new PlaylistDynamicInfo();
            if (pl.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object)
            {
                if (cr.TryGetProperty("nickname", out var nk) && nk.ValueKind == JsonValueKind.String)
                    info.CreatorName = nk.GetString();
                if (cr.TryGetProperty("avatarUrl", out var av) && av.ValueKind == JsonValueKind.String)
                    info.CreatorAvatar = ToHttps(av.GetString() ?? "");
            }
            info.PlayCount = pl.TryGetProperty("playCount", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt64() : 0;
            info.SubscribedCount = pl.TryGetProperty("subscribedCount", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt64() : 0;
            info.CommentCount = pl.TryGetProperty("commentCount", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt64() : 0;
            info.ShareCount = pl.TryGetProperty("shareCount", out var shc) && shc.ValueKind == JsonValueKind.Number ? shc.GetInt64() : 0;
            info.TrackCount = pl.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
            return info;
        }
        catch { return null; }
    }

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
    /// 播放直链（带音质 + 20 分钟缓存 + 多级兜底）。
    /// quality：0=标准 128k，1=极高 320k，2=无损 FLAC(VIP)，3=Hires 高解析度无损(VIP)，4=高清臻音(VIP)。
    /// </summary>
    public async Task<string?> GetPlayUrlAsync(string songId, int quality = 1)
        => (await GetPlayUrlWithTypeAsync(songId, quality)).Url;

    /// <summary>
    /// 播放直链 + 真实扩展名 + 实际档位（下载命名/提示用；播放场景可忽略 Ext/Level）。
    /// Ext 来自 eapi 返回的 type 字段（flac/mp3），老接口回退时按档位推断；
    /// Level 为服务端实际下发的档位（请求 hires 但歌曲无 Hires 资源时会回落 lossless）。
    /// </summary>
    public async Task<(string? Url, string? Ext, string? Level)> GetPlayUrlWithTypeAsync(string songId, int quality = 1)
    {
        if (string.IsNullOrWhiteSpace(songId)) return (null, null, null);
        quality = Math.Clamp(quality, 0, QualityMax);

        // VIP 档（无损及以上）未登录：匿名请求会被风控，直接降为极高 320k 流程
        if (quality >= 2 && !HasCookie) quality = 1;

        // 缓存命中
        var cacheKey = $"{songId}:{quality}";
        lock (_urlCacheLock)
        {
            if (_urlCache.TryGetValue(cacheKey, out var hit))
            {
                if (hit.ExpireAt > DateTime.UtcNow) return (hit.Url, hit.Ext, hit.Level);
                _urlCache.Remove(cacheKey);
            }
        }

        var (url, ext, level) = await ResolvePlayUrlWithTypeAsync(songId, quality);
        if (!string.IsNullOrWhiteSpace(url))
        {
            lock (_urlCacheLock)
            {
                _urlCache[cacheKey] = (url, ext, level, DateTime.UtcNow + UrlCacheTtl);
                // 简单防爆：缓存条目过多时整体清空（TTL 20 分钟，通常远达不到）
                if (_urlCache.Count > 2000) _urlCache.Clear();
            }
        }
        return (url, ext, level);
    }

    /// <summary>支持的最高音质档位（0=标准 1=极高 2=无损 3=Hires 4=高清臻音；沉浸环绕声/超清母带为 SVIP 专属未纳入）</summary>
    public const int QualityMax = 4;

    private static int QualityToBr(int quality) => quality switch
    {
        0 => 128000,
        2 or 3 or 4 => 999000,
        _ => 320000,
    };

    /// <summary>音质档位 → eapi level 参数（与官方客户端音质档一一对应）</summary>
    private static string QualityToLevel(int quality) => quality switch
    {
        0 => "standard",
        2 => "lossless",
        3 => "hires",
        4 => "jyeffect", // 高清臻音
        _ => "exhigh",
    };

    /// <summary>老接口回退时的扩展名推断（eapi 不可用时的粗略档位 → 格式映射）</summary>
    private static string? ExtFromQuality(int quality)
        => quality switch { 0 or 1 => "mp3", _ => "flac" };

    /// <summary>
    /// 多级兜底取链：① eapi level 取链（新接口，覆盖 standard/exhigh/lossless/hires/jyeffect，
    /// 返回真实 type 与实际下发档位；VIP 档需登录 Cookie）→ ② 老 enhance br 接口
    /// → ③ 免登录外链（≤320k）→ ④ 降档重试 → ⑤ 公共 API 实例。
    /// </summary>
    private async Task<(string? Url, string? Ext, string? Level)> ResolvePlayUrlWithTypeAsync(string songId, int quality)
    {
        // 方案1：eapi enhance/player/url/v1 按 level 取链（interface.music.163.com，歌词同款通道）
        var (eapiUrl, eapiExt, eapiLevel) = await GetEnhanceUrlEapiAsync(songId, QualityToLevel(quality));
        if (!string.IsNullOrWhiteSpace(eapiUrl)) return (eapiUrl, eapiExt, eapiLevel);

        // 方案2：老 web 接口按码率（封顶无损；eapi 受阻时的同能力回退）
        int br = QualityToBr(quality);
        var enhanceUrl = await GetEnhanceUrlAsync(songId, br);
        if (!string.IsNullOrWhiteSpace(enhanceUrl)) return (enhanceUrl, ExtFromQuality(quality), null);

        // 方案3：免登录外链（302 到 CDN；标准/极高档可用）
        if (br <= 320000)
        {
            try
            {
                var outer = $"https://music.163.com/song/media/outer/url?id={songId}.mp3";
                using var resp = await _http.GetAsync(outer, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent)
                    return (ToHttps(resp.RequestMessage?.RequestUri?.ToString() ?? outer), "mp3", null);
            }
            catch { }
        }

        // 方案4：高品/无损受限 → 逐步降档再试 enhance（320k → 128k 标准档，规避部分 VIP/风控提升可播性）
        if (br >= 320000)
        {
            if (br == 999000)
            {
                enhanceUrl = await GetEnhanceUrlAsync(songId, 320000);
                if (!string.IsNullOrWhiteSpace(enhanceUrl)) return (enhanceUrl, "mp3", null);
            }
            enhanceUrl = await GetEnhanceUrlAsync(songId, 128000);
            if (!string.IsNullOrWhiteSpace(enhanceUrl)) return (enhanceUrl, "mp3", null);
        }

        // 方案5：公共 NeteaseCloudMusicApi 实例兜底
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
                        if (!string.IsNullOrWhiteSpace(playUrl)) return (ToHttps(playUrl), null, null);
                    }
                }
            }
            catch { }
        }
        return (null, null, null);
    }

    /// <summary>
    /// eapi /eapi/song/enhance/player/url/v1 按 level 取链（官方客户端同款接口）。
    /// 返回 (直链, 真实扩展名, 实际档位 level)；data[].code==200 且 url 非空才算成功
    /// （VIP 档权限不足/无该音质资源时返回 404 或服务端回落到较低 level —— 如请求 hires
    /// 实际返回 lossless，故必须读返回的 level 字段告知用户真实音质）。
    /// 注意：ids 须为字符串 "[id]"（数字数组会 400）；此接口响应为裸 AES 密文（非 base64）。
    /// </summary>
    private async Task<(string? Url, string? Ext, string? Level)> GetEnhanceUrlEapiAsync(string songId, string level)
    {
        try
        {
            if (!long.TryParse(songId, out var id)) return (null, null, null);
            var raw = await NeteaseEapi.RequestAsync(_http, "/eapi/song/enhance/player/url/v1", new Dictionary<string, object>
            {
                ["ids"] = $"[{id}]", // 字符串形式 "[123456]"，与官方客户端一致
                ["level"] = level,
                ["encodeType"] = "flac",
            }, _cookie, rawCipherResponse: true);
            if (string.IsNullOrWhiteSpace(raw)) return (null, null, null);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    long code = 0;
                    if (item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                        code = c.GetInt64();
                    if (code != 200) continue;
                    if (!item.TryGetProperty("url", out var u)) continue;
                    var playUrl = u.GetString();
                    if (string.IsNullOrWhiteSpace(playUrl)) continue;
                    string? type = null, actualLevel = null;
                    if (item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                        type = t.GetString()?.Trim().TrimStart('.').ToLowerInvariant();
                    if (item.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String)
                        actualLevel = l.GetString();
                    return (ToHttps(playUrl), type, actualLevel);
                }
            }
        }
        catch { }
        return (null, null, null);
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

    /// <summary>歌单评论（资源评论族 A_PL_0_，与歌曲评论同结构同解析；匿名可用）。</summary>
    public Task<List<SongComment>> GetPlaylistCommentsAsync(string playlistId, int limit = 20, int offset = 0)
        => GetCommentsAsync(playlistId, limit, offset, hot: false, "A_PL_0_");

    /// <summary>歌单热门评论（资源评论族 A_PL_0_）。</summary>
    public Task<List<SongComment>> GetPlaylistHotCommentsAsync(string playlistId, int limit = 20)
        => GetCommentsAsync(playlistId, limit, offset: 0, hot: true, "A_PL_0_");

    /// <summary>最近一次评论请求的服务端总数（0 = 未返回）；供评论页标题显示"共 N 条"</summary>
    public int LastCommentsTotal { get; private set; }

    /// <summary>最近一次评论请求服务端是否还有更多（用于"加载更多"按钮）</summary>
    public bool LastCommentsHasMore { get; private set; }

    private async Task<List<SongComment>> GetCommentsAsync(string songId, int limit, int offset, bool hot,
        string resourcePrefix = "R_SO_4_")
    {
        var list = new List<SongComment>();
        if (string.IsNullOrWhiteSpace(songId)) return list;
        LastCommentsTotal = 0;
        LastCommentsHasMore = false;
        try
        {
            // 官方契约（api-enhanced 的 comment_hot / comment_music / comment_playlist，均走 weapi）：
            //   热门：POST /weapi/v1/resource/hotcomments/{前缀}{id}   —— 注意是 hotcomments、且前缀与 id 之间无斜杠
            //   最新：POST /weapi/v1/resource/comments/{前缀}{id}
            //   参数：rid 传**原始 id**（前缀只出现在 path 里），并且**必须带 beforeTime=0**
            // 旧实现三处都不对（热门写成 /hot/comments/ → 404；最新缺 beforeTime → 歌单 400；
            // 且走明文 GET），异常又被 catch 吞掉 → 评论区永远显示"暂无评论"。
            var path = hot
                ? $"/api/v1/resource/hotcomments/{resourcePrefix}{songId}"
                : $"/api/v1/resource/comments/{resourcePrefix}{songId}";
            var json = await NeteaseWeapi.RequestAsync(_http, path, new Dictionary<string, object>
            {
                ["rid"] = songId,
                ["limit"] = limit,
                ["offset"] = offset,
                ["beforeTime"] = 0,
            }, _cookie).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return list;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 评论数组在**顶层**：{"code":200,"total":70430,"more":true,"hotComments":[...],"comments":[...]}
            // 旧实现按 data.hotComments / data.comments 解析（没有 data 包装）→ 永远取不到，
            // 表现为评论区一直"暂无评论"（歌曲实际有数万条）。
            var arrField = hot ? "hotComments" : "comments";
            JsonElement arr;
            if (root.TryGetProperty(arrField, out var topArr) && topArr.ValueKind == JsonValueKind.Array)
            {
                arr = topArr;
            }
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                     && data.TryGetProperty(arrField, out var nestedArr) && nestedArr.ValueKind == JsonValueKind.Array)
            {
                arr = nestedArr;   // 兼容带 data 包装的返回
            }
            else
            {
                NeteaseLoginLog.Write($"评论返回结构异常（{resourcePrefix}{songId}）：无 {arrField} 字段");
                return list;
            }

            if (root.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
                && totalEl.TryGetInt32(out var total))
                LastCommentsTotal = total;
            if (root.TryGetProperty("more", out var moreEl)
                && (moreEl.ValueKind == JsonValueKind.True || moreEl.ValueKind == JsonValueKind.False))
                LastCommentsHasMore = moreEl.GetBoolean();

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
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"获取评论失败（{resourcePrefix}{songId}，{(hot ? "热门" : "最新")}）：{ex.Message}");
        }
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
            var doc = await SendJsonAsync(HttpMethod.Get, url).ConfigureAwait(false);
            // 携带了登录 Cookie 却被判定"需要登录"(code=301)：会话已过期 →
            // 尝试静默续期（/api/login/token/refresh）一次并重试；续期失败再提示用户重登。
            if (doc != null && IsLoginRequired(doc.RootElement)
                && !string.IsNullOrWhiteSpace(_cookie))
            {
                NeteaseLoginLog.Write($"接口返回 301（需要登录）：{url} → 尝试静默续期");
                if (await RefreshLoginTokenAsync().ConfigureAwait(false))
                {
                    doc = await SendJsonAsync(HttpMethod.Get, url).ConfigureAwait(false);
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
        // 本类为纯数据层：ConfigureAwait(false) 让响应读取 + JsonDocument.Parse 脱离 UI 线程
        using var resp = await _http.SendAsync(Build(method, url)).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
        return new NeteasePlaylist
        {
            Id = pl.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "",
            Platform = "netease",
            Name = pl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            CoverUrl = CoverWithSize(ToHttps(cover), 300),
            Description = pl.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            SongCount = pl.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0,
            PlayCount = pl.TryGetProperty("playCount", out var pc) && pc.TryGetInt64(out var pcv) ? pcv : null,
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
