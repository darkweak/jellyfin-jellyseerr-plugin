using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyseerrRequester.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;

/// <summary>
/// Thin wrapper around the Jellyseerr (Overseerr-compatible) REST API.
/// </summary>
public class JellyseerrClient
{
    private static readonly TimeSpan UserCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JellyseerrClient> _logger;
    private readonly SemaphoreSlim _userCacheLock = new(1, 1);

    private static string _serverVersion = string.Empty;

    private List<JellyseerrUser> _userCache = new();
    private DateTime _userCacheExpiry = DateTime.MinValue;
    private string _userCacheKey = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public JellyseerrClient(IHttpClientFactory httpClientFactory, ILogger<JellyseerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether the plugin has a URL and API key configured.
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.JellyseerrUrl) && !string.IsNullOrWhiteSpace(Config.ApiKey);

    /// <summary>
    /// Gets the product name of the configured server, as last seen by a connection test or user lookup.
    /// </summary>
    public static string ServerName => ServerNameFor(_serverVersion);

    /// <summary>
    /// Names the product from its version. Seerr, the successor of Jellyseerr and Overseerr, starts at 3.0;
    /// its API is the same for everything this plugin uses.
    /// </summary>
    /// <param name="version">Version reported by /api/v1/status.</param>
    /// <returns>"Seerr" or "Jellyseerr".</returns>
    public static string ServerNameFor(string? version) =>
        System.Version.TryParse((version ?? string.Empty).TrimStart('v').Split('-')[0], out var v) && v.Major >= 3 ? "Seerr" : "Jellyseerr";

    /// <summary>
    /// Checks connectivity and credentials, returning the API key owner's display name and the server version.
    /// </summary>
    /// <param name="url">Jellyseerr URL.</param>
    /// <param name="apiKey">API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (version, displayName).</returns>
    public async Task<(string Version, string User)> TestConnectionAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        var status = await SendAsync(HttpMethod.Get, url, apiKey, "/api/v1/status", null, null, cancellationToken).ConfigureAwait(false);
        var me = await SendAsync(HttpMethod.Get, url, apiKey, "/api/v1/auth/me", null, null, cancellationToken).ConfigureAwait(false);
        var version = status?["version"]?.GetValue<string>() ?? "unknown";
        if (url.TrimEnd('/') == Config.JellyseerrUrl.TrimEnd('/'))
        {
            _serverVersion = version;
        }

