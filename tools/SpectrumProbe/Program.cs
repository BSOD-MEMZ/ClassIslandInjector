using ClassIslandInjector;
using NAudio.Wave;

// 探针：验证 AudioSpectrumCapture 回环捕获在本机是否正常工作。
// 播放一个 440Hz 测试音到默认渲染设备，同时用回环捕获读取电平，检查是否有非零且变化的数据。

var capture = new AudioSpectrumCapture();
Console.WriteLine($"capture.Start() ...");
capture.Start();
Thread.Sleep(300);
Console.WriteLine($"IsRunning={capture.IsRunning}");

var outDevice = new WaveOutEvent();
outDevice.Init(new ToneProvider(440, 0.35));
outDevice.Play();
Console.WriteLine("已开始播放 440Hz 测试音");

var levels = new float[32];
var maxSeen = 0f;
var changed = false;
var prev = -1f;
for (var i = 0; i < 30; i++)
{
    Thread.Sleep(100);
    capture.GetLevels(levels);
    var sum = levels.Sum();
    if (sum > maxSeen)
    {
        maxSeen = sum;
    }

    if (Math.Abs(sum - prev) > 1e-4f)
    {
        changed = true;
    }

    prev = sum;
    Console.WriteLine($"t={i * 100,4}ms  sum={sum:F3}  maxLevel={levels.Max():F3}  first={levels[0]:F3}  last={levels[31]:F3}");
}

outDevice.Stop();
outDevice.Dispose();
capture.Stop();
capture.Dispose();

Console.WriteLine($"maxSeen={maxSeen:F3}  changed={changed}  running={capture.IsRunning}");
Console.WriteLine(maxSeen > 0.01f && changed ? "=== 捕获正常：电平非零且变化 ===" : "=== 捕获异常：电平为零或不变 ===");
return maxSeen > 0.01f && changed ? 0 : 1;

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
