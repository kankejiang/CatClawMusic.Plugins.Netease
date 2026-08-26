# CatClawMusic 网易云插件 ← api-enhanced 移植/完善方案

> 目的：把 `api-enhanced`（Node 服务，439 个接口）的差异化能力移植进
> `CatClawMusic.Plugins.Netease`（纯 C# / MAUI 插件），补齐播放体验与推荐能力。
> 本文档先审阅、确认后再实施。

---

## 一、两方现状盘点

### 插件已覆盖（NetEaseOpenApiClient.cs / NeteaseEapi.cs）
搜索（歌/单/歌手）、歌单广场（分类+分页）、歌单详情、榜单、歌手热门/专辑、专辑歌曲、
播放直链（三档音质 + enhance→outer→公共API 三级兜底 + 20min 缓存）、歌词（eapi 兜底）、
私人漫游 + 36 场景、每日推荐（歌曲+歌单）、我的歌单、红心、FM 垃圾桶、听歌打卡、浏览器登录。

**已达成的加密能力**：老 web API（`music.163.com/api/*`，无加密）+ eapi（`NeteaseEapi.cs`，
AES-ECB + MD5 签名，`interface.music.163.com/eapi/*`）。

### api-enhanced 独有、插件缺失的资产
| 资产 | 说明 |
|---|---|
| **weapi 加密** | 双层 AES-128-CBC + 原始 RSA（`util/crypto.js:64`），可解锁大量 `*/*weapi*` 接口 |
| **xeapi + 匿名游客 token** | 匿名 `register_anonimous` 注册 `MUSIC_A`，Android 桌面伪装取链，风控更稳 |
| **通用解灰** | `matchID(songId, source)` 跨源匹配可播版本（QQ/酷狗/酷我/咪咕），VIP/下架也能播 |
| **相似歌曲** | `weapi /v1/discovery/simiSong` |
| **历史每日推荐** | `weapi /discovery/recommend/songs/history/recent` |
| **MV 播放** | `weapi /song/enhance/play/mv/url` |

---

## 二、移植目标与优先级

按「收益/成本」排序：

| # | 功能 | 收益 | 成本 | 改动面 | 风险 |
|---|---|---|---|---|---|
| **P1** | weapi 加密层（基础设施） | 高——解锁上面三个接口 + 未来扩展 | 中 | 新增 `NeteaseWeapi.cs` | 低 |
| **P2** | 相似歌曲 / 历史推荐（UI+接口） | 中高——感知强 | 低（依赖 P1） | Client + VM + 页面 | 低 |
| **P3** | MV 播放 | 中 | 中（需新增 UI 与播放视频链路） | Client + VM + 页面 | 中 |
| **P4** | 解灰（跨源匹配） | 高 | **高**——需新增 2~4 个外部音源适配器 | 新增 `Unblock` 模块 | 高（合规/接口易变） |
| **P5** | xeapi + 匿名 token | 中（稳定性锦上添花） | **高**——X25519+AES-GCM 会话协商 + 公钥初始化 | 新增 `NeteaseXeapi.cs` | 中高（平台算法可用性） |

> 建议按 P1→P5 顺序推进，P4/P5 作为可选阶段（存量取链兜底已够用）。

---

## 三、P1 weapi 加密层（新增 `NeteaseWeapi.cs`）

### 算法（对齐 `util/crypto.js:64-79`，全部 .NET 内置实现）
```
secretKey = 16 个 base62 随机字符
params    = AES-128-CBC( AES-128-CBC( json, presetKey, iv ), secretKey, iv )   // base64
encSecKey = RSA_NoPadding( reverse(secretKey) )                                  // hex
```
- `presetKey = "0CoJUm6Qyw8W8jud"`，`iv = "0102030405060708"`（固定）。
- 请求：`POST {domain}/weapi/{path}`，`Content-Type` form-urlencoded，携带 `csrf_token`（= `__csrf`）。
- **原始 RSA（NoPadding）**：`.NET` 的 `RSA` 不支持无填充。用 `BigInteger.ModPow` 实现
  `c = m^e mod n`（从 PEM 提取 1024 位模数 `n` 与公开指数 `e=65537`，结果大端补满 128 字节）。
- 响应：普通 JSON（weapi `e_r` 兜底时也仅极少数接口压缩，P1 目标接口均为明文 JSON）。

