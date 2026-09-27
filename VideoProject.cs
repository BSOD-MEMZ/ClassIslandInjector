using System.IO;
using System.Text.Json;

namespace ClassIslandInjector;

/// <summary>
/// 视频工程：输出尺寸（与 ClassIsland 主界面显示宽高比一致）+ 多轨片段列表。
/// 片段按「轨道 + 起始时间」排列：同一时刻多轨同时播放（轨道号越大越靠上层），
/// 由 <see cref="VideoProjectPlayer"/> 同步解码并叠放合成。
/// </summary>
public sealed class VideoProject
{
    /// <summary>序列名（PR 的 Sequence 名；仅展示用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>输出宽（逻辑像素，与 ClassIsland 主界面显示比例一致）。</summary>
    public double OutputWidth { get; set; } = 400;
    /// <summary>输出高。</summary>
    public double OutputHeight { get; set; } = 90;
    /// <summary>片段列表（无序；渲染按轨道 + 起始时间）。</summary>
    public List<VideoClip> Clips { get; set; } = [];

    /// <summary>素材库（PR 的 Project 面板：导入过的素材，按路径去重）。</summary>
    public List<ProjectAsset> Assets { get; set; } = [];

    /// <summary>视频轨数量（最大视频轨号 + 1，至少 1）。音频片段不计入。</summary>
    public int TrackCount =>
        Clips.Where(c => !c.IsAudio).Select(c => c.Track).DefaultIfEmpty(0).Max() + 1;

    /// <summary>音频轨数量（最大音频轨号 + 1，至少 1）。</summary>
    public int AudioTrackCount =>
        Clips.Where(c => c.IsAudio).Select(c => c.AudioTrack).DefaultIfEmpty(0).Max() + 1;

    /// <summary>每轨状态（按下标 = 视频轨号；长度不足时按需补默认）。</summary>
    public List<TrackState> TrackStates { get; set; } = [];

    /// <summary>音频轨状态（按下标 = 音频轨号；对应 PR 的 A1/A2…）。</summary>
    public List<TrackState> AudioTrackStates { get; set; } = [];

    /// <summary>取轨道状态（只读；不存在返回 null = 默认行为）。播放/渲染用。</summary>
    public TrackState? GetTrackState(int track) =>
        track >= 0 && track < TrackStates.Count ? TrackStates[track] : null;

    /// <summary>取轨道状态（按需创建默认，编辑器用）。</summary>
    public TrackState TrackStateOf(int track)
    {
        while (TrackStates.Count <= track)
        {
            TrackStates.Add(new TrackState());
        }

        return TrackStates[track];
    }

    /// <summary>取音频轨状态（只读；不存在返回 null = 默认行为）。</summary>
    public TrackState? GetAudioTrackState(int track) =>
        track >= 0 && track < AudioTrackStates.Count ? AudioTrackStates[track] : null;

    /// <summary>取音频轨状态（按需创建默认，编辑器用）。</summary>
    public TrackState AudioTrackStateOf(int track)
    {
        while (AudioTrackStates.Count <= track)
        {
            AudioTrackStates.Add(new TrackState { IsAudio = true });
        }

        return AudioTrackStates[track];
    }

    /// <summary>工程总时长（秒）= 所有片段（含音频）末尾的最大值（多轨取最长）。</summary>
    public double Duration => Clips.Count == 0 ? 0 : Clips.Max(c => c.StartTime + c.Duration);

    /// <summary>某视频轨的末尾时间（秒，供追加片段定位）。</summary>
    public double TrackEnd(int track) =>
        Clips.Where(c => !c.IsAudio && c.Track == track).Select(c => c.StartTime + c.Duration)
             .DefaultIfEmpty(0).Max();

    /// <summary>某音频轨的末尾时间（秒）。</summary>
    public double AudioTrackEnd(int track) =>
        Clips.Where(c => c.IsAudio && c.AudioTrack == track).Select(c => c.StartTime + c.Duration)
             .DefaultIfEmpty(0).Max();

