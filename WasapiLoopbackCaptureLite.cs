using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace ClassIslandInjector;

/// <summary>
/// 回环捕获源的统一抽象：NAudio 实现（<see cref="NaudioLoopbackCapture"/>）与
/// 自带 COM 互操作实现（<see cref="WasapiLoopbackCaptureLite"/>）都归到这一层，
/// 上层的 FFT / 电平 / 覆盖层逻辑对二者无感知。
/// </summary>
internal interface ILoopbackCapture : IDisposable
{
    /// <summary>收到一批 PCM（字节缓冲区 + 有效字节数），可能与调用线程不同。</summary>
    event EventHandler<LoopbackDataEventArgs>? DataAvailable;

    /// <summary>捕获停止（参数为原因描述，正常停止时为 null）。</summary>
    event EventHandler<string?>? Stopped;

    bool IsRunning { get; }

    /// <summary>流格式（采样率 / 声道 / 位深 / 是否浮点），用于解码 PCM。</summary>
    LoopbackFormat Format { get; }

    /// <summary>实现描述，写进日志便于区分走的哪条路。</summary>
    string Describe { get; }

    void Start();

    void Stop();
}

/// <summary>回环流的格式描述。</summary>
internal readonly record struct LoopbackFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

internal sealed class LoopbackDataEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    public byte[] Buffer { get; } = buffer;

    public int BytesRecorded { get; } = bytesRecorded;
}

/// <summary>
/// NAudio 实现：即原来的 <see cref="WasapiLoopbackCapture"/>，诊断信息最全
/// （设备友好名 / 静音 / 主音量都能读到），正常情况下优先使用它。
/// </summary>
internal sealed class NaudioLoopbackCapture : ILoopbackCapture
{
    private readonly WasapiLoopbackCapture _capture = new();
    private volatile bool _started;

    public NaudioLoopbackCapture()
    {
        var fmt = _capture.WaveFormat;
        // WASAPI 混音格式一般是 WAVE_FORMAT_EXTENSIBLE + IEEE float；NAudio 会归一化成
        // 标准 WaveFormat，所以浮点既可能报成 IeeeFloat，也可能报成「32 位非 PCM」。
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                      fmt.BitsPerSample == 32 && fmt.Encoding != WaveFormatEncoding.Pcm;
        Format = new LoopbackFormat(fmt.SampleRate, Math.Max(1, fmt.Channels), fmt.BitsPerSample, isFloat);
        _capture.DataAvailable += (_, e) => DataAvailable?.Invoke(this, new LoopbackDataEventArgs(e.Buffer, e.BytesRecorded));
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(
            this,
            e.Exception != null ? $"{e.Exception.GetType().Name}: {e.Exception.Message}" : null);
    }

    public event EventHandler<LoopbackDataEventArgs>? DataAvailable;

    public event EventHandler<string?>? Stopped;

    public bool IsRunning => _started;

    public LoopbackFormat Format { get; }

    public string Describe => $"NAudio（{WasapiEndpointInfo.DescribeDefaultRenderDevice()}）";

    public void Start()
    {
        _capture.StartRecording();
        _started = true;
    }

    public void Stop()
    {
        _started = false;
        try { _capture.StopRecording(); } catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        try { _capture.Dispose(); } catch { /* 忽略 */ }
    }
}