### 对外接口（先支持 3 个，验证链路后留作通用扩展）
静态类，签名如 `Task<string?> WeapiRequestAsync(HttpClient, string path, IReadOnlyDictionary<string,object> body, string? userCookie)`，
返回解析后的 JSON 文本；复用客户端现有 UA/Referer 头。

### 改动文件
- 新增 `Netease\NeteaseWeapi.cs`
- `NetEaseOpenApiClient.cs`：新增三个调用方法（见 P2/P3）

### 验证
- 单测/手工：对未登录+登录态分别调用 `simiSong`，返回 `200/code` 与歌曲数组即可。

---

## 四、P2 相似歌曲 + 历史推荐

### 相似歌曲（`/weapi/v1/discovery/simiSong`）
- body：`{ songid, limit, offset }`。
- 落点：**播放页/歌曲详情**新增「相似歌曲」横向区，点击即从当前播放构造队列 / 直接播放。
- 返回字段复用现有 `ParseSong`（`simiSongs` 数组）。

### 历史每日推荐（`/weapi/discovery/recommend/songs/history/recent`）
- body：`{}`。
- 落点：**每日推荐 Tab** 增加「历史推荐」入口/折叠区，可回味历史日推。
- 返回：`data` 内 `dailySongs` 数组（同 `GetDailyRecommendAsync` 解析）。

### 改动文件
- `NetEaseOpenApiClient.cs`：`GetSimilarSongsAsync(string songId)`、`GetHistoryRecommendSongsAsync()`
- `NeteaseOnlineMusicViewModel.cs` / `NeteaseOnlineMusicPage.cs` / `NeteaseUiKit.cs`：列表入口与点击播放
- `NetEaseMusicPlugin.cs`：如需暴露给宿主再补 `Capabilities`/接口

### 验证
- 手动打开对应页面，确认列表渲染、点击播放队列正确。

---

## 五、P3 MV 播放

### 接口（`/weapi/song/enhance/play/mv/url`）
- body：`{ id, r }`（r=清晰度 1080 默认）。`id` 是 **mvId**（歌单里每个 track 的 `mvid` 或 `mv.id`）。
- 歌曲详情里取 `mvid`（`ParseSong` 暂未解析该字段，需补充）。

### UI 落点
- 播放页右上角加 **MV 入口**（有 `mvid>0` 才显示）→ Push 播放页或弹出视频层。
- 视频播放走宿主 `IAudioPlayerService` 之外的能力；**需确认宿主是否已有视频播放控件**，
  若无则先做「MV 直链获取 + 展示 MV 详情封面/跳转」，视频播放后续再排。

### 改动文件
- `NetEaseOpenApiClient.cs`：`ParseSong` 补 `mvid`；`GetMvUrlAsync(mvId)`
- 播放页 VM/Page：MV 按钮与详情
- 需与宿主协同一处：MV 实际播放

### 验证
- 取到直链并在浏览器/宿主播放；无 `mvid` 的歌不显示入口。

---

## 六、P4 解灰（跨源匹配，可选 · 成本最高）

### 原理（对齐 `util/crypto` 相关 + `unblockmusic-utils.matchID` / `/song/url/match`）
1. 用网易云 `song/detail` 拿到 `歌名 + 歌手 + 时长`；
2. 去外部音源（首选 **酷狗 kuwo**）搜索同名歌曲；
3. **启发式匹配**（歌名归一化相似度 + 歌手命中 + 时长误差阈值）挑最优；
4. 调用该源播放直链接口返回可播 URL（酷狗直链对 UA 敏感，通常需携带平台请求 UA/哈希签名）。

### 触发策略（避免无谓跨源）
仅在**当前直链全部失败**（enhance / outer / 公共API 均空）或 `fee` 受限不可播时才进入解灰，
成功后同样进 20min 直链缓存（`_urlCache`）。

### 关键决策点（实施前需确认）
- **先做单源 POC（酷狗）** 还是直接多源？——建议单源验证匹配率后再扩。
- 跨源产物与合规：仅用于个人已购歌单的补播，避免明显侵权场景；若顾虑，可只做「网易云内部
  × 相似歌曲直链」的更轻量回退。

