using System.Reflection;
using System.Text.Json;

namespace ClassIslandInjector;

/// <summary>
/// 通过反射访问宿主教程系统（宿主 ClassIsland.Core 2.1.x 的 ITutorialService 与教程模型）。
/// 插件编译期引用的 ClassIsland.Core 2.0.0.2 尚不包含教程类型，因此不能强类型引用；
/// 运行时插件加载上下文强制使用宿主的 ClassIsland.Core，类型总是可解析的。
/// 所有调用均 try/catch 兜底，宿主不支持或失败时静默降级，不影响插件功能。
/// </summary>
internal static class HostTutorial
{
    private static readonly Type? TutorialGroupType = FindType("ClassIsland.Core.Models.Tutorial.TutorialGroup");
    private static readonly Type? TutorialServiceType = FindType("ClassIsland.Core.Abstractions.Services.ITutorialService");
    private static readonly Type? IAppHostType = FindType("ClassIsland.Shared.IAppHost");

    /// <summary>宿主教程系统是否可用（教程类型可解析）。</summary>
    public static bool IsSupported => TutorialGroupType != null && TutorialServiceType != null && IAppHostType != null;

    /// <summary>诊断日志路径（由运行时初始化时设置）；失败时写入便于排查。</summary>
    public static string? ErrorLogPath { get; set; }

