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
using Jellyfin.Plugin.JellyseerrRequester.Child;
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

    // Jellyfin's role for administrators (Jellyfin.Data.Enums.UserRoles / Jellyfin.Api.Constants.UserRoles).
    private const string AdministratorRole = "Administrator";

    // Jellyseerr returns 20 search results per page, movies, shows and people mixed.
    private const int MaxSearchPages = 5;

    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TitleCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DiscoverCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DownloadsCacheDuration = TimeSpan.FromSeconds(45);

    // Age ratings hardly ever change; keep them long so child searches stay fast.
    private static readonly TimeSpan AgeRatingCacheDuration = TimeSpan.FromHours(12);

    private readonly JellyseerrClient _client;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _libraryManager;
    private readonly ChildTagService _childTags;
    private readonly ILogger<RequesterService> _logger;

    private readonly ConcurrentDictionary<string, (DateTime Expiry, Task<List<SearchItemDto>> Task)> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<VirtualId, CachedTitle> _titles = new();
    private readonly ConcurrentDictionary<VirtualId, (DateTime Expiry, Task<int?> Age)> _ageRatings = new();
    private readonly SemaphoreSlim _ratingLookups = new(6, 6);
    private readonly ConcurrentDictionary<string, (DateTime Expiry, Task<DiscoverResponseDto> Task)> _discoverCache = new();
    private readonly ConcurrentDictionary<string, (DateTime Expiry, Task<DiscoverResponseDto> Task)> _downloadsCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RequesterService"/> class.
    /// </summary>
    /// <param name="client">Jellyseerr client.</param>
    /// <param name="host">Server application host.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="childTags">Child mode Jellyfin tagging.</param>
    /// <param name="logger">Logger.</param>
    public RequesterService(JellyseerrClient client, IServerApplicationHost host, ILibraryManager libraryManager, ChildTagService childTags, ILogger<RequesterService> logger)
    {
        _client = client;
        _host = host;
        _libraryManager = libraryManager;
        _childTags = childTags;
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
        var first = await _client.SearchAsync(query, 1, cts.Token).ConfigureAwait(false);

        // Enough pages for the configured number of movies and of shows; later pages are fetched together.
        var wanted = Math.Clamp(((Math.Clamp(Config.MaxResults, 1, 100) + 9) / 10) + 1, 1, MaxSearchPages);
        var totalPages = first?["totalPages"]?.GetValue<int>() ?? 1;
        var pages = new List<System.Text.Json.Nodes.JsonNode?> { first };
        if (totalPages > 1 && wanted > 1)
        {
            var more = Enumerable.Range(2, Math.Min(wanted, totalPages) - 1).Select(async page =>
            {
                try
                {
                    return await _client.SearchAsync(query, page, cts.Token).ConfigureAwait(false);
                }
                catch (JellyseerrException ex)
                {
                    _logger.LogDebug("Jellyseerr Requester: search page {Page} failed: {Message}", page, ex.Message);
                    return null;
                }
            });
            pages.AddRange(await Task.WhenAll(more).ConfigureAwait(false));
        }

        var hideAvailable = Config.HideAvailable;
        var results = new List<SearchItemDto>();
        var seen = new HashSet<VirtualId>();
        foreach (var node in pages.SelectMany(p => p?["results"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()))
        {
            var item = JellyseerrMapper.ToSearchItem(node);
            if (item is null || item.Id <= 0
                || !seen.Add(ToVirtualId(item))
                || item.Status == (int)MediaStatus.Blacklisted
                || (hideAvailable && item.Status == (int)MediaStatus.Available)
                || FindInLibrary(item) is not null)
            {
                continue;
            }

            results.Add(item);
            CacheListedTitle(item);
        }

        return results;
    }

    private void CacheListedTitle(SearchItemDto item)
    {
        _titles.AddOrUpdate(
            ToVirtualId(item),
            _ => new CachedTitle(item, null, false),
            (_, existing) => existing with { Item = MergeSearch(existing.Item, item) });
    }

    private static VirtualId ToVirtualId(SearchItemDto item) =>
        new(item.MediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, item.Id);

    /// <summary>
    /// Finds the title in the Jellyfin library by TMDB id (Jellyseerr's availability sync can lag behind).
    /// </summary>
    private Guid? FindInLibrary(SearchItemDto item)
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
            var found = _libraryManager.GetItemList(query);
            return found.Count > 0 ? found[0].Id : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jellyseerr Requester: library lookup failed for TMDB {Id}", item.Id);
            return null;
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

    // ------------------------------------------------------------------ discover

    /// <summary>
    /// Gets a home page row from Jellyseerr's discover lists. Cached for a few minutes: these lists change slowly.
    /// </summary>
    /// <param name="list">"trending", "movies" (popular movies) or "tv" (popular shows).</param>
    /// <returns>The row.</returns>
    public Task<DiscoverResponseDto> DiscoverAsync(string list)
    {
        if (list is not ("trending" or "movies" or "tv"))
        {
            throw new JellyseerrException(StatusCodes.Status400BadRequest, "Unknown list.");
        }

        var now = DateTime.UtcNow;
        if (_discoverCache.TryGetValue(list, out var cached) && cached.Expiry > now && !cached.Task.IsFaulted)
        {
            return cached.Task;
        }

        var task = DiscoverUncachedAsync(list);
        _discoverCache[list] = (now + DiscoverCacheDuration, task);
        return task;
    }

    private async Task<DiscoverResponseDto> DiscoverUncachedAsync(string list)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var max = Math.Clamp(Config.DiscoverItemCount, 1, 60);
        var hideInLibrary = Config.DiscoverHideInLibrary;
        var forcedType = list switch { "movies" => "movie", "tv" => "tv", _ => null };
        var dto = new DiscoverResponseDto { List = list };
        var seen = new HashSet<VirtualId>();

        // 20 titles per page; read a few pages when titles are left out.
        for (var page = 1; page <= 3 && dto.Results.Count < max; page++)
        {
            var raw = await _client.DiscoverAsync(list, page, cts.Token).ConfigureAwait(false);
            foreach (var node in raw?["results"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                // Trending also lists people; ToSearchItem skips them.
                var item = JellyseerrMapper.ToSearchItem(node, forcedType);
                if (item is null || item.Id <= 0 || item.Status == (int)MediaStatus.Blacklisted || !seen.Add(ToVirtualId(item)))
                {
                    continue;
                }

                var libraryId = FindInLibrary(item);
                if (libraryId is not null && hideInLibrary)
                {
                    continue;
                }

                CacheListedTitle(item);
                dto.Results.Add(new DiscoverItemDto
                {
                    ItemId = (libraryId ?? ToVirtualId(item).ToGuid()).ToString("N"),
                    InLibrary = libraryId is not null,
                    TmdbId = item.Id,
                    MediaType = item.MediaType,
                    Title = item.Title,
                    Year = item.Year,
                    Status = item.Status,
                    ImageTag = libraryId is null ? ImageTag(item.PosterPath, BadgeStatus(item, null)) : null,
                    PosterPath = item.PosterPath
                });
                if (dto.Results.Count >= max)
                {
                    break;
                }
            }

            if (page >= (raw?["totalPages"]?.GetValue<int>() ?? 1))
            {
                break;
            }
        }

        return dto;
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
            SetAgeRating(item, raw, true);
            title = new CachedTitle(item, null, true);
        }
        else
        {
            var raw = await _client.GetTvAsync(id.TmdbId, cancellationToken).ConfigureAwait(false);
            var item = JellyseerrMapper.ToSearchItem(raw, "tv") ?? throw new JellyseerrException(404, "Series not found.");
            var tv = JellyseerrMapper.BuildTvDetails(id.TmdbId, raw);
            SetAgeRating(item, raw, false);

            // What's awaited is the latest season being downloaded, not the show's first episode.
            var awaited = tv.Seasons.Where(s => s.Status == (int)MediaStatus.Processing).Select(s => s.AirDate).LastOrDefault(d => d is not null);
            item.Download = DownloadAnalyzer.Analyze(raw?["mediaInfo"], false, awaited ?? item.ReleaseDate);
            title = new CachedTitle(item, tv, true);
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

    private void SetAgeRating(SearchItemDto item, System.Text.Json.Nodes.JsonNode? raw, bool isMovie)
    {
        var rating = AgeRating.Resolve(raw, isMovie, Config.RatingCountry);
        item.AgeRating = rating?.Label;
        item.MinimumAge = rating?.MinimumAge;
        _ageRatings[new VirtualId(isMovie ? VirtualKind.Movie : VirtualKind.Series, item.Id)] = (DateTime.UtcNow + AgeRatingCacheDuration, Task.FromResult(item.MinimumAge));
    }

    // ------------------------------------------------------------------ child mode

    /// <summary>
    /// The age a user may request up to, when the administrator put them in child mode.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <returns>Maximum age, or null when the user isn't in child mode.</returns>
    public static int? ChildMaxAgeFor(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId) ? Config.ChildMaxAgeFor(userId) : null;

    /// <summary>
    /// For a user in child mode, keeps only the titles rated for their age. Search results carry no age rating,
    /// so each title's details are looked up (a few at a time, cached for hours). Titles whose rating isn't known
    /// within <paramref name="budget"/> are left out; the lookups carry on, so the next search has them.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <param name="items">Titles to filter.</param>
    /// <param name="budget">How long to wait for age ratings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The titles the user may see, in the same order; <paramref name="items"/> itself for other users.</returns>
    public Task<List<SearchItemDto>> FilterForChildAsync(ClaimsPrincipal principal, List<SearchItemDto> items, TimeSpan budget, CancellationToken cancellationToken)
        => FilterForChildAsync(principal, items, i => ToVirtualId(i.MediaType, i.Id), budget, cancellationToken);

    /// <summary>
    /// Gets a home page row for the current user: in child mode, without the titles above the child's age.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <param name="list">"trending", "movies" or "tv".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row.</returns>
    public async Task<DiscoverResponseDto> DiscoverAsync(ClaimsPrincipal principal, string list, CancellationToken cancellationToken)
    {
        var row = await DiscoverAsync(list).ConfigureAwait(false);
        if (ChildMaxAgeFor(principal) is null)
        {
            return row;
        }

        // Rows load in the background on the home page, so they can wait longer than search.
        var kept = await FilterForChildAsync(principal, row.Results, i => ToVirtualId(i.MediaType, i.TmdbId), TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        var filtered = new DiscoverResponseDto { List = row.List };
        filtered.Results.AddRange(kept);
        return filtered;
    }

    private static VirtualId ToVirtualId(string mediaType, int tmdbId) => new(mediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, tmdbId);

    private async Task<List<T>> FilterForChildAsync<T>(ClaimsPrincipal principal, List<T> items, Func<T, VirtualId> idOf, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (ChildMaxAgeFor(principal) is not { } maxAge || items.Count == 0)
        {
            return items;
        }

        var lookups = items.Select(i => (Item: i, Age: MinimumAgeAsync(idOf(i)))).ToList();
        var all = Task.WhenAll(lookups.Select(l => l.Age));
        try
        {
            await Task.WhenAny(all, Task.Delay(budget, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client gave up; filter with what's known.
        }

        var kept = lookups.Where(l => l.Age.IsCompletedSuccessfully && IsAllowedForAge(l.Age.Result, maxAge)).Select(l => l.Item).ToList();
        if (kept.Count < items.Count)
        {
            _logger.LogDebug("Jellyseerr Requester: hid {Count} titles from a child account", items.Count - kept.Count);
        }

        return kept;
    }

    private static bool IsAllowedForAge(int? minimumAge, int maxAge)
        => minimumAge is { } age ? age <= maxAge : !Config.ChildBlockUnrated;

    private Task<int?> MinimumAgeAsync(VirtualId id)
    {
        if (_ageRatings.TryGetValue(id, out var cached) && cached.Expiry > DateTime.UtcNow && !cached.Age.IsFaulted)
        {
            return cached.Age;
        }

        var lookup = FetchMinimumAgeAsync(id);
        _ageRatings[id] = (DateTime.UtcNow + AgeRatingCacheDuration, lookup);
        return lookup;
    }

    private async Task<int?> FetchMinimumAgeAsync(VirtualId id)
    {
        await _ratingLookups.WaitAsync().ConfigureAwait(false);
        try
        {
            // Not tied to the search request: a lookup that finishes late still fills the cache.
            return (await GetTitleAsync(id, CancellationToken.None).ConfigureAwait(false)).Item.MinimumAge;
        }
        finally
        {
            _ratingLookups.Release();
        }
    }

    /// <summary>
    /// Why a child account may not request a title, or null when it may (or the user isn't in child mode).
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <param name="item">Title, with details loaded (see <see cref="GetTitleAsync"/>).</param>
    /// <returns>Reason or null.</returns>
    public static string? ChildBlockReason(ClaimsPrincipal principal, SearchItemDto item)
    {
        if (ChildMaxAgeFor(principal) is not { } maxAge)
        {
            return null;
        }

        if (item.MinimumAge is not { } age)
        {
            return Config.ChildBlockUnrated
                ? "This title has no age rating, so it can't be requested from a child account. Ask a parent."
                : null;
        }

        return age > maxAge
            ? string.Format(CultureInfo.InvariantCulture, "This title is rated {0} (ages {1}+). Your account can request titles for ages {2} and under. Ask a parent.", item.AgeRating, age, maxAge)
            : null;
    }

    // ------------------------------------------------------------------ users & requests

    /// <summary>
    /// Whether the administrator turned the plugin off for this Jellyfin user.
    /// Unauthenticated calls (e.g. poster images) are not considered disabled.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    /// <returns>True when the plugin is off for the user.</returns>
    public static bool IsDisabledFor(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId) && Config.IsDisabledFor(userId);

    /// <summary>
    /// Throws when the administrator turned the plugin off for this Jellyfin user.
    /// </summary>
    /// <param name="principal">Current Jellyfin user.</param>
    public static void EnsureEnabledFor(ClaimsPrincipal principal)
    {
        if (IsDisabledFor(principal))
        {
            throw new JellyseerrException(StatusCodes.Status403Forbidden, DisabledMessage);
        }
    }

    /// <summary>
    /// Message shown to users the plugin is turned off for.
    /// </summary>
    public const string DisabledMessage = "Requesting is turned off for your account. Ask your administrator.";

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

        if (Config.IsDisabledFor(userId))
        {
            throw new JellyseerrException(StatusCodes.Status403Forbidden, DisabledMessage);
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
        var isChild = ChildMaxAgeFor(principal) is not null;
        if (isChild)
        {
            var title = await GetTitleAsync(new VirtualId(body.MediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, body.TmdbId), cancellationToken).ConfigureAwait(false);
            if (ChildBlockReason(principal, title.Item) is { } reason)
            {
                throw new JellyseerrException(StatusCodes.Status403Forbidden, reason);
            }
        }

        var result = isChild
            ? await CreateChildRequestAsync(body, seasons, user?.Id, cancellationToken).ConfigureAwait(false)
            : await _client.CreateRequestAsync(body.MediaType, body.TmdbId, seasons, body.Is4K, user?.Id, cancellationToken).ConfigureAwait(false);
        var approved = result?["status"]?.GetValue<int>() == 2;
        if (isChild)
        {
            _childTags.Track(body.MediaType, body.TmdbId, PluginConfiguration.SplitTags(Config.ChildJellyfinTags));
        }

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
    /// Creates a child's request with the child mode Radarr/Sonarr tags. If Jellyseerr won't take the tags
    /// (unknown tags, no server, or not allowed for this account), the request goes through without them.
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> CreateChildRequestAsync(CreateRequestDto body, List<int>? seasons, int? jellyseerrUserId, CancellationToken cancellationToken)
    {
        var labels = PluginConfiguration.SplitTags(Config.ChildArrTags);
        List<int>? tagIds = null;
        if (labels.Length > 0)
        {
            try
            {
                var (ids, missing) = await _client.ResolveArrTagsAsync(body.MediaType, body.Is4K, labels, cancellationToken).ConfigureAwait(false);
                if (missing.Count > 0)
                {
                    _logger.LogWarning("Jellyseerr Requester: child mode tags not found in {Service}: {Tags}. Create them there first.", body.MediaType == "movie" ? "Radarr" : "Sonarr", string.Join(", ", missing));
                }

                tagIds = ids.Count > 0 ? ids : null;
            }
            catch (JellyseerrException ex)
            {
                _logger.LogWarning("Jellyseerr Requester: could not read Radarr/Sonarr tags from Jellyseerr: {Message}", ex.Message);
            }
        }

        if (tagIds is null)
        {
            return await _client.CreateRequestAsync(body.MediaType, body.TmdbId, seasons, body.Is4K, jellyseerrUserId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _client.CreateRequestAsync(body.MediaType, body.TmdbId, seasons, body.Is4K, jellyseerrUserId, cancellationToken, tagIds).ConfigureAwait(false);
        }
        catch (JellyseerrException ex) when (ex.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status403Forbidden)
        {
            _logger.LogWarning("Jellyseerr Requester: Jellyseerr refused the child mode Radarr/Sonarr tags ({Message}); requesting without them", ex.Message);
            return await _client.CreateRequestAsync(body.MediaType, body.TmdbId, seasons, body.Is4K, jellyseerrUserId, cancellationToken).ConfigureAwait(false);
        }
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

    // ------------------------------------------------------------------ downloads

    /// <summary>
    /// Resolves who acts in Jellyseerr for download management. Jellyfin administrators act as the API key owner;
    /// other users as their linked Jellyseerr account, with that account's permissions.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Jellyseerr user id to act as (null for the API key owner) and whether they may remove downloads.</returns>
    public async Task<(int? UserId, bool CanManage)> ResolveActorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        EnsureEnabledFor(principal);
        if (principal.IsInRole(AdministratorRole))
        {
            return (null, true);
        }

        if (!Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var userId))
        {
            throw new JellyseerrException(StatusCodes.Status403Forbidden, "Could not identify the Jellyfin user.");
        }

        var linked = await _client.FindLinkedUserAsync(userId, principal.Identity?.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new JellyseerrException(StatusCodes.Status403Forbidden, "Your Jellyfin account is not linked to a Jellyseerr account.");
        return (linked.Id, linked.CanManageRequests);
    }

    /// <summary>
    /// Whether the user may remove downloads.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when allowed.</returns>
    public async Task<bool> CanRemoveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        try
        {
            return (await ResolveActorAsync(principal, cancellationToken).ConfigureAwait(false)).CanManage;
        }
        catch (JellyseerrException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a title that is being downloaded, so it stops and can be requested again.
    /// A title with nothing available yet is removed from Radarr/Sonarr (with its partial files) and cleared in
    /// Jellyseerr. A show with some seasons already available only has its open requests cancelled, so its files stay.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message.</returns>
    public async Task<MessageDto> RemoveAsync(ClaimsPrincipal principal, string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        if (mediaType is not ("movie" or "tv") || tmdbId <= 0)
        {
            throw new JellyseerrException(StatusCodes.Status400BadRequest, "Invalid media type or id.");
        }

        var (actAs, canManage) = await ResolveActorAsync(principal, cancellationToken).ConfigureAwait(false);
        if (!canManage)
        {
            throw new JellyseerrException(StatusCodes.Status403Forbidden, "Removing downloads needs the Manage Requests permission in Jellyseerr. Ask your administrator.");
        }

        var id = new VirtualId(mediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, tmdbId);
        _titles.TryRemove(id, out _);
        var title = await GetTitleAsync(id, cancellationToken).ConfigureAwait(false);
        var item = title.Item;
        if (item.MediaId is not { } mediaId)
        {
            throw new JellyseerrException(StatusCodes.Status404NotFound, "Nothing to remove: this title hasn't been requested.");
        }

        if (item.Status == (int)MediaStatus.Available)
        {
            throw new JellyseerrException(StatusCodes.Status409Conflict, "This title is already available, there's no download to remove.");
        }

        string message;
        if (item.Status == (int)MediaStatus.PartiallyAvailable)
        {
            // Keep the seasons that are already there: only cancel what's still open.
            if (item.OpenRequestIds.Count == 0)
            {
                throw new JellyseerrException(StatusCodes.Status409Conflict, "There's no open request to cancel for this show.");
            }

            foreach (var requestId in item.OpenRequestIds)
            {
                await _client.DeleteRequestAsync(requestId, actAs, cancellationToken).ConfigureAwait(false);
            }

            message = "Cancelled " + item.OpenRequestIds.Count + (item.OpenRequestIds.Count == 1 ? " request" : " requests") + ". Seasons already available are kept.";
        }
        else
        {
            var arr = mediaType == "movie" ? "Radarr" : "Sonarr";
            var removedFromArr = false;
            try
            {
                await _client.DeleteMediaFileAsync(mediaId, false, actAs, cancellationToken).ConfigureAwait(false);
                removedFromArr = true;
            }
            catch (JellyseerrException ex) when (ex.StatusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden))
            {
                // Older Jellyseerr versions, or titles that never reached Radarr/Sonarr: clearing Jellyseerr still unblocks it.
                _logger.LogWarning("Jellyseerr Requester: could not remove {Type} {TmdbId} from {Arr}: {Message}", mediaType, tmdbId, arr, ex.Message);
            }

            await _client.DeleteMediaAsync(mediaId, actAs, cancellationToken).ConfigureAwait(false);
            message = removedFromArr
                ? "Removed from " + arr + " and " + JellyseerrClient.ServerName + ". It can be requested again."
                : "Removed from " + JellyseerrClient.ServerName + ". It can be requested again; check " + arr + " if it's still there.";
        }

        _logger.LogInformation("{User} removed the download of {Type} {TmdbId} ({Title})", principal.Identity?.Name, mediaType, tmdbId, item.Title);
        InvalidateTitle(id);
        return new MessageDto { Message = message };
    }

    /// <summary>
    /// Lists titles being downloaded (approved requests that aren't available yet), stalled ones first.
    /// Users without the Manage Requests or View Requests permission in Jellyseerr only see their own.
    /// </summary>
    /// <param name="principal">Current user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Downloads row.</returns>
    public async Task<DiscoverResponseDto> DownloadsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var (actAs, _) = await ResolveActorAsync(principal, cancellationToken).ConfigureAwait(false);
        var key = actAs?.ToString(CultureInfo.InvariantCulture) ?? "owner";
        var now = DateTime.UtcNow;
        if (_downloadsCache.TryGetValue(key, out var cached) && cached.Expiry > now && !cached.Task.IsFaulted)
        {
            return await cached.Task.ConfigureAwait(false);
        }

        var task = DownloadsUncachedAsync(actAs);
        _downloadsCache[key] = (now + DownloadsCacheDuration, task);
        return await task.ConfigureAwait(false);
    }

    private async Task<DiscoverResponseDto> DownloadsUncachedAsync(int? actAs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var raw = await _client.GetRequestsAsync("processing", 100, actAs, cts.Token).ConfigureAwait(false);

        // One entry per title and quality: a show can have several requests.
        var requests = (raw?["results"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            .Where(r => r?["media"]?["tmdbId"] is not null && r["status"]?.GetValue<int>() == 2)
            .GroupBy(r => (r!["media"]!["id"]?.GetValue<int>() ?? 0, r["is4k"]?.GetValue<bool>() == true))
            .Select(g => g.First()!)
            .Take(40)
            .ToList();

        using var gate = new SemaphoreSlim(6);
        var entries = await Task.WhenAll(requests.Select(async r =>
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                return await ToDownloadItemAsync(r, cts.Token).ConfigureAwait(false);
            }
            catch (JellyseerrException ex)
            {
                _logger.LogDebug("Jellyseerr Requester: skipping a download: {Message}", ex.Message);
                return null;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var dto = new DiscoverResponseDto { List = "downloads" };
        dto.Results.AddRange(entries.OfType<DiscoverItemDto>()
            .OrderByDescending(e => e.Download!.Stale)
            .ThenBy(e => e.Download!.Since ?? DateTime.MaxValue));
        return dto;
    }

    private async Task<DiscoverItemDto?> ToDownloadItemAsync(System.Text.Json.Nodes.JsonNode request, CancellationToken cancellationToken)
    {
        var media = request["media"]!;
        var mediaType = media["mediaType"]?.GetValue<string>();
        var tmdbId = media["tmdbId"]?.GetValue<int>() ?? 0;
        if (mediaType is not ("movie" or "tv") || tmdbId <= 0)
        {
            return null;
        }

        var is4K = request["is4k"]?.GetValue<bool>() == true;
        var id = new VirtualId(mediaType == "movie" ? VirtualKind.Movie : VirtualKind.Series, tmdbId);
        var title = await GetTitleAsync(id, cancellationToken).ConfigureAwait(false);
        var item = title.Item;

        var awaited = title.Tv?.Seasons
            .Where(s => (is4K ? s.Status4K : s.Status) == (int)MediaStatus.Processing)
            .Select(s => s.AirDate)
            .LastOrDefault(d => d is not null);
        var since = ParseDate(request["updatedAt"]?.GetValue<string>()) ?? ParseDate(request["createdAt"]?.GetValue<string>());
        var download = is4K
            ? DownloadAnalyzer.Analyze(media, true, awaited ?? item.ReleaseDate, since)
            : item.Download ?? DownloadAnalyzer.Analyze(media, false, awaited ?? item.ReleaseDate, since);
        if (download is null)
        {
            return null;
        }

        var libraryId = FindInLibrary(item);
        var requestedBy = request["requestedBy"];
        return new DiscoverItemDto
        {
            ItemId = (libraryId ?? id.ToGuid()).ToString("N"),
            InLibrary = libraryId is not null,
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = item.Title + (is4K ? " (4K)" : string.Empty),
            Year = item.Year,
            Status = is4K ? item.Status4K : item.Status,
            PosterPath = item.PosterPath,
            Download = download,
            RequestedBy = requestedBy?["displayName"]?.GetValue<string>() ?? requestedBy?["username"]?.GetValue<string>() ?? requestedBy?["jellyfinUsername"]?.GetValue<string>()
        };
    }

    private void InvalidateTitle(VirtualId id)
    {
        _titles.TryRemove(id, out _);
        _searchCache.Clear();
        _discoverCache.Clear();
        _downloadsCache.Clear();
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
        _searchCache.Clear();
        _discoverCache.Clear();
        _downloadsCache.Clear();
    }

    // ------------------------------------------------------------------ Jellyfin DTOs

    /// <summary>
    /// Builds the Jellyfin item for a movie or series.
    /// </summary>
    /// <param name="item">Jellyseerr item.</param>
    /// <param name="tv">TV details, if loaded.</param>
    /// <param name="cannotRequestReason">Why the current user cannot request, if applicable.</param>
    /// <param name="canRemove">Whether the current user may remove the download.</param>
    /// <returns>The item DTO.</returns>
    public BaseItemDto ToDto(SearchItemDto item, TvDetailsDto? tv, string? cannotRequestReason, bool canRemove = false)
    {
        var isMovie = item.MediaType == "movie";
        var id = new VirtualId(isMovie ? VirtualKind.Movie : VirtualKind.Series, item.Id);
        var status = EffectiveStatus(item, tv);

        var dto = BaseDto(id, item.Title, status, BadgeStatus(item, tv), item.PosterPath, item.BackdropPath);
        dto.Type = isMovie ? BaseItemKind.Movie : BaseItemKind.Series;
        dto.IsFolder = !isMovie;
        dto.ProductionYear = int.TryParse(item.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null;
        dto.PremiereDate = ParseDate(item.ReleaseDate);
        dto.CommunityRating = item.Rating is > 0 ? (float)Math.Round(item.Rating.Value, 1) : null;
        dto.Overview = BuildOverview(item.Overview, status, isMovie, cannotRequestReason, item.Download, canRemove);
        dto.Taglines = new[] { StatusText(status, item.Download) };
        dto.Genres = item.Genres ?? Array.Empty<string>();
        dto.RunTimeTicks = item.RuntimeMinutes is > 0 ? TimeSpan.FromMinutes(item.RuntimeMinutes.Value).Ticks : null;
        dto.ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = item.Id.ToString(CultureInfo.InvariantCulture) };
        dto.OfficialRating = item.AgeRating;
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
        var dto = BaseDto(id, season.Name, season.Status, season.Status, season.PosterPath ?? series.PosterPath, series.BackdropPath);
        dto.Type = BaseItemKind.Season;
        dto.IsFolder = true;
        dto.IndexNumber = season.SeasonNumber;
        dto.SeriesId = seriesId.ToGuid();
        dto.SeriesName = series.Title;
        dto.ParentId = seriesId.ToGuid();
        dto.SeriesPrimaryImageTag = ImageTag(series.PosterPath, BadgeStatus(series, null));
        dto.PremiereDate = ParseDate(season.AirDate);
        dto.ProductionYear = dto.PremiereDate?.Year;
        dto.ChildCount = season.EpisodeCount;
        dto.Overview = BuildOverview(
            (season.EpisodeCount > 0 ? season.EpisodeCount + " episodes. " : string.Empty) + season.Overview,
            season.Status,
            false,
            cannotRequestReason,
            null,
            false);
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

    /// <summary>
    /// Status drawn on the poster: like <see cref="EffectiveStatus"/>, but a stalled download gets its own badge.
    /// </summary>
    /// <param name="item">Title.</param>
    /// <param name="tv">TV details, if loaded.</param>
    /// <returns>Media status, or <see cref="PosterRenderer.StalledBadge"/>.</returns>
    public static int BadgeStatus(SearchItemDto item, TvDetailsDto? tv) =>
        item.Download?.Stale == true ? PosterRenderer.StalledBadge : EffectiveStatus(item, tv);

    private BaseItemDto BaseDto(VirtualId id, string name, int status, int badge, string? posterPath, string? backdropPath)
    {
        var guid = id.ToGuid();
        var tag = ImageTag(posterPath, badge);
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
    /// <param name="download">Download progress, if any.</param>
    /// <returns>Label.</returns>
    public static string StatusText(int status, DownloadDto? download = null) => download is not null
        ? (download.Stale ? "Download stalled – " : "Requested – ") + download.Summary.TrimEnd('.')
        : status switch
    {
        (int)MediaStatus.Pending => "Requested – waiting for approval",
        (int)MediaStatus.Processing => "Requested – downloading",
        (int)MediaStatus.PartiallyAvailable => "Partially available – more seasons can be requested",
        (int)MediaStatus.Available => "Available",
        (int)MediaStatus.Blacklisted => "Cannot be requested",
        _ => "Not in your library – available to request"
    };

    private static string BuildOverview(string? overview, int status, bool isMovie, string? cannotRequestReason, DownloadDto? download, bool canRemove)
    {
        if (!Config.ShowHintInOverview)
        {
            return overview ?? string.Empty;
        }

        string hint;
        if (download is { Stale: true })
        {
            hint = "Jellyseerr: the download looks stuck. " + download.Summary
                + (canRemove
                    ? " Remove it from your favorites (♥) to cancel it, so it can be requested again."
                    : " An administrator can remove it so it can be requested again.");
        }
        else if (download is not null && status != (int)MediaStatus.PartiallyAvailable)
        {
            hint = "Jellyseerr: requested. " + download.Summary;
        }
        else if (JellyseerrMapper.IsRequestable(status) || status == (int)MediaStatus.PartiallyAvailable)
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

