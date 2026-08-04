using Avalonia.Threading;

namespace ClassIslandInjector;

internal static class InjectorRuntime
{
    private static MainWindowStyleInjector? _injector;
    private static SmtcWatcher? _smtcWatcher;

    public static InjectorSettings Settings { get; private set; } = new();

    public static string ConfigDirectory { get; private set; } = string.Empty;

    public static void Initialize(string configDirectory, string pluginDirectory)
    {
        ConfigDirectory = configDirectory;
        Settings = InjectorSettingsStore.Load(configDirectory, pluginDirectory);
        SmtcAlbumColorPicker.SetLogPath(Path.Combine(configDirectory, "album-color.log"));
        Settings.Changed += OnSettingsChanged;
        _injector = new MainWindowStyleInjector(Settings);
    }

    public static void Attach()
    {
        Dispatcher.UIThread.Post(() =>
        {
            GetInjector().Attach();
            UpdateSmtcWatcher();
        });
    }

    public static void SaveAndApply()
    {
        InjectorSettingsStore.Save(ConfigDirectory, Settings);
        Dispatcher.UIThread.Post(() =>
        {
            GetInjector().Apply();
            UpdateSmtcWatcher();
        });
    }

    /// <summary>
    /// 惰性获取注入器：删除所有数据后旧实例已被释放，下次应用时按全新默认重建。
    /// </summary>
    private static MainWindowStyleInjector GetInjector()
    {
        _injector ??= new MainWindowStyleInjector(Settings);
        return _injector;
    }

    public static void ReloadStyleSheet()
    {
        Dispatcher.UIThread.Post(() => _injector?.ReloadStyleSheet());
    }

    public static void PreviewRipple()
    {
        Dispatcher.UIThread.Post(() => _injector?.PreviewRipple());
    }

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        SaveAndApply();
    }

    private static bool NeedsSmtc => Settings.Enabled && (
        (Settings.CustomBackgroundEnabled && Settings.DynamicBackgroundColorEnabled) ||
        (Settings.BorderEnabled && Settings.DynamicBorderColorEnabled) ||
        (Settings.ShadowEnabled && Settings.DynamicShadowColorEnabled) ||
        (Settings.WallpaperEnabled && Settings.WallpaperSource == WallpaperSource.SmtcAlbum));

    /// <summary>
    /// 根据当前设置启动/停止事件驱动的 SMTC 监听器。
    /// </summary>
    private static void UpdateSmtcWatcher()
    {
        if (NeedsSmtc)
        {
            if (_smtcWatcher == null)
            {
                _smtcWatcher = new SmtcWatcher();
                _smtcWatcher.MediaChanged += OnSmtcMediaChanged;
                _ = StartSmtcWatcherAsync();
            }
            else
            {
                _smtcWatcher.RefreshIntervalSeconds = Settings.AlbumColorPollingIntervalSeconds;
                _smtcWatcher.UpdateFallbackTimer();
            }
        }
        else if (_smtcWatcher != null)
        {
            _smtcWatcher.MediaChanged -= OnSmtcMediaChanged;
            _smtcWatcher.Dispose();
            _smtcWatcher = null;
        }
    }

    private static async Task StartSmtcWatcherAsync()
    {
        try
        {
            await _smtcWatcher!.StartAsync();
            _smtcWatcher.RefreshIntervalSeconds = Settings.AlbumColorPollingIntervalSeconds;
            _smtcWatcher.UpdateFallbackTimer();
        }
        catch
        {
            // 启动失败（如 WinRT 不可用）不影响插件其余功能。
        }
    }

    private static void OnSmtcMediaChanged(object? sender, SmtcMediaChangedEventArgs e)
    {
        // SMTC 事件可能在非 UI 线程触发，统一调度到 UI 线程再应用。
        Dispatcher.UIThread.Post(() => _injector?.OnSmtcMediaChanged(e.Colors, e.ThumbnailBytes, e.IsPlaying));
    }

    /// <summary>
    /// 删除本插件在 ClassIsland 中创建的全部数据，并把主界面恢复为原生状态，
    /// 让插件回到“全新安装”的状态，之后即可安全卸载。
    /// </summary>
    public static void DeleteAllData()
    {
        // 1. 停止 SMTC 监听。
        if (_smtcWatcher != null)
        {
            _smtcWatcher.MediaChanged -= OnSmtcMediaChanged;
            _smtcWatcher.Dispose();
            _smtcWatcher = null;
        }

        // 2. 恢复主界面到原生状态（UI 线程），并释放注入器。
        Dispatcher.UIThread.Post(() =>
        {
            _injector?.Dispose();
            _injector = null;
        });

        // 3. 删除插件在配置目录中创建的全部文件（设置、覆盖样式表、诊断日志等）。
        try
        {
            if (Directory.Exists(ConfigDirectory))
            {
                foreach (var file in Directory.GetFiles(ConfigDirectory))
                {
                    TryDeleteFile(file);
                }

                foreach (var directory in Directory.GetDirectories(ConfigDirectory))
                {
                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch
                    {
                        // 个别文件被占用时忽略，尽力清理。
                    }
                }
            }
        }
        catch
        {
            // 清理失败不影响其余步骤。
        }

        // 4. 内存设置重置为全新默认；下次启动时配置目录为空会重新生成默认文件。
        Settings = new InjectorSettings
        {
            StyleSheetPath = Path.Combine(ConfigDirectory, "Overrides.axaml")
        };
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // 文件被占用（如诊断日志）时忽略。
        }
    }
}