    /// <summary>
    /// 指定工程时刻（秒）应当出声的片段：音频片段 + 自带原声的视频片段。
    /// 是否真的有音轨由播放端探测（<see cref="ProjectAudioMixer"/>），这里只做时间筛选。
    /// </summary>
    public IEnumerable<VideoClip> AudioClipsAt(double time) =>
        Clips.Where(c => (c.IsAudio || string.Equals(c.Kind, "Video", StringComparison.OrdinalIgnoreCase))
                         && !c.Muted && c.Volume > 0.0001
                         && time >= c.StartTime && time < c.StartTime + c.Duration);

    /// <summary>工程是否含任何可能出声的片段（决定播放时要不要建立音频输出）。</summary>
    public bool HasAudioContent => Clips.Any(c =>
        (c.IsAudio || string.Equals(c.Kind, "Video", StringComparison.OrdinalIgnoreCase))
        && !c.Muted && c.Volume > 0.0001);

    /// <summary>工程里引用到的全部素材路径（素材库与波形缓存用）。</summary>
    public IEnumerable<string> ReferencedSources =>
        Clips.Where(c => !string.IsNullOrWhiteSpace(c.SourcePath)).Select(c => c.SourcePath).Distinct();

    /// <summary>
    /// 归一化工程：把旧版单轨拼接工程（全部片段无显式起始时间/轨道）迁移为
    /// 轨道 0 上按列表顺序首尾相接。新工程片段都带显式 StartTime，不受影响。
    /// </summary>
    public static void Normalize(VideoProject project)
    {
        if (project.Clips.Count > 1 && project.Clips.All(c => c.Track == 0 && c.StartTime <= 0))
        {
            var t = 0.0;
            foreach (var clip in project.Clips)
            {
                clip.StartTime = t;
                t += clip.Duration;
            }
        }
    }
}

/// <summary>轨道级状态（锁定 / 隐藏 / 静音 / 音量）。</summary>
public sealed class TrackState
{
    /// <summary>锁定：编辑器禁止修改该轨片段（拖动 / 删除 / 改属性）。</summary>
    public bool Locked { get; set; }
    /// <summary>隐藏：编辑器内该轨元素半透明显示，播放 / 渲染不显示。音频轨表示「不参与混音」。</summary>
    public bool Hidden { get; set; }
    /// <summary>是否为音频轨（对应 PR 的 A1/A2…；音频轨不参与画面合成）。</summary>
    public bool IsAudio { get; set; }
    /// <summary>轨道静音（音频轨；速控开关，不打乱片段自身的音量设置）。</summary>
    public bool Muted { get; set; }
    /// <summary>轨道音量倍率（音频轨，0..2）。</summary>
    public double Volume { get; set; } = 1;
}

