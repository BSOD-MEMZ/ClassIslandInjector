using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.Plugin;
using ClassIslandInjector.Automation;
using ClassIslandInjector.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIslandInjector;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    /// <summary>当前插件清单（供设置页「关于」区展示）。</summary>
    public static PluginManifest? Manifest { get; private set; }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Manifest = Info.Manifest;
        // 尽早设置进程 AUMID：让 SMTC 会话与任务栏按钮（主窗口/播放器）的 AppUserModelID
        // 一致，任务栏缩略图预览下方才会出现媒体控制按钮（也早于主窗口创建）。
        FakePlayerWindow.EnsureAppUserModelId();
        InjectorRuntime.Initialize(PluginConfigFolder, Info.PluginFolderPath);
        services.AddSettingsPage<InjectorSettingsPage>();
        InjectorAutomation.Register(services);
        AppBase.Current.AppStarted += OnAppStarted;
    }

    private static void OnAppStarted(object? sender, EventArgs e)
    {
        InjectorRuntime.Attach();
        InjectorRuntime.ScheduleStartupNavigation();
    }
}
