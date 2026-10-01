using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ClassIslandInjector;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// 探针：验证 AudioSpectrumCapture 回环捕获在本机是否正常工作。
// 1) 默认模式：播放 440Hz 测试音到默认渲染设备，用回环捕获读取电平，检查是否有非零且变化的数据。
// 2) --conflict：先复现 Issue #6 的进程内 NAudio 冲突（同进程两份 NAudio → 后激活的一方抛
//    InvalidCastException: 'MMDeviceEnumeratorComObject' 转 'MMDeviceEnumeratorComObject'），
//    再确认自带互操作的捕获实现不受影响、频谱依然能工作。

var conflict = args.Contains("--conflict");
var conflictPoisoned = false;
if (conflict)
{
    var result = ReproduceNaudioConflict();
    conflictPoisoned = result.poisoned;
    Console.WriteLine($"[冲突复现] {result.detail}");
}

var capture = new AudioSpectrumCapture();
Console.WriteLine("capture.Start() ...");
capture.Start();
Thread.Sleep(300);
Console.WriteLine($"IsRunning={capture.IsRunning}");
Console.WriteLine($"SampleRate={capture.SampleRate}  Channels={capture.Channels}");
Console.WriteLine($"实现={capture.CaptureImplementation}");
if (capture.DiagnosticMessage is { Length: > 0 } diag)
{
    Console.WriteLine($"DiagnosticMessage={diag}");
}

var outDevice = new WaveOutEvent();
outDevice.Init(new ToneProvider(440, 0.35));
outDevice.Play();
Console.WriteLine("已开始播放 440Hz 测试音");

var levels = new float[32];
var raw = new float[32];
var maxSeen = 0f;
var maxRaw = 0f;
var changed = false;
var prev = -1f;
for (var i = 0; i < 30; i++)
{
    Thread.Sleep(100);
    capture.GetLevels(levels);
    capture.GetRawLevels(raw);
    var sum = levels.Sum();
    var rawSum = raw.Sum();
    if (sum > maxSeen)
    {
        maxSeen = sum;
    }

    if (rawSum > maxRaw)
    {
        maxRaw = rawSum;
    }

    if (Math.Abs(sum - prev) > 1e-4f)
    {
        changed = true;
    }

    prev = sum;
    Console.WriteLine(
        $"t={i * 100,4}ms  sum={sum:F3}  rawSum={rawSum:F3}  maxLevel={levels.Max():F3}  " +
        $"first={levels[0]:F3}  last={levels[31]:F3}  samples={capture.SampleCount}  fftFrames={capture.BlockCount}");
}

outDevice.Stop();
outDevice.Dispose();
capture.Stop();
capture.Dispose();

Console.WriteLine($"maxSeen={maxSeen:F3}  maxRaw={maxRaw:F3}  changed={changed}  running={capture.IsRunning}");
// 判定放宽到 raw：平滑值会因为首帧尚未建立而偏低，raw 更能反映「确实取到了能量」。
var ok = maxRaw > 0.005f && changed;
Console.WriteLine(ok ? "=== 捕获正常：电平非零且变化 ===" : "=== 捕获异常：电平为零或不变 ===");
if (conflict)
{
    Console.WriteLine($"[冲突复现] NAudio 侧被击穿={conflictPoisoned}；最终捕获实现={capture.CaptureImplementation}");
    Console.WriteLine(ok
        ? "=== 冲突场景下自带互操作仍可用：Issue #6 的修复生效 ==="
        : "=== 冲突场景下捕获仍失败：修复未生效 ===");
}

return ok ? 0 : 1;

/// <summary>
/// 复现 Issue #6 的进程内 NAudio 冲突：ClassIsland 给每个插件独立 ALC，插件目录里的
/// NAudio 因此是各自独立的副本；MMDeviceEnumerator 这个 COM 类在同一进程里只有一份
/// 「类型标识」，先激活的一方占住它，后激活的一方就会抛
/// InvalidCastException: Unable to cast object of type 'MMDeviceEnumeratorComObject'
/// to type 'MMDeviceEnumeratorComObject'（源/目标类型同名）。
/// <para>这里把同一份 NAudio.Wasapi.dll 再加载进一个独立 ALC 并抢先激活，再回到本 ALC 试一次。</para>
/// </summary>
(bool poisoned, string detail) ReproduceNaudioConflict()
{
    var wasapiPath = typeof(MMDeviceEnumerator).Assembly.Location;
    var corePath = typeof(WaveFormat).Assembly.Location;
    try
    {
        var other = new AssemblyLoadContext("SpectrumProbe.OtherPlugin", isCollectible: false);
        other.Resolving += (_, name) => name.Name is { } simple &&
                                        simple.Equals("NAudio.Core", StringComparison.OrdinalIgnoreCase)
            ? other.LoadFromAssemblyPath(corePath)
            : null;
        var otherWasapi = other.LoadFromAssemblyPath(wasapiPath);
        var comType = otherWasapi.GetType("NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject")
                      ?? throw new InvalidOperationException("未找到 MMDeviceEnumeratorComObject 类型。");
        var instance = InstantiateComObject(comType);
        Console.WriteLine($"[冲突复现] 已用独立 ALC 的副本激活 MMDeviceEnumerator：{instance?.GetType().FullName}");

        // 现在回到本 ALC 的 NAudio 试一次 —— 这正是用户在 ClassIsland 里踩到的那一步。
        using var enumerator = new MMDeviceEnumerator();
        _ = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (false, "本 ALC 的 NAudio 仍能激活 —— 本次未复现冲突（本机 COM 标识没被抢占）");
    }
    catch (Exception ex) when (ex is InvalidCastException or MissingMethodException)
    {
        return (true, $"已复现：{ex.GetType().Name}: {ex.Message}");
    }
    catch (Exception ex)
    {
        return (false, $"复现过程异常（非 InvalidCastException）：{ex.GetType().Name}: {ex.Message}");
    }
}

/// <summary>按 COM 类激活一个 [ComImport] 类型（Activator 不可用时退回构造函数反射调用）。</summary>
object? InstantiateComObject(Type comType)
{
    try
    {
        return Activator.CreateInstance(comType);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[冲突复现] Activator 不可用（{ex.GetType().Name}），改用构造函数调用");
        var ctor = comType.GetConstructor(Type.EmptyTypes)
                   ?? throw new InvalidOperationException("ComImport 类型没有无参构造函数。");
        return ctor.Invoke(null);
    }
}

/// <summary>极简正弦波采样提供器（NAudio 2.x 已移除 SignalGenerator）。</summary>
sealed class ToneProvider : ISampleProvider
{
    private readonly double _freq;
    private readonly double _gain;
    private double _phase;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public ToneProvider(double freq, double gain)
    {
        _freq = freq;
        _gain = gain;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            buffer[offset + i] = (float)(Math.Sin(_phase) * _gain);
            _phase += 2 * Math.PI * _freq / WaveFormat.SampleRate;
        }

        return count;
    }
}
