# ClassIslandInjector — 项目长期约定

## 仓库与工作目录

- **`.workbuddy/` 是故意提交进 git 的，不要动它。** 用户在两台电脑上开发，靠提交 `.workbuddy/`（含 `memory/`、`issue-*-reply.md` 等）同步 AI 记忆与工作记录。禁止建议加回 `.gitignore`、禁止从索引移除、禁止把它当"误提交"来提醒。（2026-09-27 明确）

## 构建与部署

- **提交时注意文件名大小写**：仓库索引里是 `agents.md`（小写），在 Windows 上执行 `git add AGENTS.md` 会**静默不匹配**（不报错也不暂存，改动会漏提交）。用 `git add agents.md` 或 `git add -A`。

- 构建：`dotnet build ClassIslandInjector.csproj -c Release -p:CreateCipx=false`
- 部署前必须关闭宿主：`Stop-Process -Name "ClassIsland*" -Force`，再复制 `bin\Release\net8.0-windows10.0.19041.0\*` 到 `D:\Dev\ClassIsland\data\Plugins\classisland.injector`
- 配置目录：`D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector`（`settings.json`、`Overrides.axaml`、`preview-debug.log`、`album-color.log`）
- 含中文注释的脚本（deploy.ps1 等 UTF-8 无 BOM）必须用 `pwsh` 执行（Windows PowerShell 5.1 会解析失败）。若宿主没在跑，等价做法是 bash 里直接 `rm -rf $PLUG/* && cp -r $OUT/* $PLUG/`
- 用户日志是排查 UI 问题的一等证据：`data\Config\Plugins\classisland.injector\video-editor.log`（含 `PRESS-ROOT`/`DRAG 按下/移动/释放`/落轨行，`EditorLog` 写），工程实况看 `video-project.ciproj`。**同一坐标「按得中片段」但 `PRESS-ROOT hasClip=False`，就说明模型几何 ≠ 绘制几何。**

## 时间轴（VideoEditorWindow）行号/几何

- **行号与泳道顶部坐标的唯一权威**：`LaneOrder()` / `LaneAtRow(row)` / `RowOfLane(lane)` / `LaneVisualTop(lane)` —— 视频轨在上（轨号大的在上）、音频轨在下（A1 最底）。任何按 Y 判定（落点、命中、框选、标签、自动滚动）都必须走它；**禁止**再用 `TrackCount/TotalLaneCount` 反推行号（曾因此让音频泳道排到视频泳道上方，Y 判定整体少算一个泳道高 → 拖拽差 60px、落点错一轨）。
- 片段所在泳道一律用 `LaneOfClip(clip)`：音频片段 `Track` 恒为 -1，历史数据里还可能有被拖拽写成视频轨号的脏值。
- 拖拽跟手三铁律：①抓取偏移用 `TranslatePoint` 量真实块位置；②`SnapTime` 排除被拖片段自身（否则吸回自己）；③边缘自动滚动的跟随用**实际**滚动量重算内容坐标。

## 工程音频（"没声音"先查这三样）

- 片段出声条件 = `!Muted && Volume > 0.0001`（`VideoProject.ContributesAudio` / `AudioClipsAt`）；**音量 0% 的片段被整条排除**，混音器连源都不建 —— 这是"没声音"的第一大原因，不是声卡。其次是音频轨被静音（轨道头喇叭按钮）/ 视频片段被"分离音频"或静音开关置了 `Muted`。
- 排查顺序：`video-project.ciproj` 里看 `Volume`/`Muted` → `video-audio.log` 有没有「音频源进入：xxx」→ 播放器日志 `video-player.log` 的 `t=` 是否落在该片段区间内 → 最后才怀疑系统/单应用音量。
- 增益口径：`VideoClip.AudioGainAt(local)` = 音量 × 淡入 × 淡出（0..2 钳制），再乘 `ProjectAudioMixer.TrackGainOf`（音频轨静音/音量；视频片段原声不叠轨增益）。时间轴波形、混音、检查器提示都走同一套，改口径要一起改。

## 设计约束

- 全新安装必须零改动：默认值中性（`Shape=HostDefault`、`RippleType=None`、`CountdownArrowsEnabled=false` 等）
- 目标框架锁定 `net8.0-windows10.0.19041.0`（必须与宿主的 WinRT SDK 对齐）
- Avalonia 派生控件必须覆写 `StyleKeyOverride`
- 判断 SMTC 焦点会话必须用 `SourceAppUserModelId` 字符串比较，不能用 `ReferenceEquals`
- **FFmpeg 解码输出尺寸必须向上对齐到 16**（H.264 宏块；`FFmpegVideoDecoder.AlignUp16`）。非 16 倍数的尺寸（如 412×68）会让 `sws_scale` 越界写坏托管堆，随后以 `coreclr.dll` + `0xc0000005` **静默击穿进程**，无任何托管异常可抓。详见 `agents.md` 第 9 节。

## 调试与排查（沙箱环境）

- **判宿主存活不能看进程**：`ClassIsland.exe` 只是启动器，真身是它拉起的 `ClassIsland.Desktop.exe`。可靠判据是宿主日志里 `MemoryWatchDogService` 的 **60 秒心跳条数**。
- **沙箱里起的宿主窗口在另一个桌面会话**：截屏截不到、`EnumWindows` 枚举不到 → 视觉验证只能交给用户，别在自动化截图上耗轮次。
- `Get-WinEvent` 在沙箱被拒，但 **`wevtutil qe Application /c:5 /rd:true /f:text /q:"*[System[Provider[@Name='Application Error']]]"` 可用**。
- 对照实验一次只动一个变量；怀疑某次改动引入回归时用 `git worktree` 构建旧版做 A/B（**不要** `git checkout`/`git stash` 当前工作区）。
- 工具的构建产物在 `tools\` 下（SmtcProbe / SpectrumProbe / FFmpegProbe / ScheduleProbe / AudioProbe），不参与主项目编译；`AudioProbe --project` 校验工程格式、`--vdec` 解视频、`--play` 测音频设备。
