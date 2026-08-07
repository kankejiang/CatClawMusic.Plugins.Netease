# CatClawMusic.Plugins.Netease

猫爪音乐（CatClawMusic）的**网易云音乐音源插件**工程，独立于宿主应用编译与交付。

基于网易云官方接口（老 web API：匿名优先 + 可选用户 Cookie 增强），覆盖
搜索 / 歌单广场 / 歌单内歌曲 / 排行榜 / 播放直链（三级兜底）/ 歌词 /
私人漫游（无限电台）/ 每日推荐。

## 形态

- 宿主（CatClawMusic.Maui）是"空壳"，不内置任何音源；
- 本工程编译产出独立 DLL，Release 构建后自动复制为
  `CatClawMusic.Plugins.Netease.ccp`，在宿主应用的
  **插件管理 → ＋ 添加 → 本地/网络安装**中导入后即可使用网易云音源。

## 功能

- **搜索**：歌曲搜索（cloudsearch），另支持歌单搜索与歌手热门歌曲；
- **歌单广场**：按分类浏览热门歌单（华语/欧美/流行/ACG 等），歌单内歌曲一次拉全；
- **排行榜**：官方全部榜单（飙升/新歌/热歌等），榜单可当歌单打开；
- **私人漫游**：随机推荐无限电台，播完自动续播、边播边拉新歌（去重入队）；
- **每日推荐**：匿名可用，登录后个性化；
- **播放直链**：免登录外链（outer 302 → CDN）→ enhance/player/url + 静态 Cookie →
  公共 NeteaseCloudMusicApi 实例，三级兜底，统一 https；
- **歌词**：LRC + 翻译；
- **浏览器登录**：宿主 WebView 打开 music.163.com 登录页，提取 Cookie 回传完成登录，
  Cookie 持久化到 `LocalApplicationData/CatClawMusic.Maui/netease_cookie.txt`，
  重启自动恢复；支持退出登录。

## 插件界面

插件以纯 C# 代码构建 MAUI UI（不用 XAML，避免跨程序集编译问题），
通过 DynamicResource 复用宿主全局主题资源，提供两种接入形态：

- **整页入口**（`IViewContributorPlugin`）：宿主发现页显示「网易云音乐」入口，
  点击后 Push `NeteaseOnlineMusicPage` 整页；
- **发现页子 tab**（`IDiscoverTabPlugin`）：在宿主发现页「推荐」右侧内嵌
  `NeteaseDiscoverTabView`（TabOrder=1），与整页共享同一套 ViewModel。

`IDiscoverTabPlugin` 接口定义在本插件程序集内（不改宿主 Core），宿主可通过
FullName 匹配或鸭子类型反射探测接入，详见接口注释。

## 构建

```bash
dotnet build -c Release
```

产物：`bin/Release/net10.0/CatClawMusic.Plugins.Netease.ccp`

> 依赖：需要宿主仓库 `CatClawMusic` 中的 `CatClawMusic.Core` 工程（接口与模型定义，
> 本工程以相对路径引用 `..\CatClawMusic\CatClawMusic.Core`）。
> 另引用 `Microsoft.Maui.Controls`（10.0.20，与宿主一致）与
> `CommunityToolkit.Mvvm`，均不随插件分发（`CopyLocalLockFileAssemblies=false`，由宿主提供）。

## 版本

当前插件版本 **2.0.0**（`NetEaseMusicPlugin.Version`）。

## 协议

[MIT](LICENSE)
