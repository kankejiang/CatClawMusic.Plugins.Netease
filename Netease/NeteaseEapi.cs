using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云桌面客户端 eapi 接口（走 interface.music.163.com，与 music.163.com 老接口不同源、限流策略不同）。
/// 实现参考 Lyrico-Plugins 的 netease 插件：AES-128-ECB(PKCS5) + MD5 签名 + 随机设备指纹伪装桌面客户端，
/// 无需匿名会话注册即可调用 /eapi/song/lyric/v1 等接口。作为官方 /api/song/lyric 被风控时的歌词兜底。
/// </summary>
internal static class NeteaseEapi
{
    private const string EapiKey = "e82ckenh8dichen8";
    private const string EncryptSalt = "-36cd479b6b5-";
    private const string AppVer = "3.1.3.203419";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.3.203419";

    private static readonly Random _rnd = new();

    // ── 设备指纹：**必须跨进程稳定**（关键）──
    // 网易云会话与设备绑定。原实现每次进程启动都重新随机 deviceId/clientSign，
    // 于是"登录那个进程"与"续期那个进程"设备号不同 → /eapi/login/token/refresh 被拒
    // （实测：设备号不匹配时返回 400/301，而同一 Cookie 调歌曲详情却完全正常，
    //   极易被误判成"会话过期"）。官方客户端与 api-enhanced 同样要求 deviceId 稳定
    // 持久（后者甚至自带 1.3MB 的 deviceid.txt）。这里落盘一次、之后复用。
    private static readonly string DeviceFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CatClawMusic.Maui", "netease_device.txt");

    private static readonly string DeviceId;
    private static readonly string ClientSign;
    private static readonly string OsVer;
    private static readonly string Mode;

    private sealed record DeviceProfile(string DeviceId, string ClientSign, string OsVer, string Mode);

    /// <summary>初始化（或首次生成并持久化）设备指纹</summary>
    static NeteaseEapi()
    {
        var profile = TryLoadDevice();
        var firstRun = profile == null;
        profile ??= new DeviceProfile(
            RandomHex(32),
            RandomMac() + "@@@" + RandomUpper(8) + "@@@@@@" + RandomHex(64),
            "Microsoft-Windows-10--build-" + _rnd.Next(20000, 30000) + "-64bit",
            _rnd.Next(5) switch
            {
                0 => "MS-iCraft B760M WIFI",
                1 => "ASUS ROG STRIX Z790",
                2 => "MSI MAG B550 TOMAHAWK",
                3 => "ASRock X670E Taichi",
                _ => "GIGABYTE Z790 AORUS ELITE",
            });

        DeviceId = profile.DeviceId;
        ClientSign = profile.ClientSign;
        OsVer = profile.OsVer;
        Mode = profile.Mode;

        if (firstRun)
        {
            TrySaveDevice(profile);
            NeteaseLoginLog.Write($"首次生成设备指纹并落盘（deviceId={Short(DeviceId)}）");
        }
        else
        {
            NeteaseLoginLog.Write($"复用已保存设备指纹（deviceId={Short(DeviceId)}）");
        }
    }

    private static string Short(string s) => s.Length <= 8 ? s : s.Substring(0, 8) + "…";