        return (version, DisplayName(me));
    }

    /// <summary>
    /// Searches Jellyseerr (TMDB) for movies and TV shows.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="page">Result page (1-based).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw search response.</returns>
    public Task<JsonNode?> SearchAsync(string query, int page, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/api/v1/search?query={0}&page={1}", EncodeQuery(query), page);
        return SendAsync(HttpMethod.Get, path, null, null, cancellationToken);
    }

    /// <summary>
    /// Gets a Jellyseerr discover list (same endpoints in Jellyseerr, Overseerr and Seerr).
    /// </summary>
    /// <param name="list">"trending", "movies" (popular movies) or "tv" (popular shows).</param>
    /// <param name="page">Result page (1-based).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw discover response.</returns>
    public Task<JsonNode?> DiscoverAsync(string list, int page, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/api/v1/discover/{0}?page={1}", list, page);
        return SendAsync(HttpMethod.Get, path, null, null, cancellationToken);
    }

    /// <summary>
    /// Gets TV show details including seasons and their availability.
    /// </summary>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw TV details.</returns>
    public Task<JsonNode?> GetTvAsync(int tmdbId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "/api/v1/tv/" + tmdbId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken);

    /// <summary>
    /// Gets movie details.
    /// </summary>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw movie details.</returns>
    public Task<JsonNode?> GetMovieAsync(int tmdbId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "/api/v1/movie/" + tmdbId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken);

    /// <summary>
    /// Creates a media request on behalf of a Jellyseerr user.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="seasons">Season numbers for TV requests; null or empty requests all seasons.</param>
    /// <param name="is4K">Whether to request the 4K version.</param>
    /// <param name="jellyseerrUserId">Jellyseerr user to request as, or null for the API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="arrTags">Radarr/Sonarr tag ids to send with the request; null leaves the server defaults.</param>
    /// <returns>Raw request response.</returns>
    public Task<JsonNode?> CreateRequestAsync(string mediaType, int tmdbId, IReadOnlyCollection<int>? seasons, bool is4K, int? jellyseerrUserId, CancellationToken cancellationToken, IReadOnlyCollection<int>? arrTags = null)
    {
        var body = new JsonObject
        {
            ["mediaType"] = mediaType,
            ["mediaId"] = tmdbId,
            ["is4k"] = is4K
        };

        if (mediaType == "tv")
        {
            body["seasons"] = seasons is { Count: > 0 }
                ? new JsonArray(seasons.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
                : JsonValue.Create("all");
        }

        if (arrTags is { Count: > 0 })
        {
            body["tags"] = new JsonArray(arrTags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        }

        return SendAsync(HttpMethod.Post, "/api/v1/request", body, jellyseerrUserId, cancellationToken);
    }

    /// <summary>
    /// Resolves Radarr (movies) or Sonarr (shows) tag labels to ids on the server Jellyseerr sends the request to
    /// (its default server), together with that server's default tags, which a request's own tags replace.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="is4K">Whether the request goes to the 4K server.</param>
    /// <param name="labels">Tag labels, compared case-insensitively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tag ids to send, and the labels that don't exist on the server.</returns>
    public async Task<(List<int> TagIds, List<string> Missing)> ResolveArrTagsAsync(string mediaType, bool is4K, IReadOnlyCollection<string> labels, CancellationToken cancellationToken)
    {
        var service = mediaType == "movie" ? "radarr" : "sonarr";
        var servers = await SendAsync(HttpMethod.Get, "/api/v1/service/" + service, null, null, cancellationToken).ConfigureAwait(false);
        var server = servers?.AsArray()
            .Where(s => s?["is4k"]?.GetValue<bool>() == is4K)
            .OrderByDescending(s => s?["isDefault"]?.GetValue<bool>() == true)
            .FirstOrDefault();
        if (server?["id"]?.GetValue<int>() is not { } serverId)
        {
            throw new JellyseerrException(503, $"No {(is4K ? "4K " : string.Empty)}{(service == "radarr" ? "Radarr" : "Sonarr")} server is set up in Jellyseerr.");
        }

        var details = await SendAsync(HttpMethod.Get, "/api/v1/service/" + service + "/" + serverId.ToString(CultureInfo.InvariantCulture), null, null, cancellationToken).ConfigureAwait(false);
        var available = (details?["tags"]?.AsArray() ?? new JsonArray())
            .Select(t => (Id: t?["id"]?.GetValue<int>(), Label: t?["label"]?.GetValue<string>()))
            .Where(t => t.Id is not null && t.Label is not null)
            .ToList();

        var ids = (server["activeTags"]?.AsArray() ?? new JsonArray()).Select(t => t?.GetValue<int>()).OfType<int>().ToList();
        var missing = new List<string>();
        foreach (var label in labels)
        {
            var match = available.FirstOrDefault(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));
            if (match.Id is { } id)
            {
                ids.Add(id);
            }
            else
            {
                missing.Add(label);
            }
        }

        return (ids.Distinct().ToList(), missing);
    }

    /// <summary>
    /// Lists requests, newest change first. Jellyseerr only returns the acting user's own requests unless
    /// they may view or manage everyone's.
    /// </summary>
    /// <param name="filter">Jellyseerr filter, e.g. "processing" (approved, not available yet).</param>
    /// <param name="take">Page size.</param>
    /// <param name="actAsUserId">Jellyseerr user to act as, or null for the API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw request list.</returns>
    public Task<JsonNode?> GetRequestsAsync(string filter, int take, int? actAsUserId, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/api/v1/request?take={0}&skip=0&filter={1}&sort=modified", take, Uri.EscapeDataString(filter));
        return SendAsync(HttpMethod.Get, path, null, actAsUserId, cancellationToken);
    }

    /// <summary>
    /// Removes a title from Radarr or Sonarr (Jellyseerr's "Remove from *arr"), deleting its files there.
    /// </summary>
    /// <param name="mediaId">Jellyseerr media id (not the TMDB id).</param>
    /// <param name="is4K">Whether to remove it from the 4K server.</param>
    /// <param name="actAsUserId">Jellyseerr user to act as, or null for the API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteMediaFileAsync(int mediaId, bool is4K, int? actAsUserId, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/api/v1/media/{0}/file?is4k={1}", mediaId, is4K ? "true" : "false");
        return SendAsync(HttpMethod.Delete, path, null, actAsUserId, cancellationToken);
    }

    /// <summary>
    /// Deletes a title's media record and its requests from Jellyseerr ("Clear data"), so it can be requested again.
    /// </summary>
    /// <param name="mediaId">Jellyseerr media id (not the TMDB id).</param>
    /// <param name="actAsUserId">Jellyseerr user to act as, or null for the API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteMediaAsync(int mediaId, int? actAsUserId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, "/api/v1/media/" + mediaId.ToString(CultureInfo.InvariantCulture), null, actAsUserId, cancellationToken);

    /// <summary>
    /// Deletes a request.
    /// </summary>
    /// <param name="requestId">Request id.</param>
    /// <param name="actAsUserId">Jellyseerr user to act as, or null for the API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteRequestAsync(int requestId, int? actAsUserId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, "/api/v1/request/" + requestId.ToString(CultureInfo.InvariantCulture), null, actAsUserId, cancellationToken);

    /// <summary>
    /// Finds the Jellyseerr user linked to a Jellyfin user.
    /// </summary>
    /// <param name="jellyfinUserId">Jellyfin user id.</param>
    /// <param name="jellyfinUserName">Jellyfin user name (used as fallback).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The linked user, or null.</returns>
    public async Task<JellyseerrUser?> FindLinkedUserAsync(Guid jellyfinUserId, string? jellyfinUserName, CancellationToken cancellationToken)
    {
        var users = await GetUsersAsync(cancellationToken).ConfigureAwait(false);
        var idN = jellyfinUserId.ToString("N");

        var match = users.FirstOrDefault(u => string.Equals(NormalizeId(u.JellyfinUserId), idN, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.IsNullOrEmpty(jellyfinUserName))
        {
            match = users.FirstOrDefault(u => string.Equals(u.JellyfinUsername, jellyfinUserName, StringComparison.OrdinalIgnoreCase));
        }

        return match;
    }

    private async Task<List<JellyseerrUser>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var cacheKey = Config.JellyseerrUrl + "|" + Config.ApiKey;
        await _userCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _userCacheExpiry && _userCacheKey == cacheKey)
            {
                return _userCache;
            }

            try
            {
                var status = await SendAsync(HttpMethod.Get, "/api/v1/status", null, null, cancellationToken).ConfigureAwait(false);
                _serverVersion = status?["version"]?.GetValue<string>() ?? _serverVersion;
            }
            catch (JellyseerrException)
            {
                // Only used to name the product in the UI.
            }

            var users = new List<JellyseerrUser>();
            const int PageSize = 100;
            for (var skip = 0; ; skip += PageSize)
            {
                var path = string.Format(CultureInfo.InvariantCulture, "/api/v1/user?take={0}&skip={1}", PageSize, skip);
                var response = await SendAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
                var results = response?["results"]?.AsArray();
                if (results is null || results.Count == 0)
                {
                    break;
                }

                foreach (var u in results)
                {
                    if (u is null)
                    {
                        continue;
                    }

                    users.Add(new JellyseerrUser
                    {
                        Id = u["id"]?.GetValue<int>() ?? 0,
                        DisplayName = DisplayName(u),
                        JellyfinUserId = u["jellyfinUserId"]?.GetValue<string>(),
                        JellyfinUsername = u["jellyfinUsername"]?.GetValue<string>(),
                        Permissions = u["permissions"] is JsonValue p && p.TryGetValue<int>(out var bits) ? bits : 0
                    });
                }

                var total = response?["pageInfo"]?["results"]?.GetValue<int>() ?? 0;
                if (users.Count >= total)
                {
                    break;
                }
            }

            _userCache = users;
            _userCacheKey = cacheKey;
            _userCacheExpiry = DateTime.UtcNow + UserCacheDuration;
            return users;
        }
        finally
        {
            _userCacheLock.Release();
        }
    }

    private Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, int? actAsUserId, CancellationToken cancellationToken)
        => SendAsync(method, Config.JellyseerrUrl, Config.ApiKey, path, body, actAsUserId, cancellationToken);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string baseUrl, string apiKey, string path, JsonNode? body, int? actAsUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new JellyseerrException(503, "Jellyseerr is not configured. Set the URL and API key in the plugin settings.");
        }

        using var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", apiKey.Trim());
        request.Headers.Add("Accept", "application/json");
        if (actAsUserId.HasValue)
        {
            // Overseerr/Jellyseerr: an admin API key can act as another user, so that user's
            // permissions, quotas and auto-approval rules apply to the request.
            request.Headers.Add("X-Api-User", actAsUserId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not reach Jellyseerr at {Url}", baseUrl);
            throw new JellyseerrException(502, "Could not reach Jellyseerr: " + ex.Message);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = TryGetMessage(content) ?? response.ReasonPhrase ?? "Unknown error";
                _logger.LogWarning("Jellyseerr {Method} {Path} returned {Status}: {Message}", method, path, (int)response.StatusCode, message);
                throw new JellyseerrException((int)response.StatusCode, message);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(content);
            }
            catch (JsonException)
            {
                throw new JellyseerrException(502, "Jellyseerr returned an invalid response. Check the configured URL.");
            }
        }
    }

    private static string? TryGetMessage(string content)
    {
        try
        {
            return JsonNode.Parse(content)?["message"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DisplayName(JsonNode? user)
    {
        if (user is null)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "displayName", "username", "jellyfinUsername", "plexUsername", "email" })
        {
            var value = user[key]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "#" + (user["id"]?.ToString() ?? "?");
    }

    private static string NormalizeId(string? id) => (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Jellyseerr's request validator rejects some characters that <see cref="Uri.EscapeDataString(string)"/> leaves as-is.
    /// </summary>
    private static string EncodeQuery(string query)
    {
        var sb = new StringBuilder(Uri.EscapeDataString(query));
        sb.Replace("!", "%21").Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
        return sb.ToString();
    }
}
