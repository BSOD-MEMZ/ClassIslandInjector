using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClassIslandInjector;

/// <summary>
/// 系统声音输出（回环）频谱捕获：通过 WASAPI Loopback 抓取默认渲染设备正在播放的
/// 混合音频，做加窗 FFT 后按对数频段聚合为若干柱条电平，供「动态频谱」底纹绘制。
///
/// 捕获在 NAudio 的工作线程进行，UI 线程通过 <see cref="GetLevels"/> 读取电平；
/// 内部用锁保护共享电平数组。任何失败都不抛异常、不冒泡到宿主，但会写入诊断日志
/// （preview-debug.log）并记录 <see cref="DiagnosticMessage"/>，便于排查「选了没反应」。
/// </summary>
public sealed class AudioSpectrumCapture : IDisposable
{
    private const int FftSize = 1024;
    private const int DefaultBars = 32;
    private const int KeepOverlap = FftSize / 4;
    /// <summary>每次 FFT 至少积累的新样本数，避免小缓冲下反复空转。</summary>
    private const int MinFreshSamples = FftSize - KeepOverlap;

    private readonly object _lock = new();
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _levels = new float[DefaultBars];
    private readonly float[] _smoothed = new float[DefaultBars];
    private readonly float[] _raw = new float[DefaultBars];
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _windowed = new float[FftSize];
    private int _windowWrite;
    private int _windowCount;
    private int _windowTotal;
    private int _lastFftPos;
    private float _peak;
    private ILoopbackCapture? _capture;
    private volatile bool _running;
    private bool _disposed;
    private long _sampleCount;
    private long _blockCount;
    private long _lastDiagnosticSampleCount = -1;
    private DateTime _lastDiagnosticAt = DateTime.MinValue;
    private int _dataCallbackLogged;
    /// <summary>是否已记录过「首次得到有效电平」（诊断用，一次性）。</summary>
    private int _firstSignalLogged;

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

    /// <summary>
    /// 最近一次失败/状态诊断信息（成功时为 null）。注入器读取后写入日志，
    /// 用于回答用户「为什么频谱不动」——不再静默失败。
    /// </summary>
    public string? DiagnosticMessage { get; private set; }

    /// <summary>已捕获的样本总数（诊断用：为 0 说明回环根本没送来数据）。</summary>
    public long SampleCount => Interlocked.Read(ref _sampleCount);

    /// <summary>已完成 FFT 的帧数（诊断用：为 0 说明帧长不够 / 没数据）。</summary>
    public long BlockCount => Interlocked.Read(ref _blockCount);

    /// <summary>捕获设备的采样率（诊断用）。</summary>
    public int SampleRate => _capture?.Format.SampleRate ?? 0;

    /// <summary>捕获设备的声道数（诊断用）。</summary>
    public int Channels => _capture?.Format.Channels ?? 0;

    /// <summary>当前生效的捕获实现（"NAudio" / "自带互操作"），诊断用。</summary>
    public string CaptureImplementation { get; private set; } = "未启动";

    /// <summary>
    /// 启动回环捕获。失败不抛异常，但记录诊断信息并写日志。
    /// <para>
    /// 先走 NAudio（诊断信息最全）；如果它挂了（典型是「同进程另一份 NAudio 抢占了
    /// MMDeviceEnumerator 的 COM 类型标识」导致 InvalidCastException，见
    /// <see cref="WasapiLoopbackCaptureLite"/> 的说明），自动回退到自带 COM 互操作实现。
    /// 两条路都失败才算真失败。
    /// </para>
    /// </summary>
    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        ILoopbackCapture? capture = null;
        Exception? naudioError = null;
        try
        {
            capture = new NaudioLoopbackCapture();
            Attach(capture);
        }
        catch (Exception ex)
        {
            naudioError = ex;
            Detach(capture);
        }

