using System.Collections.Concurrent;
using System.Diagnostics;
using FFmpeg.AutoGen;

namespace ClassIslandInjector;

/// <summary>
/// 预览播放压力测试（无 GUI、无肉眼）：把「卡顿 / 音画不同步」量成数字。
/// <para>
/// 用法：RaceProbe.exe --stress [视频] [音频] [FFmpeg库目录]
/// </para>
/// <para>
/// 关键建模：**UI 的代价必须放在另一条线程上**（真实链路是「播放线程 Post → UI 线程拷贝+重绘」
/// → 播放线程等 Consumed），把忙等压在播放线程里等于测错东西。
/// </para>
/// <list type="bullet">
/// <item>A. 音频块预算：离线按 20ms 块取混音，量单块耗时（&gt;20ms 即声卡回调欠载 = 听感卡顿）。</item>
/// <item>B. 投递节奏：真实 <see cref="VideoProjectPlayer"/> + 独立「UI」线程，量每轨帧间隔的最大值。</item>
/// <item>C. 边播边 seek：每 1.2s 跳一次，量 seek 后到首帧的延迟。</item>
/// <item>D. 播放中改倍速：改 Speed + RefreshClips，看画面是否还在动。</item>
/// </list>
/// 每项都跑「空闲」与「加载 CPU（模拟开浏览器）」对照。
/// </summary>
internal static class Stress
{
    private static readonly List<Thread> LoadThreads = [];
    private static volatile bool _loadRunning;
    private static readonly byte[] Scratch = new byte[800 * 592 * 4];

    public static int Run(string ffmpegRoot, string videoPath, string audioPath)
    {
        ffmpeg.RootPath = ffmpegRoot;
        _ = ffmpeg.avcodec_version();

        Console.WriteLine($"核数={Environment.ProcessorCount}");
        var p = BuildProject(videoPath, audioPath);
        Console.WriteLine($"工程：3 视频轨（同源 {Path.GetFileName(videoPath)}）+ 1 音频轨，总长 {p.Duration:0.##}s");
        Console.WriteLine("预览参数按编辑器默认：maxDimension=800, targetFps=24\n");

        var ok = true;
        ok &= Phase0(videoPath);
        ok &= PhaseA(p);
        ok &= PhaseB(videoPath, audioPath, tracks: 1, dim: 800, jankMs: 0, loaded: false);
        ok &= PhaseB(videoPath, audioPath, tracks: 1, dim: 800, jankMs: 0, loaded: true);
        ok &= PhaseB(videoPath, audioPath, tracks: 2, dim: 640, jankMs: 0, loaded: false);
        ok &= PhaseB(videoPath, audioPath, tracks: 3, dim: 800, jankMs: 0, loaded: false);
        ok &= PhaseB(videoPath, audioPath, tracks: 3, dim: 400, jankMs: 0, loaded: false);
        ok &= PhaseB(videoPath, audioPath, tracks: 3, dim: 800, jankMs: 60, loaded: true);
        ok &= PhaseF(videoPath, audioPath);
        ok &= PhaseE(videoPath, audioPath);
        ok &= PhaseC(videoPath, audioPath, loaded: true);
        ok &= PhaseD(videoPath, audioPath, loaded: true);

        StopLoad();
        Console.WriteLine(ok ? "== 结束：见上面各阶段数字 ==" : "== 有退化项，见上面 ⚠ 行 ==");
        return ok ? 0 : 1;
    }

    // ---------------- 0. 素材参数与 seek 成本 ----------------
    private static bool Phase0(string videoPath)
    {
        Console.WriteLine("--- 0. 素材参数 / seek 成本（决定「追赶」该用顺序解码还是跳转）---");
        var dec = new FFmpegVideoDecoder();
        if (!dec.Open(videoPath, 800))
        {
            Console.WriteLine("  打开失败");
            return false;
        }

        Console.WriteLine($"  源 {dec.SourceWidth}x{dec.SourceHeight} → 解码 {dec.OutputWidth}x{dec.OutputHeight}  "
                          + $"源帧率 {dec.SourceFps:0.###}fps  时长 {dec.Duration:0.##}s");

        // 顺序解码单帧成本
        var sw = new Stopwatch();
        var decode = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            sw.Restart();
            var okFrame = dec.ReadFrame(out _);
            sw.Stop();
            if (!okFrame)
            {
                break;
            }

            decode.Add(sw.Elapsed.TotalMilliseconds);
        }

