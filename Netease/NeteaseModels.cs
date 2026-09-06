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

/// <summary>
/// 歌单动态信息（eapi /eapi/playlist/detail/dynamic）。
/// 歌单详情页头部三操作胶囊（分享/评论/收藏）与创建者行数据来源。
/// </summary>
public class PlaylistDynamicInfo
{
    /// <summary>创建者昵称</summary>
    public string? CreatorName { get; set; }
    /// <summary>创建者头像 URL</summary>
    public string? CreatorAvatar { get; set; }
    /// <summary>累计播放次数</summary>
    public long PlayCount { get; set; }
    /// <summary>收藏（订阅）人数</summary>
    public long SubscribedCount { get; set; }
    /// <summary>评论数</summary>
    public long CommentCount { get; set; }
    /// <summary>分享数</summary>
    public long ShareCount { get; set; }
    /// <summary>歌曲总数（与广场卡片 trackCount 一致，兜底校验用）</summary>
    public int TrackCount { get; set; }
}