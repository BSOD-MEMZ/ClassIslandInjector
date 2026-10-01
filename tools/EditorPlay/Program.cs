using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

namespace ClassIslandInjector;

/// <summary>headless 下的应用壳（只提供主题；不加载 ClassIsland 的样式表）。</summary>
internal sealed class PlayApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new FluentAvalonia.Styling.FluentAvaloniaTheme());
    }
}

/// <summary>
/// 「假人剪视频」驱动探针：用**真实的** <see cref="Views.VideoEditorWindow"/> + 真实宿主程序集，
/// 在 Avalonia headless 平台上像真人一样操作编辑器（鼠标按下/拖动/释放、滚轮、键盘、拖放），
/// 每一步 dump 工程模型 + 渲染 PNG，用来在没有 GUI 会话的情况下做真实的功能/视觉核查。
/// <para>
/// ⚠️ 两条写场景时必须遵守的纪律（都踩过）：
/// <list type="number">
/// <item><b>不要跨步骤持有 VideoClip 引用</b>：撤销/重做走 <c>RestoreProject</c>，它会把
/// <c>_project.Clips</c> 整体换成 <c>Clone()</c> 出来的新对象，旧引用从此不在 <c>_blockByClip</c> 里，
/// 后续 <c>BlockRect</c> 会抛/拿到空矩形 → 点了个寂寞。每步都用 <see cref="FindClip"/> 重新解析。</item>
/// <item><b>拖动的 <c>MouseMove</c> 必须带 <c>RawInputModifiers.LeftMouseButton</c></b>，
/// 否则编辑器按「左键已松开」中止拖拽。</item>
/// </list>
/// </para>
/// </summary>
internal static class Program
{
    private static readonly string Root = @"D:\Dev\ClassIslandInjector\.workbuddy\tmp\editorplay";
    private static readonly string ConfigDir = Path.Combine(Root, "config");
    private static readonly string ShotDir = Path.Combine(Root, "shots");
    private const string PluginDir = @"D:\Dev\ClassIsland\data\Plugins\classisland.injector";
    private const string FfmpegDir = @"D:\Dev\ClassIsland\data\Config\Plugins\classisland.injector\ffmpeg";

    private const string VideoA = @"D:\Downloads\HAO_back.mp4";
    private const string VideoB = @"D:\Downloads\什么时候告白啊!!!!!.40930379625.mp4";
    private const string AudioA = @"D:\Dev\ClassIslandInjector\Assets\Music\livetune,初音ミク - Tell Your World.mp3";

