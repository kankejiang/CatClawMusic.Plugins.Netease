# CatClawMusic.Plugins

猫爪音乐（CatClawMusic）的**在线音乐音源插件**工程，独立于宿主应用编译与交付。

包含 5 个音源插件：**Apple Music / 酷狗 / 网易云 / QQ 音乐 / Soda**，由共享基类
`OnlineMusicPluginBase` 统一实现元数据与 HTTP 访问，各插件只需实现搜索、播放地址与歌词。

## 形态

- 宿主（CatClawMusic.Maui）是"空壳"，不内置任何音源；
- 本工程编译产出独立 DLL（`CatClawMusic.Plugins.OnlineMusic.dll`），在宿主应用的
  **插件管理 → ＋ 添加 → 本地/网络安装**中导入后即可使用在线音源。

## 构建

```bash
dotnet build -c Release
```

产物：`bin/Release/net10.0/CatClawMusic.Plugins.OnlineMusic.dll`

> 依赖：需要宿主仓库 `CatClawMusic` 中的 `CatClawMusic.Core` 工程（接口与模型定义，
> `CatClawMusic.Plugins.csproj` 以相对路径引用 `..\CatClawMusic\CatClawMusic.Core`）。

## 协议

[MIT](LICENSE)