        // 随机 seek 成本（av_seek_frame + 解到目标）
        var seek = new List<double>();
        var rnd = new Random(3);
        for (var i = 0; i < 10; i++)
        {
            sw.Restart();
            dec.SeekTo(rnd.NextDouble() * Math.Max(1, dec.Duration - 2));
            dec.ReadFrame(out _);
            sw.Stop();
            seek.Add(sw.Elapsed.TotalMilliseconds);
        }

        dec.Dispose();
        decode.Sort();
        seek.Sort();

        // 硬解对照：预览目前从不设 HardwareDecoder（全软解），而底图/渲染都用硬解。
        foreach (var hw in new[] { "auto", "d3d11va", "h264_qsv" })
        {
            var hd = new FFmpegVideoDecoder { HardwareDecoder = hw };
            if (!hd.Open(videoPath, 800))
            {
                Console.WriteLine($"  硬解[{hw}]：打不开");
                continue;
            }

            var hwTimes = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var sw2 = Stopwatch.StartNew();
                var okFrame = hd.ReadFrame(out _);
                sw2.Stop();
                if (!okFrame)
                {
                    break;
                }

                hwTimes.Add(sw2.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine($"  硬解[{hw}]：均值 {(hwTimes.Count > 0 ? hwTimes.Average() : 0):0.##}ms/帧"
                              + $"（软解 {decode.Average():0.##}ms）  解码器={hd.SourceFps:0.#}fps");
            hd.Dispose();
        }

        var dpf = decode.Count > 0 ? decode.Average() : 0;
        Console.WriteLine($"  顺序解一帧：均值 {dpf:0.##}ms（中位 {decode[decode.Count / 2]:0.##}ms）"
                          + $"  → 单轨 24fps 需 {dpf * 24:0}ms/s/轨");
        Console.WriteLine($"  随机 seek+解一帧：均值 {seek.Average():0.#}ms  最大 {seek[^1]:0.#}ms");
        Console.WriteLine();
        return true;
    }

