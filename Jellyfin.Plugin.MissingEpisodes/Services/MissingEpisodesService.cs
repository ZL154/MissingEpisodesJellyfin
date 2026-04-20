using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingEpisodes.Configuration;
using Jellyfin.Plugin.MissingEpisodes.Sonarr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodes.Services;

public class MissingEpisodesService
{
    private readonly SonarrClient _sonarr;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MissingEpisodesService> _logger;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private ScanResult? _lastResult;

    public MissingEpisodesService(SonarrClient sonarr, ILibraryManager libraryManager, ILogger<MissingEpisodesService> logger)
    {
        _sonarr = sonarr;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public ScanResult? LastResult => _lastResult;

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        if (string.IsNullOrWhiteSpace(cfg.SonarrUrl) || string.IsNullOrWhiteSpace(cfg.SonarrApiKey))
        {
            throw new InvalidOperationException("Sonarr URL and API key must be configured.");
        }

        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ignored = new HashSet<int>(cfg.IgnoredSeriesTvdbIds);
            var allSeries = await _sonarr.GetSeriesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, ct).ConfigureAwait(false);

            Dictionary<int, HashSet<(int season, int episode)>>? jellyfinPresent = null;
            if (string.Equals(cfg.ScanSource, "jellyfin", StringComparison.OrdinalIgnoreCase))
            {
                jellyfinPresent = BuildJellyfinPresentMap();
            }

            var now = DateTime.UtcNow;
            var result = new ScanResult
            {
                ScannedAtUtc = now,
                Source = cfg.ScanSource,
                Series = new List<ScanSeries>()
            };

            foreach (var s in allSeries)
            {
                if (ignored.Contains(s.TvdbId)) continue;
                if (cfg.OnlyMonitored && !s.Monitored) continue;

                List<SonarrEpisode> eps;
                try
                {
                    eps = await _sonarr.GetEpisodesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, s.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch episodes for series {Title}", s.Title);
                    continue;
                }

                var missing = new List<MissingEpisode>();
                HashSet<(int, int)>? presentForSeries = null;
                if (jellyfinPresent != null)
                {
                    jellyfinPresent.TryGetValue(s.TvdbId, out presentForSeries);
                }

                foreach (var ep in eps)
                {
                    if (cfg.IgnoreSpecials && ep.SeasonNumber == 0) continue;
                    if (cfg.OnlyMonitored && !ep.Monitored) continue;
                    if (cfg.IgnoreUnaired && (ep.AirDateUtc == null || ep.AirDateUtc > now)) continue;

                    bool isMissing;
                    if (jellyfinPresent != null)
                    {
                        isMissing = presentForSeries == null
                            || !presentForSeries.Contains((ep.SeasonNumber, ep.EpisodeNumber));
                    }
                    else
                    {
                        isMissing = !ep.HasFile;
                    }

                    if (!isMissing) continue;

                    missing.Add(new MissingEpisode
                    {
                        Id = ep.Id,
                        SeasonNumber = ep.SeasonNumber,
                        EpisodeNumber = ep.EpisodeNumber,
                        Title = ep.Title,
                        AirDateUtc = ep.AirDateUtc,
                        Overview = ep.Overview,
                        FinaleType = ep.FinaleType
                    });
                }

                if (missing.Count == 0) continue;

                var poster = s.Images?.FirstOrDefault(i =>
                    string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl
                    ?? s.Images?.FirstOrDefault(i =>
                        string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.Url;

                result.Series.Add(new ScanSeries
                {
                    SonarrId = s.Id,
                    TvdbId = s.TvdbId,
                    Title = s.Title,
                    Year = s.Year,
                    Network = s.Network,
                    Status = s.Status,
                    PosterUrl = poster,
                    MissingCount = missing.Count,
                    TotalEpisodes = s.Statistics?.TotalEpisodeCount ?? eps.Count,
                    Missing = missing
                });
            }

            result.TotalMissing = result.Series.Sum(x => x.MissingCount);
            result.Series = result.Series
                .OrderByDescending(x => x.MissingCount)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _lastResult = result;
            cfg.LastScanIso = result.ScannedAtUtc.ToString("o");
            Plugin.Instance?.SaveConfiguration();
            return result;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    // Build a map of TvdbId -> set of (season, episode) tuples present in the Jellyfin library,
    // expanding multi-episode files via IndexNumberEnd so a file covering E01-E02 counts both.
    private Dictionary<int, HashSet<(int, int)>> BuildJellyfinPresentMap()
    {
        var map = new Dictionary<int, HashSet<(int, int)>>();

        var seriesItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        });

        foreach (var item in seriesItems)
        {
            if (item is not Series series) continue;
            var tvdb = series.GetProviderId(MetadataProvider.Tvdb);
            if (string.IsNullOrEmpty(tvdb) || !int.TryParse(tvdb, out var tvdbId)) continue;

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true
            });

            if (!map.TryGetValue(tvdbId, out var set))
            {
                set = new HashSet<(int, int)>();
                map[tvdbId] = set;
            }

            foreach (var e in episodes)
            {
                if (e is not Episode ep) continue;
                var season = ep.ParentIndexNumber;
                var start = ep.IndexNumber;
                if (!season.HasValue || !start.HasValue) continue;
                var end = ep.IndexNumberEnd ?? start.Value;
                for (var n = start.Value; n <= end; n++)
                {
                    set.Add((season.Value, n));
                }
            }
        }

        return map;
    }

    public async Task<int> TriggerSearchAsync(IReadOnlyList<int> episodeIds, CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
        var batch = cfg.AutoSearchBatchLimit > 0 ? cfg.AutoSearchBatchLimit : 50;

        var total = 0;
        for (var i = 0; i < episodeIds.Count; i += batch)
        {
            var slice = episodeIds.Skip(i).Take(batch).ToList();
            var ok = await _sonarr.TriggerEpisodeSearchAsync(cfg.SonarrUrl, cfg.SonarrApiKey, slice, ct)
                .ConfigureAwait(false);
            if (ok) total += slice.Count;
        }
        return total;
    }
}

public class ScanResult
{
    public DateTime ScannedAtUtc { get; set; }
    public string Source { get; set; } = "sonarr";
    public int TotalMissing { get; set; }
    public List<ScanSeries> Series { get; set; } = new();
}

public class ScanSeries
{
    public int SonarrId { get; set; }
    public int TvdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Network { get; set; }
    public string? Status { get; set; }
    public string? PosterUrl { get; set; }
    public int MissingCount { get; set; }
    public int TotalEpisodes { get; set; }
    public List<MissingEpisode> Missing { get; set; } = new();
}

public class MissingEpisode
{
    public int Id { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public DateTime? AirDateUtc { get; set; }
    public string? Overview { get; set; }
    public string? FinaleType { get; set; }
}
