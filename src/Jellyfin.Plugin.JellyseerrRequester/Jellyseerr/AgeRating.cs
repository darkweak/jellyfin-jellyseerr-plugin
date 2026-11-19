using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;

/// <summary>
/// Turns TMDB certifications (movie release certifications, TV content ratings) into a minimum age.
/// </summary>
public static partial class AgeRating
{
    // Letter codes per country. Numeric codes ("12", "-16", "FSK 12", "MA15+", "TV-14"…) are parsed generically.
    // US ages follow Jellyfin's own us.csv rating scores.
    private static readonly Dictionary<string, Dictionary<string, int>> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = Codes(("G", 0), ("PG", 10), ("PG-13", 13), ("R", 17), ("NC-17", 18), ("TV-Y", 0), ("TV-Y7", 7), ("TV-Y7-FV", 7), ("TV-G", 0), ("TV-PG", 10), ("TV-14", 14), ("TV-MA", 17)),
        ["GB"] = Codes(("U", 0), ("UC", 0), ("PG", 8), ("12A", 12), ("R18", 18)),
        ["CA"] = Codes(("G", 0), ("PG", 8), ("14A", 14), ("18A", 18), ("R", 18), ("A", 18), ("C", 0), ("C8", 8), ("E", 0)),
        ["AU"] = Codes(("E", 0), ("G", 0), ("PG", 8), ("M", 15), ("MA15+", 15), ("R18+", 18), ("X18+", 18), ("C", 0), ("P", 0), ("AV15+", 15)),
        ["FR"] = Codes(("U", 0), ("TP", 0), ("Tous publics", 0)),
        ["NL"] = Codes(("AL", 0)),
        ["BR"] = Codes(("L", 0), ("ER", 0)),
        ["ES"] = Codes(("A", 0), ("APTA", 0), ("TP", 0), ("X", 18)),
        ["IT"] = Codes(("T", 0), ("VM14", 14), ("VM18", 18)),
    };

    // Codes that mean "all ages" almost everywhere.
    private static readonly Dictionary<string, int> CommonCodes = Codes(("U", 0), ("TP", 0), ("AL", 0), ("L", 0), ("G", 0), ("ALL", 0), ("TV-Y", 0), ("TV-G", 0));

    /// <summary>
    /// Countries offered in the settings, most common first.
    /// </summary>
    public static readonly string[] Countries = { "US", "GB", "FR", "DE", "CA", "AU", "ES", "IT", "NL", "BR" };

    /// <summary>
    /// Finds the age rating of a title from Jellyseerr movie or TV details.
    /// Uses <paramref name="country"/> first, then the US, then the UK, then the strictest rating found anywhere.
    /// </summary>
    /// <param name="raw">Raw Jellyseerr movie or TV details.</param>
    /// <param name="isMovie">Whether <paramref name="raw"/> is a movie.</param>
    /// <param name="country">Preferred ISO 3166-1 country code.</param>
    /// <returns>The certification as shown in that country and its minimum age, or null when unrated.</returns>
    public static (string Label, int MinimumAge)? Resolve(JsonNode? raw, bool isMovie, string? country)
    {
        if (raw is null)
        {
            return null;
        }

        var ratings = Certifications(raw, isMovie)
            .Select(c => (c.Country, c.Code, Age: ToAge(c.Country, c.Code)))
            .Where(c => c.Age is not null)
            .ToList();

        // TMDB flags adult (pornographic) titles separately; they rarely carry a certification.
        var adult = raw["adult"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;

        foreach (var preferred in new[] { country, "US", "GB" })
        {
            if (string.IsNullOrWhiteSpace(preferred))
            {
                continue;
            }

            var match = ratings.Where(r => r.Country.Equals(preferred, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Age).FirstOrDefault();
            if (match.Code is not null)
            {
                return Finish(match.Code, match.Age!.Value, adult);
            }
        }

        var strictest = ratings.OrderByDescending(r => r.Age).FirstOrDefault();
        if (strictest.Code is not null)
        {
            return Finish(strictest.Country + " " + strictest.Code, strictest.Age!.Value, adult);
        }

        return adult ? ("Adult", 18) : null;
    }

    /// <summary>
    /// Converts one certification to a minimum age.
    /// </summary>
    /// <param name="country">ISO 3166-1 country code.</param>
    /// <param name="code">Certification, e.g. "PG-13", "12", "-16", "FSK 12".</param>
    /// <returns>The minimum age, or null when the code means unrated or is unknown.</returns>
    public static int? ToAge(string country, string code)
    {
        code = code.Trim();
        if (code.Length == 0 || code.Equals("NR", StringComparison.OrdinalIgnoreCase) || code.Contains("unrated", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (CountryCodes.TryGetValue(country, out var codes) && codes.TryGetValue(code, out var age))
        {
            return age;
        }

        var number = NumberPattern().Match(code);
        if (number.Success && int.TryParse(number.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n <= 21)
        {
            return n;
        }

        return CommonCodes.TryGetValue(code, out var common) ? common : null;
    }

    private static (string Label, int MinimumAge) Finish(string label, int age, bool adult) => adult ? (label, Math.Max(age, 18)) : (label, age);

    private static IEnumerable<(string Country, string Code)> Certifications(JsonNode raw, bool isMovie)
    {
        if (isMovie)
        {
            // releases.results[].release_dates[].certification
            foreach (var country in raw["releases"]?["results"]?.AsArray() ?? new JsonArray())
            {
                var iso = country?["iso_3166_1"]?.GetValue<string>();
                foreach (var release in country?["release_dates"]?.AsArray() ?? new JsonArray())
                {
                    var cert = release?["certification"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(iso) && !string.IsNullOrWhiteSpace(cert))
                    {
                        yield return (iso, cert);
                    }
                }
            }
        }
        else
        {
            // contentRatings.results[].rating
            foreach (var rating in raw["contentRatings"]?["results"]?.AsArray() ?? new JsonArray())
            {
                var iso = rating?["iso_3166_1"]?.GetValue<string>();
                var cert = rating?["rating"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(iso) && !string.IsNullOrWhiteSpace(cert))
                {
                    yield return (iso, cert);
                }
            }
        }
    }

    private static Dictionary<string, int> Codes(params (string Code, int Age)[] codes)
        => codes.ToDictionary(c => c.Code, c => c.Age, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\d{1,2}")]
    private static partial Regex NumberPattern();
}
