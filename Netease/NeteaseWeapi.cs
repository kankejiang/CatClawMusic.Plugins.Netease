using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云 Web 端 weapi 加密接口（POST 到 music.163.com/weapi/*）。
/// 算法对齐 api-enhanced 的 util/crypto.js：双层 AES-128-CBC + 原始 RSA(NoPadding) 加密随机密钥。
/// 用于老接口/eapi 覆盖不到或须走 Web 加密的接口（相似歌曲、历史日推、MV、评论等）。
/// </summary>
internal static class NeteaseWeapi
{
    private const string PresetKey = "0CoJUm6Qyw8W8jud";
    private const string Iv = "0102030405060708";
    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    // 与 api-enhanced util/crypto.js 一致的 1024 位固定公钥
    private const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    private static readonly Random _rnd = new();

    /// <summary>
    /// 调用 weapi 接口，返回响应 JSON 文本；失败返回 null。
    /// <paramref name="path"/> 形如 /api/v1/discovery/simiSong（内部自动去掉 /api/ 前缀拼 /weapi/xxx）。
    /// </summary>
    public static async Task<string?> RequestAsync(HttpClient http, string path,
        IReadOnlyDictionary<string, object> body, string? userCookie)
    {
        try
        {
            var paramsJson = JsonSerializer.Serialize(body);
            var secretKey = new StringBuilder(16);
            for (var i = 0; i < 16; i++) secretKey.Append(Base62[_rnd.Next(62)]);
            var sk = secretKey.ToString();

            var paramsOuter = AesCbcEncrypt(AesCbcEncrypt(paramsJson, PresetKey), sk);
            var encSecKey = RsaNoPaddingEncryptHex(Reverse(sk));

            var weapiPath = path.StartsWith("/api/", StringComparison.Ordinal) ? path.Substring("/api/".Length) : path.TrimStart('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/weapi/" + weapiPath)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["params"] = paramsOuter,
                    ["encSecKey"] = encSecKey,
                    ["csrf_token"] = ExtractCsrf(userCookie),
                }),
            };
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            if (!string.IsNullOrWhiteSpace(userCookie))
                req.Headers.TryAddWithoutValidation("Cookie", userCookie);

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch { return null; }
    }

    // ── 内部 ──

    /// <summary>AES-128-CBC PKCS7 加密，输出 base64</summary>
    private static string AesCbcEncrypt(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(Iv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var result = enc.TransformFinalBlock(Encoding.UTF8.GetBytes(text), 0, text.Length);
        return Convert.ToBase64String(result);
    }

    /// <summary>原始 RSA（NoPadding）：m^e mod n，输出 128 字节大端 hex。secretKey 为纯 ASCII，按无符号处理。</summary>
    private static string RsaNoPaddingEncryptHex(string input)
    {
        var modulus = GetModulus();
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var m = new BigInteger(Encoding.UTF8.GetBytes(input), isUnsigned: true, isBigEndian: true);
        var c = BigInteger.ModPow(m, new BigInteger(65537), n);
        var raw = c.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[128];
        Array.Copy(raw, 0, padded, padded.Length - raw.Length, raw.Length);
        return Convert.ToHexString(padded);
    }

    /// <summary>解析 PEM 公钥获取 1024 位 RSA 模数（十六进制大端字节）</summary>
    private static byte[] GetModulus()
    {
        var base64 = string.Concat(PublicKeyPem.Split('\n').Where(IsNotPemBoundary));
        var der = Convert.FromBase64String(base64);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(der, out _);
        return rsa.ExportParameters(false).Modulus!;
    }

    private static bool IsNotPemBoundary(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && !t.StartsWith("-----", StringComparison.Ordinal);
    }

    /// <summary>从 Cookie 提取 csrf_token（__csrf），缺失返回空串</summary>
    private static string ExtractCsrf(string? cookie)
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

    private static string Reverse(string s)
    {
        var arr = s.ToCharArray();
        Array.Reverse(arr);
        return new string(arr);
    }
}