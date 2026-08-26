using System.Runtime.InteropServices;

namespace VideoProbe;

/// <summary>
/// Media Foundation Source Reader 探针：全部用原生指针 + vtable 手动调用，
/// 完全绕开 .NET 的 ComImport marshaller，用于定位 SetCurrentMediaType 返回
/// E_INVALIDARG / 崩溃的问题。用法：VideoProbe.exe <视频路径>
/// </summary>
internal static class Program
{
    // ---- GUID 常量 ----
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_SUBTYPE = new("76A5AE91-13FA-4D14-8D91-3384233D92E9");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x10;
    private const ulong MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_NOSOCK = 0x1;
    private const uint MFVideoInterlace_Progressive = 2;

    // ---- P/Invoke（全部用 IntPtr，避免 ComImport 包装）----
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(ulong version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IntPtr reader);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    private const uint COINIT_MULTITHREADED = 0x0;

    // ---- vtable 委托 ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStreamSelectionD(IntPtr self, uint index, out int selected);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetStreamSelectionD(IntPtr self, uint index, int selected);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNativeMediaTypeD(IntPtr self, uint index, uint position, out IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentMediaTypeD(IntPtr self, uint index, out IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeD(IntPtr self, uint index, IntPtr type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeByIndexD(IntPtr self, uint index, uint position);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FlushD(IntPtr self, uint index);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadSampleD(IntPtr self, uint index, uint control, out uint flags, out long timestamp, out IntPtr sample, out uint actual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidD(IntPtr self, ref Guid key, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUint32D(IntPtr self, ref Guid key, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUint64D(IntPtr self, ref Guid key, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetGuidD(IntPtr self, ref Guid key, ref Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUint32D(IntPtr self, ref Guid key, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUint64D(IntPtr self, ref Guid key, ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferD(IntPtr self, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockD(IntPtr self, out IntPtr buffer, out uint maxLength, out uint currentLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockD(IntPtr self);

    // ---- vtable 槽位 ----
    // IMFSourceReader: GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5,
    //   GetCurrentMediaType=6, SetCurrentMediaType=7, SetCurrentMediaTypeByIndex=8, Flush=9, ReadSample=10
    // IMFAttributes: GetGUID=10, GetUINT32=7, GetUINT64=8, SetGUID=24, SetUINT32=21, SetUINT64=22
    // IMFSample: ConvertToContiguousBuffer=41
    // IMFMediaBuffer: Lock=3, Unlock=4

    private static T GetVtbl<T>(IntPtr obj, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(obj);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static string Hr(int hr) => hr >= 0 ? $"0x{hr:X8}(OK)" : $"0x{hr:X8}(0x{(uint)(-hr):X8})";

    private static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : @"D:\BSOD-MEMZ\为你的课表注入活力——ClassIslandInjector样式注入器发布！.mp4";
        Console.WriteLine($"=== MF 探针 路径={path} ===");
        if (!File.Exists(path))
        {
            Console.WriteLine("文件不存在！");
            return 1;
        }

        var coHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        Console.WriteLine($"CoInitializeEx(MTA) = {Hr(coHr)}");

        var hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCK);
        Console.WriteLine($"MFStartup = {Hr(hr)}");
        if (hr < 0)
        {
            return 1;
        }

