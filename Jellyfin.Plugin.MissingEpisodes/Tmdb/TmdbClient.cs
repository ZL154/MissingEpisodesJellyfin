using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingEpisodes.Tmdb;

public class TmdbClient
{
    public const string ImageBase = "https://image.tmdb.org/t/p/w300";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    public async Task<(bool ok, string? error)> TestAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "TMDB key is empty.");
        try
        {
            using var client = CreateClient();
            var resp = await client.GetAsync("authentication?api_key=" + Uri.EscapeDataString(apiKey), ct)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, null);
            if ((int)resp.StatusCode == 401) return (false, "TMDB rejected the API key.");
            return (false, "TMDB returned HTTP " + (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB test failed");
            return (false, ex.Message);
        }
    }

    public async Task<TmdbSeriesDetail?> GetSeriesAsync(string apiKey, int tmdbId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        return await client.GetFromJsonAsync<TmdbSeriesDetail>(
            $"tv/{tmdbId}?api_key={Uri.EscapeDataString(apiKey)}", JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<TmdbSeasonDetail?> GetSeasonAsync(string apiKey, int tmdbId, int seasonNumber, CancellationToken ct = default)
    {
        using var client = CreateClient();
        return await client.GetFromJsonAsync<TmdbSeasonDetail>(
            $"tv/{tmdbId}/season/{seasonNumber}?api_key={Uri.EscapeDataString(apiKey)}", JsonOptions, ct).ConfigureAwait(false);
    }
}

public class TmdbSeriesDetail
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("number_of_seasons")] public int NumberOfSeasons { get; set; }
    [JsonPropertyName("number_of_episodes")] public int NumberOfEpisodes { get; set; }
    [JsonPropertyName("seasons")] public List<TmdbSeasonSummary>? Seasons { get; set; }
}

public class TmdbSeasonSummary
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class TmdbSeasonDetail
{
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episodes")] public List<TmdbEpisode>? Episodes { get; set; }
}

public class TmdbEpisode
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    [JsonPropertyName("still_path")] public string? StillPath { get; set; }
}
