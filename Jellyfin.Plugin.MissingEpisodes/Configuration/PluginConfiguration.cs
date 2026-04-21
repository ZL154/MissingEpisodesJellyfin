using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MissingEpisodes.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string SonarrUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    // "sonarr" — trust Sonarr's monitored/hasFile flags as the source of truth
    // "jellyfin" — use Jellyfin library data, method picked by JellyfinScanMode
    public string ScanSource { get; set; } = "sonarr";

    // When ScanSource == "jellyfin", which method defines "what should exist":
    //   "virtual" — Jellyfin's virtual episode items (needs library "Display missing" setting)
    //   "tmdb"    — query TMDB for the canonical episode list (needs TmdbApiKey)
    //   "gap"     — infer gaps from existing episode numbering (no external deps)
    public string JellyfinScanMode { get; set; } = "virtual";

    public bool IgnoreSpecials { get; set; } = true;

    public bool IgnoreUnaired { get; set; } = true;

    public bool OnlyMonitored { get; set; } = true;

    // Series TVDB IDs to skip entirely (e.g. shows that bundle multiple episodes per file in ways we can't detect)
    public List<int> IgnoredSeriesTvdbIds { get; set; } = new();

    public bool AutoSearchEnabled { get; set; }

    public int AutoSearchIntervalHours { get; set; } = 24;

    // Max episodes to send in a single Sonarr search command to avoid thrashing the indexer.
    public int AutoSearchBatchLimit { get; set; } = 50;

    // TMDB v3 API key — optional. Used as a fallback source of expected-episode
    // data in Jellyfin scan mode when the library doesn't have virtual items.
    public string TmdbApiKey { get; set; } = string.Empty;

    public string? LastScanIso { get; set; }

    public string? LastAutoSearchIso { get; set; }
}
