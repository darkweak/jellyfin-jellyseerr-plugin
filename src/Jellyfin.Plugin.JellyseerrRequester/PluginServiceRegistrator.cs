using Jellyfin.Plugin.JellyseerrRequester.Injection;
using Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;
using Jellyfin.Plugin.JellyseerrRequester.Virtual;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyseerrRequester;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellyseerrClient>();
        serviceCollection.AddSingleton<RequesterService>();
        serviceCollection.AddSingleton<PosterRenderer>();
        serviceCollection.AddScoped<VirtualItemFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<VirtualItemFilter>());
        serviceCollection.AddHostedService<WebInjectionService>();
    }
}
