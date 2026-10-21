using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyseerrRequester.Api;
using Jellyfin.Plugin.JellyseerrRequester.Configuration;
using Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyseerrRequester.Virtual;

/// <summary>
/// Hooks Jellyfin's own API so Jellyseerr titles behave like (virtual) library items in every client:
/// <list type="bullet">
/// <item>search results (<c>/Items?searchTerm=</c>, <c>/Search/Hints</c>) get Jellyseerr movies and shows appended;</item>
/// <item>requests for a virtual item id (details, images, seasons, user data…) are answered by the plugin;</item>
/// <item>marking a virtual item as favorite creates the Jellyseerr request.</item>
/// </list>
/// </summary>
public class VirtualItemFilter : IAsyncActionFilter
{
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(6);

    private readonly RequesterService _service;
    private readonly PosterRenderer _posters;
    private readonly ILogger<VirtualItemFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualItemFilter"/> class.
    /// </summary>
    /// <param name="service">Requester service.</param>
    /// <param name="posters">Poster renderer.</param>
    /// <param name="logger">Logger.</param>
    public VirtualItemFilter(RequesterService service, PosterRenderer posters, ILogger<VirtualItemFilter> logger)
    {
        _service = service;
        _posters = posters;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerTypeInfo.Assembly == typeof(Plugin).Assembly
            || !JellyseerrClient.IsConfigured)
        {
            await next().ConfigureAwait(false);
            return;
        }