### 改动文件
- 新增 `Netease\Unblock\KuwoClient.cs`（搜索+取链+签名）、`Netease\Unblock\SongMatcher.cs`
- `NetEaseOpenApiClient.cs` 的 `ResolvePlayUrlAsync` 增加一级兜底

### 验证
- 对若干 VIP/下架歌确认可播且音频文件能正常播放、时长吻合，返回码与 UA 等。

---

## 七、P5 xeapi + 匿名游客 token（可选 · 成本最高）

### xeapi 加密链路（对齐 `util/crypto.js:167-297`，会话式）
1. **初始化**：调用 `register_xeapikey` 获取 `xeapi_public_key`（`publicKey` + `sk` + `version`），服务端生成；首次需 / 非首次重刷；
2. **每请求①**：AES-ECB(staticKey=`ab1d...1b84`) 加密明文（含 body base64 + 查询串），再过 `xeapiMidTransform`（随机 16B XOR + base64 轮转）→ 外层 AES-ECB(dynamicKey)；
3. **每请求②**：`xeapiEncryptS` —— X25519 临时密钥对 + ECDH 共享密钥 → HKDF-SHA256(ephemeral+`1`) 派生 16B → **AES-128-GCM** 加密 `(base64(dynamicKey)|os|sk)` → 拼 `ephemeral||iv||cipher||tag`；
4. **每请求③**：AES-ECB(staticKey) 加密 `version|sessionId` → `R`；
5. 发送 `B/S/R` 三字段到 `interface.music.163.com/xeapi/{path}`，头带 `x-aeapi`、`x-os/appver/osver/deviceid/sdeviceid`、UA（Android `NeteaseMusic/9.5.61`）；
6. **响应**：AES-ECB(eapiKey) 解密 → 可选 gzip → JSON；响应头带 `x-encr-ssid/x-encr-sskey` 缓存为会话密钥。

### C# 技术点与风险
- **X25519 ECDH**：`.NET` 跨平台支持不稳（Windows/Android + Mono 上的可用性需实测），是最大不确定点；
  ≥\.NET 11 / .NET MAUI 环境需验证 `ECDiffieHellman` X25519 曲线可用性。
- AES-128-GCM：`System.Security.Cryptography.AesGcm`（.NET 8+）可直接用。
- HKDF：`HKDF.DeriveKey(SHA256)`（.NET 5+ 内置）。
- 匿名 token：`register_anonimous` 走 xeapi，拿到 `MUSIC_A` 后存盘，供后续取链伪装匿名游客。

### 收益再评估
插件已有 enhance(静态 cookie) → outer → 公共 API 三级取链兜底，匿名场景稳定性已可接受。
**建议 P5 仅作为「播放取链不稳/风控加剧」时的后续储备**，不与 P1–P3 同批实施。

### 改动文件
- 新增 `Netease\NeteaseXeapi.cs`
- 初始化流程：插件 `InitializeAsync` 或首次需要时注册公钥 + 匿名 token

---

## 八、建议落地顺序与产出

| 阶段 | 内容 | 依赖 | 构建验证 |
|---|---|---|---|
| **阶段①** | P1 weapi 层 + 单测链路 | 无 | `dotnet build -c Release` 通过，ccp 出包 |
| **阶段②** | P2 相似歌曲 + 历史推荐（UI+交互） | 阶段① | 页面可用、点击播放正常 |
| **阶段③** | P3 MV（先直链+入口，播放能力视宿主） | 阶段① | MV 直链可播 / 无 mvid 不显示 |
| **阶段④** | P4 解灰（单源 POC 起） | —— | VIP/下架歌成功补播 |
| **阶段⑤** | P5 xeapi 匿名（可选，视平台算法验证结果） | —— | 匿名取链稳定性提升 |

> 每次阶段完成即按你的习惯 `git commit`（本地，不 push）。

---

## 九、待你确认的决策点
1. **范围**：①–③ 全做？④ / ⑤ 是否纳入本期？
2. **解灰**：若做，先单源（酷狗）POC？是否介意跨源合规边界？
3. **MV**：宿主当前是否已有可用的视频播放控件？（决定 MV 是做到"能播放"还是"仅直链+入口"）
4. **UI**：相似歌曲/历史推荐/MV 入口的具体位置（播放页、每日推荐 Tab 等）是否有偏好？