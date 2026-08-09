using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassIslandInjector;

/// <summary>
/// 宿主点位对照表中的单条记录。
/// </summary>
public sealed class ContractEntry
{
    /// <summary>插件逻辑名对应的宿主实际字符串（控件名 / 类型全名 / 反射成员名 / GUID 等）。</summary>
    public string Value { get; set; } = "";

    /// <summary>该点位对应的功能说明（用于健康检查降级报告与设置页提示）。</summary>
    public string? Feature { get; set; }

    /// <summary>是否可选项：宿主特定主题/布局下可能不存在的点位，失效不计入降级。</summary>
    public bool Optional { get; set; }

    /// <summary>
    /// 反射成员的宿主类型定位（仅 memberNames 使用）：
    /// settings / mainWindow / mainWindowLine / notificationRequest / effectWindow / effectViewModel。
    /// </summary>
    public string? Target { get; set; }

    public ContractEntry()
    {
    }

    public ContractEntry(string value, string? feature = null, string? target = null)
    {
        Value = value;
        Feature = feature;
        Target = target;
    }
}

/// <summary>
/// 一类点位（控件名 / 类型名 / 反射成员名等）的分组字典：键 = 插件逻辑名，值 = 宿主字符串。
/// </summary>
public sealed class ContractGroup : Dictionary<string, ContractEntry>
{
    public ContractGroup()
    {
    }

    public ContractGroup(IDictionary<string, ContractEntry> source) : base(source)
    {
    }
}

/// <summary>
/// 宿主点位对照表（ContractCatalog）：包含插件访问 ClassIsland 宿主所需的全部
/// 魔法字符串、适配的宿主版本信息、制作时间与制作人。可序列化为 JSON 挂在网站上，
/// 用户按宿主版本下载对应对照表后，插件在运行时用其覆盖内置默认值恢复工作。
/// </summary>
public sealed class ContractCatalog
{
    /// <summary>对照表 JSON 结构版本号。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>对照表唯一标识（如 classisland-2.1.0）。</summary>
    public string Id { get; set; } = "builtin";

    /// <summary>对照表显示名称。</summary>
    public string Name { get; set; } = "内置默认";

    /// <summary>适配的宿主最低版本（含），如 2.1.0.0。</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>适配的宿主最高版本（含），如 2.2.0.0。</summary>
    public string? MaxHostVersion { get; set; }

    /// <summary>适配的 ClassIsland 插件 API 版本（如 2.0.0.0）。</summary>
    public string? ApiVersion { get; set; }

    /// <summary>对照表制作时间。</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>对照表制作人。</summary>
    public string Author { get; set; } = "";

    /// <summary>对照表 JSON 的下载地址（索引列表里展示用）。</summary>
    public string? SourceUrl { get; set; }

    /// <summary>说明（适配内容、已知限制等）。</summary>
    public string? Notes { get; set; }

    // ---- 点位分组 ----

    /// <summary>命名控件（宿主 MainWindow.axaml 中的 x:Name）。</summary>
    public ContractGroup ControlNames { get; set; } = new();

    /// <summary>控件类型全名。</summary>
    public ContractGroup TypeNames { get; set; } = new();

    /// <summary>程序集名。</summary>
    public ContractGroup AssemblyNames { get; set; } = new();

    /// <summary>反射成员名（属性/字段，含 Target 定位）。</summary>
    public ContractGroup MemberNames { get; set; } = new();

    /// <summary>注入样式类名。</summary>
    public ContractGroup ClassNames { get; set; } = new();

    /// <summary>GUID 字符串。</summary>
    public ContractGroup Guids { get; set; } = new();

    /// <summary>伪类名（如 :mask-in）。</summary>
    public ContractGroup PseudoClasses { get; set; } = new();

    // ---- 快捷访问 ----

    public ContractEntry? Find(string groupName, string key)
    {
        var group = groupName switch
        {
            nameof(ControlNames) => ControlNames,
            nameof(TypeNames) => TypeNames,
            nameof(AssemblyNames) => AssemblyNames,
            nameof(MemberNames) => MemberNames,
            nameof(ClassNames) => ClassNames,
            nameof(Guids) => Guids,
            nameof(PseudoClasses) => PseudoClasses,
            _ => null
        };
        return group != null && group.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// 判断本对照表是否适配指定宿主版本。
    /// 内置默认（未声明版本区间）视为适配任意版本。
    /// </summary>
    public bool Matches(string hostVersion)
    {
        if (string.IsNullOrWhiteSpace(MinHostVersion) && string.IsNullOrWhiteSpace(MaxHostVersion))
        {
            return true;
        }

        var ok = Version.TryParse(hostVersion, out var version);
        if (!ok)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MinHostVersion) &&
            Version.TryParse(MinHostVersion, out var min) && version < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(MaxHostVersion) &&
            Version.TryParse(MaxHostVersion, out var max) && version > max)
        {
            return false;
        }

