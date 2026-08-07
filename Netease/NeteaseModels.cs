namespace CatClawMusic.Plugins.Netease;

/// <summary>歌手（网易云搜索结果 / 歌手页用，插件本地模型）</summary>
public class NeteaseArtist
{
    /// <summary>歌手 ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>歌手名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>头像/封面 URL</summary>
    public string? PicUrl { get; set; }

    /// <summary>歌曲数（musicSize）</summary>
    public int SongCount { get; set; }

    /// <summary>专辑数（albumSize）</summary>
    public int AlbumCount { get; set; }
}

/// <summary>专辑（歌手专辑列表 / 专辑页用，插件本地模型）</summary>
public class NeteaseAlbum
{
    /// <summary>专辑 ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>专辑名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>封面 URL</summary>
    public string? PicUrl { get; set; }

    /// <summary>歌曲数</summary>
    public int SongCount { get; set; }

    /// <summary>歌手名（展示用）</summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>发行时间（yyyy 展示用）</summary>
    public string? PublishYear { get; set; }
}
