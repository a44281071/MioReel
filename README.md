<div align="center">
  <img src="src/MioReel.Desktop/Assets/MioReel.png" alt="妙妙短剧" width="96" height="96" />
  <h1>妙妙短剧（MioReel）</h1>
  <p><strong>🎬 桌面端极速追剧神器 · 真正意义上的遥遥领先！</strong></p>

  <blockquote>
    <p>🔥 <strong>「卧槽，有这么好的短剧 App 你咋不早点告诉我？！」</strong></p>
    <p>🏆 <strong>「0 Star 以内最好的短剧 App，没有之一！」</strong></p>
    <p>🚀 <strong>「妙妙汽车还在车机里转圈，妙妙短剧已经丝滑连播下一集了——这就是真正的遥遥领先！」</strong></p>
    <p>💎 <strong>「一集接着一集秒开不卡顿，打工人摸鱼生产力直接拉满，买妙妙汽车不如先装妙妙短剧！」</strong></p>
  </blockquote>
</div>

---

## 为什么叫「妙妙短剧」？

某妙妙汽车整天吹得天花乱坠，车机卡成 PPT 还要卖你几十万；  
**妙妙短剧** 不吹牛、不画饼、不收你一分钱——**0 Star 以内断档第一**，打开就能看，摸鱼比开车稳多了，主打一个 **「真·遥遥领先」**。

---

## 核心功能（每一项都遥遥领先）

### 🎬 沉浸追剧，功能栏懂事隐身
- **智能控制栏显隐**：正常窗口看剧时，鼠标移出视频画面或静置 3 秒无移动自动隐藏底部控制栏，绝不遮挡字幕；鼠标回到画面立刻呼出。
- **丝滑换集预加载**：自动提前缓冲下一集，支持单集播完自动连播、断点续播与观影历史同步。
- **画质倍速随心调**：支持 1080P / 720P / 480P 清晰度切换与 0.75x ～ 3.0x 记忆倍速，切集、重开依然保留你的习惯。

### 🪟 摸鱼专精：自适应比例悬浮小窗
- **真·按画面比例缩放**：横屏剧、竖屏剧自动匹配原始画幅比例，拖动小窗任意边角或滚动鼠标滚轮，窗口严格按视频比例等比放大缩小，绝不留丑陋大黑边。
- **无感防打扰**：切到小窗时若鼠标不在小窗内，功能栏默认隐身只留纯粹画面；支持窗口置顶、右键/双击显隐控制栏，以及一键还原大窗。

### 🕶️ 老板键 & 全局热键（打工人刚需）
- **一键隐身**：支持系统级**全局老板键**，哪怕当前焦点不在播放器，按下快捷键瞬间暂停播放并隐藏窗口，再按秒速恢复追剧。
- **全键盘操控**：支持自定义播放/暂停、快进快退、倍速调节、上下集、切换小窗、还原窗体等快捷键，自动检测冲突。

### 📥 离线批量缓存
- 支持剧集列表前 20 集或全集一键离线缓存，支持任务暂停、继续、失败重试与断点续下，断网也能流畅本地追剧。

### 🎨 双主题影院观感
- 内置**深色影院**与**浅色日间**双主题，支持片库分类浏览（真人剧 / 漫剧 / AI 剧）、全库检索与搜索记录。

---

## 快速上手

1. 启动客户端后，进入 **设置 → 存储与服务** 填写视频源服务地址与访问令牌（如有），点击 **连接并同步** 即可开看（也支持通过环境变量 `MIOREEL_GATEWAY_URL`、`MIOREEL_GATEWAY_TOKEN` 预设）。
2. 在播放页点击 **缓存** 可将剧集下载到本地目录；离线剧集可在 **离线下载 → 已完成** 中直接播放。
3. 在 **设置 → 快捷键与全局热键** 中可自定义老板键与各类播放快捷键。

---

## 📺 Android TV 版（armeabi-v7a）

电视端复用桌面端的片库 / 历史 / 下载 / 设置逻辑，只替换窗口层与播放器：

- **播放器**：AndroidX Media3 `ExoPlayer` + `PlayerView`，由系统 `MediaCodec` 硬解，APK 内**不包含 FFmpeg / FrameFlux**。
- **界面**：Avalonia 单页 TV 布局，方向键移动焦点，确定键执行，返回键逐级返回/退出播放。
- **字体/图标**：TV 界面使用 Android 系统字体（`$Default`），避免 Inter 缺少中文字形时显示成横条；播放控制使用矢量 `PathIcon`，不依赖 emoji 字体。
- **移除的桌面能力**：标题栏、迷你窗/窗口置顶、Win32 窗口切换、系统全局热键。
- **输出**：仅 `armeabi-v7a`（32 位 ARM）APK，包名 `com.mioreel.tv`，minSdk 24、targetSdk 36，支持 Leanback 启动器与 TV banner。

### 构建

需要 .NET SDK `10.0.403`（`global.json` 已固定）以及 Android SDK 36。

```powershell
./scripts/publish-android-tv.ps1
# 或手动执行
dotnet publish src/MioReel.Desktop/MioReel.Desktop.csproj -c Release -f net10.0-android36.0 -p:AndroidPackageFormat=apk
```

产物：`src/MioReel.Desktop/bin/Release/net10.0-android36.0/publish/com.mioreel.tv-Signed.apk`

> 不要给 `dotnet publish` 追加 `-r android-arm`。多目标还原会把它套用到桌面 TFM，进而要求并不存在的 `Microsoft.NETCore.App.Runtime.linux-bionic-arm`。ABI 由 Android TFM 的 `RuntimeIdentifiers` 控制。

### 签名

不传签名参数时使用本机 debug key 签名，可直接 `adb install` 侧载测试。正式发布时：

```powershell
./scripts/publish-android-tv.ps1 `
  -KeystorePath .\release.keystore `
  -KeystorePassword '***' `
  -KeyAlias mioreel `
  -KeyPassword '***'
```

GitHub Actions 的 Release 工作流会自动构建 TV APK。若仓库配置了
`ANDROID_KEYSTORE_BASE64`、`ANDROID_KEYSTORE_PASSWORD`、`ANDROID_KEY_ALIAS`、
`ANDROID_KEY_PASSWORD` 四个 secrets，则使用正式签名；否则回退为 debug 签名。

### 遥控器操作

- 方向键移动焦点；确定键执行当前按钮（播放页的确定键即播放/暂停）。
- 播放页：返回键退出播放；左右方向键快退/快进 10 秒；媒体键支持播放/暂停、上一集/下一集、快退/快进。
- 搜索页会调起 Android 输入法，建议使用带键盘或语音输入的遥控器。

### 首次使用

与桌面端一致：进入 **设置** 填写视频源地址与访问令牌（也可通过 `MIOREEL_GATEWAY_URL`、`MIOREEL_GATEWAY_TOKEN` 环境变量预设），随后刷新片库即可。
