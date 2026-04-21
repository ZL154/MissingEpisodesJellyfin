using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingEpisodes.Services;
using Jellyfin.Plugin.MissingEpisodes.Sonarr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MissingEpisodes.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/MissingEpisodes")]
public class MissingEpisodesController : ControllerBase
{
    private readonly MissingEpisodesService _service;
    private readonly SonarrClient _sonarr;

    public MissingEpisodesController(MissingEpisodesService service, SonarrClient sonarr)
    {
        _service = service;
        _sonarr = sonarr;
    }

    public record TestConnectionRequest(string? Url, string? ApiKey);

    [HttpPost("test")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> TestConnection([FromBody] TestConnectionRequest req, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var url = string.IsNullOrWhiteSpace(req.Url) ? cfg?.SonarrUrl ?? string.Empty : req.Url!;
        var key = string.IsNullOrWhiteSpace(req.ApiKey) ? cfg?.SonarrApiKey ?? string.Empty : req.ApiKey!;
        var (ok, error) = await _sonarr.TestConnectionAsync(url, key, ct).ConfigureAwait(false);
        return Ok(new { ok, error });
    }

    public record TestTmdbRequest(string? ApiKey);

    [HttpPost("tmdb/test")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> TestTmdb([FromBody] TestTmdbRequest req, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var key = string.IsNullOrWhiteSpace(req.ApiKey) ? cfg?.TmdbApiKey ?? string.Empty : req.ApiKey!;
        var (ok, error) = await _service.Tmdb.TestAsync(key, ct).ConfigureAwait(false);
        return Ok(new { ok, error });
    }

    public record ScanRequest(string? Source);

    // Fire-and-forget: validates config synchronously so the user gets errors
    // (bad creds, wrong source) immediately, then starts the scan on a background
    // task. The UI polls /progress while it runs and /last when done. Navigating
    // away from the plugin page no longer aborts the scan.
    [HttpPost("scan")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    public ActionResult Scan([FromBody] ScanRequest? req)
    {
        if (_service.Progress.InProgress)
        {
            return Accepted(new { running = true, note = "Scan already in progress." });
        }

        // Fail fast on obvious misconfigurations so the user sees the error in the
        // scan button's response, not only by polling progress.
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return StatusCode(500, new { error = "Plugin not initialized." });
        var sourceCheck = string.IsNullOrWhiteSpace(req?.Source) ? cfg.ScanSource : req!.Source;
        var useJellyfin = string.Equals(sourceCheck, "jellyfin", System.StringComparison.OrdinalIgnoreCase);
        if (!useJellyfin && !MissingEpisodesService.IsSonarrConfigured(cfg))
        {
            return BadRequest(new { error = "Sonarr scans need a URL and API key. Switch to Jellyfin or fill these in." });
        }
        if (useJellyfin && string.Equals(cfg.JellyfinScanMode, "tmdb", System.StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
        {
            return BadRequest(new { error = "TMDB mode requires a TMDB API key in settings." });
        }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await _service.ScanAsync(req?.Source, System.Threading.CancellationToken.None).ConfigureAwait(false); }
            catch (System.Exception ex)
            {
                _service.Progress.LastError = ex.Message;
            }
        });
        return Accepted(new { started = true });
    }

    [HttpGet("progress")]
    [ProducesResponseType(typeof(ScanProgress), StatusCodes.Status200OK)]
    public ActionResult GetProgress() => Ok(_service.Progress);

    [HttpGet("last")]
    [ProducesResponseType(typeof(ScanResult), StatusCodes.Status200OK)]
    public ActionResult GetLast()
    {
        var last = _service.LastResult;
        if (last == null) return Ok(new { empty = true });
        return Ok(last);
    }

    public record SearchRequest(List<int> EpisodeIds);

    [HttpPost("search")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> Search([FromBody] SearchRequest req, CancellationToken ct)
    {
        if (req.EpisodeIds == null || req.EpisodeIds.Count == 0)
        {
            return BadRequest(new { error = "No episode ids supplied." });
        }
        var sent = await _service.TriggerSearchAsync(req.EpisodeIds, ct).ConfigureAwait(false);
        return Ok(new { sent });
    }

    [HttpPost("series/{tvdbId}/ignore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Ignore(int tvdbId)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return NotFound();
        if (!cfg.IgnoredSeriesTvdbIds.Contains(tvdbId))
        {
            cfg.IgnoredSeriesTvdbIds.Add(tvdbId);
            Plugin.Instance!.SaveConfiguration();
        }
        return NoContent();
    }

    [HttpPost("series/{tvdbId}/unignore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Unignore(int tvdbId)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null) return NotFound();
        if (cfg.IgnoredSeriesTvdbIds.Remove(tvdbId))
        {
            Plugin.Instance!.SaveConfiguration();
        }
        return NoContent();
    }
}
