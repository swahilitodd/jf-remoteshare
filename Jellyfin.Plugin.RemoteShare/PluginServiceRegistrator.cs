using Jellyfin.Plugin.RemoteShare.Guest;
using Jellyfin.Plugin.RemoteShare.Host;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RemoteShare;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HostClient.HttpClientName);
        serviceCollection.AddSingleton<HostService>();
        serviceCollection.AddSingleton<HostClient>();
        serviceCollection.AddSingleton<GuestSyncService>();
    }
}
