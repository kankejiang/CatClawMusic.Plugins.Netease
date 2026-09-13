using CatClawMusic.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// Shell / 桌面双通道导航辅助（插件内等价宿主 DesktopNavigation，但不能引用宿主 Maui 程序集）。
/// Windows 桌面主窗口是 Window(DesktopBlankPage)（无 Shell）：Shell.Current 直接抛
/// InvalidOperationException 而非返回 null——"?." 与 "!= null" 均防不住，必须 try 捕获。
/// Android 竖屏/横屏都在 Shell 内，走导航栈。
/// Push 子页面：Shell 环境走导航栈；桌面无 Shell 以模态浮层打开（子页返回按钮自动 PopModal 关闭）。
/// </summary>
internal static class NeteaseNav
{
    /// <summary>安全获取 Shell；无 Shell 窗口返回 null（不抛）</summary>
    public static Shell? TryGetShell()
    {
        try { return Shell.Current; }
        catch { return null; }
    }

    /// <summary>Push 插件子页面：桌面壳层优先交给宿主内嵌到主内容区；Shell 走导航栈；其余用模态浮层</summary>
    public static async Task PushAsync(Page page)
    {
        // 桌面壳层（Windows 无 Shell）：内嵌到主内容区。
        // 原实现走 PushModalAsync → 整窗模态会盖住侧栏与底部播放条（用户反馈"歌单详情页挡住左侧栏"）。
        if (SubPageHost() is { CanEmbed: true } embedHost)
        {
            await embedHost.OpenEmbeddedAsync(page);
            return;
        }
        var shell = TryGetShell();
        if (shell != null)
        {
            await shell.Navigation.PushAsync(page);
            return;
        }
        var nav = WindowNav();
        if (nav != null) await nav.PushModalAsync(page);
    }

    /// <summary>返回：本页在模态栈 → PopModal；在 Shell 导航栈 → Pop；
    /// 都不在（桌面嵌入模式的主插件页）→ 宿主 INavigationService.GoBackAsync 关闭嵌入恢复 tab。</summary>
    public static async Task PopAsync(Page? self, IServiceProvider? services = null)
    {
        var nav = WindowNav();
        if (self != null && nav != null && nav.ModalStack.Contains(self))
        {
            await nav.PopModalAsync();
            return;
        }
        var shell = TryGetShell();
        if (self != null && shell != null && shell.Navigation.NavigationStack.Contains(self))
        {
            await shell.Navigation.PopAsync();
            return;
        }
        // 桌面内嵌模式：优先走宿主「内嵌页栈」逐级返回。
        // 必须放在 INavigationService.GoBackAsync 之前——后者只把内容区恢复成 tab 根内容，
        // 表现为"从二级页返回直接退出插件"（用户反馈）。
        if (SubPageHost() is { CanEmbed: true } embedHost)
        {
            await embedHost.CloseEmbeddedAsync();
            return;
        }
        if (services != null && services.GetService<INavigationService>() is { } hostNav)
        {
            await hostNav.GoBackAsync();
            return;
        }
        if (shell?.Navigation.NavigationStack.Count > 1)
            await shell.Navigation.PopAsync();
    }

    private static INavigation? WindowNav()
        => Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;

    /// <summary>
    /// 用 <paramref name="target"/> 替换当前登录子页：先关闭自己再推入目标页。
    /// <para>
    /// 登录页之间切换必须"替换"而不是叠加：若叠成 插件页 → 扫码页 → 手机号登录页，
    /// 登录成功只退一层就会停在另一个登录页（用户反馈"登录成功却回到扫码页"）。
    /// 保持登录页始终只有一层，成功后退一次必然回到插件主页。
    /// </para>
    /// </summary>
    public static async Task ReplaceAsync(Page self, Page target, IServiceProvider? services = null)
    {
        await PopAsync(self, services);
        await Task.Delay(180);   // 等关闭动画/内嵌栈恢复完成，避免紧接着的推入被覆盖
        await PushAsync(target);
    }

    /// <summary>宿主内嵌能力（未注册/不可用时返回 null，调用方回退原有推页方式）</summary>
    private static ISubPageHost? SubPageHost()
    {
        try { return Microsoft.Maui.IPlatformApplication.Current?.Services?.GetService<ISubPageHost>(); }
        catch { return null; }
    }
}
