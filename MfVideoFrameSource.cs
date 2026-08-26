using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassIslandInjector;

/// <summary>
/// 用 Windows Media Foundation 的 Source Reader 逐帧解码视频（MP4 / H.264 等系统支持格式），
/// 输出自上而下的 BGRA 像素（alpha 强制 0xFF）。
/// 纯 Win32 COM 调用，不依赖 WinRT / Windows SDK 版本对齐，因此不会踩宿主 PluginLoadContext
/// 的 SDK 版本坑；使用系统自带硬件解码器，性能远优于 System.Drawing 软解码 GIF。
/// 后台线程以目标帧率驱动逐帧读取，通过 <see cref="Start"/> 传入的回调把帧交给调用方
/// （注入器负责 Dispatcher.UIThread.Post + WriteableBitmap 更新）。
/// 内部用「UI 消费完毕」信号量同步，保证帧缓冲不被并发覆写；循环播放用
/// SetStreamSelection 复位实现。所有 MF 调用均有 try/catch 兜底，失败静默降级。
/// </summary>
internal sealed class MfVideoFrameSource : IDisposable
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[video-fill] {message}");

    // ---- Media Foundation GUID 常量 ----
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71"); // 'vids'
    private static readonly Guid MF_MT_SUBTYPE = new("76A5AE91-13FA-4D14-8D91-3384233D92E9");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    /// <summary>NV12（解码器原生格式，视频处理器拒绝 RGB32 时的回退）。</summary>
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D"); // UINT64，高=高，低=宽
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    /// <summary>视频隔行模式（UINT32，MFVideoInterlaceMode）。</summary>
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private const uint MFVideoInterlace_Progressive = 2;
    /// <summary>启用 Source Reader 的视频处理（颜色转换/缩放），否则设置非解码器原生输出类型会被拒绝。</summary>
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    /// <summary>高级视频处理（Win8+，支持更多转换），与上者同时设置以最大化兼容。</summary>
    private static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x10;
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const ulong MF_VERSION = 0x00020070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const uint MFSTARTUP_NOSOCK = 0x1;

    // ---- P/Invoke ----
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(ulong version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IMFSourceReader reader);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    // ---- COM 接口（vtable 顺序与原生头文件一致；不调用的方法以占位签名保留槽位）----

    /// <summary>IMFAttributes（IID 2CD2D921-C447-44A7-A13C-4ADABFC247E3）。</summary>
    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(ref Guid key, IntPtr value);
        int GetItemType(ref Guid key, out int type);
        int CompareItem(ref Guid key, IntPtr value, out int result);
        int Compare([In] IMFAttributes theirs, out int result);
        int GetUINT32(ref Guid key, out uint value);
        int GetUINT64(ref Guid key, out ulong value);
        int GetDouble(ref Guid key, out double value);
        int GetGUID(ref Guid key, out Guid value);
        int GetStringLength(ref Guid key, out uint length);
        int GetString(ref Guid key, IntPtr value, uint length, out uint lengthOut);
        int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        int GetBlobSize(ref Guid key, out uint size);
        int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint sizeOut);
        int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        int GetUnknown(ref Guid key, ref Guid riid, out IntPtr unknown);
        int SetItem(ref Guid key, IntPtr value);
        int DeleteItem(ref Guid key);
        int DeleteAllItems();
        int SetUINT32(ref Guid key, uint value);
        int SetUINT64(ref Guid key, ulong value);
        int SetDouble(ref Guid key, double value);
        int SetGUID(ref Guid key, ref Guid value);
        int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        int SetBlob(ref Guid key, byte[] value, uint size);
        int SetUnknown(ref Guid key, IntPtr unknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out uint count);
        int GetItemByIndex(uint index, out Guid key, IntPtr value);
        int CopyAllItems([In] IMFAttributes dest);
    }

    /// <summary>IMFMediaType（IID 44AE0FA8-EA31-4109-8D2E-4CAE4997C555），继承 IMFAttributes。</summary>
    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        int GetMajorType(out Guid type);
        int IsCompressedFormat(out int compressed);
        int IsEqual([In] IMFMediaType other, out uint flags);
        int GetRepresentation(Guid guid, out IntPtr representation);
        int FreeRepresentation(Guid guid, IntPtr representation);
    }

    /// <summary>IMFSourceReader（IID 70AE66F2-C809-4E4F-8915-BDCB406B7993）。</summary>
    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        int GetStreamSelection(uint index, out int selected);
        int SetStreamSelection(uint index, int selected);
        int GetNativeMediaType(uint index, uint position, out IMFMediaType type);
        int GetCurrentMediaType(uint index, out IMFMediaType type);
        int SetCurrentMediaType(uint index, IntPtr type);
        int SetCurrentMediaTypeByIndex(uint index, uint position);
        int Flush(uint index);
        int ReadSample(uint index, uint control, out uint flags, out long timestamp, out IMFSample sample, out uint actualIndex);
        int GetPresentationAttribute(uint index, ref Guid key, IntPtr value);
    }

    /// <summary>IMFSample（IID C40A00F2-B93A-4D80-AE8C-5A1C634F58E4），继承 IMFAttributes。</summary>
    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        int GetSampleFlags(out uint flags);
        int SetSampleFlags(uint flags);
        int GetSampleTime(out long time);
        int SetSampleTime(long time);
        int GetSampleDuration(out long duration);
        int SetSampleDuration(long duration);
        int GetBufferCount(out uint count);
        int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        int AddBuffer([In] IMFMediaBuffer buffer);
        int RemoveBufferByIndex(uint index);
        int RemoveAllBuffers();
        int GetTotalLength(out uint length);
        int CopyToBuffer([In] IMFMediaBuffer buffer);
    }

    /// <summary>IMFMediaBuffer（IID 045FA593-8799-42B8-BC8D-8968C6453507）。</summary>
    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        int Unlock();
        int GetCurrentLength(out uint length);
        int SetCurrentLength(uint length);
        int GetMaxLength(out uint length);
    }

    // ---- vtable 手动调用（绕开 .NET ComImport marshaller 对 SetCurrentMediaType 的处理问题）----
    /// <summary>
    /// .NET 的 ComImport marshaller 对 IMFSourceReader.SetCurrentMediaType 的调用会抛
    /// ArgumentException（"Value does not fall within the expected range"，与参数形式无关），
    /// 因此改用 GetDelegateForFunctionPointer 直接从 vtable 槽 7 手动调用。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeDelegate(IntPtr self, uint index, IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeByIndexDelegate(IntPtr self, uint index, uint position);

    private static int CallSetCurrentMediaType(IntPtr readerPtr, uint index, IntPtr typePtr)
    {
        var vtablePtr = Marshal.ReadIntPtr(readerPtr);
        var methodPtr = Marshal.ReadIntPtr(vtablePtr, 7 * IntPtr.Size); // IUnknown 3 + SetCurrentMediaType 第 5 个方法
        var del = Marshal.GetDelegateForFunctionPointer<SetCurrentMediaTypeDelegate>(methodPtr);
        return del(readerPtr, index, typePtr);
    }

    /// <summary>以原生指针走 vtable 手动调用 reader.SetCurrentMediaType（绕开 .NET ComImport marshaller）。</summary>
    private int ApplyOutputType(IMFSourceReader reader, IMFMediaType type)
    {
        var readerPtr = Marshal.GetIUnknownForObject(reader);
        var mtPtr = Marshal.GetIUnknownForObject(type);
        try
        {
            return CallSetCurrentMediaType(readerPtr, _videoStreamIndex, mtPtr);
        }
        finally
        {
            Marshal.Release(mtPtr);
            Marshal.Release(readerPtr);
        }
    }

    /// <summary>构建指定 SUBTYPE（可带尺寸）的媒体类型并尝试 SetCurrentMediaType，返回 HRESULT。</summary>
    private int TrySetOutputType(IMFSourceReader reader, Guid subtype, long targetW, long targetH)
    {
        var hr = MFCreateMediaType(out var mt);
        if (hr < 0 || mt == null)
        {
            return hr;
        }

        try
        {
            var gMajor = MF_MT_MAJOR_TYPE;
            var gVideo = MFMediaType_Video;
            var gSubtype = MF_MT_SUBTYPE;
            var gInterlace = MF_MT_INTERLACE_MODE;
            var gSub = subtype;
            mt.SetGUID(ref gMajor, ref gVideo);
            mt.SetGUID(ref gSubtype, ref gSub);
            mt.SetUINT32(ref gInterlace, MFVideoInterlace_Progressive);
            if (targetW > 0 && targetH > 0)
            {
                var gSize = MF_MT_FRAME_SIZE;
                mt.SetUINT64(ref gSize, ((ulong)targetH << 32) | (ulong)targetW);
            }

            // 诊断：读回验证媒体类型属性是否真正写入成功（ComImport SetGUID 可能静默失败）。
            var mtPtr = Marshal.GetIUnknownForObject(mt);
            try
            {
                var getGuid = GetVtbl<GetGuidDelegate>(mtPtr, 10);
                var gM = MF_MT_MAJOR_TYPE;
                var gS = MF_MT_SUBTYPE;
                var hM = getGuid(mtPtr, ref gM, out var major);
                var hS = getGuid(mtPtr, ref gS, out var sub);
                Log($"输出类型读回 MAJOR hr=0x{hM:X8}={major} SUBTYPE hr=0x{hS:X8}={sub}");
            }
            finally
            {
                Marshal.Release(mtPtr);
            }

            return ApplyOutputType(reader, mt);
        }
        finally
        {
            Marshal.ReleaseComObject(mt);
        }
    }

    // ---- vtable 手动调用通用辅助 ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStreamSelectionDelegate(IntPtr self, uint index, out int selected);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNativeMediaTypeDelegate(IntPtr self, uint index, uint position, out IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidDelegate(IntPtr self, ref Guid key, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUint64Delegate(IntPtr self, ref Guid key, out ulong value);

    /// <summary>从对象指针的 vtable 取指定槽位的函数指针并包装为委托。</summary>
    private static TDelegate GetVtbl<TDelegate>(IntPtr objPtr, int slot) where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(objPtr);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
    }

    /// <summary>诊断：验证 vtable 槽位并枚举各流/各原生媒体类型（不参与正常流程）。</summary>
    private static void DiagnoseReader(IMFSourceReader reader)
    {
        try
        {
            var rp = Marshal.GetIUnknownForObject(reader);
            try
            {
                var gss = GetVtbl<GetStreamSelectionDelegate>(rp, 3);
                for (var s = 0u; s < 4; s++)
                {
                    var hs = gss(rp, s, out var sel);
                    Log($"流 {s} GetStreamSelection hr=0x{hs:X8} sel={sel}");
                    var gGetNative = GetVtbl<GetNativeMediaTypeDelegate>(rp, 5);
                    for (var t = 0u; t < 4; t++)
                    {
                        var nh = gGetNative(rp, s, t, out var ntPtr);
                        if (nh < 0 || ntPtr == IntPtr.Zero)
                        {
                            break;
                        }

                        try
                        {
                            var getGuid = GetVtbl<GetGuidDelegate>(ntPtr, 10);
                            var getUint64 = GetVtbl<GetUint64Delegate>(ntPtr, 8);
                            var gMajor = MF_MT_MAJOR_TYPE;
                            var hm = getGuid(ntPtr, ref gMajor, out var major);
                            var gSub = MF_MT_SUBTYPE;
                            var hsub = getGuid(ntPtr, ref gSub, out var sub);
                            var gSize = MF_MT_FRAME_SIZE;
                            var hz = getUint64(ntPtr, ref gSize, out var size);
                            Log($"  类型[{t}] hr=0x{nh:X8} MAJOR hr=0x{hm:X8}={major} SUBTYPE hr=0x{hsub:X8}={sub} SIZE hr=0x{hz:X8}={(hz >= 0 ? $"{size & 0xFFFFFFFF}x{size >> 32}" : "?")}");
                        }
                        finally
                        {
                            Marshal.Release(ntPtr);
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(rp);
            }
        }
        catch (Exception ex)
        {
            Log($"诊断异常: {ex.Message}");
        }
    }

    // ---- 实例状态 ----
    private IMFSourceReader? _reader;
    private FFmpegVideoDecoder? _ffmpeg;
    private Thread? _worker;
    private volatile bool _running;
    private bool _mfStarted;
    private int _width;
    private int _height;
    private int _srcStride;
    /// <summary>视频流的具体索引（SetCurrentMediaType 不接受 FIRST_VIDEO_STREAM 特殊值，必须用具体索引）。</summary>
    private uint _videoStreamIndex;
    /// <summary>当前输出是否为 NV12（RGB32 被拒时的回退，需手动 YUV→BGRA）。</summary>
    private bool _outputIsNv12;
    /// <summary>MF 原始输出像素（bottom-up / 任意 alpha），逐帧复用。</summary>
    private byte[]? _rawBuffer;
    /// <summary>处理后 BGRA（top-down，alpha=0xFF）像素，交回调使用。</summary>
    private byte[]? _frameBuffer;
    private bool _alphaOpaque;
    private bool _alphaChecked;
    private readonly ManualResetEventSlim _uiConsumed = new(true);
    private Action<MfVideoFrame>? _frameCallback;
    private double _targetFps = 24;
    private bool _loop = true;

    /// <summary>
    /// 打开视频并建立解码（输出 BGRA，必要时缩放到 maxDimension 内）。
    /// 优先 FFmpeg（自带解码器，任何机器可用）；FFmpeg 不可用时回退 Media Foundation。
    /// 成功返回 true；任何错误返回 false（调用方降级为无视频）。
    /// </summary>
    public bool Open(string path, int maxDimension)
    {
        var ff = new FFmpegVideoDecoder();
        if (ff.Open(path, maxDimension))
        {
            _ffmpeg = ff;
            _width = ff.OutputWidth;
            _height = ff.OutputHeight;
            _frameBuffer = new byte[_width * _height * 4];
            Log($"使用 FFmpeg 解码 {_width}x{_height}");
            return true;
        }

        ff.Dispose();
        Log("FFmpeg 不可用，回退 Media Foundation");
        return OpenMf(path, maxDimension);
    }

    /// <summary>Media Foundation 打开路径（FFmpeg 不可用时的回退）。</summary>
    private bool OpenMf(string path, int maxDimension)
    {
        try
        {
            var hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCK);
            if (hr < 0)
            {
                Log($"MFStartup 失败 0x{hr:X8}");
                return false;
            }

            _mfStarted = true;
            // 创建 attributes 并启用视频处理：Source Reader 默认不做格式转换，
            // 不启用则 SetCurrentMediaType(RGB32) 会被拒绝（E_INVALIDARG）。
            var attrsHr = MFCreateAttributes(out var attrs, 2);
            if (attrsHr < 0 || attrs == null)
            {
                Log($"MFCreateAttributes 失败 0x{attrsHr:X8}");
                return false;
            }

            try
            {
                var gEnableVp = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                var hrVp = attrs.SetUINT32(ref gEnableVp, 1);
                Log($"attrs 视频处理设置 hrVp=0x{hrVp:X8}");
            }
            catch (Exception ex)
            {
                Log($"attrs 设置异常: {ex.Message}");
            }

            IMFSourceReader? reader = null;
            var attrsPtr = Marshal.GetIUnknownForObject(attrs);
            try
            {
                hr = MFCreateSourceReaderFromURL(ToFileUrl(path), attrsPtr, out reader);
            }
            finally
            {
                Marshal.Release(attrsPtr);
                Marshal.ReleaseComObject(attrs);
            }

            if (hr < 0 || reader == null)
            {
                Log($"MFCreateSourceReaderFromURL 失败 0x{hr:X8}");
                return false;
            }

            DiagnoseReader(reader);

            _videoStreamIndex = FindVideoStreamIndex(reader);
            Log($"视频流索引 = {_videoStreamIndex}");

            _reader = reader;
            if (!ConfigureOutput(maxDimension))
            {
                return false;
            }

            Log($"已打开 {path}：{_width}x{_height} stride={_srcStride}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Open 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>解码器当前配置步骤（诊断用）。</summary>
    private string _step = string.Empty;

    /// <summary>
    /// 枚举各流，返回第一个视频流的具体索引。
    /// 注意：SetCurrentMediaType / ReadSample 不接受 MF_SOURCE_READER_FIRST_VIDEO_STREAM 特殊值，
    /// 必须用具体流索引，否则返回 E_INVALIDARG。
    /// </summary>
    private uint FindVideoStreamIndex(IMFSourceReader reader)
    {
        try
        {
            var rp = Marshal.GetIUnknownForObject(reader);
            try
            {
                var gGetNative = GetVtbl<GetNativeMediaTypeDelegate>(rp, 5);
                for (var s = 0u; s < 8; s++)
                {
                    var nh = gGetNative(rp, s, 0, out var ntPtr);
                    if (nh < 0 || ntPtr == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        // 注意：GetGUID 必须从媒体类型对象的 vtable 取（槽 10），
                        // 不能复用 reader 的槽 10（那是 ReadSample，会原生崩溃）。
                        var gMajor = MF_MT_MAJOR_TYPE;
                        var getGuid = GetVtbl<GetGuidDelegate>(ntPtr, 10);
                        if (getGuid(ntPtr, ref gMajor, out var major) >= 0 &&
                            major == MFMediaType_Video)
                        {
                            return s;
                        }
                    }
                    finally
                    {
                        Marshal.Release(ntPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(rp);
            }
        }
        catch
        {
            // 找不到时回退流 0
        }

        return 0;
    }

    /// <summary>
    /// 配置输出为 RGB32 并读取实际尺寸 / stride；必要时让 Source Reader 缩放到 maxDimension 内。
    /// </summary>
    private bool ConfigureOutput(int maxDimension)
    {
        try
        {
            var reader = _reader;
            if (reader == null)
            {
                return false;
            }

            // 读取原生尺寸（用于按比例缩放）。
            // 注意：COM interop 的 Guid 参数必须用局部变量传 ref（静态只读字段不能 ref）。
            _step = "GetNativeMediaType";
            var nativeW = 0L;
            var nativeH = 0L;
            var gFrameSize = MF_MT_FRAME_SIZE;
            if (reader.GetNativeMediaType(_videoStreamIndex, 0, out var native) >= 0 && native != null)
            {
                try
                {
                    var g = gFrameSize;
                    if (native.GetUINT64(ref g, out var size) >= 0)
                    {
                        nativeW = (long)(size & 0xFFFFFFFF);
                        nativeH = (long)(size >> 32);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(native);
                }
            }
            else
            {
                Log("GetNativeMediaType 不可用（使用默认输出）");
            }

            var targetW = nativeW;
            var targetH = nativeH;
            if (targetW > 0 && targetH > 0 && maxDimension > 0 &&
                Math.Max(targetW, targetH) > maxDimension)
            {
                var scale = (double)maxDimension / Math.Max(targetW, targetH);
                targetW = Math.Max(2, (long)Math.Round(targetW * scale));
                targetH = Math.Max(2, (long)Math.Round(targetH * scale));
                targetW -= targetW % 2;
                targetH -= targetH % 2;
            }

            Log($"原生尺寸 {nativeW}x{nativeH}，目标输出 {targetW}x{targetH}");
            _step = "SetCurrentMediaType";
            _outputIsNv12 = false;
            var hr = TrySetOutputType(reader, MFVideoFormat_RGB32, targetW, targetH);
            if (hr < 0)
            {
                // 视频处理器可能拒绝显式尺寸：重试不带尺寸的 RGB32。
                hr = TrySetOutputType(reader, MFVideoFormat_RGB32, 0, 0);
            }

            if (hr < 0)
            {
                // RGB32 转换被拒（某些解码器/系统视频处理器受限）：回退解码器原生 NV12，
                // 由本类手动 YUV→BGRA。
                Log($"SetCurrentMediaType(RGB32) 失败 0x{hr:X8}，回退 NV12");
                hr = TrySetOutputType(reader, MFVideoFormat_NV12, targetW, targetH);
                if (hr < 0)
                {
                    hr = TrySetOutputType(reader, MFVideoFormat_NV12, 0, 0);
                }

                if (hr < 0)
                {
                    Log($"SetCurrentMediaType(NV12) 也失败 0x{hr:X8}");
                    // 诊断：用 SetCurrentMediaTypeByIndex（槽 8）选择原生类型，对比验证 vtable 布局。
                    try
                    {
                        var rp2 = Marshal.GetIUnknownForObject(reader);
                        try
                        {
                            var setByIndex = GetVtbl<SetCurrentMediaTypeByIndexDelegate>(rp2, 8);
                            var hbi = setByIndex(rp2, _videoStreamIndex, 0);
                            Log($"SetCurrentMediaTypeByIndex(流{_videoStreamIndex}, 类型0) hr=0x{hbi:X8}");
                        }
                        finally
                        {
                            Marshal.Release(rp2);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"SetCurrentMediaTypeByIndex 异常: {ex.Message}");
                    }

                    return false;
                }

                _outputIsNv12 = true;
                Log("已切换到 NV12 输出（手动 YUV→BGRA）");
            }

            // 读取实际输出尺寸与 stride。
            _step = "GetCurrentMediaType";
            hr = reader.GetCurrentMediaType(_videoStreamIndex, out var actual);
            if (hr < 0 || actual == null)
            {
                Log($"GetCurrentMediaType 失败 0x{hr:X8}");
                return false;
            }

            try
            {
                _step = "GetUINT64/GetUINT32";
                var g = gFrameSize;
                if (actual.GetUINT64(ref g, out var size) >= 0)
                {
                    _width = (int)(size & 0xFFFFFFFF);
                    _height = (int)(size >> 32);
                }

                _srcStride = _width * 4;
                var gStride = MF_MT_DEFAULT_STRIDE;
                if (actual.GetUINT32(ref gStride, out var stride) >= 0)
                {
                    _srcStride = (int)stride;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(actual);
            }

            if (_width <= 0 || _height <= 0)
            {
                Log("未取得有效视频尺寸");
                return false;
            }

            _frameBuffer = new byte[_width * _height * 4];
            _alphaChecked = false;
            return true;
        }
        catch (Exception ex)
        {
            Log($"ConfigureOutput 异常（步骤={_step}）: {ex}");
            return false;
        }
    }

    /// <summary>启动后台解码线程。</summary>
    public void Start(Action<MfVideoFrame> frameCallback, double targetFps, bool loop)
    {
        _frameCallback = frameCallback;
        _targetFps = targetFps;
        _loop = loop;
        _running = true;
        _worker = new Thread(DecodeLoop) { IsBackground = true, Name = "MfVideoFill" };
        _worker.Start();
    }

    /// <summary>UI 线程处理完当前帧后调用，允许后台线程写入下一帧。</summary>
    public void MarkFrameConsumed() => _uiConsumed.Set();

    /// <summary>后台解码主循环：拉帧 → 限帧 → 等 UI 消费 → 回调。</summary>
    private void DecodeLoop()
    {
        var intervalMs = _targetFps > 0 ? (long)(1000.0 / _targetFps) : 0L;
        var sw = new Stopwatch();
        while (_running)
        {
            sw.Restart();
            var ok = _ffmpeg != null ? ReadNextFrameFfmpeg() : ReadNextFrame();
            if (!ok)
            {
                if (_loop && TryRestart())
                {
                    continue;
                }

                Log(_loop ? "播放结束（循环复位失败），停止" : "播放结束，停止");
                break;
            }

            if (intervalMs > 0)
            {
                var wait = intervalMs - sw.ElapsedMilliseconds;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }
            }

            // 等 UI 消费上一帧，避免覆写正在被读取的缓冲；超时则继续（可接受丢帧）。
            _uiConsumed.Wait(1000);
            _uiConsumed.Reset();
            if (!_running)
            {
                break;
            }

            try
            {
                _frameCallback?.Invoke(new MfVideoFrame(_frameBuffer!, _width, _height));
            }
            catch
            {
                _uiConsumed.Set();
            }
        }
    }

    /// <summary>FFmpeg 路径：读一帧并拷入 BGRA 帧缓冲；EOF / 错误返回 false。</summary>
    private bool ReadNextFrameFfmpeg()
    {
        if (_ffmpeg == null || !_ffmpeg.ReadFrame(out var pixels))
        {
            return false;
        }

        if (_frameBuffer == null || _frameBuffer.Length != pixels.Length)
        {
            _frameBuffer = new byte[pixels.Length];
        }

        Buffer.BlockCopy(pixels, 0, _frameBuffer, 0, pixels.Length);
        return true;
    }

    /// <summary>读取一帧视频并拷入帧缓冲；到流尾或失败返回 false。</summary>
    private bool ReadNextFrame()
    {
        var reader = _reader;
        if (reader == null)
        {
            return false;
        }

        while (_running)
        {
            var hr = reader.ReadSample(_videoStreamIndex, 0,
                out var flags, out _, out var sample, out _);
            if (hr < 0)
            {
                Log($"ReadSample 失败 0x{hr:X8}");
                return false;
            }

            if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                return false;
            }

            if (sample == null)
            {
                continue; // 无新样本，继续拉
            }

            try
            {
                hr = sample.ConvertToContiguousBuffer(out var buffer);
                if (hr < 0 || buffer == null)
                {
                    return false;
                }

                try
                {
                    hr = buffer.Lock(out var ptr, out _, out var len);
                    if (hr < 0 || ptr == IntPtr.Zero)
                    {
                        return false;
                    }

                    var expectedLen = _outputIsNv12
                        ? _width * _height * 3 / 2
                        : _width * _height * 4;
                    CopyPixels(ptr, (int)Math.Min(len, (uint)expectedLen));
                    buffer.Unlock();
                    return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }

        return false;
    }

    /// <summary>
    /// 把 MF 输出像素拷入帧缓冲：统一为自上而下、每行 width*4、alpha 0xFF。
    /// 原生 stride 为负（bottom-up）时逐行翻转；alpha 已不透明时走整块拷贝快速路径。
    /// 全程安全代码（Marshal.Copy），无需 /unsafe。NV12 输出时走手动 YUV→BGRA。
    /// </summary>
    private void CopyPixels(IntPtr src, int length)
    {
        if (_outputIsNv12)
        {
            CopyPixelsNv12(src, length);
            return;
        }

        var w = _width;
        var h = _height;
        var srcStride = _srcStride;
        var dstStride = w * 4;
        if (_frameBuffer == null)
        {
            return;
        }

        if (_rawBuffer == null || _rawBuffer.Length < length)
        {
            _rawBuffer = new byte[Math.Max(length, 1024)];
        }

        Marshal.Copy(src, _rawBuffer, 0, length);

        if (!_alphaChecked)
        {
            _alphaOpaque = true;
            var samples = Math.Min(length, 64 * 4);
            for (var i = 3; i < samples; i += 4)
            {
                if (_rawBuffer[i] != 0xFF)
                {
                    _alphaOpaque = false;
                    break;
                }
            }

            _alphaChecked = true;
        }

        if (srcStride == dstStride && srcStride > 0 && _alphaOpaque)
        {
            var copyLen = Math.Min(length, _frameBuffer.Length);
            Buffer.BlockCopy(_rawBuffer, 0, _frameBuffer, 0, copyLen);
            return;
        }

        var absStride = srcStride < 0 ? -srcStride : srcStride;
        for (var y = 0; y < h; y++)
        {
            var srcRow = srcStride < 0 ? (h - 1 - y) * absStride : y * absStride;
            var dstBase = y * dstStride;
            if (_alphaOpaque)
            {
                var rowLen = Math.Min(dstStride, Math.Max(0, length - srcRow));
                Buffer.BlockCopy(_rawBuffer, srcRow, _frameBuffer, dstBase, rowLen);
            }
            else
            {
                for (var x = 0; x < w; x++)
                {
                    var si = srcRow + x * 4;
                    var di = dstBase + x * 4;
                    _frameBuffer[di] = _rawBuffer[si];
                    _frameBuffer[di + 1] = _rawBuffer[si + 1];
                    _frameBuffer[di + 2] = _rawBuffer[si + 2];
                    _frameBuffer[di + 3] = 0xFF;
                }
            }
        }
    }

    /// <summary>
    /// NV12 → BGRA（BT.601 整数近似，满量程）：Y plane 在前，随后交错 UV plane。
    /// 布局：Y 每像素 1 字节，UV 每 2x2 块 2 字节（U、V 交替），UV 起始偏移 = Y stride × 高。
    /// </summary>
    private void CopyPixelsNv12(IntPtr src, int length)
    {
        var w = _width;
        var h = _height;
        var yStride = _srcStride < 0 ? -_srcStride : _srcStride;
        if (_frameBuffer == null)
        {
            return;
        }

        if (_rawBuffer == null || _rawBuffer.Length < length)
        {
            _rawBuffer = new byte[Math.Max(length, 1024)];
        }

        Marshal.Copy(src, _rawBuffer, 0, length);
        var uvBase = yStride * h;
        for (var y = 0; y < h; y++)
        {
            var yRow = _srcStride < 0 ? (h - 1 - y) * yStride : y * yStride;
            var uvRow = (_srcStride < 0 ? (h - 1 - y) / 2 : y / 2) * yStride;
            var dstBase = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var yy = _rawBuffer[yRow + x];
                var u = _rawBuffer[uvBase + uvRow + (x / 2) * 2];
                var v = _rawBuffer[uvBase + uvRow + (x / 2) * 2 + 1];
                var c = yy - 16;
                var d = u - 128;
                var e = v - 128;
                var di = dstBase + x * 4;
                _frameBuffer[di] = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
                _frameBuffer[di + 1] = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                _frameBuffer[di + 2] = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                _frameBuffer[di + 3] = 0xFF;
            }
        }
    }

    /// <summary>循环播放复位：FFmpeg 走 seek，MF 走取消再重选视频流。</summary>
    private bool TryRestart()
    {
        if (_ffmpeg != null)
        {
            return _ffmpeg.Restart();
        }

        try
        {
            if (_reader == null)
            {
                return false;
            }

            _reader.SetStreamSelection(_videoStreamIndex, 0);
            _reader.SetStreamSelection(_videoStreamIndex, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把本地路径转为 file:// URL（MFCreateSourceReaderFromURL 需要 URL）。</summary>
    private static string ToFileUrl(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }

        return "file:///" + p;
    }

    public void Dispose()
    {
        _running = false;
        _uiConsumed.Set(); // 解除后台线程可能阻塞的等待
        _worker?.Join(800);
        _worker = null;
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        if (_reader != null)
        {
            try
            {
                Marshal.ReleaseComObject(_reader);
            }
            catch
            {
                // 忽略释放错误
            }

            _reader = null;
        }

        if (_mfStarted)
        {
            try
            {
                MFShutdown();
            }
            catch
            {
                // 忽略关闭错误
            }

            _mfStarted = false;
        }
    }
}

/// <summary>一帧解码结果（像素为复用缓冲，仅回调执行期间有效）。</summary>
internal sealed class MfVideoFrame
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>每行字节数（= Width * 4，自上而下）。</summary>
    public int Stride => Width * 4;

    public MfVideoFrame(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }
}
