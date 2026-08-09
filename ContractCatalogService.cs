using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ClassIsland.Core;

namespace ClassIslandInjector;

/// <summary>
/// 健康检查发现的失效点位（宿主更新后对照表未跟进时出现，代表某个功能已降级）。
/// </summary>
public sealed class ContractDegradation
{
    /// <summary>点位分组（控件名 / 类型名 / 反射成员）。</summary>
    public string Group { get; }

    /// <summary>插件逻辑名。</summary>
    public string Key { get; }

    /// <summary>对照表中配置的宿主字符串。</summary>
    public string Value { get; }

    /// <summary>受影响的插件功能说明。</summary>
    public string Feature { get; }

    public ContractDegradation(string group, string key, string value, string feature)
    {
        Group = group;
        Key = key;
        Value = value;
        Feature = feature;
    }
}

/// <summary>
/// 索引文件中的一条对照表记录（索引是网站上的轻量列表，指向各对照表 JSON）。
/// </summary>
public sealed class ContractIndexEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MinHostVersion { get; set; }
    public string? MaxHostVersion { get; set; }
    public string? ApiVersion { get; set; }
    public string? CreatedAt { get; set; }
    public string? Author { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }

    /// <summary>使用该对照表要求的最低插件版本（留空表示无限制）；用户插件低于此版本时设置页顶部提醒更新。</summary>
    public string? MinPluginVersion { get; set; }

    /// <summary>下拉列表展示文本（名称 + 适配版本区间）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(MinHostVersion) && string.IsNullOrWhiteSpace(MaxHostVersion)
        ? Name
        : $"{Name}（{MinHostVersion} ~ {MaxHostVersion}）";

    public override string ToString() => Display;
}

/// <summary>
/// 网站上的对照表索引文件（列出所有可用对照表，供用户按宿主版本选择）。
/// </summary>
public sealed class ContractIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string? IndexUrl { get; set; }
    public string? UpdatedAt { get; set; }
    public List<ContractIndexEntry> Tables { get; set; } = [];
}

/// <summary>
/// 宿主对照表（ContractCatalog）服务：
/// 管理内置/下载对照表、联网获取索引与下载对照表、运行时切换并持久化、健康检查与降级检测。
/// </summary>
internal static class ContractCatalogService
{
    private const string CatalogFileName = "contract-catalog.json";

    /// <summary>插件网站上的对照表索引地址（写死，用户无需填写）。</summary>
    public const string DefaultIndexUrl = "https://xxtsoft.top/support/injector/tables/index.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>索引/对照表 JSON 均来自网站（camelCase），读取必须大小写不敏感。</summary>
    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>插件配置目录。</summary>
    public static string ConfigDirectory { get; private set; } = "";

    /// <summary>持久化对照表文件路径（下载应用后写入）。</summary>
    public static string CatalogFilePath { get; private set; } = "";

    /// <summary>当前生效的对照表。</summary>
    public static ContractCatalog Current { get; private set; } = ContractCatalog.BuiltIn;

    /// <summary>当前生效对照表是否为内置默认。</summary>
    public static bool IsBuiltIn => ReferenceEquals(Current, ContractCatalog.BuiltIn);

    /// <summary>健康检查发现的失效点位（宿主更新后未跟进对照表时非空）。</summary>
    public static IReadOnlyList<ContractDegradation> Degradations { get; private set; } = [];

    /// <summary>对照表切换后触发（供设置页刷新）。</summary>
    public static event EventHandler? CatalogChanged;

    public static void Initialize(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        CatalogFilePath = Path.Combine(configDirectory, CatalogFileName);

        // 启动时加载持久化的联网对照表；缺失/损坏则使用内置默认。
        try
        {
            if (File.Exists(CatalogFilePath))
            {
                var loaded = ContractCatalog.FromJson(File.ReadAllText(CatalogFilePath));
                if (loaded != null)
                {
                    HostContract.Apply(loaded);
                    Current = loaded;
                }
            }
        }
        catch
        {
            // 忽略：损坏的对照表回退为内置默认。
        }

        RunHealthCheck();
    }

    /// <summary>
    /// 切换生效对照表并持久化（内置默认会删除持久化文件）。随后应重新 Attach 注入器。
    /// </summary>
    public static void SetActive(ContractCatalog catalog)
    {
        if (catalog == null)
        {
            return;
        }

        HostContract.Apply(catalog);
        Current = catalog;

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (ReferenceEquals(catalog, ContractCatalog.BuiltIn))
            {
                if (File.Exists(CatalogFilePath))
                {
                    File.Delete(CatalogFilePath);
                }
            }
            else
            {
                File.WriteAllText(CatalogFilePath, catalog.ToJson());
            }
        }
        catch
        {
            // 忽略：持久化失败不影响本次应用。
        }

