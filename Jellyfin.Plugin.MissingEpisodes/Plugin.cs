using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MissingEpisodes.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MissingEpisodes;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override string Name => "Missing Episodes";

    public override string Description => "Scan for missing TV episodes across your Jellyfin library and Sonarr, with a cleaner UI and optional auto-search.";

    public override Guid Id => Guid.Parse("b7a24c68-7f3e-4c0a-9a9e-6e2b9e1a4f33");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "missingepisodesconfigpage",
                EmbeddedResourcePath = "Jellyfin.Plugin.MissingEpisodes.Configuration.configPage.html"
            }
        };
    }
}
