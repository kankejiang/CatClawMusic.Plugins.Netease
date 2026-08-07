# 项目长期记忆：CatClawMusic.Plugins.Netease

## 工作流约定（用户明确要求）
- **改完插件直接发布 .ccp**：每次插件代码改动完成后，编译 Release 产出 `bin/Release/net10.0/CatClawMusic.Plugins.Netease.ccp`，提交源码并打 GitHub Release（标签递增），把 .ccp 作为 Release asset 上传，方便用户「插件管理 → ＋ 添加 → 网络安装」联网下载。
- **版本号规则（用户明确）**：默认只加补丁位 **+0.0.1**（如 v0.1.0 → v0.1.1 → v0.1.2）；**只有发布“完整版本”时才动 minor/major**。禁止无谓地跳 minor/major（例如不要把普通改动发成 v0.2.0）。
- .ccp 被 .gitignore 忽略（bin/），**不要**把 .ccp 提交进仓库，只作 Release 附件；源码改动正常提交推送。
- **不要**把 `.workbuddy/` 目录提交进仓库（项目数据，非源码）。
- 发布命令（已验证可用）：`dotnet build -c Release` → `git add <改动的源码>` → commit → `git push origin main` → `gh release create vX.Y.Z --generate-notes -R kankejiang/CatClawMusic.Plugins.Netease bin/Release/net10.0/CatClawMusic.Plugins.Netease.ccp`。

## 关键架构
- 插件独立 DLL（.ccp）交付，不携带依赖（Core/MAUI 由宿主提供）。编译引用同级 `..\CatClawMusic\CatClawMusic.Core`。
- 网易云插件 `NetEaseMusicPlugin` 实现：`IOnlineMusicPlugin`（搜索/歌单/播放/歌词/漫游/每日推荐/排行榜）、`IViewContributorPlugin`（整页入口）、`IDiscoverTabPlugin`（发现页「推荐」右侧内嵌子 tab，内联 View）。
- 客户端只认 .ccp；安装入口在宿主「插件管理 → ＋ 添加 → 本地/网络安装」。

## 宿主接入契约：IDiscoverTabPlugin（插件已就绪，宿主待接入）
- 插件已在 `Netease/IDiscoverTabPlugin.cs` 定义贡献接口，`NetEaseMusicPlugin` 实现（TabTitle="网易云"、TabIcon="🎵"、TabOrder=1、CreateTabView 返回 NeteaseDiscoverTabView 内联 View），**v0.1.1 已发布**。tab 不显示是宿主侧未渲染，不是插件 bug。
- 推荐接入方式（鸭子类型，零 Core 改动）：宿主在 PluginManager 对每个已启用插件实例用反射探测以下成员，存在即视为发现子 tab 贡献者：
  - `string TabTitle { get; }`
  - `string TabIcon { get; }`
  - `int TabOrder { get; }`
  - `object CreateTabView(IServiceProvider services)` （返回 Microsoft.Maui.Controls.View）
- 宿主渲染要点：DesktopDiscoverPage 的 CategoryTabBar 目前写死 5 列（推荐/排行/歌手/专辑/报告，CurrentCategory 0–4、PanelRecommend…PanelStats 固定绑定）。接入时需把插件 tab 动态插入「推荐」(0) 右侧，并重映射内置 tab 索引与面板绑定；选中插件 tab 时把 `CreateTabView(services)` 返回的 View 加入对应面板容器。
- 备选方式 A（强类型，改动更大不推荐）：宿主在 Core 复制全限定名 `CatClawMusic.Plugins.Netease.IDiscoverTabPlugin`（成员签名一致），在 PluginManager 反射适配体系按 FullName 匹配包装后可用 `GetEnabledPlugins<IDiscoverTabPlugin>()`。
