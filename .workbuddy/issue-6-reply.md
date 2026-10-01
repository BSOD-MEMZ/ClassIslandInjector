## Issue #6 回复草稿

（本回复由 AI 生成）

这次日志终于把话说清楚了：**不是「柱条不动」，是回环捕获一次都没起来**。

### 日志里的事实

- 整份日志（113080 行）里 `频谱捕获: 启动成功` **一次都没出现过**；
- 77 次启动尝试，每次都是同一个异常：

```
InvalidCastException: Unable to cast object of type
  'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'
to type 'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'.
```

- 所有诊断行都是 `samples=0 fftFrames=0 rawMax=0.000` —— 连一个样本都没收到，所以一根柱条都画不出来；
- `overlays=1 / 4` 说明频谱覆盖层**挂上了**（分体 4 行各一个），上一层修的「切纹理后宿主不重建」是有效的，这次的问题在更前面：捕获根本没启动。

### 根因：同一个进程里有两份 NAudio

注意异常里 **源类型和目标类型同名**。这种「同名转同名」只可能出现在：**同一个 COM 类在进程里有两份类型标识**。

NAudio 是靠 `new MMDeviceEnumeratorComObject()` 按 CLSID 激活 MMDeviceEnumerator 的，而一个 COM 对象在一个进程里只有一份类型标识，**谁先激活就归谁**。ClassIsland 的 `PluginLoadContext` 给每个插件独立的 AssemblyLoadContext，插件目录里的 NAudio 因此是各自独立的副本 —— 只要**另一个也带 NAudio 的插件先激活过它**，我们这边就必挂，而且此后每次重试都挂。

这也解释了你说的「SMTC 一切正常」：SMTC 和音频设备完全无关，排除不了这种情况。

回到你上次问的「是不是跟某个插件撞了」——是的。你的插件列表里 **Decibel_Monitor** 明确引用了 `NAudio 2.2.1` + `NAudio.Wasapi 2.2.1`，并且开了 `CopyLocalLockFileAssemblies`（依赖 dll 会打进它自己的插件目录），它读麦克风峰值，几乎必然比我们更早激活这个组件。同类已知问题：NAudio#421、Flow.Launcher#4258（两个插件各带一份 NAudio，后加载的直接起不来）。

我已在本地把这条链路完整复现（把同一份 `NAudio.Wasapi.dll` 再加载进一个独立 ALC 并抢先激活，再回到本 ALC 试一次）：

```
[冲突复现] 已复现：InvalidCastException: Unable to cast object of type
  'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'
to type 'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'.
```

### 怎么修（下一个 release）

不再依赖 NAudio 去取设备：新增一套**自带 COM 互操作的最小 WASAPI 回环捕获**。它声明的全是 `[ComImport]` **接口**，一律按 IID 走 `CoCreateInstance` / `QueryInterface`，不依赖 COM「类」的类型标识 —— 进程里有几份、哪个版本的 NAudio，都跟它无关。

启动顺序改成了「先 NAudio（诊断信息最全），失败自动回退到自带实现」，并把进程内所有 NAudio 副本的路径打进日志，冲突一眼可见：

```
频谱捕获: NAudio 回环启动失败（InvalidCastException: ...），改用自带互操作实现。
频谱捕获: 进程内 NAudio 副本 = NAudio.Wasapi 2.2.1.0 @ ...\data\Plugins\Decibel_Monitor\NAudio.Wasapi.dll | ...
频谱捕获: 启动成功（实现=自带互操作（默认输出设备="..." 未静音 主音量=..%））。设备采样率=48000 声道=2 位深=32 浮点=True
```

同一冲突场景下的实测：NAudio 侧被击穿，自带实现照常启动，电平非零且逐帧变化（探针 `tools/SpectrumProbe --conflict` 可复现）。

### 麻烦帮忙验证

1. 更新到下一个 release，直接播放音乐看柱条是否跳起来（**不需要卸载 Decibel_Monitor**）；
2. 如果还是不行，把新日志里的 `频谱捕获: 进程内 NAudio 副本 = ...` 那行贴回来 —— 它能直接确认是哪几个插件带的 NAudio 副本，我可以顺手给它们也提 issue。

### 顺带：这是整个插件生态的坑

同一个包被多个插件各自打包，谁先跑谁赢、后跑的插件直接坏掉，问题不在某一个插件身上。最彻底的解法是宿主把 NAudio 作为共享依赖（就像它现在对 WinRT 依赖（`WinRT.Runtime` / `Microsoft.Windows.SDK.NET`）做的那样，`PluginLoadContext` 会统一用宿主那一份）。我会去上游提这个建议；在那之前，本插件的频谱已经不受影响了。

感谢你的耐心和这份日志 —— 没有它这条线还得再猜几轮。