        RunHealthCheck();
        CatalogChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>获取当前 ClassIsland 宿主版本号（用于对照表匹配与展示）。</summary>
    public static string GetHostVersion()
    {
        try
        {
            return AppBase.AppVersion;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }

    // ============ 联网 ============

    /// <summary>从网站获取对照表索引（列出所有可用对照表）。</summary>
    public static async Task<ContractIndex> FetchIndexAsync(string indexUrl)
    {
        var json = await Http.GetStringAsync(indexUrl).ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<ContractIndex>(json, IndexJsonOptions);
        return index ?? new ContractIndex();
    }

    /// <summary>下载并解析指定对照表 JSON；格式非法时抛异常。</summary>
    public static async Task<ContractCatalog> DownloadAsync(string url)
    {
        var json = await Http.GetStringAsync(url).ConfigureAwait(false);
        return ContractCatalog.FromJson(json) ?? throw new InvalidDataException("对照表文件格式无效。");
    }

    // ============ 健康检查 ============

    /// <summary>
    /// 逐项检查当前对照表点位是否能在宿主中解析：
    /// 控件名/类型名 → 主窗口可视树扫描；反射成员 → 目标类型反射查找。
    /// 失效项写入 <see cref="Degradations"/>，供设置页顶部 InfoBar 提示降级。
    /// </summary>
    public static void RunHealthCheck()
    {
        var list = new List<ContractDegradation>();
        try
        {
            var mainWindow = AppBase.Current.MainWindow;
            if (mainWindow == null)
            {
                Degradations = [];
                return;
            }

            var descendants = mainWindow.GetVisualDescendants().OfType<Control>().ToArray();
            var controlNames = new HashSet<string>(descendants
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!));
            var typeNames = new HashSet<string>(descendants
                .Select(c => c.GetType().FullName ?? "")
                .Where(n => n.Length > 0));

            // 控件名
            foreach (var (key, entry) in Current.ControlNames)
            {
                if (!controlNames.Contains(entry.Value) && !entry.Optional)
                {
                    list.Add(new ContractDegradation("控件名", key, entry.Value, entry.Feature ?? key));
                }
            }

            // 类型名（AvaloniaRuntimeXamlLoader 不在可视树中，改为反射查类型）
            foreach (var (key, entry) in Current.TypeNames)
            {
                var ok = typeNames.Contains(entry.Value) || FindType(entry.Value) != null;
                if (!ok && !entry.Optional)
                {
                    list.Add(new ContractDegradation("类型名", key, entry.Value, entry.Feature ?? key));
                }
            }

            // 反射成员
            var settingsType = ResolveTargetType("settings", mainWindow, null, null);
            var appType = AppBase.Current?.GetType();
            var mainWindowType = mainWindow.GetType();
            foreach (var (key, entry) in Current.MemberNames)
            {
                var targetType = ResolveTargetType(entry.Target, mainWindow, settingsType, descendants);
                if (targetType == null)
                {
                    // 目标类型当前不可得（如特效窗口尚未创建），无法判定，跳过。
                    continue;
                }

                if (!HasMemberAny(entry.Value, targetType, settingsType, appType, mainWindowType) && !entry.Optional)
                {
                    list.Add(new ContractDegradation("反射成员", key, entry.Value, entry.Feature ?? key));
                }
            }
        }
        catch
        {
            // 健康检查失败不阻断；保持原结果。
        }

        Degradations = list;
    }

    /// <summary>在类型及其基类层级中查找成员（属性或字段，含非公开成员）。</summary>
    private static bool HasMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.GetProperty(name, flags) != null || t.GetField(name, flags) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在目标类型上查找成员；找不到时回退到宿主设置对象 / App / 主窗口类型，
    /// 兼容旧版对照表把 Settings 的 Target 误标为 settings 的情况（Settings 属性在 App 上）。
    /// </summary>
    private static bool HasMemberAny(string memberName, Type? primary, Type? settingsType, Type? appType, Type? mainWindowType)
    {
        if (primary != null && HasMember(primary, memberName))
        {
            return true;
        }

        foreach (var fallback in new[] { settingsType, appType, mainWindowType })
        {
            if (fallback != null && !ReferenceEquals(fallback, primary) && HasMember(fallback, memberName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把对照表的 Target 定位转换为宿主类型；无法解析时返回 null。</summary>
    private static Type? ResolveTargetType(string? target, Window mainWindow, Type? settingsType, Control[]? descendants)
    {
        switch (target)
        {
            case "settings":
                return settingsType ??= GetSettingsType();
            case "app":
                return AppBase.Current?.GetType();
            case "mainWindow":
                return mainWindow.GetType();
            case "mainWindowLine":
                return GetMainWindowLineType(descendants ?? mainWindow.GetVisualDescendants().OfType<Control>().ToArray());
            case "notificationRequest":
                return typeof(ClassIsland.Core.Models.Notification.NotificationRequest);
            case "effectWindow":
            {
                var value = mainWindow.GetType()
                    .GetProperty(HostContract.TopmostEffectWindowProperty, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(mainWindow);
                return value?.GetType();
            }
            case "effectViewModel":
            {
                var effectWindow = mainWindow.GetType()
                    .GetProperty(HostContract.TopmostEffectWindowProperty, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(mainWindow);
                return effectWindow?.GetType()
                    .GetProperty(HostContract.ViewModelProperty, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(effectWindow)?.GetType();
            }
            default:
                return null;
        }
    }

    private static Type? GetSettingsType()
    {
        var app = AppBase.Current;
        return app?.GetType()
            .GetProperty(HostContract.SettingsProperty, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(app)?.GetType();
    }

    private static Type? GetMainWindowLineType(Control[] descendants)
    {
        var found = descendants.FirstOrDefault(c => c.GetType().FullName == HostContract.MainWindowLineTypeName);
        if (found != null)
        {
            return found.GetType();
        }

        return FindType(HostContract.MainWindowLineTypeName);
    }

    /// <summary>在已加载程序集中按全名查找类型。</summary>
    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // 个别程序集反射失败时继续。
            }
        }

        return null;
    }
}