/// <summary>一个视频/覆盖层片段：素材路径（视频）+ 入出点 + 轨道/起始时间 + 显示变换 + 文本/形状。</summary>
public sealed class VideoClip
{
    /// <summary>片段类型：Video（视频素材）/ Audio（音频素材）/ Text（文本覆盖层）/ Shape（形状覆盖层）/ Filter（滤镜片段）。</summary>
    public string Kind { get; set; } = "Video";
    /// <summary>素材文件路径（Kind=Video / Kind=Audio）。</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>文本内容（Kind=Text）。</summary>
    public string Text { get; set; } = "文本";
    /// <summary>文本/形状颜色（#AARRGGBB，支持透明度）。</summary>
    public string Color { get; set; } = "#FFFFFFFF";
    /// <summary>文本字体族（Kind=Text；空 = 微软雅黑）。</summary>
    public string TextFontFamily { get; set; } = "";
    /// <summary>文本字号系数（相对输出高度，0.32 = 默认字号）。</summary>
    public double TextFontSize { get; set; } = 0.32;
    /// <summary>文本是否加粗。</summary>
    public bool TextBold { get; set; } = true;
    /// <summary>形状描边颜色（#AARRGGBB；描边宽度 &gt; 0 时生效）。</summary>
    public string StrokeColor { get; set; } = "#FF000000";
    /// <summary>形状描边宽度（相对输出高的比例，0 = 无描边）。</summary>
    public double StrokeWidth { get; set; }
    /// <summary>形状类型（Kind=Shape）：Rect / Ellipse / Triangle / Diamond / Star / Heart / ArrowRight / Pentagon / Ring / Cross。</summary>
    public string Shape { get; set; } = "Rect";
    /// <summary>滤镜类型（Kind=Filter）：Grayscale / Sepia / Invert / Brighten / Darken / Mosaic。</summary>
    public string Filter { get; set; } = "";
    /// <summary>滤镜强度（0..1，0 = 无效果，1 = 完全应用）。</summary>
    public double FilterIntensity { get; set; } = 1;
    /// <summary>灰度效果（0=彩色，1=完全灰度）。</summary>
    public double Grayscale { get; set; }
    /// <summary>水平翻转。</summary>
    public bool FlipH { get; set; }
    /// <summary>垂直翻转。</summary>
    public bool FlipV { get; set; }
    /// <summary>所在轨道（0 = 底层，数字越大越靠上层）。</summary>
    public int Track { get; set; }
    /// <summary>在全局时间轴上的起始时间（秒）。</summary>
    public double StartTime { get; set; }
    /// <summary>素材入点（秒，相对素材起点）。</summary>
    public double InPoint { get; set; }
    /// <summary>素材出点（秒，相对素材起点）。</summary>
    public double OutPoint { get; set; } = 10;
    /// <summary>缩放倍率（1 = 原始帧铺满；&gt;1 放大裁边）。</summary>
    public double Scale { get; set; } = 1;
    /// <summary>横向拉伸倍率（相对 <see cref="Scale"/> 的额外乘数，1 = 无；左右边手柄拖拽产生非等比拉伸）。</summary>
    public double ScaleX { get; set; } = 1;
    /// <summary>纵向拉伸倍率（相对 <see cref="Scale"/> 的额外乘数，1 = 无；上下边手柄拖拽产生非等比拉伸）。</summary>
    public double ScaleY { get; set; } = 1;
    /// <summary>水平偏移（相对输出宽，-0.5..0.5）。</summary>
    public double OffsetX { get; set; }
    /// <summary>垂直偏移（相对输出高，-0.5..0.5）。</summary>
    public double OffsetY { get; set; }
    /// <summary>旋转角（度）。</summary>
    public double Rotation { get; set; }
    /// <summary>不透明度（0..1）。</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>裁剪左（归一化 0..1，0 = 不裁）。</summary>
    public double CropLeft { get; set; }
    /// <summary>裁剪上（归一化 0..1）。</summary>
    public double CropTop { get; set; }
    /// <summary>裁剪右（归一化 0..1，1 = 不裁）。</summary>
    public double CropRight { get; set; } = 1;
    /// <summary>裁剪下（归一化 0..1，1 = 不裁）。</summary>
    public double CropBottom { get; set; } = 1;

    // ---- 音频（Kind=Audio 的音频片段，以及 Kind=Video 片段自带的原声）----

    /// <summary>音频轨编号（Kind=Audio 专用；与视频轨 <see cref="Track"/> 是独立的编号空间，对应 PR 的 A1/A2…）。</summary>
    public int AudioTrack { get; set; }

    /// <summary>音量倍率（0..2，1 = 原始音量；0 = 静音）。音频片段与视频片段原声共用。</summary>
    public double Volume { get; set; } = 1;

    /// <summary>淡入时长（秒，从片段起点开始；0 = 不淡入）。</summary>
    public double AudioFadeIn { get; set; }

    /// <summary>淡出时长（秒，在片段末尾结束；0 = 不淡出）。</summary>
    public double AudioFadeOut { get; set; }

    /// <summary>是否静音该片段的音频。</summary>
    public bool Muted { get; set; }

    /// <summary>素材总时长（秒；音频片段用于铺满时间轴与波形绘制，0 = 未知按 <see cref="Duration"/>）。</summary>
    public double SourceDuration { get; set; }

