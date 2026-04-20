using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MissingEpisodes.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string SonarrUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    // "sonarr" — trust Sonarr's monitored/hasFile flags as the source of truth
    // "jellyfin" — diff Sonarr's expected episodes against what's actually present in the Jellyfin library
    public string ScanSource { get; set; } = "sonarr";

    public bool IgnoreSpecials { get; set; } = true;

    public bool IgnoreUnaired { get; set; } = true;

    public bool OnlyMonitored { get; set; } = true;

    // Series TVDB IDs to skip entirely (e.g. shows that bundle multiple episodes per file in ways we can't detect)
    public List<int> IgnoredSeriesTvdbIds { get; set; } = new();

    public bool AutoSearchEnabled { get; set; }

    public int AutoSearchIntervalHours { get; set; } = 24;

    // Max episodes to send in a single Sonarr search command to avoid thrashing the indexer.
    public int AutoSearchBatchLimit { get; set; } = 50;

    public string? LastScanIso { get; set; }

    public string? LastAutoSearchIso { get; set; }
}
