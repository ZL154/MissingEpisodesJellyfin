using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MissingEpisodes.Sonarr;

public class SonarrSeries
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("sortTitle")] public string? SortTitle { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("year")] public int Year { get; set; }
    [JsonPropertyName("tvdbId")] public int TvdbId { get; set; }
    [JsonPropertyName("imdbId")] public string? ImdbId { get; set; }
    [JsonPropertyName("tmdbId")] public int TmdbId { get; set; }
    [JsonPropertyName("monitored")] public bool Monitored { get; set; }
    [JsonPropertyName("seriesType")] public string? SeriesType { get; set; }
    [JsonPropertyName("useSceneNumbering")] public bool UseSceneNumbering { get; set; }
    [JsonPropertyName("images")] public List<SonarrImage>? Images { get; set; }
    [JsonPropertyName("statistics")] public SonarrSeriesStatistics? Statistics { get; set; }
}

public class SonarrImage
{
    [JsonPropertyName("coverType")] public string? CoverType { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("remoteUrl")] public string? RemoteUrl { get; set; }
}

public class SonarrSeriesStatistics
{
    [JsonPropertyName("episodeFileCount")] public int EpisodeFileCount { get; set; }
    [JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }
    [JsonPropertyName("totalEpisodeCount")] public int TotalEpisodeCount { get; set; }
    [JsonPropertyName("sizeOnDisk")] public long SizeOnDisk { get; set; }
    [JsonPropertyName("percentOfEpisodes")] public double PercentOfEpisodes { get; set; }
}

public class SonarrEpisode
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("seriesId")] public int SeriesId { get; set; }
    [JsonPropertyName("tvdbId")] public int TvdbId { get; set; }
    [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episodeNumber")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("airDate")] public string? AirDate { get; set; }
    [JsonPropertyName("airDateUtc")] public DateTime? AirDateUtc { get; set; }
    [JsonPropertyName("hasFile")] public bool HasFile { get; set; }
    [JsonPropertyName("monitored")] public bool Monitored { get; set; }
    [JsonPropertyName("absoluteEpisodeNumber")] public int? AbsoluteEpisodeNumber { get; set; }
    [JsonPropertyName("finaleType")] public string? FinaleType { get; set; }
}
