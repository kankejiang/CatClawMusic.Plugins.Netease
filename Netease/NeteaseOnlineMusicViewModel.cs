using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CatClawMusic.Core.Interfaces;
using CatClawMusic.Core.Models;
using CatClawMusic.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 网易云在线音乐 ViewModel：分类歌单/排行榜/我的歌单/推荐歌单 → 歌曲列表 → 在线播放，
/// 搜索（歌曲/歌单/歌手三模式，带分页），私人漫游无限电台，红心/FM 垃圾桶，
/// 音质三档切换，播放失败轻提示。
/// <para>
/// 点击单曲时以当前完整列表构造播放队列（保留上下文，可上/下一首续播），
/// 不再用单曲替换整个队列。
/// </para>
/// </summary>
public partial class NeteaseOnlineMusicViewModel : ObservableObject
{
    private readonly NetEaseMusicPlugin _plugin;
    private readonly PlayQueue _queue;
    private readonly IAudioPlayerService _audioPlayer;
    private readonly IServiceProvider? _services;

    /// <summary>插件实例（视图层跳转歌手/专辑页需要）</summary>
    public NetEaseMusicPlugin Plugin => _plugin;

    // ── 浏览上下文（决定"加载更多"路由与返回行为）──
    private enum BrowseContext { Square, Toplists, SearchSongs, SearchPlaylists, SearchArtists, MyPlaylists, RecommendPlaylists, Songs }
    private BrowseContext _context = BrowseContext.Square;
    private int _playlistPage = 1;
    private int _searchPage = 1;
    private string _lastQuery = "";
    private bool _isLoadingMore;
    private bool _browsingToplists;

    // ── 私人漫游（FM）无限播放 ──
    /// <summary>是否处于私人漫游电台模式（播完自动续播并持续拉新歌补充缓冲）</summary>
    [ObservableProperty]
    private bool _isFmMode;

    /// <summary>已进入全局播放队列的 FM 歌曲的网易云 id（用于去重，避免重复追加）</summary>
    private readonly HashSet<string> _fmSongIds = new(StringComparer.Ordinal);
    /// <summary>防止并发追加缓冲</summary>
    private bool _fmAppending;
    /// <summary>进入 FM 前的播放模式（退出 FM 时恢复）</summary>
    private PlayMode? _fmSavedPlayMode;
    private const int FmBatchSize = 3;       // 每次补充拉取的数量（对齐官方：3 首 3 首拉）
    private const int FmBufferThreshold = 0; // 队列中剩余 FM 歌曲 ≤ 此值时开始补充（官方：播到本批最后一首时拉下一批）

    /// <summary>FM 缓冲定时兜底：宿主若不触发 PlaybackCompleted（Android 旧 APK 等），每 20 秒检查一次队列剩余并补货；与宿主事件路径并存（EnsureFmBufferAsync 内 _fmAppending 防重入）</summary>
    private IDispatcherTimer? _fmBufferTimer;

    /// <summary>行内按钮（红心/垃圾桶）点击后短暂抑制紧随其后的列表 SelectionChanged（600ms 窗口）</summary>
    private DateTime _suppressSelectionUntil = DateTime.MinValue;

    /// <summary>行内按钮点击时调用：标记接下来 600ms 内的列表选择应被忽略</summary>
    public void ArmSuppressSelection() => _suppressSelectionUntil = DateTime.UtcNow.AddMilliseconds(600);

    /// <summary>列表选择时调用：窗口内返回 true（应忽略本次选择），并复位</summary>
    public bool ConsumeSuppressSelection()
    {
        if (DateTime.UtcNow <= _suppressSelectionUntil)
        {
            _suppressSelectionUntil = DateTime.MinValue;
            return true;
        }
        return false;
    }

    // ── 账号登录状态 ──

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string? _accountName;

    /// <summary>账号按钮文案</summary>
    public string AccountButtonText => IsLoggedIn ? $"👤 {AccountName ?? "已登录"}" : "👤 登录";

    /// <summary>是否支持登录</summary>
    [ObservableProperty]
    private bool _supportsLogin;

    /// <summary>当前音源的浏览器登录配置</summary>
    public BrowserLoginInfo? CurrentLoginInfo { get; private set; }

    /// <summary>已红心歌曲 id 集合（登录后加载，用于列表 ❤ 状态）</summary>
    private HashSet<string> _likedIds = new(StringComparer.Ordinal);

    // ── 页面状态 ──

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "加载中...";

    /// <summary>轻量提示（播放失败/操作反馈，3 秒自动消失）</summary>
    [ObservableProperty]
    private string _tipMessage = "";

    public bool HasTip => !string.IsNullOrEmpty(TipMessage);
    private CancellationTokenSource? _tipCts;

    /// <summary>显示轻提示（非阻塞，3 秒后自动清空）</summary>
    public void ShowTip(string message)
    {
        _tipCts?.Cancel();
        var cts = new CancellationTokenSource();
        _tipCts = cts;
        TipMessage = message;
        _ = ClearTipAfterDelayAsync(cts.Token);
    }

