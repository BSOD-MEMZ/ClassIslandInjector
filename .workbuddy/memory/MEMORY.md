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
- **不要抢控件的空格键**：编辑器快捷键挂窗口级 `KeyDown`（冒泡），焦点在按钮/开关/下拉上时由控件先吃掉空格（Button 触发 Click、ToggleSwitch 切换）——这是 Avalonia 默认行为，用户明确要求保留。曾改成隧道（Tunnel）阶段强制「空格永远只切预览播放」，被要求回退（7d8c90c）。
- **FFmpeg native 上下文（AVCodecContext/SwsContext/AVFormatContext）的释放规则**：①先停线程并 `Join`；②释放要与「可能正在用它做 native 调用的入口」互斥。`FFmpegVideoDecoder` 用 `_gate` 把 `ReadFrame`/`SeekTo`/`Restart` 与 `Dispose` 串起来；持有者（`VideoProjectPlayer`）必须先 Join 再释放各轨 Source。违反任一条 → `sws_scale`/`ReadFrame` 读已释放内存 → `AccessViolationException`(0xc0000005) **静默击穿宿主进程**（无托管异常、无 crash.log，只有事件日志里 `coreclr.dll` + `Application Error`）。回归探针：`tools\RaceProbe`。
- **排查「静默崩溃」用**：`wevtutil qe Application /c:15 /rd:true /f:text /q:"*[System[Provider[@Name='.NET Runtime']]]"`（给出异常类型 + 完整托管栈，比 Application Error 的单行有用得多）。
- **FFmpeg 解码输出尺寸必须向上对齐到 16**（H.264 宏块；`FFmpegVideoDecoder.AlignUp16`）。非 16 倍数的尺寸（如 412×68）会让 `sws_scale` 越界写坏托管堆，随后以 `coreclr.dll` + `0xc0000005` **静默击穿进程**，无任何托管异常可抓。详见 `agents.md` 第 9 节。

## FFmpeg mp4 封装（音频/双流）

- **`pkt->stream_index` 必须自己写**：`avcodec_receive_packet` **不设置**这个字段（默认 0）。纯视频时视频恰好是 0 号流所以看不出问题，一旦加音轨（渲染带音频）音频包会被塞进 0 号流 → 复用器刷 `non monotonically increasing dts` 并把视频轨写坏（产物 1~2KB、`unspecified pixel format`、读它还会在 libswscale 里断言崩掉）。见 `agents.md` 约束 10。
- 双流时**两路时间基分开记**（视频写头后是 1/10240 之类、音频 1/48000），`Finish()` 两路都要 flush 再 `av_write_trailer`；音频时间轴用**累计样本号** `round(t × 48000)` 推进，别按帧长浮点累加（非整数帧率会漂）。
- 主界面底图音频链路：产物带音轨 → `VideoAudioPlayer`（「播放声音」开关）出声。**循环同步**靠 `VideoFrameSource.OnLoopRestart`（画面复位时音频 `Seek(0)`）；「播放声音」开启时底图按**源帧率**播放（否则视频循环周期 ≠ 音频时长，一个循环里音频会重复若干遍）。回归探针：`tools\RaceProbe --audio`。


## 预览播放器（卡顿 / 音画不同步，四铁律）

用户反复报「预览时画面突然卡住只剩声音 / 音频断续 / 音画越来越不同步」。压力探针 `RaceProbe --stress` 测出并已修（详见 `agents.md` 约束 11）：

1. **`PumpTrack` 必须先 `Consumed.Reset()` 再调 `_onFrame`**。反过来写会丢消费信号（UI 可能在回调返回前就 Set），此后该轨 `!Consumed.IsSet` 恒成立 = **永久不再投递**（画面卡死）。日志特征：`显N 丢N seekN[等UI消化]` 长期完全冻结。
2. **追赶必须有上限**：`CatchUpSeekFrames = 8`（落后 0.33s 直接 seek；seek+解一帧 57ms ≪ 硬解 32 帧 371ms）+ `MaxSkipPerTick = 4`。否则「解码丢弃」滚成正反馈，投递率崩到 1fps。
3. **`Consumed.Wait` 只等 5ms**：pump 逐轨串行，等久了 3 轨会把一拍拖到 450ms。
4. **`AudibleTime` 不能拿声卡累计位置当绝对时间**：`WasapiOut.GetPosition()` 自流启动起算，而 `Seek` 只改 `_baseTime` → 每次 seek 后凭空多出「已播时长」（实测 +3.01s）→ 漂移校正反复重定位（声音断续 + 锁阻塞视频线程）。按锚点算，起播/Seek/Resume 重锚；容差 0.30s + 10s 冷却。

5. **解码成本 = 源帧率 × 分辨率，与显示尺寸无关**（320px 与 1280px 只差 2ms）。真正的杠杆是**素材帧率**：手机视频 54~60fps 而显示只要 24fps，白解近两倍。`VideoTranscoder` 输出**上限 30fps**（按时间抽帧，产物为 CFR）；实测用户那段 54fps 素材转成 800×368/30fps 后，解一帧 7.4ms → **1.87ms**，单/双/三轨全部 **24fps**。
6. **每轨一条解码线程**（`TrackWorker`，主循环只打拍、**异步派发不等收工**——同步等会把单轨从 23fps 拖到 16.5fps）。解码器的开关/seek/释放**只在本轨线程**，跨线程只用 Signal/Done/RestartPending 握手；`Dispose` 不代释放（各轨线程自释放），否则回到「跨线程释放 native → 静默崩溃」的老坑。
7. **CFR vs VFR**：`targetN = mediaTime × 源帧率` 只在固定帧率下成立；VFR 素材会让「落后多少帧」失真 → 频繁 seek。测试用 `--makecfr` 合成 CFR 素材区分「引擎问题」与「素材问题」。

诊断入口：`video-player.log` 每 2 秒一行（拍 均/峰 + 每轨 源fps/显/跳/seek/拍峰[闲置原因]）。注意「跳」是源帧率高于显示帧率时的**正常**跳帧，不是故障。
## 调试与排查（沙箱环境）

- **判宿主存活不能看进程**：`ClassIsland.exe` 只是启动器，真身是它拉起的 `ClassIsland.Desktop.exe`。可靠判据是宿主日志里 `MemoryWatchDogService` 的 **60 秒心跳条数**。
- **沙箱里起的宿主窗口在另一个桌面会话**：截屏截不到、`EnumWindows` 枚举不到 → 视觉验证只能交给用户，别在自动化截图上耗轮次。
- `Get-WinEvent` 在沙箱被拒，但 **`wevtutil qe Application /c:5 /rd:true /f:text /q:"*[System[Provider[@Name='Application Error']]]"` 可用**。
- 对照实验一次只动一个变量；怀疑某次改动引入回归时用 `git worktree` 构建旧版做 A/B（**不要** `git checkout`/`git stash` 当前工作区）。
- 工具的构建产物在 `tools\` 下（SmtcProbe / SpectrumProbe / FFmpegProbe / ScheduleProbe / AudioProbe），不参与主项目编译；`AudioProbe --project` 校验工程格式、`--vdec` 解视频、`--play` 测音频设备。
