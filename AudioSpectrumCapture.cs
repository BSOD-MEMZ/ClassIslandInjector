using System;
using NAudio.Wave;

namespace ClassIslandInjector;

/// <summary>
/// 系统声音输出（回环）频谱捕获：通过 WASAPI Loopback 抓取默认渲染设备正在播放的
/// 混合音频，做加窗 FFT 后按对数频段聚合为若干柱条电平，供「动态频谱」底纹绘制。
///
/// 捕获在 NAudio 的工作线程进行，UI 线程通过 <see cref="GetLevels"/> 读取电平；
/// 内部用锁保护共享电平数组，任何失败都会静默降级（不抛异常、不冒泡到宿主）。
/// </summary>
public sealed class AudioSpectrumCapture : IDisposable
{
    private const int FftSize = 1024;
    private const int DefaultBars = 32;
    private const int KeepOverlap = FftSize / 4;

    private readonly object _lock = new();
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _levels = new float[DefaultBars];
    private readonly float[] _smoothed = new float[DefaultBars];
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _ring = new float[FftSize];
    private int _ringWrite;
    private int _ringCount;
    private float _peak;
    private WasapiLoopbackCapture? _capture;
    private volatile bool _running;
    private bool _disposed;

    public AudioSpectrumCapture()
    {
        // Hann 窗，降低频谱泄漏。
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
        }
    }

    public int BarCount => DefaultBars;

    public bool IsRunning => _running;

    /// <summary>启动回环捕获；失败时静默降级（捕获不到时频谱保持静止，不影响其它功能）。</summary>
    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        try
        {
            var capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            _running = true;
            capture.StartRecording();
        }
        catch
        {
            try { _capture?.Dispose(); } catch { /* 忽略 */ }
            _capture = null;
            _running = false;
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        var capture = _capture;
        _capture = null;
        try { capture?.StopRecording(); } catch { /* 忽略 */ }
        try { capture?.Dispose(); } catch { /* 忽略 */ }
        lock (_lock)
        {
            Array.Clear(_levels);
            Array.Clear(_smoothed);
            _ringCount = 0;
            _ringWrite = 0;
            _peak = 0;
        }
    }

    /// <summary>读取当前柱条电平（0-1 归一化，已平滑），写入调用方提供的缓冲区。</summary>
    public float[] GetLevels(float[] destination)
    {
        lock (_lock)
        {
            var count = Math.Min(destination.Length, DefaultBars);
            Array.Copy(_smoothed, destination, count);
            return destination;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        var buffer = e.Buffer;
        var bytes = e.BytesRecorded;
        // WASAPI Loopback 为 IEEE float 32bit 交错双声道，每样本 4 字节。
        var samples = bytes / 4;
        for (var i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * 4);
            _ring[_ringWrite] = sample;
            _ringWrite = (_ringWrite + 1) % FftSize;
            if (_ringCount < FftSize)
            {
                _ringCount++;
            }
        }

        if (_ringCount >= FftSize)
        {
            ProcessFft();
            // 保留最近 1/4 样本做 75% 重叠，提升刷新率与平滑度。
            var keep = KeepOverlap;
            var srcStart = (_ringWrite - keep + FftSize) % FftSize;
            for (var i = 0; i < keep; i++)
            {
                _ring[i] = _ring[(srcStart + i) % FftSize];
            }

            _ringCount = keep;
            _ringWrite = keep;
        }
    }

    private void ProcessFft()
    {
        // 取环形缓冲里最新的 FftSize 个样本（已加窗）。
        for (var i = 0; i < FftSize; i++)
        {
            var idx = (_ringWrite + i) % FftSize;
            _fftReal[i] = _ring[idx] * _window[i];
            _fftImag[i] = 0;
        }

        Fft(_fftReal, _fftImag);

        var half = FftSize / 2;
        var magnitude = new float[half];
        var max = 0f;
        for (var i = 0; i < half; i++)
        {
            magnitude[i] = MathF.Sqrt(_fftReal[i] * _fftReal[i] + _fftImag[i] * _fftImag[i]);
            if (magnitude[i] > max)
            {
                max = magnitude[i];
            }
        }

        // 峰值自动增益（缓慢衰减），保持动态范围。
        _peak = Math.Max(_peak * 0.9f, max);
        var scale = _peak > 1e-6f ? 1f / _peak : 1f;

        // 对数频段聚合（约 20Hz - 20kHz）。
        var sampleRate = _capture?.WaveFormat?.SampleRate ?? 48000;
        var minFreq = 20f;
        var maxFreq = Math.Min(20000f, sampleRate / 2f);
        var minBin = Math.Max(1, (int)(minFreq / sampleRate * FftSize));
        var maxBin = Math.Min(half - 1, (int)(maxFreq / sampleRate * FftSize));
        var logMin = MathF.Log(minBin);
        var logMax = MathF.Log(maxBin);

        var newLevels = new float[DefaultBars];
        for (var b = 0; b < DefaultBars; b++)
        {
            var binLo = (int)MathF.Exp(logMin + (logMax - logMin) * b / DefaultBars);
            var binHi = (int)MathF.Exp(logMin + (logMax - logMin) * (b + 1) / DefaultBars);
            binLo = Math.Clamp(binLo, minBin, maxBin);
            binHi = Math.Clamp(binHi, minBin, maxBin);
            var sum = 0f;
            var count = 0;
            for (var k = binLo; k <= binHi; k++)
            {
                sum += magnitude[k];
                count++;
            }

            var avg = count > 0 ? sum / count : 0f;
            // 对数压缩，让低音量细节可见。
            var compressed = MathF.Pow(avg * scale, 0.6f);
            newLevels[b] = Math.Clamp(compressed * 4f, 0f, 1f);
        }

        lock (_lock)
        {
            // 时间平滑（上升快、下降慢，形成自然的「峰值保持」效果）。
            for (var i = 0; i < DefaultBars; i++)
            {
                var target = newLevels[i];
                _smoothed[i] = target > _smoothed[i]
                    ? _smoothed[i] + (target - _smoothed[i]) * 0.5f
                    : _smoothed[i] + (target - _smoothed[i]) * 0.12f;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _running = false;
    }

    /// <summary>迭代基 2 快速傅里叶变换（就地，长度必须为 2 的幂）。</summary>
    private static void Fft(float[] real, float[] imag)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = (float)Math.Cos(ang);
            var wIm = (float)Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var curRe = 1f;
                var curIm = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = real[i + k];
                    var uIm = imag[i + k];
                    var vRe = real[i + k + len / 2] * curRe - imag[i + k + len / 2] * curIm;
                    var vIm = real[i + k + len / 2] * curIm + imag[i + k + len / 2] * curRe;
                    real[i + k] = uRe + vRe;
                    imag[i + k] = uIm + vIm;
                    real[i + k + len / 2] = uRe - vRe;
                    imag[i + k + len / 2] = uIm - vIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
