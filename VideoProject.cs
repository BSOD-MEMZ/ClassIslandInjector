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
    /// <summary>输出宽（逻辑像素，与 ClassIsland 主界面显示比例一致）。</summary>
    public double OutputWidth { get; set; } = 400;
    /// <summary>输出高。</summary>
    public double OutputHeight { get; set; } = 90;
    /// <summary>片段列表（无序；渲染按轨道 + 起始时间）。</summary>
    public List<VideoClip> Clips { get; set; } = [];

    /// <summary>轨道数（最大轨道号 + 1，至少 1）。</summary>
    public int TrackCount => Clips.Count == 0 ? 1 : Clips.Max(c => c.Track) + 1;

    /// <summary>每轨状态（按下标 = 轨道号；长度不足时按需补默认）。</summary>
    public List<TrackState> TrackStates { get; set; } = [];

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

    /// <summary>工程总时长（秒）= 所有片段末尾的最大值（多轨取最长）。</summary>
    public double Duration => Clips.Count == 0 ? 0 : Clips.Max(c => c.StartTime + c.Duration);

    /// <summary>某轨道的末尾时间（秒，供追加片段定位）。</summary>
    public double TrackEnd(int track) =>
        Clips.Where(c => c.Track == track).Select(c => c.StartTime + c.Duration)
             .DefaultIfEmpty(0).Max();

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

/// <summary>轨道级状态（锁定 / SOLO / 隐藏）。</summary>
public sealed class TrackState
{
    /// <summary>锁定：编辑器禁止修改该轨片段（拖动 / 删除 / 改属性）。</summary>
    public bool Locked { get; set; }
    /// <summary>SOLO：有任一轨 SOLO 时仅播放 / 渲染 SOLO 轨（其余静音）。</summary>
    public bool Solo { get; set; }
    /// <summary>隐藏：编辑器内该轨元素半透明显示，播放 / 渲染不显示。</summary>
    public bool Hidden { get; set; }
}

/// <summary>一个视频/覆盖层片段：素材路径（视频）+ 入出点 + 轨道/起始时间 + 显示变换 + 文本/形状。</summary>
public sealed class VideoClip
{
    /// <summary>片段类型：Video（视频素材）/ Text（文本覆盖层）/ Shape（形状覆盖层）/ Filter（滤镜片段）。</summary>
    public string Kind { get; set; } = "Video";
    /// <summary>素材文件路径（Kind=Video）。</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>文本内容（Kind=Text）。</summary>
    public string Text { get; set; } = "文本";
    /// <summary>文本/形状颜色（#AARRGGBB，支持透明度）。</summary>
    public string Color { get; set; } = "#FFFFFFFF";
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

    /// <summary>片段时长（秒）。</summary>
    public double Duration => Math.Max(0.1, OutPoint - InPoint);

    public VideoClip Clone() => (VideoClip)MemberwiseClone();
}

/// <summary>视频工程持久化（JSON，存配置目录）。</summary>
public static class VideoProjectStore
{
    /// <summary>默认工程路径（配置目录\video-project.json）。</summary>
    public static string DefaultPath { get; private set; } = string.Empty;

    public static void Initialize(string configDirectory)
    {
        DefaultPath = Path.Combine(configDirectory, "video-project.json");
    }

    public static VideoProject Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var project = JsonSerializer.Deserialize<VideoProject>(File.ReadAllText(path));
                if (project != null)
                {
                    // 旧版单轨拼接工程迁移为多轨时间轴布局。
                    VideoProject.Normalize(project);
                    return project;
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

        File.WriteAllText(path, JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true }));
    }
}