    /// <summary>该片段是否参与音频混音（音频片段恒参与；视频片段需自带音轨，由播放端探测）。</summary>
    public bool ContributesAudio => !Muted && Volume > 0.0001 &&
                                    (IsAudio || Kind == "Video");

    /// <summary>是否为音频片段。</summary>
    public bool IsAudio => string.Equals(Kind, "Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 片段在 <paramref name="localTime"/>（相对片段起点的秒数）处的音频增益包络：
    /// 音量 × 淡入 × 淡出，结果钳制在 0..2。淡入淡出总长超过片段时长时按比例压缩（PR 的同类行为）。
    /// </summary>
    public double AudioGainAt(double localTime)
    {
        if (Muted || Volume <= 0.0001)
        {
            return 0;
        }

        var duration = Duration;
        var gain = Math.Clamp(Volume, 0, 2);

        var fadeIn = Math.Max(0, AudioFadeIn);
        var fadeOut = Math.Max(0, AudioFadeOut);
        if (fadeIn + fadeOut > duration && fadeIn + fadeOut > 0)
        {
            // 淡入淡出互相重叠：按比例压缩，避免出现负的稳态区间。
            var scale = duration / (fadeIn + fadeOut);
            fadeIn *= scale;
            fadeOut *= scale;
        }

        if (fadeIn > 0 && localTime < fadeIn)
        {
            gain *= Math.Clamp(localTime / fadeIn, 0, 1);
        }

        if (fadeOut > 0 && localTime > duration - fadeOut)
        {
            gain *= Math.Clamp((duration - localTime) / fadeOut, 0, 1);
        }

        return Math.Clamp(gain, 0, 2);
    }

    /// <summary>片段时长（秒）。</summary>
    public double Duration => Math.Max(0.1, OutPoint - InPoint);

    public VideoClip Clone() => (VideoClip)MemberwiseClone();
}

/// <summary>
/// 工程文件（磁盘格式 v2，PR 风格分层）：序列（画幅 + 视频轨 / 音频轨）+ 片段列表 + 素材库。
/// </summary>
public sealed class VideoProjectFile
{
    /// <summary>格式版本（v2 = 分层结构；缺失该字段视为 v1 单层结构）。</summary>
    public int Version { get; set; } = 2;
    /// <summary>序列名。</summary>
    public string Name { get; set; } = "";
    /// <summary>序列（PR 的 Sequence）。</summary>
    public VideoSequence Sequence { get; set; } = new();
    /// <summary>全部片段（视频 / 音频 / 文本 / 形状 / 滤镜）。</summary>
    public List<VideoClip> Clips { get; set; } = [];
    /// <summary>素材库（PR 的 Project 面板）。</summary>
    public List<ProjectAsset> Assets { get; set; } = [];
}

/// <summary>序列（PR 的 Sequence）：画幅尺寸与两类轨道定义。</summary>
public sealed class VideoSequence
{
    /// <summary>输出宽（与主界面显示比例一致）。</summary>
    public double OutputWidth { get; set; } = 400;
    /// <summary>输出高。</summary>
    public double OutputHeight { get; set; } = 90;
    /// <summary>视频轨（下标 0 = V1，数字越大越靠上层）。</summary>
    public List<TrackState> VideoTracks { get; set; } = [];
    /// <summary>音频轨（下标 0 = A1）。</summary>
    public List<TrackState> AudioTracks { get; set; } = [];
}

/// <summary>素材库条目（PR 的 Project 面板）：导入过的源文件。</summary>
public sealed class ProjectAsset
{
    /// <summary>素材文件路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>显示名（默认取文件名）。</summary>
    public string Name { get; set; } = "";
    /// <summary>素材类型：Video / Audio / Image。</summary>
    public string Kind { get; set; } = "Video";
    /// <summary>时长（秒；音频 / 视频，0 = 未知）。</summary>
    public double Duration { get; set; }
}

/// <summary>
/// 视频工程持久化：磁盘格式 v2（PR 风格分层 JSON，扩展名 <c>.ciproj</c>）。
/// v1 的单文件 JSON（顶层 OutputWidth/Clips）读取时自动迁移 —— 迁移过来的视频片段会**显式静音**，
/// 因为它们在「视频背景支持音频」之前本来就不出声，静音可避免升级后旧工程突然发声。
/// </summary>
public static class VideoProjectStore
{
    /// <summary>默认工程路径（配置目录\video-project.ciproj）。</summary>
    public static string DefaultPath { get; private set; } = string.Empty;

