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

    /// <summary>Push 插件子页面：Shell 导航栈优先；桌面无 Shell 用窗口级模态浮层</summary>
    public static async Task PushAsync(Page page)
    {
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
}
