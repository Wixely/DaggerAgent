using System.Reflection;

namespace Daggeragent.Server;

/// <summary>
/// Helpers for reading files embedded into the assembly via the EmbeddedResource itemgroup
/// in DaggerAgent.csproj. Right now this is just icon.ico; designed so other assets can
/// slot in later without proliferating helpers.
/// </summary>
public static class EmbeddedAssets
{
    private const string IconResourceName = "Dagger.icon.ico";

    private static byte[]? _iconBytes;
    private static string? _iconTempPath;

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
}
