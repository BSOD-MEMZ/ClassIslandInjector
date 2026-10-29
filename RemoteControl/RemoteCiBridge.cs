using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Enums;

namespace ClassIslandInjector.RemoteControl;

/// <summary>
/// RemoteCI 远程管理的安全入口（本类不引用任何 RemoteCI 类型）。
/// <para>
/// RemoteCI 是<b>可选</b>依赖：未安装时 <see cref="RemoteCiIntegration"/> 里的类型根本解析不到，
/// JIT 一编译就抛 <see cref="FileNotFoundException"/>，且发生在调用方 try 之前。
/// 所以真正触碰 RemoteCI 的调用都隔离在 NoInlining 方法里，由这里的 try/catch 包住（同 WinRT 的防御写法）。
/// </para>
/// </summary>
internal static class RemoteCiBridge
{
    /// <summary>RemoteCI 插件 Id。</summary>
    public const string RemoteCiPluginId = "remoteci.plugin";

    /// <summary>RemoteCI 程序集名前缀：只有这些程序集会被重定向到 RemoteCI 的加载上下文。</summary>
    private const string RemoteCiAssemblyPrefix = "RemoteCI.";

    private static bool _resolverInstalled;

    /// <summary>是否已成功接入 RemoteCI。</summary>
    public static bool IsAttached { get; private set; }

    /// <summary>
    /// 本机设置页当前是否被远程锁定：只在 RemoteCI 已接入时生效（卸载 / 停用 / 不连接 RemoteCI 后锁定自动失效，避免本机被永久锁死）。
    /// </summary>
    public static bool IsLocalEditingLocked => IsAttached && RemoteManagementState.LockLocalEditing;

    /// <summary>接入状态说明（设置页展示 / 排查用）。</summary>
    public static string StatusText { get; private set; } = "尚未检测";

    private static string LogPath => Path.Combine(InjectorRuntime.ConfigDirectory, "remote-control.log");

    /// <summary>
    /// 远程管理日志（谁在什么时候远程改了什么）按审计日志写：不受「输出诊断日志」开关控制——
    /// 那个开关本身就能被远程修改，跟着它走等于让远程把自己的操作记录关掉。
    /// </summary>
    public static void Log(string message) => DiagnosticLog.WriteAudit(LogPath, message);

