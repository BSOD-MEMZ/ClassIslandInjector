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
Copy-Item "bin\Release\net8.0-windows10.0.19041.0\*" "D:\Dev\ClassIsland\data\Plugins\miku.classisland.injector" -Recurse -Force
```

路径速查：

- 插件目录：`D:\Dev\ClassIsland\data\Plugins\miku.classisland.injector`
- 插件配置目录：`D:\Dev\ClassIsland\data\Config\Plugins\miku.classisland.injector`（`settings.json`、`Overrides.axaml`、诊断日志 `album-color.log`）

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

## 设置持久化

- `InjectorSettings` 用 System.Text.Json 序列化到 `settings.json`（全字段写入）。改动字段默认值时只影响「缺字段」的旧配置与全新安装；已有 JSON 会覆盖新默认。
- 设置变更经 `Changed` 事件 → `InjectorRuntime.SaveAndApply()` → 保存 + UI 线程 `Apply()` + 更新 SMTC watcher。
- 预设（`ApplyPreset`）不修改基础变形：`CaptureProtectedSettings()` / `RestoreProtectedSettings()` 保护 不透明度/缩放/位置/旋转/圆角/固定尺寸/底图/动态取色/轮询等设置。

## 代码风格约定

- C#，Nullable 启用，ImplicitUsings 启用。
- 文件内使用中文 XML 文档注释说明意图。
- 新设置属性必须同步更新：字段 → 属性 → `CopyFrom` → `ProtectedSettings`（如相关）→ 设置页 `LoadFromSettings`/`SaveAndApply`。
- 涉及 UI 线程访问必须通过 `Dispatcher.UIThread.Post`。
- 任何 WinRT 调用都要 try/catch 兜底，异常不能冒泡到宿主。
