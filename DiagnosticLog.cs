using Avalonia.Threading;

namespace ClassIslandInjector;

/// <summary>
/// 全局诊断日志门面：所有日志文件（album-color / preview-debug / canvas-debug /
/// tutorial-error / crash）统一经此写入，避免各处重复「追加 + 时间戳 + try/catch」样板。
///
/// - 全局开关 <see cref="Enabled"/>：为 false 时所有常规日志静默丢弃（省磁盘 IO），
///   由设置项「输出诊断日志」驱动，经 <see cref="InjectorRuntime"/> 同步。
/// - 崩溃兜底 <see cref="WriteCrash"/> 始终尝试写入（不受开关影响）——
///   漏网异常是最需要记录的故障，不应被开关屏蔽。
/// - 所有写入 try/catch 静默吞掉：日志本身绝不抛异常、绝不弹窗、绝不影响功能。
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Sync = new();

    /// <summary>全局日志开关（由设置驱动）。为 false 时常规日志静默丢弃。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>crash.log 路径（由运行时初始化时设置；未设置时崩溃日志丢弃）。</summary>
    public static string? CrashLogPath { get; set; }

    /// <summary>
    /// 写入指定日志文件（追加一行，带毫秒时间戳）。文件级失败静默忽略。
    /// 全局开关关闭或路径为空时直接返回。带锁保证并发写入不交错。
    /// </summary>
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
            // 日志失败不影响功能。
        }
    }

    /// <summary>
    /// 崩溃兜底日志（crash.log）：即使常规日志被关闭也照常写入。
    /// 时间戳带日期，方便跨天排查。
    /// </summary>
    public static void WriteCrash(string message)
    {
        var path = CrashLogPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响功能。
        }
    }

    /// <summary>
    /// 注册全局异常兜底：记录一切漏网异常，且**不打扰用户**——
    /// UI 线程异常标记 Handled 吞掉（不弹窗、不崩溃），任务异常 SetObserved
    /// 防止进程终结时被终止，AppDomain 未处理异常照常记录（进程可能仍会退出，
    /// 但至少留下 crash.log 供排查）。任何注册失败都静默忽略。
    /// </summary>
    public static void RegisterGlobalHandlers()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteCrash($"未处理异常 (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
        }
        catch
        {
            // 注册失败不影响功能。
        }

        try
        {
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrash($"未观察任务异常: {e.Exception}");
                // 标记已观察，避免异常在终结时被当作崩溃终止进程。
                e.SetObserved();
            };
        }
        catch
        {
            // 注册失败不影响功能。
        }

        try
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                WriteCrash($"UI 线程未处理异常: {e.Exception}");
                // 吞掉异常，不让用户觉得不对，只静默写入日志。
                e.Handled = true;
            };
        }
        catch
        {
            // 注册失败（如 Dispatcher 尚未就绪）不影响功能。
        }
    }
}