        if (_capture == null)
        {
            // 关键落点之一：NAudio 侧的 new WasapiLoopbackCapture() 抛
            // InvalidCastException（源/目标类型同名 = 进程内有两份 NAudio）。
            Log($"频谱捕获: NAudio 回环启动失败（{naudioError?.GetType().Name}: {naudioError?.Message}），改用自带互操作实现。");
            Log($"频谱捕获: 进程内 NAudio 副本 = {NaudioConflictDiagnostics.DescribeLoadedCopies()}");
            try
            {
                var lite = new WasapiLoopbackCaptureLite();
                Attach(lite);
            }
            catch (Exception ex2)
            {
                DiagnosticMessage =
                    $"回环捕获启动失败：{naudioError?.GetType().Name}: {naudioError?.Message}；" +
                    $"自带互操作实现同样失败：{ex2.GetType().Name}: {ex2.Message}";
                Log($"频谱捕获: {DiagnosticMessage}");
                CaptureImplementation = "启动失败";
                return;
            }
        }

        DiagnosticMessage = null;
        var format = _capture!.Format;
        Log(
            $"频谱捕获: 启动成功（实现={CaptureImplementation}）。设备采样率={format.SampleRate} " +
            $"声道={format.Channels} 位深={format.BitsPerSample} 浮点={format.IsFloat}");
        // 回环抓的是「默认渲染端点」，不是「正在出声的设备」。把该端点的名字 /
        // 静音 / 音量写进日志，能一眼区分「设备选错（样本全 0）」和「代码问题」。
        Log($"频谱捕获: {DescribeDefaultRenderDevice()}");
    }

    /// <summary>挂上捕获源（订阅事件 → 置位 → 真正 Start），失败时内部已清理。</summary>
    private void Attach(ILoopbackCapture capture)
    {
        _capture = capture;
        capture.DataAvailable += OnDataAvailable;
        capture.Stopped += OnCaptureStopped;
        // 先置位再 Start：捕获线程可能立刻回调，此时 OnDataAvailable 需要 _running 为真。
        _running = true;
        try
        {
            capture.Start();
        }
        catch
        {
            Detach(capture);
            throw;
        }

        CaptureImplementation = capture.Describe;
    }

    /// <summary>摘掉捕获源并复位状态（正常停止与启动失败共用）。</summary>
    private void Detach(ILoopbackCapture? capture)
    {
        _running = false;
        if (capture != null)
        {
            try { capture.Stop(); } catch { /* 忽略 */ }
            capture.DataAvailable -= OnDataAvailable;
            capture.Stopped -= OnCaptureStopped;
            try { capture.Dispose(); } catch { /* 忽略 */ }
        }

        if (ReferenceEquals(_capture, capture))
        {
            _capture = null;
        }
    }

    /// <summary>
    /// 诊断日志落地回调（由注入器在构造后注入）。默认走 <see cref="DiagnosticLog"/>；
    /// 独立探针项目（tools\SpectrumProbe）不编译注入器，可注入自己的实现或留空。
    /// 用委托而不是直接调用 DiagnosticLog，是为了让本文件不依赖宿主侧类型，便于单测。
    /// </summary>
    internal static Action<string>? DiagnosticSink { get; set; }

    /// <summary>配置目录提供者（默认走 InjectorRuntime；探针可覆盖）。</summary>
    internal static Func<string?>? ConfigDirectoryProvider { get; set; }

    private static void Log(string message)
    {
        // 优先走注入的落地实现（探针 / 单测用）。
        if (DiagnosticSink is { } sink)
        {
            try { sink(message); } catch { /* 日志失败不影响功能 */ }
            return;
        }

        // 插件运行时无注入，走内置门面。
        try
        {
            var path = DiagnosticPath;
            if (!string.IsNullOrEmpty(path))
            {
                DiagnosticLog.Write(path, message);
            }
        }
        catch
        {
            // 日志失败不影响捕获。
        }
    }

    /// <summary>日志文件路径（诊断信息用；为空表示尚未确定配置目录）。</summary>
    public static string? DiagnosticPath
    {
        get
        {
            try
            {
                var dir = ConfigDirectoryProvider?.Invoke() ?? InjectorRuntime.ConfigDirectory;
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "preview-debug.log");
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 读取默认渲染端点（回环捕获目标）的友好名 / 静音状态 / 主音量。
    /// 这三项是「样本一直有、电平一直是 0」的直接原因：实际出声设备与默认输出设备
    /// 不是同一个（USB 耳机 / 蓝牙 / HDMI / Win11 应用级设备路由）时，回环抓到的
    /// 就是纯静音。写进日志可一眼分辨环境问题与代码问题。
    /// <para>
    /// 先走 NAudio（字段最全）；NAudio 侧因同进程副本冲突不可用时，退回自带互操作实现，
    /// 保证这条诊断在冲突机器上依然有内容，而不是只留一句「读取失败」。
    /// </para>
    /// </summary>
    public static string DescribeDefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var name = device.FriendlyName;
            var volume = device.AudioEndpointVolume;
            var muted = volume?.Mute == true ? "已静音" : "未静音";
            var level = volume?.MasterVolumeLevelScalar ?? 0f;
            return $"默认输出设备=\"{name}\" {muted} 主音量={level * 100:F0}%";
        }
        catch
        {
            // 例如 InvalidCastException（进程内两份 NAudio 抢 COM 类型标识）→ 走自带实现。
            return WasapiEndpointInfo.DescribeDefaultRenderDevice();
        }
    }

    public void Stop()
    {
        if (!_running && _capture == null)
        {
            return;
        }

        Detach(_capture);
        lock (_lock)
        {
            Array.Clear(_levels);
            Array.Clear(_smoothed);
            Array.Clear(_raw);
            _windowCount = 0;
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

    /// <summary>
    /// 读取未平滑的瞬时电平（诊断用）。平滑数组在静音时会缓慢衰减，
    /// 用它判断「是否真的一点声音都没收到」比看平滑值更准确。
    /// </summary>
    public float[] GetRawLevels(float[] destination)
    {
        lock (_lock)
        {
            var count = Math.Min(destination.Length, DefaultBars);
            Array.Copy(_raw, destination, count);
            return destination;
        }
    }

    private void OnDataAvailable(object? sender, LoopbackDataEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        var buffer = e.Buffer;
        var bytes = e.BytesRecorded;
        if (bytes <= 0)
        {
            return;
        }

        // WASAPI (WAVE_FORMAT_EXTENSIBLE) 的位深可能是 32/24/16，不能写死 float32。
        var format = _capture?.Format ?? new LoopbackFormat(48000, 2, 32, true);
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameBytes = bytesPerSample * channels;
        if (frameBytes <= 0)
        {
            return;
        }

        var frames = bytes / frameBytes;
        if (frames <= 0)
        {
            return;
        }

        // 首次收到数据时留一条日志：能确认回环真的在送数据。
        if (Interlocked.CompareExchange(ref _dataCallbackLogged, 1, 0) == 0)
        {
            Log(
                $"频谱捕获: 收到首批音频数据。bytes={bytes} 采样率={format.SampleRate} " +
                $"声道={channels} 位深={format.BitsPerSample} 浮点={format.IsFloat}");
        }

        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameBytes;
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += ReadSample(buffer, offset + c * bytesPerSample, format);
            }

            // 多声道下混为单声道，避免只取左声道导致部分素材「看起来没反应」。
            PushSample(sum / channels);
        }

        Interlocked.Add(ref _sampleCount, frames);
    }

    /// <summary>按当前位深/编码把单个样本解码为 -1..1 的浮点。</summary>
    private static float ReadSample(byte[] buffer, int offset, LoopbackFormat format)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return 0f;
        }

        var bitsPerSample = format.BitsPerSample;

        // 浮点编码（WASAPI 混音格式固定为 IEEE float）。
        if (format.IsFloat)
        {
            if (offset + 4 > buffer.Length)
            {
                return 0f;
            }

            return BitConverter.ToSingle(buffer, offset);
        }

        switch (bitsPerSample)
        {
            case 16:
                if (offset + 2 > buffer.Length) return 0f;
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            case 24:
                if (offset + 3 > buffer.Length) return 0f;
                var v24 = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                // 24 位有符号扩展。
                if ((v24 & 0x800000) != 0)
                {
                    v24 |= unchecked((int)0xFF000000);
                }

                return v24 / 8388608f;
            case 32:
                if (offset + 4 > buffer.Length) return 0f;
                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
            default:
                return 0f;
        }
    }

    /// <summary>把一个样本推入滑动窗口，窗口满则做一次 FFT。</summary>
    private void PushSample(float sample)
    {
        _windowed[_windowWrite] = sample;
        _windowWrite = (_windowWrite + 1) % FftSize;
        if (_windowCount < FftSize)
        {
            _windowCount++;
            return;
        }

        // _windowTotal 是单调递增的总样本数（不受窗口容量截断），用它判断「又积累了
        // MinFreshSamples 个新样本」。早期版本用 _windowCount 判断，而它在达到 FftSize
        // 后就不再增长，条件永远不成立 → 只做一次 FFT，之后频谱完全静止（Issue #6 根因）。
        _windowTotal++;
        if (_windowTotal - _lastFftPos >= MinFreshSamples)
        {
            _lastFftPos = _windowTotal;
            ProcessFft();
        }
    }

    /// <summary>
    /// 对滑动窗口内最新的 <see cref="FftSize"/> 个样本做加窗 FFT。
    /// <para>
    /// 旧实现用「环形缓冲 + 读完后就地重排」的方式取窗，重排时会把尚未写入的区域
    /// （首轮全为 0）当作最旧样本，且定位索引错位一轮，导致窗内数据半数是静音 ——
    /// 表现为频谱要么完全不跳、要么幅度极低（Issue #6）。
    /// 这里改为单调递增计数 + 取模读取，索引关系严格可验证：最旧样本到最新样本。
    /// </para>
    /// </summary>
    private void ProcessFft()
    {
        var oldest = (_windowWrite - FftSize + FftSize * 2) % FftSize;
        for (var i = 0; i < FftSize; i++)
        {
            var idx = (oldest + i) % FftSize;
            _fftReal[i] = _windowed[idx] * _window[i];
            _fftImag[i] = 0;
        }

        Fft(_fftReal, _fftImag);

        var half = FftSize / 2;
        var max = 0f;
        for (var i = 0; i < half; i++)
        {
            _fftImag[i] = MathF.Sqrt(_fftReal[i] * _fftReal[i] + _fftImag[i] * _fftImag[i]);
            if (_fftImag[i] > max)
            {
                max = _fftImag[i];
            }
        }

        // 峰值自动增益：快速跟涨、极慢回落。若静音时不衰减，一次大音量后
        // 峰值会永久偏高，后续小声永远被压平（表现为「只有开头会跳」）。
        var targetPeak = Math.Max(max, 1e-4f);
        _peak = targetPeak > _peak ? targetPeak : _peak * 0.995f + targetPeak * 0.005f;
        var scale = _peak > 1e-6f ? 1f / _peak : 1f;

        // 对数频段聚合（约 20Hz - 20kHz）。
        var sampleRate = _capture?.Format.SampleRate ?? 48000;
        var minFreq = 20f;
        var maxFreq = Math.Min(20000f, sampleRate / 2f);
        var minBin = Math.Max(1, (int)(minFreq / sampleRate * FftSize));
        var maxBin = Math.Min(half - 1, (int)(maxFreq / sampleRate * FftSize));
        if (maxBin <= minBin)
        {
            minBin = 1;
            maxBin = half - 1;
        }

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
                sum += _fftImag[k];
                count++;
            }

            var avg = count > 0 ? sum / count : 0f;
            // 对数压缩，让低音量细节可见。
            var compressed = MathF.Pow(Math.Clamp(avg * scale, 0f, 1f), 0.6f);
            newLevels[b] = Math.Clamp(compressed * 4f, 0f, 1f);
        }

        lock (_lock)
        {
            for (var i = 0; i < DefaultBars; i++)
            {
                _raw[i] = newLevels[i];
                // 时间平滑（上升快、下降慢，形成自然的「峰值保持」效果）。
                var target = newLevels[i];
                _smoothed[i] = target > _smoothed[i]
                    ? _smoothed[i] + (target - _smoothed[i]) * 0.5f
                    : _smoothed[i] + (target - _smoothed[i]) * 0.12f;
            }
        }

        // 一次性里程碑日志：出现这一行即说明「捕获 → FFT → 电平」整条链路正常，
        // 用户若仍看不到柱条，问题必定在覆盖层挂载或绘制侧，排查方向立刻收窄。
        var peakLevel = 0f;
        foreach (var level in newLevels)
        {
            peakLevel = Math.Max(peakLevel, level);
        }

        if (peakLevel > 0.01f && Interlocked.CompareExchange(ref _firstSignalLogged, 1, 0) == 0)
        {
            Log($"频谱捕获: 首次得到有效电平（最大={peakLevel:F3}）—— 捕获与 FFT 链路正常。");
        }

        Interlocked.Increment(ref _blockCount);
        ReportDiagnosticPeriodically();
    }

    /// <summary>
    /// 周期性写出运行诊断（每 10 秒，或在「长时间零样本」时给出明确原因），
    /// 让用户能把 preview-debug.log 直接贴给 issue。
    /// </summary>
    private void ReportDiagnosticPeriodically()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDiagnosticAt).TotalSeconds < 10)
        {
            return;
        }

        var samples = Interlocked.Read(ref _sampleCount);
        var stalled = samples == _lastDiagnosticSampleCount;
        _lastDiagnosticAt = now;
        _lastDiagnosticSampleCount = samples;
        var raw = 0f;
        lock (_lock)
        {
            for (var i = 0; i < DefaultBars; i++)
            {
                raw = Math.Max(raw, _raw[i]);
            }
        }

        // 直接给出结论，避免让用户自己解读数字。注意首次采样时 _lastDiagnosticSampleCount
        // 取 -1（而非 0），否则「还没有任何数据」会被误判成「数据停止了」。
        var conclusion = raw > 0.001f
            ? "正常（捕获到有效电平）"
            : samples == 0
                ? "回环完全没送数据 → 默认输出设备不可用/被独占（见下行设备状态）"
                : stalled
                    ? "回环未送新样本 → 捕获已中断/默认设备被占用，注入器会自动重建"
                    : "有样本但全为静音 → 实际出声设备很可能不是默认输出设备";
        Log(
            $"频谱运行: 已捕获={samples} 样本 FFT帧数={Interlocked.Read(ref _blockCount)} " +
            $"峰值增益={_peak:F4} 瞬时最大电平={raw:F3} 判定={conclusion}");
        if (raw <= 0.001f)
        {
            Log($"频谱运行: {DescribeDefaultRenderDevice()}");
        }
    }

    private void OnCaptureStopped(object? sender, string? reason)
    {
        _running = false;
        // 回环被中断（默认设备切换 / 音频服务重启 / 独占占用）时通知注入器重建捕获，
        // 否则频谱会永久静止且没有任何提示（Issue #6 的「听不见、不会跳」）。
        var text = string.IsNullOrEmpty(reason) ? "无异常（设备切换或停止）" : reason;
        DiagnosticMessage = $"回环捕获已停止：{text}";
        Log($"频谱捕获: {DiagnosticMessage}");
        try
        {
            RecordingStopped?.Invoke(this, text);
        }
        catch
        {
            // 回调异常不影响捕获器自身。
        }
    }

    /// <summary>回环捕获意外停止时触发（参数为原因描述）。注入器据此决定是否重建。</summary>
    public event Action<AudioSpectrumCapture, string>? RecordingStopped;

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
