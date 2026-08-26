namespace CatClawMusic.Plugins.Netease;

/// <summary>歌手信息（cloudsearch type=100 / 歌手搜索）</summary>
public class NeteaseArtist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PicUrl { get; set; }
    public int SongCount { get; set; }
    public int AlbumCount { get; set; }
}

/// <summary>专辑信息（/api/artist/albums / 专辑搜索）</summary>
public class NeteaseAlbum
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PicUrl { get; set; }
    public int SongCount { get; set; }
    public string? ArtistName { get; set; }
    public string? PublishYear { get; set; }
}

/// <summary>网易云评论（歌曲热门评论区用）</summary>
public class SongComment
{
    public long Id { get; set; }
    public string User { get; set; } = "";
    public string Content { get; set; } = "";
    public long Time { get; set; }
    public int LikedCount { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>搜索联想词（类型：song/album/artist）</summary>
public class SearchSuggestion
{
    public string Word { get; set; } = "";
    public string Type { get; set; } = "song";
}

/// <summary>相似/相关歌单卡</summary>
public class SimilarPlaylistInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CoverUrl { get; set; }
    public int SongCount { get; set; }
    public int PlayCount { get; set; }
    public string Creator { get; set; } = "";
}