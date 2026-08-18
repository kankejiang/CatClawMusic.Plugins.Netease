# 项目长期记忆：CatClawMusic.Plugins.Netease

## 工作流约定（用户明确要求，务必遵守）
- **改完插件直接发布上传 .ccp，无需再询问用户**（2026-08-15 用户明确要求："下次支持发布上传.ccp"）：编译 Release 产出 `bin/Release/net10.0/CatClawMusic.Plugins.Netease.ccp` → 提交源码 → push → 更新 GitHub Release（.ccp 作 asset）。补丁级小改动不新建 Release，直接覆盖现有 Release 的 .ccp 附件（`gh release upload <tag> ...ccp --clobber`），Version 属性不变。禁止无谓跳 minor/major。
- 版本号规则（2026-08-08）：大版本（minor 跳变）才新建 Release（新 tag）；补丁级小改动不新建 Release，直接覆盖现有 Release 的 .ccp 附件（`gh release upload <tag> ...ccp --clobber`），Version 属性不变。禁止无谓跳 minor/major。
- Version 必须与 Release tag 一致：`NetEaseMusicPlugin.Version` 随每次发布同步（当前 `"0.1.20"`）。每次发布前检查并同步。
- 发行版清理（v0.1.20 起）：只留最新一个 Release，删除其余（连 tag）。
- 不提交 .ccp（bin/ 被 gitignore，仅作 Release 附件）；不提交 `.workbuddy//`。
- 发布命令：`dotnet build -c Release` → `git add 源码` → commit → `git push origin main` → `gh release upload vX.Y.Z bin/Release/net10.0/CatClawMusic.Plugins.Netease.ccp --clobber -R kankejiang/CatClawMusic.Plugins.Netease`
- 发布前验证 .ccp 真实性（python）：类型/方法名在 #Strings heap 是 UTF-8；字符串字面量在 #US heap 是 UTF-16LE；`Debug.WriteLine` 参数 Release 下被 `[Conditional]` 剔除，勿用于验证。

## 关键架构
- 插件独立 DLL（.ccp）交付，不携带依赖，编译引用同级 `..\CatClawMusic\CatClawMusic.Core`。
- `NetEaseMusicPlugin` 实现 `IOnlineMusicPlugin` + `IViewContributorPlugin` + `ILyricsProviderPlugin`（整页入口；`IDiscoverTabPlugin` 已移除）。
- FM 电台架构（防回归）：`NeteaseOnlineMusicViewModel` 是插件级单例（`_sharedVm`），`Detach()` 不退出 FM/不停 Timer；电台是全局播放行为，仅用户明确操作才 LeaveFmMode。改成每次 new VM 会杀死正在播放的电台。
- 播放队列构造：`NeteasePlaybackHelper.ToQueueSong` 把 OnlineSong 转宿主 Song，`RemoteId="{Platform}:{Id}"`、`CoverArtPath=os.CoverUrl`、`Source=SongSource.Local`。

## 已实施修复：FM 错封面 + 部分歌词缺失
- **根因（实测确认）**：私人FM 接口 `/api/v1/radio/get` 返回的 `album` 是推荐引擎的"关联专辑"，其 `picUrl` 与歌曲真实发行专辑不符 → 错封面（已用 curl 验证 FM 返回 album 非歌曲真实专辑）。歌词本身按 song.id 拉取正确，但官方 `/api/song/lyric` 对部分歌（VIP/风控/新歌）返回空 `lrc` 且无公共API兜底 → 部分歌词缺失。
- **修复（NeteaseOpenApiClient.cs，commits 649d9ce / cf464a9 / a0a6d74）**：
  - 新增 `CorrectFmMetadataAsync`（三级兜底）：① 官方 `music.163.com/api/song/detail`（依赖用户 Cookie，可能限流空响应）→ ② `PublicApiBases[0]=zm.wwoyun.cn/song/detail` → ③ `PublicApiBases[1]=iwenwiki.com:3000/song/detail`。每级失败返回 false 继续下一级。
  - 抽出 `TryCorrectSongCoversAsync(url, expectArrayKey)` + `ApplySongCoverCorrection(songs, list)`：按 id 匹配，覆盖 `CoverUrl`（`CoverWithSize(ToHttps(pic),1000)`）与 `Album` 为标准 `al.picUrl`/`al.name`。全部失败静默保留原 FM 封面。
  - `GetLyricsAsync` 拆出 `FetchLyricFromOfficialAsync`（参数 `tv=-1`→`tv=0`），并加 NeteaseCloudMusicApi 公共兜底循环（`/lyric?id=`）。
  - `CoverWithSize` 修双 `?` 隐患：URL 已含 query 时改 `&param=` 追加。
