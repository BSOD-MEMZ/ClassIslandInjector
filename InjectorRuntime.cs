using Avalonia;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIslandInjector;

internal static class InjectorRuntime
{
    private static MainWindowStyleInjector? _injector;
    private static SmtcWatcher? _smtcWatcher;
    private static List<UserPreset> _presets = [];

    public static InjectorSettings Settings { get; private set; } = new();

    public static string ConfigDirectory { get; private set; } = string.Empty;

    /// <summary>插件安装目录（含 Assets/Stickers 等随插件部署的资源）。</summary>
    public static string PluginDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// 用户预设列表发生变化（新增/删除）时触发，供 UI 与自动化设置控件刷新下拉列表。
    /// </summary>
    public static event EventHandler? PresetsChanged;

    public static void Initialize(string configDirectory, string pluginDirectory)
    {
        ConfigDirectory = configDirectory;
        PluginDirectory = pluginDirectory;
        Settings = InjectorSettingsStore.Load(configDirectory, pluginDirectory);
        _presets = InjectorPresetStore.Load(configDirectory);
        SmtcAlbumColorPicker.SetLogPath(Path.Combine(configDirectory, "album-color.log"));
        DiagnosticLog.CrashLogPath = Path.Combine(configDirectory, "crash.log");
        // 注册全局异常兜底（漏网异常静默写入 crash.log）并同步日志开关（须在设置加载之后）。
        DiagnosticLog.RegisterGlobalHandlers();
        ApplyDiagnosticLoggingEnabled();
        Settings.Changed += OnSettingsChanged;
        ContractCatalogService.Initialize(configDirectory);
        HostTutorial.ErrorLogPath = Path.Combine(configDirectory, "tutorial-error.log");
        InitializeTutorial(configDirectory, pluginDirectory);
        _injector = new MainWindowStyleInjector(Settings);
    }

    /// <summary>
    /// 注册插件教程到宿主教学中心：首次运行把默认教程 JSON 复制到配置目录
    /// （用户可自行编辑），之后读取配置目录副本，通过反射注册到宿主教程系统。
    /// 会遍历 Defaults/Tutorials 下的全部教程文件；多个教程文件共享同一个
    /// 教程组 Id，由 HostTutorial 合并进同一个分组。教程加载失败不影响插件其余功能。
    /// </summary>
    private static void InitializeTutorial(string configDirectory, string pluginDirectory)
    {
        try
        {
            var packagedDir = Path.Combine(pluginDirectory, "Defaults", "Tutorials");
            if (!Directory.Exists(packagedDir))
            {
                return;
            }

            var tutorialDir = Path.Combine(configDirectory, "Tutorials");
            // 教程模板用 {stickerUri} 占位符引用 PJSK 贴纸（随插件部署在 Assets/Stickers 下）、
            // 用 {assetsUri} 引用插件 Assets 目录（如教程 Banner），
            // 注册时替换为插件实际目录的 file:// URI，确保本地可加载。
            var stickerDir = Path.Combine(pluginDirectory, "Assets", "Stickers");
            var assetsUri = new Uri(Path.Combine(pluginDirectory, "Assets")).AbsoluteUri.TrimEnd('/');
            foreach (var fileName in Directory.GetFiles(packagedDir, "*.json"))
            {
                var name = Path.GetFileName(fileName);
                var tutorialFile = Path.Combine(tutorialDir, name);
                if (!File.Exists(tutorialFile))
                {
                    Directory.CreateDirectory(tutorialDir);
                    File.Copy(fileName, tutorialFile);
                }

                var json = File.ReadAllText(tutorialFile)
                    .Replace("{stickerUri}", new Uri(stickerDir).AbsoluteUri.TrimEnd('/'))
                    .Replace("{assetsUri}", assetsUri);
                HostTutorial.RegisterGroupFromJson(json);
            }
        }
        catch
        {
            // 教程加载失败不影响插件其余功能。
        }
    }

    public static void Attach()
    {
        Dispatcher.UIThread.Post(() =>
        {
            GetInjector().Attach();
            UpdateSmtcWatcher();
            // 主窗口就绪后执行宿主点位健康检查；用户关闭该检查时跳过。
            if (!Settings.DisableDegradationCheck)
            {
                ContractCatalogService.RunHealthCheck();
            }
        });
    }

    /// <summary>
    /// 启动后延时自动打开指定设置页面（调试用）。
    /// 延迟等待宿主设置窗口注册完 Uri 导航处理器，避免导航目标不存在。
    /// </summary>
    public static void ScheduleStartupNavigation()
    {
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
        {
            timer?.Stop();
            try
            {
                var page = Settings.StartupOpenTarget switch
                {
                    1 => "classisland://app/settings",
                    2 => "classisland://app/settings/classisland.injector",
                    3 => "classisland://app/settings/classisland.plugins",
                    _ => null
                };
                if (page == null)
                {
                    return;
                }

                IAppHost.TryGetService<IUriNavigationService>()?.Navigate(new Uri(page));
            }
            catch
            {
                // 忽略：设置窗口未就绪等情况下静默失败。
            }
        });
        timer.Start();
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

    /// <summary>
    /// 应用下载/切换的宿主对照表：写入 HostContract、持久化、清空反射缓存并重新 Attach，
    /// 使新点位立即生效。
    /// </summary>
    public static void ApplyContractCatalog(ContractCatalog catalog)
    {
        ContractCatalogService.SetActive(catalog);
        Dispatcher.UIThread.Post(() =>
        {
            MainWindowStyleInjector.ClearReflectionCaches();
            GetInjector().Attach();
        });
    }

    /// <summary>当前 ClassIsland 宿主版本号（供对照表匹配与展示）。</summary>
    public static string HostVersion => ContractCatalogService.GetHostVersion();

