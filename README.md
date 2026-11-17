# MioReel

基于 Avalonia、Aviora 和 FrameFlux 的短剧桌面客户端。

## 功能

- 片库分类、搜索、滚动预加载和分页去重。
- 浏览历史、播放进度保存和断点续播。
- 上下集切换、下一集预加载、清晰度选择和播放倍速记忆。
- 小窗播放、工具栏自动隐藏，以及大小窗独立的位置记录。
- 深浅主题和最小 13 DIP 字号。
- 离线下载、暂停继续、失败重试、任务恢复和本地播放。

## 开发

需要 .NET 11 SDK 和项目依赖。`nuget.config` 中的 `AvioraLocal` 指向开发机本地包目录；在其他环境中请将它改为自己的 Aviora 包目录，或在包已发布时使用 nuget.org。

```powershell
dotnet restore MioReel.slnx
dotnet run --project src/MioReel.Desktop
```

在设置中填写兼容的视频源网关地址及访问令牌，也可通过 `MIOREEL_GATEWAY_URL`、`MIOREEL_GATEWAY_TOKEN` 环境变量配置。仓库不包含视频源服务或访问凭据。

## 离线下载

播放页点击“缓存”，选择下载前 20 集或全部剧集。通过“设置 → 存储与服务 → 离线下载目录 → 更改目录”选择文件夹，新目录仅影响新任务。

下载完成后，通过“离线下载 → 已完成 → 立即观看”播放本地 MP4。继续任务会复用完整分集，未完成的当前集重新下载；重启后未完成任务保持暂停。移除任务保留本地文件。

设置、历史和下载任务记录位于 `%LOCALAPPDATA%\MioReel`，视频保存在用户选择的下载目录。

## 验证

```powershell
dotnet build MioReel.slnx
dotnet run --project tests/MioReel.RegressionTests
```

更多检查说明见 [回归检查](tests/MioReel.RegressionTests/README.md)。