        try
        {
            // 1. Requests about a virtual item are answered here; Jellyfin doesn't know these ids.
            if (FindVirtualArgument(context) is { } virtualId)
            {
                context.Result = await HandleVirtualAsync(context, action, virtualId).ConfigureAwait(false);
                return;
            }

            // 2. /Items?ids=a,b,c may mix real and virtual ids.
            if (IsItemsQuery(action) && context.ActionArguments.TryGetValue("ids", out var idsArg) && idsArg is Guid[] ids && ids.Length > 0)
            {
                var virtualIds = ids.Select(i => VirtualId.TryParse(i, out var v) ? v : (VirtualId?)null).OfType<VirtualId>().ToList();
                if (virtualIds.Count > 0)
                {
                    await HandleMixedIdsAsync(context, next, ids, virtualIds).ConfigureAwait(false);
                    return;
                }
            }

            // 3. Search: append Jellyseerr titles to the results.
            if (Config.InjectIntoSearch && SearchTerm(context) is { } term)
            {
                if (IsItemsQuery(action))
                {
                    await InjectIntoItemsSearchAsync(context, next, term).ConfigureAwait(false);
                    return;
                }

                if (action.ControllerName == "Search" && action.ActionName == "GetSearchHints")
                {
                    await InjectIntoSearchHintsAsync(context, next, term).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (JellyseerrException ex)
        {
            var status = ex.StatusCode == StatusCodes.Status401Unauthorized ? StatusCodes.Status502BadGateway : ex.StatusCode;
            context.Result = new ObjectResult(new { message = ex.Message }) { StatusCode = status };
            return;
        }

        await next().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ search

    private async Task InjectIntoItemsSearchAsync(ActionExecutingContext context, ActionExecutionDelegate next, string term)
    {
        var (movies, series) = WantedTypes(context, emptyMeansAll: false);
        if ((!movies && !series) || StartIndex(context) > 0 || HasVirtualParent(context))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var search = _service.SearchAsync(term);
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> result } objectResult)
        {
            return;
        }

        var extra = await GetSearchItemsAsync(search, movies, series, result.Items, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (extra.Count == 0)
        {
            return;
        }

        var items = result.Items.Concat(extra.Select(i => _service.ToDto(i, null, null))).ToList();
        objectResult.Value = new QueryResult<BaseItemDto>(result.StartIndex, result.TotalRecordCount + extra.Count, items);
    }

    private async Task InjectIntoSearchHintsAsync(ActionExecutingContext context, ActionExecutionDelegate next, string term)
    {
        var (movies, series) = WantedTypes(context, emptyMeansAll: true);
        if ((!movies && !series) || StartIndex(context) > 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var search = _service.SearchAsync(term);
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is not ObjectResult { Value: SearchHintResult result } objectResult)
        {
            return;
        }

        var existing = result.SearchHints.Select(h => new BaseItemDto { Name = h.Name, ProductionYear = h.ProductionYear, Type = h.Type }).ToList();
        var extra = await GetSearchItemsAsync(search, movies, series, existing, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (extra.Count == 0)
        {
            return;
        }

        var hints = result.SearchHints.Concat(extra.Select(i =>
        {
            var dto = _service.ToDto(i, null, null);
            return new SearchHint
            {
                Id = dto.Id,
                ItemId = dto.Id,
                Name = dto.Name,
                MatchedTerm = term,
                ProductionYear = dto.ProductionYear,
                PrimaryImageTag = dto.ImageTags[ImageType.Primary],
                PrimaryImageAspectRatio = dto.PrimaryImageAspectRatio,
                Type = dto.Type,
                IsFolder = dto.IsFolder,
                MediaType = MediaType.Unknown
            };
        })).ToList();
        objectResult.Value = new SearchHintResult(hints, result.TotalRecordCount + extra.Count);
    }

    private async Task<List<SearchItemDto>> GetSearchItemsAsync(Task<List<SearchItemDto>> search, bool movies, bool series, IReadOnlyList<BaseItemDto> libraryItems, CancellationToken cancellationToken)
    {
        // Don't hold up Jellyfin's own results for long if Jellyseerr is slow.
        var finished = await Task.WhenAny(search, Task.Delay(SearchWait, cancellationToken)).ConfigureAwait(false);
        if (finished != search)
        {
            _logger.LogWarning("Jellyseerr Requester: Jellyseerr search took longer than {Seconds}s, results skipped", SearchWait.TotalSeconds);
            return new List<SearchItemDto>();
        }

        if (search.IsFaulted)
        {
            _logger.LogWarning(search.Exception?.GetBaseException(), "Jellyseerr Requester: Jellyseerr search failed");
            return new List<SearchItemDto>();
        }

        // Library items are already filtered out by TMDB id; also skip same title + year as a local result
        // (covers items without a TMDB id).
        bool InLibrary(SearchItemDto item) => libraryItems.Any(l =>
            (l.ProviderIds is not null && l.ProviderIds.TryGetValue("Tmdb", out var tmdb) && tmdb == item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            || (NormalizeTitle(l.Name) == NormalizeTitle(item.Title)
                && (l.ProductionYear is null || item.Year is null || l.ProductionYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == item.Year)));

        var max = Math.Clamp(Config.MaxResults, 1, 50);
        var results = search.Result;
        return results.Where(i => i.MediaType == "movie" && movies && !InLibrary(i)).Take(max)
            .Concat(results.Where(i => i.MediaType == "tv" && series && !InLibrary(i)).Take(max))
            .ToList();
    }

    private static string NormalizeTitle(string? title)
    {
        // "The Matrix (1999)" / "the matrix" / "The Matrix!" all compare equal.
        var t = System.Text.RegularExpressions.Regex.Replace(title ?? string.Empty, @"\s*\(\d{4}\)\s*$", string.Empty);
        return new string(t.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static bool IsItemsQuery(ControllerActionDescriptor action) =>
        action.ControllerName == "Items" && action.ActionName is "GetItems" or "GetItemsByUserIdLegacy";

    private static string? SearchTerm(ActionExecutingContext context) =>
        context.ActionArguments.TryGetValue("searchTerm", out var value) && value is string term && term.Trim().Length >= 2 ? term.Trim() : null;

    private static int StartIndex(ActionExecutingContext context) =>
        context.ActionArguments.TryGetValue("startIndex", out var value) && value is int i ? i : 0;

    private static bool HasVirtualParent(ActionExecutingContext context) =>
        context.ActionArguments.TryGetValue("parentId", out var value) && VirtualId.TryParse(value, out _);

    private static (bool Movies, bool Series) WantedTypes(ActionExecutingContext context, bool emptyMeansAll)
    {
        var types = context.ActionArguments.TryGetValue("includeItemTypes", out var value) && value is BaseItemKind[] t ? t : Array.Empty<BaseItemKind>();
        var excluded = context.ActionArguments.TryGetValue("excludeItemTypes", out var ex) && ex is BaseItemKind[] e ? e : Array.Empty<BaseItemKind>();
        if (types.Length == 0)
        {
            return emptyMeansAll ? (!excluded.Contains(BaseItemKind.Movie), !excluded.Contains(BaseItemKind.Series)) : (false, false);
        }

        return (types.Contains(BaseItemKind.Movie), types.Contains(BaseItemKind.Series));
    }

    // ------------------------------------------------------------------ virtual items

    private static VirtualId? FindVirtualArgument(ActionExecutingContext context)
    {
        foreach (var (name, value) in context.ActionArguments)
        {
            if (name.Equals("userId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (VirtualId.TryParse(value, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private async Task<IActionResult> HandleVirtualAsync(ActionExecutingContext context, ControllerActionDescriptor action, VirtualId id)
    {
        var ct = context.HttpContext.RequestAborted;
        var user = context.HttpContext.User;
        var name = action.ControllerName + "." + action.ActionName;

        switch (name)
        {
            case "Image.GetItemImage":
            case "Image.GetItemImage2":
            case "Image.GetItemImageByIndex":
            case "Image.HeadItemImage":
            case "Image.HeadItemImage2":
            case "Image.HeadItemImageByIndex":
                return await GetImageAsync(context, id, ct).ConfigureAwait(false);

            case "UserLibrary.MarkFavoriteItem":
            case "UserLibrary.MarkFavoriteItemLegacy":
                return await FavoriteAsync(context, id, ct).ConfigureAwait(false);

            case "Items.UpdateItemUserData":
            case "Items.UpdateItemUserDataLegacy":
                if (context.ActionArguments.Values.OfType<UpdateUserItemDataDto>().FirstOrDefault()?.IsFavorite == true)
                {
                    return await FavoriteAsync(context, id, ct).ConfigureAwait(false);
                }

                return new OkObjectResult(await GetUserDataAsync(id, ct).ConfigureAwait(false));

            case "UserLibrary.UnmarkFavoriteItem":
            case "UserLibrary.UnmarkFavoriteItemLegacy":
            case "Items.GetItemUserData":
            case "Items.GetItemUserDataLegacy":
                // A request can't be cancelled from here; report the actual state.
                return new OkObjectResult(await GetUserDataAsync(id, ct).ConfigureAwait(false));

            case "UserLibrary.GetItem":
            case "UserLibrary.GetItemLegacy":
                return new OkObjectResult(await GetItemDtoAsync(id, user, ct).ConfigureAwait(false));

            case "TvShows.GetSeasons":
                return new OkObjectResult(await GetSeasonsAsync(id, user, ct).ConfigureAwait(false));

            case "Items.GetItems":
            case "Items.GetItemsByUserIdLegacy":
                // Children of a virtual series are its seasons; anything else is empty.
                if (context.ActionArguments.TryGetValue("parentId", out var parent) && VirtualId.TryParse(parent, out var parentId)
                    && parentId.Kind == VirtualKind.Series && WantsSeasons(context))
                {
                    return new OkObjectResult(await GetSeasonsAsync(parentId, user, ct).ConfigureAwait(false));
                }

                return new OkObjectResult(new QueryResult<BaseItemDto>());
        }

        return EmptyResultFor(action);
    }

    private static bool WantsSeasons(ActionExecutingContext context)
    {
        var types = context.ActionArguments.TryGetValue("includeItemTypes", out var value) && value is BaseItemKind[] t ? t : Array.Empty<BaseItemKind>();
        return types.Length == 0 || types.Contains(BaseItemKind.Season);
    }

    private async Task HandleMixedIdsAsync(ActionExecutingContext context, ActionExecutionDelegate next, Guid[] ids, List<VirtualId> virtualIds)
    {
        var ct = context.HttpContext.RequestAborted;
        var dtos = new List<BaseItemDto>();
        foreach (var id in virtualIds)
        {
            try
            {
                dtos.Add(await GetItemDtoAsync(id, context.HttpContext.User, ct).ConfigureAwait(false));
            }
            catch (JellyseerrException ex)
            {
                _logger.LogDebug("Jellyseerr Requester: skipping {Id}: {Message}", id, ex.Message);
            }
        }

        var realIds = ids.Where(i => !VirtualId.TryParse(i, out _)).ToArray();
        if (realIds.Length == 0)
        {
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(0, dtos.Count, dtos));
            return;
        }

        context.ActionArguments["ids"] = realIds;
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> result } objectResult)
        {
            objectResult.Value = new QueryResult<BaseItemDto>(result.StartIndex, result.TotalRecordCount + dtos.Count, result.Items.Concat(dtos).ToList());
        }
    }

    private async Task<BaseItemDto> GetItemDtoAsync(VirtualId id, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct)
    {
        var title = await _service.GetTitleAsync(id, ct).ConfigureAwait(false);
        var reason = await _service.GetCannotRequestReasonAsync(user, ct).ConfigureAwait(false);
        if (id.Kind == VirtualKind.Season)
        {
            var season = title.Tv?.Seasons.FirstOrDefault(s => s.SeasonNumber == id.Season)
                ?? throw new JellyseerrException(StatusCodes.Status404NotFound, "Season not found.");
            return _service.ToSeasonDto(title.Item, season, reason);
        }

        return _service.ToDto(title.Item, title.Tv, reason);
    }

    private async Task<QueryResult<BaseItemDto>> GetSeasonsAsync(VirtualId id, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct)
    {
        var title = await _service.GetTitleAsync(id, ct).ConfigureAwait(false);
        var reason = await _service.GetCannotRequestReasonAsync(user, ct).ConfigureAwait(false);
        var seasons = (title.Tv?.Seasons ?? new List<SeasonDto>()).Select(s => _service.ToSeasonDto(title.Item, s, reason)).ToList();
        return new QueryResult<BaseItemDto>(0, seasons.Count, seasons);
    }

    private async Task<UserItemDataDto> GetUserDataAsync(VirtualId id, CancellationToken ct)
    {
        var title = await _service.GetTitleAsync(id, ct).ConfigureAwait(false);
        var status = id.Kind == VirtualKind.Season
            ? title.Tv?.Seasons.FirstOrDefault(s => s.SeasonNumber == id.Season)?.Status ?? (int)MediaStatus.Unknown
            : RequesterService.EffectiveStatus(title.Item, title.Tv);
        return RequesterService.UserData(id.ToGuid(), status);
    }

    private async Task<IActionResult> FavoriteAsync(ActionExecutingContext context, VirtualId id, CancellationToken ct)
    {
        try
        {
            await _service.RequestAsync(context.HttpContext.User, id, ct).ConfigureAwait(false);
        }
        catch (JellyseerrException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            // Already requested: favoriting again is fine.
        }

        var data = await GetUserDataAsync(id, ct).ConfigureAwait(false);
        data.IsFavorite = true;
        return new OkObjectResult(data);
    }

    private async Task<IActionResult> GetImageAsync(ActionExecutingContext context, VirtualId id, CancellationToken ct)
    {
        var imageType = context.ActionArguments.TryGetValue("imageType", out var it) && it is ImageType t ? t : ImageType.Primary;
        int? width = null;
        foreach (var key in new[] { "fillWidth", "maxWidth", "width" })
        {
            if (context.ActionArguments.TryGetValue(key, out var w) && w is int value && value > 0)
            {
                width = value;
                break;
            }
        }

        var title = _service.PeekTitle(id);
        if (title is null || (id.Kind == VirtualKind.Season && title.Tv is null))
        {
            title = await _service.GetTitleAsync(id, ct).ConfigureAwait(false);
        }

        byte[]? data;
        if (imageType == ImageType.Primary)
        {
            var season = id.Kind == VirtualKind.Season ? title.Tv?.Seasons.FirstOrDefault(s => s.SeasonNumber == id.Season) : null;
            var status = season?.Status ?? RequesterService.EffectiveStatus(title.Item, title.Tv);
            data = await _posters.GetPosterAsync(
                season?.PosterPath ?? title.Item.PosterPath,
                season?.Name ?? title.Item.Title,
                Config.BadgePosters ? status : null,
                width,
                ct).ConfigureAwait(false);
        }
        else if (imageType is ImageType.Backdrop or ImageType.Thumb or ImageType.Art)
        {
            data = await _posters.GetBackdropAsync(title.Item.BackdropPath, width, ct).ConfigureAwait(false);
        }
        else
        {
            data = null;
        }

        if (data is null || data.Length == 0)
        {
            return new NotFoundResult();
        }

        context.HttpContext.Response.Headers.CacheControl = "public, max-age=3600";
        return new FileContentResult(data, "image/jpeg");
    }

    /// <summary>
    /// For any other endpoint called with a virtual id (similar items, theme media, playback info…),
    /// return an empty value of the declared type rather than an error, so clients don't break.
    /// </summary>
    private static IActionResult EmptyResultFor(ControllerActionDescriptor action)
    {
        var type = action.MethodInfo.ReturnType;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            type = type.GetGenericArguments()[0];
        }
        else
        {
            return new NotFoundResult();
        }

        if (type.IsArray)
        {
            return new OkObjectResult(Array.CreateInstance(type.GetElementType()!, 0));
        }

        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.GetGenericArguments()[0];
            return new OkObjectResult(Array.CreateInstance(element, 0));
        }

        if (type.IsClass && type != typeof(string) && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return new OkObjectResult(Activator.CreateInstance(type));
        }

        return new NotFoundResult();
    }
}