    private static DeviceProfile? TryLoadDevice()
    {
        try
        {
            if (!File.Exists(DeviceFilePath)) return null;
            string? id = null, sign = null, osver = null, mode = null;
            foreach (var line in File.ReadAllLines(DeviceFilePath))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var key = line.Substring(0, i).Trim();
                var val = line.Substring(i + 1).Trim();
                if (val.Length == 0) continue;
                switch (key)
                {
                    case "deviceId": id = val; break;
                    case "clientSign": sign = val; break;
                    case "osver": osver = val; break;
                    case "mode": mode = val; break;
                }
            }
            if (id == null || sign == null || osver == null || mode == null) return null;
            return new DeviceProfile(id, sign, osver, mode);
        }
        catch { return null; }
    }

    private static void TrySaveDevice(DeviceProfile p)
    {
        try
        {
            var dir = Path.GetDirectoryName(DeviceFilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DeviceFilePath,
                $"deviceId={p.DeviceId}\nclientSign={p.ClientSign}\nosver={p.OsVer}\nmode={p.Mode}\n");
        }
        catch { }
    }

    // JS JSON.stringify 行为：不转义非 ASCII，只转义引号/反斜杠（与 UnsafeRelaxedJsonEscaping 一致）
    private static readonly JsonSerializerOptions JsonOpt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 调用 eapi 接口（POST params=密文 到 interface.music.163.com），返回解密后的 JSON 文本；失败返回 null。
    /// </summary>
    /// <param name="http">复用客户端（不共享 Cookie 管理）</param>
    /// <param name="path">形如 /eapi/song/lyric/v1</param>
    /// <param name="parameters">业务参数（须为 string/long/bool，序列化顺序与 JS Object 一致）</param>
    /// <param name="userCookie">用户登录 Cookie（可选；缺省用模拟桌面客户端的预置 Cookie）</param>
    public static Task<string?> RequestAsync(HttpClient http, string path, IReadOnlyDictionary<string, object> parameters, string? userCookie)
        => RequestAsync(http, path, parameters, userCookie, rawCipherResponse: false);

    /// <summary>eapi 调用结果：解密后的 body + 响应 Set-Cookie。
    /// 登录/续期（/eapi/login/token/refresh、/eapi/login/qrcode/*）的新会话只出现在 Set-Cookie 里，
    /// 只返回 body 的旧签名拿不到，故单独提供本结果类型。</summary>
    internal sealed class EapiResult
    {
        public string? Body { get; init; }
        public IReadOnlyList<string> SetCookies { get; init; } = Array.Empty<string>();
    }

    /// <param name="rawCipherResponse">
    /// 响应体是否为「裸 AES 密文」。实测 /eapi/song/enhance/player/url/v1 返回裸密文，
    /// 而 /eapi/song/lyric/v1 返回 base64 密文，两个接口格式不同，故需调用方指定。
    /// </param>
    public static async Task<string?> RequestAsync(HttpClient http, string path, IReadOnlyDictionary<string, object> parameters, string? userCookie, bool rawCipherResponse)
        => (await RequestDetailedAsync(http, path, parameters, userCookie, rawCipherResponse).ConfigureAwait(false)).Body;

    /// <summary>调用 eapi 接口，返回解密后的 body **与响应的 Set-Cookie**（供登录/续期使用）。</summary>
    public static async Task<EapiResult> RequestDetailedAsync(HttpClient http, string path, IReadOnlyDictionary<string, object> parameters, string? userCookie, bool rawCipherResponse = true)
    {
        try
        {
            var header = new JsonObject
            {
                ["clientSign"] = ClientSign,
                ["osver"] = OsVer,
                ["deviceId"] = DeviceId,
                ["os"] = "pc",
                ["appver"] = AppVer,
                ["requestId"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            };

            var finalParams = new JsonObject();
            foreach (var kv in parameters)
            {
                finalParams[kv.Key] = kv.Value switch
                {
                    long l => JsonValue.Create(l),
                    int i => JsonValue.Create((long)i),
                    bool b => JsonValue.Create(b),
                    string s => JsonValue.Create(s),
                    _ => throw new ArgumentException("Unsupported eapi param type: " + kv.Value?.GetType().Name),
                };
            }
            finalParams["header"] = header.ToJsonString(JsonOpt);
            finalParams["e_r"] = true;

            var encryptPath = path.StartsWith("/eapi/", StringComparison.Ordinal)
                ? "/api/" + path.Substring("/eapi/".Length)
                : path;
            var paramsText = finalParams.ToJsonString(JsonOpt);
            var digest = Md5Hex("nobody" + encryptPath + "use" + paramsText + "md5forencrypt");
            var data = encryptPath + EncryptSalt + paramsText + EncryptSalt + digest;
            var enc = AesEcbEncryptHex(Encoding.UTF8.GetBytes(data));

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com" + path)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["params"] = enc }),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Host", "interface.music.163.com");
            req.Headers.TryAddWithoutValidation("Cookie", !string.IsNullOrWhiteSpace(userCookie)
                ? userCookie
                : $"os=pc; deviceId={DeviceId}; osver={OsVer}; clientSign={ClientSign}; channel=netease; mode={Mode}; appver={AppVer}");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                NeteaseLoginLog.Write($"eapi {path} HTTP {(int)resp.StatusCode}");
                return new EapiResult();
            }

            var setCookies = new List<string>();
            if (resp.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var v in values) setCookies.Add(v);

            var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var text = DecryptBody(body, rawCipherResponse);
            if (text != null)
            {
                // 裸密文按 PKCS7 去填充后尾部可能残留可解析的垃圾字节，截断到最后一个 '}'
                var end = text.LastIndexOf('}');
                if (end >= 0 && end < text.Length - 1) text = text.Substring(0, end + 1);
            }
            return new EapiResult { Body = text, SetCookies = setCookies };
        }
        catch (Exception ex)
        {
            NeteaseLoginLog.Write($"eapi {path} 异常: {ex.Message}");
            return new EapiResult();
        }
    }

    /// <summary>
    /// 解密响应体：按调用方指定的格式优先（裸密文 / base64 密文），失败则换另一种再试。
    /// 早期实现只认单一格式，一旦某个接口换了包装（实测 lyric 走 base64、player/url 走裸密文、
    /// token/refresh 走裸密文）就整条链路静默失败，这里两种都容错。
    /// </summary>
    private static string? DecryptBody(byte[] body, bool rawFirst)
    {
        if (body == null || body.Length == 0) return null;
        string? text = null;
        try { text = rawFirst ? AesEcbDecryptRawToText(body) : AesEcbDecryptBase64ToText(body); } catch { }
        if (string.IsNullOrWhiteSpace(text))
        {
            try { text = rawFirst ? AesEcbDecryptBase64ToText(body) : AesEcbDecryptRawToText(body); } catch { }
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>eapi 歌词：返回 (Lrc, TLrc, RLrc)，失败 null。
    /// rv=-1 已请求 romalrc 字段；比官方 /api/song/lyric 多 yrc/romalrc 字段。</summary>
    public static async Task<(string? Lrc, string? TLrc, string? RLrc)?> FetchLyricAsync(HttpClient http, long songId, string? userCookie)
    {
        var raw = await RequestAsync(http, "/eapi/song/lyric/v1", new Dictionary<string, object>
        {
            ["id"] = songId,
            ["lv"] = "-1",
            ["tv"] = "-1",
            ["rv"] = "-1",
            ["yv"] = "-1",
        }, userCookie);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
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

    /// <summary>eapi 单曲详情批量（/eapi/v3/song/detail，c="[{\"id\":...}]"）；返回解密后的 JSON 文本，失败 null</summary>
    public static async Task<string?> FetchSongDetailRawAsync(HttpClient http, long[] songIds, string? userCookie)
    {
        if (songIds == null || songIds.Length == 0) return null;
        // c=JSON.stringify([{id:1},{id:2}]) —— 与 JS JSON.stringify 字节一致
        var c = "[" + string.Join(",", songIds.Select(i => $"{{\"id\":{i}}}")) + "]";
        return await RequestAsync(http, "/eapi/v3/song/detail", new Dictionary<string, object>
        {
            ["c"] = c,
        }, userCookie, rawCipherResponse: true);
    }

    // ── 内部 ──

    private static string Md5Hex(string text)
        => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>AES-128-ECB PKCS5(=PKCS7) 加密，输出大写 hex（与桌面客户端一致）</summary>
    private static string AesEcbEncryptHex(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(EapiKey);
        using var enc = aes.CreateEncryptor();
        var result = enc.TransformFinalBlock(data, 0, data.Length);
        return Convert.ToHexString(result); // 大写
    }

    /// <summary>响应体（base64 的 AES 密文）解密为 UTF-8 文本</summary>
    private static string? AesEcbDecryptBase64ToText(byte[] body)
    {
        if (body == null || body.Length == 0) return null;
        return AesEcbDecryptRawToText(Convert.FromBase64String(Encoding.UTF8.GetString(body)));
    }

    /// <summary>裸 AES 密文（未做 base64）直接解密为 UTF-8 文本</summary>
    private static string? AesEcbDecryptRawToText(byte[] cipher)
    {
        if (cipher == null || cipher.Length == 0) return null;
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(EapiKey);
        using var dec = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
    }

    private static string RandomHex(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append("0123456789abcdef"[_rnd.Next(16)]);
        return sb.ToString();
    }

    private static string RandomUpper(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append((char)('A' + _rnd.Next(26)));
        return sb.ToString();
    }

    private static string RandomMac()
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++) parts[i] = _rnd.Next(256).ToString("X2");
        return string.Join(":", parts);
    }
}
