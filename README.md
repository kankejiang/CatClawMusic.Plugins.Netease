# CatClawMusic.Plugins.Netease

猫爪音乐（CatClawMusic）的**网易云音乐音源插件**工程，独立于宿主应用编译与交付。

基于网易云官方接口（老 web API：匿名优先 + 可选用户 Cookie 增强），覆盖
搜索（歌曲/歌单/歌手）/ 歌单广场（分类+分页）/ 歌单内歌曲 / 排行榜 /
歌手与专辑 / 播放直链（音质三档+三级兜底+缓存）/ 歌词 /
私人漫游（无限电台）/ 每日推荐（歌曲+歌单）/ 我的歌单 / 红心。

## 形态

- 宿主（CatClawMusic.Maui）是"空壳"，不内置任何音源；
- 本工程编译产出独立 DLL，Release 构建后自动复制为
  `CatClawMusic.Plugins.Netease.ccp`，在宿主应用的
  **插件管理 → ＋ 添加 → 本地/网络安装**中导入后即可使用网易云音源。

## 功能

**浏览**

- **搜索**：歌曲 / 歌单 / 歌手三模式切换，歌曲搜索带分页（滑到底自动加载下一页）；
- **歌单广场**：官方分类接口动态拉取（华语/欧美/流行/ACG 等），分页加载，歌单内歌曲一次拉全；
- **排行榜**：官方全部榜单（飙升/新歌/热歌等），榜单可当歌单打开；
- **歌手页 / 专辑页**：歌手热门 50 首 + 全部专辑，专辑内歌曲可整播/单曲播放；
- **封面裁尺寸**：所有封面 URL 追加 `?param=NxN`，显著降低流量与内存。

**播放**

- **音质三档**：标准 128k / 高品 320k / 无损 FLAC（无损需登录，匿名自动降级），
  偏好持久化到 `netease_quality.txt`；
- **播放直链**：enhance 按音质取链 → 免登录外链（outer 302 → CDN）→ 降档重试 →
  公共 NeteaseCloudMusicApi 实例，三级兜底，统一 https，带 20 分钟直链缓存；
- **保留队列**：点击单曲以当前完整列表构造播放队列，支持上/下一首续播；
- **失败提示**：VIP/下架歌曲行内降透明度并标 VIP 角标，取链失败轻提示（3 秒自动消失）；
- **歌词**：LRC + 翻译。

**私人漫游 / 每日推荐**

- **私人漫游**：随机推荐无限电台，播完自动续播、边播边拉新歌（去重入队）；
  登录后可「红心」与「垃圾桶」（减少此类推荐）；
- **每日推荐**：歌曲匿名可用、登录后个性化；推荐歌单需登录；
- **听歌打卡**：播完自动上报 weblog，静默失败，长期提升推荐精度。

**登录增强**（浏览器登录：宿主 WebView 打开 music.163.com 登录页，提取 Cookie 回传，
持久化到 `netease_cookie.txt`，重启自动恢复，支持退出）

- **我的歌单**：创建与收藏的歌单（含「我喜欢的音乐」）；
- **红心**：普通歌曲写入「我喜欢的音乐」歌单，FM 歌曲走 radio/like，列表实时刷新 ❤ 状态。

## 插件界面

插件以纯 C# 代码构建 MAUI UI（不用 XAML，避免跨程序集编译问题），
通过 DynamicResource 复用宿主全局主题资源，提供两种接入形态：

- **整页入口**（`IViewContributorPlugin`）：宿主发现页显示「网易云音乐」入口，
  点击后 Push `NeteaseOnlineMusicPage` 整页；
- **发现页子 tab**（`IDiscoverTabPlugin`）：在宿主发现页「推荐」右侧内嵌
  `NeteaseDiscoverTabView`（TabOrder=1），与整页共享同一套 ViewModel。

歌手页（`NeteaseArtistPage`）与专辑页（`NeteaseAlbumPage`）由插件自行 Push 到导航栈，
共享 `NeteaseUiKit` 模板保证视觉一致。

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


## 协议

[MIT](LICENSE)