        return true;
    }

    // ---- JSON ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 网站/用户手工编辑的对照表均为 camelCase（如 controlNames/tables/minHostVersion），
        // 读取必须大小写不敏感，否则无法绑定到 PascalCase 属性导致列表/分组为空。
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析对照表 JSON；格式非法时返回 null。</summary>
    public static ContractCatalog? FromJson(string json)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<ContractCatalog>(json, JsonOptions);
            if (catalog == null)
            {
                return null;
            }

            return catalog.SchemaVersion >= 1 ? catalog : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 内置默认对照表：与插件当前编译期 HostContract 默认值完全一致，
    /// 作为「零联网」兜底与联网对照表的基线。
    /// </summary>
    public static ContractCatalog BuiltIn { get; } = BuildBuiltIn();

    private static ContractCatalog BuildBuiltIn()
    {
        var catalog = new ContractCatalog
        {
            Id = "builtin",
            Name = "内置默认（随插件版本）",
            Author = "ClassIslandInjector 内置",
            CreatedAt = new DateTime(2026, 8, 8),
            Notes = "随插件编译进代码的默认对照表，适配插件发布时对应的 ClassIsland 宿主版本；" +
                    "若宿主更新后功能失效，请从插件网站的索引中下载与当前宿主版本匹配的对照表。"
        };

        // ---- 控件名 ----
        catalog.ControlNames["StackPanelRootContainer"] = new("StackPanelRootContainer", "主界面根容器（不透明度/变形）");
        catalog.ControlNames["WindowRoot"] = new("WindowRoot", "窗口根网格（特效宿泊）");
        catalog.ControlNames["ResourceLoaderBorder"] = new("ResourceLoaderBorder", "样式宿主（覆盖样式表注入点）");
        catalog.ControlNames["WorkingRoot"] = new("WorkingRoot", "客户区根（布局边界）");
        catalog.ControlNames["RootLayoutTransformControl"] = new("RootLayoutTransformControl", "根布局缩放控件");
        catalog.ControlNames["GridRoot"] = new("GridRoot", "主界面网格根（底图/纹理宿主）");
        catalog.ControlNames["BackgroundBorder"] = new("BackgroundBorder", "每行主界面背景（底色/边框/圆角）");
        catalog.ControlNames["BackgroundBorderWrapper"] = new("BackgroundBorderWrapper", "Fluent 主题背景容器") { Optional = true };
        catalog.ControlNames["BackgroundBorderOverlayMask"] = new("BackgroundBorderOverlayMask", "背景遮罩");
        catalog.ControlNames["OverlayMask"] = new("OverlayMask", "通知遮罩");
        catalog.ControlNames["GridOverlay"] = new("GridOverlay", "覆盖层宿主（即将上课/倒计时箭头）");

        // ---- 类型全名 ----
        catalog.TypeNames["MainWindowLine"] = new("ClassIsland.Controls.MainWindowLine", "主界面行（提醒/覆盖层/纹理宿主）");
        catalog.TypeNames["SlideComponent"] = new("ClassIsland.Controls.Components.SlideComponent", "轮播容器（翻页动画）");
        catalog.TypeNames["AvaloniaRuntimeXamlLoader"] = new("Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader", "运行时 XAML 加载器");

        // ---- 程序集名 ----
        catalog.AssemblyNames["AvaloniaXamlLoader"] = new("Avalonia.Markup.Xaml.Loader", "运行时 XAML 加载器程序集");

        // ---- 反射成员名 ----
        catalog.MemberNames["Settings"] = new("Settings", "宿主设置对象", "app");
        catalog.MemberNames["IsIslandSeperated"] = new("IsIslandSeperated", "分体主界面开关（注意宿主拼写）", "settings");
        catalog.MemberNames["RadiusX"] = new("RadiusX", "原生圆角 X", "settings");
        catalog.MemberNames["RadiusY"] = new("RadiusY", "原生圆角 Y", "settings");
        catalog.MemberNames["IsMouseInFadingEnabled"] = new("IsMouseInFadingEnabled", "鼠标悬停淡出开关", "settings");
        catalog.MemberNames["LastWeatherInfo"] = new("LastWeatherInfo", "天气数据（虚假天气）", "settings");
        catalog.MemberNames["Theme"] = new("Theme", "主题模式（动态主题色）", "settings");
        catalog.MemberNames["ColorSource"] = new("ColorSource", "主题色来源（动态主题色）", "settings");
        catalog.MemberNames["PrimaryColor"] = new("PrimaryColor", "自定义主色（动态主题色）", "settings");
        catalog.MemberNames["SelectedPlatte"] = new("SelectedPlatte", "壁纸/屏幕取色（动态主题色）", "settings");
        catalog.MemberNames["MaskContent"] = new("MaskContent", "提醒遮罩内容", "mainWindowLine");
        catalog.MemberNames["OverlayContent"] = new("OverlayContent", "提醒覆盖内容", "mainWindowLine");
        catalog.MemberNames["CurrentNotificationRequest"] = new("CurrentNotificationRequest", "当前通知请求", "mainWindowLine");
        catalog.MemberNames["PseudoClasses"] = new("PseudoClasses", "伪类集合（提醒动画）", "mainWindowLine");
        catalog.MemberNames["ChannelId"] = new("ChannelId", "通知频道 ID", "notificationRequest");
        catalog.MemberNames["TopmostEffectWindow"] = new("TopmostEffectWindow", "全屏特效窗口", "mainWindow");
        catalog.MemberNames["TopmostEffectWindowBackingField"] = new("<TopmostEffectWindow>k__BackingField", "特效窗口后备字段", "mainWindow");
        catalog.MemberNames["ViewModel"] = new("ViewModel", "特效窗口视图模型", "effectWindow");
        catalog.MemberNames["EffectControls"] = new("EffectControls", "特效集合", "effectViewModel");

        // ---- 样式类名 ----
        catalog.ClassNames["InjectorWindowClass"] = new("classisland-injector", "注入样式类（窗口）");
        catalog.ClassNames["InjectorRootClass"] = new("classisland-injector-root", "注入样式类（主界面根）");

        // ---- GUID ----
        catalog.Guids["PrepareOnClassChannelId"] = new("CDDFE7FF-B904-4C73-B458-82793B2F66E9", "即将上课倒计时频道");

        // ---- 伪类 ----
        catalog.PseudoClasses["MaskIn"] = new(":mask-in", "提醒遮罩进入");
        catalog.PseudoClasses["MaskOut"] = new(":mask-out", "提醒遮罩退出");

        return catalog;
    }
}
