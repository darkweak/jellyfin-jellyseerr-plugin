using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.JellyseerrRequester.Virtual;

/// <summary>
/// Kind of virtual (not in library) item.
/// </summary>
public enum VirtualKind : short
{
    /// <summary>A movie.</summary>
    Movie = 1,

    /// <summary>A TV series.</summary>
    Series = 2,

    /// <summary>A season of a TV series.</summary>
    Season = 3
}

/// <summary>
/// Self-describing Jellyfin item id for Jellyseerr titles, so any request for one can be served
/// without server-side state: <c>4a535252-KKKK-SSSS-5345-4552TTTTTTTT</c> (K = kind, S = season, T = TMDB id).
/// </summary>
public readonly record struct VirtualId(VirtualKind Kind, int TmdbId, int Season = 0)
{
    private const int MagicA = 0x4a535252; // "JSRR"
    private static readonly byte[] MagicD = { 0x53, 0x45, 0x45, 0x52 }; // "SEER"

    /// <summary>
    /// Gets the series id for a season, or this id otherwise.
    /// </summary>
    public VirtualId Series => Kind == VirtualKind.Season ? new VirtualId(VirtualKind.Series, TmdbId) : this;

    /// <summary>
    /// Gets the TMDB media type ("movie" or "tv").
    /// </summary>
    public string MediaType => Kind == VirtualKind.Movie ? "movie" : "tv";

    /// <summary>
    /// Encodes this id as a Jellyfin item id.
    /// </summary>
    /// <returns>The item id.</returns>
    public Guid ToGuid()
    {
        var d = new byte[8];
        MagicD.CopyTo(d, 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), TmdbId);
        return new Guid(MagicA, (short)Kind, (short)Season, d);
    }

    /// <summary>
    /// Decodes a Jellyfin item id.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="value">Decoded virtual id.</param>
    /// <returns>True when the id is a virtual Jellyseerr id.</returns>
    public static bool TryParse(Guid id, out VirtualId value)
    {
        value = default;
        Span<byte> bytes = stackalloc byte[16];
        if (!id.TryWriteBytes(bytes))
        {
            return false;
        }

        // Guid byte layout: a (int, little-endian), b (short LE), c (short LE), d (8 bytes as-is).
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != MagicA || !bytes.Slice(8, 4).SequenceEqual(MagicD))
        {
            return false;
        }

        var kind = (VirtualKind)BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(4));
        if (kind is not (VirtualKind.Movie or VirtualKind.Series or VirtualKind.Season))
        {
            return false;
        }

        value = new VirtualId(kind, BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(12)), BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(6)));
        return value.TmdbId > 0;
    }

    /// <summary>
    /// Decodes any value that may hold a Jellyfin item id (Guid, Guid?, or string).
    /// </summary>
    /// <param name="value">Value.</param>
    /// <param name="id">Decoded virtual id.</param>
    /// <returns>True when the value is a virtual Jellyseerr id.</returns>
    public static bool TryParse(object? value, out VirtualId id)
    {
        id = default;
        return value switch
        {
            Guid g => TryParse(g, out id),
            string s when Guid.TryParse(s, out var g) => TryParse(g, out id),
            _ => false
        };
    }
}
