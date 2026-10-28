using System;
using System.Linq;
using Jellyfin.Plugin.SearchAccelerator.Api;
using Jellyfin.Plugin.SearchAccelerator.Search;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SearchAccelerator;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ReplaceSearchManager(serviceCollection);

        serviceCollection.AddSingleton<PeopleSearchActionFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<PeopleSearchActionFilter>());
    }

    /// <summary>
    /// Jellyfin registers its own services (AddSingleton&lt;ISearchManager, SearchManager&gt;) before plugin
    /// registrators run, so replacing that registration here wins. Jellyfin's SearchManager lives in
    /// Emby.Server.Implementations, which plugins can't reference, so it's created from the registered type.
    /// </summary>
    internal static bool ReplaceSearchManager(IServiceCollection serviceCollection)
    {
        var original = serviceCollection.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ISearchManager));
        if (original?.ImplementationType is not { } implementationType || original.Lifetime != ServiceLifetime.Singleton)
        {
            // Unknown registration shape (a newer Jellyfin, or another plugin got there first): leave it alone.
            return false;
        }

        serviceCollection.Remove(original);
        serviceCollection.AddSingleton<ISearchManager>(serviceProvider =>
        {
            ISearchManager Create() => (ISearchManager)ActivatorUtilities.CreateInstance(serviceProvider, implementationType);

            return new AcceleratedSearchManager(
                externalOnly: Create(),
                internalOnly: Create(),
                allProviders: Create(),
                serviceProvider.GetRequiredService<ILibraryManager>(),
                serviceProvider.GetRequiredService<ILogger<AcceleratedSearchManager>>());
        });
        return true;
    }
}
