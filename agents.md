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
| `VideoProject.cs` / `VideoProjectPlayer.cs` / `Views/VideoEditorWindow.cs`               | 视频工程（多片段拼接/变换）与 PR 风格视频编辑器（素材库/舞台/属性/时间轴）            |
| `Views/InjectorSettingsPage.cs`                                                                 | 设置页 UI（FluentAvalonia`SettingsExpander`/`InfoBar`/`ContentDialog`）             |
| `Views/IslandVisualEditor.cs`                                                                   | 可视化编辑器窗口 + 直接操作画布                                                           |
| `CountdownArrowOverlay.cs` / `IslandRippleOverlay.cs` / `SuppressingTopmostEffectPlayer.cs` | 覆盖层效果组件                                                                            |
| `Defaults/Overrides.axaml`                                                                      | 默认覆盖样式表（首次运行复制到配置目录，用户可热重载编辑）                                |
| `manifest.yml`                                                                                  | 插件清单                                                                                  |

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

- 视频解码只有 `FFmpegVideoDecoder`（FFmpeg.AutoGen **9.0.1**，惰性加载）；**WMF / Media Foundation 已整体删除**（曾因 vtable 手动调用 `SetCurrentMediaTypeByIndex` 触发原生访问违规击穿进程）。
- FFmpeg 原生库（`avcodec-63.dll` 等，版本由 `ffmpeg.LibraryVersionMap` 动态决定）部署在**配置目录\ffmpeg**（用户数据目录，deploy 不清空），通过 `ffmpeg.RootPath` 指向。
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
- **版本号必须从 `ffmpeg.LibraryVersionMap` 动态读取，勿硬编码**：9.0 是 avcodec-63/avformat-63/avutil-61/swresample-7/swscale-10；7.1 是 avcodec-61/.../swscale-8。升级 FFmpeg.AutoGen 时 `FFmpegVideoDecoder` 的 `ffmpeg.SWS_BILINEAR` 已改为 `(int)SwsFlags.SWS_BILINEAR`（9.x 起是枚举）。

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