        try
        {
            // ---- 创建 attrs ----
            hr = MFCreateAttributes(out var attrs, 2);
            Console.WriteLine($"MFCreateAttributes = {Hr(hr)}");
            if (hr < 0)
            {
                return 1;
            }

            var gVp = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            var setUint32 = GetVtbl<SetUint32D>(attrs, 21);
            var hvp = setUint32(attrs, ref gVp, 1);
            Console.WriteLine($"attrs.SetUINT32(ENABLE_VIDEO_PROCESSING) = {Hr(hvp)}");

            // ---- 创建 reader ----
            var url = "file:///" + path.Replace('\\', '/');
            hr = MFCreateSourceReaderFromURL(url, attrs, out var readerPtr);
            Console.WriteLine($"MFCreateSourceReaderFromURL = {Hr(hr)}");
            if (hr < 0 || readerPtr == IntPtr.Zero)
            {
                return 1;
            }

            // ---- 枚举所有流 / 类型 ----
            var getStreamSel = GetVtbl<GetStreamSelectionD>(readerPtr, 3);
            var getNative = GetVtbl<GetNativeMediaTypeD>(readerPtr, 5);
            var getGuid = GetVtbl<GetGuidD>(readerPtr, 10);
            var getUint64 = GetVtbl<GetUint64D>(readerPtr, 8);
            uint videoStream = uint.MaxValue;
            Console.WriteLine("--- 枚举流/类型（逐步） ---");
            for (uint s = 0; s < 4; s++)
            {
                var hsSel = getStreamSel(readerPtr, s, out var sel);
                Console.WriteLine($"流 {s} GetStreamSelection = {Hr(hsSel)} sel={sel}");
                for (uint t = 0; t < 4; t++)
                {
                    var nh = getNative(readerPtr, s, t, out var nt);
                    if (nh < 0 || nt == IntPtr.Zero)
                    {
                        Console.WriteLine($"  类型[{t}] GetNativeMediaType = {Hr(nh)}（结束）");
                        break;
                    }

                    Console.WriteLine($"  类型[{t}] GetNativeMediaType OK");
                    // 注意：GetGUID/GetUINT64 必须从媒体类型对象自身的 vtable 取，不能复用 reader 的！
                    var getGuidOnType = GetVtbl<GetGuidD>(nt, 10);
                    var getUint64OnType = GetVtbl<GetUint64D>(nt, 8);
                    var gMajor = MF_MT_MAJOR_TYPE;
                    var hMajor = getGuidOnType(nt, ref gMajor, out var major);
                    Console.WriteLine($"    MAJOR = {Hr(hMajor)} {MajorName(major)}");
                    var gSub = MF_MT_SUBTYPE;
                    var hSub = getGuidOnType(nt, ref gSub, out var sub);
                    Console.WriteLine($"    SUBTYPE = {Hr(hSub)} {SubName(sub)}");
                    var gSize = MF_MT_FRAME_SIZE;
                    var hSize = getUint64OnType(nt, ref gSize, out var size);
                    Console.WriteLine($"    SIZE = {Hr(hSize)} {(hSize >= 0 ? $"{size & 0xFFFFFFFF}x{size >> 32}" : "-")}");
                    Marshal.Release(nt);
                    if (major == MFMediaType_Video && videoStream == uint.MaxValue)
                    {
                        videoStream = s;
                    }
                }
            }

            if (videoStream == uint.MaxValue)
            {
                Console.WriteLine("未找到视频流！");
                return 1;
            }

            Console.WriteLine($"视频流 = {videoStream}");
            var setCur = GetVtbl<SetCurrentMediaTypeD>(readerPtr, 7);
            var setByIndex = GetVtbl<SetCurrentMediaTypeByIndexD>(readerPtr, 8);
            var flush = GetVtbl<FlushD>(readerPtr, 9);

            // ---- 对照实验：不带 attrs（无视频处理）创建第二个 reader，直接 ReadSample ----
            Console.WriteLine("--- 对照：无 attrs reader ReadSample ---");
            var hr2 = MFCreateSourceReaderFromURL(url, IntPtr.Zero, out var reader2);
            Console.WriteLine($"MFCreateSourceReaderFromURL(无attrs) = {Hr(hr2)}");
            if (hr2 >= 0 && reader2 != IntPtr.Zero)
            {
                var rs2 = GetVtbl<ReadSampleD>(reader2, 10);
                var gss2 = GetVtbl<GetStreamSelectionD>(reader2, 3);
                var gGetNative2 = GetVtbl<GetNativeMediaTypeD>(reader2, 5);
                var getGuid2 = GetVtbl<GetGuidD>(reader2, 10);
                // 找视频流
                uint vs2 = uint.MaxValue;
                for (uint s = 0; s < 4; s++)
                {
                    var nh = gGetNative2(reader2, s, 0, out var nt);
                    if (nh < 0 || nt == IntPtr.Zero)
                    {
                        break;
                    }

                    var getGuidOnType = GetVtbl<GetGuidD>(nt, 10);
                    var gM2 = MF_MT_MAJOR_TYPE;
                    var hM2 = getGuidOnType(nt, ref gM2, out var major2);
                    Marshal.Release(nt);
                    if (hM2 >= 0 && major2 == MFMediaType_Video)
                    {
                        vs2 = s;
                        break;
                    }
                }

                Console.WriteLine($"无 attrs 视频流 = {vs2}");
                for (var i = 0; i < 8; i++)
                {
                    var hrR = rs2(reader2, vs2, 0, out var f2, out _, out var s2, out _);
                    Console.WriteLine($"无attrs ReadSample[{i}] = {Hr(hrR)} flags=0x{f2:X8} sample={(s2 == IntPtr.Zero ? "null" : "有")}");
                    if (hrR < 0)
                    {
                        break;
                    }

                    if (s2 != IntPtr.Zero)
                    {
                        Marshal.Release(s2);
                    }
                }

                Marshal.Release(reader2);
            }

            // ---- 验证 vtable 槽位：SetStreamSelection(槽4) 应影响 GetStreamSelection(槽3) ----
            Console.WriteLine("--- vtable 槽位验证 ---");
            var setStreamSel = GetVtbl<SetStreamSelectionD>(readerPtr, 4);
            var hs0 = setStreamSel(readerPtr, videoStream, 0);
            getStreamSel(readerPtr, videoStream, out var selAfterDisable);
            var hs1 = setStreamSel(readerPtr, videoStream, 1);
            getStreamSel(readerPtr, videoStream, out var selAfterEnable);
            Console.WriteLine($"SetStreamSelection({videoStream},0)={Hr(hs0)} → GetStreamSelection={selAfterDisable}；SetStreamSelection(,1)={Hr(hs1)} → GetStreamSelection={selAfterEnable}");

            // ---- 实验：音频流 ReadSample（对比视频流，判断是否仅视频解码器异常）----
            Console.WriteLine("--- 音频流（流0）ReadSample ---");
            var readSample = GetVtbl<ReadSampleD>(readerPtr, 10);
            for (var i = 0; i < 5; i++)
            {
                var hrA = readSample(readerPtr, 0, 0, out var fA, out _, out var sA, out _);
                Console.WriteLine($"音频 ReadSample[{i}] = {Hr(hrA)} flags=0x{fA:X8} sample={(sA == IntPtr.Zero ? "null" : "有")}");
                if (hrA < 0)
                {
                    break;
                }

                if (sA != IntPtr.Zero)
                {
                    Marshal.Release(sA);
                }
            }

            // ---- 实验：不设置输出类型，直接用默认输出 ReadSample ----
            Console.WriteLine("--- 视频流 ReadSample（默认输出，跳过 SetCurrentMediaType） ---");
            var getCur = GetVtbl<GetCurrentMediaTypeD>(readerPtr, 6);
            var hCur = getCur(readerPtr, videoStream, out var curType);
            Console.WriteLine($"GetCurrentMediaType({videoStream}) = {Hr(hCur)}");
            if (hCur >= 0 && curType != IntPtr.Zero)
            {
                var getGuidOnType = GetVtbl<GetGuidD>(curType, 10);
                var getUint64OnType = GetVtbl<GetUint64D>(curType, 8);
                var getUint32OnType = GetVtbl<GetUint32D>(curType, 7);
                var gSub2 = MF_MT_SUBTYPE;
                var hs2 = getGuidOnType(curType, ref gSub2, out var sub2);
                Console.WriteLine($"  当前输出 SUBTYPE({Hr(hs2)})={SubName(sub2)}");
                var gSize2 = MF_MT_FRAME_SIZE;
                var hz2 = getUint64OnType(curType, ref gSize2, out var size2);
                Console.WriteLine($"  当前输出 SIZE({Hr(hz2)})={(hz2 >= 0 ? $"{size2 & 0xFFFFFFFF}x{size2 >> 32}" : "-")}");
                var gStride = MF_MT_DEFAULT_STRIDE;
                var hstride = getUint32OnType(curType, ref gStride, out var stride2);
                Console.WriteLine($"  当前输出 STRIDE({Hr(hstride)})={(int)stride2}");
                Marshal.Release(curType);
            }

            for (var i = 0; i < 12; i++)
            {
                var hrR = readSample(readerPtr, videoStream, 0, out var flags, out var ts, out var sample, out _);
                Console.WriteLine($"ReadSample[{i}] = {Hr(hrR)} flags=0x{flags:X8} sample={(sample == IntPtr.Zero ? "null" : "有")}");
                if (hrR < 0)
                {
                    break;
                }

                if (sample == IntPtr.Zero)
                {
                    continue; // 无样本继续拉
                }

                var convert = GetVtbl<ConvertToContiguousBufferD>(sample, 41);
                var hc = convert(sample, out var buf);
                Console.WriteLine($"  ConvertToContiguousBuffer = {Hr(hc)}");
                if (hc >= 0 && buf != IntPtr.Zero)
                {
                    var lockBuf = GetVtbl<LockD>(buf, 3);
                    var hlk = lockBuf(buf, out var ptr, out var maxLen, out var curLen);
                    Console.WriteLine($"  Buffer.Lock = {Hr(hlk)} len={curLen} max={maxLen}");
                    if (hlk >= 0)
                    {
                        var unlock = GetVtbl<UnlockD>(buf, 4);
                        unlock(buf);
                    }

                    Marshal.Release(buf);
                }

                Marshal.Release(sample);
            }

            Console.WriteLine("=== 完成 ===");
        }
        finally
        {
            MFShutdown();
            CoUninitialize();
        }

