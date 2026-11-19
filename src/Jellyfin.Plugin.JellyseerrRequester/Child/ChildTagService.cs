using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyseerrRequester.Child;

/// <summary>
/// Adds the child mode Jellyfin tags to titles a child requested, once they're in the library: straight away when
/// the title is already there, otherwise when Jellyfin adds it. Tags are added back if a metadata refresh drops them,
/// so Jellyfin's parental control ("Allow items with tags") keeps letting the child see the title.
/// </summary>
public sealed class ChildTagService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChildTagService> _logger;
    private readonly object _lock = new();
    private Dictionary<string, TaggedTitle>? _titles;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildTagService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public ChildTagService(ILibraryManager libraryManager, ILogger<ChildTagService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static string StorePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "child-tags.json");

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;

        // Titles that arrived while Jellyfin was stopped or the plugin wasn't loaded.
        foreach (var title in Titles().Where(t => !t.Applied).ToList())
        {
            TagLibraryItems(title);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
    }

    /// <summary>
    /// Remembers that a child requested a title, and tags it now if it's already in the library.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="tags">Jellyfin tags to add.</param>
    public void Track(string mediaType, int tmdbId, IReadOnlyCollection<string> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        TaggedTitle title;
        lock (_lock)
        {
            var titles = Load();
            var key = Key(mediaType, tmdbId);
            if (!titles.TryGetValue(key, out title!))
            {
                title = new TaggedTitle { MediaType = mediaType, TmdbId = tmdbId, RequestedAt = DateTime.UtcNow };
                titles[key] = title;
            }

            title.Tags = title.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
        }

        TagLibraryItems(title);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        var mediaType = item switch
        {
            Movie => "movie",
            Series => "tv",
            _ => null
        };
        if (mediaType is null
            || !int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return;
        }

        TaggedTitle? title;
        lock (_lock)
        {
            if (!Load().TryGetValue(Key(mediaType, tmdbId), out title))
            {
                return;
            }
        }

        _ = AddTagsAsync(item, title);
    }

    private void TagLibraryItems(TaggedTitle title)
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { title.MediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series },
                HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = title.TmdbId.ToString(CultureInfo.InvariantCulture) },
                Recursive = true
            });

            foreach (var item in items)
            {
                _ = AddTagsAsync(item, title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyseerr Requester: could not look up {Type} {TmdbId} in the library", title.MediaType, title.TmdbId);
        }
    }

    private async Task AddTagsAsync(BaseItem item, TaggedTitle title)
    {
        var missing = title.Tags.Where(t => !item.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0)
        {
            MarkApplied(title);
            return;
        }

        try
        {
            item.Tags = item.Tags.Concat(missing).ToArray();
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            MarkApplied(title);
            _logger.LogInformation("Jellyseerr Requester: tagged {Name} with {Tags} (requested by a child account)", item.Name, string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyseerr Requester: could not tag {Name}", item.Name);
        }
    }

    private void MarkApplied(TaggedTitle title)
    {
        lock (_lock)
        {
            if (!title.Applied)
            {
                title.Applied = true;
                Save();
            }
        }
    }

    private List<TaggedTitle> Titles()
    {
        lock (_lock)
        {
            return Load().Values.ToList();
        }
    }

    private static string Key(string mediaType, int tmdbId) => mediaType + ":" + tmdbId.ToString(CultureInfo.InvariantCulture);

    // Callers hold _lock.
    private Dictionary<string, TaggedTitle> Load()
    {
        if (_titles is not null)
        {
            return _titles;
        }

        try
        {
            var list = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<TaggedTitle>>(File.ReadAllText(StorePath)) ?? new List<TaggedTitle>()
                : new List<TaggedTitle>();
            _titles = list.ToDictionary(t => Key(t.MediaType, t.TmdbId));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Jellyseerr Requester: could not read {Path}", StorePath);
            _titles = new Dictionary<string, TaggedTitle>();
        }

        return _titles;
    }

    // Callers hold _lock.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Load().Values.ToList(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Jellyseerr Requester: could not write {Path}", StorePath);
        }
    }

    /// <summary>
    /// A title a child requested.
    /// </summary>
    public sealed class TaggedTitle
    {
        /// <summary>Gets or sets "movie" or "tv".</summary>
        public string MediaType { get; set; } = "movie";

        /// <summary>Gets or sets the TMDB id.</summary>
        public int TmdbId { get; set; }

        /// <summary>Gets or sets the Jellyfin tags to keep on the title.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Gets or sets when it was first requested.</summary>
        public DateTime RequestedAt { get; set; }

        /// <summary>Gets or sets a value indicating whether the title has been tagged in the library at least once.</summary>
        public bool Applied { get; set; }
    }
}
