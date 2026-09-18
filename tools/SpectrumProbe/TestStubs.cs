namespace ClassIslandInjector;

/// <summary>
/// tools/ 下独立探针项目的编译桩：只提供被引用文件所需的宿主侧类型，
/// 使探针能在不编译整个插件的前提下链接 AudioSpectrumCapture 等文件的源码。
/// 绝不参与插件主项目编译（主项目已排除 tools\**）。
/// </summary>
internal static class InjectorRuntime
{
    /// <summary>插件配置目录（探针里指向临时目录，诊断日志会被写到这里）。</summary>
    public static string? ConfigDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "ClassIslandInjectorProbe");
}

/// <summary>诊断日志门面桩：探针直接打到控制台，便于观察捕获链路。</summary>
internal static class DiagnosticLog
{
    public static bool Enabled { get; set; } = true;

    public static void Write(string? path, string message)
    {
        if (!Enabled)
        {
            return;
        }

        Console.WriteLine($"[log] {message}");
    }
}
