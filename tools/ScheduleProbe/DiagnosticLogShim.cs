namespace ClassIslandInjector;

/// <summary>
/// 探针专用 DiagnosticLog shim（与主项目同名同签名，供链接编译的
/// FFmpegVideoDecoder.Log 调用）：无 Avalonia 依赖，直接写文件。
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Sync = new();

    public static bool Enabled { get; set; } = true;

    public static string? CrashLogPath { get; set; }

    public static void Write(string? path, string message)
    {
        if (!Enabled || string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败静默忽略。
        }
    }

    public static void WriteCrash(string? path, string message) => Write(path, message);
}
