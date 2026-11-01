using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyseerrRequester.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Jellyseerr URL as reachable from the Jellyfin server (e.g. http://jellyseerr:5055).
    /// </summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyseerr API key (Settings → General → API Key).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyseerr URL as reachable by users' browsers, used for "Open in Jellyseerr" links.
    /// Falls back to <see cref="JellyseerrUrl"/> when empty.
    /// </summary>
    public string PublicJellyseerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyseerr titles are added to Jellyfin search results
    /// (server-side, so they show in every client: web, Apple TV, Android TV, mobile apps…).
    /// </summary>
    public bool InjectIntoSearch { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of Jellyseerr titles added per search, per type (movies, shows).
    /// </summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether titles already fully available are left out.
    /// </summary>
    public bool HideAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a status badge ("REQUEST", "REQUESTED"…) is drawn on posters.
    /// </summary>
    public bool BadgePosters { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a how-to-request hint is prepended to the overview.
    /// </summary>
    public bool ShowHintInOverview { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the web client script (Request button on detail pages) is injected.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin users without a linked Jellyseerr account
    /// may request using the API key owner's account.
    /// </summary>
    public bool AllowUnlinkedUsers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a 4K request option is offered.
    /// </summary>
    public bool Enable4K { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Trending, Popular Movies and Popular TV rows from Jellyseerr
    /// are added to the Jellyfin web home page.
    /// </summary>
    public bool ShowDiscoverRows { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of titles per home page row.
    /// </summary>
    public int DiscoverItemCount { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether titles already in the Jellyfin library are left out of the home page rows.
    /// When false, they are shown and open the library item.
    /// </summary>
    public bool DiscoverHideInLibrary { get; set; }

    /// <summary>
    /// Whether the web client script is needed (Request button or home page rows).
    /// </summary>
    /// <returns>True when the script should be injected.</returns>
    public bool NeedsClientScript() => InjectClientScript || ShowDiscoverRows;
}