    /// <summary>
    /// 当前主界面尺寸（供底图图层编辑器初始化预览画布；未附着或不可用时返回 null）。
    /// </summary>
    public static Size? GetCurrentIslandSize()
    {
        if (_injector == null)
        {
            return null;
        }

        return _injector.GetIslandSize();
    }

    public static void ReloadStyleSheet()
    {
        Dispatcher.UIThread.Post(() => _injector?.ReloadStyleSheet());
    }

    public static void PreviewNotification()
    {
        Dispatcher.UIThread.Post(() => _injector?.PreviewNotification());
    }

    public static void PreviewPrepareOnClass()
    {
        Dispatcher.UIThread.Post(() => _injector?.PreviewPrepareOnClass());
    }

    #region 用户预设

    /// <summary>
    /// 获取所有用户预设名称（保持保存顺序），并前置内置「无预设」，
    /// 使其同样能被设置页与自动化「切换预设」行动调用。
    /// </summary>
    public static IReadOnlyList<string> GetPresetNames()
    {
        var names = new List<string> { InjectorPresetStore.NoPresetName };
        names.AddRange(_presets.Select(p => p.Name));
        return names;
    }

    /// <summary>
    /// 将当前全部设置保存为命名预设（同名覆盖）。预设可被自动化“切换预设”行动套用。
    /// </summary>
    public static void SavePreset(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        // 内置「无预设」不可被用户预设覆盖。
        if (string.Equals(trimmed, InjectorPresetStore.NoPresetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existing = _presets.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Settings = Settings.Clone();
        }
        else
        {
            _presets.Add(new UserPreset { Name = trimmed, Settings = Settings.Clone() });
        }

        SavePresets();
    }

    /// <summary>
    /// 套用命名用户预设：把预设快照复制到当前设置并立即保存应用。
    /// </summary>
    /// <returns>预设是否存在并成功套用。</returns>
    public static bool ApplyPreset(string name)
    {
        // 内置「无预设」：把全部设置重置为中性默认（类似清除插件数据后的全新状态），
        // 不注入任何内容；保留 StyleSheetPath 与 WatchStyleSheet。
        if (string.Equals(name, InjectorPresetStore.NoPresetName, StringComparison.OrdinalIgnoreCase))
        {
            Settings.ResetToDefaults();
            SaveAndApply();
            return true;
        }

        var preset = _presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            return false;
        }

        Settings.BeginUpdate();
        Settings.CopyFrom(preset.Settings);
        Settings.EndUpdate();
        SaveAndApply();
        return true;
    }

    /// <summary>
    /// 删除命名用户预设。
    /// </summary>
    public static void DeletePreset(string name)
    {
        // 内置「无预设」不可删除。
        if (string.Equals(name, InjectorPresetStore.NoPresetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var removed = _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            SavePresets();
        }
    }

    private static void SavePresets()
    {
        InjectorPresetStore.Save(ConfigDirectory, _presets);
        PresetsChanged?.Invoke(null, EventArgs.Empty);
    }

    #endregion

    /// <summary>把设置里的「输出诊断日志」开关同步到全局日志门面，开关即刻生效。</summary>
    private static void ApplyDiagnosticLoggingEnabled()
    {
        DiagnosticLog.Enabled = Settings.DiagnosticLoggingEnabled;
    }

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyDiagnosticLoggingEnabled();
        SaveAndApply();
    }

    private static bool NeedsSmtc => Settings.Enabled && (
        (Settings.CustomBackgroundEnabled && Settings.DynamicBackgroundColorEnabled) ||
        (Settings.BorderEnabled && Settings.DynamicBorderColorEnabled) ||
        (Settings.ShadowEnabled && Settings.DynamicShadowColorEnabled) ||
        (Settings.WallpaperEnabled && Settings.WallpaperSource == WallpaperSource.SmtcAlbum) ||
        (Settings.WallpaperEnabled && Settings.WallpaperDesignerEnabled &&
         Settings.WallpaperLayers.Any(l => l.Visible && l.Source == WallpaperSource.SmtcAlbum)));

    /// <summary>
    /// 根据当前设置启动/停止事件驱动的 SMTC 监听器。
    /// </summary>
    private static void UpdateSmtcWatcher()
    {
        if (!SystemCapabilities.SmtcAvailable)
        {
            // 系统过旧（低于 Win10 1809），SMTC 不可用：不启动监听器，其余功能不受影响。
            if (_smtcWatcher != null)
            {
                _smtcWatcher.MediaChanged -= OnSmtcMediaChanged;
                _smtcWatcher.Dispose();
                _smtcWatcher = null;
            }

            return;
        }

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
            // 启动失败（如 WinRT 瞬时不可用）不影响插件其余功能；
            // 但必须释放并置空，让下次 UpdateSmtcWatcher 能重建重试——
            // 否则监听器永远处于未启动状态，动态取色/SMTC 底图/暂停恢复全部静默失效。
            try
            {
                _smtcWatcher?.Dispose();
            }
            catch
            {
                // 忽略释放异常。
            }

            _smtcWatcher = null;
        }
    }

    private static void OnSmtcMediaChanged(object? sender, SmtcMediaChangedEventArgs e)
    {
        // SMTC 事件可能在非 UI 线程触发，统一调度到 UI 线程再应用。
        Dispatcher.UIThread.Post(() => _injector?.OnSmtcMediaChanged(e.Colors, e.ThumbnailBytes, e.IsPlaying, e.Title, e.Artist));
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

        // 5. 清空内存中的用户预设（presets.json 已在第 3 步被删除）。
        _presets = [];
        PresetsChanged?.Invoke(null, EventArgs.Empty);
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