/// <summary>
/// 自带 COM 互操作的 WASAPI 回环捕获（<b>完全不经过 NAudio</b>）。
///
/// <para><b>为什么需要它（Issue #6 的最终根因）</b></para>
/// <para>
/// NAudio 走的是「COM 类」路径：<c>new MMDeviceEnumeratorComObject()</c> 让运行时按 CLSID
/// 激活组件，而同一个 COM 对象在一个进程里只有一份「类型标识」，谁先激活就归谁。
/// ClassIsland 的 <c>PluginLoadContext</c> 给每个插件一个独立 ALC，插件目录里的 NAudio
/// 因此是各自独立的副本；进程里就同时存在两份
/// <c>NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject</c>。
/// 只要别的插件（例如装了 Decibel_Monitor，它引用 NAudio 2.2.1）先激活过这个组件，
/// 我们这边的 <c>new WasapiLoopbackCapture()</c> 就会抛：
/// </para>
/// <code>
/// InvalidCastException: Unable to cast object of type
///   'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'
/// to type 'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'.
/// </code>
/// <para>
/// 注意异常信息里 <b>源类型和目标类型同名</b> —— 这只有在「同进程存在两份同名类型」时
/// 才可能出现，是判定该冲突的铁证（另见 NAudio#421、Flow.Launcher#4258）。
/// </para>
/// <para><b>为什么自带互操作不受影响</b></para>
/// <para>
/// 这里声明的全是 <c>[ComImport]</c> <b>接口</b>，一律用 <c>CoCreateInstance(CLSID, IID)</c>
/// 或 <c>QueryInterface</c> 取接口视图。接口转换按 IID 走 QI，不依赖「类」的类型标识归属，
/// 因此同进程里有几份 NAudio 都与本实现无关。
/// </para>
/// <para>互操作声明参考 NAudio（MIT）的 WASAPI 声明，按最小可用集合重写。</para>
/// </summary>
internal sealed class WasapiLoopbackCaptureLite : ILoopbackCapture
{
    private const long ReftimesPerMillisec = 10000;

    /// <summary>回环缓冲长度（毫秒）；比 NAudio 默认 100ms 略小，降低一次延迟。</summary>
    private const int BufferMilliseconds = 100;

    private const int PollIntervalMs = 10;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED：设备被拔掉 / 被独占 / 音频服务重启。</summary>
    private const int EDeviceInvalidated = unchecked((int)0x88890004);

    private const int BufferFlagsSilent = 0x2;

    private readonly object _gate = new();
    private Thread? _thread;
    private WasapiEndpointInfo.IAudioClient? _audioClient;
    private WasapiEndpointInfo.IAudioCaptureClient? _captureClient;
    private byte[] _packetBuffer = new byte[1 << 16];
    private int _bytesPerFrame = 4;
    private volatile bool _running;
    private bool _disposed;

    public event EventHandler<LoopbackDataEventArgs>? DataAvailable;

    public event EventHandler<string?>? Stopped;

    public bool IsRunning => _running;

    public LoopbackFormat Format { get; private set; }

