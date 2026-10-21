using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyseerrRequester.Jellyseerr;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyseerrRequester.Virtual;

/// <summary>
/// Fetches TMDB artwork for virtual items and draws a status badge on posters.
/// Uses the SkiaSharp library bundled with Jellyfin.
/// </summary>
public class PosterRenderer
{
    private const string TmdbImageBase = "https://image.tmdb.org/t/p/";
    private const int MaxCacheEntries = 300;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PosterRenderer> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Used, byte[] Data)> _cache = new();
    private SKTypeface? _typeface;
    private bool _typefaceResolved;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterRenderer"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public PosterRenderer(IHttpClientFactory httpClientFactory, ILogger<PosterRenderer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a poster with an optional status badge, as JPEG.
    /// </summary>
    /// <param name="posterPath">TMDB poster path (may be null: a placeholder is drawn).</param>
    /// <param name="title">Title for the placeholder.</param>
    /// <param name="status">Media status, used for the badge; null for no badge.</param>
    /// <param name="width">Requested width, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JPEG bytes.</returns>
    public async Task<byte[]> GetPosterAsync(string? posterPath, string title, int? status, int? width, CancellationToken cancellationToken)
    {
        var targetWidth = Math.Clamp(width ?? 400, 80, 780);
        var key = $"p|{posterPath}|{title}|{status}|{targetWidth}";
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        var source = string.IsNullOrEmpty(posterPath)
            ? null
            : await DownloadAsync(TmdbImageBase + (targetWidth > 342 ? "w780" : "w342") + posterPath, cancellationToken).ConfigureAwait(false);

        byte[] result;
        try
        {
            result = Render(source, title, status, targetWidth);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never break a client over artwork: fall back to the untouched poster.
            _logger.LogWarning(ex, "Jellyseerr Requester: could not draw poster badge");
            result = source ?? Array.Empty<byte>();
        }

        AddCached(key, result);
        return result;
    }

    /// <summary>
    /// Gets a backdrop image as JPEG.
    /// </summary>
    /// <param name="backdropPath">TMDB backdrop path.</param>
    /// <param name="width">Requested width.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JPEG bytes, or null.</returns>
    public async Task<byte[]?> GetBackdropAsync(string? backdropPath, int? width, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(backdropPath))
        {
            return null;
        }

        var size = width is > 0 and <= 780 ? "w780" : "w1280";
        var key = "b|" + size + backdropPath;
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        var data = await DownloadAsync(TmdbImageBase + size + backdropPath, cancellationToken).ConfigureAwait(false);
        if (data is not null)
        {
            AddCached(key, data);
        }

        return data;
    }

    private byte[] Render(byte[]? source, string title, int? status, int width)
    {
        var height = width * 3 / 2;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x1f, 0x29, 0x37));

        using var image = source is null ? null : SKImage.FromEncodedData(source);
        if (image is not null)
        {
            // Cover-fit the poster into 2:3.
            var scale = Math.Max((float)width / image.Width, (float)height / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            var dest = SKRect.Create((width - w) / 2, (height - h) / 2, w, h);
            canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        else
        {
            DrawCenteredText(canvas, title, width / 2f, height / 2f, width * 0.08f, width * 0.85f, SKColors.White);
        }

        if (status.HasValue && Badge(status.Value) is { } badge)
        {
            DrawBadge(canvas, width, height, badge.Text, badge.Color, status.Value);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    private void DrawBadge(SKCanvas canvas, int width, int height, string text, SKColor color, int status)
    {
        var bandHeight = Math.Max(18f, height * 0.085f);
        var band = SKRect.Create(0, height - bandHeight, width, bandHeight);

        using var fill = new SKPaint { Color = color.WithAlpha(235), IsAntialias = true };
        canvas.DrawRect(band, fill);

        // Icon: "+" for requestable, clock for requested, arrow for downloading, check for available.
        var iconSize = bandHeight * 0.5f;
        var typeface = GetTypeface();
        using var font = typeface is null ? null : new SKFont(typeface, bandHeight * 0.46f) { Embolden = typeface.FontStyle.Weight < 600 };
        var hasText = font is not null && font.ContainsGlyphs(text);
        var textWidth = hasText ? font!.MeasureText(text) : 0f;
        var gap = hasText ? bandHeight * 0.3f : 0f;
        var totalWidth = iconSize + gap + textWidth;
        var x = (width - totalWidth) / 2f;
        var cy = band.MidY;

        using var stroke = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2f, bandHeight * 0.09f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        var icon = SKRect.Create(x, cy - (iconSize / 2), iconSize, iconSize);
        using (var path = new SKPath())
        {
            switch (status)
            {
                case (int)MediaStatus.Pending:
                    canvas.DrawCircle(icon.MidX, icon.MidY, iconSize / 2, stroke);
                    path.MoveTo(icon.MidX, icon.Top + (iconSize * 0.22f));
                    path.LineTo(icon.MidX, icon.MidY);
                    path.LineTo(icon.MidX + (iconSize * 0.22f), icon.MidY + (iconSize * 0.15f));
                    break;
                case (int)MediaStatus.Processing:
                    path.MoveTo(icon.MidX, icon.Top);
                    path.LineTo(icon.MidX, icon.Bottom);
                    path.MoveTo(icon.Left + (iconSize * 0.15f), icon.MidY + (iconSize * 0.1f));
                    path.LineTo(icon.MidX, icon.Bottom);
                    path.LineTo(icon.Right - (iconSize * 0.15f), icon.MidY + (iconSize * 0.1f));
                    break;
                case (int)MediaStatus.Available:
                    path.MoveTo(icon.Left, icon.MidY);
                    path.LineTo(icon.Left + (iconSize * 0.38f), icon.Bottom - (iconSize * 0.12f));
                    path.LineTo(icon.Right, icon.Top + (iconSize * 0.1f));
                    break;
                default:
                    path.MoveTo(icon.MidX, icon.Top);
                    path.LineTo(icon.MidX, icon.Bottom);
                    path.MoveTo(icon.Left, icon.MidY);
                    path.LineTo(icon.Right, icon.MidY);
                    break;
            }

            canvas.DrawPath(path, stroke);
        }

        if (hasText)
        {
            using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            font!.GetFontMetrics(out var metrics);
            var baseline = cy - ((metrics.Ascent + metrics.Descent) / 2);
            canvas.DrawText(text, icon.Right + gap, baseline, SKTextAlign.Left, font, textPaint);
        }
    }

    private void DrawCenteredText(SKCanvas canvas, string text, float cx, float cy, float size, float maxWidth, SKColor color)
    {
        var typeface = GetTypeface();
        if (typeface is null)
        {
            return;
        }

        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        // Greedy word wrap.
        var lines = new System.Collections.Generic.List<string>();
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureText(candidate) > maxWidth)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        var lineHeight = size * 1.25f;
        var y = cy - ((lines.Count - 1) * lineHeight / 2);
        foreach (var l in lines.Take(6))
        {
            canvas.DrawText(l, cx, y, SKTextAlign.Center, font, paint);
            y += lineHeight;
        }
    }

    private SKTypeface? GetTypeface()
    {
        if (_typefaceResolved)
        {
            return _typeface;
        }

        foreach (var family in new[] { "DejaVu Sans", "Noto Sans", "Liberation Sans", "Arial", "Helvetica", "Segoe UI", "sans-serif" })
        {
            var tf = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (tf is not null && tf.GlyphCount > 0 && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                _typeface = tf;
                break;
            }
        }

        _typeface ??= SKTypeface.Default is { GlyphCount: > 0 } d ? d : null;
        _typefaceResolved = true;
        if (_typeface is null)
        {
            _logger.LogInformation("Jellyseerr Requester: no font found, poster badges will show icons only");
        }

        return _typeface;
    }

    private static (string Text, SKColor Color)? Badge(int status) => status switch
    {
        (int)MediaStatus.Unknown or (int)MediaStatus.Deleted => ("REQUEST", new SKColor(0x63, 0x66, 0xf1)),
        (int)MediaStatus.Pending => ("REQUESTED", new SKColor(0xd9, 0x77, 0x06)),
        (int)MediaStatus.Processing => ("DOWNLOADING", new SKColor(0x25, 0x63, 0xeb)),
        (int)MediaStatus.PartiallyAvailable => ("REQUEST MORE", new SKColor(0x63, 0x66, 0xf1)),
        (int)MediaStatus.Available => ("AVAILABLE", new SKColor(0x16, 0xa3, 0x4a)),
        _ => null
    };

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            return await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Jellyseerr Requester: could not download {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    private bool TryGetCached(string key, out byte[] data)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            _cache[key] = (DateTime.UtcNow, entry.Data);
            data = entry.Data;
            return true;
        }

        data = Array.Empty<byte>();
        return false;
    }

    private void AddCached(string key, byte[] data)
    {
        _cache[key] = (DateTime.UtcNow, data);
        if (_cache.Count > MaxCacheEntries)
        {
            foreach (var old in _cache.OrderBy(kv => kv.Value.Used).Take(_cache.Count - MaxCacheEntries).ToList())
            {
                _cache.TryRemove(old.Key, out _);
            }
        }
    }
}