    /// <summary>
    /// 把教程组 JSON 反序列化为宿主的 TutorialGroup 并注册到
    /// ITutorialService.RegisteredTutorialGroups。多个教程 JSON 可共用同一个
    /// 组 Id：已存在同 Id 分组时，把本次的教程条目合并进该分组（教学中心里
    /// 同一插件的教程聚为一组），否则新增分组。
    /// 失败时把异常写入 <see cref="ErrorLogPath"/> 便于排查。
    /// </summary>
    public static void RegisterGroupFromJson(string json)
    {
        try
        {
            if (TutorialGroupType == null || TutorialServiceType == null)
            {
                Log(
                    $"宿主教程类型解析失败：TutorialGroup={TutorialGroupType?.FullName ?? "null"}, " +
                    $"ITutorialService={TutorialServiceType?.FullName ?? "null"}, " +
                    $"IAppHost={IAppHostType?.FullName ?? "null"}");
                return;
            }

            var group = JsonSerializer.Deserialize(json, TutorialGroupType);
            if (group == null)
            {
                return;
            }

            var groups = TutorialServiceType
                .GetProperty("RegisteredTutorialGroups", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (groups is not System.Collections.IEnumerable enumerable)
            {
                return;
            }

            var groupId = TutorialGroupType.GetProperty("Id")?.GetValue(group) as string;
            foreach (var existing in enumerable)
            {
                if (existing == null)
                {
                    continue;
                }

                var id = existing.GetType().GetProperty("Id")?.GetValue(existing) as string;
                if (id != groupId)
                {
                    continue;
                }

                MergeTutorialsInto(existing, group);
                return;
            }

            groups.GetType().GetMethod("Add")?.Invoke(groups, [group]);
        }
        catch (Exception e)
        {
            Log(e.ToString());
        }
    }

    /// <summary>把 source 组里的教程条目逐个追加到 target 组的 Tutorials 集合。</summary>
    private static void MergeTutorialsInto(object target, object source)
    {
        var targetTutorials = target.GetType().GetProperty("Tutorials")?.GetValue(target);
        var sourceTutorials = source.GetType().GetProperty("Tutorials")?.GetValue(source);
        var add = targetTutorials?.GetType().GetMethod("Add");
        if (add == null || sourceTutorials is not System.Collections.IEnumerable enumerable)
        {
            return;
        }

        foreach (var tutorial in enumerable)
        {
            add.Invoke(targetTutorials, [tutorial]);
        }
    }

    /// <summary>查询指定教程路径（TutorialId/ParagraphId）是否已完成。</summary>
    public static bool GetIsTutorialCompleted(string path)
    {
        try
        {
            if (TutorialServiceType == null || IAppHostType == null)
            {
                return false;
            }

            var tryGetService = IAppHostType.GetMethod("TryGetService", BindingFlags.Public | BindingFlags.Static);
            var service = tryGetService?.MakeGenericMethod(TutorialServiceType).Invoke(null, null);
            return service?.GetType().GetMethod("GetIsTutorialCompleted")?.Invoke(service, [path]) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前是否有教程正在进行。</summary>
    public static bool IsTutorialRunning()
    {
        try
        {
            if (TutorialServiceType == null || IAppHostType == null)
            {
                return false;
            }

            var tryGetService = IAppHostType.GetMethod("TryGetService", BindingFlags.Public | BindingFlags.Static);
            var service = tryGetService?.MakeGenericMethod(TutorialServiceType).Invoke(null, null);
            return service?.GetType().GetProperty("IsTutorialRunning")?.GetValue(service) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开始指定路径（TutorialId/ParagraphId）的未完成教程。</summary>
    public static void BeginNotCompletedTutorials(params string[] paths)
    {
        InvokeOnService("BeginNotCompletedTutorials", [paths]);
    }

    /// <summary>按标签向前推动当前教程句（仅当教程正停在该标签的等待句时生效）。</summary>
    public static void PushToNextSentenceByTag(string tag)
    {
        InvokeOnService("PushToNextSentenceByTag", [tag]);
    }

    /// <summary>把当前等待句向前推进一步（仅当当前句 WaitForNextCommand 时生效）。</summary>
    public static void PushToNextSentence()
    {
        InvokeOnService("PushToNextSentence", [null]);
    }

    /// <summary>
    /// 无条件推进到下一句（不受 WaitForNextCommand 限制）。
    /// 用于非等待句（如「放歌看效果」的按钮句）需要代码主动推进的场景（跳过播放）。
    /// </summary>
    public static void TryStartNextSentence()
    {
        InvokeOnService("TryStartNextSentence", []);
    }

    /// <summary>读取当前教程句的 Tag（无教程进行时返回 null）。</summary>
    public static string? GetCurrentSentenceTag()
    {
        try
        {
            if (TutorialServiceType == null || IAppHostType == null)
            {
                return null;
            }

            var tryGetService = IAppHostType.GetMethod("TryGetService", BindingFlags.Public | BindingFlags.Static);
            var service = tryGetService?.MakeGenericMethod(TutorialServiceType).Invoke(null, null);
            if (service == null)
            {
                return null;
            }

            var sentence = service.GetType().GetProperty("CurrentSentence")?.GetValue(service);
            return sentence?.GetType().GetProperty("Tag")?.GetValue(sentence) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void InvokeOnService(string methodName, object?[] args)
    {
        try
        {
            if (TutorialServiceType == null || IAppHostType == null)
            {
                return;
            }

            var tryGetService = IAppHostType.GetMethod("TryGetService", BindingFlags.Public | BindingFlags.Static);
            var service = tryGetService?.MakeGenericMethod(TutorialServiceType).Invoke(null, null);
            if (service == null)
            {
                return;
            }

            service.GetType().GetMethod(methodName)?.Invoke(service, args);
        }
        catch (Exception e)
        {
            Log($"调用 {methodName} 失败：{e}");
        }
    }

    private static void Log(string message)
    {
        if (string.IsNullOrEmpty(ErrorLogPath))
        {
            return;
        }

        // 统一经 DiagnosticLog 门面写入：全局开关关闭时静默丢弃。
        DiagnosticLog.Write(ErrorLogPath, message);
    }

    private static Type? FindType(string fullName)
    {
        try
        {
            var type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }
        }
        catch
        {
            // 继续尝试遍历已加载程序集。
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // 忽略单个类型解析失败。
            }
        }

        return null;
    }
}