    public string Describe => $"自带互操作（{WasapiEndpointInfo.DescribeDefaultRenderDevice()}）";

    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        var devicePtr = WasapiEndpointInfo.OpenDefaultRenderDevice();
        try
        {
            var audioClient = WasapiEndpointInfo.ActivateAudioClient(devicePtr);
            var format = WasapiEndpointInfo.ReadMixFormat(audioClient, out var mixFormatPtr);
            try
            {
                // 回环 = 共享模式 + LOOPBACK 标志；共享模式的格式必须是设备混音格式本身，
                // 所以直接把 GetMixFormat 的指针交回去（引擎会拷贝，随后即可释放）。
                var sessionGuid = Guid.Empty;
                var hr = audioClient.Initialize(
                    WasapiEndpointInfo.ShareModeShared,
                    WasapiEndpointInfo.StreamFlagsLoopback,
                    ReftimesPerMillisec * BufferMilliseconds,
                    0,
                    mixFormatPtr,
                    ref sessionGuid);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }

            var hrBuffer = audioClient.GetBufferSize(out var bufferFrames);
            if (hrBuffer < 0)
            {
                Marshal.ThrowExceptionForHR(hrBuffer);
            }

            var captureClient = WasapiEndpointInfo.GetCaptureClient(audioClient);

            Format = format;
            _bytesPerFrame = Math.Max(1, format.Channels * Math.Max(1, format.BitsPerSample / 8));
            var packetBytes = Math.Max(_bytesPerFrame * 4096, (int)bufferFrames * _bytesPerFrame);
            if (_packetBuffer.Length < packetBytes)
            {
                _packetBuffer = new byte[packetBytes];
            }

            lock (_gate)
            {
                _audioClient = audioClient;
                _captureClient = captureClient;
            }

            var hrStart = audioClient.Start();
            if (hrStart < 0)
            {
                Marshal.ThrowExceptionForHR(hrStart);
            }

            _running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "InjectorSpectrumLoopback"
            };
            _thread.Start();
        }
        finally
        {
            WasapiEndpointInfo.Release(devicePtr);
        }
    }

    public void Stop()
    {
        if (!_running && _audioClient == null)
        {
            return;
        }

        _running = false;
        var thread = _thread;
        _thread = null;
        try { thread?.Join(500); } catch { /* 忽略 */ }

        lock (_gate)
        {
            try { _audioClient?.Stop(); } catch { /* 忽略 */ }
            _captureClient = null;
            _audioClient = null;
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

    private void CaptureLoop()
    {
        string? reason = null;
        try
        {
            while (_running)
            {
                if (!DrainPackets())
                {
                    reason = "设备失效或被独占（AUDCLNT_E_DEVICE_INVALIDATED）";
                    break;
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _running = false;
            try
            {
                Stopped?.Invoke(this, reason);
            }
            catch
            {
                // 回调异常不影响捕获器自身。
            }
        }
    }

    /// <summary>把当前可读的包全部读出并投递。返回 false 表示设备已失效，应结束并触发重建。</summary>
    private bool DrainPackets()
    {
        WasapiEndpointInfo.IAudioCaptureClient? capture;
        lock (_gate)
        {
            capture = _captureClient;
        }

        if (capture == null)
        {
            return false;
        }

        while (_running)
        {
            var hr = capture.GetNextPacketSize(out var framesInNextPacket);
            if (hr < 0)
            {
                return hr != EDeviceInvalidated;
            }

            if (framesInNextPacket <= 0)
            {
                return true;
            }

            hr = capture.GetBuffer(out var dataPtr, out var frames, out var flags, out _, out _);
            if (hr < 0)
            {
                return hr != EDeviceInvalidated;
            }

            try
            {
                if (frames > 0)
                {
                    var bytes = frames * _bytesPerFrame;
                    if ((flags & BufferFlagsSilent) != 0)
                    {
                        // 静音包不读内存（此时指针内容无定义），投递等长的 0。
                        // 故意投递静音：让滑窗继续推进，柱条能自然衰减而不是「冻住」。
                        if (_packetBuffer.Length < bytes)
                        {
                            _packetBuffer = new byte[bytes];
                        }

                        Array.Clear(_packetBuffer, 0, bytes);
                        DataAvailable?.Invoke(this, new LoopbackDataEventArgs(_packetBuffer, bytes));
                    }
                    else if (dataPtr != IntPtr.Zero)
                    {
                        if (_packetBuffer.Length < bytes)
                        {
                            _packetBuffer = new byte[bytes];
                        }

                        Marshal.Copy(dataPtr, _packetBuffer, 0, bytes);
                        DataAvailable?.Invoke(this, new LoopbackDataEventArgs(_packetBuffer, bytes));
                    }
                }
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }

        return true;
    }
}

/// <summary>
/// 诊断用：列出进程内已加载的 NAudio 程序集及其来源路径。
///
/// <para>
/// ClassIsland 的 <c>PluginLoadContext</c> 给每个插件独立的 ALC，插件目录里的 NAudio
/// 因此是各自独立的副本 —— 同进程出现 <b>两份及以上同名 NAudio</b> 时，
/// <c>MMDeviceEnumeratorComObject</c> 这个 COM 类的类型标识会被先激活的一方占住，
/// 后激活的一方必然抛 InvalidCastException（源/目标类型同名）。把 Location 打出来，
/// 就能一眼看出是哪个插件目录带的副本。
/// </para>
/// </summary>
internal static class NaudioConflictDiagnostics
{
    public static string DescribeLoadedCopies()
    {
        try
        {
            var items = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName();
                if (name.Name is not { } simple ||
                    !simple.StartsWith("NAudio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string location;
                try
                {
                    location = assembly.Location;
                }
                catch
                {
                    location = "(无法读取路径)";
                }

                var item = $"{simple} {name.Version} @ {location}";
                if (!items.Contains(item))
                {
                    items.Add(item);
                }
            }

            return items.Count == 0 ? "（未发现已加载的 NAudio 程序集）" : string.Join(" | ", items);
        }
        catch (Exception ex)
        {
            return $"(枚举已加载程序集失败: {ex.GetType().Name})";
        }
    }
}

/// <summary>
/// 默认渲染端点的取用工具。全部按接口 IID 取（CoCreateInstance / QueryInterface），
/// 不触碰 NAudio 的 COM 类，因此不受同进程 NAudio 副本的类型标识冲突影响。
/// </summary>
internal static class WasapiEndpointInfo
{
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PkeyDeviceFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    internal const uint CLSCTX_ALL = 0x17;
    internal const uint ShareModeShared = 0;
    internal const uint StreamFlagsLoopback = 0x00020000;

    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int FormatTagIeeeFloat = 3;
    private const int FormatTagExtensible = 0xFFFE;
    private const int PidDeviceFriendlyName = 14;
    private const uint StorageAccessRead = 0;
    private const ushort VariantTypeLpwstr = 31;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>CoCreateInstance 出 MMDeviceEnumerator，再取默认渲染端点（返回裸指针，调用方负责 Release）。</summary>
    public static IntPtr OpenDefaultRenderDevice()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var enumeratorPtr);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumeratorPtr);
            var hrEndpoint = enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var devicePtr);
            if (hrEndpoint < 0)
            {
                Marshal.ThrowExceptionForHR(hrEndpoint);
            }

            return devicePtr;
        }
        finally
        {
            Marshal.Release(enumeratorPtr);
        }
    }

    public static void Release(IntPtr comPointer)
    {
        if (comPointer != IntPtr.Zero)
        {
            Marshal.Release(comPointer);
        }
    }

    /// <summary>设备 ID（形如 <c>{0.0.0.00000000}.{guid}</c>）；失败返回空串。</summary>
    public static string TryGetDeviceId(IntPtr devicePtr)
    {
        try
        {
            var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            return device.GetId(out var id) < 0 || string.IsNullOrEmpty(id) ? "" : id;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>在端点设备上激活 IAudioClient（走 QueryInterface，不依赖 COM 类标识）。</summary>
    public static IAudioClient ActivateAudioClient(IntPtr devicePtr)
    {
        var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
        var iid = IID_IAudioClient;
        var hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var client);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return client as IAudioClient
               ?? throw new InvalidOperationException("端点设备未提供 IAudioClient 接口。");
    }

    public static IAudioCaptureClient GetCaptureClient(IAudioClient audioClient)
    {
        var iid = IID_IAudioCaptureClient;
        var hr = audioClient.GetService(iid, out var capture);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return capture as IAudioCaptureClient
               ?? throw new InvalidOperationException("音频客户端未提供 IAudioCaptureClient 接口。");
    }

    /// <summary>读取混音格式（返回的指针需调用方 FreeCoTaskMem），同时解析出格式描述。</summary>
    public static LoopbackFormat ReadMixFormat(IAudioClient audioClient, out IntPtr mixFormatPtr)
    {
        var hr = audioClient.GetMixFormat(out mixFormatPtr);
        if (hr < 0 || mixFormatPtr == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
        }

        var ex = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPtr);
        var isFloat = ex.FormatTag == FormatTagIeeeFloat;
        if (ex.FormatTag == FormatTagExtensible && ex.CbSize >= 22)
        {
            // WAVEFORMATEXTENSIBLE：WAVEFORMATEX(18) + wValidBitsPerSample(2) + dwChannelMask(4) 之后是 SubFormat GUID。
            isFloat = Marshal.PtrToStructure<Guid>(IntPtr.Add(mixFormatPtr, 24)) == IeeeFloatSubFormat;
        }

        return new LoopbackFormat(
            (int)ex.SamplesPerSec,
            Math.Max(1, (int)ex.Channels),
            ex.BitsPerSample == 0 ? 32 : ex.BitsPerSample,
            isFloat);
    }

    /// <summary>
    /// 默认渲染端点的友好名 / 静音 / 主音量。三者在「样本一直有、电平一直是 0」时最关键：
    /// 回环抓的是默认输出设备，实际出声设备不是它（USB / 蓝牙 / HDMI 没设成默认）时抓到的
    /// 就是纯静音。失败时把原因写进字符串，别让日志出现空白。
    /// </summary>
    public static string DescribeDefaultRenderDevice()
    {
        var devicePtr = IntPtr.Zero;
        try
        {
            devicePtr = OpenDefaultRenderDevice();
            var device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            var name = TryGetFriendlyName(device);
            if (string.IsNullOrEmpty(name))
            {
                // 友好名取不到（属性存储不可用）时退回设备 ID，至少能对上「设置 → 声音」里的条目。
                name = TryGetDeviceId(devicePtr);
            }

            if (string.IsNullOrEmpty(name))
            {
                name = "(未知设备)";
            }
            var mute = "静音状态未知";
            var volume = "";
            if (TryGetEndpointVolume(device) is { } endpointVolume)
            {
                if (endpointVolume.GetMute(out var muted) >= 0)
                {
                    mute = muted ? "已静音" : "未静音";
                }

                if (endpointVolume.GetMasterVolumeLevelScalar(out var level) >= 0)
                {
                    volume = $" 主音量={level * 100:F0}%";
                }
            }

            return $"默认输出设备=\"{name}\" {mute}{volume}";
        }
        catch (Exception ex)
        {
            return $"默认输出设备读取失败: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Release(devicePtr);
        }
    }

    private static string? TryGetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(StorageAccessRead, out store) < 0 || store == null)
            {
                return null;
            }

            var key = new PropertyKey { FormatId = PkeyDeviceFriendlyName, PropertyId = PidDeviceFriendlyName };
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                return value.VariantType == VariantTypeLpwstr && value.PointerValue != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store != null)
            {
                try { Marshal.ReleaseComObject(store); } catch { /* 忽略 */ }
            }
        }
    }

    private static IAudioEndpointVolume? TryGetEndpointVolume(IMMDevice device)
    {
        try
        {
            var iid = IID_IAudioEndpointVolume;
            return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volume) < 0
                ? null
                : volume as IAudioEndpointVolume;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid id,
            uint clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int propCount);

        int GetAt(int property, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        int SetValue(ref PropertyKey key, ref PropVariant value);

        int Commit();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, ref Guid audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferSize);

        long GetStreamLatency();

        [PreserveSig]
        int GetCurrentPadding(out int currentPadding);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr format, IntPtr closestMatchFormat);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormatPointer);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr dataBuffer, out int numFramesToRead, out int bufferFlags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);

        int UnregisterControlChangeNotify(IntPtr notify);

        int GetChannelCount(out int channelCount);

        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        int GetChannelVolumeLevel(uint channel, out float levelDb);

        int GetChannelVolumeLevelScalar(uint channel, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        int GetVolumeStepInfo(out uint step, out uint stepCount);

        int VolumeStepUp(ref Guid eventContext);

        int VolumeStepDown(ref Guid eventContext);

        int QueryHardwareSupport(out uint hardwareSupportMask);

        int GetVolumeRange(out float volumeMinDb, out float volumeMaxDb, out float volumeIncrementDb);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;

        [FieldOffset(8)] public IntPtr PointerValue;
    }
}
