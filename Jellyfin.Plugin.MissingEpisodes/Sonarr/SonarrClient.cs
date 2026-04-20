using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodes.Sonarr;

public class SonarrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SonarrClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SonarrClient(IHttpClientFactory httpClientFactory, ILogger<SonarrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public async Task<(bool ok, string? error)> TestConnectionAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Sonarr URL or API key is empty.");
        }
        try
        {
            using var client = CreateClient(baseUrl, apiKey);
            var resp = await client.GetAsync("api/v3/system/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"Sonarr returned HTTP {(int)resp.StatusCode}.");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sonarr connection test failed");
            return (false, ex.Message);
        }
    }

    public async Task<List<SonarrSeries>> GetSeriesAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, apiKey);
        var result = await client.GetFromJsonAsync<List<SonarrSeries>>("api/v3/series", JsonOptions, ct).ConfigureAwait(false);
        return result ?? new List<SonarrSeries>();
    }

    public async Task<List<SonarrEpisode>> GetEpisodesAsync(string baseUrl, string apiKey, int seriesId, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, apiKey);
        var result = await client.GetFromJsonAsync<List<SonarrEpisode>>(
            $"api/v3/episode?seriesId={seriesId}&includeImages=true", JsonOptions, ct).ConfigureAwait(false);
        return result ?? new List<SonarrEpisode>();
    }

    public async Task<bool> TriggerEpisodeSearchAsync(string baseUrl, string apiKey, IReadOnlyList<int> episodeIds, CancellationToken ct = default)
    {
        if (episodeIds.Count == 0) return true;
        using var client = CreateClient(baseUrl, apiKey);
        var body = new { name = "EpisodeSearch", episodeIds };
        var resp = await client.PostAsJsonAsync("api/v3/command", body, JsonOptions, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Sonarr EpisodeSearch failed: {Code} {Body}", (int)resp.StatusCode, text);
            return false;
        }
        return true;
    }

    public async Task<bool> TriggerSeriesSearchAsync(string baseUrl, string apiKey, int seriesId, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, apiKey);
        var body = new { name = "SeriesSearch", seriesId };
        var resp = await client.PostAsJsonAsync("api/v3/command", body, JsonOptions, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }
}
