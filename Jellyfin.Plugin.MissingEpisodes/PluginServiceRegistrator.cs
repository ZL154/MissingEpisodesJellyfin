using Jellyfin.Plugin.MissingEpisodes.Services;
using Jellyfin.Plugin.MissingEpisodes.Sonarr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MissingEpisodes;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<SonarrClient>();
        serviceCollection.AddSingleton<MissingEpisodesService>();
        serviceCollection.AddHostedService<AutoSearchWorker>();
    }
}
