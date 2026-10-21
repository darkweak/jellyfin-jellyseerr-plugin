using System.Collections.Generic;

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