    private static int _shotSeq;
    private static int _fail;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var scenario = args.Length > 0 ? args[0] : "edit";

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };

        PrepareSandbox();
        AppBuilder.Configure<PlayApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        InjectorRuntime.Initialize(ConfigDir, PluginDir);
        // ⚠️ 必须自己补这一步：`FFmpegRuntime.Initialize` 只探测「文件齐不齐」并记住目录，
        // 真正把 `ffmpeg.RootPath` 设上、触发原生库加载的是 `EnsureLoaded()`；真实宿主在插件启动时
        // 会调它，探针不调的话所有 FFmpeg 路径都会抛 `NotSupportedException: Specified method is not supported.`
        // （表现为：素材缩略图全是空白、音频打开失败 —— 曾把「缩略图没生成」误当成应用 bug）。
        Console.WriteLine($"FFmpeg EnsureLoaded={FFmpegRuntime.EnsureLoaded()}");

        Console.WriteLine($"=== EditorPlay / 场景 {scenario} ===");

        try
        {
            var code = scenario switch
            {
                "empty" => RunEmpty(),
                "edit" => RunEdit(),
                "exittest" => RunExitTest(),
                "thumb" => RunThumbTest(),
                _ => Unknown(scenario)
            };

            Console.WriteLine(_fail == 0 ? "\n★ 全部断言通过" : $"\n★ {_fail} 条断言失败");
            return _fail == 0 ? code : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"× 场景异常: {ex}");
            return 1;
        }
    }

    private static int Unknown(string s)
    {
        Console.WriteLine($"× 未知场景：{s}");
        return 2;
    }

    private static void PrepareSandbox()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(ShotDir);
        var link = Path.Combine(ConfigDir, "ffmpeg");
        if (!Directory.Exists(link))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{link}\" \"{FfmpegDir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })?.WaitForExit(10000);
            }
            catch
            {
                // 忽略
            }
        }

        FFmpegRuntime.Initialize(link);
        Console.WriteLine($"FFmpeg 可用: {FFmpegRuntime.IsAvailable}");
    }

    // ============ 场景 ============

    private static int RunEmpty()
    {
        var w = OpenEditor();
        Dump(w, "空工程-初始");
        Shot(w, "空工程");
        w.Close();
        Pump();
        return 0;
    }

    /// <summary>模拟一次真人剪视频的完整会话。</summary>
    private static int RunEdit()
    {
        SeedProject();
        var w = OpenEditor();
        Dump(w, "① 打开已有工程");
        Shot(w, "01-打开工程");
        // 视觉核查用放大图：音频块（看文字是否被波形挤掉）、素材库 tab 行与卡片、时间轴整体。
        var audioRect = BlockRect(w, FindClip(w, "Audio", -1));
        ShotZoom(w, "01a-音频块放大", new Rect(audioRect.X - 8, audioRect.Y - 10, 460, audioRect.Height + 20), 2);
        ShotZoom(w, "01b-素材库放大", new Rect(12, 84, 300, 250), 2);

        // ---- ② 单击选中（点单独占一条轨的文本片段，后面拿它做无损实验）----
        Console.WriteLine("\n② 单击 V3 上的文本片段");
        Click(w, Center(BlockRect(w, FindClip(w, "Text", 2))));
        Console.WriteLine($"    选中数={SelectedCount(w)}（期望 1）");
        Assert(SelectedCount(w) == 1, "单击应恰好选中 1 个片段");
        Shot(w, "02-单击选中");
        var br = BlockRect(w, FindClip(w, "Text", 2));
        ShotZoom(w, "02b-选中块放大-裁剪手柄", new Rect(br.X - 30, br.Y - 16, br.Width + 60, br.Height + 32));

        // ---- ③ 向右拖 3s（该轨只有它自己，不会被「不堆叠」规则挪走）----
        Console.WriteLine("\n③ 把文本片段向右拖 3s");
        var startBefore = FindClip(w, "Text", 2).StartTime;
        var px = Field<double>(w, "_pxPerSecond");
        Drag(w, Center(BlockRect(w, FindClip(w, "Text", 2))),
            Shift(Center(BlockRect(w, FindClip(w, "Text", 2))), 3.0 * px, 0));
        var text2 = FindClip(w, "Text", 2);
        Console.WriteLine($"    start {startBefore:0.###} → {text2.StartTime:0.###}（期望 {startBefore + 3:0.###}）");
        Assert(Math.Abs(text2.StartTime - (startBefore + 3)) < 0.08, "拖动应把 StartTime 精确右移 3s");
        Shot(w, "03-拖动之后");

        // ---- ④ 撤销 ----
        Console.WriteLine("\n④ Ctrl+Z 撤销");
        PressKey(w, Key.Z, RawInputModifiers.Control);
        Console.WriteLine($"    start → {FindClip(w, "Text", 2).StartTime:0.###}（期望回到 {startBefore:0.###}）");
        Assert(Math.Abs(FindClip(w, "Text", 2).StartTime - startBefore) < 0.08, "撤销应还原拖动");
        Shot(w, "04-撤销之后");

        // ---- ⑤ 拖右边缘裁剪 2s ----
        Console.WriteLine("\n⑤ 选中文本片段并把右边缘向左拖 2s（裁剪）");
        Click(w, Center(BlockRect(w, FindClip(w, "Text", 2))));
        var durBefore = FindClip(w, "Text", 2).Duration;
        var r = BlockRect(w, FindClip(w, "Text", 2));
        var edge = new Point(r.X + r.Width - 6, r.Y + r.Height / 2);
        Drag(w, edge, new Point(edge.X - 2.0 * px, edge.Y));
        var durAfter = FindClip(w, "Text", 2).Duration;
        Console.WriteLine($"    duration {durBefore:0.###} → {durAfter:0.###}（期望 {durBefore - 2:0.###}）");
        Assert(Math.Abs(durAfter - (durBefore - 2)) < 0.08, "拖右边缘应把时长缩短 2s");
        Shot(w, "05-裁剪之后");

        // ---- ⑥ 撤销裁剪 ----
        Console.WriteLine("\n⑥ Ctrl+Z 撤销裁剪");
        PressKey(w, Key.Z, RawInputModifiers.Control);
        var durUndo = FindClip(w, "Text", 2).Duration;
        Console.WriteLine($"    duration → {durUndo:0.###}（期望 {durBefore:0.###}）");
        Assert(Math.Abs(durUndo - durBefore) < 0.08, "撤销应还原裁剪");
        Shot(w, "06-撤销裁剪");

        // ---- ⑦ 「不堆叠」规则 ----
        Console.WriteLine("\n⑦ 把 V1 首个视频拖进同轨另一片段的时间区间（应被自动挪走，不堆叠）");
        Click(w, Center(BlockRect(w, FindClip(w, "Video", 0, VideoA))));
        Drag(w, Center(BlockRect(w, FindClip(w, "Video", 0, VideoA))),
            Shift(Center(BlockRect(w, FindClip(w, "Video", 0, VideoA))), 3.0 * px, 0));
        var v1 = FindClip(w, "Video", 0, VideoA);
        var overlaps = Field<VideoProject>(w, "_project")!.Clips
            .Where(c => !c.IsAudio && c.Track == 0 && !ReferenceEquals(c, v1))
            .Any(o => v1.StartTime < o.StartTime + o.Duration - 0.001 &&
                      v1.StartTime + v1.Duration > o.StartTime + 0.001);
        Console.WriteLine($"    start={v1.StartTime:0.###}（原 0）与同轨片段重叠={overlaps}");
        Assert(!overlaps, "同轨片段不允许堆叠（应自动挪到最近空位）");
        Shot(w, "07-不堆叠");

        // ---- ⑧ 框选 ----
        Console.WriteLine("\n⑧ 在时间轴空白处拉框多选");
        // 起点要避开播放头的 18px seek 热区（它在 t=0 处，覆盖内容坐标 x≈-1..17）：
        // 落在热区上会进入 scrub 而不是框选（日志「PLAYHEAD 点空白 → 进入 scrub」）。
        // 也避开第一个片段（文本在 x=36 起），所以取 28。
        var blank = Translate(w, "_timelineRoot", new Point(28, 4));
        Drag(w, blank, new Point(blank.X + 620, blank.Y + 150));
        Console.WriteLine($"    选中数={SelectedCount(w)}（期望 ≥2）");
        Assert(SelectedCount(w) >= 2, "框选应选中多个片段");
        Shot(w, "08-框选");

        // ---- ⑨ 缩放 ----
        var pxBefore = Field<double>(w, "_pxPerSecond");
        Console.WriteLine("\n⑨ Ctrl+滚轮缩放");
        Wheel(w, new Point(blank.X + 300, blank.Y + 60), new Vector(0, 3), RawInputModifiers.Control);
        var pxAfter = Field<double>(w, "_pxPerSecond");
        Console.WriteLine($"    pxPerSecond {pxBefore:0.##} → {pxAfter:0.##}（期望放大）");
        Assert(pxAfter > pxBefore, "Ctrl+滚轮向上应放大时间轴");
        Shot(w, "09-缩放");

        // ---- ⑩ 删除 + 撤销 ----
        var countBefore = Field<VideoProject>(w, "_project")!.Clips.Count;
        var selCount = SelectedCount(w);
        Console.WriteLine($"\n⑩ Delete 删除 {selCount} 个选中片段");
        PressKey(w, Key.Delete, RawInputModifiers.None);
        var afterDel = Field<VideoProject>(w, "_project")!.Clips.Count;
        Console.WriteLine($"    片段数 {countBefore} → {afterDel}（期望 {countBefore - selCount}）");
        Assert(afterDel == countBefore - selCount, "Delete 应删除全部选中片段");
        Shot(w, "10-删除之后");

        Console.WriteLine("\n⑪ Ctrl+Z 撤销删除");
        PressKey(w, Key.Z, RawInputModifiers.Control);
        var afterUndo = Field<VideoProject>(w, "_project")!.Clips.Count;
        Console.WriteLine($"    片段数 → {afterUndo}（期望 {countBefore}）");
        Assert(afterUndo == countBefore, "撤销应还原删除");
        Dump(w, "⑪ 撤销删除之后");
        Shot(w, "11-撤销删除");

        w.Close();
        Pump();
        return 0;
    }

    /// <summary>
    /// 专门实验：拖片段到**窗口外**松开，看编辑器能不能正确收敛状态。
    /// （已验证 PointerExited 不能用来判「离开窗口」：窗口内部移动也会触发。）
    /// </summary>
    private static int RunExitTest()
    {
        SeedProject();
        var w = OpenEditor();

        Console.WriteLine("\n[A] 窗口内拖动（跨控件）");
        var c = Center(BlockRect(w, FindClip(w, "Text", 2)));
        Drag(w, c, Shift(c, 40, 6), steps: 4);
        Console.WriteLine($"    拖拽状态={DragState(w)}  start={FindClip(w, "Text", 2).StartTime:0.###}");
        Assert(DragState(w) == "move=False trim=False marquee=False", "正常拖完后不应残留交互状态");

        Console.WriteLine("\n[B] 拖到窗口外再松开，然后回到窗口内移动一下");
        var c2 = Center(BlockRect(w, FindClip(w, "Text", 2)));
        w.MouseDown(c2, MouseButton.Left);
        Pump(2);
        w.MouseMove(new Point(c2.X + 20, c2.Y), RawInputModifiers.LeftMouseButton);
        Pump(1);
        w.MouseMove(new Point(-40, -40), RawInputModifiers.LeftMouseButton);
        Pump(3);
        Console.WriteLine($"    移出窗口后 拖拽状态={DragState(w)}（预期还残留：松手事件送不到窗口）");
        w.MouseUp(new Point(-40, -40), MouseButton.Left);
        Pump(3);
        w.MouseMove(new Point(c2.X + 30, c2.Y + 4));
        Pump(8);
        Console.WriteLine($"    回到窗口内移动后 拖拽状态={DragState(w)}（期望已自愈）");
        Assert(DragState(w) == "move=False trim=False marquee=False", "回到窗口内后应自愈，不留残留状态");

        Shot(w, "exittest");
        w.Close();
        Pump();
        return 0;
    }

    /// <summary>
    /// 素材缩略图链路实验：素材库卡片里视频没有缩略图，先分清是「缩略图链路坏了」还是「沙箱/异步问题」。
    /// 直接跑 LoadAssetThumbnail 内部那两步（VideoFrameSource.Open + TryReadFrame + CreateBitmap）。
    /// </summary>
    private static int RunThumbTest()
    {
        foreach (var path in new[] { VideoA, VideoB, Path.Combine(PluginDir, "icon.png") })
        {
            Console.Write($"{Path.GetFileName(path)}: ");
            try
            {
                if (VideoTranscoder.IsImageFile(path))
                {
                    using var st = File.OpenRead(path);
                    var bmp = new Avalonia.Media.Imaging.Bitmap(st);
                    Console.WriteLine($"图片 → {bmp.PixelSize.Width}x{bmp.PixelSize.Height} ✓");
                    continue;
                }

                using var src = new VideoFrameSource();
                var opened = src.Open(path, 256);
                VideoFrame? frame = null;
                var got = opened && src.TryReadFrame(out frame) && frame != null;
                if (!got)
                {
                    Console.WriteLine($"解码首帧失败（Open={opened}）✗");
                    continue;
                }

                var pixels = new byte[frame!.Pixels.Length];
                Buffer.BlockCopy(frame.Pixels, 0, pixels, 0, pixels.Length);
                var m = typeof(Views.VideoEditorWindow).GetMethod("CreateBitmap",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                var wbmp = m.Invoke(null, new object[] { pixels, frame.Width, frame.Height });
                Console.WriteLine($"视频 → {frame.Width}x{frame.Height} 位图={(wbmp == null ? "null ✗" : "✓")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"异常 ✗ {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("（素材库卡片若是空白，就看上面这一段有没有失败）");
        return 0;
    }

    /// <summary>预置一个「真人剪到一半」的工程：2 视频片段 + 1 图片 + 1 文本 + 1 音频。</summary>
    private static void SeedProject()
    {
        var p = new VideoProject { OutputWidth = 800, OutputHeight = 480, Name = "EditorPlay 测试工程" };
        p.Clips.Add(new VideoClip
        {
            Kind = "Video", SourcePath = VideoA, Track = 0, StartTime = 0, InPoint = 0, OutPoint = 8
        });
        p.Clips.Add(new VideoClip
        {
            Kind = "Video", SourcePath = VideoB, Track = 0, StartTime = 9, InPoint = 0, OutPoint = 7
        });
        p.Clips.Add(new VideoClip
        {
            Kind = "Image", SourcePath = Path.Combine(PluginDir, "icon.png"), Track = 1, StartTime = 3,
            InPoint = 0, OutPoint = 5
        });
        p.Clips.Add(new VideoClip
        {
            Kind = "Text", Shape = "Rect", Text = "标题文字", Color = "#FFFFEB3B", Track = 2,
            StartTime = 1, InPoint = 0, OutPoint = 4, Scale = 1, ScaleX = 1, ScaleY = 1
        });
        p.Clips.Add(new VideoClip
        {
            Kind = "Audio", SourcePath = AudioA, Track = -1, AudioTrack = 0, StartTime = 0,
            InPoint = 0, OutPoint = 20
        });
        p.TrackStates.Clear();
        for (var i = 0; i < 3; i++)
        {
            p.TrackStates.Add(new TrackState());
        }

        p.AudioTrackStates.Clear();
        p.AudioTrackStates.Add(new TrackState());

        VideoProjectStore.Save(p, VideoProjectStore.DefaultPath);
        Console.WriteLine($"已写入种子工程：{p.Clips.Count} 片段，{p.TrackCount}V+{p.AudioTrackCount}A，时长 {p.Duration:0.###}s");
    }

    private static Views.VideoEditorWindow OpenEditor()
    {
        var w = new Views.VideoEditorWindow();
        w.Show();
        w.Width = 1280;
        w.Height = 800;
        Pump();
        // 素材缩略图是后台解帧生成的，等一会儿再截图，否则会把「还在加载」误判成「缩略图坏了」。
        Thread.Sleep(3000);
        Pump(20);
        return w;
    }

    // ============ 输入模拟 ============

    private static void Click(TopLevel w, Point p)
    {
        w.MouseDown(p, MouseButton.Left);
        Pump(2);
        w.MouseUp(p, MouseButton.Left);
        Pump(5);
    }

    /// <summary>按住拖动（所有 MouseMove 都带左键修饰符，见类注释第 2 条）。</summary>
    private static void Drag(TopLevel w, Point from, Point to, int steps = 12)
    {
        w.MouseDown(from, MouseButton.Left);
        Pump(2);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            w.MouseMove(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t),
                RawInputModifiers.LeftMouseButton);
            Pump(1);
        }

        w.MouseUp(to, MouseButton.Left);
        Pump(6);
    }

    private static void PressKey(TopLevel w, Key key, RawInputModifiers mods)
    {
        w.KeyPress(key, mods);
        Pump(2);
        w.KeyRelease(key, mods);
        Pump(8);
    }

    private static void Wheel(TopLevel w, Point p, Vector delta, RawInputModifiers mods)
    {
        w.MouseWheel(p, delta, mods);
        Pump(5);
    }

    // ============ 观测 ============

    private static void Pump(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(6);
        }
    }

    private static void Assert(bool ok, string what)
    {
        if (ok)
        {
            Console.WriteLine($"    ✓ {what}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"    ✗ 失败：{what}");
        }
    }

    private static void Shot(TopLevel w, string name)
    {
        try
        {
            var frame = w.CaptureRenderedFrame();
            if (frame == null)
            {
                Console.WriteLine($"   [截图] {name}: 拿不到帧");
                return;
            }

            var file = Path.Combine(ShotDir, $"{++_shotSeq:00}-{name}.png");
            frame.Save(file);
            Console.WriteLine($"   [截图] {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [截图] {name} 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把当前界面的某个区域**放大若干倍**另存（视觉核查用：手柄/边框/文字这类细节在整图里看不清）。
    /// </summary>
    private static void ShotZoom(TopLevel w, string name, Rect region, double scale = 3)
    {
        try
        {
            var frame = w.CaptureRenderedFrame();
            if (frame == null)
            {
                Console.WriteLine($"   [放大] {name}: 拿不到帧");
                return;
            }

            var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)(region.Width * scale), (int)(region.Height * scale)));
            using (var ctx = rtb.CreateDrawingContext())
            {
                var m = Matrix.CreateScale(scale, scale) *
                        Matrix.CreateTranslation(-region.X * scale, -region.Y * scale);
                using (ctx.PushTransform(m))
                {
                    ctx.DrawImage(frame, new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height));
                }
            }

            var file = Path.Combine(ShotDir, $"{++_shotSeq:00}-{name}.png");
            rtb.Save(file);
            Console.WriteLine($"   [放大] {Path.GetFileName(file)}  区域={region} x{scale}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [放大] {name} 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Dump(Views.VideoEditorWindow w, string tag)
    {
        var p = Field<VideoProject>(w, "_project")!;
        var px = Field<double>(w, "_pxPerSecond");
        Console.WriteLine($"--- {tag} ---");
        Console.WriteLine($"  工程 {p.Clips.Count} 片段 / {p.TrackCount}V+{p.AudioTrackCount}A / 时长 {p.Duration:0.###}s" +
                          $"  缩放 {px:0.##}px/s");
        foreach (var c in p.Clips.OrderBy(c => c.Track).ThenBy(c => c.StartTime))
        {
            var lane = (int)Invoke(w, "LaneOfClip", c)!;
            Console.WriteLine($"    lane={lane} {(c.IsAudio ? $"A{c.AudioTrack + 1}" : $"V{c.Track + 1}")} " +
                              $"「{c.Kind}」start={c.StartTime:0.###} dur={c.Duration:0.###} " +
                              $"in={c.InPoint:0.##} out={c.OutPoint:0.##}" +
                              (c.IsAudio ? "" : $" muted={c.Muted}") +
                              $" {Path.GetFileName(c.SourcePath)}");
        }

        // 模型坐标 vs 实际渲染坐标（不一致就是「点不中 / 拖不跟手」的根源）
        var root = (Visual)Field(w, "_timelineRoot")!;
        var blocks = Field<IDictionary>(w, "_blockByClip")!;
        var drift = 0;
        foreach (DictionaryEntry e in blocks)
        {
            var clip = (VideoClip)e.Key!;
            var block = (Control)e.Value!;
            var pos = block.TranslatePoint(new Point(0, 0), root);
            if (pos == null)
            {
                continue;
            }

            var lane = (int)Invoke(w, "LaneOfClip", clip)!;
            var mx = clip.StartTime * px;
            // 块在泳道内还有 6px 上边距（BuildClipBlock 的 Canvas.SetTop(block, 6)）。
            var my = (double)Invoke(w, "LaneVisualTop", lane)! + 6;
            if (Math.Abs(pos.Value.X - mx) > 1 || Math.Abs(pos.Value.Y - my) > 1)
            {
                drift++;
                Console.WriteLine($"    ⚠ 几何不符 lane={lane}「{clip.Kind}」模型=({mx:0.#},{my:0.#}) " +
                                  $"实际=({pos.Value.X:0.#},{pos.Value.Y:0.#})");
            }
        }

        Console.WriteLine(drift == 0 ? $"  ✓ 块几何与模型一致（{blocks.Count} 块）" : $"  ⚠ {drift} 个块几何不符");
    }

    // ============ 反射 / 几何 ============

    /// <summary>从**当前**工程里按条件解析片段（不要跨步骤持有引用，见类注释第 1 条）。</summary>
    private static VideoClip FindClip(object w, string kind, int track, string? sourceContains = null)
    {
        var p = Field<VideoProject>(w, "_project")!;
        return p.Clips.First(c => c.Kind == kind && c.Track == track &&
                                  (sourceContains == null || c.SourcePath.Contains(sourceContains)));
    }

    private static int SelectedCount(object w)
    {
        var set = Field(w, "_selectedClips")!;
        return (int)set.GetType().GetProperty("Count")!.GetValue(set)!;
    }

    private static string DragState(object w)
    {
        var move = Field(w, "_moveGroup") != null;
        var trim = Field(w, "_trimState") != null;
        var marquee = Field(w, "_marqueeStart") != null;
        return $"move={move} trim={trim} marquee={marquee}";
    }

    private static Rect BlockRect(Views.VideoEditorWindow w, VideoClip clip)
    {
        var blocks = Field<IDictionary>(w, "_blockByClip")!;
        if (!blocks.Contains(clip))
        {
            throw new InvalidOperationException(
                $"片段「{Path.GetFileName(clip.SourcePath)}」不在 _blockByClip 里（多半是跨步骤持有了过期引用）");
        }

        var block = (Control)blocks[clip]!;
        var tl = block.TranslatePoint(new Point(0, 0), w) ?? default;
        return new Rect(tl, block.Bounds.Size);
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static Point Shift(Point p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    private static Point Translate(object target, string fieldName, Point local)
    {
        var provider = (Visual)Field(target, fieldName)!;
        return provider.TranslatePoint(local, (Visual)target) ?? default;
    }

    private static object? Field(object target, string name)
    {
        var t = target.GetType();
        while (t != null)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
            {
                return f.GetValue(target);
            }

            t = t.BaseType;
        }

        throw new MissingFieldException(target.GetType().Name, name);
    }

    private static T? Field<T>(object target, string name) => (T?)Field(target, name);

    private static object? Invoke(object target, string name, params object?[] args)
    {
        var t = target.GetType();
        while (t != null)
        {
            var m = t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m != null)
            {
                return m.Invoke(target, args);
            }

            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null && args.Length == 0)
            {
                return p.GetValue(target);
            }

            t = t.BaseType;
        }

        throw new MissingMethodException(target.GetType().Name, name);
    }
}
