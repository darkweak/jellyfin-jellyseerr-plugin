using System;
using System.Linq;
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
    public int MaxResults { get; set; } = 20;

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
    /// Gets or sets the number of hours after which a download without progress, or a request with nothing
    /// in the download queue, is shown as stalled.
    /// </summary>
    public int StaleAfterHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets a value indicating whether a Downloads row (requests being downloaded, with their progress)
    /// is added to the Jellyfin web home page.
    /// </summary>
    public bool ShowDownloadsRow { get; set; } = true;

    /// <summary>
    /// Gets or sets the Jellyfin users the plugin is turned off for: no Jellyseerr titles in their search,
    /// no home page rows, no Request button, and the plugin's API refuses their calls.
    /// </summary>
#pragma warning disable CA1819 // Arrays keep the XML configuration serializable.
    public Guid[] DisabledUserIds { get; set; } = Array.Empty<Guid>();
#pragma warning restore CA1819

    /// <summary>
    /// Whether the plugin is turned off for a Jellyfin user.
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>True when the user is in <see cref="DisabledUserIds"/>.</returns>
    public bool IsDisabledFor(Guid userId) => userId != Guid.Empty && Array.IndexOf(DisabledUserIds ?? Array.Empty<Guid>(), userId) >= 0;

    /// <summary>
    /// Gets or sets the users in child mode and the age each one may request up to.
    /// </summary>
#pragma warning disable CA1819 // Arrays keep the XML configuration serializable.
    public ChildUser[] ChildUsers { get; set; } = Array.Empty<ChildUser>();
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the country whose age ratings are used first (ISO 3166-1, e.g. "US", "FR").
    /// </summary>
    public string RatingCountry { get; set; } = "US";

    /// <summary>
    /// Gets or sets a value indicating whether child accounts are refused titles without any age rating.
    /// </summary>
    public bool ChildBlockUnrated { get; set; } = true;

    /// <summary>
    /// Gets or sets the Jellyfin tags (comma separated) added to titles a child requested, once they're in the library.
    /// </summary>
    public string ChildJellyfinTags { get; set; } = "kids";

    /// <summary>
    /// Gets or sets the Radarr/Sonarr tags (comma separated labels) sent with requests made by a child.
    /// </summary>
    public string ChildArrTags { get; set; } = string.Empty;

    /// <summary>
    /// The age a user may request up to, when they are in child mode.
    /// </summary>
    /// <param name="userId">Jellyfin user id.</param>
    /// <returns>Maximum age, or null when the user isn't in child mode.</returns>
    public int? ChildMaxAgeFor(Guid userId)
        => userId == Guid.Empty ? null : (ChildUsers ?? Array.Empty<ChildUser>()).FirstOrDefault(c => c.UserId == userId)?.MaxAge;

    /// <summary>
    /// Splits a comma separated tag setting.
    /// </summary>
    /// <param name="tags">Setting value.</param>
    /// <returns>Distinct, trimmed tags.</returns>
    public static string[] SplitTags(string? tags)
        => (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Whether the web client script is needed (Request button or home page rows).
    /// </summary>
    /// <returns>True when the script should be injected.</returns>
    public bool NeedsClientScript() => InjectClientScript || ShowDiscoverRows || ShowDownloadsRow;
}

/// <summary>
/// A user in child mode.
/// </summary>
public class ChildUser
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the highest age rating the user may request (e.g. 12 allows titles rated 12 and under).
    /// </summary>
    public int MaxAge { get; set; } = 12;
}
