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

    public record ScanRequest(string? Source);

    [HttpPost("scan")]
    [ProducesResponseType(typeof(ScanResult), StatusCodes.Status200OK)]
    public async Task<ActionResult> Scan([FromBody] ScanRequest? req, CancellationToken ct)
    {
        try
        {
            var result = await _service.ScanAsync(req?.Source, ct).ConfigureAwait(false);
            return Ok(result);
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            return StatusCode(502, new { error = "Sonarr unreachable: " + ex.Message });
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
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
