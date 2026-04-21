using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MissingEpisodes.Configuration;
using Jellyfin.Plugin.MissingEpisodes.Sonarr;
using Jellyfin.Plugin.MissingEpisodes.Tmdb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodes.Services;

public class MissingEpisodesService
{
    private readonly SonarrClient _sonarr;
    private readonly TmdbClient _tmdb;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MissingEpisodesService> _logger;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private ScanResult? _lastResult;
    private bool _diskLoadAttempted;
    public ScanProgress Progress { get; } = new();

    private static readonly JsonSerializerOptions PersistJsonOpts = new()
    {
        WriteIndented = false
    };

    private string? DataDir
    {
        get
        {
            var inst = Plugin.Instance;
            if (inst == null) return null;
            try { return inst.DataFolderPath; } catch { return null; }
        }
    }
    private string? LastResultPath => DataDir == null ? null : Path.Combine(DataDir, "last-result.json");
    private string? HistoryPath => DataDir == null ? null : Path.Combine(DataDir, "history.json");

    public MissingEpisodesService(
        SonarrClient sonarr,
        TmdbClient tmdb,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ILogger<MissingEpisodesService> logger)
    {
        _sonarr = sonarr;
        _tmdb = tmdb;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public TmdbClient Tmdb => _tmdb;

    public ScanResult? LastResult
    {
        get
        {
            if (!_diskLoadAttempted && _lastResult == null)
            {
                _diskLoadAttempted = true;
                TryLoadLastFromDisk();
            }
            return _lastResult;
        }
    }

    private void TryLoadLastFromDisk()
    {
        try
        {
            var path = LastResultPath;
            if (path == null || !File.Exists(path)) return;
            var json = File.ReadAllText(path);
            _lastResult = JsonSerializer.Deserialize<ScanResult>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load last scan result from disk");
        }
    }

    private void PersistLast(ScanResult result)
    {
        try
        {
            var dir = DataDir;
            var path = LastResultPath;
            if (dir == null || path == null) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(result, PersistJsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist last scan result");
        }
    }

    public List<ScanHistoryEntry> LoadHistory()
    {
        try
        {
            var path = HistoryPath;
            if (path == null || !File.Exists(path)) return new List<ScanHistoryEntry>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ScanHistoryEntry>>(json) ?? new List<ScanHistoryEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load scan history");
            return new List<ScanHistoryEntry>();
        }
    }

    private void AppendHistory(ScanHistoryEntry entry)
    {
        try
        {
            var dir = DataDir;
            var path = HistoryPath;
            if (dir == null || path == null) return;
            Directory.CreateDirectory(dir);
            var list = LoadHistory();
            list.Insert(0, entry);
            if (list.Count > 25) list.RemoveRange(25, list.Count - 25);
            File.WriteAllText(path, JsonSerializer.Serialize(list, PersistJsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append to scan history");
        }
    }

    public static bool IsSonarrConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.SonarrUrl) && !string.IsNullOrWhiteSpace(cfg.SonarrApiKey);

    public async Task<ScanResult> ScanAsync(string? sourceOverride = null, CancellationToken ct = default)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        // Allow the caller (UI) to pass the currently-selected source so an unsaved
        // segment click still wins. Persist it so later reads stay consistent.
        if (!string.IsNullOrWhiteSpace(sourceOverride))
        {
            var norm = sourceOverride.Equals("jellyfin", StringComparison.OrdinalIgnoreCase) ? "jellyfin" : "sonarr";
            if (cfg.ScanSource != norm)
            {
                cfg.ScanSource = norm;
                Plugin.Instance?.SaveConfiguration();
            }
        }

        var useJellyfin = string.Equals(cfg.ScanSource, "jellyfin", StringComparison.OrdinalIgnoreCase);
        var sonarrReady = IsSonarrConfigured(cfg);

        if (!useJellyfin && !sonarrReady)
        {
            throw new InvalidOperationException(
                "Sonarr scans need a URL and API key. Switch the scan source to Jellyfin, or add your Sonarr credentials.");
        }

        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        Progress.Reset(useJellyfin ? "jellyfin" : "sonarr");
        try
        {
            ScanResult result;
            if (useJellyfin)
            {
                result = await ScanJellyfinOnlyAsync(cfg, ct).ConfigureAwait(false);
                if (sonarrReady)
                {
                    // Only enrich with Sonarr episode IDs (for searching). Don't replace Jellyfin's
                    // own thumbnails with Sonarr screenshots — when the user picks "Jellyfin", the
                    // UI should show Jellyfin's data.
                    await EnrichWithSonarrIdsAsync(cfg, result, ct).ConfigureAwait(false);
                }
            }
            else
            {
                result = await ScanSonarrAsync(cfg, ct).ConfigureAwait(false);
            }

            result.SonarrConfigured = sonarrReady;
            _lastResult = result;
            _diskLoadAttempted = true;
            cfg.LastScanIso = result.ScannedAtUtc.ToString("o");
            Plugin.Instance?.SaveConfiguration();

            PersistLast(result);
            AppendHistory(new ScanHistoryEntry
            {
                ScannedAtUtc = result.ScannedAtUtc,
                Source = result.Source,
                JellyfinMode = useJellyfin ? (cfg.JellyfinScanMode ?? "virtual") : null,
                TotalMissing = result.TotalMissing,
                ShowCount = result.Series.Count,
                IgnoredCount = result.IgnoredSeries.Count,
                DurationMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Progress.StartedAtMs)
            });

            _ = NotifyAdminsAsync(result);
            return result;
        }
        finally
        {
            Progress.Finish();
            _scanLock.Release();
        }
    }

    private async Task NotifyAdminsAsync(ScanResult result)
    {
        try
        {
            var header = "Missing Episodes";
            var text = result.TotalMissing == 0
                ? "Scan complete. Nothing missing."
                : $"Scan complete — {result.TotalMissing} missing across {result.Series.Count} show" +
                  (result.Series.Count == 1 ? string.Empty : "s");
            foreach (var session in _sessionManager.Sessions)
            {
                try
                {
                    await _sessionManager.SendMessageCommand(
                        session.Id,
                        session.Id,
                        new MessageCommand { Header = header, Text = text, TimeoutMs = 8000 },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* per-session failures are fine */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sending MissingEpisodes toast failed");
        }
    }

    private async Task<ScanResult> ScanSonarrAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var ignored = new HashSet<int>(cfg.IgnoredSeriesTvdbIds);
        var allSeries = await _sonarr.GetSeriesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        Progress.SetTotal(allSeries.Count);

        var jellyfinByTvdb = BuildJellyfinIndex();

        var result = new ScanResult
        {
            ScannedAtUtc = now,
            Source = "sonarr",
            Series = new List<ScanSeries>()
        };

        foreach (var s in allSeries)
        {
            Progress.Advance(s.Title);
            if (ignored.Contains(s.TvdbId))
            {
                result.IgnoredSeries.Add(new ScanSeries
                {
                    SonarrId = s.Id,
                    TvdbId = s.TvdbId,
                    TmdbId = s.TmdbId,
                    Title = s.Title,
                    Year = s.Year,
                    Network = s.Network,
                    Status = s.Status,
                    Path = s.Path,
                    SeriesType = NormalizeSeriesType(s.SeriesType),
                    PosterUrl = PickImage(s.Images, "poster"),
                    SizeOnDisk = s.Statistics?.SizeOnDisk ?? 0,
                    TotalEpisodes = s.Statistics?.TotalEpisodeCount ?? 0
                });
                continue;
            }
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

            // Build per-season stats over the episodes that count (honoring ignore flags).
            var seasonStats = new Dictionary<int, SeasonCount>();
            var missing = new List<MissingEpisode>();
            foreach (var ep in eps)
            {
                if (cfg.IgnoreSpecials && ep.SeasonNumber == 0) continue;
                if (cfg.OnlyMonitored && !ep.Monitored) continue;
                var unaired = ep.AirDateUtc == null || ep.AirDateUtc > now;
                if (cfg.IgnoreUnaired && unaired) continue;

                if (!seasonStats.TryGetValue(ep.SeasonNumber, out var stats)) stats = new SeasonCount();
                stats.Total += 1;
                if (ep.HasFile) stats.Have += 1;
                seasonStats[ep.SeasonNumber] = stats;

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
                TmdbId = s.TmdbId,
                JellyfinSeriesId = jfId,
                Title = s.Title,
                Year = s.Year,
                Network = s.Network,
                Status = s.Status,
                Path = s.Path,
                SeriesType = NormalizeSeriesType(s.SeriesType),
                PosterUrl = PickImage(s.Images, "poster"),
                BackdropUrl = PickImage(s.Images, "fanart") ?? PickImage(s.Images, "banner"),
                MissingCount = missing.Count,
                HaveEpisodes = seasonStats.Values.Sum(x => x.Have),
                TotalEpisodes = seasonStats.Values.Sum(x => x.Total),
                SizeOnDisk = s.Statistics?.SizeOnDisk ?? 0,
                Seasons = seasonStats.OrderBy(kv => kv.Key).Select(kv => new SeasonSummary
                {
                    SeasonNumber = kv.Key,
                    TotalEpisodes = kv.Value.Total,
                    HaveEpisodes = kv.Value.Have
                }).ToList(),
                Missing = missing
            });
        }

        FinalizeResult(result);
        return result;
    }

    private async Task<ScanResult> ScanJellyfinOnlyAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var mode = (cfg.JellyfinScanMode ?? "virtual").ToLowerInvariant();
        if (mode != "virtual" && mode != "tmdb" && mode != "gap") mode = "virtual";
        if (mode == "tmdb" && string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDB mode requires a TMDB API key in settings.");
        }

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
        Progress.SetTotal(seriesItems.Count);

        foreach (var item in seriesItems)
        {
            if (item is not Series series) { Progress.Advance(null); continue; }
            Progress.Advance(series.Name);

            var tvdbStr = series.GetProviderId(MetadataProvider.Tvdb);
            int.TryParse(tvdbStr, out var tvdbId);
            var tmdbStrSeries = series.GetProviderId(MetadataProvider.Tmdb);
            int.TryParse(tmdbStrSeries, out var tmdbIdSeries);
            if (tvdbId > 0 && ignoredTvdb.Contains(tvdbId))
            {
                var jfSeriesIdIg = series.Id.ToString("N");
                result.IgnoredSeries.Add(new ScanSeries
                {
                    SonarrId = 0,
                    TvdbId = tvdbId,
                    TmdbId = tmdbIdSeries,
                    JellyfinSeriesId = jfSeriesIdIg,
                    Title = series.Name,
                    Year = series.ProductionYear ?? 0,
                    Network = series.Studios?.FirstOrDefault(),
                    Status = series.Status?.ToString(),
                    Path = series.Path,
                    SeriesType = InferJellyfinSeriesType(series),
                    PosterUrl = "jellyfin:" + jfSeriesIdIg
                });
                continue;
            }

            var entry = await ProcessJellyfinSeriesAsync(cfg, series, mode, ct).ConfigureAwait(false);
            if (entry != null) result.Series.Add(entry);
        }

        FinalizeResult(result);
        return result;
    }

    // Populate the expected set from TMDB. Adds missing-episode entries for TMDB
    // episodes that aren't on disk. Does NOT mutate seasonStats — that's computed
    // by the caller once from the final present/expected union.
    private async Task<bool> FillFromTmdbAsync(
        PluginConfiguration cfg,
        int tmdbId,
        Dictionary<int, HashSet<int>> presentBySeason,
        Dictionary<int, HashSet<int>> expectedBySeason,
        List<MissingEpisode> missing,
        DateTime now,
        CancellationToken ct)
    {
        var detail = await _tmdb.GetSeriesAsync(cfg.TmdbApiKey, tmdbId, ct).ConfigureAwait(false);
        if (detail?.Seasons == null || detail.Seasons.Count == 0) return false;

        var tmdbSeasons = detail.Seasons.Select(x => x.SeasonNumber).Where(sn => !(cfg.IgnoreSpecials && sn == 0));

        foreach (var sn in tmdbSeasons)
        {
            TmdbSeasonDetail? season;
            try
            {
                season = await _tmdb.GetSeasonAsync(cfg.TmdbApiKey, tmdbId, sn, ct).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException)
            {
                continue;
            }
            if (season?.Episodes == null) continue;

            presentBySeason.TryGetValue(sn, out var presentSet);
            presentSet ??= new HashSet<int>();

            if (!expectedBySeason.TryGetValue(sn, out var expSet))
            {
                expSet = new HashSet<int>();
                expectedBySeason[sn] = expSet;
            }

            foreach (var ep in season.Episodes)
            {
                DateTime? air = null;
                if (!string.IsNullOrWhiteSpace(ep.AirDate)
                    && DateTime.TryParse(ep.AirDate, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    air = parsed;
                }
                if (cfg.IgnoreUnaired && (!air.HasValue || air.Value > now)) continue;

                expSet.Add(ep.EpisodeNumber);

                if (presentSet.Contains(ep.EpisodeNumber)) continue;

                var thumb = !string.IsNullOrEmpty(ep.StillPath) ? TmdbClient.ImageBase + ep.StillPath : null;
                missing.Add(new MissingEpisode
                {
                    Id = 0,
                    SeasonNumber = sn,
                    EpisodeNumber = ep.EpisodeNumber,
                    Title = ep.Name,
                    AirDateUtc = air,
                    Overview = ep.Overview,
                    ThumbnailUrl = thumb
                });
            }
        }
        return true;
    }

    public void ClearHistory()
    {
        try
        {
            var path = HistoryPath;
            if (path != null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear history");
        }
    }

    // Refresh a single series without a full rescan. Works in whichever source
    // is currently configured. Returns the updated ScanSeries (null if the
    // show couldn't be resolved).
    public async Task<ScanSeries?> RefreshSeriesAsync(int tvdbId, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
        if (tvdbId <= 0) throw new InvalidOperationException("A TVDB id is required.");

        var useJellyfin = string.Equals(cfg.ScanSource, "jellyfin", StringComparison.OrdinalIgnoreCase);
        var sonarrReady = IsSonarrConfigured(cfg);
        if (!useJellyfin && !sonarrReady)
            throw new InvalidOperationException("Sonarr must be configured to refresh in Sonarr mode.");

        // Use the shared progress object so the UI shows the familiar progress strip
        // even for a single-show refresh. Total = 1.
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        ScanSeries? updated;
        Progress.Reset(useJellyfin ? "jellyfin" : "sonarr");
        Progress.SetTotal(1);
        try
        {
            updated = useJellyfin
                ? await RefreshOneFromJellyfinAsync(cfg, tvdbId, sonarrReady, ct).ConfigureAwait(false)
                : await RefreshOneFromSonarrAsync(cfg, tvdbId, ct).ConfigureAwait(false);
        }
        finally
        {
            Progress.Finish();
            _scanLock.Release();
        }

        if (updated == null) return null;

        // Splice into the cached result and persist.
        var res = LastResult;
        if (res != null)
        {
            var idx = res.Series.FindIndex(x => x.TvdbId == tvdbId);
            if (idx >= 0)
            {
                if (updated.MissingCount == 0)
                    res.Series.RemoveAt(idx);
                else
                    res.Series[idx] = updated;
            }
            else if (updated.MissingCount > 0)
            {
                res.Series.Insert(0, updated);
            }
            res.TotalMissing = res.Series.Sum(x => x.MissingCount);
            PersistLast(res);
        }
        return updated;
    }

    private async Task<ScanSeries?> RefreshOneFromSonarrAsync(PluginConfiguration cfg, int tvdbId, CancellationToken ct)
    {
        // Use the TVDB-indexed Sonarr endpoint — O(1) on their side, not a full series dump.
        var s = await _sonarr.GetSeriesByTvdbAsync(cfg.SonarrUrl, cfg.SonarrApiKey, tvdbId, ct).ConfigureAwait(false);
        if (s == null) return null;
        Progress.Advance(s.Title);
        var eps = await _sonarr.GetEpisodesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, s.Id, ct).ConfigureAwait(false);
        return BuildSeriesFromSonarr(cfg, s, eps);
    }

    private ScanSeries? BuildSeriesFromSonarr(PluginConfiguration cfg, SonarrSeries s, List<SonarrEpisode> eps)
    {
        var now = DateTime.UtcNow;
        var seasonStats = new Dictionary<int, SeasonCount>();
        var missing = new List<MissingEpisode>();
        foreach (var ep in eps)
        {
            if (cfg.IgnoreSpecials && ep.SeasonNumber == 0) continue;
            if (cfg.OnlyMonitored && !ep.Monitored) continue;
            var unaired = ep.AirDateUtc == null || ep.AirDateUtc > now;
            if (cfg.IgnoreUnaired && unaired) continue;

            if (!seasonStats.TryGetValue(ep.SeasonNumber, out var stats)) stats = new SeasonCount();
            stats.Total += 1;
            if (ep.HasFile) stats.Have += 1;
            seasonStats[ep.SeasonNumber] = stats;

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

        var jfMap = BuildJellyfinIndex();
        jfMap.TryGetValue(s.TvdbId, out var jfId);

        return new ScanSeries
        {
            SonarrId = s.Id,
            TvdbId = s.TvdbId,
            TmdbId = s.TmdbId,
            JellyfinSeriesId = jfId,
            Title = s.Title,
            Year = s.Year,
            Network = s.Network,
            Status = s.Status,
            Path = s.Path,
            SeriesType = NormalizeSeriesType(s.SeriesType),
            PosterUrl = PickImage(s.Images, "poster"),
            BackdropUrl = PickImage(s.Images, "fanart") ?? PickImage(s.Images, "banner"),
            MissingCount = missing.Count,
            HaveEpisodes = seasonStats.Values.Sum(x => x.Have),
            TotalEpisodes = seasonStats.Values.Sum(x => x.Total),
            SizeOnDisk = s.Statistics?.SizeOnDisk ?? 0,
            Seasons = seasonStats.OrderBy(kv => kv.Key).Select(kv => new SeasonSummary
            {
                SeasonNumber = kv.Key,
                TotalEpisodes = kv.Value.Total,
                HaveEpisodes = kv.Value.Have
            }).ToList(),
            Missing = missing
        };
    }

    private async Task<ScanSeries?> RefreshOneFromJellyfinAsync(PluginConfiguration cfg, int tvdbId, bool sonarrReady, CancellationToken ct)
    {
        // Find the one Jellyfin series matching this TVDB id. No full library walk.
        var seriesItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        });
        Series? match = null;
        foreach (var item in seriesItems)
        {
            if (item is not Series s) continue;
            var tvdbStr = s.GetProviderId(MetadataProvider.Tvdb);
            if (int.TryParse(tvdbStr, out var id) && id == tvdbId) { match = s; break; }
        }
        if (match == null) return null;
        Progress.Advance(match.Name);

        var mode = (cfg.JellyfinScanMode ?? "virtual").ToLowerInvariant();
        if (mode != "virtual" && mode != "tmdb" && mode != "gap") mode = "virtual";
        if (mode == "tmdb" && string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            throw new InvalidOperationException("TMDB mode requires a TMDB API key in settings.");

        var updated = await ProcessJellyfinSeriesAsync(cfg, match, mode, ct).ConfigureAwait(false);
        if (updated != null && sonarrReady)
        {
            await EnrichOneFromSonarrAsync(cfg, updated, ct).ConfigureAwait(false);
        }
        return updated;
    }

    // Extracted per-series builder for Jellyfin scans. Used by both the full scan and the
    // per-show refresh.
    //
    // Data model:
    //   - presentBySeason = episodes that actually exist on disk. Built from Jellyfin's
    //     non-virtual episode items + any episodes parsed from the series folder
    //     (catches files Jellyfin never imported).
    //   - expectedBySeason = episodes that SHOULD exist. Built from mode:
    //       virtual -> Jellyfin's virtual items (+ present, since you have them too)
    //       tmdb    -> TMDB's episode list (+ present)
    //       gap     -> {1..max(present)} per season
    //
    // Stats = counts of these sets. Have = |present|. Total = |expected ∪ present|.
    // Missing = entries in expected not in present.
    //
    // Returns null for ignored series or ones with nothing missing.
    private async Task<ScanSeries?> ProcessJellyfinSeriesAsync(PluginConfiguration cfg, Series series, string mode, CancellationToken ct)
    {
        var ignoredTvdb = new HashSet<int>(cfg.IgnoredSeriesTvdbIds);
        var tvdbStr = series.GetProviderId(MetadataProvider.Tvdb);
        int.TryParse(tvdbStr, out var tvdbId);
        var tmdbStrSeries = series.GetProviderId(MetadataProvider.Tmdb);
        int.TryParse(tmdbStrSeries, out var tmdbIdSeries);
        if (tvdbId > 0 && ignoredTvdb.Contains(tvdbId)) return null;

        var now = DateTime.UtcNow;
        var allEps = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            Recursive = true
        });

        var presentBySeason = new Dictionary<int, HashSet<int>>();
        var jfExpectedBySeason = new Dictionary<int, HashSet<int>>();
        var jfVirtualMeta = new Dictionary<(int, int), Episode>();

        foreach (var e in allEps)
        {
            if (e is not Episode episode) continue;
            var season = episode.ParentIndexNumber;
            var num = episode.IndexNumber;
            if (!season.HasValue || !num.HasValue) continue;
            if (cfg.IgnoreSpecials && season.Value == 0) continue;

            var air = episode.PremiereDate;
            var unaired = !air.HasValue || air.Value.ToUniversalTime() > now;
            if (cfg.IgnoreUnaired && unaired) continue;

            var start = num.Value;
            var end = episode.IndexNumberEnd ?? start;

            if (!jfExpectedBySeason.TryGetValue(season.Value, out var exp))
            {
                exp = new HashSet<int>();
                jfExpectedBySeason[season.Value] = exp;
            }
            for (var n = start; n <= end; n++) exp.Add(n);

            if (episode.IsVirtualItem)
            {
                for (var n = start; n <= end; n++) jfVirtualMeta[(season.Value, n)] = episode;
            }
            else
            {
                if (!presentBySeason.TryGetValue(season.Value, out var set))
                {
                    set = new HashSet<int>();
                    presentBySeason[season.Value] = set;
                }
                for (var n = start; n <= end; n++) set.Add(n);
            }
        }

        // Walk the folder once for size + filename-parsed episodes. This catches the
        // Office/TMNT case where files exist but Jellyfin's library never imported them.
        var (sizeOnDisk, filenameEps) = ScanSeriesFolder(series.Path);
        foreach (var (s, e) in filenameEps)
        {
            if (cfg.IgnoreSpecials && s == 0) continue;
            if (!presentBySeason.TryGetValue(s, out var set))
            {
                set = new HashSet<int>();
                presentBySeason[s] = set;
            }
            set.Add(e);
        }

        // Build the expected set from the selected mode.
        var expectedBySeason = new Dictionary<int, HashSet<int>>();
        var missing = new List<MissingEpisode>();

        if (mode == "virtual")
        {
            // Expected = whatever Jellyfin knows about (real + virtual).
            foreach (var kv in jfExpectedBySeason)
            {
                expectedBySeason[kv.Key] = new HashSet<int>(kv.Value);
            }
            // Missing = virtual items not present.
            foreach (var kv in jfVirtualMeta)
            {
                var (s, n) = kv.Key;
                if (presentBySeason.TryGetValue(s, out var pres) && pres.Contains(n)) continue;
                var episode = kv.Value;
                var jfEpIdVirt = episode.Id.ToString("N");
                missing.Add(new MissingEpisode
                {
                    Id = 0,
                    SeasonNumber = s,
                    EpisodeNumber = n,
                    Title = episode.Name,
                    AirDateUtc = episode.PremiereDate?.ToUniversalTime(),
                    Overview = episode.Overview,
                    JellyfinEpisodeId = jfEpIdVirt,
                    ThumbnailUrl = "jellyfin:" + jfEpIdVirt
                });
            }
        }
        else if (mode == "tmdb" && tmdbIdSeries > 0)
        {
            try
            {
                await FillFromTmdbAsync(cfg, tmdbIdSeries, presentBySeason, expectedBySeason, missing, now, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TMDB fetch failed for {Title} ({Tmdb})", series.Name, tmdbIdSeries);
            }
        }
        else if (mode == "gap")
        {
            foreach (var kv in presentBySeason)
            {
                var sn = kv.Key;
                var present = kv.Value;
                if (present.Count == 0) continue;
                var max = present.Max();
                var set = new HashSet<int>();
                for (var n = 1; n <= max; n++) set.Add(n);
                expectedBySeason[sn] = set;
                for (var n = 1; n <= max; n++)
                {
                    if (present.Contains(n)) continue;
                    missing.Add(new MissingEpisode
                    {
                        Id = 0,
                        SeasonNumber = sn,
                        EpisodeNumber = n,
                        Title = null,
                        Overview = "Inferred from numbering gap in the Jellyfin library.",
                        JellyfinEpisodeId = null,
                        ThumbnailUrl = "jellyfin:" + series.Id.ToString("N")
                    });
                }
            }
        }

        // Compute final stats from the two sets. Have = files present. Total = files
        // expected (at minimum as many as are present, since an on-disk file implies
        // the episode exists).
        var seasonStats = new Dictionary<int, SeasonCount>();
        var allSeasons = new HashSet<int>(presentBySeason.Keys);
        foreach (var k in expectedBySeason.Keys) allSeasons.Add(k);
        foreach (var sn in allSeasons)
        {
            if (cfg.IgnoreSpecials && sn == 0) continue;
            var have = presentBySeason.TryGetValue(sn, out var p) ? p.Count : 0;
            var totalExpected = expectedBySeason.TryGetValue(sn, out var ex) ? ex.Count : 0;
            // Union size — some expected items also count as present; we want expected ∪ present.
            var unionCount = totalExpected;
            if (ex != null && p != null)
            {
                // Count present items not in expected and add to total.
                foreach (var n in p) if (!ex.Contains(n)) unionCount += 1;
            }
            else
            {
                unionCount = Math.Max(totalExpected, have);
            }
            seasonStats[sn] = new SeasonCount { Have = have, Total = unionCount };
        }

        if (missing.Count == 0) return null;

        var jfSeriesId = series.Id.ToString("N");
        return new ScanSeries
        {
            SonarrId = 0,
            TvdbId = tvdbId,
            TmdbId = tmdbIdSeries,
            JellyfinSeriesId = jfSeriesId,
            Title = series.Name,
            Year = series.ProductionYear ?? 0,
            Network = series.Studios?.FirstOrDefault(),
            Status = series.Status?.ToString(),
            Path = series.Path,
            SeriesType = InferJellyfinSeriesType(series),
            PosterUrl = "jellyfin:" + jfSeriesId,
            BackdropUrl = "jellyfin-backdrop:" + jfSeriesId,
            MissingCount = missing.Count,
            HaveEpisodes = seasonStats.Values.Sum(x => x.Have),
            TotalEpisodes = seasonStats.Values.Sum(x => x.Total),
            SizeOnDisk = sizeOnDisk,
            Seasons = seasonStats.OrderBy(kv => kv.Key).Select(kv => new SeasonSummary
            {
                SeasonNumber = kv.Key,
                TotalEpisodes = kv.Value.Total,
                HaveEpisodes = kv.Value.Have
            }).ToList(),
            Missing = missing
        };
    }

    // Targeted enrichment for a single ScanSeries — one Sonarr API call per series
    // instead of fetching the whole library.
    private async Task EnrichOneFromSonarrAsync(PluginConfiguration cfg, ScanSeries s, CancellationToken ct)
    {
        try
        {
            SonarrSeries? sonarrS = null;
            if (s.TvdbId > 0)
            {
                sonarrS = await _sonarr.GetSeriesByTvdbAsync(cfg.SonarrUrl, cfg.SonarrApiKey, s.TvdbId, ct)
                    .ConfigureAwait(false);
            }
            if (sonarrS == null) return;
            s.SonarrId = sonarrS.Id;
            var sonarrSize = sonarrS.Statistics?.SizeOnDisk ?? 0;
            if (s.SizeOnDisk == 0 && sonarrSize > 0)
            {
                s.SizeOnDisk = sonarrSize;
                if (!string.IsNullOrEmpty(sonarrS.Path)) s.Path = sonarrS.Path;
            }
            if (s.SeriesType == "standard")
            {
                var t = NormalizeSeriesType(sonarrS.SeriesType);
                if (t != "standard") s.SeriesType = t;
            }

            var eps = await _sonarr.GetEpisodesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, sonarrS.Id, ct)
                .ConfigureAwait(false);
            var epIndex = new Dictionary<(int, int), SonarrEpisode>();
            foreach (var e in eps) epIndex[(e.SeasonNumber, e.EpisodeNumber)] = e;
            foreach (var m in s.Missing)
            {
                if (epIndex.TryGetValue((m.SeasonNumber, m.EpisodeNumber), out var se))
                    m.Id = se.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Targeted Sonarr enrichment failed for {Title}", s.Title);
        }
    }

    private async Task EnrichWithSonarrIdsAsync(PluginConfiguration cfg, ScanResult result, CancellationToken ct)
    {
        try
        {
            var sonarrSeries = await _sonarr.GetSeriesAsync(cfg.SonarrUrl, cfg.SonarrApiKey, ct).ConfigureAwait(false);
            var byTvdb = new Dictionary<int, SonarrSeries>();
            var byTmdb = new Dictionary<int, SonarrSeries>();
            foreach (var x in sonarrSeries)
            {
                if (x.TvdbId > 0) byTvdb[x.TvdbId] = x;
                if (x.TmdbId > 0) byTmdb[x.TmdbId] = x;
            }

            foreach (var s in result.Series)
            {
                SonarrSeries? sonarrS = null;
                if (s.TvdbId > 0) byTvdb.TryGetValue(s.TvdbId, out sonarrS);
                if (sonarrS == null && s.TmdbId > 0) byTmdb.TryGetValue(s.TmdbId, out sonarrS);
                if (sonarrS == null) continue;
                s.SonarrId = sonarrS.Id;
                if (s.TvdbId <= 0 && sonarrS.TvdbId > 0) s.TvdbId = sonarrS.TvdbId;
                // Opportunistic data from Sonarr — fill only when missing so we don't clobber
                // Jellyfin's own values for the user-picked source.
                // Path + size reconciliation: trust whichever side actually has files.
                // If Jellyfin's folder is empty/missing (size==0) but Sonarr has content,
                // Jellyfin's path is stale — swap in Sonarr's path and size.
                // If Jellyfin has content, keep Jellyfin's numbers (user-sourced truth).
                var sonarrSize = sonarrS.Statistics?.SizeOnDisk ?? 0;
                if (s.SizeOnDisk == 0 && sonarrSize > 0)
                {
                    s.SizeOnDisk = sonarrSize;
                    if (!string.IsNullOrEmpty(sonarrS.Path)) s.Path = sonarrS.Path;
                }
                // If Sonarr knows the series as anime but Jellyfin didn't tag it, use Sonarr's type.
                if (s.SeriesType == "standard")
                {
                    var sonarrType = NormalizeSeriesType(sonarrS.SeriesType);
                    if (sonarrType != "standard") s.SeriesType = sonarrType;
                }

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
                        // Do NOT replace ThumbnailUrl here — leaving Jellyfin's own thumb intact.
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

    private static string NormalizeSeriesType(string? sonarrType)
    {
        if (string.IsNullOrEmpty(sonarrType)) return "standard";
        var lowered = sonarrType.ToLowerInvariant();
        return lowered switch
        {
            "anime" => "anime",
            "daily" => "daily",
            _ => "standard"
        };
    }

    private static string InferJellyfinSeriesType(Series series)
    {
        // Jellyfin has no first-class "anime" flag. Best-effort: look for an Anime genre/tag.
        if (series.Genres != null)
        {
            foreach (var g in series.Genres)
            {
                if (string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase)) return "anime";
            }
        }
        if (series.Tags != null)
        {
            foreach (var t in series.Tags)
            {
                if (string.Equals(t, "Anime", StringComparison.OrdinalIgnoreCase)) return "anime";
            }
        }
        return "standard";
    }

    private static void FinalizeResult(ScanResult result)
    {
        result.TotalMissing = result.Series.Sum(x => x.MissingCount);
        result.Series = result.Series
            .OrderByDescending(x => x.MissingCount)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // S01E05, s01e05, S1E5, multi-ep ranges like S01E01-E02 / S01E01E02.
    private static readonly Regex SeasonEpisodeRegex = new(
        @"[Ss](\d{1,3})[Ee](\d{1,3})(?:[-Ee](?:[Ee])?(\d{1,3}))?",
        RegexOptions.Compiled);
    // Alt '1x05' style (avoid matching parts of larger numbers).
    private static readonly Regex AltSeasonEpisodeRegex = new(
        @"(?<![0-9])(\d{1,2})x(\d{1,3})(?![0-9])",
        RegexOptions.Compiled);
    // Common video extensions Sonarr / Jellyfin care about.
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".ts", ".mpg", ".mpeg"
    };

    // Walk a series folder once: sum file sizes AND parse episode coordinates from
    // filenames. The parser is the safety net for shows where Jellyfin's library is
    // out of sync with the filesystem — files exist on disk but Jellyfin never
    // imported them, so its library lists them as missing.
    private static (long size, List<(int season, int episode)> episodes) ScanSeriesFolder(string? path)
    {
        var episodes = new List<(int, int)>();
        if (string.IsNullOrEmpty(path)) return (0, episodes);
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return (0, episodes);
            long total = 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; } catch { }
                if (!VideoExts.Contains(f.Extension)) continue;

                var m = SeasonEpisodeRegex.Match(f.Name);
                if (m.Success)
                {
                    var s = int.Parse(m.Groups[1].Value);
                    var e1 = int.Parse(m.Groups[2].Value);
                    var e2 = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : e1;
                    if (e2 < e1) e2 = e1; // malformed range — ignore
                    for (var e = e1; e <= e2; e++) episodes.Add((s, e));
                    continue;
                }
                var m2 = AltSeasonEpisodeRegex.Match(f.Name);
                if (m2.Success)
                {
                    episodes.Add((int.Parse(m2.Groups[1].Value), int.Parse(m2.Groups[2].Value)));
                }
            }
            return (total, episodes);
        }
        catch { return (0, episodes); }
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
        var filtered = episodeIds.Where(id => id > 0).Distinct().ToList();
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
    public List<ScanSeries> IgnoredSeries { get; set; } = new();
}

public class ScanSeries
{
    public int SonarrId { get; set; }
    public int TvdbId { get; set; }
    public int TmdbId { get; set; }
    public string? JellyfinSeriesId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Network { get; set; }
    public string? Status { get; set; }
    public string? Path { get; set; }
    public string SeriesType { get; set; } = "standard";
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int MissingCount { get; set; }
    public int HaveEpisodes { get; set; }
    public int TotalEpisodes { get; set; }
    public long SizeOnDisk { get; set; }
    public List<SeasonSummary> Seasons { get; set; } = new();
    public List<MissingEpisode> Missing { get; set; } = new();
}

public class SeasonSummary
{
    public int SeasonNumber { get; set; }
    public int TotalEpisodes { get; set; }
    public int HaveEpisodes { get; set; }
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

internal class SeasonCount
{
    public int Total { get; set; }
    public int Have { get; set; }
}

public class ScanHistoryEntry
{
    public DateTime ScannedAtUtc { get; set; }
    public string Source { get; set; } = "sonarr";
    public string? JellyfinMode { get; set; }
    public int TotalMissing { get; set; }
    public int ShowCount { get; set; }
    public int IgnoredCount { get; set; }
    public long DurationMs { get; set; }
}

public class ScanProgress
{
    public bool InProgress { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
    public string? CurrentTitle { get; set; }
    public string? Source { get; set; }
    public long StartedAtMs { get; set; }
    public long CompletedAtMs { get; set; }
    public string? LastError { get; set; }

    public void Reset(string source)
    {
        InProgress = true;
        Current = 0;
        Total = 0;
        CurrentTitle = null;
        Source = source;
        StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        CompletedAtMs = 0;
        LastError = null;
    }

    public void SetTotal(int total) { Total = total; }

    public void Advance(string? title)
    {
        Current += 1;
        CurrentTitle = title;
    }

    public void Finish()
    {
        InProgress = false;
        CurrentTitle = null;
        CompletedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
