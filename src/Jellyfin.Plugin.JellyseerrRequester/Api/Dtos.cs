using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyseerrRequester.Api;

/// <summary>
/// Client-side settings and the current user's link status.
/// </summary>
public class ClientConfigDto
{
    /// <summary>Gets or sets a value indicating whether the plugin is configured.</summary>
    public bool Configured { get; set; }

    /// <summary>Gets or sets a value indicating whether 4K requests are offered.</summary>
    public bool Enable4K { get; set; }

    /// <summary>Gets or sets the public Jellyseerr URL.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the current user can request.</summary>
    public bool CanRequest { get; set; }

    /// <summary>Gets or sets the linked Jellyseerr display name, if any.</summary>
    public string? JellyseerrUser { get; set; }

    /// <summary>Gets or sets a reason why the user cannot request.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets a value indicating whether the Request button is added to detail pages.</summary>
    public bool RequestButton { get; set; }

    /// <summary>Gets or sets a value indicating whether the discover rows are added to the home page.</summary>
    public bool DiscoverRows { get; set; }

    /// <summary>Gets or sets the product name to show ("Jellyseerr" or "Seerr").</summary>
    public string ServerName { get; set; } = "Jellyseerr";

    /// <summary>Gets or sets a value indicating whether the current user can remove downloads.</summary>
    public bool CanRemove { get; set; }

    /// <summary>Gets or sets a value indicating whether the Downloads row is added to the home page.</summary>
    public bool DownloadsRow { get; set; }

    /// <summary>Gets or sets the age the user may request up to, when they're in child mode.</summary>
    public int? ChildMaxAge { get; set; }
}

/// <summary>
/// Download progress of a requested title, as Radarr/Sonarr report it through Jellyseerr.
/// </summary>
public class DownloadDto
{
    /// <summary>Gets or sets the state: downloading, queued, paused, importing, stalled, waiting or missing.</summary>
    public string State { get; set; } = "downloading";

    /// <summary>Gets or sets a value indicating whether the download looks stuck.</summary>
    public bool Stale { get; set; }

    /// <summary>Gets or sets the progress, 0 to 1, when known.</summary>
    public double? Progress { get; set; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the bytes left.</summary>
    public long SizeLeft { get; set; }

    /// <summary>Gets or sets the number of items in the download queue.</summary>
    public int Items { get; set; }

    /// <summary>Gets or sets the estimated completion time.</summary>
    public DateTime? EstimatedCompletion { get; set; }

    /// <summary>Gets or sets when the request was approved (or the media last changed).</summary>
    public DateTime? Since { get; set; }

    /// <summary>Gets or sets a short label, e.g. "45% · 1h 20m" or "Stalled".</summary>
    public string Short { get; set; } = string.Empty;

    /// <summary>Gets or sets a one-sentence description.</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// A title in a home page discover row.
/// </summary>
public class DiscoverItemDto
{
    /// <summary>Gets or sets the Jellyfin item id: the library item when it's in the library, otherwise a virtual id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the title is in the Jellyfin library.</summary>
    public bool InLibrary { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public string? Year { get; set; }

    /// <summary>Gets or sets the media status (see MediaStatus).</summary>
    public int Status { get; set; } = 1;

    /// <summary>Gets or sets the primary image tag (virtual items only).</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets the TMDB poster path.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the download progress (Downloads row only).</summary>
    public DownloadDto? Download { get; set; }

    /// <summary>Gets or sets who requested it (Downloads row only).</summary>
    public string? RequestedBy { get; set; }
}

/// <summary>
/// A home page discover row.
/// </summary>
public class DiscoverResponseDto
{
    /// <summary>Gets or sets the list ("trending", "movies" or "tv").</summary>
    public string List { get; set; } = string.Empty;

    /// <summary>Gets the titles.</summary>
    public List<DiscoverItemDto> Results { get; } = new();
}

/// <summary>
/// Search result item.
/// </summary>
public class SearchItemDto
{
    /// <summary>Gets or sets the TMDB id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public string? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the full release / first air date.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the TMDB poster path.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the TMDB backdrop path.</summary>
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the tagline (details only).</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the runtime in minutes (details only).</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets the genres (details only).</summary>
    public string[]? Genres { get; set; }

    /// <summary>Gets or sets the TMDB vote average.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the media status (see MediaStatus).</summary>
    public int Status { get; set; } = 1;

    /// <summary>Gets or sets the 4K media status.</summary>
    public int Status4K { get; set; } = 1;

    /// <summary>Gets or sets the Jellyseerr media id, once the title has been requested.</summary>
    public int? MediaId { get; set; }

    /// <summary>Gets or sets the download progress while the title is being downloaded.</summary>
    public DownloadDto? Download { get; set; }

    /// <summary>Gets or sets the ids of open (pending or approved) standard-quality requests.</summary>
    [JsonIgnore]
    public List<int> OpenRequestIds { get; set; } = new();

    /// <summary>Gets or sets the age rating, e.g. "PG-13" (only known once details are loaded).</summary>
    public string? AgeRating { get; set; }

    /// <summary>Gets or sets the minimum age for <see cref="AgeRating"/>.</summary>
    public int? MinimumAge { get; set; }

    /// <summary>Gets or sets why the current (child) user may not request this title, if so.</summary>
    public string? NotAllowedReason { get; set; }

    /// <summary>
    /// Copies the title for one user, so per-user fields don't leak into the shared cache.
    /// </summary>
    /// <param name="notAllowedReason">Why this user may not request it.</param>
    /// <returns>A shallow copy.</returns>
    public SearchItemDto ForUser(string? notAllowedReason)
    {
        var copy = (SearchItemDto)MemberwiseClone();
        copy.NotAllowedReason = notAllowedReason;
        return copy;
    }
}

/// <summary>
/// Search response.
/// </summary>
public class SearchResponseDto
{
    /// <summary>Gets or sets the page.</summary>
    public int Page { get; set; }

    /// <summary>Gets or sets the total pages.</summary>
    public int TotalPages { get; set; }

    /// <summary>Gets the results.</summary>
    public List<SearchItemDto> Results { get; } = new();
}

/// <summary>
/// TV season with request status.
/// </summary>
public class SeasonDto
{
    /// <summary>Gets or sets the season number.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the season name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode count.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the TMDB poster path.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the air date.</summary>
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public int Status { get; set; } = 1;

    /// <summary>Gets or sets the 4K status.</summary>
    public int Status4K { get; set; } = 1;
}

/// <summary>
/// TV details for the season picker.
/// </summary>
public class TvDetailsDto
{
    /// <summary>Gets or sets the TMDB id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets the seasons.</summary>
    public List<SeasonDto> Seasons { get; } = new();
}

/// <summary>
/// Request body for creating a request.
/// </summary>
public class CreateRequestDto
{
    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the season numbers (TV only, empty = all).</summary>
    public List<int>? Seasons { get; set; }

    /// <summary>Gets or sets a value indicating whether to request 4K.</summary>
    public bool Is4K { get; set; }
}

/// <summary>
/// Request body for removing a download.
/// </summary>
public class RemoveDto
{
    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }
}

/// <summary>
/// Body for testing a connection from the configuration page.
/// </summary>
public class TestConnectionDto
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the API key.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Simple message response.
/// </summary>
public class MessageDto
{
    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a request was auto-approved.</summary>
    public bool Approved { get; set; }
}
