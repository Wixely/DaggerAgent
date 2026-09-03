using System.Security.Cryptography;
using Banter.Client.Core;

namespace Daggeragent.Modes;

/// <summary>
/// The Banter agent's private key at rest.
///
/// <para>On Windows the PKCS#8 blob is wrapped with DPAPI for the current user before it touches
/// disk, so a copied file or a stolen disk yields nothing — the SDK's <see cref="AgentKeyFile"/>
/// deliberately leaves that improvement to hosts, since user-only file permissions are a no-op
/// on Windows. Elsewhere it defers to <see cref="AgentKeyFile"/>, which writes user-only POSIX
/// permissions. Loading accepts both shapes, so a key enrolled by banter-warden still works.</para>
/// </summary>
public static class BanterKeyStore
{
    /// <summary>Marks a DPAPI-wrapped key file; raw PKCS#8 never starts with this.</summary>
    private static readonly byte[] Magic = "DGRBKEY1"u8.ToArray();

    public static async Task SaveAsync(string path, byte[] privateKey, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var wrapped = ProtectedData.Protect(privateKey, Magic, DataProtectionScope.CurrentUser);
            var bytes = new byte[Magic.Length + wrapped.Length];
            Magic.CopyTo(bytes, 0);
            wrapped.CopyTo(bytes, Magic.Length);
            await AgentKeyFile.SaveAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        await AgentKeyFile.SaveAsync(path, privateKey, cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="InvalidOperationException">The file is not a key this machine can use, with the reason.</exception>
    public static async Task<byte[]> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        if (bytes.AsSpan().StartsWith(Magic))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    $"{path} is DPAPI-protected and only opens on the Windows account that enrolled it. " +
                    "Enrol this machine with its own code instead of copying the file.");
            }

            try
            {
                return ProtectedData.Unprotect(bytes[Magic.Length..], Magic, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"{path} could not be unwrapped — it belongs to a different Windows user or machine. " +
                    "Enrol this machine with its own code instead of copying the file.", ex);
            }
        }

        return bytes;
    }

    /// <summary>
    /// Whether the file holds a key this process could sign with, checked before connecting so a
    /// truncated or foreign file is reported as itself rather than as "invalid credentials",
    /// which would send someone to the server logs for a local problem.
    /// </summary>
    public static async Task<string?> ValidateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return $"key file {path} does not exist. Enrol first: dagger banter --enrol <code>";
        }

        byte[] key;
        try
        {
            key = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (IOException ex)
        {
            return $"could not read {path}: {ex.Message}";
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(key, out _);
            return null;
        }
        catch (CryptographicException)
        {
            return $"{path} exists but does not hold a usable key — it may be truncated or corrupted. " +
                   "Move it aside and enrol again with a fresh code.";
        }
    }
}
