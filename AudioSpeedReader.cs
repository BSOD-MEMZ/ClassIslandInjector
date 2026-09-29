using System;

namespace ClassIslandInjector;

/// <summary>
/// 变速读源：按播放倍率把源 PCM 交给混音器。统一 48kHz / 立体声 / s16（与
/// <see cref="FFmpegAudioDecoder"/> 输出一致）。
/// <para>两种模式（由片段的 <see cref="VideoClip.PreservePitch"/> 决定）：</para>
/// <list type="bullet">
/// <item><b>变调（默认）</b>：线性插值重采样 —— 快放音调变高、慢放变低（磁带效果），CPU 极低。</item>
/// <item><b>保持音调</b>：SOLA 时间伸缩（30ms 窗 / 15ms 跳 / ±1ms 相关搜索）—— 原调，只是变快变慢。</item>
/// </list>
/// <para>
/// SOLA 的关键：相邻输出窗重叠 15ms，重叠段要能**回改已经产出但还没交付的尾部**，
/// 所以输出缓冲始终扣住最后 15ms 不交付（产生 15ms 额外延迟，人耳无感）。
/// </para>
/// </summary>
internal sealed class AudioSpeedReader
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    /// <summary>一帧（含全部声道）的字节数：s16 × 2ch。</summary>
    public const int BytesPerFrame = 4;

    // ---- SOLA 参数（帧数；1 帧 = 1/48000 秒） ----
    /// <summary>窗口长度 = 30ms。</summary>
    private const int SolaWindow = SampleRate * 30 / 1000;
    /// <summary>输出跳长 = 15ms（每步产出）。</summary>
    private const int SolaHopOut = SolaWindow / 2;
    /// <summary>重叠长度 = 15ms（= 窗口 - 跳长）。</summary>
    private const int SolaOverlap = SolaWindow - SolaHopOut;
    /// <summary>相关搜索半范围 = 1ms。</summary>
    private const int SolaSearch = SampleRate / 1000;

    /// <summary>从解码器拉 PCM 的委托（返回实际字节数，0 = 源结束）。</summary>
    private readonly Func<byte[], int, int, int> _readPcm;

    /// <summary>已消耗的源帧数（整数帧）——调用方用它校正音画漂移。</summary>
    public long SourceFramesConsumed { get; private set; }

    private bool _lastPreservePitch;
    private double _lastSpeed = double.NaN;

    // ---- 线性插值（变调）模式 ----
    private byte[] _linearBuf = new byte[BytesPerFrame * 16384];
    private int _linearFrames;
    private long _linearBase;
    private double _linearPos;

    // ---- SOLA（保持音调）模式 ----
    private byte[] _solaIn = new byte[BytesPerFrame * 16384];
    private int _solaInFrames;
    private long _solaInBase;
    private double _solaInPos;
    private byte[] _solaOut = new byte[BytesPerFrame * 4096];
    private int _solaOutStart;
    private int _solaOutEnd;
    private bool _solaEof;
    /// <summary>
    /// 已**交付**的源进度（帧，小数累加 = 交付帧数 × 速度）。
    /// 不能用分析窗口位置：SOLA 为了凑够一窗会提前读入（并扣住 15ms 不交付），
    /// 报分析位置会让调用方以为音频跑到了前面 → 每块都触发重定位（听感上就是断续）。
    /// </summary>
    private double _solaConsumed;

    public AudioSpeedReader(Func<byte[], int, int, int> readPcm) => _readPcm = readPcm;

    /// <summary>seek 之后调用：丢弃内部缓冲与位置（源位置从 0 重新按解码器实际进度计数）。</summary>
    public void Reset()
    {
        _linearFrames = 0;
        _linearBase = 0;
        _linearPos = 0;
        _solaInFrames = 0;
        _solaInBase = 0;
        _solaInPos = 0;
        _solaOutStart = 0;
        _solaOutEnd = 0;
        _solaEof = false;
        _solaConsumed = 0;
        SourceFramesConsumed = 0;
        _lastSpeed = double.NaN;
    }

    /// <summary>
    /// 读 <paramref name="frames"/> 帧输出到 <paramref name="destination"/>（从 0 开始写），
    /// 源按 <paramref name="speed"/> 倍率前进。返回实际写入帧数（源结束会少于请求）。
    /// </summary>
    public int ReadFrames(byte[] destination, int frames, double speed, bool preservePitch)
    {
        if (frames <= 0)
        {
            return 0;
        }

        speed = Math.Clamp(speed, 0.1, 8);
        if (Math.Abs(speed - 1) < 0.0001 && !preservePitch)
        {
            return ReadDirect(destination, frames); // 原速不变调：直接透传（最省 CPU）
        }

        if (!_lastSpeed.Equals(speed) || _lastPreservePitch != preservePitch)
        {
            DropBuffers();
            _lastSpeed = speed;
            _lastPreservePitch = preservePitch;
        }

        return preservePitch
            ? ReadSola(destination, frames, speed)
            : ReadLinear(destination, frames, speed);
    }

    /// <summary>原速透传。</summary>
    private int ReadDirect(byte[] destination, int frames)
    {
        var got = _readPcm(destination, 0, frames * BytesPerFrame);
        var gotFrames = got / BytesPerFrame;
        SourceFramesConsumed += gotFrames;
        return gotFrames;
    }

    // ============================ 线性插值（变调） ============================

    private int ReadLinear(byte[] destination, int frames, double speed)
    {
        FillLinear((int)Math.Ceiling(frames * speed) + 2);

        var written = 0;
        var pos = _linearPos;
        for (var f = 0; f < frames; f++)
        {
            var i = (int)pos;
            if (i + 1 >= _linearFrames)
            {
                break; // 源到头
            }

            var frac = pos - i;
            var a = i * BytesPerFrame;
            var b = a + BytesPerFrame;
            var o = written * BytesPerFrame;
            for (var c = 0; c < Channels; c++)
            {
                var s0 = (short)(_linearBuf[a + c * 2] | (_linearBuf[a + c * 2 + 1] << 8));
                var s1 = (short)(_linearBuf[b + c * 2] | (_linearBuf[b + c * 2 + 1] << 8));
                var v = Math.Clamp((int)Math.Round(s0 + (s1 - s0) * frac), short.MinValue, short.MaxValue);
                destination[o + c * 2] = (byte)(v & 0xFF);
                destination[o + c * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }

            written++;
            pos += speed;
        }

        _linearPos = pos;
        _linearBase += Compact(_linearBuf, ref _linearFrames, ref _linearPos, 4096, (int)_linearPos);
        SourceFramesConsumed = _linearBase + (long)_linearPos;
        return written;
    }

    private void FillLinear(int frames)
    {
        while (frames > _linearFrames - (int)_linearPos)
        {
            _linearBase += Compact(_linearBuf, ref _linearFrames, ref _linearPos, frames, (int)_linearPos);
            var room = _linearBuf.Length / BytesPerFrame - _linearFrames;
            if (room <= 0)
            {
                Array.Resize(ref _linearBuf, (_linearFrames + frames + 4096) * BytesPerFrame);
                room = _linearBuf.Length / BytesPerFrame - _linearFrames;
            }

            var got = _readPcm(_linearBuf, _linearFrames * BytesPerFrame, room * BytesPerFrame);
            if (got <= 0)
            {
                return;
            }

            _linearFrames += got / BytesPerFrame;
        }
    }

    // ============================ SOLA（保持音调） ============================

    private int ReadSola(byte[] destination, int frames, double speed)
    {
        var written = 0;
        var guard = 0;
        while (written < frames && guard++ < 4096)
        {
            // 可交付帧数：扣住最后 SolaOverlap 帧（下一步要回改这一段）。
            var hold = _solaEof ? 0 : SolaOverlap;
            var avail = _solaOutEnd - _solaOutStart - hold;
            if (avail <= 0)
            {
                if (!SolaStep(speed))
                {
                    _solaEof = true;
                    avail = _solaOutEnd - _solaOutStart;
                    if (avail <= 0)
                    {
                        break; // 源彻底结束
                    }
                }
                else
                {
                    continue;
                }
            }

            var take = Math.Min(frames - written, avail);
            Buffer.BlockCopy(_solaOut, _solaOutStart * BytesPerFrame, destination, written * BytesPerFrame,
                take * BytesPerFrame);
            written += take;
            _solaOutStart += take;
            _solaConsumed += take * speed;
            CompactSolaOut();
        }

        SourceFramesConsumed = (long)_solaConsumed;
        return written;
    }

    /// <summary>跑一步 SOLA：相关搜索 → 重叠段交叉淡化 → 追加新窗口尾部。返回 false = 源结束。</summary>
    private bool SolaStep(double speed)
    {
        if (!SolaEnsure())
        {
            return false;
        }

        var ideal = (int)Math.Round(_solaInPos);
        var offset = 0;
        if (_solaOutEnd > 0)
        {
            offset = SolaSearchOffset(ideal);
        }

        var pos = Math.Clamp(ideal + offset, 0, Math.Max(0, _solaInFrames - SolaWindow - 1));
        var needFrames = _solaOutEnd == 0 ? SolaHopOut : _solaOutEnd + SolaHopOut;
        if (needFrames * BytesPerFrame > _solaOut.Length)
        {
            Array.Resize(ref _solaOut, needFrames * BytesPerFrame + BytesPerFrame * 4096);
        }

        if (_solaOutEnd == 0)
        {
            Buffer.BlockCopy(_solaIn, pos * BytesPerFrame, _solaOut, 0, SolaHopOut * BytesPerFrame);
            _solaOutEnd = SolaHopOut;
        }
        else
        {
            // 重叠段（输出尾部 × 渐出 + 新窗口头部 × 渐入）。
            var ovStart = _solaOutEnd - SolaOverlap;
            for (var i = 0; i < SolaOverlap; i++)
            {
                var fadeIn = (double)i / SolaOverlap;
                var fadeOut = 1 - fadeIn;
                var srcIdx = (pos + i) * BytesPerFrame;
                var dstIdx = (ovStart + i) * BytesPerFrame;
                for (var c = 0; c < Channels; c++)
                {
                    var s = (short)(_solaIn[srcIdx + c * 2] | (_solaIn[srcIdx + c * 2 + 1] << 8));
                    var t = (short)(_solaOut[dstIdx + c * 2] | (_solaOut[dstIdx + c * 2 + 1] << 8));
                    var v = Math.Clamp((int)Math.Round(t * fadeOut + s * fadeIn), short.MinValue, short.MaxValue);
                    _solaOut[dstIdx + c * 2] = (byte)(v & 0xFF);
                    _solaOut[dstIdx + c * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
            }

            // 追加重叠段之后的部分（Hop 帧）。
            Buffer.BlockCopy(_solaIn, (pos + SolaOverlap) * BytesPerFrame, _solaOut,
                _solaOutEnd * BytesPerFrame, SolaHopOut * BytesPerFrame);
            _solaOutEnd += SolaHopOut;
        }

        _solaInPos += Math.Max(1, (int)Math.Round(SolaHopOut * speed));
        _solaInBase += Compact(_solaIn, ref _solaInFrames, ref _solaInPos, 8192, (int)_solaInPos);
        return true;
    }

    /// <summary>在 ±SolaSearch 内找与输出尾部最相似的新窗口起点（左声道隔点取样，省 CPU）。</summary>
    private int SolaSearchOffset(int ideal)
    {
        var best = long.MinValue;
        var bestOffset = 0;
        var ovStart = _solaOutEnd - SolaOverlap;
        for (var d = -SolaSearch; d <= SolaSearch; d += 2)
        {
            var start = ideal + d;
            if (start < 0 || start + SolaOverlap >= _solaInFrames)
            {
                continue;
            }

            long sum = 0;
            for (var i = 0; i < SolaOverlap; i += 4)
            {
                var si = (start + i) * BytesPerFrame;
                var ti = (ovStart + i) * BytesPerFrame;
                var s = (short)(_solaIn[si] | (_solaIn[si + 1] << 8));
                var t = (short)(_solaOut[ti] | (_solaOut[ti + 1] << 8));
                sum += (long)s * t;
            }

            if (sum > best)
            {
                best = sum;
                bestOffset = d;
            }
        }

        return bestOffset;
    }

    /// <summary>确保输入缓冲里从 <see cref="_solaInPos"/> 起至少有一个窗口 + 搜索余量。</summary>
    private bool SolaEnsure()
    {
        var need = SolaWindow + SolaSearch + 2;
        while (need > _solaInFrames - (int)_solaInPos)
        {
            _solaInBase += Compact(_solaIn, ref _solaInFrames, ref _solaInPos, need, (int)_solaInPos);
            var room = _solaIn.Length / BytesPerFrame - _solaInFrames;
            if (room <= 0)
            {
                Array.Resize(ref _solaIn, (_solaInFrames + need + 8192) * BytesPerFrame);
                room = _solaIn.Length / BytesPerFrame - _solaInFrames;
            }

            var got = _readPcm(_solaIn, _solaInFrames * BytesPerFrame, room * BytesPerFrame);
            if (got <= 0)
            {
                return _solaInFrames - (int)_solaInPos >= SolaWindow; // 末尾不足一窗：还能挤出最后一窗
            }

            _solaInFrames += got / BytesPerFrame;
        }

        return true;
    }

    /// <summary>丢弃已读前缀（超过 <paramref name="keep"/>/阈值才做，减少搬运）。返回丢弃的帧数。</summary>
    private static int Compact(byte[] buffer, ref int frames, ref double pos, int need, int readPos)
    {
        var drop = readPos;
        if (drop <= 8192 && frames - drop >= need)
        {
            return 0;
        }

        drop = Math.Max(0, Math.Min(drop, frames));
        if (drop > 0)
        {
            var keep = frames - drop;
            if (keep > 0)
            {
                Buffer.BlockCopy(buffer, drop * BytesPerFrame, buffer, 0, keep * BytesPerFrame);
            }

            frames = keep;
            pos -= drop;
        }

        return drop;
    }

    /// <summary>SOLA 输出缓冲：丢掉已交付的前缀，但保留末尾重叠段。</summary>
    private void CompactSolaOut()
    {
        var keepFrom = Math.Max(0, _solaOutEnd - SolaOverlap);
        if (keepFrom <= 4096)
        {
            return;
        }

        Buffer.BlockCopy(_solaOut, keepFrom * BytesPerFrame, _solaOut, 0, (_solaOutEnd - keepFrom) * BytesPerFrame);
        _solaOutEnd -= keepFrom;
        _solaOutStart = Math.Max(0, _solaOutStart - keepFrom);
    }

    /// <summary>倍率/模式切换时丢缓冲（源位置按解码器实际进度继续，最多损失一个窗的音频）。</summary>
    private void DropBuffers()
    {
        _linearFrames = 0;
        _linearPos = 0;
        _linearBase = SourceFramesConsumed;
        _solaInFrames = 0;
        _solaInPos = 0;
        _solaInBase = SourceFramesConsumed;
        _solaOutStart = 0;
        _solaOutEnd = 0;
        _solaEof = false;
        _solaConsumed = 0;
    }
}
