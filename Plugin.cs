using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIslandInjector.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIslandInjector;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        InjectorRuntime.Initialize(PluginConfigFolder, Info.PluginFolderPath);
        services.AddSettingsPage<InjectorSettingsPage>();
        AppBase.Current.AppStarted += OnAppStarted;
    }

    private static void OnAppStarted(object? sender, EventArgs e)
    {
        InjectorRuntime.Attach();
    }
}
