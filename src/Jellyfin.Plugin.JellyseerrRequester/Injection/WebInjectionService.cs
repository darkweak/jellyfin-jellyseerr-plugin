using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyseerrRequester.Injection;

/// <summary>
/// Injects the client script into Jellyfin's web UI.
/// Prefers the "File Transformation" plugin (in-memory, works with read-only web folders);
/// otherwise patches index.html on disk.
/// </summary>
public partial class WebInjectionService : IHostedService
{
    private const string Marker = "data-jellyseerr-requester";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<WebInjectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionService"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public WebInjectionService(IApplicationPaths applicationPaths, ILogger<WebInjectionService> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    private static string ScriptTag =>
        $"<script {Marker} defer src=\"../JellyseerrRequester/ClientScript?v={typeof(Plugin).Assembly.GetName().Version}\"></script>";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var enabled = Plugin.Instance?.Configuration.NeedsClientScript() ?? true;

        if (enabled && TryRegisterFileTransformation())
        {
            // Remove any tag left on disk by an earlier fallback injection, so it isn't loaded twice.
            PatchIndexFile(false);
            return Task.CompletedTask;
        }

        PatchIndexFile(enabled);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Adds (or removes) the script tag in the given HTML.
    /// </summary>
    /// <param name="html">index.html contents.</param>
    /// <param name="inject">True to add the tag, false to remove it.</param>
    /// <returns>Updated HTML.</returns>
    public static string Transform(string html, bool inject)
    {
        html = ExistingTagRegex().Replace(html, string.Empty);
        if (!inject)
        {
            return html;
        }

        // Only touch real HTML pages; appending a tag to anything else (e.g. a JS chunk) breaks it.
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyEnd < 0 ? html : html.Insert(bodyEnd, ScriptTag);
    }

    private bool TryRegisterFileTransformation()
    {
        try
        {
            var assemblies = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).ToList();
            var ftAssembly = assemblies.FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
            var pluginInterface = ftAssembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = pluginInterface?.GetMethod("RegisterTransformation");

            // File Transformation expects a Newtonsoft JObject; it ships Newtonsoft itself, so use it via reflection.
            var jObjectType = assemblies
                .Where(a => a.GetName().Name == "Newtonsoft.Json")
                .Select(a => a.GetType("Newtonsoft.Json.Linq.JObject"))
                .FirstOrDefault(t => t is not null);
            var parse = jObjectType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });

            if (register is null || parse is null)
            {
                return false;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = Plugin.PluginId.ToString(),
                // A regex: unescaped, "index.html" also matches chunks like "itemDetails-index-html.<hash>.chunk.js".
                fileNamePattern = @"(^|/)index\.html$",
                callbackAssembly = typeof(WebInjectionService).Assembly.FullName,
                callbackClass = typeof(FileTransformationCallback).FullName,
                callbackMethod = nameof(FileTransformationCallback.TransformIndex)
            });

            register.Invoke(null, new[] { parse.Invoke(null, new object[] { json }) });
            _logger.LogInformation("Jellyseerr Requester: client script registered with the File Transformation plugin");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyseerr Requester: File Transformation registration failed, falling back to patching index.html");
            return false;
        }
    }

    private void PatchIndexFile(bool inject)
    {
        var webPath = _applicationPaths.WebPath;
        var indexPath = string.IsNullOrEmpty(webPath) ? null : Path.Combine(webPath, "index.html");
        if (indexPath is null || !File.Exists(indexPath))
        {
            if (inject)
            {
                _logger.LogWarning("Jellyseerr Requester: index.html not found in web path '{WebPath}', cannot inject client script", webPath);
            }

            return;
        }

        try
        {
            var original = File.ReadAllText(indexPath);
            var updated = Transform(original, inject);
            if (updated == original)
            {
                return;
            }

            File.WriteAllText(indexPath, updated);
            _logger.LogInformation("Jellyseerr Requester: {Action} client script in {Path}", inject ? "injected" : "removed", indexPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (inject)
            {
                _logger.LogWarning(
                    "Jellyseerr Requester: cannot write {Path} ({Message}). Install the 'File Transformation' plugin, or make the web folder writable, to enable the search integration",
                    indexPath,
                    ex.Message);
            }
        }
    }

    [GeneratedRegex("<script [^>]*" + Marker + "[^>]*></script>", RegexOptions.IgnoreCase)]
    private static partial Regex ExistingTagRegex();
}

/// <summary>
/// Callback invoked by the File Transformation plugin when serving index.html.
/// </summary>
public static class FileTransformationCallback
{
    /// <summary>
    /// Injects the client script into index.html.
    /// </summary>
    /// <param name="payload">Payload with the current file contents.</param>
    /// <returns>Transformed contents.</returns>
    public static string TransformIndex(PatchRequestPayload payload)
    {
        var enabled = Plugin.Instance?.Configuration.NeedsClientScript() ?? true;
        return WebInjectionService.Transform(payload.Contents ?? string.Empty, enabled);
    }
}

/// <summary>
/// File Transformation callback payload.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>Gets or sets the file contents.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
