using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyseerrRequester.Api;

namespace Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;

/// <summary>
/// Maps raw Jellyseerr JSON to the plugin's DTOs.
/// </summary>
public static class JellyseerrMapper
{
    /// <summary>
    /// Maps a search result, movie or TV details node.
    /// </summary>
    /// <param name="node">Jellyseerr JSON node. Must contain "mediaType" or have it forced by <paramref name="mediaType"/>.</param>
    /// <param name="mediaType">Media type override, for details endpoints that don't return it.</param>
    /// <returns>The item, or null for unsupported media types (e.g. people).</returns>
    public static SearchItemDto? ToSearchItem(JsonNode? node, string? mediaType = null)
    {
        mediaType ??= node?["mediaType"]?.GetValue<string>();
        if (node is null || mediaType is not ("movie" or "tv"))
        {
            return null;
        }

        var isMovie = mediaType == "movie";
        var date = node[isMovie ? "releaseDate" : "firstAirDate"]?.GetValue<string>();
        var mediaInfo = node["mediaInfo"];
        var runtime = isMovie
            ? node["runtime"]?.GetValue<int>()
            : node["episodeRunTime"]?.AsArray().Select(r => r?.GetValue<int>()).FirstOrDefault(r => r > 0);

        return new SearchItemDto
        {
            Id = node["id"]?.GetValue<int>() ?? 0,
            MediaType = mediaType,
            Title = node[isMovie ? "title" : "name"]?.GetValue<string>() ?? string.Empty,
            Year = date is { Length: >= 4 } ? date[..4] : null,
            ReleaseDate = string.IsNullOrEmpty(date) ? null : date,
            Overview = node["overview"]?.GetValue<string>(),
            PosterPath = node["posterPath"]?.GetValue<string>(),
            BackdropPath = node["backdropPath"]?.GetValue<string>(),
            Rating = node["voteAverage"]?.GetValue<double>(),
            Tagline = node["tagline"]?.GetValue<string>(),
            RuntimeMinutes = runtime,
            Genres = node["genres"]?.AsArray().Select(g => g?["name"]?.GetValue<string>()).OfType<string>().ToArray(),
            Status = mediaInfo?["status"]?.GetValue<int>() ?? (int)MediaStatus.Unknown,
            Status4K = mediaInfo?["status4k"]?.GetValue<int>() ?? (int)MediaStatus.Unknown
        };
    }

    /// <summary>
    /// Builds TV details with a per-season status, taking open requests into account.
    /// </summary>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="raw">Raw Jellyseerr TV details.</param>
    /// <returns>TV details.</returns>
    public static TvDetailsDto BuildTvDetails(int tmdbId, JsonNode? raw)
    {
        var dto = new TvDetailsDto { Id = tmdbId, Title = raw?["name"]?.GetValue<string>() ?? string.Empty };
        var mediaInfo = raw?["mediaInfo"];

        var seasonStatus = new Dictionary<int, (int Status, int Status4K)>();
        foreach (var s in mediaInfo?["seasons"]?.AsArray() ?? new JsonArray())
        {
            var number = s?["seasonNumber"]?.GetValue<int>();
            if (number.HasValue)
            {
                seasonStatus[number.Value] = (s!["status"]?.GetValue<int>() ?? 1, s["status4k"]?.GetValue<int>() ?? 1);
            }
        }

        // Seasons with an open request (pending approval or approved) are not requestable again.
        var requested = new Dictionary<int, int>();
        var requested4K = new Dictionary<int, int>();
        foreach (var r in mediaInfo?["requests"]?.AsArray() ?? new JsonArray())
        {
            var requestStatus = r?["status"]?.GetValue<int>() ?? 0; // 1 pending, 2 approved, 3 declined
            if (requestStatus is not (1 or 2))
            {
                continue;
            }

            var mapped = requestStatus == 1 ? (int)MediaStatus.Pending : (int)MediaStatus.Processing;
            var target = r!["is4k"]?.GetValue<bool>() == true ? requested4K : requested;
            foreach (var s in r["seasons"]?.AsArray() ?? new JsonArray())
            {
                var number = s?["seasonNumber"]?.GetValue<int>();
                if (number.HasValue)
                {
                    target[number.Value] = mapped;
                }
            }
        }

        foreach (var s in raw?["seasons"]?.AsArray() ?? new JsonArray())
        {
            var number = s?["seasonNumber"]?.GetValue<int>() ?? 0;
            if (number <= 0)
            {
                continue; // Specials are not requestable by default in Jellyseerr.
            }

            var (status, status4K) = seasonStatus.TryGetValue(number, out var st) ? st : (1, 1);
            if (status == (int)MediaStatus.Unknown && requested.TryGetValue(number, out var rs))
            {
                status = rs;
            }

            if (status4K == (int)MediaStatus.Unknown && requested4K.TryGetValue(number, out var rs4))
            {
                status4K = rs4;
            }

            dto.Seasons.Add(new SeasonDto
            {
                SeasonNumber = number,
                Name = s!["name"]?.GetValue<string>() ?? "Season " + number,
                EpisodeCount = s["episodeCount"]?.GetValue<int>() ?? 0,
                Overview = s["overview"]?.GetValue<string>(),
                PosterPath = s["posterPath"]?.GetValue<string>(),
                AirDate = s["airDate"]?.GetValue<string>(),
                Status = status,
                Status4K = status4K
            });
        }

        return dto;
    }

    /// <summary>
    /// Whether an item or season with this status can be requested.
    /// </summary>
    /// <param name="status">Media status.</param>
    /// <returns>True when requestable.</returns>
    public static bool IsRequestable(int status) => status is (int)MediaStatus.Unknown or (int)MediaStatus.Deleted;
}
