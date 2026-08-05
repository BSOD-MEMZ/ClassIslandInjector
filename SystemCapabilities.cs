namespace ClassIslandInjector;

/// <summary>
/// 宿主系统能力检测。
/// </summary>
internal static class SystemCapabilities
{
    /// <summary>
    /// SMTC（<c>Windows.Media.Control</c> 的全局媒体会话 API，
    /// <c>GlobalSystemMediaTransportControlsSessionManager</c>）自 Windows 10 1809
    /// （build 17763）起可用；低于此版本时动态专辑取色、暂停恢复原色与
    /// SMTC 专辑封面底图均无法工作，但插件其余功能不受影响。
    /// </summary>
    public const int SmtcMinimumBuild = 17763;

    /// <summary>
    /// 当前系统是否支持 SMTC。
    /// .NET 8 的 <see cref="Environment.OSVersion"/> 直接读取 RtlGetVersion，
    /// 不受进程 manifest 兼容段影响，返回真实 Windows 版本。
    /// </summary>
    public static bool SmtcAvailable =>
        Environment.OSVersion.Platform == PlatformID.Win32NT &&
        Environment.OSVersion.Version.Build >= SmtcMinimumBuild;
}
