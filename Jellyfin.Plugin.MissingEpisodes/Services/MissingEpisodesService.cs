using System;
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

    public static bool IsSonarrConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.SonarrUrl) && !string.IsNullOrWhiteSpace(cfg.SonarrApiKey);

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        var useJellyfin = string.Equals(cfg.ScanSource, "jellyfin", StringComparison.OrdinalIgnoreCase);
        var sonarrReady = IsSonarrConfigured(cfg);

        if (!useJellyfin && !sonarrReady)
        {
            throw new InvalidOperationException(
                "Sonarr scans need a URL and API key. Switch the scan source to Jellyfin, or add your Sonarr credentials.");
        }

        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ScanResult result;
            if (useJellyfin)
            {
                result = ScanJellyfinOnly(cfg);
                if (sonarrReady)
                {
                    await EnrichWithSonarrIdsAsync(cfg, result, ct).ConfigureAwait(false);
                }
            }
            else
            {
                result = await ScanSonarrAsync(cfg, ct).ConfigureAwait(false);
            }

            result.SonarrConfigured = sonarrReady;
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

    private async Task<ScanResult> ScanSonarrAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var ignored = new HashSet<int>(cfg.IgnoredSeriesTvdbIds);
        var allSeries = await _sonarr.GetSeriesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var jellyfinByTvdb = BuildJellyfinIndex();

        var result = new ScanResult
        {
            ScannedAtUtc = now,
            Source = "sonarr",
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
            foreach (var ep in eps)
            {
                if (cfg.IgnoreSpecials && ep.SeasonNumber == 0) continue;
                if (cfg.OnlyMonitored && !ep.Monitored) continue;
                if (cfg.IgnoreUnaired && (ep.AirDateUtc == null || ep.AirDateUtc > now)) continue;
                if (ep.HasFile) continue;

                missing.Add(new MissingEpisode
                {
                    Id = ep.Id,
                    SeasonNumber = ep.SeasonNumber,
                    EpisodeNumber = ep.EpisodeNumber,
                    Title = ep.Title,
                    AirDateUtc = ep.AirDateUtc,
                    Overview = ep.Overview,
                    FinaleType = ep.FinaleType,
                    ThumbnailUrl = PickImage(ep.Images, "screenshot")
                });
            }

            if (missing.Count == 0) continue;

            jellyfinByTvdb.TryGetValue(s.TvdbId, out var jfId);

            result.Series.Add(new ScanSeries
            {
                SonarrId = s.Id,
                TvdbId = s.TvdbId,
                JellyfinSeriesId = jfId,
                Title = s.Title,
                Year = s.Year,
                Network = s.Network,
                Status = s.Status,
                PosterUrl = PickImage(s.Images, "poster"),
                BackdropUrl = PickImage(s.Images, "fanart") ?? PickImage(s.Images, "banner"),
                MissingCount = missing.Count,
                TotalEpisodes = s.Statistics?.TotalEpisodeCount ?? eps.Count,
                Missing = missing
            });
        }

        FinalizeResult(result);
        return result;
    }

    private ScanResult ScanJellyfinOnly(PluginConfiguration cfg)
    {
        var now = DateTime.UtcNow;
        var result = new ScanResult
        {
            ScannedAtUtc = now,
            Source = "jellyfin",
            Series = new List<ScanSeries>()
        };

        var ignoredTvdb = new HashSet<int>(cfg.IgnoredSeriesTvdbIds);

        var seriesItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        });

        foreach (var item in seriesItems)
        {
            if (item is not Series series) continue;

            var tvdbStr = series.GetProviderId(MetadataProvider.Tvdb);
            int.TryParse(tvdbStr, out var tvdbId);
            if (tvdbId > 0 && ignoredTvdb.Contains(tvdbId)) continue;

            // Jellyfin creates virtual episode items for episodes it knows exist (per metadata provider)
            // but has no file for — that's our "missing" set.
            var missingEps = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsVirtualItem = true,
                Recursive = true
            });

            var totalEps = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true
            }).Count;

            var missing = new List<MissingEpisode>();
            foreach (var e in missingEps)
            {
                if (e is not Episode episode) continue;
                var season = episode.ParentIndexNumber;
                var num = episode.IndexNumber;
                if (!season.HasValue || !num.HasValue) continue;
                if (cfg.IgnoreSpecials && season.Value == 0) continue;

                var air = episode.PremiereDate;
                if (cfg.IgnoreUnaired && (!air.HasValue || air.Value.ToUniversalTime() > now)) continue;

                var jfEpId = episode.Id.ToString("N");
                missing.Add(new MissingEpisode
                {
                    Id = 0,
                    SeasonNumber = season.Value,
                    EpisodeNumber = num.Value,
                    Title = episode.Name,
                    AirDateUtc = air?.ToUniversalTime(),
                    Overview = episode.Overview,
                    JellyfinEpisodeId = jfEpId,
                    ThumbnailUrl = "jellyfin:" + jfEpId // prefix tells the UI to resolve via ApiClient
                });
            }

            if (missing.Count == 0) continue;

            var jfSeriesId = series.Id.ToString("N");
            result.Series.Add(new ScanSeries
            {
                SonarrId = 0,
                TvdbId = tvdbId,
                JellyfinSeriesId = jfSeriesId,
                Title = series.Name,
                Year = series.ProductionYear ?? 0,
                Network = series.Studios?.FirstOrDefault(),
                Status = series.Status?.ToString(),
                PosterUrl = "jellyfin:" + jfSeriesId,
                BackdropUrl = "jellyfin-backdrop:" + jfSeriesId,
                MissingCount = missing.Count,
                TotalEpisodes = totalEps,
                Missing = missing
            });
        }

        FinalizeResult(result);
        return result;
    }

    private async Task EnrichWithSonarrIdsAsync(PluginConfiguration cfg, ScanResult result, CancellationToken ct)
    {
        try
        {
            var sonarrSeries = await _sonarr.GetSeriesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, ct).ConfigureAwait(false);
            var byTvdb = sonarrSeries
                .Where(x => x.TvdbId > 0)
                .GroupBy(x => x.TvdbId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var s in result.Series)
            {
                if (s.TvdbId <= 0 || !byTvdb.TryGetValue(s.TvdbId, out var sonarrS)) continue;
                s.SonarrId = sonarrS.Id;

                List<SonarrEpisode> eps;
                try
                {
                    eps = await _sonarr.GetEpisodesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, sonarrS.Id, ct).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                var epIndex = new Dictionary<(int, int), SonarrEpisode>();
                foreach (var e in eps) epIndex[(e.SeasonNumber, e.EpisodeNumber)] = e;

                foreach (var m in s.Missing)
                {
                    if (epIndex.TryGetValue((m.SeasonNumber, m.EpisodeNumber), out var se))
                    {
                        m.Id = se.Id;
                        // Prefer Sonarr's screenshot when we have one, over the Jellyfin virtual episode thumb.
                        var shot = PickImage(se.Images, "screenshot");
                        if (!string.IsNullOrEmpty(shot)) m.ThumbnailUrl = shot;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich Jellyfin scan with Sonarr IDs");
        }
    }

    private Dictionary<int, string> BuildJellyfinIndex()
    {
        var map = new Dictionary<int, string>();
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
            map[tvdbId] = series.Id.ToString("N");
        }
        return map;
    }

    private static void FinalizeResult(ScanResult result)
    {
        result.TotalMissing = result.Series.Sum(x => x.MissingCount);
        result.Series = result.Series
            .OrderByDescending(x => x.MissingCount)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? PickImage(List<SonarrImage>? images, string coverType)
    {
        if (images == null) return null;
        foreach (var img in images)
        {
            if (!string.Equals(img.CoverType, coverType, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(img.RemoteUrl)) return img.RemoteUrl;
            if (!string.IsNullOrWhiteSpace(img.Url)) return img.Url;
        }
        return null;
    }

    public async Task<int> TriggerSearchAsync(IReadOnlyList<int> episodeIds, CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
        if (!IsSonarrConfigured(cfg))
        {
            throw new InvalidOperationException("Sonarr must be configured to trigger searches.");
        }
        var batch = cfg.AutoSearchBatchLimit > 0 ? cfg.AutoSearchBatchLimit : 50;

        var total = 0;
        var filtered = episodeIds.Where(id => id > 0).ToList();
        for (var i = 0; i < filtered.Count; i += batch)
        {
            var slice = filtered.Skip(i).Take(batch).ToList();
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
    public bool SonarrConfigured { get; set; }
    public int TotalMissing { get; set; }
    public List<ScanSeries> Series { get; set; } = new();
}

public class ScanSeries
{
    public int SonarrId { get; set; }
    public int TvdbId { get; set; }
    public string? JellyfinSeriesId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Network { get; set; }
    public string? Status { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
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
    public string? JellyfinEpisodeId { get; set; }
    public string? ThumbnailUrl { get; set; }
}
