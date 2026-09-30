# AGENTS.md — ClassIslandInjector 开发指南

本文件为 AI 编程助手（Copilot / Claude Code 等）提供在本仓库内工作所需的关键约束、架构说明与常用命令。开始改动前请先阅读。

## 项目概览

`ClassIslandInjector` 是 [ClassIsland](https://github.com/ClassIsland/ClassIsland) 的一个插件（Cipx），通过运行时注入 + 可热重载 Avalonia 样式表，深度重塑 ClassIsland 主界面的外观：基础变形（不透明度/缩放/位置/旋转/圆角）、固定尺寸、自定义背景/渐变、阴影、边框、动画与提醒效果、倒计时箭头、SMTC（Windows 媒体会话）动态取色、主界面底图（本地图片/文件夹幻灯片/SMTC 专辑封面），以及一个可视化编辑器。

- 目标框架：`net8.0-windows10.0.19041.0`（**必须**与宿主对齐，见下文「WinRT」）。
- 宿主运行环境：Windows 上的 ClassIsland 桌面应用。
- 请自行联网搜索 ClassIsland 插件编写规范。

## 目录结构

| 文件                                                                                              | 职责                                                                                      |
| ------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `Plugin.cs`                                                                                     | 插件入口：初始化运行时、注册设置页、AppStarted 时 Attach                                  |
| `InjectorRuntime.cs`                                                                            | 静态运行时门面：设置加载/保存、注入器生命周期、SMTC watcher 生命周期、`DeleteAllData()` |
| `InjectorSettings.cs`                                                                           | 设置模型（含`InjectorSettingsStore` JSON 持久化）、预设、`Spin` 无关                  |
| `MainWindowStyleInjector.cs`                                                                    | 核心注入器：注入/恢复主界面视觉效果、动态取色过渡、底图、Ripple、倒计时箭头等             |
| `SmtcWatcher.cs`                                                                                | 事件驱动的 SMTC 会话监听器（WinRT），推送取色结果/缩略图/播放状态                         |
| `SmtcAlbumColorPicker.cs`                                                                       | 纯取色工具（MaterialColorUtilities），**不含 WinRT**；含诊断日志                    |
| `VideoFrameSource.cs` / `FFmpegVideoDecoder.cs` / `FFmpegRuntime.cs`                         | 视频背景解码（**纯 FFmpeg，无 WMF**）：后台解码线程、FFmpeg 解码器、库检测 + 联机下载 |
| `FFmpegAudioDecoder.cs` / `VideoAudioPlayer.cs`                                             | 单文件视频背景的**音频输出**（「播放声音」开关，默认关闭）：FFmpeg 解音频轨 + swr 统一重采样为 48kHz/立体声/s16 → NAudio `WasapiOut`（共享模式）。无音频轨 / 设备不可用一律静默降级为无声。**循环同步**：`VideoFrameSource.OnLoopRestart` 在每次画面循环复位时把音频 `Seek(0)`（音频与视频各自独立循环，周期一旦不一致就会越滚越错位）；「播放声音」开启时底图按**源帧率**播放（否则视频循环周期 ≠ 音频时长，一个画面循环里音频会重复若干遍） |
| `ProjectAudioMixer.cs` / `AudioWaveform.cs`                                                 | 工程（多片段）音频：按工程时间轴混音「音频轨片段 + 视频片段自带原声」，逐片段应用音量与淡入/淡出包络、int 累加后钳制防削波；波形峰值包络提取（20ms 细粒度 + 按文件缓存，供时间轴绘制）。**离线渲染走同一个 `MixChunk`**：`StartOffline(project)` + `ReadForRender(...)` 不建立音频输出、直接按样本号产出 PCM，所以「渲染出来的声音」与「预览听到的」口径完全一致（含变速/保持音调/轨静音） |
| `VideoTranscoder.cs`                                                                                | 素材导入时的转码压缩（720p/CRF27 → 配置目录 `video-cache/`）。**输出帧率上限 30fps**（`ProxyMaxFps`，按目标时间抽帧）：源帧率直接决定预览/底图的解码成本，而展示只要 24fps，照抄 54~60fps 的源帧率等于每次播放白解近两倍的帧；30fps 同时保证产物是 CFR |
| `VideoProject.cs` / `VideoProjectPlayer.cs` / `Views/VideoEditorWindow.cs`               | 视频工程（多片段拼接/变换）与 PR 风格视频编辑器（素材库/舞台/属性/时间轴）            || `PresetExchange.cs`                                                                              | 预设交换：把用户预设（含静态资源）导出为 .cizip / 从 .cizip 导入；包内 metadata.json / preview.png 商店展示字段 |
| `PresetStoreService.cs`                                                                          | 预设商店联机服务：索引抓取（15min 磁盘缓存 + 离线回退）、预览图缓存、.cizip 下载（进度）、已安装记录（installed.json）、版本兼容检查 |
| `Views/PresetStoreWindow.cs` + `Views/PresetStoreCard.cs`                                        | 预设商店窗口（1:1 仿新版微软商店）：自定义标题栏 + 左窄导航（首页/全部/热门/我的）+ Banner 轮播 + 横向卡行 + 网格浏览 + 详情页 || `Views/InjectorSettingsPage.cs`                                                                 | 设置页 UI（FluentAvalonia`SettingsExpander`/`InfoBar`/`ContentDialog`）             |
| `Views/InjectorSettingsPage.cs`                                                                 | 设置页 UI（FluentAvalonia`SettingsExpander`/`InfoBar`/`ContentDialog`）             |
| `CountdownArrowOverlay.cs` / `IslandRippleOverlay.cs` / `SuppressingTopmostEffectPlayer.cs` | 覆盖层效果组件                                                                            |
| `Defaults/Overrides.axaml`                                                                      | 默认覆盖样式表（首次运行复制到配置目录，用户可热重载编辑）                                |
| `manifest.yml`                                                                                  | 插件清单                                                                                  |

## 工程文件格式（v2，PR 风格分层）

`video-project.ciproj`（JSON）：`Version` / `Name` / `Sequence{ OutputWidth, OutputHeight, VideoTracks[], AudioTracks[] }` / `Clips[]` / `Assets[]`。

- v1 旧格式（`video-project.json`，顶层 `OutputWidth` + `Clips`）读取时自动迁移；**迁移过来的视频片段显式 `Muted=true`** —— 它们在支持音频之前本来就不出声，静音可避免升级后突然发声。
- **音频片段约定**：`Kind="Audio"`、`Track=-1`（不进视频轨，让所有按 `Track` 筛选的画面逻辑天然忽略它）、`AudioTrack` 独立编号（A1 从 0 起）。视频片段的自带原声由同一个 `VideoClip` 的 `Volume / AudioFadeIn / AudioFadeOut / Muted` 控制。
- 时间轴泳道映射：`VideoLaneCount = max(1, TrackCount)`，音频泳道紧随其后（`IsAudioLane(lane)` 判定），视频在上、音频在下；`_selectedTrack` / `_headerByTrack` 用的都是泳道号。
- **行号/顶部坐标的唯一权威是 `VideoEditorWindow.LaneOrder()` / `LaneAtRow(row)` / `RowOfLane(lane)` / `LaneVisualTop(lane)`**（视频轨在上 = 轨号大的在上，音频轨在下 = A1 最底）。凡按 Y 判定（拖拽落点、命中测试、框选、落点标签、自动滚动）都必须经这四个方法，**不要**再用 `TrackCount/TotalLaneCount` 反推行号——曾因两处各算一套，音频泳道被排到视频泳道上方，导致拖拽抓取偏移差一个泳道高（片段与指针差一截）+ 落点错一轨。
- 音频片段的 `Track` 恒为 -1（历史数据里也可能残留被拖拽写成视频轨号的脏值），取所在泳道一律用 `LaneOfClip(clip)`。
- 拖拽跟手的三条铁律：①抓取偏移用 `TranslatePoint` 量**块的真实位置**，不用模型推算；②`SnapTime` 必须排除被拖片段自身（否则吸回自己、永远落后 8px）；③边缘自动滚动的跟随回调 `_dragScrollFollow` 里要用**实际**滚动量重算内容坐标（指针在边缘不动时内容仍在滚）。
- 工程背景生效路径：`MainWindowStyleInjector.ResolveVideoProjectPath()` —— 设置项 `VideoProjectPath` 从无写入点，「使用编辑工程」开关只切 `VideoProjectEnabled`，因此**必须回退到默认工程路径**，否则工程背景永不生效。

## 文件路径
ClassIsland源代码：`D:\Dev\ClassIsland-Code`

## 常用命令（PowerShell）

```powershell
# 构建（不生成 cipx）
dotnet build ClassIslandInjector.csproj -c Release -p:CreateCipx=false

# 部署到宿主（先关闭 ClassIsland，否则 DLL 被占用）
Stop-Process -Name "ClassIsland*" -Force
Copy-Item "bin\Release\net8.0-windows10.0.19041.0\*" "D:\Dev\ClassIsland\data\Plugins\classisland.injector" -Recurse -Force
```

路径速查：

- 插件目录：`D:\Dev\ClassIsland\data\Plugins\classisland.injector`
- 插件配置目录：`D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector`（`settings.json`、`Overrides.axaml`、诊断日志 `album-color.log`）

## 关键约束（务必遵守）

### 1. WinRT / SDK 版本对齐（最容易踩坑）

- 插件使用 WinRT API（`Windows.Media.Control` 的 SMTC）。宿主 ClassIsland 自带 `Microsoft.Windows.SDK.NET.dll` **10.0.19041.38** 与 `WinRT.Runtime.dll` 2.2.0.0，且 `PluginLoadContext` 强制使用宿主版本（拒绝从插件目录加载）。
- 因此插件 TFM 必须为 `net8.0-windows10.0.19041.0`。若用更高 SDK（如 26100）编译，运行时会抛 `FileNotFoundException`（找不到 10.0.26100.38），且发生在 try/catch 之前导致崩溃。
- 防御技巧：把 WinRT 调用隔离到带 `[MethodImpl(MethodImplOptions.NoInlining)]` 的独立方法，由调用方 try/catch 包裹。
- 忽略的异常 HResult：`0x800706BA`（RPC 不可用）、`0x80070015`（设备未就绪）。

### 2. `tools\` 子项目

- 仓库里 `tools\SmtcProbe` 是独立工具项目，**不得**被主项目编译。
- 若删掉 csproj 中的 `<DefaultItemExcludes>$(DefaultItemExcludes);tools\**</DefaultItemExcludes>`，会出现 CS0579（重复特性，来自 tools 的 obj 生成 AssemblyInfo）。
- 现有回归探针（都是独立项目，用 `dotnet run -c Release` 直接跑，**不需要宿主 GUI**）：
  - `tools\AudioProbe`：无参=解音频轨并写 wav（`--play` 顺带测设备输出）；`--project`=工程 v2 格式往返与迁移校验（22 项）；`--vdec <视频> <ffmpeg库目录>`=用插件自己的 `FFmpegVideoDecoder` 完整解一遍视频，用于定位「解码侧」问题（区分「文件/解码坏了」与「渲染侧崩了」）；`--loop`=验证「解到 EOF → Restart → 回到开头」；**`--demux <视频> <ffmpeg目录>`=裸 avformat 逐包读完全文件**（每条流包数 + 结束返回码 + seek 到中段能否解码）。**`--vdec` 的帧数应当等于 `--demux` 视频流包数** —— 不等就是解码链路的问题，别去怀疑素材（见约束 17）。
  - `tools\FFmpegProbe`：`--enc` 生成对照 mp4（尺寸写在 `EncodeTest` 里的 `const int w/h`，排查尺寸相关崩溃时临时改），无参=解若干帧。
  - `tools\RaceProbe`：`RaceProbe.exe [视频] [FFmpeg库目录]` = 「解码线程正在 `ReadFrame` 时另一线程 `Dispose`」的竞态回归（5 轮）。
    守护的是这个坑：**释放解码器必须先 `Join` 播放线程、且 `ReadFrame`/`SeekTo`/`Restart` 与 `Dispose` 共用 `FFmpegVideoDecoder._gate` 互斥**，
    否则 native 上下文被 free 后继续读 → `AccessViolationException`(0xc0000005) 静默击穿宿主进程（点「渲染并应用」第一步 `StopPreview` 就会踩到）。
    **⚠️ 它是靠 `<Compile Include>` 链接主项目源码编译的，所以主项目新增/修改「被链接的文件」后要顺手
    `dotnet build tools\RaceProbe\RaceProbe.csproj` 验一下** —— 2026-09-30 就发现它自 `32f97ef`（修 WebP，
    给 `VideoTranscoder.cs` 引入 `Avalonia.Bitmap`、给 `OverlayFrameGenerator.cs` 引入 `InjectorRuntime.ConfigDirectory`）
    起一直编译不过，**烂了整整一轮无人察觉**（主项目编译不受影响，因为 csproj 用 `DefaultItemExcludes` 排除了 `tools\**`）。
    当时的两处补偿：csproj 加 `Avalonia` 包引用 + `Program.cs` 里加 `InjectorRuntime` 桩。
   `--single [视频] [音频] [FFmpeg库目录] [解码尺寸] [秒数]` = 单轨长跑（真机排查：投递率/间隔 +
   播放器每 2 秒的「拍 均/峰 + 每轨 源fps/显/跳/seek/拍峰[闲置原因]」）；
   `--makecfr [输出] [fps] [秒] [宽]` = 合成固定帧率参考素材（区分引擎问题与素材问题）。
   还有 `RaceProbe.exe --stress [视频] [音频] [FFmpeg库目录]` = **预览播放压力测试**（种子）：
   用一个 3 视频轨 + 1 音频轨的工程跑矩阵（1/2/3 轨 × 解码 400/640/800px × 空闲/加载 CPU × UI 卡顿 0/60ms），
   量每轨投递帧率与最大间隔、seek 后到首帧延迟、改倍速后是否还在动；另有
   音频 20ms 块耗时（回调欠载）、以及「声卡位置 vs 渲染位置」偏差（漂移校正的输入是否可信）。
   加 3 个 AboveNormal 忙线程模拟「开着浏览器」，用 `UiSimulator` 把 UI 代价放在**另一条线程**上
   （压在播放线程里就测错东西了）。卡顿类问题先跑它，别靠肉眼。
   另有 `RaceProbe.exe --audio [视频] [FFmpeg库目录] [输出目录]` = 「渲染带音频」端到端回归（探针自己合成 1kHz 正弦当音源，跑真实 `VideoProjectRenderer` 后从 mp4 里解回音频量化）：断言音频时长 == 工程时长、片段起点前静音、片段区间内有声、**变速下频率正确**（2× 保持音调仍 1kHz、2× 变调 2kHz），以及关掉「包含音频」时产物确实没有音轨。守护的是**复用器 `stream_index`** 这个坑（见约束 10）。
  - 探针跑通 **不等于** 宿主里不出问题——很多崩溃只在「解码帧进 Avalonia 渲染层」之后才发生（见约束 9 的 16 对齐）。

### 3. Avalonia 派生控件必须覆写 `StyleKeyOverride`

- 任何从 Avalonia 控件派生的自定义控件（如设置页的 `Spin : NumericUpDown`），必须 `protected override Type StyleKeyOverride => typeof(基类);`，否则隐式主题查找按派生类型找 ControlTheme，而 FluentAvalonia 只注册了基类的主题 → 控件渲染为空（不可见）。
- `StyleKey` 不可覆写；必须覆写 `StyleKeyOverride`。

### 4. FAUI 2.4.1 的 API 命名

- 宿主 FluentAvalonia 版本为 **2.4.1**：
  - 对话框类型是 `ContentDialog` / `ContentDialogResult` / `ContentDialogButton`（`FAContentDialog` 系列是 FAUI 2.5+ 的命名，当前不可用）。
  - `ContentDialog.ShowAsync()` 无参可自动找活动窗口。
  - `InfoBar`、`SettingsExpander`、`FluentIconSource` 均在 `FluentAvalonia.UI.Controls`。
- `FluentIconSource` 实际来自 `ClassIsland.Core.Controls`（非 FAUI），使用 `FluentSystemIcons-Resizable` 字体；图标码点映射文件在 `tools\FluentSystemIcons-Resizable.json`（可下载自 ClassIsland 仓库）。每个图标有 `_filled`（实心）与 `_regular`（空心）两个码点，通常相邻（如 wand `0xF42E`/`0xF42F`）。

### 5. SMTC 事件驱动

- 取色/底图由 `SmtcWatcher` 事件驱动（MediaIsland 同款方案），订阅 SessionManager 的 `SessionsChanged`/`CurrentSessionChanged` 与每个会话的 `MediaPropertiesChanged`/`PlaybackInfoChanged`/`TimelinePropertiesChanged`。
- **判断焦点会话必须用 `SourceAppUserModelId` 字符串比较，绝不能用 `ReferenceEquals`**（CsWinRT 每次 `GetCurrentSession()` 可能返回新的托管包装对象，引用比较永远为 false → 事件全部失效，只剩兜底 Timer 驱动）。
- 保留低频兜底 Timer（间隔 = `AlbumColorPollingIntervalSeconds`）。
- 事件可能在非 UI 线程触发，必须 `Dispatcher.UIThread.Post` 后再改 UI。
- 快照指纹去重：`播放状态|标题|歌手|专辑|缩略图字节数`——**必须含播放状态**，否则暂停/恢复不会触发（无法实现「暂停恢复原色」）。

### 6. 全新安装 = 零改动

- 默认值必须中性：`Shape=HostDefault` 时不写圆角；`RippleType=None`、`VisibilityAnimation=None`、`EmphasisAnimation=None`、`AnimationMode=None`、`CountdownArrowsEnabled=false`。
- 用户显式修改圆角时，`SaveAndApply` 会把 `Shape` 自动切为 `RoundedRectangle` 使自定义圆角生效。
- `ResetToDefaults()`（恢复默认）会保留 `StyleSheetPath` 与 `WatchStyleSheet`，其余回中性默认。

### 7. 分体主界面背景识别（底色填充）

- 分体主界面开关：全局 `Settings.IsIslandSeperated`（注意宿主拼写 Seperated 单 p）；行级 `MainWindowLineSettings.IslandSeparationMode`（0 继承 / 1 禁用 / 2 启用）。
- 分体模式（IsIslandSeperated=True）下宿主隐藏 `Border#BackgroundBorder`（`BackgroundBorderWrapper` IsVisible 绑定取反），改由每行根组件模板渲染 `<Border Classes="line-background"/>`（无 Name）作为背景。
- 底色填充必须同时识别两者：非分体 `BackgroundBorder`（按 Name）+ 分体根组件背景（Name 空、带 `line-background` 类、且不在 `Grid#GridOverlay` 内，见 `IsSplitComponentBackground()`）。`GridOverlay` 内提醒覆盖层的 Border 也带 `line-background` 类，必须排除。
- 分体开关/行级分体切换会即时重建行模板，装饰需重应用：插件订阅宿主 `Settings.IsIslandSeperated` 的 PropertyChanged（`EnsureSplitSwitchSubscription`）+ `OnStateTick` 50ms 轮询统计分体背景数量签名兜底。
- 样式类名 `line-background` 在 `HostContract.LineBackgroundClass`，纳入契约对照表（`classNames` 分组），宿主升级可联网覆盖。
- 目前底色/边框/阴影装饰已适配分体；**分体主界面下整岛底图（图层编辑器底图）已整体禁用（2026-09）**：运行时 `ApplyWallpaper` 判 `IsSeparatedMode()` 跳过渲染并 `RemoveWallpaper`，设置页分体页隐藏「背景图片」图层编辑器入口与「底图模糊」组（`ApplyWallpaperSectionVisibility`），分体页 `SaveAndApply` 不写全局底图（`if (!_splitPage)` 保护，保留非分体下配置）。视频覆盖层宿主约束（`ApplyOverlayHostBounds`）仍识别分体背景（`IsSplitComponentBackground`），会把视频填充约束到分体块并集边界内。
- 底纹纹理分两种形态（`ApplyTextureHost`→`UpdateTextureBounds`）：**非分体，或全局=动态频谱** → 行级宿主（MainWindowLine 每行一个、铺满该行背景并集、跨块连续，见 `UpdateLineTextureBounds`/`PositionTextureHost`）；**分体且全局为静态纹理** → 逐块宿主（`UpdateBlockTextureBounds`/`EnsureBlockTextureHost`/`PositionBlockTextureHost`：每个分块 background 一个宿主、插在该块底色之上内容之下；块覆盖独立于全局开关——无覆盖继承全局、`HasTextureOverride` 且 `TextureType=None`=清除该块、静态=用该块图案/颜色/大小）。动态频谱不可逐块；`RemoveTextureHost`/`UpdateTextureClip` 同时清理行级与逐块宿主；`OnStateTick` 每 tick 走一次（块规格变经宿主 `Tag` 比对才重建画刷）。

### 8. 分体块独立配色（一个分体一个颜色）

- 数据：`InjectorSettings.SplitBlockBackgrounds`（`Dictionary<string, SplitBlockBackgroundSetting>`），键 = 宿主 `ComponentSettings.Id`（组件唯一 GUID，组件增删/排序后颜色不错位）。
- 识别：分体根组件背景 Border → `GetVisualAncestors()` 回溯 `ComponentPresenter`（`HostContract.ComponentPresenterTypeName`）→ 反射读 `Settings.Id` / `Settings.NameCache`（`ComponentPresenterSettingsProperty` / `ComponentSettingsIdProperty` / `ComponentSettingsNameCacheProperty`，均纳入契约对照表）。
- 应用：`ApplyDecorations` 对每个分体块查 `SplitBlockBackgrounds`，命中且 `Enabled` 时用块级颜色/渐变（`BuildBlockBackgroundBrush`），否则回退全局底色。
- SMTC 按块：`SplitBlockBackgroundSetting.UseDynamicColor` 控制该块是否跟随动态取色；`RefreshDynamicColors` 依此只更新对应块的画刷。
- 底纹覆盖数据：`SplitBlockBackgroundSetting.HasTextureOverride / TextureType / TextureColor / TextureSize`（原子覆盖——有覆盖才用该块自己的图案/颜色/大小；`TextureType=None`=清除该块底纹；无覆盖=继承全局底纹；动态频谱不可逐块）。`Clone()`/预设/`CopyFrom` 已同步。运行时逐块渲染见约束 7。
- **分块专属背景图已整体删除（2026-09）**：`SplitBlockBackgroundSetting.HasWallpaperOverride / WallpaperPath` 字段已删（设置模型/Clone/运行时 `_blockWallpaperHosts` 系列宿主方法/设置页「分块背景图片」第三支画笔全部移除）；旧 settings.json 里对应键被 JSON 忽略，无需迁移。分体块外观只剩底色/底纹两支画笔。
- 设置页（`Views/InjectorSettingsPage.cs`）：分体块**作用域选择区**位于页面最顶部（「样式注入器」主标题之上；非分体整区 `IsVisible=false`）。结构：① 带图标 `CommandBar`（全选 / 清空 / 刷新 / 清除配色）→ ② 原生 `DataGrid`（仿宿主「档案→科目」：勾选列在最前，其后 组件名 / 行号 / 当前颜色色块，行模型 `SplitBlockRow`，INPC）。
- **画笔 = 「背景 → 底色填充」与「背景 → 底纹纹理」两支**（旧独立「批量编辑分体块」卡片组已删除）。都由顶部同一份分体块勾选（作用域）驱动：分体页面（`_splitPage`）下两组的“总开关”都隐藏、整组充当分块画笔——勾选（默认全选）哪些分块就只给哪些设置（分别写入其 `SplitBlockBackgrounds` 专属配置：底色 `Enabled=true`；底纹 `HasTextureOverride=true` 等）；**一个都不勾 → 两整组都禁用**；取消勾选某块＝只缩小作用域、不动它现有配置。非分体页面两组件行为不变（全局）。
- 写盘按属性增量：底色画笔控件变化 → `BrushChanged`（仅 `_brushActive` 走分块提交）→ 200ms `_splitCommitTimer` 防抖 → `CommitBrushToBlocks`（已有专属配置的块只改改动项；无配置的块用 `CopyBrushStyle` 整体初始化防跳变）→ `InjectorRuntime.SaveAndApply()`。分体页面下 `SaveAndApply` **不写全局底色/底纹**（`if (!_splitPage)` 保护）。
- 底纹第二画笔：分体页把「底纹纹理」组总开关**保留为“该块底纹开关”**（开=勾选块用自己的图案 / 关=勾选块无底纹，`HasTextureOverride=true`+`TextureType=None`），图案下拉切为逐块静态选项 `BlockTextureOptions`（网格/点阵/斜线/十字，无频谱、也不再混入“无/继承”）；改动（含总开关）经 `WireTextureBrush` → `_pendingTextureCommit` → 同一防抖 → `CommitTextureToBlocks`。混值/回填提示 `RefreshBackgroundTextureState`/`SetBgItemDesc`（“多个值”时开关/图案保持原值仅作提示）。全局为动态频谱时整组禁用提示。两画笔状态由 `RefreshBackgroundBrushState`（包裹 Core+Texture）在勾选/提交变化时一起刷新。
- 取值口径：分块“当前值”＝其专属配置（若 `Enabled`），否则回落全局底色（运行时正是这样显示，故画笔/色块所见即所得）。勾选多个且不一致 → 对应卡片说明追加「（多个值）」提示（`RefreshBackgroundBrushState` / `SetBgItemDesc`，控件保持原值仅作提示，`_suppressBrushRefresh` 抑制回填提交）；重新设置即统一写入勾选块。行色块 `UpdateSplitRowVisual` 同理：专属优先 → 否则全局 → 均无则空。
- 数据枚举仍用 `MainWindowStyleInjector.EnumerateSplitBlocks()`（返回 `SplitBlockInfo`：Id/显示名 NameCache/行号）。让某块回全局 = 勾选它后点「清除配色」（删 `SplitBlockBackgrounds` 配置）。
- 分块显示名：优先 `NameCache`（宿主可能未填充），其次 `AssociatedComponentInfo.Name`（组件类型名如「时钟」「课程表」），最后回退 Id 前缀（`GetSplitBlockDisplayName`）。
- 运行时分块级背景**不依赖**全局「底色填充」开关：块有配置且 `Enabled` 时直接生效（用户显式应用了块配色）。
- 新分体块设置字段需同步：字段 → 属性 → `CopyFrom` → 设置页 `LoadSplitBlockToEditor` / `ApplySplitBlockColorsToSelection`。

### 9. 视频背景 = 纯 FFmpeg（无 WMF 备胎）

- 视频解码只有 `FFmpegVideoDecoder`（FFmpeg.AutoGen **8.1.0**，惰性加载）；**WMF / Media Foundation 已整体删除**（曾因 vtable 手动调用 `SetCurrentMediaTypeByIndex` 触发原生访问违规击穿进程）。
- FFmpeg 原生库（`avcodec-62.dll` 等，版本由 `ffmpeg.LibraryVersionMap` 动态决定）部署在**配置目录\ffmpeg**（用户数据目录，deploy 不清空），通过 `ffmpeg.RootPath` 指向。
- **⚠️ 解码输出尺寸必须向上对齐到 16**（`AlignUp16`，2026-09-27；漏洞在 `Open()` 里）：H.264 宏块是 16×16，`sws_scale` 在宽度非 16 倍数时会按 SIMD 粒度写目标行，而目标缓冲恰好是 `W*H*4`（`stride == W*4`，**行尾零余量**）→ **最后一行的越界写会砸坏托管堆**，之后以 `coreclr.dll` 访问违规（`0xc0000005`）静默击穿进程，且**没有任何托管异常/日志**（`try/catch` 拦不住）。
  - 实测判据：`412×68`（视频编辑器的渲染产物，宽高都非 16 倍数）**必崩**；`416×80`、`640×80` 正常。修在解码器内部后该文件稳定存活。
  - 修在**解码器**而不是渲染侧，是为了同时对任意用户素材生效（不是只有我们的渲染产物会非对齐）。代价：`OutputWidth/Height` 最多比源大 15px、`maxDimension` 上限最多超 15px，视觉无差别（最终尺寸由主界面 `Image` 缩放决定）。
  - 新增任何「把解码帧写进自建缓冲」的路径都要遵守这条（含 `maxDimension` 缩放后的结果）；`VideoTranscoder` 走同一解码器，已自动继承。
  - 排查手法：这类崩溃宿主日志会「戛然而止」，插件日志也停在最后一帧之前；先用 `AudioProbe --vdec` 确认解码侧无恙，再对照「能崩的素材 vs 不崩的素材」找差异（尺寸 / 编码 / 封装）。
- `FFmpegRuntime` 职责：
  - **双档位安装包（A∪B=C，2026-08-30）**：`FfmpegPackageKind.Minimal`（精简解码包 7.2MB，仅解码，壁纸/预览够用）与 `FfmpegPackageKind.Full`（完整包 50.7MB，含 libx264/libx265/aac，渲染剪辑与压缩转码必需）。两包是同一组 5 个 DLL，区别在构建裁剪；运行时唯一可靠判定是 `EnsureLoaded()` 后 `EncoderAvailable`（`avcodec_find_encoder(H264)`）。安装器窗口（`FfmpegInstallWindow`）打开时若无预设档位先让用户选（精简/完整）；剪辑渲染/压缩遇到精简包会引导升级（`VideoEditorWindow.EnsureFullPackageAsync`，含「进程已加载精简库→需重启宿主」提示）。内置源 `DefaultSourceUrl`（min-decode）/`FullSourceUrl`（xxtsoft.top），完整包回退 BtbN/ghps/gyan。包体与制作说明见 `dist/README-ffmpeg-packages.md`。
  - `Refresh()` 启动/下载后检测可用性——**仅查文件存在**。缺失时**绝不调用任何 ffmpeg 函数**（失败委托会被缓存为占位，之后装好库也要重启才能恢复）。
  - `EnsureLoaded()` 仅在文件齐全时调用：设置 `RootPath` 并触发各库惰性加载验证（avutil → avcodec+swresample → avformat → swscale）。
  - `InstallAsync()` 多源联机下载：**内置默认源 `https://xxtsoft.top/support/injector/ffmpeg-8.1-win64-shared-min.zip`（用户自建精简镜像，7.6MB，`FFmpegRuntime.DefaultSourceUrl`）** → 用户设置的自定义源（`CustomFfmpegDownloadUrl`，设置页「自定义 FFmpeg 下载源」）→ GitHub BtbN latest → ghps.cc 代理 → gyan.dev；解压匹配版本 dll 后重新检测。精简源包制作见 `dist/ffmpeg-8.1-win64-shared-min.zip` 与 `dist/README-ffmpeg-source.md`。
- 运行时 `ApplyVideoFill` 先查 `FFmpegRuntime.IsAvailable` + `EnsureLoaded()`，缺失直接降级为无视频（不崩溃）。设置页缺失时禁用整个视频组并显示下载 InfoBar（`RefreshFfmpegAvailability`）；点击「下载」打开 `Views/FfmpegInstallWindow` 安装器窗口（仿 Linux 软件包管理器：进度条/实时速度/剩余时间估算/日志区，单实例，可取消），关闭窗口后 `FFmpegRuntime.Refresh()` 重新检测。
- **视频卡顿修复要点**：覆盖层宿主必须约束到框架内（`ApplyOverlayHostBounds` 识别分体背景，见约束 7）；`_videoFillImage.Source` 是复用的同一实例，引用不变时 `Image` 不会自动重绘（曾依赖主界面动画时钟全窗口重绘导致卡爆），需每帧 `InvalidateVisual()` 局部重绘；`OnStateTick` 50ms 轮询同步 `UpdateVideoFillBounds`。
  - **预览/渲染慢放修复（2026-08-31）**：旧版播放器/渲染器「一拍一帧顺序拉帧」——高帧率源（60fps）在 24fps 时钟下被慢放一半。新调度按**媒体时间**选帧：`TrackState.LastMediaTime/SourceFps/Eof`（`VideoFrameSource.SourceFps` 透传 `FFmpegVideoDecoder.SourceFps`=avg_frame_rate）；拍长抖动/解码慢时丢帧保持速度，落后 >4 帧用 `SeekTo(mediaTime)` 追帧；**UI 未消费（Consumed 未 Set）本拍不发帧**（Post 队列永不积压，UI 卡顿自动丢帧——旧版 Wait(300) 超时继续 Post 会堆积卡死 UI）。编辑器 `PreviewMaxDimension=800`（1280 时 UI 写帧 3.7MB/帧吃力）。打开素材即 seed 到当前媒体时间（拖播放头后不再从入点重播）。
  - **时间轴拖拽体验（2026-08-31）**：`_timelineScroll` 纵向滚动 Auto（轨道多/轨道高时「新建轨道」区曾被面板高度裁掉摸不到）+ 轨道头经 `ScrollChanged` 平移同步；拖拽边缘自动滚动（`_dragScrollTimer` 16ms，横/纵向 40px 边缘带，块拖拽 PointerMoved 与 lane/newTrackZone DragOver 共同驱动，Drop/Clear 时停止）；拖动中块 Opacity=0.8 + `_dropTrackBadge` 落点标签（「轨道 N / ＋ 新建轨道」，ZIndex=40 不被块遮挡）；newTrackZone 28→36；**RefreshTimeline 不再重置纵向 Offset**（旧逻辑每次重建跳回顶部=拖不到下方轨道）。
  - **视频工程（多片段拼接）**：`VideoProject`（JSON 存配置目录 video-project.json）+ `VideoProjectPlayer`（顺序播放片段、跳过入点、按帧数到出点切换下一片段、播完整个时间轴循环；**切换前先 `current.Stop()` 再 Post 到 UI 打开下一片段，否则旧解码线程继续读帧导致级联重开**）+ `Views/VideoEditorWindow`（PR 风格：左素材库、中舞台（宽高比 = 主界面 `GetCurrentIslandSize`，可用「自定义画幅」按钮改）、右片段属性、底时间轴；编辑后「渲染并应用」写工程并 `VideoProjectEnabled=true`）。运行时 `ApplyVideoFill` 检测到工程（`HasVideoProject()`）则走 `ApplyVideoProjectFill`，单文件模式停工程播放器（反之亦然）。片段入点裁剪用 `FFmpegVideoDecoder.SeekTo`（`av_seek_frame` + 流 time_base 换算）。
  - **素材转码压缩**：`VideoTranscoder.Compress`（`VideoTranscoder.cs`）= `FFmpegVideoDecoder`（新增 `SourceFps`，读 avg_frame_rate）逐帧解码 → `FFmpegVideoEncoder` 按源帧率重编码 720p CRF27 mp4（丢音频），输出到配置目录 `video-cache/`（文件名含源大小防冲突、可复用、取消清理半成品）。视频编辑器「添加素材」时弹「压缩后导入/直接导入/取消」（多选应用到全部；精简包先引导升级完整包）。
  - **图片覆盖层（Kind=Image）**：`VideoClip.Kind="Image"` + `SourcePath=图片`。`OverlayFrameGenerator.Render` 加图片分支（图片按原比例居中画进整帧、透明底、预乘；System.Drawing 位图静态缓存），播放器/渲染器/编辑器 scrub 的「非 Video=覆盖层静态帧」路径**全部自动生效**，无需各自改。素材库支持图片（缩略图直接解码）；导入固定 5 秒、可拖可调。与文本/形状共用右侧「覆盖层」属性区（图片隐藏内容/颜色/形状三行）。
  - **底图图层编辑器 → 视频编辑器互通**：底图编辑器命令栏「导入视频编辑器」（`\uF3E1`）→ `WallpaperLayerCanvas.ExportIslandSnapshot`（临时隐藏装饰/棋盘格/岛屿提示，`_stage.RenderTransform=Translate(-CanvasMargin)` + RenderTargetBitmap 按 RenderScaling 渲染主界面区域，直接 Save PNG 到 `video-import/`）→ `VideoEditorWindow.ImportImageAsClip`（Kind=Image 轨道 0 底层、时长=max(工程时长,5s)）。底图记录相对位置、快照比例=主界面比例，图片覆盖层「原比例居中」基准下比例一致即铺满=按视频舞台画幅排好。
  - **工程自动保存**：任何修改（增删/拖拽/裁剪/变换/画幅）经 `ScheduleSave()`（600ms 防抖）→ `SaveProject()`，`Closed` 时立即保存——「对齐后重开丢失」根因就是只在渲染时保存。新字段（如 `ScaleX/ScaleY`）须同步：字段 → `CopyFrom` → 设置页。
  - **非等比缩放**：`VideoClip.ScaleX/ScaleY`（相对 `Scale` 的乘数）。角手柄等比、上下边只改 `ScaleY`、左右边只改 `ScaleX`；`CaptureResizeStart` 按下冻结基准尺寸，中心=锚点+符号×新尺寸/2（被拖边/角跟手），不钳制中心。渲染器/运行时/编辑器三处变换都用 `Scale*ScaleX/ScaleY`。
  - **空轨自动删除 + 新建轨道**：`CompactTracks()` 把用中轨道压缩为 0..n-1；泳道底部 `NewTrackZoneHeight=28`「新建轨道」拖放区（Drop 传 `trackCount` 作新轨号）；块释放 `Math.Clamp((int)(rootY/LaneHeight),0,32)`。
  - **时间轴面板高度自适应**：根 Grid 行 `Auto,*,Auto`（勿回 190 固定）+ `MaxHeight=460`，`RefreshTimeline` 重置 `_timelineScroll.Offset.Y`，否则多轨溢出 → ScrollViewer 滚动 → 泳道/轨道头错位。
  - **工具条按钮用 `Button + IconText`**（28x24 紧凑，`ClassIsland.Core.Controls.IconText`），勿用 `CommandBarButton`（即使 `IsCompact` 也 64px 高）；输出/轨道计数/状态文本同一行。
  - **seek 帧预览**：非播放时 `SetPlayhead` → `ShowFrameAt`（后台解码各轨覆盖片段帧，`_seekFrameGen` 代次 + 单 worker 合并高频 scrub）。
  - **可拖拽分割条**：`VerticalSplitter`/`HorizontalSplitter`（6px 透明 Border，hover 强调色）。body 列 `"{assetW},6,*,6,{inspW}"`（字段 `_assetPanelWidth`/`_inspectorWidth`）；根行 `"Auto,*,6,Auto"`。**水平分割条方向坑**：时间轴面板锚定窗口底部 → 行高 = `startH - delta`（下拖变矮/上拖变高），`get` 须返回当前实际行高（自动时 = `_timelineChromeHeight`）。
  - **撤销/重做**：快照栈（List，`CloneProject` 深拷贝，MaxUndoDepth=100）；`PushUndo(coalesce)` 合并 500ms 内连续数值调整（`_lastPushCoalesced` 防吞离散操作）；拖拽类操作 `_xxxUndoPushed` 首次实际移动才压；`RestoreProject` 按索引恢复选中 + StopPreview + 清 `_stageLayers` + Refresh + Save。按钮 `\uE195`/`\uE121` + Ctrl+Z/Y（Shift+Z 重做）。
  - **时间轴标尺 + 缩放**：`_pxPerSecond` 字段（2..60，勿当常量）；`RulerHeight=22` 标尺在 `_timelineRoot` 顶（`BuildRuler` 主/次刻度 + `NiceTickInterval` 90px/刻度，≥60s 显示分钟）；泳道 `Canvas.SetTop(_timeline, RulerHeight)`、轨道头 StackPanel 顶部加 22px 空 Border 对齐、newTrackZone/playheadHeight 均 +RulerHeight。缩放：按钮（`\uF4D0` 放大/`\uF4D2` 缩小 + `_zoomText` 百分比）+ Ctrl+滚轮（锚定鼠标下时间，`newOffset=t*(newPx-oldPx)+oldOffsetX`）+ 普通滚轮横向滚动。
  - **舞台移动不飘**：`ApplyResizeDrag` handle==8 用绝对参考（`newCx = startRect.Center.X + (current.X-start.X)` 写回 Offset），**勿用 `OffsetX += delta` 增量**（PointerMoved 每帧累积 → 越拖越飘）。
  - **时间轴片段拖拽跟随指针**：按下把块移到 `_timelineRoot`（泳道 `ClipToBounds` 会裁剪跨泳道浮动），`block.ZIndex=30`；`PointerMoved` 用 `GrabX/GrabY` 跟手 + `UpdateDropHighlight` 高亮目标泳道；释放 `rootPos.Y - RulerHeight` 落轨。
  - **左侧工具栏**：body 列 `"44,{assetW},6,*,6,{inspW}"`，col0=44px 工具条（选择/文本/矩形/椭圆/效果，`ToolButton` 紧凑图标）；`AddOverlayClip` 加文本/形状覆盖层。**舞台点击放置的处理器必须挂 `_stageBorder`**（不能挂 `_stageHostGrid`——它是子级，空白处点击事件源是 `_stageBorder`，冒泡不含 Grid）。
  - **文本/形状覆盖层**：`VideoClip` 加 `Kind/Text/Color/Shape/Grayscale/FlipX/FlipY`；`OverlayFrameGenerator.cs`（System.Drawing 渲染）；播放器覆盖层返回静态帧、渲染器预乘合成+翻转/灰度、`WriteFrameToImage` 加灰度参数、`ApplyVideoClipTransform` 支持 FlipX/Y。
- **版本号必须从 `ffmpeg.LibraryVersionMap` 动态读取，勿硬编码**：本仓库用的是 **8.1**（avcodec-62/avformat-62/avutil-60/swresample-6/swscale-9）；9.0 是 avcodec-63/avformat-63/avutil-61/swresample-7/swscale-10；7.1 是 avcodec-61/.../swscale-8。`FFmpegRuntime` 按主版本生成候选（`known = 9.0 / 8.1 / 8.0 / 7.1 / 7.0`，因为 avcodec 主版本 62 同时对应 8.0/8.1）逐个尝试。升级 FFmpeg.AutoGen 时 `FFmpegVideoDecoder` 的 `ffmpeg.SWS_BILINEAR` 已改为 `(int)SwsFlags.SWS_BILINEAR`。

### 10. mp4 双流复用（渲染带音频）的两个坑

渲染产物要带音轨时，`FFmpegVideoEncoder` 会在视频流之外再建一条 AAC 流（原生 `aac` 编码器 + `SwrContext` 做 s16 交错 → fltp 平面；`swresample` 本来就在必需库清单里，不需要多下发 dll）。实测过两件事：

- **`pkt->stream_index` 必须自己写**：`avcodec_receive_packet` **不会**设置这个字段（默认 0）。单流（纯视频）时因为视频恰好就是 0 号流而看不出问题；一旦加音轨，音频包会被当成视频包塞进 0 号流 → 复用器刷 `Application provided invalid, non monotonically increasing dts to muxer in stream 0`，并把视频轨写坏：产物只剩 1~2 KB、`Could not find codec parameters for stream 0 ... unspecified pixel format`，再读它会在 libswscale 里断言崩掉（`Assertion desc failed at libswscale/swscale_internal.h`）。`DrainPackets(ctx, pkt, tb, streamIndex)` 里按所属流显式赋值，是唯一的修法。
- **两路流各自的时间基要分开记**：视频 `_tbStream`（写头后被复用器改成 1/10240 之类）与音频 `_tbAudioStream`（1/48000）不是一回事，`av_packet_rescale_ts` 必须用对应的那个；`Finish()` 里两路编码器都要 flush（音频尾巴不足一帧要补静音凑满 AAC 的 1024）再 `av_write_trailer`。
- **音频时间轴用「累计样本号」推进**（`endSample = round(t × 48000)`），不要按帧长做浮点累加——非整数帧率（29.97 等）下后者会积累漂移。AAC 有 ~1024 样本（21ms）前置延迟，mp4 复用器会自己写 edit list 补偿，不用手动处理。
- 渲染选项里的「包含音频」（`InjectorSettings.RenderIncludeAudio`，默认开）会在产物带音频时**顺手打开底图的「播放声音」**（`VideoFillAudioEnabled`），否则用户会以为渲染没声音。

### 11. 预览播放器的调度陷阱（`VideoProjectPlayer`，2026-09-29 压力测试后定稿）

「画面突然卡住只剩声音 / 音频断续 / 音画越来越不同步」这三类症状，实测根因如下（探针 `--stress` 可复现）：

- **消费信号会丢（最致命，症状=画面永久卡住）**：`PumpTrack` 里必须**先 `Consumed.Reset()` 再调 `_onFrame`**。
  反过来写时，UI 线程可能在 `_onFrame` 返回**之前**就消费完并 `MarkTrackConsumed`（Set），
  随后那句 `Reset()` 把信号抹掉 → 此后 `!Consumed.IsSet` 恒成立 → **该轨永久不再投递**
  （片段不换就一直不动）。实测修前 8 秒只投递 6 帧，修后 139 帧。
- **追赶必须有上限**：源帧率远高于显示帧率时（手机视频常见 54fps vs 显示 24fps），
  「把中间帧顺序解出来丢掉」要 54 次解码/秒/轨；解码全在**一条线程上串行**，
  一旦拍内解码超过拍长就会滚成正反馈（拍越耗时→落后越多→解得越多），投递率崩到 1fps。
  故 `CatchUpSeekFrames = 8`（落后超 0.33s 直接 seek：实测 seek+解一帧 76ms，硬解 32 帧要 370ms）
  + `MaxSkipPerTick = 4`（单拍解码量封顶）。
- **`Consumed.Wait` 不要等久**：pump 是逐轨串行的，等 150ms 时 3 轨 + UI 忙会被拖到 450ms/拍。
  现在只等 5ms —— 防覆写的判断在下一拍的 `!Consumed.IsSet → return`，不靠这个等待。
- **`AudibleTime` 必须按锚点算，不能拿声卡累计位置当绝对时间**：`WasapiOut.GetPosition()` 是
  「自流启动起算」的累计值，而我们自己的 `Seek` 只改 `_baseTime`。直接相加会在每次 seek 后
  凭空多出「已播时长」的偏移（实测 Seek(10) 后恒定偏 **+3.01s**），于是音画漂移校正拿到假漂移、
  每 5 秒重定位一次 —— 每次重定位都丢弃并重开音频源（听感断续），且持锁阻塞视频线程（画面卡顿）。
  现在按 `锚点渲染位置 +（声卡位置 − 锚点声卡位置）` 算，起播 / Seek / Resume 都重新锚定。
  漂移容差也从 0.15s 放宽到 **0.30s**（声卡缓冲本身就有 ~0.12s 固定延迟，容差贴太近会被噪声触发），
  并加 10s 冷却（一次重定位 = 一次可听的断点）。
- **诊断入口**：`video-player.log` 每 2 秒一行，现在带**单拍耗时**（`拍 均28ms/峰67ms`）与
  **每轨闲置原因**（`此刻无片段 / 无解码器 / 源已播完 / 等UI消化 / 正常`）—— 卡顿时先看这行。
- **节拍精度**：`Thread.Sleep` 默认粒度 15.6ms，而 24fps 的拍长只有 41.7ms —— 播放期间必须
  `timeBeginPeriod(1)`（停止时恢复，引用计数），否则拍长被拉到 ~57ms、单轨投递率卡在 17fps。
  修后单轨 800px = 23.1fps（间隔 43ms）。**这是「画面比音频慢半拍」的主要来源之一。**
- **追赶阈值按时间算不按帧算**：`MaxPictureLagSeconds = 0.15`（歌词对拍的容忍边界），
  按源帧率换算成帧数。写死 8 帧对 54fps 源等于允许落后 0.33s，字幕就会对不上。
  seek 追平后要**本拍就把画面补上**（seek 后直接 return 会让画面一顿一顿）。
- **时间映射被改（倍速 / 裁剪）后帧游标会失效**：目标帧号可能大幅**倒退**，
  此时 `behind <= 0` 恒成立、画面会停住（0.5× 实测停 6 秒）→ `behind < -2` 时重新 seek 对齐游标。
- **时间轴清空（`_duration == 0`）必须停播**：否则每拍都判「播完了 → 循环复位」，
  播放头会在 0s 附近抽搐。播放器在 `_duration <= 0` 时退出线程并把时间归零，
  编辑器侧 `SyncPreviewWithProject()` 负责停播 + 回 0s。
- **解码成本 = 源帧率 × 分辨率，与「显示多大」无关**：同一台机器 320px 11.4ms / 800px 12ms /
  1280px 13.1ms（降到 320px 只省 2ms），所以「预览画质自适应」主要省的是 UI 上传/合成，不是解码。
  **真正的杠杆是素材帧率**：手机视频常见 54~60fps，而显示只要 24fps ——
  实测同一段素材解一帧 7.4ms、每轨每秒要解 54 次（0.4 核/轨）；转成 30fps CFR 代理解一帧 1.87ms、
  每轨每秒 30 次（0.045 核/轨），差 9 倍。
- **按轨并行解码（2026-09-30 落地）**：`VideoProjectPlayer` 每轨一条 `TrackWorker` 线程，
  主循环只负责打拍（异步派发，**不等收工**：同步等引入两次线程交接，实测单轨从 23fps 掉到 16.5fps）。
  规则：**解码器的打开 / seek / 释放只在该轨线程上发生**，跨线程只通过 `Signal`/`Done`/
  `RestartPending` 握手；`Dispose` 绝不代替它们释放（各轨线程退出前自释放），
  否则回到「跨线程释放 native 上下文 → 静默崩溃」的老坑。跳转是粘性标记（RestartPending），
  不会被随后的普通拍覆盖。
- **CFR vs VFR**：帧号换算 `targetN = mediaTime × 源帧率` 只在**固定帧率**下严格成立；
  VFR 手机视频会让「落后多少帧」的判断失真（进而频繁 seek）。测试时用 `--makecfr` 合成
  固定帧率素材把「引擎问题」与「素材问题」分开。
- 实测结论（本机 4 核）：**代理素材（800px / 30fps CFR）下单轨、双轨、三轨全部 24fps**
  （间隔均值 42ms = 精确 1/24s）；用原始 54fps 素材则三轨只有 ~15fps。

### 12. GDI+ 没有 WebP 解码器（覆盖层图片必须归一化，2026-09-30）

- `OverlayFrameGenerator` / `VideoProjectRenderer` 走 **GDI+（System.Drawing）**画图，而 **GDI+
  没有 WebP 解码器** —— Windows 那个「WebP 图像扩展」只注册 WIC 编解码器，GDI+ 不走 WIC。
  实测 `new Bitmap(stream)` 对 WebP 抛 `ArgumentException: Parameter is not valid.`（png/jpg 正常）。
- 症状（**没有异常、没有崩溃，只有静默的错**）：`GetCachedImage` catch 返回 null → `Render` 返回 null →
  ①**预览在该片段上整段不动**（`video-player.log` 里状态恒为 `[覆盖层待发]`、「显N」帧号不增长）；
  ②**渲染成片里图片静默消失**（`VideoProjectRenderer` 拿不到 buffer 就不合成）。
- 迷惑点：素材缩略图（`VideoEditorWindow.LoadAssetThumbnail`）、素材信息、主界面底图
  （`MainWindowStyleInjector.cs:3216`）都走 **Avalonia/Skia**，WebP 正常 → 表现为「有的地方能看有的地方坏」。
- 修法：`VideoTranscoder.IsGdiDecodable()` 探针 + `VideoTranscoder.NormalizeImage()` 用
  Avalonia/Skia 解码（`new Avalonia.Media.Imaging.Bitmap(stream)`，与缩略图同一套，可后台线程）另存 PNG 到
  配置目录 `image-cache/`（文件名带源大小防同名冲突），**导入素材时**（`ImportAssetsAsync`）与
  **打开工程时**（`VideoEditorWindow.NormalizeProjectImagesAsync`）改写引用。实测 2048×1261 无损 WebP：
  Skia 解码 94ms + PNG 编码 868ms，一次性成本。
- 不要为了省事把 WebP 从选择器里删掉：插件其它链路都支持它，只有这一条是 GDI+ 的限制；转一下就好。

### 13. `clip.Track` 的负值陷阱（音频片段恒为 -1，别裸索引，2026-09-30）

- **音频片段的 `Track` 恒为 -1**（音频轨号在 `AudioTrack`，见「工程文件格式」）。历史数据里还可能有
  被拖拽写成视频轨号的脏值。
- 因此 **`clip.Track < _stageLayers.Count` 这种守卫是错的**：`-1 < Count` 恒成立，
  接着 `_stageLayers[-1]` 直接抛 `ArgumentOutOfRangeException`(index -1)。
  实测踩点：点「分离音频」→ `DetachAudioFromSelection`（构造时落了 `Track = -1`）→
  `FillPropertyPanel → ShowSelectedClipFrame` → 崩。
- 规矩：
  1. 取片段所在泳道用 `LaneOfClip(clip)`；
  2. 取片段对应的舞台图层用 `StageLayerOf(clip)`（判 `Track < 0 || >= Count` → null），
     **不要再手写 `< _stageLayers.Count`**；
  3. `VideoProject.Normalize`（读盘必经）会把音频片段的 `Track` 强制写回 -1，兜住历史脏数据。

### 14. 挂起背景播放：画面与音频必须各挂一次

- `MainWindowStyleInjector.SuspendBackgroundPlayback` 管的是**两条独立链**：
  `_videoSource`（画面）+ `_videoAudio`（「播放声音」的音频输出），
  以及 `_videoProjectPlayer`（工程背景，自带 Pause/Resume）。**三者都要处理**。
- 曾经只挂了画面 → 编辑器一打开就「画面停了、声音还在放」。
- `VideoAudioPlayer.SetSuspended` 的拦截点在 `DecoderWaveProvider.Read`（吐静音、**不推进解码器**，
  位置保留）：WASAPI 的拉取是它主动发起的，在调用方挡不住。

### 15. 视频轨号重映射必须排除音频片段（`Track = -1`）

- 凡「把某些轨号 +1 给新轨腾位」或「把轨号压缩成连续」的循环，**必须 `if (c.IsAudio) continue;`**。
  音频片段的 `Track` 恒为 -1（占位约定，真实轨号在 `AudioTrack`），参与整数运算会把 -1 变成 0
  ——一个**伪造的视频轨号**，随后 `TrackCount`（只统计非音频片段）凭空 +1，多出一条没有任何
  视频片段的空泳道，且每做一次「拖到轨道边界新建轨道」就再涨一轨。
- 实测症状：「添加/移动素材后莫名其妙出现一个新的空轨道」（2026-09-30 定位）。
- 已在三处加固：`CompactTracks`、`HandleTimelineDrop` 的插入新轨分支、
  `OnTimelinePointerReleased` 的组拖新建轨 / 落既有轨分支（后两者还额外跳过了音频片段的
  `FitsOnTrack`/`FitToTrack`，音频占用判定走 `FitsOnAudioTrack`）。

### 16. 时间轴交互状态必须能被「指针移出窗口」收敛

- 片段拖拽**刻意不做指针捕获**（窗口级 `PointerMoved`/`PointerReleased` 驱动，规避重挂载导致的不跟手），
  代价是：指针在**窗口外**松开时 `PointerReleased` 不会来 → `_moveGroup` / `_trimState` /
  `_marqueeStart` 留在置位状态。块会一直浮在 `_timelineRoot` 上挡住泳道，泳道按下也反复重新捕获框选起点。
- 用户看到的是「打开编辑器后轨道点不动、跟卡住一样」，而 **resize 一下就好了**（resize 触发
  `RefreshTimeline` 从模型整体重建，把残留视觉状态一并冲掉）。
- 兜底：窗口级 `PointerExited` / `Deactivated` → `CancelTimelineDrag()`（该函数现在同时清
  `_marqueeStart`），泳道另挂 `PointerCaptureLost` 清框选。

### 17. 解码循环必须遵守 FFmpeg send/receive 协议（否则 HEVC 只解出开头几十帧）

- **现象**：本地视频底图/预览反复只播开头一小段（用户报「一直循环播放开头那几秒」）。
- **真因**（2026-09-30 定位，此前一度误判成「素材残缺」，是错的）：`FFmpegVideoDecoder.ReadFrameLocked`
  把 `avcodec_send_packet` 返回的 **`AVERROR(EAGAIN)` 当成致命错误直接 `return false`**。
  EAGAIN 的真实含义是「解码器输入队列满了，先把已就绪的帧取走再回来喂」。
  旧代码的形态还有一个连带毛病：每个包只 `receive_frame` 一帧就 `return true`，
  **从不排空**，加上多线程解码的重排缓冲，HEVC 素材会很快撞上 EAGAIN → 被误判成 EOF。
- **实测影响面**（A/B 对照，`tools\AudioProbe --vdec`）：**HEVC 素材塌得很惨，H.264 正常**。
  同一个文件修复前 `30` 帧 → 修复后 `5220` 帧；三个 H.264 对照素材修复前后分别是
  `4379→4382` / `1282→1282` / `4349→4350`（只多出 1~3 帧，那是 flush 排空延迟帧带来的正确性提升）。
  所以症状看起来像「某些视频坏了」，实际是**编码相关的触发条件**。
- **正确写法**（现在的实现）：一个 while 循环里
  ① 先 `receive_frame` 取帧（取到就返回）；
  ② 取不到且已 flush 过 → 才是真 EOF；
  ③ 否则 `av_read_frame` 取下一个包，非目标流要 `av_packet_unref` 后继续（旧代码漏 unref 会泄漏）；
  ④ 读到文件尾先 `avcodec_send_packet(ctx, null)` **flush 一次**排空重排缓冲里的延迟帧，
     置 `<c>_draining = true</c>`，然后继续取帧；
  ⑤ `send_packet` 返回 `< 0` 时**只有非 EAGAIN 才算错误**。
  `_draining` 必须在 `RestartLocked` / `SeekToLocked` 里复位。
- 附带好处：以前**每个片段的最后几帧会被丢掉**（延迟帧从未取出），现在补齐了。
- **排查手法（先分清层次，别急着赖素材）**：
  `tools\AudioProbe --demux <视频> <ffmpeg目录>` 逐包读完全文件，给出「每条流各多少个包 + 结束返回码 +
  seek 到中段能否解码」。`--vdec` 的解码帧数**应当等于** `--demux` 里视频流的包数；
  两者不一致就是**解码链路**的问题，不要发散去怀疑编码太新或文件损坏。
  （验证过：`--demux` 读到 5220 个视频包、正常 EOF、seek 到 100s 解得动 → 文件完好。）
- `VideoFrameSource.WarnIfFileTruncated()` 仍保留在首次 EOF 对账「解出帧数 vs 声明时长×帧率」，
  但它**不是第一诊断手段**：只有解码循环确认无误后再拿它怀疑素材。阈值 25%，只告警一次。

### 18. 时间轴工具条那一行**不放任何 label**（状态提示走 `_statusText` 接收器）

- 用户明确要求：「那个位置不要有任何 label，什么『选择工具。』之类的」（2026-09-30，说过两次）。
  时间轴工具条的左半行**只有控件**（工具下拉 / 刀片 / 删除 / 画幅 / 定格），不要加文字说明。
- 原来的 `TextBlock _statusText`（显示「选择工具。」「预览播放中…」「已删除 N 个片段」等）**已从界面移除**，
  替换为同名同 API 的 `StatusSink` 接收器：`.Text = "…"` 仍是合法写法（全文件 70 处调用点不用改），
  只是改为写 `video-editor.log`（`[status] …`），`.Text` 仍可读
  （「不要覆盖上一条自动放到最近空位的提示」那处判断依赖它）。
- 改这块时的注意事项：**不要因为「删掉了 label」就去删那 70 处赋值** —— 它们是提示语的生产者，
  留着进日志是有价值的排查线索；真要恢复界面提示，应该换成一个**瞬时**呈现（toast/浮层），
  而不是把常驻 label 加回去。

## 预设商店（PresetStore）

- 数据源与格式沿用此前约定：索引 `https://xxtsoft.top/support/injector/presets/index.json`（schemaVersion 1，camelCase、大小写不敏感），条目字段 = `Defaults/preset-index.sample.json`（id/name/author/school/description/pluginVersion/minPluginVersion/createdAt/downloadUrl(.cizip)/previewUrl(.png)/sizeBytes，可选 downloads 供热门排序）。
- 入口：设置页「用户预设 → 预设商店」；窗口单实例（`PresetStoreWindow.Current`）。窗口用 `MyWindow`（FA `AppWindow`）+ `TitleBar.ExtendsContentIntoTitleBar` + `TitleBarHitTestType.Complex`（宿主 SettingsWindowNew 同款）。
- **2026-09-05 QFW 对照改造（最终形态，用户要求 1:1 照 QFluentWidgets 源码）**：结构 = QFW `MSFluentWindow`：**标题栏横跨全宽（48px，透明透 Mica，浮顶层）+ 内容整体从 48px 下开始**（`hBoxLayout.setContentsMargins(0,48,0,0)`）。侧边导航 = `Views/StoreNavBar.cs`，照 QFW `NavigationBarPushButton` 绘制规格 1:1 手写（按钮 64x58/圆角5/图标20x20@y13/文字11px@y32/选中=白(浅)或rgba(255,255,255,42)(深)底+左侧指示条(0,16,4,24)圆角2强调色+**filled 实心图标**+强调色文字/hover rgba(0|255,9)/pressed α6/未选中图标 opacity0.6→hover 1）；图标 regular/filled 码点成对（home 59796/59795、apps 57455/57454、fire 59453/59452、library 60034/60033）。标题栏 = QFW CustomTitleBar 规格：左 20px 处图标 18x18+标题、搜索框**固定 400 宽居中**（原生样式+InnerLeftContent）、刷新+caption 150。页面切换 = Avalonia 原生 `TransitioningContentControl`+`PageSlide(240ms)`（页面对象缓存于 `_pages`，切 Content 即过渡）。**注意**：曾试过 FA `NavigationView`（LeftCompact）与"pane 铺满"两种布局，用户均不满意；QFW 真实结构是标题栏全宽+窄栏图标上文字下，`NavigationBar`（微软商店风）≠ `NavigationInterface`（汉堡折叠风）。Banner = 原生 `Carousel`（PageSlide 400ms，每页 Tag=entry，点页空白进详情）；骨架屏 = 宿主 `Shimmer`（`AutoDetectContentLoadState=false`+图片到位手动 `IsContentLoaded=true`，失败也要置 true 停呼吸）；网格 = FA `ItemsRepeater`+`UniformGridLayout`（`MinItemWidth/MinRowSpacing`，`FuncDataTemplate` 建卡，`ItemsSource` 整表赋值）。
- 下载安装流程与「双击 .cizip」共用：`PresetStoreService.DownloadPresetAsync`（下载到 配置目录\store\downloads，zip 可读性校验）→ `PresetExchange.Import` → `PresetInstallDialog.ShowAsync` 确认 → `InjectorRuntime.ImportUserPreset` → `PresetStoreService.MarkInstalled`（store/installed.json 记录 id/安装名/源 createdAt，用于「已安装」状态与「商店端有更新」检测）。
- 获取按钮状态机：未安装=强调色「获取」/ 下载中=禁用+进度文本 / 已安装=禁用「已安装」/ 有更新=「更新」/ `minPluginVersion` 不满足=禁用「需要插件 vX.Y.Z」（`Version.TryParse`，任一端解析失败视为兼容）。一个 entry 可对应多个按钮（banner/卡片/详情），由 `_getButtonEntries` 字典统一渲染与进度刷新。
- 预览图：内存 + 磁盘（store/previews）双缓存，`SemaphoreSlim(6)` 限并发；`Bitmap` 解码在 UI 线程、下载在后台，完成后 `Dispatcher.UIThread.Post` 回填。
- 纯代码 UI 注意（此窗口踩过的坑）：FA `AppWindow` 已有 `Icon` 属性，静态图标辅助方法勿命名 `Icon`（CS0108）；`ToolTip.SetTip` 不能写进对象初始化器（`ToolTip.Tip = …` 是 attached property，CS0747）；Avalonia 11 的 `ScrollViewer` 无 `ScrollToHorizontalOffset`（用 `Offset = new Vector(...)`）；`RowDefinitions.Add` 收 `RowDefinition` 不收 `GridLength`；out 参数不能被 lambda 捕获（先拷局部变量）；`ScrollBarVisibility` 在 `Avalonia.Controls.Primitives`。

## 设置持久化

- `InjectorSettings` 用 System.Text.Json 序列化到 `settings.json`（全字段写入）。改动字段默认值时只影响「缺字段」的旧配置与全新安装；已有 JSON 会覆盖新默认。
- **底图简单模式已整体删除（2026-08-31）**：图层编辑器是唯一入口；`WallpaperSource/WallpaperPath/WallpaperOpacity/WallpaperDisplayMode/WallpaperScale/WallpaperOffsetX/Y/WallpaperSlideshowIntervalSeconds` 等设置属性已从模型删除（`WallpaperSource`/`WallpaperDisplayMode` **枚举保留**——图层功能共用）；`WallpaperDesignerEnabled` 属性保留但恒 true（settings.json 兼容）；`WallpaperBlurRadius` 保留（作用于整个底图宿主，图层共用）。旧简单模式配置在 `InjectorSettingsStore.MigrateLegacySimpleWallpaper` 加载时迁移为一个铺满主界面的图层（幻灯片文件夹不迁移）。设置页背景图片区 = 单行卡片（打开图层编辑器按钮 + 底图开关）+ 底图模糊行。运行时 `MainWindowStyleInjector` 只剩图层渲染路径（`WallpaperHostMode.Simple` 已删）。视频填充组不再依赖专家模式显隐。
- 设置变更经 `Changed` 事件 → `InjectorRuntime.SaveAndApply()` → 保存 + UI 线程 `Apply()` + 更新 SMTC watcher。
- 预设（`ApplyPreset`）不修改基础变形：`CaptureProtectedSettings()` / `RestoreProtectedSettings()` 保护 不透明度/缩放/位置/旋转/圆角/固定尺寸/底图/动态取色/轮询等设置。

## 代码风格约定

- C#，Nullable 启用，ImplicitUsings 启用。
- 文件内使用中文 XML 文档注释说明意图。
- 新设置属性必须同步更新：字段 → 属性 → `CopyFrom` → `ProtectedSettings`（如相关）→ 设置页 `LoadFromSettings`/`SaveAndApply`。
- 涉及 UI 线程访问必须通过 `Dispatcher.UIThread.Post`。
- 任何 WinRT 调用都要 try/catch 兜底，异常不能冒泡到宿主。

## 界面改动与检查器结构（2026-09，持续更新）

- 设置页（`Views/InjectorSettingsPage.cs`）已删除的冗余文案/控件：
  - 页面最底部状态文本 `_status` 已从 `panel.Children` 移除（不再显示）；字段与 `_status.Text = …` 赋值仍保留（纯读字段无 CS0414，仅不渲染）。
  - 顶部「分体块背景」CommandBar 下方的大段说明 TextBlock 已删（只留标题 IconText）。
  - 「自定义 FFmpeg 下载源（可选）」卡片的 0.6 透明度说明 TextBlock 已删（保留加粗标题 + 输入框 `_customFfmpegUrl`）。
- 分体主界面警告：`MainWindowStyleInjector` 新增 `public static void DisableIslandSeparation()`（优先写宿主 DI `SettingsService.Settings` 的 `IsIslandSeperated=false`，回退 App.Settings；try/catch 兜底）。设置页在 `IsSeparatedMode()` 时新建 **Warning InfoBar**（“仍部分未完全适配，不建议使用”）+ ActionButton「关闭分体主界面」→ 调该方法 + 关 InfoBar + `RefreshSplitBlockList()`。
- FFmpeg 已就绪（`FFmpegRuntime.IsAvailable`）时隐藏自定义下载源区：`RefreshFfmpegAvailability()` 内 `_customFfmpegPanel.IsVisible = !available`（安装器关闭 / 删除库后该函数被调，天然联动）。
- **底图图层编辑器检查器（`Views/WallpaperLayerEditor.cs`）改为 TabStrip 分段分组**（仿视频编辑器，ClassIsland 原生 `TabStripStyle` + `compact` 类 + `AnimatedIconButton display-role` 图标按钮，选中展开文本）：
  - 段与分组页：`general`「图层」(名称/SMTC 模式/暂停隐藏/不透明度/显示方式/画布图层操作)、`content`「内容」(形状+文本专属行)、`effect`「效果」(投影)、`transform`「变换」(尺寸/旋转/相对定位/重置变换)。画笔/选区仍是工具上下文组，独立于分段。
  - **「扩展到整个显示框架」与九宫格切图已整体删除（2026-09）**：`FullscreenExtend / SliceEnabled / SliceTop/Bottom/Left/Right` 字段已从模型删除，`WallpaperNineSliceVisual.cs`、`Views/SliceEditorWindow.cs` 文件已删，运行时全屏宿主（`_fullscreenCanvas` 系列 + `UpdateFullscreenLayers/DisposeFullscreenHost`）与 `ApplyDecorations` 的全屏隐藏底色/边框/阴影逻辑一并移除。保留 `IsCanvasLayer` 与变换页 3×3 `AnchorGridPicker`（相对定位锚点，与九宫格切图无关）。旧配置里这些键被 JSON 忽略，原「全屏扩展」图层回退为普通图片图层。
  - 字段：`_inspectorSegmented/_inspectorTabs/_inspectorPages/_activeInspectorPage/_noLayerHint/_lastSelectionProfile/_updatingSegments`。构造：`BuildInspector()` 末尾建页并 `BuildInspectorTabStrip()`；辅助：`ActivateInspectorPage/SetPageVisibility/RefreshInspectorSegments`。
  - 段显隐：内容=全部选中同类型且是 形状/文本；效果=是图片；变换=非 SMTC 默认处理；画笔/选区工具或未选中时整条分段与分组页隐藏，未选中只显示占位提示 `_noLayerHint`。
  - 默认段：形状/文本**新选中**（选中指纹 `Id:Kind` 排序串变化，`_lastSelectionProfile`）才落到「内容」页；原地编辑（改数值触发 ApplyToSelected→RefreshInspector，指纹不变）不跳页，避免在变换页调旋转/偏移时 Tab 乱跳。教程「换形状类型」的 `#EditorShapeType` 在内容页，新画形状后默认即落内容页 → 教程可正常点到。
