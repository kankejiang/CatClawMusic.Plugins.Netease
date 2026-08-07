using CatClawMusic.Core.Interfaces;
using Microsoft.Maui.Controls;

namespace CatClawMusic.Plugins.Netease;

/// <summary>
/// 发现页子 tab 贡献者接口：插件借此在宿主「发现」页贡献一个内嵌子 tab（位于「推荐」右侧），
/// 选中时宿主把 <see cref="CreateTabView"/> 返回的 MAUI <see cref="View"/> 挂载到发现页面板区，
/// 实现“客户端空壳、插件自治”的发现页内嵌体验（区别于 <see cref="IViewContributorPlugin"/> 的整页 Push）。
/// <para>
/// 本接口定义在插件程序集内（而非 CatClawMusic.Core），目的是让本仓库自洽、不改动宿主。
/// 宿主后续接入时有两种等价方式（任选其一）：
/// <list type="bullet">
///   <item><b>方式 A（强类型）</b>：宿主在 CatClawMusic.Core 复制同一全限定名接口
///         <c>CatClawMusic.Plugins.Netease.IDiscoverTabPlugin</c>（成员签名一致）并在
///         PluginManager 反射适配体系里按 FullName 匹配、包装为适配器；之后即可用
///         <c>GetEnabledPlugins&lt;IDiscoverTabPlugin&gt;()</c> 获取。</item>
///   <item><b>方式 B（鸭子类型，推荐，零 Core 改动）</b>：宿主对每个已启用插件实例用反射探测以下成员，
///         存在即视为发现子 tab 贡献者：
///         <c>string TabTitle</c>、<c>string TabIcon</c>、<c>int TabOrder</c>、
///         <c>object CreateTabView(IServiceProvider)</c>（返回 Microsoft.Maui.Controls.View）。
///         这样宿主无需新增 Core 接口即可接入任意插件。</item>
/// </list>
/// </para>
/// <para>
/// 排序约定：<c>TabOrder</c> 越小越靠左。建议「推荐」为 0，插件用 1 即紧随其右侧；
/// 宿主按 TabOrder 升序、同序按插件名称排序后插入到「推荐」右侧。
/// </para>
/// </summary>
public interface IDiscoverTabPlugin : IPlugin
{
    /// <summary>子 tab 显示标题（如「网易云音乐」）。</summary>
    string TabTitle { get; }

    /// <summary>子 tab 图标（Emoji 或图片资源名）。</summary>
    string TabIcon { get; }

    /// <summary>
    /// 子 tab 排序权重。越小越靠左；建议「推荐」=0，插件=1 即紧随其右侧。
    /// 宿主按升序、同序按插件名称排序后插入到「推荐」右侧。
    /// </summary>
    int TabOrder { get; }

    /// <summary>
    /// 创建并返回子 tab 内容视图（MAUI <see cref="View"/>，非整页 ContentPage）。
    /// <para>
    /// 每次调用应返回新实例。宿主将其挂载到发现页面板区。
    /// 通过 <paramref name="services"/> 可获取宿主服务（如 PlayQueue、IAudioPlayerService）。
    /// 返回类型用 <see cref="object"/> 而非 <see cref="View"/>，是为避免 Core 直接依赖 Microsoft.Maui.Controls；
    /// 宿主收到后强制转换为 <see cref="View"/> 即可。
    /// </para>
    /// </summary>
    /// <param name="services">宿主服务提供者</param>
    /// <returns>MAUI View 实例（以 object 形式返回）</returns>
    object CreateTabView(IServiceProvider services);
}
