using System.Text.Json;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 跨源解灰 POC：酷我（Kuwo）单源补播。
/// 网易云取链失败且开关开启时，用「歌名+歌手」在酷我搜索同名曲并取播放直链。
/// 依赖酷我公开的搜索 / 转换端点（非官方签名接口），可用性会有波动；
/// 开关 <see cref="UnblockEnabled"/> 默认关闭，仅供已购歌单补播验证。
/// </summary>
internal static class KuwoUnblock
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>按歌名（必填）+歌手（可选）解析直链；找不到返回 null</summary>
    public static async Task<string?> ResolveAsync(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var rids = await SearchAsync(title, artist);
            if (rids == null) return null;
            foreach (var rid in rids)
            {
                var url = await GetUrlAsync(rid);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        catch { }
        return null;
    }

    /// <summary>搜索 + 歌名/歌手匹配，返回排序后的候选 rid 列表</summary>
    private static async Task<List<string>?> SearchAsync(string title, string? artist)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var url = $"http://search.kuwo.cn/r.s?all={Uri.EscapeDataString(query)}&ft=music&itemset=web_2013&client=kt&pn=0&rn=12&rformat=json&encoding=utf8&vipver=1&mobi=1&source=kwplayer_ar_5.0.1.0.apk";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetKwHeaders(req);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        var candidates = new List<string>();
        var normTitle = Normalize(title);
        var normArtist = Normalize(artist ?? "");

        // 优先标准 JSON 解析；酷我偶发无引号非标准 JSON，失败则跳过
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(json); }
        catch { }
        if (doc != null)
        {
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("abslist", out var list) || list.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var it in list.EnumerateArray())
                {
                    var rid = GetProp(it, "MUSICRID") ?? GetProp(it, "musicrid");
                    if (string.IsNullOrEmpty(rid) || !Match(it, normTitle, normArtist)) continue;
                    candidates.Add(rid);
                }
            }
        }
        else
        {
            // 宽松正则解析非标准 JSON：抓取 NAME/ARTIST/MUSICRID 三元组
            var names = RegexAll(json, "NAME\"?\\s*[:=]\\s*\"?([^\",}]+?)\"?", true);
            var artists = RegexAll(json, "ARTIST\"?\\s*[:=]\\s*\"?([^\",}]+?)\"?", true);
            var rids = RegexAll(json, "MUSICRID\"?\\s*[:=]\\s*\"?([^\",}]+?)\"?", false);
            int n = Math.Min(rids.Count, Math.Min(names.Count, artists.Count));
            for (int i = 0; i < n; i++)
            {
                if (MatchText(names[i], artists[i], normTitle, normArtist)) candidates.Add(rids[i]);
            }
        }
        return candidates.Count > 0 ? candidates : null;
    }

    private static bool Match(JsonElement it, string normTitle, string normArtist)
    {
        var name = GetProp(it, "NAME") ?? GetProp(it, "name");
        var art = GetProp(it, "ARTIST") ?? GetProp(it, "artist");
        return MatchText(name ?? "", art ?? "", normTitle, normArtist);
    }

    /// <summary>歌名+歌手启发式匹配：歌名强相似且歌手互含</summary>
    private static bool MatchText(string rawName, string rawArtist, string normTitle, string normArtist)
    {
        var name = Normalize(rawName);
        var art = Normalize(rawArtist);
        if (name.Length == 0) return false;
        bool nameOk = name == normTitle
            || (normTitle.Length >= 6 && (name.Contains(normTitle, StringComparison.Ordinal) || normTitle.Contains(name, StringComparison.Ordinal)));
        if (!nameOk) return false;
        if (normArtist.Length == 0) return true;
        return art.Length > 0 && (art.Contains(normArtist, StringComparison.Ordinal) || normArtist.Contains(art, StringComparison.Ordinal));
    }

    private static async Task<string?> GetUrlAsync(string rid)
    {
        var url = $"http://antiserver.kuwo.cn/anti.s?type=convert_url3&rid={rid}&format=mp3&response=url&source=kwplayer_ar_5.1.0.0.apk";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetKwHeaders(req);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var text = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) return null;
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return text.Replace("http://", "https://");
        return null;
    }

    private static void SetKwHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", "kwplayer/5.0.1.0");
        req.Headers.TryAddWithoutValidation("Referer", "http://www.kuwo.cn/");
    }

    private static string? GetProp(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static List<string> RegexAll(string input, string pattern, bool stripParens)
    {
        var list = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(input, pattern))
        {
            var val = m.Groups[1].Value.Trim();
            if (stripParens) val = System.Text.RegularExpressions.Regex.Replace(val, @"[\(（].*?[\)）]", "");
            list.Add(val);
        }
        return list;
    }

    /// <summary>归一化：仅保留字母/数字并转小写，剔除标点与括号内容差异</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var b = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) b.Append(char.ToLowerInvariant(ch));
        }
        return b.ToString();
    }
}