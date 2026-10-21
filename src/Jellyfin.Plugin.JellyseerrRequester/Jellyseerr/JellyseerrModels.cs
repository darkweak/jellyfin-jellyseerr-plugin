using System;

namespace Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;

/// <summary>
/// Jellyseerr media status (MediaStatus enum in Jellyseerr).
/// </summary>
public enum MediaStatus
{
    /// <summary>Not requested / unknown.</summary>
    Unknown = 1,

    /// <summary>Requested, waiting for approval.</summary>
    Pending = 2,

    /// <summary>Approved, downloading.</summary>
    Processing = 3,

    /// <summary>Some seasons available.</summary>
    PartiallyAvailable = 4,

    /// <summary>Fully available.</summary>
    Available = 5,

    /// <summary>Blacklisted by an admin.</summary>
    Blacklisted = 6,

    /// <summary>Deleted from the media server.</summary>
    Deleted = 7
}

/// <summary>
/// Minimal Jellyseerr user.
/// </summary>
public class JellyseerrUser
{
    /// <summary>Gets or sets the Jellyseerr user id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the linked Jellyfin user id.</summary>
    public string? JellyfinUserId { get; set; }

    /// <summary>Gets or sets the linked Jellyfin user name.</summary>
    public string? JellyfinUsername { get; set; }
}

/// <summary>
/// Error returned by Jellyseerr or raised while talking to it.
/// </summary>
public class JellyseerrException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrException"/> class.
    /// </summary>
    /// <param name="statusCode">HTTP status code to return to the client.</param>
    /// <param name="message">Message.</param>
    public JellyseerrException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }
}
