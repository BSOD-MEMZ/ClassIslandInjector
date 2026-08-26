using ClassIsland.Core;
using Microsoft.Win32;

namespace ClassIslandInjector;

/// <summary>
/// 预设包文件关联（.cizip → ClassIsland --uri 安装协议）。
/// 双击预设包时 Windows 启动 ClassIsland.exe 并带上
/// <c>--uri "classisland://plugins/classisland.injector/install?file=&lt;路径&gt;"</c>，
/// 由 ClassIsland 的 Uri 导航（冷启动自导航 / 热启动 IPC 转发）分发到插件的安装处理器。
/// 注册写入 HKCU（当前用户，无需管理员）。
/// </summary>
internal static class PresetFileAssociation
{
    /// <summary>预设包专属后缀名（独一无二，不与 .cipx 等冲突）。</summary>
    public const string Extension = ".cizip";

    /// <summary>注册表中 ProgId（扩展名指向的类标识）。</summary>
    public const string ProgId = "ClassIslandInjector.PresetPackage";

    /// <summary>文件关联的显示名称（资源管理器右键「打开方式」中显示）。</summary>
    public const string FriendlyName = "ClassIsland 预设包";

    /// <summary>双击时传给 ClassIsland 的安装 Uri 模板（%1 由 Windows 替换为文件路径）。</summary>
    public const string InstallUriTemplate = "classisland://plugins/classisland.injector/install?file=%1";

    /// <summary>注册 .cizip 文件关联（当前用户）。</summary>
    public static void Register()
    {
        var exePath = AppBase.ExecutingEntrance;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            // 拿不到宿主可执行文件（极罕见），回退到当前进程路径。
            exePath = Environment.ProcessPath ?? string.Empty;
        }

        var classes = Registry.CurrentUser.OpenSubKey("Software\\Classes", RegistryKeyPermissionCheck.ReadWriteSubTree);
        if (classes == null)
        {
            return;
        }

        // .cizip → ProgId
        using (var extKey = classes.CreateSubKey(Extension))
        {
            extKey.SetValue("", ProgId);
        }

        // ProgId 的打开命令
        using (var progIdKey = classes.CreateSubKey(ProgId))
        {
            progIdKey.SetValue("", FriendlyName);
        }

        using (var shellKey = classes.CreateSubKey($"{ProgId}\\shell\\open\\command"))
        {
            shellKey.SetValue("", $"\"{exePath}\" --uri \"{InstallUriTemplate}\"");
        }

        // 默认图标（可选）：直接用 ClassIsland 的图标。
        using (var iconKey = classes.CreateSubKey($"{ProgId}\\DefaultIcon"))
        {
            iconKey.SetValue("", $"\"{exePath}\",0");
        }
    }

    /// <summary>移除 .cizip 文件关联（当前用户）。</summary>
    public static void Unregister()
    {
        var classes = Registry.CurrentUser.OpenSubKey("Software\\Classes", RegistryKeyPermissionCheck.ReadWriteSubTree);
        if (classes == null)
        {
            return;
        }

        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree(Extension, throwOnMissingSubKey: false);
    }

    /// <summary>当前是否已注册 .cizip 文件关联。</summary>
    public static bool IsRegistered()
    {
        try
        {
            var classes = Registry.CurrentUser.OpenSubKey("Software\\Classes");
            if (classes == null)
            {
                return false;
            }

            using var extKey = classes.OpenSubKey(Extension);
            if (extKey == null)
            {
                return false;
            }

            var progId = extKey.GetValue("") as string;
            return string.Equals(progId, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按开关确保关联状态（开启则注册，关闭则移除）。</summary>
    public static void Ensure(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!IsRegistered())
                {
                    Register();
                }
            }
            else if (IsRegistered())
            {
                Unregister();
            }
        }
        catch
        {
            // 注册表不可写（如受控环境）时静默失败，不干扰主流程。
        }
    }
}