    /// <summary>v1 旧工程路径（配置目录\video-project.json；仅迁移读取用）。</summary>
    public static string LegacyPath { get; private set; } = string.Empty;

    public static void Initialize(string configDirectory)
    {
        DefaultPath = Path.Combine(configDirectory, "video-project.ciproj");
        LegacyPath = Path.Combine(configDirectory, "video-project.json");
    }

    /// <summary>实际读取路径：新格式存在则用新格式，否则回退旧格式（首次读到的旧工程会在下次保存时写成新格式）。</summary>
    public static string ResolveReadPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        return !string.IsNullOrEmpty(LegacyPath) && File.Exists(LegacyPath) ? LegacyPath : path;
    }

    public static VideoProject Load(string path)
    {
        try
        {
            var readPath = ResolveReadPath(path);
            if (File.Exists(readPath))
            {
                var text = File.ReadAllText(readPath);
                if (IsV2(text))
                {
                    var file = JsonSerializer.Deserialize<VideoProjectFile>(text);
                    if (file?.Sequence != null)
                    {
                        return FromFile(file);
                    }
                }
                else
                {
                    var legacy = JsonSerializer.Deserialize<VideoProject>(text);
                    if (legacy != null)
                    {
                        MigrateFromV1(legacy);
                        return legacy;
                    }
                }
            }
        }
        catch
        {
            // 损坏的工程回退为空工程。
        }

        return new VideoProject();
    }

    public static void Save(VideoProject project, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(ToFile(project), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>运行时模型 → 磁盘格式（v2 分层）。</summary>
    public static VideoProjectFile ToFile(VideoProject project) => new()
    {
        Version = 2,
        Name = project.Name,
        Sequence = new VideoSequence
        {
            OutputWidth = project.OutputWidth,
            OutputHeight = project.OutputHeight,
            VideoTracks = project.TrackStates,
            AudioTracks = project.AudioTrackStates
        },
        Clips = project.Clips,
        Assets = project.Assets
    };

    /// <summary>磁盘格式（v2 分层）→ 运行时模型。</summary>
    public static VideoProject FromFile(VideoProjectFile file)
    {
        var project = new VideoProject
        {
            Name = file.Name ?? "",
            OutputWidth = file.Sequence.OutputWidth,
            OutputHeight = file.Sequence.OutputHeight,
            Clips = file.Clips ?? [],
            Assets = file.Assets ?? [],
            TrackStates = file.Sequence.VideoTracks ?? [],
            AudioTrackStates = file.Sequence.AudioTracks ?? []
        };

        // 画幅缺失（0）时回落到默认，避免 0 除。
        if (project.OutputWidth <= 1)
        {
            project.OutputWidth = 400;
        }

        if (project.OutputHeight <= 1)
        {
            project.OutputHeight = 90;
        }

        // v2 里也可能混着 v1 遗留的单轨拼接工程（用户在 v1 时期导出、又用 v2 存过）。
        VideoProject.Normalize(project);
        return project;
    }

    /// <summary>v1（单层 JSON）→ 运行时模型：单轨拼接迁移 + 视频片段显式静音。</summary>
    public static void MigrateFromV1(VideoProject project)
    {
        VideoProject.Normalize(project);
        foreach (var clip in project.Clips)
        {
            if (!clip.IsAudio)
            {
                clip.Muted = true;
            }
        }
    }

    /// <summary>判断 JSON 文本是否为 v2 分层格式。</summary>
    private static bool IsV2(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("Version", out var version) &&
                   version.TryGetInt32(out var value) && value >= 2 &&
                   doc.RootElement.TryGetProperty("Sequence", out _);
        }
        catch
        {
            return false;
        }
    }
}