    // ---------------- A. 音频块预算 ----------------
    private static bool PhaseA(VideoProject project)
    {
        Console.WriteLine("--- A. 音频：每 20ms 块的实际混音耗时（>20ms 即回调欠载，会听到咔哒/断续）---");
        var ok = true;
        foreach (var loaded in new[] { false, true })
        {
            if (loaded)
            {
                StartLoad();
            }

            var mixer = new ProjectAudioMixer();
            if (!mixer.StartOffline(project))
            {
                Console.WriteLine("  离线混音启动失败");
                return false;
            }

            var block = new byte[960 * FFmpegAudioDecoder.OutBytesPerSample];
            var times = new List<double>(600);
            var sw = new Stopwatch();
            for (var i = 0; i < 600; i++)
            {
                sw.Restart();
                mixer.ReadForRender(block, 0, block.Length);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            mixer.Dispose();
            StopLoad();
            times.Sort();
            var over = times.Count(t => t > 20);
            var bad = over > 0;
            ok &= !bad;
            Console.WriteLine($"  {(loaded ? "加载" : "空闲")}：均值 {times.Average():0.##}ms  p99 {times[(int)(times.Count * 0.99)]:0.##}ms  "
                              + $"最大 {times[^1]:0.##}ms  超 20ms 的块 {over}/600{(bad ? "   ⚠" : "")}");
        }

        Console.WriteLine();
        return ok;
    }

    // ---------------- B/C/D. 播放器 ----------------
    private static bool PhaseB(string videoPath, string audioPath, int tracks, int dim, int jankMs, bool loaded)
    {
        Console.WriteLine($"--- B. 投递节奏（{tracks} 轨 / 解码 {dim}px / UI 卡顿 {jankMs}ms / "
                          + $"{(loaded ? "加载 CPU" : "空闲")}）---");

        var ui = new UiSimulator(jankMs);
        var clock = new Stopwatch();
        var sent = 0;
        VideoProjectPlayer? player = null;
        player = new VideoProjectPlayer(BuildProject(videoPath, audioPath, tracks), dim, 24, (frame, _, track) =>
        {
            // 播放线程只做「把像素交给 UI」这件事（真实链路的 Post），代价由 UI 线程承担。
            Interlocked.Increment(ref sent);
            Buffer.BlockCopy(frame.Pixels, 0, Scratch, 0, Math.Min(frame.Pixels.Length, Scratch.Length));
            ui.Post(track, () => player?.MarkTrackConsumed(track));
        });

        if (loaded)
        {
            StartLoad();
        }

        clock.Start();
        player.Start();
        Thread.Sleep(8000);
        var playerTime = player.CurrentTime;
        var wall = clock.Elapsed.TotalSeconds;
        player.Stop();
        player.Dispose();
        StopLoad();
        clock.Stop();
        ui.Stop();

        var recorded = ui.Tracks.Sum(ui.Count);
        Console.WriteLine($"  [对账] 播放器回调 {sent} 次 / UI 侧记录 {recorded} 次"
                          + (sent == recorded ? "" : $"   ⚠ 差 {sent - recorded}（探针侧的丢失）"));

        var ok = true;
        foreach (var track in ui.Tracks.OrderBy(t => t))
        {
            var g = ui.Gaps(track);
            var max = g.Count > 0 ? g[^1] : 0;
            var p95 = g.Count > 0 ? g[(int)(g.Count * 0.95)] : 0;
            var mean = g.Count > 0 ? g.Average() : 0;
            // 判据：目标 24fps（间隔 41.7ms）。最大间隔 > 400ms 就是肉眼可见的「画面停住」。
            var stalled = max > 0.4;
            ok &= !stalled;
            Console.WriteLine($"  轨{track}：{ui.Count(track),4} 帧（{ui.Count(track) / 8.0,4:0.#}fps）  "
                              + $"间隔 均值 {mean * 1000,4:0}ms / p95 {p95 * 1000,4:0}ms / 最大 {max * 1000,5:0}ms"
                              + (stalled ? "   ⚠ 画面会明显停住" : ""));
        }

        Console.WriteLine($"  播放器时钟 {playerTime:0.##}s / 墙钟 {wall:0.##}s（差 {Math.Abs(playerTime - wall) * 1000:0}ms）");
        Console.WriteLine();
        return ok;
    }

    private static bool PhaseC(string videoPath, string audioPath, bool loaded)
    {
        Console.WriteLine("--- C. 边播边 seek（每 1.2s 随机跳一次 / 加载 CPU）---");
        var ui = new UiSimulator(0);
        var clock = new Stopwatch();
        var seekLatency = new List<double>();
        double seekIssuedAt = -1;
        var seeks = 0;

        VideoProjectPlayer? player = null;
        player = new VideoProjectPlayer(BuildProject(videoPath, audioPath), 800, 24, (frame, _, track) =>
        {
            Buffer.BlockCopy(frame.Pixels, 0, Scratch, 0, Math.Min(frame.Pixels.Length, Scratch.Length));
            if (track == 0 && seekIssuedAt >= 0)
            {
                seekLatency.Add(clock.Elapsed.TotalSeconds - seekIssuedAt);
                seekIssuedAt = -1;
            }

            ui.Post(track, () => player?.MarkTrackConsumed(track));
        });

        var seeker = new Thread(() =>
        {
            var rnd = new Random(7);
            while (_loadRunning || seeks == 0)
            {
                Thread.Sleep(1200);
                seeks++;
                seekIssuedAt = clock.Elapsed.TotalSeconds;
                player.Seek(rnd.NextDouble() * 18);
                if (seeks >= 7)
                {
                    break;
                }
            }
        })
        {
            IsBackground = true
        };

        if (loaded)
        {
            StartLoad();
        }

        clock.Start();
        player.Start();
        seeker.Start();
        seeker.Join(20_000);
        player.Stop();
        player.Dispose();
        StopLoad();
        clock.Stop();
        ui.Stop();

        var avg = seekLatency.Count > 0 ? seekLatency.Average() * 1000 : 0;
        var worst = seekLatency.Count > 0 ? seekLatency.Max() * 1000 : 0;
        var ok = seekLatency.Count >= seeks - 1 && worst < 400;
        Console.WriteLine($"  跳转 {seeks} 次，记录到 {seekLatency.Count} 次回到画面：均值 {avg:0}ms / 最差 {worst:0}ms"
                          + (ok ? "" : "   ⚠ 跳转后画面回来太慢 / 有跳转没恢复"));
        Console.WriteLine();
        return ok;
    }

    private static bool PhaseD(string videoPath, string audioPath, bool loaded)
    {
        Console.WriteLine("--- D. 播放中改倍速（1× → 2× → 0.5× → 3× / 加载 CPU）---");
        var project = BuildProject(videoPath, audioPath);
        var clip = project.Clips.First(c => c.Kind == "Video" && c.Track == 0);
        var ui = new UiSimulator(0);
        var clock = new Stopwatch();

        VideoProjectPlayer? player = null;
        player = new VideoProjectPlayer(project, 800, 24, (frame, _, track) =>
        {
            Buffer.BlockCopy(frame.Pixels, 0, Scratch, 0, Math.Min(frame.Pixels.Length, Scratch.Length));
            ui.Post(track, () => player?.MarkTrackConsumed(track));
        });

        if (loaded)
        {
            StartLoad();
        }

        clock.Start();
        player.Start();
        var marks = new List<(double Speed, int Frames)>();
        foreach (var speed in new[] { 2.0, 0.5, 3.0, 1.0 })
        {
            var before = ui.Count(0);
            clip.Speed = speed;
            player.RefreshClips();
            Thread.Sleep(1500);
            marks.Add((speed, ui.Count(0) - before));
        }

        player.Stop();
        player.Dispose();
        StopLoad();
        clock.Stop();
        ui.Stop();

        var ok = true;
        foreach (var (speed, frames) in marks)
        {
            var blocked = frames < 10; // 1.5s 内少于 10 帧 = 画面基本停了
            ok &= !blocked;
            Console.WriteLine($"  {speed:0.##}×（片段时长 {clip.Duration:0.##}s）：1.5s 内轨0 投递 {frames} 帧"
                              + (blocked ? "   ⚠ 改速度后画面停了" : ""));
        }

        Console.WriteLine();
        return ok;
    }

    /// <summary>模拟 UI 线程：单线程串行消费（真实 Dispatcher 也是串行的），代价 = 忙等。</summary>
    private sealed class UiSimulator
    {
        private readonly int _jankMs;
        private readonly BlockingCollection<(int Track, Action Done)> _queue = [];
        private readonly Thread _worker;
        private readonly ConcurrentDictionary<int, List<double>> _deliveries = new();
        private readonly ConcurrentDictionary<int, List<double>> _gaps = new();
        private readonly ConcurrentDictionary<int, double> _last = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile bool _running = true;

        public UiSimulator(int jankMs)
        {
            _jankMs = jankMs;
            _worker = new Thread(() =>
            {
                foreach (var (track, done) in _queue.GetConsumingEnumerable())
                {
                    if (!_running)
                    {
                        break;
                    }

                    Busy(_jankMs);
                    var now = _clock.Elapsed.TotalSeconds;
                    var list = _deliveries.GetOrAdd(track, _ => []);
                    lock (list)
                    {
                        list.Add(now);
                    }

                    if (_last.TryGetValue(track, out var prev))
                    {
                        _gaps.GetOrAdd(track, _ => []).Add(now - prev);
                    }

                    _last[track] = now;
                    done();
                }
            })
            {
                IsBackground = true
            };
            _worker.Start();
        }

        public IEnumerable<int> Tracks => _deliveries.Keys.OrderBy(t => t);

        public int Count(int track) => _deliveries.TryGetValue(track, out var l) ? l.Count : 0;

        public List<double> Gaps(int track)
        {
            var g = _gaps.TryGetValue(track, out var l) ? [.. l] : new List<double>();
            g.Sort();
            return g;
        }

        public void Post(int track, Action done) => _queue.Add((track, done));

        public void Stop()
        {
            _running = false;
            _queue.CompleteAdding();
            _worker.Join(1000);
        }

        private static void Busy(int ms)
        {
            if (ms <= 0)
            {
                return;
            }

            var until = Stopwatch.GetTimestamp() + (long)(ms / 1000.0 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
            {
                // 忙等：既是延迟也是 CPU 占用（像被浏览器挤爆的 UI 线程）。
            }
        }
    }

    // ---------------- F. 播放中清空片段（应停止并回 0s，而不是在 0s 抽搐） ----------------
    private static bool PhaseF(string videoPath, string audioPath)
    {
        Console.WriteLine("--- F. 播放中删光片段（期望：停止播放、时间归零、不再反复复位）---");
        var project = BuildProject(videoPath, audioPath, tracks: 1);
        var ui = new UiSimulator(0);
        var clock = new Stopwatch();
        var framesAfterClear = 0;
        var cleared = false;

        VideoProjectPlayer? player = null;
        player = new VideoProjectPlayer(project, 400, 24, (frame, _, track) =>
        {
            if (Volatile.Read(ref cleared))
            {
                Interlocked.Increment(ref framesAfterClear);
            }

            ui.Post(track, () => player?.MarkTrackConsumed(track));
        });

        clock.Start();
        player.Start();
        Thread.Sleep(2500);
        var timeBefore = player.CurrentTime;

        // 清空时间轴（等价于编辑器里删光所有片段 + RefreshClips）：
        // 编辑器还会调 SyncPreviewWithProject() 显式停播，这里只验证播放器自身不再抽搐。
        project.Clips.Clear();
        Volatile.Write(ref cleared, true);
        player.RefreshClips();

        var samples = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            Thread.Sleep(120);
            samples.Add(player.CurrentTime);
        }

        var after = player.CurrentTime;
        player.Stop();
        player.Dispose();
        ui.Stop();

        // 判据：清空后不再投递帧；时间不再前进（也不再被反复复位成 0 → 波动应极小）。
        var spread = samples.Max() - samples.Min();
        var ok = framesAfterClear == 0 && spread < 0.35 && after < 0.35;
        Console.WriteLine($"  清空前 {timeBefore:0.##}s → 清空后 12 次采样：最大波动 {spread:0.###}s、末值 {after:0.###}s、"
                          + $"清空后仍投递 {framesAfterClear} 帧" + (ok ? "" : "   ⚠ 仍在 0s 附近抽搐"));
        Console.WriteLine();
        return ok;
    }

    // ---------------- E. 声卡位置 vs 墙钟（音画漂移校正的依据） ----------------
    private static bool PhaseE(string videoPath, string audioPath)
    {
        Console.WriteLine("--- E. 声卡播放位置 vs 墙钟（CheckAudioSync 就是拿它算漂移的）---");
        var project = BuildProject(videoPath, audioPath, tracks: 1);
        var mixer = new ProjectAudioMixer();
        if (!mixer.Start(project, 0))
        {
            Console.WriteLine("  音频输出没起来（无设备）：跳过");
            Console.WriteLine();
            return true;
        }

        var sw = Stopwatch.StartNew();
        var worst = 0.0;
        var phase = "播放";
        void Sample(int i)
        {
            var wall = sw.Elapsed.TotalSeconds;
            var rendered = mixer.CurrentTime;
            var audible = mixer.AudibleTime;
            var drift = audible - rendered;
            worst = Math.Max(worst, Math.Abs(drift));
            if (i % 3 == 0)
            {
                Console.WriteLine($"  [{phase}] t={wall,5:0.00}s  渲染 {rendered,6:0.00}s  "
                                  + $"声卡 {audible,6:0.00}s  两者差 {drift,6:+0.00;-0.00}s");
            }
        }

        for (var i = 0; i < 6; i++) { Thread.Sleep(500); Sample(i); }
        Console.WriteLine("  >>> Seek(10)");
        phase = "seek 后";
        mixer.Seek(10);
        for (var i = 0; i < 6; i++) { Thread.Sleep(500); Sample(i); }
        Console.WriteLine("  >>> Pause");
        phase = "暂停中";
        mixer.Pause();
        for (var i = 0; i < 2; i++) { Thread.Sleep(500); Sample(i); }
        Console.WriteLine("  >>> Resume");
        phase = "恢复后";
        mixer.Resume();
        for (var i = 0; i < 6; i++) { Thread.Sleep(500); Sample(i); }

        mixer.Dispose();
        var ok = worst < 0.3;
        Console.WriteLine($"  渲染位置与声卡位置最大差 {worst:0.###}s"
                          + (ok ? "" : "   ⚠ 漂移校正会误判（CheckAudioSync 拿这两个量作差）"));
        Console.WriteLine();
        return ok;
    }

    // ---------------- 工程与负载 ----------------
    private static VideoProject BuildProject(string videoPath, string audioPath, int tracks = 3)
    {
        var project = new VideoProject { OutputWidth = 1280, OutputHeight = 578 };
        for (var t = 0; t < tracks; t++)
        {
            project.Clips.Add(new VideoClip
            {
                Kind = "Video",
                SourcePath = videoPath,
                Track = t,
                StartTime = t * 2,
                OutPoint = 20 - t * 2,
                Muted = true
            });
        }

        project.Clips.Add(new VideoClip
        {
            Kind = "Audio", SourcePath = audioPath, Track = -1, AudioTrack = 0,
            StartTime = 0, OutPoint = 20, Volume = 1
        });
        return project;
    }

    private static void StartLoad()
    {
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        for (var i = 0; i < Math.Max(1, Environment.ProcessorCount - 1); i++)
        {
            var t = new Thread(() =>
            {
                var acc = 1.0;
                while (_loadRunning)
                {
                    for (var j = 0; j < 100_000; j++)
                    {
                        acc = Math.Sin(acc) * 1.000001 + 0.000001;
                    }
                }

                GC.KeepAlive(acc);
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal // 模拟「别的程序在抢 CPU」
            };
            t.Start();
            LoadThreads.Add(t);
        }

        Console.WriteLine($"  [加载：{LoadThreads.Count} 个忙线程 @AboveNormal]");
    }

    private static void StopLoad()
    {
        _loadRunning = false;
        foreach (var t in LoadThreads)
        {
            t.Join(500);
        }

        LoadThreads.Clear();
    }
}