    private async Task ClearTipAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(3000, token);
            if (!token.IsCancellationRequested) TipMessage = "";
        }
        catch { }
    }

    partial void OnTipMessageChanged(string value) => OnPropertyChanged(nameof(HasTip));

    // ── 音质 ──

    /// <summary>音质档位（0=标准 128k，1=高品 320k，2=无损 FLAC）</summary>
    [ObservableProperty]
    private int _qualityLevel;

    /// <summary>音质按钮文案</summary>
    public string QualityText => QualityLevel switch
    {
        0 => "🎚 标准",
        2 => "🎚 无损",
        _ => "🎚 高品",
    };

    partial void OnQualityLevelChanged(int value)
    {
        OnPropertyChanged(nameof(QualityText));
        _plugin.SetQualityLevel(value);
    }

    /// <summary>切换音质档位（循环：标准 → 高品 → 无损）</summary>
    [RelayCommand]
    private void CycleQuality()
    {
        QualityLevel = (QualityLevel + 1) % 3;
        if (QualityLevel == 2 && !IsLoggedIn)
            ShowTip("无损音质需登录，未登录时将自动使用高品");
    }

    // ── 歌单浏览模式 ──

    [ObservableProperty]
    private bool _showPlaylists = true;

    /// <summary>分类 chips 是否可见（仅歌单广场模式）</summary>
    [ObservableProperty]
    private bool _showCategories = true;

    /// <summary>歌单分类 chips（启动为硬编码兜底，分类接口返回后替换）</summary>
    public ObservableCollection<CategoryChipItem> Categories { get; } = new()
    {
        new("全部", true), new("华语", false), new("欧美", false), new("日韩", false),
        new("流行", false), new("摇滚", false), new("民谣", false), new("电子", false),
        new("轻音乐", false), new("ACG", false), new("怀旧", false), new("治愈", false),
        new("运动", false), new("夜晚", false),
    };

    [ObservableProperty]
    private string _selectedCategory = "全部";

    /// <summary>歌单列表</summary>
    public ObservableCollection<OnlinePlaylist> Playlists { get; } = new();

    [ObservableProperty]
    private string _playlistStatus = "";

    // ── 搜索模式 ──

    /// <summary>搜索类型 chips（歌曲/歌单/歌手）</summary>
    public ObservableCollection<CategoryChipItem> SearchModes { get; } = new()
    {
        new("歌曲", true), new("歌单", false), new("歌手", false),
    };

    private string _searchMode = "歌曲";

    [ObservableProperty]
    private string _searchQuery = "";

    // ── 歌手列表模式 ──

    [ObservableProperty]
    private bool _showArtists;

    /// <summary>歌手搜索结果</summary>
    public ObservableCollection<NeteaseArtist> Artists { get; } = new();

    // ── 歌曲列表模式 ──

    [ObservableProperty]
    private bool _showSongs;

    [ObservableProperty]
    private string _currentListTitle = "";

    /// <summary>歌曲列表</summary>
    public ObservableCollection<OnlineSong> Songs { get; } = new();

    [ObservableProperty]
    private string _songsStatus = "";

    public bool HasPlaylistSongs => Songs.Count > 0;

    public NeteaseOnlineMusicViewModel(NetEaseMusicPlugin plugin, PlayQueue queue, IAudioPlayerService audioPlayer, IServiceProvider? services = null)
    {
        _plugin = plugin;
        _queue = queue;
        _audioPlayer = audioPlayer;
        _services = services;
        Songs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPlaylistSongs));
        _audioPlayer.PlaybackCompleted += OnAudioPlaybackCompleted;
        _audioPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
        _qualityLevel = _plugin.QualityLevel;
    }

    /// <summary>页面出现时加载初始数据</summary>
    public async Task OnAppearingAsync()
    {
        await LoadLoginStateAsync();
        _ = LoadCategoriesAsync(); // 后台刷新官方分类，不阻塞首屏
        await LoadPlaylistsAsync();
    }

    // ── 分类 ──

    /// <summary>拉取官方歌单分类替换硬编码列表（失败保持现状）</summary>
    private async Task LoadCategoriesAsync()
    {
        try
        {
            var cats = await _plugin.GetCategoriesAsync();
            if (cats == null || cats.Count <= 1) return;
            Categories.Clear();
            foreach (var name in cats)
                Categories.Add(new CategoryChipItem(name, name == SelectedCategory));
        }
        catch { }
    }

    [RelayCommand]
    public async Task SelectCategoryAsync(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || category == SelectedCategory) return;
        SelectedCategory = category;
        foreach (var c in Categories) c.IsSelected = c.Name == category;
        await LoadPlaylistsAsync();
    }

    /// <summary>加载当前分类的歌单列表（第 1 页）</summary>
    public async Task LoadPlaylistsAsync()
    {
        _context = BrowseContext.Square;
        _playlistPage = 1;
        IsLoading = true;
        PlaylistStatus = "正在加载歌单...";
        Playlists.Clear();
        try
        {
            var category = SelectedCategory == "全部" ? null : SelectedCategory;
            var pls = await _plugin.GetPlaylistsPageAsync(category, 1);
            foreach (var pl in pls ?? new List<OnlinePlaylist>())
                Playlists.Add(pl);
            PlaylistStatus = Playlists.Count == 0 ? "该分类暂无歌单" : "";
            StatusText = "";
        }
        catch (Exception ex)
        {
            PlaylistStatus = $"歌单加载失败：{ex.Message}";
            Log.Debug("NeteasePlugin", $"[Netease] LoadPlaylists failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
        ShowCategories = true;
        ShowPlaylists = true;
        ShowSongs = false;
        ShowArtists = false;
    }

    /// <summary>滑到底部加载更多（歌单广场分页 / 歌曲搜索分页，其余上下文直接忽略）</summary>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (_isLoadingMore || IsLoading) return;
        _isLoadingMore = true;
        try
        {
            switch (_context)
            {
                case BrowseContext.Square when ShowPlaylists:
                {
                    var category = SelectedCategory == "全部" ? null : SelectedCategory;
                    var next = await _plugin.GetPlaylistsPageAsync(category, _playlistPage + 1);
                    if (next != null && next.Count > 0)
                    {
                        _playlistPage++;
                        foreach (var pl in next) Playlists.Add(pl);
                    }
                    break;
                }
                case BrowseContext.SearchSongs when ShowSongs && _lastQuery.Length > 0:
                {
                    var next = await _plugin.SearchAsync(_lastQuery, _searchPage + 1, SearchPageSize);
                    if (next != null && next.Count > 0)
                    {
                        _searchPage++;
                        AppendSongs(next);
                    }
                    break;
                }
            }
        }
        catch { }
        finally { _isLoadingMore = false; }
    }

    // ── 歌单/榜单/我的/推荐 入口 ──

    [RelayCommand]
    public async Task OpenPlaylistAsync(OnlinePlaylist? playlist)
    {
        if (playlist == null) return;
        LeaveFmMode();
        IsLoading = true;
        SongsStatus = "正在加载歌曲...";
        CurrentListTitle = playlist.Name;
        _context = BrowseContext.Songs;
        Songs.Clear();
        try
        {
            var pageSize = playlist.SongCount > 0 ? playlist.SongCount : 200;
            var songs = await _plugin.GetPlaylistSongsAsync(playlist, 1, pageSize);
            FillSongs(songs);
            SongsStatus = Songs.Count == 0 ? "歌单为空" : "";
        }
        catch (Exception ex)
        {
            SongsStatus = $"歌曲加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        ShowPlaylists = false;
        ShowArtists = false;
        ShowSongs = true;
    }

    [RelayCommand]
    public async Task BackToPlaylistsAsync()
    {
        ShowSongs = false;
        ShowArtists = false;
        ShowPlaylists = true;
        if (_browsingToplists)
        {
            _browsingToplists = false;
            await LoadPlaylistsAsync();
        }
    }

    [RelayCommand]
    public async Task LoadToplistsAsync()
    {
        LeaveFmMode();
        IsLoading = true;
        PlaylistStatus = "正在加载排行榜...";
        Playlists.Clear();
        try
        {
            var lists = await _plugin.GetToplistsAsync();
            foreach (var pl in lists) Playlists.Add(pl);
            _browsingToplists = true;
            _context = BrowseContext.Toplists;
            PlaylistStatus = Playlists.Count == 0 ? "排行榜加载失败" : "";
        }
        catch (Exception ex)
        {
            PlaylistStatus = $"排行榜加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        ShowCategories = false;
        ShowSongs = false;
        ShowArtists = false;
        ShowPlaylists = true;
    }

    /// <summary>我的歌单（需登录）</summary>
    [RelayCommand]
    public async Task LoadMyPlaylistsAsync()
    {
        if (!IsLoggedIn) { ShowTip("登录后可查看我的歌单"); return; }
        LeaveFmMode();
        IsLoading = true;
        PlaylistStatus = "正在加载我的歌单...";
        Playlists.Clear();
        try
        {
            var lists = await _plugin.GetUserPlaylistsAsync();
            foreach (var pl in lists) Playlists.Add(pl);
            _context = BrowseContext.MyPlaylists;
            PlaylistStatus = Playlists.Count == 0 ? "暂无歌单" : "";
        }
        catch (Exception ex)
        {
            PlaylistStatus = $"我的歌单加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        ShowCategories = false;
        ShowSongs = false;
        ShowArtists = false;
        ShowPlaylists = true;
    }

    /// <summary>推荐歌单（匿名可用，无需登录）</summary>
    [RelayCommand]
    public async Task LoadRecommendPlaylistsAsync()
    {
        LeaveFmMode();
        IsLoading = true;
        PlaylistStatus = "正在加载推荐歌单...";
        Playlists.Clear();
        try
        {
            var lists = await _plugin.GetRecommendPlaylistsAsync();
            foreach (var pl in lists) Playlists.Add(pl);
            _context = BrowseContext.RecommendPlaylists;
            PlaylistStatus = Playlists.Count == 0 ? "今日暂无推荐歌单" : "";
        }
        catch (Exception ex)
        {
            PlaylistStatus = $"推荐歌单加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        ShowCategories = false;
        ShowSongs = false;
        ShowArtists = false;
        ShowPlaylists = true;
    }

    // ── 私人漫游 / 每日推荐 ──

    [RelayCommand]
    public async Task LoadPrivateFmAsync()
    {
        IsLoading = true;
        SongsStatus = "正在获取私人漫游...";
        CurrentListTitle = "🎧 私人漫游";
        _context = BrowseContext.Songs;
        Songs.Clear();
        try
        {
            var songs = await _plugin.GetPrivateFmAsync(3);  // 官方节奏：初拉 3 首，播到本批最后一首时再拉 3 首
            FillSongs(songs, markAsFm: true);
            SongsStatus = Songs.Count == 0 ? "私人漫游暂无歌曲，稍后再试" : "";
        }
        catch (Exception ex)
        {
            SongsStatus = $"私人漫游获取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        IsFmMode = Songs.Count > 0; // 私人漫游电台模式
        // 私人漫游 = 电台按钮：加载后直接开播第一首（不进歌曲列表视图），
        // 播放中播完自动续拉新歌（PlayFmSongAsync 的无限电台逻辑）；界面保持入口卡视图，播放条显示当前歌
        if (IsFmMode && Songs.Count > 0)
        {
            await PlayFmSongAsync(Songs[0]);
        }
    }

    [RelayCommand]
    public async Task LoadDailyRecommendAsync()
    {
        LeaveFmMode();
        IsLoading = true;
        SongsStatus = "正在获取每日推荐...";
        CurrentListTitle = "📅 每日推荐";
        _context = BrowseContext.Songs;
        Songs.Clear();
        try
        {
            var songs = await _plugin.GetDailyRecommendAsync(20);
            FillSongs(songs);
            SongsStatus = Songs.Count == 0 ? "每日推荐暂无歌曲" : "";
        }
        catch (Exception ex)
        {
            SongsStatus = $"每日推荐获取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        ShowPlaylists = false;
        ShowArtists = false;
        ShowSongs = true;
    }

    // ── 搜索（歌曲/歌单/歌手三模式）──

    private const int SearchPageSize = 20;

    /// <summary>切换搜索类型（若已有搜索词与结果，立即按新类型重搜）</summary>
    [RelayCommand]
    public async Task SelectSearchModeAsync(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == _searchMode) return;
        _searchMode = mode;
        foreach (var c in SearchModes) c.IsSelected = c.Name == mode;
        if (!string.IsNullOrWhiteSpace(_lastQuery)) await SearchSongsAsync();
    }

    [RelayCommand]
    public async Task SearchSongsAsync()
    {
        var q = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;
        LeaveFmMode();
        _lastQuery = q;
        _searchPage = 1;
        IsLoading = true;
        try
        {
            switch (_searchMode)
            {
                case "歌单":
                {
                    _context = BrowseContext.SearchPlaylists;
                    SongsStatus = "";
                    CurrentListTitle = $"搜索歌单：{q}";
                    Playlists.Clear();
                    var pls = await _plugin.SearchPlaylistsAsync(q, 30);
                    foreach (var pl in pls ?? new List<OnlinePlaylist>()) Playlists.Add(pl);
                    PlaylistStatus = Playlists.Count == 0 ? "没有找到相关歌单" : "";
                    ShowCategories = false;
                    ShowSongs = false;
                    ShowArtists = false;
                    ShowPlaylists = true;
                    break;
                }
                case "歌手":
                {
                    _context = BrowseContext.SearchArtists;
                    Artists.Clear();
                    var artists = await _plugin.SearchArtistsAsync(q, 30);
                    foreach (var a in artists ?? new List<NeteaseArtist>()) Artists.Add(a);
                    if (Artists.Count == 0) ShowTip("没有找到相关歌手");
                    ShowCategories = false;
                    ShowPlaylists = false;
                    ShowSongs = false;
                    ShowArtists = true;
                    break;
                }
                default: // 歌曲
                {
                    _context = BrowseContext.SearchSongs;
                    SongsStatus = "正在搜索...";
                    CurrentListTitle = $"搜索：{q}";
                    Songs.Clear();
                    var songs = await _plugin.SearchAsync(q, 1, SearchPageSize);
                    FillSongs(songs);
                    SongsStatus = Songs.Count == 0 ? "没有找到相关歌曲，换个关键词试试" : "";
                    ShowPlaylists = false;
                    ShowArtists = false;
                    ShowSongs = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ShowTip($"搜索失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── 播放 ──

    /// <summary>全部播放：当前列表整队入队，从第一首开始</summary>
    public Task PlayAllAsync() => PlayFromAsync(Songs.FirstOrDefault());

    /// <summary>播放单首在线歌曲：以当前完整列表构造队列，从点击位置播放（保留上/下一首上下文）</summary>
    public Task PlaySongAsync(OnlineSong song) => PlayFromAsync(song);

    private async Task PlayFromAsync(OnlineSong? startSong)
    {
        if (startSong == null) return;
        if (IsFmMode)
        {
            await PlayFmSongAsync(startSong);
            return;
        }
        LeaveFmMode();
        var list = Songs.ToList();
        if (list.Count == 0) return;

        var temp = new List<Song>();
        var failedTitles = new List<string>();
        foreach (var s in list)
        {
            string? url = null;
            try { url = await _plugin.GetPlayUrlAsync(s, QualityLevel); } catch { }
            if (string.IsNullOrWhiteSpace(url)) { failedTitles.Add(s.Title); continue; }
            temp.Add(NeteasePlaybackHelper.ToQueueSong(s, url));
        }
        if (temp.Count == 0)
        {
            ShowTip($"《{startSong.Title}》暂时无法播放（可能是 VIP 歌曲）");
            return;
        }

        _queue.SetSongs(temp);
        var target = temp.FirstOrDefault(s => s.RemoteId == $"{startSong.Platform}:{startSong.Id}") ?? temp[0];
        _queue.SelectSong(target.Id);
        try { await _audioPlayer.PlayAsync(target.FilePath); }
        catch { ShowTip("播放失败，请重试"); }

        if (failedTitles.Count > 0)
        {
            ShowTip(failedTitles.Contains(startSong.Title)
                ? $"《{startSong.Title}》无法播放（可能是 VIP 歌曲），已跳过 {failedTitles.Count} 首"
                : $"{failedTitles.Count} 首无法播放已跳过");
        }
    }

    // ── 私人漫游（FM）无限电台 ──

    /// <summary>
    /// 私人漫游播放：把当前 FM 列表整批入队，播放被点击的那首，
    /// 并开启"无限电台"续播——播完自动拉新歌追加到队尾。
    /// </summary>
    private async Task PlayFmSongAsync(OnlineSong clicked)
    {
        var list = Songs.ToList();
        if (list.Count == 0) return;

        // 整批解析播放直链入队
        var temp = new List<Song>();
        foreach (var os in list)
        {
            string? url = null;
            try { url = await _plugin.GetPlayUrlAsync(os, QualityLevel); }
            catch { }
            if (string.IsNullOrWhiteSpace(url)) continue;
            temp.Add(NeteasePlaybackHelper.ToQueueSong(os, url));
        }
        if (temp.Count == 0)
        {
            ShowTip("私人漫游暂时取不到播放链接");
            return;
        }

        _queue.SetSongs(temp);
        _fmSongIds.Clear();
        foreach (var os in list)
            if (!string.IsNullOrWhiteSpace(os.Id)) _fmSongIds.Add(os.Id);
        IsFmMode = true;
        if (_fmSavedPlayMode == null) _fmSavedPlayMode = _queue.PlayMode;
        _queue.PlayMode = PlayMode.Sequential; // FM 为顺序无限电台，不循环早期歌曲
        _queue.IsFmMode = true;  // 通知宿主模式按钮切换为"无限"显示

        var target = temp.FirstOrDefault(s => s.RemoteId == $"{clicked.Platform}:{clicked.Id}") ?? temp[0];
        _queue.SelectSong(target.Id);
        try { await _audioPlayer.PlayAsync(target.FilePath); }
        catch { ShowTip("播放失败，请重试"); }

        // FM 缓冲定时兜底：宿主若不触发 PlaybackCompleted 事件（如 Android 旧 APK），定时检查并补货
        EnsureFmBufferTimerStart();
    }

    /// <summary>启动 FM 缓冲定时兜底（每 20 秒检查一次，确保不依赖宿主 PlaybackCompleted 事件）</summary>
    private void EnsureFmBufferTimerStart()
    {
        if (_fmBufferTimer != null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        _fmBufferTimer = dispatcher.CreateTimer();
        _fmBufferTimer.Interval = TimeSpan.FromSeconds(20);
        _fmBufferTimer.IsRepeating = true;
        _fmBufferTimer.Tick += OnFmBufferTimerTick;
        _fmBufferTimer.Start();
    }

    private void OnFmBufferTimerTick(object? sender, EventArgs e)
    {
        // 定时兜底补货；EnsureFmBufferAsync 内部 _fmAppending 防与宿主事件路径重入
        if (IsFmMode) _ = EnsureFmBufferAsync();
    }

    /// <summary>离开私人漫游电台模式（恢复用户原播放模式）</summary>
    private void LeaveFmMode()
    {
        if (!IsFmMode && _fmSavedPlayMode == null) return;
        IsFmMode = false;
        _queue.IsFmMode = false;  // 通知宿主模式按钮恢复普通循环
        if (_fmBufferTimer != null) { _fmBufferTimer.Stop(); _fmBufferTimer = null; }
        if (_fmSavedPlayMode is PlayMode saved)
        {
            _queue.PlayMode = saved;
            _fmSavedPlayMode = null;
        }
    }

    /// <summary>FM 垃圾桶：不再推荐该歌曲，并从队列/列表移除；正在播放则自动切下一首</summary>
    [RelayCommand]
    public async Task TrashFmSongAsync(OnlineSong? song)
    {
        if (song == null || !IsFmMode) return;
        ArmSuppressSelection();
        var trashTask = _plugin.FmTrashAsync(song.Id);

        var qSong = _queue.GetSongs().FirstOrDefault(s => s.RemoteId == $"{song.Platform}:{song.Id}");
        bool wasCurrent = qSong != null && ReferenceEquals(_queue.CurrentSong, qSong);
        if (qSong != null) _queue.RemoveSong(qSong.Id);
        Songs.Remove(song);

        if (wasCurrent)
        {
            var next = _queue.CurrentSong;
            if (next != null)
            {
                try { await _audioPlayer.PlayAsync(next.FilePath); } catch { }
            }
            else
            {
                try { await _audioPlayer.StopAsync(); } catch { }
            }
        }

        var ok = await trashTask;
        ShowTip(ok ? "已减少此类推荐" : "已从队列移除");
    }

    // ── 红心 ──

    /// <summary>红心/取消红心（FM 歌曲走 radio/like，普通歌曲走「我喜欢的音乐」歌单增删）</summary>
    [RelayCommand]
    public async Task ToggleLikeAsync(OnlineSong? song)
    {
        if (song == null) return;
        ArmSuppressSelection();
        if (!IsLoggedIn) { ShowTip("登录后可使用红心功能"); return; }
        song.Internal ??= new Dictionary<string, object>();
        bool current = song.Internal.TryGetValue("Liked", out var v) && v is true;
        bool target = !current;
        bool fromFm = song.Internal.TryGetValue("FromFm", out var f) && f is true;

        bool ok = fromFm
            ? await _plugin.FmLikeAsync(song.Id, target)
            : await _plugin.LikeSongAsync(song.Id, target);

        if (ok)
        {
            song.Internal["Liked"] = target;
            if (target) _likedIds.Add(song.Id); else _likedIds.Remove(song.Id);
            RefreshSongRow(song);
            ShowTip(target ? "已添加到我喜欢的音乐" : "已取消红心");
            // 同步到宿主本地数据库（Songs[Cache] + Favorites），宿主收藏列表可见；失败静默不影响红心主流程
            _ = SyncFavoriteToDatabaseAsync(song, target);
        }
        else
        {
            // 红心请求失败：最常见原因是登录 Cookie 已过期（MUSIC_U 失效，服务器拒绝操作）
            ShowTip("操作失败，可能登录已过期，请点击右上角账号重新登录");
        }
    }

    /// <summary>
    /// 红心状态同步到宿主本地数据库：红心 → 写入 Songs（Source=Cache，不入本地曲库视图）+ Favorites；
    /// 取消红心 → 移除 Favorites 记录（Songs 记录保留，Cleanup 不会误删已收藏项）。
    /// 网易云红心主逻辑在服务器（LikeSongAsync），这里只是本地可查询的镜像。
    /// </summary>
    private async Task SyncFavoriteToDatabaseAsync(OnlineSong os, bool liked)
    {
        try
        {
            if (_services?.GetService(typeof(CatClawMusic.Data.MusicDatabase)) is not CatClawMusic.Data.MusicDatabase db) return;
            string remoteId = $"{os.Platform}:{os.Id}";
            // 查重：已收藏集合里找同 RemoteId（防重复插入/取消时定位）
            var favSongs = await db.GetFavoriteSongsAsync();
            var existing = favSongs.FirstOrDefault(s => s.RemoteId == remoteId);

            if (liked)
            {
                if (existing != null) return; // 已入库
                string? url = null;
                try { url = await _plugin.GetPlayUrlAsync(os, QualityLevel); } catch { }
                var song = new Song
                {
                    Title = os.Title,
                    Artist = os.Artist,
                    Album = os.Album,
                    Duration = (int)(os.DurationMs / 1000),
                    FilePath = url ?? "",
                    RemoteId = remoteId,
                    Source = SongSource.Cache,
                    CoverArtPath = os.CoverUrl,
                    AllArtists = os.Artist,
                };
                await db.InsertSongAsync(song);
                if (song.Id > 0) await db.SetFavoriteAsync(song.Id, true);
            }
            else if (existing != null)
            {
                await db.SetFavoriteAsync(existing.Id, false);
            }
        }
        catch { /* 本地镜像失败不影响服务器红心 */ }
    }

    /// <summary>替换集合中的实例触发该行重新渲染（OnlineSong 无 INPC，借此刷新红心图标）</summary>
    private void RefreshSongRow(OnlineSong song)
    {
        var idx = Songs.IndexOf(song);
        if (idx >= 0) Songs[idx] = song;
    }

    // ── 歌曲列表填充辅助 ──

    /// <summary>填充歌曲列表（标记红心状态；FM 歌曲额外标记来源）</summary>
    private void FillSongs(List<OnlineSong>? songs, bool markAsFm = false)
    {
        Songs.Clear();
        AppendCore(songs, markAsFm);
    }

    private void AppendSongs(List<OnlineSong>? songs) => AppendCore(songs, false);

    private void AppendCore(List<OnlineSong>? songs, bool markAsFm)
    {
        if (songs == null) return;
        foreach (var s in songs)
        {
            s.Internal ??= new Dictionary<string, object>();
            s.Internal["Liked"] = _likedIds.Contains(s.Id);
            if (markAsFm) s.Internal["FromFm"] = true;
            Songs.Add(s);
        }
    }

    // ── 播放完成事件：打卡 + FM 缓冲补充 ──

    /// <summary>
    /// 每首歌开始播放（isPlaying=true）时检查 FM 缓冲：若当前是队列最后一首（remaining=0）立即补货。
    /// 关键：补货必须【提前到最后一首开始播时】发起，而不是播完才拉——
    /// 否则宿主"播完队尾即停"（PeekNextSong 为 null 就 Stop）会先执行，异步补货晚一步导致队列空、电台中断。
    /// </summary>
    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        if (!isPlaying || !IsFmMode) return;
        MainThread.BeginInvokeOnMainThread(async () => await EnsureFmBufferAsync());
    }

    private void OnAudioPlaybackCompleted(object? sender, EventArgs e)
    {
        // 听歌打卡（提升推荐精度；静默失败）
        var finished = _queue.CurrentSong;
        if (finished?.RemoteId != null && finished.RemoteId.StartsWith("netease:", StringComparison.Ordinal))
        {
            var sid = finished.RemoteId.Substring("netease:".Length);
            _ = _plugin.ScrobbleAsync(sid, finished.Duration * 1000L);
        }

        if (!IsFmMode) return;
        MainThread.BeginInvokeOnMainThread(async () => await EnsureFmBufferAsync());
    }

    /// <summary>若队列中剩余 FM 歌曲不足阈值，则拉取新一批追加到队尾</summary>
    private async Task EnsureFmBufferAsync()
    {
        if (!IsFmMode || _fmAppending) return;
        var songs = _queue.GetSongs();
        var current = _queue.CurrentSong;
        if (current == null || songs.Count == 0) return;
        int idx = -1;
        for (int i = 0; i < songs.Count; i++)
        {
            if (ReferenceEquals(songs[i], current)) { idx = i; break; } // 入队实例与 CurrentSong 为同一对象
        }
        if (idx < 0) return;
        int remaining = songs.Count - idx - 1;
        if (remaining > FmBufferThreshold) return;
        await AppendFmBatchAsync();
    }

    /// <summary>向私人漫游队列追加一批新歌（去重后入队尾）；若去重后无新歌，放宽去重重试一次保证电台不断</summary>
    private async Task AppendFmBatchAsync()
    {
        _fmAppending = true;
        try
        {
            int added = await AppendFmBatchCoreAsync(allowDuplicate: false);
            if (added == 0)
            {
                // 全部被 _fmSongIds 去重（网易云接口常返回固定候选集）或取不到直链：
                // 放宽去重再拉一次，允许少量重复，保证电台不中断
                System.Diagnostics.Debug.WriteLine("[NetEaseFM] 补货首批全部去重/失败，放宽去重重试");
                added = await AppendFmBatchCoreAsync(allowDuplicate: true);
            }
            if (added == 0)
                System.Diagnostics.Debug.WriteLine("[NetEaseFM] 补货失败（接口无返回或无直链），队列即将耗尽");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetEaseFM] 补货异常: {ex.Message}");
        }
        finally { _fmAppending = false; }
    }

    private async Task<int> AppendFmBatchCoreAsync(bool allowDuplicate)
    {
        var batch = await _plugin.GetPrivateFmAsync(FmBatchSize);
        if (batch == null) return 0;
        int added = 0;
        foreach (var os in batch)
        {
            if (os == null || string.IsNullOrWhiteSpace(os.Id)) continue;
            if (!allowDuplicate && _fmSongIds.Contains(os.Id)) continue;
            string? url = null;
            try { url = await _plugin.GetPlayUrlAsync(os, QualityLevel); } catch { }
            if (string.IsNullOrWhiteSpace(url)) continue;
            os.Internal ??= new Dictionary<string, object>();
            os.Internal["FromFm"] = true;
            _queue.AddToEnd(NeteasePlaybackHelper.ToQueueSong(os, url));
            _fmSongIds.Add(os.Id);
            added++;
        }
        return added;
    }

    /// <summary>
    /// 页面消失时的轻量清理。
    /// 注意：VM 为插件级单例，私人漫游（FM）电台是全局播放行为——【不】在此退出 FM、
    /// 【不】解绑播放完成事件、【不】停补货 Timer，否则打开播放页（本页 OnDisappearing）
    /// 会杀死正在播放的电台（按钮恢复普通循环、补货失效、3 首播完即断）。
    /// FM 退出由用户明确操作触发：搜索/每日推荐/普通播放/打开歌单（对应各方法内的 LeaveFmMode）。
    /// </summary>
    public void Detach()
    {
        // 仅非 FM 时清空历史 id 集，避免内存增长；FM 期间保留（补货去重依赖）
        if (!IsFmMode) _fmSongIds.Clear();
    }

    // ── 登录 ──

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(AccountButtonText));
    partial void OnAccountNameChanged(string? value) => OnPropertyChanged(nameof(AccountButtonText));

    public async Task LoadLoginStateAsync()
    {
        try
        {
            IsLoggedIn = await _plugin.IsLoggedInAsync();
            AccountName = IsLoggedIn ? await _plugin.GetAccountNameAsync() ?? "已登录" : null;
            CurrentLoginInfo = await _plugin.GetBrowserLoginInfoAsync();
            SupportsLogin = CurrentLoginInfo != null;
            if (IsLoggedIn) _ = RefreshLikedIdsAsync();
        }
        catch
        {
            IsLoggedIn = false;
            AccountName = null;
            SupportsLogin = false;
            CurrentLoginInfo = null;
        }
    }

    /// <summary>加载红心集合并刷新当前列表的 ❤ 状态</summary>
    private async Task RefreshLikedIdsAsync()
    {
        try
        {
            _likedIds = await _plugin.GetLikedSongIdsAsync();
            foreach (var s in Songs)
            {
                s.Internal ??= new Dictionary<string, object>();
                s.Internal["Liked"] = _likedIds.Contains(s.Id);
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        try
        {
            await _plugin.LogoutAsync();
            IsLoggedIn = false;
            AccountName = null;
            _likedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in Songs)
            {
                if (s.Internal != null) s.Internal["Liked"] = false;
            }
        }
        catch { }
    }
}

/// <summary>播放队列构造辅助（整页/子 tab/歌手页/专辑页共用）</summary>
public static class NeteasePlaybackHelper
{
    private static int _idSeq;

    /// <summary>在线歌曲 → 带唯一负 Id 的临时 Song（队列索引/去重需要唯一 Id）</summary>
    public static Song ToQueueSong(OnlineSong os, string url) => new()
    {
        Id = -(System.Threading.Interlocked.Increment(ref _idSeq) + 1000),
        Title = os.Title,
        Artist = os.Artist,
        Album = os.Album,
        Duration = (int)(os.DurationMs / 1000),
        FilePath = url,
        RemoteId = $"{os.Platform}:{os.Id}",
        Source = SongSource.Local,
        AllArtists = os.Artist,
        // 封面：网易云 coverUrl（http）→ 宿主 CoverArtPath。宿主 LoadCoverAsync 检测到 http/https
        // 会自动下载并缓存到 cover_{Id}.jpg，播放页即可显示封面（此前未赋值导致播放页无封面）。
        CoverArtPath = os.CoverUrl,
    };

    /// <summary>
    /// 通用"列表播放"：整队解析直链入队，从 <paramref name="start"/> 开始播放。
    /// 歌手页/专辑页等轻量场景使用（无 FM 逻辑）。
    /// </summary>
    public static async Task<int> PlayListAsync(IServiceProvider services, NetEaseMusicPlugin plugin,
        IReadOnlyList<OnlineSong> songs, OnlineSong start)
    {
        var queue = services.GetRequiredService<PlayQueue>();
        var player = services.GetRequiredService<IAudioPlayerService>();
        var temp = new List<Song>();
        foreach (var s in songs)
        {
            string? url = null;
            try { url = await plugin.GetPlayUrlAsync(s, plugin.QualityLevel); } catch { }
            if (string.IsNullOrWhiteSpace(url)) continue;
            temp.Add(ToQueueSong(s, url));
        }
        if (temp.Count == 0) return 0;
        queue.SetSongs(temp);
        var target = temp.FirstOrDefault(s => s.RemoteId == $"{start.Platform}:{start.Id}") ?? temp[0];
        queue.SelectSong(target.Id);
        try { await player.PlayAsync(target.FilePath); } catch { }
        return temp.Count;
    }
}

/// <summary>歌单分类 chip 项（分类与搜索模式共用）</summary>
public partial class CategoryChipItem : ObservableObject
{
    /// <summary>显示名称</summary>
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>构造函数</summary>
    public CategoryChipItem(string name, bool isSelected)
    {
        Name = name;
        IsSelected = isSelected;
    }
}
