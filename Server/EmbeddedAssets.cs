using System.Collections.Concurrent;

namespace Daggeragent.Server;

/// <summary>
/// Helpers for reading files embedded into the assembly via the EmbeddedResource itemgroup
/// in DaggerAgent.csproj. Used for icon.ico and the embedded Web UI (Server/Ui/**).
/// </summary>
public static class EmbeddedAssets
{
    private const string IconResourceName = "Dagger.icon.ico";
    private const string UiPrefix = "Dagger.ui.";

    private static byte[]? _iconBytes;
    private static string? _iconTempPath;
    private static readonly ConcurrentDictionary<string, byte[]> _uiCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Raw bytes of the embedded icon.ico. Cached after first read. </summary>
    public static byte[] IconBytes
    {
        get
        {
            if (_iconBytes is not null) return _iconBytes;
            using var stream = typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(IconResourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{IconResourceName}' not found.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return _iconBytes = ms.ToArray();
        }
    }

    /// <summary>
    /// Path to a temp-extracted copy of the icon — needed by Win32 LoadImage which
    /// only takes a file path for LR_LOADFROMFILE. Returns null if extraction fails.
    /// Cached for the process lifetime.
    /// </summary>
    public static string? GetIconTempPath()
    {
        if (_iconTempPath is not null && File.Exists(_iconTempPath)) return _iconTempPath;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "daggeragent.icon.ico");
            File.WriteAllBytes(path, IconBytes);
            return _iconTempPath = path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Look up an embedded UI asset by relative path (e.g. "index.html", "halfmoon.min.css").
    /// Maps to manifest resource "Dagger.ui.{path}" — matches the csproj
    /// EmbeddedResource glob's LogicalName transform. Subdirectory separators map to dots.
    /// Returns false if the asset isn't embedded.
    /// </summary>
    public static bool TryGetUiAsset(string relPath, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "application/octet-stream";
        if (string.IsNullOrWhiteSpace(relPath)) return false;

        var normalised = relPath.Replace('\\', '/').Trim('/');
        // Block obvious path-escape attempts before we even try the manifest.
        if (normalised.Contains("..", StringComparison.Ordinal)) return false;

        var resourceName = UiPrefix + normalised.Replace('/', '.');
        if (_uiCache.TryGetValue(resourceName, out var cached))
        {
            bytes = cached;
            contentType = MimeFor(normalised);
            return true;
        }

        using var stream = typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return false;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        bytes = ms.ToArray();
        _uiCache[resourceName] = bytes;
        contentType = MimeFor(normalised);
        return true;
    }

    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".map" => "application/json; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }
}
