using CatClawMusic.Core.Models;
using Microsoft.Maui.Controls;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 歌曲行「⋮」菜单：全插件统一入口（日推 / 歌单广场 / 搜索 / 歌单详情 / 专辑 / 歌手 共用）。
/// 由 <c>NeteaseUiKit.CreateSongItemTemplate</c> 的 <c>SongRowOptions.MenuCommand</c> 触发。
/// 原先歌单详情页有一套私有菜单（仅 播放/下载/红心/评论），其余列表用内联字形按钮
/// （红心 / 垃圾桶 / 相似 / MV / 评论）——两者样式与能力都不一致，这里统一为一套。
/// 可选能力按上下文降级：拿不到 ViewModel 时（专辑/歌手页）只保留 播放 / 下载。
/// </summary>
internal static class NeteaseSongMenu
{
    private const string Play = "▶ 播放";
    private const string Download = "⬇ 下载音乐";
    private const string Like = "🤍 红心";
    private const string Unlike = "❤ 取消红心";
    private const string Similar = "🎼 相似歌曲";
    private const string Mv = "🎬 观看 MV";
    private const string Comment = "💬 查看评论";
    private const string Trash = "🗑 不感兴趣（换一首）";
    private const string Cancel = "取消";

    /// <summary>弹出歌曲操作菜单</summary>
    /// <param name="host">承载对话框的页面</param>
    /// <param name="song">目标歌曲</param>
    /// <param name="vm">插件主 ViewModel（可为 null：专辑/歌手页无 VM，此时仅保留播放/下载）</param>
    /// <param name="plugin">插件实例（下载能力）</param>
    /// <param name="services">宿主服务（下载落盘用）</param>
    /// <param name="playOverride">自定义播放实现（专辑/歌手页用自己的播放链路）</param>
    public static async Task ShowAsync(Page host, OnlineSong song,
        NeteaseOnlineMusicViewModel? vm, NetEaseMusicPlugin plugin, IServiceProvider? services,
        Func<OnlineSong, Task>? playOverride = null)
    {
        var items = new List<string> { Play, Download };
        if (vm != null)
        {
            items.Add(NeteaseUiKit.SongIsLiked(song) ? Unlike : Like);   // 按当前状态出文案
            items.Add(Similar);
            if (NeteaseUiKit.SongHasMv(song)) items.Add(Mv);
            items.Add(Comment);
            if (vm.IsFmMode) items.Add(Trash);
        }

        string? pick;
        try { pick = await host.DisplayActionSheetAsync(song.Title, Cancel, null, items.ToArray()); }
        catch { return; }
        if (string.IsNullOrEmpty(pick) || pick == Cancel) return;

        try
        {
            switch (pick)
            {
                case Play:
                    if (playOverride != null) await playOverride(song);
                    else if (vm != null) await vm.PlaySongAsync(song);
                    break;

                case Download:
                    var quality = await plugin.PickDownloadQualityAsync();
                    if (quality != null)
                    {
                        var ok = await plugin.DownloadOnlineSongAsync(song, quality, services);
                        if (!ok) await AlertAsync(host, "下载失败：无法获取播放直链");
                    }
                    break;

                case Like:
                case Unlike:
                    if (vm != null) await vm.ToggleLikeAsync(song);
                    break;

                case Similar:
                    if (vm?.LoadSimilarSongsCommand?.CanExecute(song) == true) vm.LoadSimilarSongsCommand.Execute(song);
                    break;

                case Mv:
                    if (vm?.OpenMvCommand?.CanExecute(song) == true) vm.OpenMvCommand.Execute(song);
                    break;

                case Comment:
                    if (vm != null) await vm.OpenCommentsAsync(song);
                    break;

                case Trash:
                    if (vm?.TrashFmSongCommand?.CanExecute(song) == true) vm.TrashFmSongCommand.Execute(song);
                    break;
            }
        }
        catch (Exception ex)
        {
            await AlertAsync(host, $"操作失败：{ex.Message}");
        }
    }

    private static async Task AlertAsync(Page host, string message)
    {
        try { await host.DisplayAlertAsync("网易云音乐", message, "知道了"); } catch { }
    }
}