    /// <summary>在宿主 AppStarted 之后调用：RemoteCI 已加载则注册分组与远程功能，否则静默跳过。</summary>
    public static void TryAttach()
    {
        if (IsAttached)
        {
            return;
        }

        if (RemoteManagementState.DisconnectRemoteCi)
        {
            StatusText = "已在设置页「连接器」中选择不连接 RemoteCI";
            return;
        }

        if (!IsRemoteCiLoaded())
        {
            StatusText = "未安装或未启用 RemoteCI 插件";
            return;
        }

        try
        {
            EnsureAssemblyResolver();
            if (AttachCore())
            {
                IsAttached = true;
                StatusText = "已接入 RemoteCI，可在 WebUI「扩展插件 → 样式注入器」统一管理";
            }
            else
            {
                StatusText = "RemoteCI 版本过旧（未提供扩展注册表）";
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException
                                       or MissingMethodException or MissingMemberException or InvalidCastException)
        {
            // 类型对不上：通常是没能重定向到 RemoteCI 的那份程序集（类型来自两份程序集）或 RemoteCI 版本缺少扩展分组 API。
            StatusText = "RemoteCI 版本不兼容（需要支持扩展分组的版本）";
            Log($"接入 RemoteCI 失败（版本/类型不兼容）：{ex}");
        }
        catch (Exception ex)
        {
            StatusText = $"接入 RemoteCI 失败：{ex.Message}";
            Log($"接入 RemoteCI 失败：{ex}");
        }

        Log(StatusText);
    }

    /// <summary>
    /// 本机设置页「不连接 RemoteCI」开关：写入状态并立即断开 / 重新接入（不必重启宿主）。
    /// 断开后 <see cref="IsAttached"/> 为 false，远程锁定随之失效。
    /// </summary>
    public static void SetDisconnected(bool disconnect)
    {
        RemoteManagementState.SetDisconnectRemoteCi(disconnect);
        if (disconnect)
        {
            Detach();
            StatusText = "已在设置页「连接器」中选择不连接 RemoteCI";
            Log("本机选择不连接 RemoteCI，已注销远程管理");
        }
        else
        {
            TryAttach();
        }
    }

    /// <summary>
    /// 从 RemoteCI 注销分组与远程功能（本机选择「不连接 RemoteCI」时）。
    /// 宿主退出时刻意<b>不</b>调用：注销会让 RemoteCI 把不含本分组的列表同步给服务端，
    /// 服务端整体替换该班的快照，教室电脑关机期间 WebUI 就没有「样式注入器」设置页，学校也没法给离线班级排队下发。
    /// RemoteCI 自己退出时会断开一切，用不着本插件注销。
    /// </summary>
    public static void Detach()
    {
        if (!IsAttached)
        {
            return;
        }

        IsAttached = false;
        try
        {
            DetachCore();
        }
        catch (Exception ex)
        {
            Log($"注销 RemoteCI 扩展失败：{ex.Message}");
        }
    }

    private static bool IsRemoteCiLoaded()
    {
        try
        {
            return IPluginService.LoadedPlugins.Any(p =>
                p.Manifest.Id == RemoteCiPluginId && p.LoadStatus == PluginLoadStatus.Loaded);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 让本插件的加载上下文把 <c>RemoteCI.*</c> 程序集解析到 RemoteCI 插件自己加载的那一份（接口类型才对得上）。
    /// <para>
    /// 不在 manifest 里声明对 RemoteCI 的依赖：宿主 PluginLoadContext 会把<b>所有</b>程序集名先委托给依赖插件，
    /// 连 NAudio 等共有依赖也会改用 RemoteCI 的那份，影响插件原有功能。这里只处理 RemoteCI 自己的程序集：
    /// 它们不在本插件目录（编译期引用 Private=false），PluginLoadContext.Load 与默认上下文都解析不到，
    /// 才会落到 <see cref="AssemblyLoadContext.Resolving"/>，其余程序集的解析完全不受影响。
    /// </para>
    /// </summary>
    private static void EnsureAssemblyResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }

        var own = AssemblyLoadContext.GetLoadContext(typeof(RemoteCiBridge).Assembly);
        if (own == null)
        {
            return;
        }

        own.Resolving += ResolveFromRemoteCi;
        _resolverInstalled = true;
    }

    private static Assembly? ResolveFromRemoteCi(AssemblyLoadContext requester, AssemblyName name)
    {
        if (name.Name == null || !name.Name.StartsWith(RemoteCiAssemblyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var context = FindRemoteCiLoadContext();
            if (context == null || ReferenceEquals(context, requester))
            {
                return null;
            }

            return context.Assemblies.FirstOrDefault(a =>
                       string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                   ?? context.LoadFromAssemblyName(name);
        }
        catch (Exception ex)
        {
            Log($"重定向 {name.Name} 到 RemoteCI 加载上下文失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 找 RemoteCI 插件的加载上下文：优先按宿主的命名约定（<c>ClassIsland.PluginLoadContext[插件Id]</c>），
    /// 命名约定变了就退回「谁加载了 RemoteCI.Plugin」。
    /// </summary>
    private static AssemblyLoadContext? FindRemoteCiLoadContext()
    {
        var contexts = AssemblyLoadContext.All.ToList();
        return contexts.FirstOrDefault(c => string.Equals(c.Name,
                   $"ClassIsland.PluginLoadContext[{RemoteCiPluginId}]", StringComparison.OrdinalIgnoreCase))
               ?? contexts.FirstOrDefault(c => c.Assemblies.Any(a =>
                   string.Equals(a.GetName().Name, "RemoteCI.Plugin", StringComparison.OrdinalIgnoreCase)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AttachCore() => RemoteCiIntegration.Register();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DetachCore() => RemoteCiIntegration.Unregister();
}
