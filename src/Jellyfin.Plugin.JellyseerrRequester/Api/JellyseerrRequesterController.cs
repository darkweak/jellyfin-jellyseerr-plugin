using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyseerrRequester.Configuration;
using Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;
using Jellyfin.Plugin.JellyseerrRequester.Virtual;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyseerrRequester.Api;

/// <summary>
/// Endpoints used by the web client script and the configuration page.
/// </summary>
[ApiController]
[Route("JellyseerrRequester")]
public class JellyseerrRequesterController : ControllerBase
{
    private const string ElevationPolicy = "RequiresElevation";

    private readonly JellyseerrClient _client;
    private readonly RequesterService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrRequesterController"/> class.
    /// </summary>
    /// <param name="client">Jellyseerr client.</param>
    /// <param name="service">Requester service.</param>
    public JellyseerrRequesterController(JellyseerrClient client, RequesterService service)
    {
        _client = client;
        _service = service;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Serves the web client script injected into Jellyfin's index.html.
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.jellyseerr-requester.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Returns client settings and whether the current user can request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Client configuration.</returns>
    [HttpGet("Config")]
    [Authorize]
    public async Task<ActionResult<ClientConfigDto>> GetClientConfig(CancellationToken cancellationToken)
    {
        var config = Config;
        var dto = new ClientConfigDto
        {
            Configured = JellyseerrClient.IsConfigured,
            Enable4K = config.Enable4K,
            RequestButton = config.InjectClientScript,
            DiscoverRows = config.ShowDiscoverRows,
            PublicUrl = (string.IsNullOrWhiteSpace(config.PublicJellyseerrUrl) ? config.JellyseerrUrl : config.PublicJellyseerrUrl).TrimEnd('/')
        };

        if (!dto.Configured)
        {
            dto.Reason = "Jellyseerr is not configured.";
            return dto;
        }

        try
        {
            var user = await _service.ResolveUserAsync(User, cancellationToken).ConfigureAwait(false);
            dto.CanRequest = true;
            dto.JellyseerrUser = user?.DisplayName;
        }
        catch (JellyseerrException ex)
        {
            dto.Reason = ex.Message;
        }

        // Known once the user lookup above has talked to the server.
        dto.ServerName = JellyseerrClient.ServerName;
        return dto;
    }

    /// <summary>
    /// Gets a movie or series with its current status.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The title.</returns>
    [HttpGet("Title/{mediaType}/{tmdbId:int}")]
    [Authorize]
    public Task<ActionResult<SearchItemDto>> GetTitle([FromRoute] string mediaType, [FromRoute] int tmdbId, CancellationToken cancellationToken)
        => Run(async () => (await _service.GetTitleAsync(ToVirtualId(mediaType, tmdbId), cancellationToken).ConfigureAwait(false)).Item);

    /// <summary>
    /// Gets a home page row: trending titles, popular movies or popular shows.
    /// </summary>
    /// <param name="list">"trending", "movies" or "tv".</param>
    /// <returns>The row.</returns>
    [HttpGet("Discover/{list}")]
    [Authorize]
    public Task<ActionResult<DiscoverResponseDto>> GetDiscover([FromRoute] string list)
        => Run(() => _service.DiscoverAsync(list));

    /// <summary>
    /// Gets TV seasons and their status for the season picker.
    /// </summary>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>TV details.</returns>
    [HttpGet("Tv/{tmdbId:int}")]
    [Authorize]
    public Task<ActionResult<TvDetailsDto>> GetTv([FromRoute] int tmdbId, CancellationToken cancellationToken)
        => Run(async () => (await _service.GetTitleAsync(new VirtualId(VirtualKind.Series, tmdbId), cancellationToken).ConfigureAwait(false)).Tv
            ?? throw new JellyseerrException(StatusCodes.Status404NotFound, "Series not found."));

    /// <summary>
    /// Creates a request in Jellyseerr as the current user's linked Jellyseerr account.
    /// </summary>
    /// <param name="body">Request details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result message.</returns>
    [HttpPost("Request")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    public Task<ActionResult<MessageDto>> CreateRequest([FromBody] CreateRequestDto body, CancellationToken cancellationToken)
        => Run(() => _service.RequestAsync(User, body, cancellationToken));

    /// <summary>
    /// Tests a Jellyseerr URL and API key (admin only).
    /// </summary>
    /// <param name="body">Connection details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Server version and API key owner.</returns>
    [HttpPost("TestConnection")]
    [Authorize(Policy = ElevationPolicy)]
    [Consumes(MediaTypeNames.Application.Json)]
    public Task<ActionResult<MessageDto>> TestConnection([FromBody] TestConnectionDto body, CancellationToken cancellationToken)
        => Run(async () =>
        {
            var (version, user) = await _client.TestConnectionAsync(body.Url, body.ApiKey, cancellationToken).ConfigureAwait(false);
            return new MessageDto { Message = $"Connected to {JellyseerrClient.ServerNameFor(version)} {version} as {user}." };
        });

    private static VirtualId ToVirtualId(string mediaType, int tmdbId) => mediaType switch
    {
        "movie" => new VirtualId(VirtualKind.Movie, tmdbId),
        "tv" => new VirtualId(VirtualKind.Series, tmdbId),
        _ => throw new JellyseerrException(StatusCodes.Status400BadRequest, "Invalid media type.")
    };

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (JellyseerrException ex)
        {
            // Never forward a 401 to the Jellyfin web client: it would look like the Jellyfin session expired.
            var status = ex.StatusCode == StatusCodes.Status401Unauthorized ? StatusCodes.Status502BadGateway : ex.StatusCode;
            var message = ex.StatusCode == StatusCodes.Status401Unauthorized
                ? "Jellyseerr rejected the API key. Check the plugin settings."
                : ex.Message;
            return StatusCode(status, new { message });
        }
    }
}
