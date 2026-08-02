using Avalonia.Threading;

namespace ClassIslandInjector;

internal static class InjectorRuntime
{
    private static MainWindowStyleInjector? _injector;

    public static InjectorSettings Settings { get; private set; } = new();

    public static string ConfigDirectory { get; private set; } = string.Empty;

    public static void Initialize(string configDirectory, string pluginDirectory)
    {
        ConfigDirectory = configDirectory;
        Settings = InjectorSettingsStore.Load(configDirectory, pluginDirectory);
        Settings.Changed += OnSettingsChanged;
        _injector = new MainWindowStyleInjector(Settings);
    }

    public static void Attach()
    {
        Dispatcher.UIThread.Post(() => _injector?.Attach());
    }

    public static void SaveAndApply()
    {
        InjectorSettingsStore.Save(ConfigDirectory, Settings);
        Dispatcher.UIThread.Post(() => _injector?.Apply());
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
}
