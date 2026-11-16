using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyseerrRequester.Api;
using Jellyfin.Plugin.JellyseerrRequester.Configuration;

namespace Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;

/// <summary>
/// Turns Jellyseerr's view of a title (media status, Radarr/Sonarr queue, request dates) into a download state,
/// and decides whether the download looks stuck.
/// </summary>
/// <remarks>
/// A download is stale when Radarr/Sonarr flag it (failed, warning, client unavailable), when its remaining size
/// hasn't changed for <see cref="PluginConfiguration.StaleAfterHours"/>, when its ETA is that far in the past,
/// or when the request was approved that long ago and nothing is in the download queue.
/// </remarks>
public static class DownloadAnalyzer
{
    private const int MaxTracked = 1000;

    // Remaining bytes per download, and since when they haven't changed. Fed every time a title is looked at.
    private static readonly ConcurrentDictionary<string, (long SizeLeft, DateTime Since, DateTime Seen)> _progress = new();

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets the configured age after which a download without progress counts as stale.
    /// </summary>
    public static TimeSpan StaleAfter => TimeSpan.FromHours(Math.Clamp(Config.StaleAfterHours, 1, 24 * 30));

    /// <summary>
    /// Analyzes a Jellyseerr media node (the "mediaInfo" of a title, or the "media" of a request).
    /// </summary>
    /// <param name="media">Media node.</param>
    /// <param name="is4K">Whether to look at the 4K side.</param>
    /// <param name="releaseDate">Release date of what is awaited (movie release, or latest requested season's air date).</param>
    /// <param name="since">When the request was approved, if known; otherwise read from the media node.</param>
    /// <returns>The download state, or null when nothing is being downloaded.</returns>
    public static DownloadDto? Analyze(JsonNode? media, bool is4K, string? releaseDate, DateTime? since = null)
    {
        if (media is null)
        {
            return null;
        }

        var suffix = is4K ? "4k" : string.Empty;
        var status = Int(media["status" + suffix]) ?? (int)MediaStatus.Unknown;
        var items = (media["downloadStatus" + suffix] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var seasonProcessing = (media["seasons"] as JsonArray ?? new JsonArray())
            .Any(s => Int(s?["status" + suffix]) == (int)MediaStatus.Processing);

        var active = status == (int)MediaStatus.Processing
            || (status == (int)MediaStatus.PartiallyAvailable && (seasonProcessing || items.Count > 0));
        if (!active)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var staleAfter = StaleAfter;
        since ??= ApprovedAt(media, is4K) ?? Date(media["updatedAt"]);
        var age = since is null ? TimeSpan.Zero : now - since.Value;
        var dto = new DownloadDto { Since = since, Items = items.Count };
        var arr = Str(media["mediaType"]) == "tv" ? "Sonarr" : "Radarr";

        if (items.Count == 0)
        {
            var release = Date(releaseDate);
            if (release is not null && release.Value > now)
            {
                dto.State = "waiting";
                dto.Short = "Not released yet";
                dto.Summary = "Waiting for the release on " + release.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ".";
                return dto;
            }

            dto.State = "missing";
            dto.Stale = age >= staleAfter;
            var notSent = Int(media["externalServiceId" + suffix]) is null;
            dto.Summary = notSent
                ? "Approved, but not sent to " + arr + (dto.Stale ? " after " + Duration(age) : string.Empty) + "."
                : "Nothing in the download queue" + (dto.Stale ? " for " + Duration(age) : string.Empty) + ": " + arr + " hasn't found a release yet.";
            dto.Short = dto.Stale ? "Stalled" : "Searching";
            return dto;
        }

        var statuses = new List<string>();
        DateTime? eta = null;
        foreach (var item in items)
        {
            dto.Size += Long(item["size"]);
            dto.SizeLeft += Long(item["sizeLeft"]);
            statuses.Add((Str(item["status"]) ?? string.Empty).ToLowerInvariant());
            var itemEta = Date(item["estimatedCompletionTime"]);
            if (itemEta is not null && (eta is null || itemEta > eta))
            {
                eta = itemEta;
            }
        }

        dto.EstimatedCompletion = eta;
        dto.Progress = dto.Size > 0 ? Math.Clamp(1 - ((double)dto.SizeLeft / dto.Size), 0, 1) : null;
        var unchangedFor = Track(Key(media, is4K, items), dto.SizeLeft, now);
        var percent = dto.Progress is { } p ? ((int)Math.Floor(p * 100)).ToString(CultureInfo.InvariantCulture) + "%" : null;

        if (statuses.Any(s => s is "failed" or "warning" or "downloadclientunavailable"))
        {
            dto.State = "stalled";
            dto.Stale = true;
            dto.Short = "Stalled";
            dto.Summary = statuses.Contains("failed") ? "The download failed in " + arr + "."
                : statuses.Contains("downloadclientunavailable") ? "The download client is unavailable."
                : arr + " reports a problem with the download (stalled, no connections or can't be imported).";
        }
        else if (unchangedFor >= staleAfter)
        {
            dto.State = "stalled";
            dto.Stale = true;
            dto.Short = "Stalled";
            dto.Summary = dto.SizeLeft == 0
                ? "Downloaded, but not imported for " + Duration(unchangedFor) + "."
                : "No progress for " + Duration(unchangedFor) + (percent is null ? "." : ", stuck at " + percent + ".");
        }
        else if (eta is not null && now - eta.Value >= staleAfter)
        {
            dto.State = "stalled";
            dto.Stale = true;
            dto.Short = "Stalled";
            dto.Summary = "Overdue: it was expected to finish " + Duration(now - eta.Value) + " ago" + (percent is null ? "." : ", and is at " + percent + ".");
        }
        else if (statuses.All(s => s == "paused"))
        {
            dto.State = "paused";
            dto.Short = "Paused" + (percent is null ? string.Empty : " · " + percent);
            dto.Summary = "The download is paused in the download client.";
        }
        else if (statuses.All(s => s is "queued" or "delay"))
        {
            dto.State = "queued";
            dto.Short = "Queued";
            dto.Summary = "Queued in the download client.";
        }
        else if (dto.SizeLeft == 0 || statuses.All(s => s == "completed"))
        {
            dto.State = "importing";
            dto.Short = "Importing";
            dto.Summary = "Downloaded, waiting for " + arr + " to import it.";
        }
        else
        {
            dto.State = "downloading";
            var left = eta is not null && eta.Value > now ? Duration(eta.Value - now) : null;
            dto.Short = string.Join(" · ", new[] { percent, left }.Where(x => x is not null));
            if (dto.Short.Length == 0)
            {
                dto.Short = "Downloading";
            }

            dto.Summary = "Downloading" + (percent is null ? string.Empty : ", " + percent) + (left is null ? "." : ", about " + left + " left.");
        }

        return dto;
    }

    /// <summary>
    /// Formats a duration the way people say it: "45 min", "5 h", "3 days".
    /// </summary>
    /// <param name="span">Duration.</param>
    /// <returns>Text.</returns>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 60)
        {
            return Math.Max(1, (int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (span.TotalHours < 48)
        {
            var h = (int)span.TotalHours;
            return h.ToString(CultureInfo.InvariantCulture) + " h" + (h < 10 && span.Minutes > 0 ? " " + span.Minutes.ToString(CultureInfo.InvariantCulture) + " min" : string.Empty);
        }

        return ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days";
    }

    // Latest approval among the open approved requests (details only: search results don't carry requests).
    private static DateTime? ApprovedAt(JsonNode media, bool is4K)
    {
        DateTime? latest = null;
        foreach (var r in media["requests"] as JsonArray ?? new JsonArray())
        {
            if (Int(r?["status"]) != 2 || (r?["is4k"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : false) != is4K)
            {
                continue;
            }

            var d = Date(r?["updatedAt"]) ?? Date(r?["createdAt"]);
            if (d is not null && (latest is null || d > latest))
            {
                latest = d;
            }
        }

        return latest;
    }

    private static string Key(JsonNode media, bool is4K, List<JsonObject> items)
    {
        var ids = string.Join(',', items.Select(i => Str(i["downloadId"]) ?? Str(i["title"]) ?? string.Empty).OrderBy(x => x, StringComparer.Ordinal));
        return (Int(media["id"]) ?? Int(media["tmdbId"]) ?? 0).ToString(CultureInfo.InvariantCulture) + (is4K ? "|4k|" : "|") + ids;
    }

    // Returns how long the remaining size has stayed the same, as far as this server has seen.
    private static TimeSpan Track(string key, long sizeLeft, DateTime now)
    {
        var entry = _progress.AddOrUpdate(
            key,
            _ => (sizeLeft, now, now),
            (_, old) => old.SizeLeft == sizeLeft ? (sizeLeft, old.Since, now) : (sizeLeft, now, now));

        if (_progress.Count > MaxTracked)
        {
            foreach (var old in _progress.Where(kv => now - kv.Value.Seen > TimeSpan.FromDays(7)).Select(kv => kv.Key).ToList())
            {
                _progress.TryRemove(old, out _);
            }
        }

        return now - entry.Since;
    }

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : null;

    private static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static DateTime? Date(JsonNode? node) => Date(Str(node));

    private static DateTime? Date(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