        return 0;
    }

    private static int TrySetOutputType(IntPtr reader, SetCurrentMediaTypeD setCur, uint stream, Guid subtype, long w, long h)
    {
        var hr = MFCreateMediaType(out var mt);
        if (hr < 0 || mt == IntPtr.Zero)
        {
            return hr;
        }

        try
        {
            var setGuid = GetVtbl<SetGuidD>(mt, 24);
            var setUint32 = GetVtbl<SetUint32D>(mt, 21);
            var setUint64 = GetVtbl<SetUint64D>(mt, 22);
            var gMajor = MF_MT_MAJOR_TYPE;
            var gVideo = MFMediaType_Video;
            var gSub = MF_MT_SUBTYPE;
            var gSubtype = subtype;
            var gInterlace = MF_MT_INTERLACE_MODE;
            var gSize = MF_MT_FRAME_SIZE;
            setGuid(mt, ref gMajor, ref gVideo);
            setGuid(mt, ref gSub, ref gSubtype);
            setUint32(mt, ref gInterlace, MFVideoInterlace_Progressive);
            if (w > 0 && h > 0)
            {
                setUint64(mt, ref gSize, ((ulong)h << 32) | (ulong)w);
            }

            // 读回验证
            var getGuid = GetVtbl<GetGuidD>(mt, 10);
            var gM = MF_MT_MAJOR_TYPE;
            var gS = MF_MT_SUBTYPE;
            var hM = getGuid(mt, ref gM, out var major);
            var hS = getGuid(mt, ref gS, out var sub);
            Console.WriteLine($"  [读回] MAJOR({Hr(hM)})={MajorName(major)} SUBTYPE({Hr(hS)})={SubName(sub)}");

            return setCur(reader, stream, mt);
        }
        finally
        {
            Marshal.Release(mt);
        }
    }

    private static string MajorName(Guid g) => g == MFMediaType_Video ? "Video" : g == MFMediaType_Audio ? "Audio" : g.ToString();

    private static string SubName(Guid g) =>
        g == MFVideoFormat_RGB32 ? "RGB32" :
        g == MFVideoFormat_NV12 ? "NV12" :
        g == MFVideoFormat_YUY2 ? "YUY2" :
        g == Guid.Empty ? "(空)" : g.ToString();
}
