using System;
using System.Collections.Generic;
using System.IO;

namespace ClassIslandInjector;

/// <summary>
/// 音频峰值包络提取（PR 风格时间轴波形的数据源）。
/// <para>
/// 全曲解码一遍，按固定细粒度（<see cref="DetailBucketMs"/>）记录每桶的绝对峰值，
/// 结果按「路径 + 大小 + 修改时间」缓存在内存里；时间轴按实际像素宽度调用
/// <see cref="Resample"/> 聚合，缩放时间轴不会重复解码。
/// </para>
/// <para>
/// 提取耗时与音频长度成正比（几分钟的曲子约 1 秒），调用方应在后台线程执行后回 UI 刷波形。
/// 任何失败都返回 null（不画波形，不影响编辑）。
/// </para>
/// </summary>
internal static class AudioWaveform
{
    /// <summary>诊断日志路径（由 InjectorRuntime 设置，置空关闭日志）。</summary>
    public static string? LogPath { get; set; }

    private static void Log(string message) => DiagnosticLog.Write(LogPath, $"[waveform] {message}");

    /// <summary>细粒度桶宽（毫秒）。缓存按它切分，展示时再聚合到实际宽度。</summary>
    public const int DetailBucketMs = 20;

    /// <summary>细粒度桶宽（秒），按素材时间换算桶下标时用。</summary>
    public const double DetailBucketSeconds = DetailBucketMs / 1000.0;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, float[]> Cache = [];

    /// <summary>
    /// 取缓存的细粒度峰值包络（值域 0..1）。素材不存在 / 无音频轨 / 解码失败返回 null。
    /// 首次调用会解码全曲，耗时随音频长度增长。
    /// </summary>
    public static float[]? GetDetailPeaks(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var key = CacheKey(path);
        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var peaks = Extract(path);
        if (peaks != null && peaks.Length > 0)
        {
            lock (Sync)
            {
                Cache[key] = peaks;
            }

            Log($"已提取波形：{Path.GetFileName(path)}（{peaks.Length} 桶 × {DetailBucketMs}ms）");
        }

        return peaks;
    }

    /// <summary>把细粒度包络按像素宽度聚合（每桶取区间内最大值）。</summary>
    public static float[] Resample(float[] peaks, int buckets)
    {
        if (buckets <= 0)
        {
            return [];
        }

        var result = new float[buckets];
        if (peaks.Length == 0)
        {
            return result;
        }

        for (var i = 0; i < buckets; i++)
        {
            var start = (int)((long)i * peaks.Length / buckets);
            var end = (int)((long)(i + 1) * peaks.Length / buckets);
            if (end <= start)
            {
                end = start + 1;
            }

            var max = 0f;
            for (var j = start; j < end && j < peaks.Length; j++)
            {
                if (peaks[j] > max)
                {
                    max = peaks[j];
                }
            }

            result[i] = max;
        }

        return result;
    }

    /// <summary>清空缓存（素材被替换、或需要重新提取时调用）。</summary>
    public static void Invalidate(string? path = null)
    {
        lock (Sync)
        {
            if (string.IsNullOrEmpty(path))
            {
                Cache.Clear();
                return;
            }

            foreach (var key in new List<string>(Cache.Keys))
            {
                if (key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                {
                    Cache.Remove(key);
                }
            }
        }
    }

    private static string CacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }

    /// <summary>全曲解码一遍填细粒度桶。</summary>
    private static float[]? Extract(string path)
    {
        try
        {
            using var decoder = new FFmpegAudioDecoder();
            if (!decoder.Open(path))
            {
                return null;
            }

            var bucketBytes = FFmpegAudioDecoder.OutSampleRate * DetailBucketMs / 1000
                              * FFmpegAudioDecoder.OutBytesPerSample;
            var buffer = new byte[Math.Max(bucketBytes,
                FFmpegAudioDecoder.OutSampleRate * FFmpegAudioDecoder.OutBytesPerSample)];
            var peaks = new List<float>(8192);

            var bucketPeak = 0;
            var pendingBytes = 0;
            var guard = 0;
            int read;
            while ((read = decoder.ReadPcm(buffer, 0, buffer.Length)) > 0 && guard++ < 200000)
            {
                for (var i = 0; i + 1 < read; i += 2)
                {
                    var magnitude = Math.Abs((int)(short)(buffer[i] | (buffer[i + 1] << 8)));
                    if (magnitude > bucketPeak)
                    {
                        bucketPeak = magnitude;
                    }
                }

                pendingBytes += read;
                while (pendingBytes >= bucketBytes)
                {
                    peaks.Add(Math.Clamp(bucketPeak / 32768f, 0f, 1f));
                    bucketPeak = 0;
                    pendingBytes -= bucketBytes;
                }
            }

            if (pendingBytes > 0)
            {
                peaks.Add(Math.Clamp(bucketPeak / 32768f, 0f, 1f));
            }

            return peaks.ToArray();
        }
        catch (Exception ex)
        {
            Log($"波形提取失败（{Path.GetFileName(path)}）：{ex.Message}");
            return null;
        }
    }
}
