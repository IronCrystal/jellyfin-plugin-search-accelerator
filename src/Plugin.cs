using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SearchAccelerator.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SearchAccelerator;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    // Jellyfin groups plugin folders by name on startup and deletes what it takes for older versions,
    // so this must not match the search provider plugin's name ("Meilisearch").
    public override string Name => "Search Accelerator";

    public override Guid Id => Guid.Parse("e47faeac-8075-4eae-a9de-c54c3e0064b7");

    public override string Description =>
        "Answers searches from an external search provider (such as Meilisearch) without waiting for Jellyfin's own database search.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
