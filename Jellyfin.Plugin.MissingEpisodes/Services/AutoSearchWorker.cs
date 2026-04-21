using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodes.Services;

public class AutoSearchWorker : BackgroundService
{
    private readonly MissingEpisodesService _service;
    private readonly ILogger<AutoSearchWorker> _logger;

    public AutoSearchWorker(MissingEpisodesService service, ILogger<AutoSearchWorker> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Jellyfin finish booting before the first pass.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg != null
                    && cfg.AutoSearchEnabled
                    && !string.IsNullOrWhiteSpace(cfg.SonarrUrl)
                    && !string.IsNullOrWhiteSpace(cfg.SonarrApiKey))
                {
                    var due = cfg.LastAutoSearchIso == null
                        || !DateTime.TryParse(cfg.LastAutoSearchIso, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                        || (DateTime.UtcNow - last).TotalHours >= Math.Max(1, cfg.AutoSearchIntervalHours);

                    if (due)
                    {
                        _logger.LogInformation("MissingEpisodes: auto-search pass starting.");
                        var result = await _service.ScanAsync(null, stoppingToken).ConfigureAwait(false);
                        var ids = result.Series.SelectMany(s => s.Missing.Select(m => m.Id)).ToList();
                        if (ids.Count > 0)
                        {
                            var n = await _service.TriggerSearchAsync(ids, stoppingToken).ConfigureAwait(false);
                            _logger.LogInformation("MissingEpisodes: requested search for {N} episodes.", n);
                        }
                        cfg.LastAutoSearchIso = DateTime.UtcNow.ToString("o");
                        Plugin.Instance?.SaveConfiguration();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MissingEpisodes: auto-search pass failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
