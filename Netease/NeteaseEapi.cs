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
    // 每进程随机一次设备指纹（模拟桌面客户端），实测无需匿名会话即可通过 eapi 风控
    private static readonly string DeviceId = RandomHex(32);
    private static readonly string ClientSign = RandomMac() + "@@@" + RandomUpper(8) + "@@@@@@" + RandomHex(64);
    private static readonly string OsVer = "Microsoft-Windows-10--build-" + _rnd.Next(20000, 30000) + "-64bit";
    private static readonly string Mode = _rnd.Next(5) switch
    {
        0 => "MS-iCraft B760M WIFI",
        1 => "ASUS ROG STRIX Z790",
        2 => "MSI MAG B550 TOMAHAWK",
        3 => "ASRock X670E Taichi",
        _ => "GIGABYTE Z790 AORUS ELITE",
    };

    // JS JSON.stringify 行为：不转义非 ASCII，只转义引号/反斜杠（与 UnsafeRelaxedJsonEscaping 一致）
    private static readonly JsonSerializerOptions JsonOpt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// 调用 eapi 接口（POST params=密文 到 interface.music.163.com），返回解密后的 JSON 文本；失败返回 null。
    /// </summary>
    /// <param name="http">复用客户端（不共享 Cookie 管理）</param>
    /// <param name="path">形如 /eapi/song/lyric/v1</param>
    /// <param name="parameters">业务参数（须为 string/long/bool，序列化顺序与 JS Object 一致）</param>
    /// <param name="userCookie">用户登录 Cookie（可选；缺省用模拟桌面客户端的预置 Cookie）</param>
    public static async Task<string?> RequestAsync(HttpClient http, string path, IReadOnlyDictionary<string, object> parameters, string? userCookie)
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
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return AesEcbDecryptBase64ToText(body);
        }
        catch { return null; }
    }

    /// <summary>eapi 歌词：返回 (Lrc, TLrc)，失败 null（比官方 /api/song/lyric 多 yrc/romalrc 字段，此处取 lrc+tlyric）</summary>
    public static async Task<(string? Lrc, string? TLrc)?> FetchLyricAsync(HttpClient http, long songId, string? userCookie)
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
        string? lrc = null, tlyric = null;
        if (doc.RootElement.TryGetProperty("lrc", out var ln) && ln.TryGetProperty("lyric", out var lt))
            lrc = lt.GetString();
        if (doc.RootElement.TryGetProperty("tlyric", out var tn) && tn.TryGetProperty("lyric", out var tt))
            tlyric = tt.GetString();
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        return (lrc, string.IsNullOrWhiteSpace(tlyric) ? null : tlyric);
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
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(EapiKey);
        using var dec = aes.CreateDecryptor();
        var result = dec.TransformFinalBlock(body, 0, body.Length);
        return Encoding.UTF8.GetString(result);
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
