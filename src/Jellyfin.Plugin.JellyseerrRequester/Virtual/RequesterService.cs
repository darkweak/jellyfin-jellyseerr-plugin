using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyseerrRequester.Api;
using Jellyfin.Plugin.JellyseerrRequester.Configuration;
using Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyseerrRequester.Virtual;

/// <summary>
/// Jellyseerr titles exposed as virtual Jellyfin items, plus request handling shared by the
/// plugin API (web dialog) and the search/favorite hooks (all clients).
/// </summary>
public class RequesterService
{
    // Jellyfin's internal claim type for the user id (Jellyfin.Api.Constants.InternalClaimTypes.UserId).
    private const string UserIdClaim = "Jellyfin-UserId";

    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TitleCacheDuration = TimeSpan.FromMinutes(1);

    private readonly JellyseerrClient _client;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RequesterService> _logger;

    private readonly ConcurrentDictionary<string, (DateTime Expiry, Task<List<SearchItemDto>> Task)> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<VirtualId, CachedTitle> _titles = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RequesterService"/> class.
    /// </summary>
    /// <param name="client">Jellyseerr client.</param>
    /// <param name="host">Server application host.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public RequesterService(JellyseerrClient client, IServerApplicationHost host, ILibraryManager libraryManager, ILogger<RequesterService> logger)
    {
        _client = client;
        _host = host;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Searches Jellyseerr. Results are cached briefly and concurrent identical searches share one call,
    /// because clients fire one search per item type for every keystroke.
    /// </summary>
    /// <param name="query">Query.</param>
    /// <returns>Movies and shows, filtered by the configured availability rules.</returns>
    public Task<List<SearchItemDto>> SearchAsync(string query)
    {
        query = query.Trim();
        var now = DateTime.UtcNow;
        if (_searchCache.TryGetValue(query, out var cached) && cached.Expiry > now && !cached.Task.IsFaulted)
        {
            return cached.Task;
        }

        var task = SearchUncachedAsync(query);
        _searchCache[query] = (now + SearchCacheDuration, task);

        if (_searchCache.Count > 500)
        {
            foreach (var key in _searchCache.Where(kv => kv.Value.Expiry < now).Select(kv => kv.Key).ToList())
            {
                _searchCache.TryRemove(key, out _);
            }
        }

        return task;
    }

    private async Task<List<SearchItemDto>> SearchUncachedAsync(string query)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var raw = await _client.SearchAsync(query, 1, cts.Token).ConfigureAwait(false);
        var hideAvailable = Config.HideAvailable;
        var results = new List<SearchItemDto>();
        foreach (var node in raw?["results"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
        {
            var item = JellyseerrMapper.ToSearchItem(node);
            if (item is null || item.Id <= 0
                || item.Status == (int)MediaStatus.Blacklisted
                || (hideAvailable && item.Status == (int)MediaStatus.Available)
                || IsInLibrary(item))
            {
                continue;
            }

            results.Add(item);
            var id = new VirtualId(item.MediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, item.Id);
            _titles.AddOrUpdate(
                id,
                _ => new CachedTitle(item, null, false),
                (_, existing) => existing with { Item = MergeSearch(existing.Item, item) });
        }

        return results;
    }

    /// <summary>
    /// Whether the title is already in the Jellyfin library (Jellyseerr's availability sync can lag behind).
    /// </summary>
    private bool IsInLibrary(SearchItemDto item)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { item.MediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series },
                HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = item.Id.ToString(CultureInfo.InvariantCulture) },
                Recursive = true,
                Limit = 1,
                DtoOptions = new DtoOptions(false)
            };
            return _libraryManager.GetItemList(query).Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jellyseerr Requester: library lookup failed for TMDB {Id}", item.Id);
            return false;
        }
    }

    private static SearchItemDto MergeSearch(SearchItemDto existing, SearchItemDto fresh)
    {
        // Keep details-only fields from an earlier details fetch.
        fresh.Tagline ??= existing.Tagline;
        fresh.Genres ??= existing.Genres;
        fresh.RuntimeMinutes ??= existing.RuntimeMinutes;
        return fresh;
    }

    // ------------------------------------------------------------------ titles

    /// <summary>
    /// Gets a title, fetching details from Jellyseerr when missing or stale.
    /// </summary>
    /// <param name="id">Virtual id (a season id resolves to its series).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached title.</returns>
    public async Task<CachedTitle> GetTitleAsync(VirtualId id, CancellationToken cancellationToken)
    {
        id = id.Series;
        if (_titles.TryGetValue(id, out var cached) && cached.Detailed && cached.FetchedAt + TitleCacheDuration > DateTime.UtcNow)
        {
            return cached;
        }

        CachedTitle title;
        if (id.Kind == VirtualKind.Movie)
        {
            var raw = await _client.GetMovieAsync(id.TmdbId, cancellationToken).ConfigureAwait(false);
            var item = JellyseerrMapper.ToSearchItem(raw, "movie") ?? throw new JellyseerrException(404, "Movie not found.");
            title = new CachedTitle(item, null, true);
        }
        else
        {
            var raw = await _client.GetTvAsync(id.TmdbId, cancellationToken).ConfigureAwait(false);
            var item = JellyseerrMapper.ToSearchItem(raw, "tv") ?? throw new JellyseerrException(404, "Series not found.");
            title = new CachedTitle(item, JellyseerrMapper.BuildTvDetails(id.TmdbId, raw), true);
        }

        _titles[id] = title;
        return title;
    }

    /// <summary>
    /// Gets a title from cache only (used for images, which must be fast).
    /// </summary>
    /// <param name="id">Virtual id.</param>
    /// <returns>The cached title or null.</returns>
    public CachedTitle? PeekTitle(VirtualId id) => _titles.TryGetValue(id.Series, out var t) ? t : null;

    // ------------------------------------------------------------------ users & requests

    /// <summary>
    /// Resolves the Jellyseerr account to request as. Returns null to request as the API key owner.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Linked user or null.</returns>
    public async Task<JellyseerrUser?> ResolveUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId))
        {
            throw new JellyseerrException(StatusCodes.Status403Forbidden, "Could not identify the Jellyfin user.");
        }

        var linked = await _client.FindLinkedUserAsync(userId, principal.Identity?.Name, cancellationToken).ConfigureAwait(false);
        if (linked is not null || Config.AllowUnlinkedUsers)
        {
            return linked;
        }

        throw new JellyseerrException(
            StatusCodes.Status403Forbidden,
            "Your Jellyfin account is not linked to a Jellyseerr account. Ask your administrator to import it in Jellyseerr (Users → Import Jellyfin Users).");
    }

    /// <summary>
    /// Returns why the user cannot request, or null when they can.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reason or null.</returns>
    public async Task<string?> GetCannotRequestReasonAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        try
        {
            await ResolveUserAsync(principal, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (JellyseerrException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Creates a request as the user's linked Jellyseerr account.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="body">What to request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message.</returns>
    public async Task<MessageDto> RequestAsync(ClaimsPrincipal principal, CreateRequestDto body, CancellationToken cancellationToken)
    {
        if (body.MediaType is not ("movie" or "tv") || body.TmdbId <= 0)
        {
            throw new JellyseerrException(StatusCodes.Status400BadRequest, "Invalid media type or id.");
        }

        if (body.Is4K && !Config.Enable4K)
        {
            throw new JellyseerrException(StatusCodes.Status400BadRequest, "4K requests are disabled.");
        }

        var user = await ResolveUserAsync(principal, cancellationToken).ConfigureAwait(false);
        var seasons = body.Seasons?.Where(s => s > 0).Distinct().ToList();
        var result = await _client.CreateRequestAsync(body.MediaType, body.TmdbId, seasons, body.Is4K, user?.Id, cancellationToken).ConfigureAwait(false);
        var approved = result?["status"]?.GetValue<int>() == 2;

        _logger.LogInformation(
            "{User} requested {Type} {TmdbId}{Seasons} via Jellyseerr (as {JellyseerrUser})",
            principal.Identity?.Name,
            body.MediaType,
            body.TmdbId,
            seasons is { Count: > 0 } ? " seasons " + string.Join(',', seasons) : string.Empty,
            user?.DisplayName ?? "API key owner");

        if (!body.Is4K)
        {
            MarkRequested(new VirtualId(body.MediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, body.TmdbId), seasons, approved);
        }

        return new MessageDto { Message = approved ? "Request approved." : "Request submitted.", Approved = approved };
    }

    /// <summary>
    /// Requests a virtual item (movie, whole series, or one season), e.g. when a user marks it as favorite.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="id">Virtual id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message.</returns>
    public async Task<MessageDto> RequestAsync(ClaimsPrincipal principal, VirtualId id, CancellationToken cancellationToken)
    {
        var body = new CreateRequestDto { MediaType = id.MediaType, TmdbId = id.TmdbId };
        if (id.Kind != VirtualKind.Movie)
        {
            var title = await GetTitleAsync(id, cancellationToken).ConfigureAwait(false);
            var requestable = (title.Tv?.Seasons ?? new List<SeasonDto>())
                .Where(s => JellyseerrMapper.IsRequestable(s.Status) && (id.Kind == VirtualKind.Series || s.SeasonNumber == id.Season))
                .Select(s => s.SeasonNumber)
                .ToList();
            if (requestable.Count == 0)
            {
                throw new JellyseerrException(StatusCodes.Status409Conflict, "Already requested or available.");
            }

            body.Seasons = requestable;
        }
        else
        {
            var title = await GetTitleAsync(id, cancellationToken).ConfigureAwait(false);
            if (!JellyseerrMapper.IsRequestable(title.Item.Status))
            {
                throw new JellyseerrException(StatusCodes.Status409Conflict, "Already requested or available.");
            }
        }

        return await RequestAsync(principal, body, cancellationToken).ConfigureAwait(false);
    }

    private void MarkRequested(VirtualId id, List<int>? seasons, bool approved)
    {
        var status = approved ? (int)MediaStatus.Processing : (int)MediaStatus.Pending;
        if (!_titles.TryGetValue(id, out var title))
        {
            return;
        }

        if (JellyseerrMapper.IsRequestable(title.Item.Status))
        {
            title.Item.Status = status;
        }

        foreach (var season in title.Tv?.Seasons ?? new List<SeasonDto>())
        {
            if ((seasons is null || seasons.Count == 0 || seasons.Contains(season.SeasonNumber)) && JellyseerrMapper.IsRequestable(season.Status))
            {
                season.Status = status;
            }
        }

        // Force a refresh on next details view, so Jellyseerr's view of the request wins.
        _titles[id] = title with { FetchedAt = DateTime.MinValue };
        foreach (var key in _searchCache.Keys)
        {
            _searchCache.TryRemove(key, out _);
        }
    }

    // ------------------------------------------------------------------ Jellyfin DTOs

    /// <summary>
    /// Builds the Jellyfin item for a movie or series.
    /// </summary>
    /// <param name="item">Jellyseerr item.</param>
    /// <param name="tv">TV details, if loaded.</param>
    /// <param name="cannotRequestReason">Why the current user cannot request, if applicable.</param>
    /// <returns>The item DTO.</returns>
    public BaseItemDto ToDto(SearchItemDto item, TvDetailsDto? tv, string? cannotRequestReason)
    {
        var isMovie = item.MediaType == "movie";
        var id = new VirtualId(isMovie ? VirtualKind.Movie : VirtualKind.Series, item.Id);
        var status = EffectiveStatus(item, tv);

        var dto = BaseDto(id, item.Title, status, item.PosterPath, item.BackdropPath);
        dto.Type = isMovie ? BaseItemKind.Movie : BaseItemKind.Series;
        dto.IsFolder = !isMovie;
        dto.ProductionYear = int.TryParse(item.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null;
        dto.PremiereDate = ParseDate(item.ReleaseDate);
        dto.CommunityRating = item.Rating is > 0 ? (float)Math.Round(item.Rating.Value, 1) : null;
        dto.Overview = BuildOverview(item.Overview, status, isMovie, cannotRequestReason);
        dto.Taglines = new[] { StatusText(status) };
        dto.Genres = item.Genres ?? Array.Empty<string>();
        dto.RunTimeTicks = item.RuntimeMinutes is > 0 ? TimeSpan.FromMinutes(item.RuntimeMinutes.Value).Ticks : null;
        dto.ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = item.Id.ToString(CultureInfo.InvariantCulture) };
        if (tv is not null)
        {
            dto.ChildCount = tv.Seasons.Count;
        }

        return dto;
    }

    /// <summary>
    /// Builds the Jellyfin item for a season.
    /// </summary>
    /// <param name="series">Series item.</param>
    /// <param name="season">Season.</param>
    /// <param name="cannotRequestReason">Why the current user cannot request, if applicable.</param>
    /// <returns>The season DTO.</returns>
    public BaseItemDto ToSeasonDto(SearchItemDto series, SeasonDto season, string? cannotRequestReason)
    {
        var seriesId = new VirtualId(VirtualKind.Series, series.Id);
        var id = new VirtualId(VirtualKind.Season, series.Id, season.SeasonNumber);
        var dto = BaseDto(id, season.Name, season.Status, season.PosterPath ?? series.PosterPath, series.BackdropPath);
        dto.Type = BaseItemKind.Season;
        dto.IsFolder = true;
        dto.IndexNumber = season.SeasonNumber;
        dto.SeriesId = seriesId.ToGuid();
        dto.SeriesName = series.Title;
        dto.ParentId = seriesId.ToGuid();
        dto.SeriesPrimaryImageTag = ImageTag(series.PosterPath, series.Status);
        dto.PremiereDate = ParseDate(season.AirDate);
        dto.ProductionYear = dto.PremiereDate?.Year;
        dto.ChildCount = season.EpisodeCount;
        dto.Overview = BuildOverview(
            (season.EpisodeCount > 0 ? season.EpisodeCount + " episodes. " : string.Empty) + season.Overview,
            season.Status,
            false,
            cannotRequestReason);
        dto.Taglines = new[] { StatusText(season.Status) };
        return dto;
    }

    /// <summary>
    /// Status shown for a title. Jellyseerr reports a show as "partially available" or "unknown" even when
    /// every missing season has already been requested; show it as requested in that case.
    /// </summary>
    /// <param name="item">Title.</param>
    /// <param name="tv">TV details, if loaded.</param>
    /// <returns>Media status.</returns>
    public static int EffectiveStatus(SearchItemDto item, TvDetailsDto? tv)
    {
        if (item.MediaType == "tv"
            && tv is { Seasons.Count: > 0 }
            && item.Status is (int)MediaStatus.Unknown or (int)MediaStatus.PartiallyAvailable or (int)MediaStatus.Deleted
            && tv.Seasons.All(s => !JellyseerrMapper.IsRequestable(s.Status)))
        {
            return tv.Seasons.Any(s => s.Status == (int)MediaStatus.Pending) ? (int)MediaStatus.Pending : (int)MediaStatus.Processing;
        }

        return item.Status;
    }

    private BaseItemDto BaseDto(VirtualId id, string name, int status, string? posterPath, string? backdropPath)
    {
        var guid = id.ToGuid();
        var tag = ImageTag(posterPath, status);
        return new BaseItemDto
        {
            Id = guid,
            ServerId = _host.SystemId,
            Name = name,
            SortName = name,
            LocationType = LocationType.Virtual,
            MediaType = MediaType.Unknown,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = tag },
            BackdropImageTags = string.IsNullOrEmpty(backdropPath) ? Array.Empty<string>() : new[] { ImageTag(backdropPath, 0) },
            PrimaryImageAspectRatio = 2.0 / 3.0,
            CanDelete = false,
            CanDownload = false,
            UserData = UserData(guid, status)
        };
    }

    /// <summary>
    /// Builds user data for a virtual item. "Favorite" means "requested".
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="status">Media status.</param>
    /// <returns>User data.</returns>
    public static UserItemDataDto UserData(Guid itemId, int status) => new()
    {
        ItemId = itemId,
        Key = itemId.ToString("N"),
        IsFavorite = status is (int)MediaStatus.Pending or (int)MediaStatus.Processing or (int)MediaStatus.Available,
        Played = false
    };

    /// <summary>
    /// Builds an image tag that changes with status, so clients refetch posters when the badge changes.
    /// </summary>
    /// <param name="path">TMDB image path.</param>
    /// <param name="status">Media status.</param>
    /// <returns>Image tag.</returns>
    public static string ImageTag(string? path, int status)
    {
        var badges = Config.BadgePosters ? "b" : "n";
        return string.Create(CultureInfo.InvariantCulture, $"jsr{status}{badges}{(uint)(path ?? string.Empty).GetHashCode(StringComparison.Ordinal):x8}");
    }

    /// <summary>
    /// Short label for a status.
    /// </summary>
    /// <param name="status">Media status.</param>
    /// <returns>Label.</returns>
    public static string StatusText(int status) => status switch
    {
        (int)MediaStatus.Pending => "Requested – waiting for approval",
        (int)MediaStatus.Processing => "Requested – downloading",
        (int)MediaStatus.PartiallyAvailable => "Partially available – more seasons can be requested",
        (int)MediaStatus.Available => "Available",
        (int)MediaStatus.Blacklisted => "Cannot be requested",
        _ => "Not in your library – available to request"
    };

    private static string BuildOverview(string? overview, int status, bool isMovie, string? cannotRequestReason)
    {
        if (!Config.ShowHintInOverview)
        {
            return overview ?? string.Empty;
        }

        string hint;
        if (JellyseerrMapper.IsRequestable(status) || status == (int)MediaStatus.PartiallyAvailable)
        {
            hint = cannotRequestReason is not null
                ? "Jellyseerr: available to request, but " + char.ToLowerInvariant(cannotRequestReason[0]) + cannotRequestReason[1..]
                : isMovie
                    ? "Jellyseerr: not in your library yet. Add it to your favorites (♥) to request it."
                    : status == (int)MediaStatus.PartiallyAvailable
                        ? "Jellyseerr: some seasons are missing. Add the show (or a season) to your favorites (♥) to request them."
                        : "Jellyseerr: not in your library yet. Add the show to your favorites (♥) to request every season, or favorite a single season.";
        }
        else
        {
            hint = "Jellyseerr: " + StatusText(status) + ".";
        }

        return string.IsNullOrWhiteSpace(overview) ? hint : hint + "\n\n" + overview;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>
    /// A cached Jellyseerr title.
    /// </summary>
    /// <param name="Item">Title.</param>
    /// <param name="Tv">TV details, for series.</param>
    /// <param name="Detailed">Whether full details were fetched.</param>
    public record CachedTitle(SearchItemDto Item, TvDetailsDto? Tv, bool Detailed)
    {
        /// <summary>Gets the fetch time.</summary>
        public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    }
}