- **为什么三级兜底必要（教训 a0a6d74）**：官方 `/api/song/detail` 在外网 curl 测下来 Content-Length: 0，即使用户有黑胶 cookie，风控/限流时同样空响应。原 fix 静默 catch → 错封面仍存在。公共 NeteaseCloudMusicApi 不需 cookie，可作硬兜底。
- **修复（NetEaseMusicPlugin.cs）**：实现 `ILyricsProviderPlugin.GetLyricsAsync(Song)`（按 `RemoteId` 取 netease id → `GetLyricsAsync` → `ParseNeteaseLyrics`），host 歌词兜底链已能在不依赖未提交的 host routing 改动下显示在线歌词。
- **歌词/封面兜底链（当前 v0.2.0，eapi 第一优先级）**：
  - 封面校正（CorrectFmMetadataAsync，FM 专用）：① **eapi `/eapi/v3/song/detail`**（NeteaseEapi.cs，interface.music.163.com 桌面客户端伪装，最稳）→ ② 官方 `music.163.com/api/song/detail`（易限流空 body）→ ③ 公共 NeteaseCloudMusicApi（zm.wwoyun.cn / iwenwiki.com:3000）。
  - 歌词（GetLyricsAsync）：① **eapi `/eapi/song/lyric/v1`** → ② 官方 `/api/song/lyric`（tv=0）→ ③ 公共镜像 `/lyric?id=`。
- **eapi 要点（NeteaseEapi.cs）**：header{clientSign/osver/deviceId/os/appver/requestId}+e_r=true；digest=md5("nobody"+encryptPath+"use"+paramsText+"md5forencrypt")；data=encryptPath+"-36cd479b6b5-"+paramsText+"-36cd479b6b5-"+digest；AES-128-ECB PKCS7 大写 hex；响应体是**裸 AES 密文**（非 base64，Lyrico 的 bodyBase64 是宿主封装）；参数序列化用 JsonObject+UnsafeRelaxedJsonEscaping 匹配 JS JSON.stringify（顺序+转义逐字节一致，已对拍验证）。常量：EapiKey="e82ckenh8dichen8"、EncryptSalt="-36cd479b6b5-"、AppVer="3.1.3.203419"。
- **宿主封面缓存坑（2026-08-09 已修，commit 7afb952 宿主 master）**：宿主 `CatClawMusic.Maui/ViewModels/AppViewModels.cs` `LoadCoverAsync` 1b 步把 http(s) 封面下载缓存到 `cover_{song.Id}.jpg`，**缓存存在即用、不校验 URL 一致性** → 插件若给过错的 CoverArtPath（旧版无校正的 FM 脏 URL），会固化成缓存，后续同 Id 命中旧图。修复：`CoverHelper.GetHttpCoverCachePath(songId, url)` 返回 `cover_{Id}_{sha256(url)前8}.jpg`，URL 一变 key 即变；非 URL 源回退 `cover_{Id}.jpg`。**验证封面问题时先排除宿主缓存（用户清缓存即恢复）或确认宿主已含 7afb952。**
- 宿主侧（其他项目 CatClawMusic.Core）的 Phase2 歌词路由设想本插件 `ILyricsProviderPlugin` 已兜底，无需等宿主改动即可生效。

## 发布版本
- 最新 Release：v0.2.0（Version 字段同步 `"0.2.0"`）。旧 v0.1.20 已删除（连 tag），列表只留最新一条。
