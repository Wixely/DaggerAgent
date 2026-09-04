using System.Security.Cryptography;
using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Daggeragent.Configuration;

namespace Daggeragent.Modes;

/// <summary>
/// The shared half of Banter mode: credential resolution, option building and enrolment, used by
/// both the standalone runner (`dagger banter`) and the in-service connection the web UI manages.
/// The two callers differ only in where overrides come from (CLI flags vs. runtime config) and
/// where errors go (stderr vs. an HTTP response), so everything else lives here once.
/// </summary>
public static class BanterSetup
{
    /// <summary>
    /// Resolve the login credential from configuration alone. Exactly one route: a password AND
    /// an existing key file is refused rather than ranked — whichever silently won, the other
    /// would be the stale credential nobody noticed. With no password, the key file must exist
    /// and validate. Returns the private key (null on the password route) or an error message.
    /// </summary>
    public static async Task<(byte[]? PrivateKey, string? Error)> ResolveCredentialAsync(
        string password, string keyPath, CancellationToken cancellationToken = default)
    {
        if (password.Length > 0 && File.Exists(keyPath))
        {
            return (null,
                $"both Banter:Password and a key file at {Path.GetFullPath(keyPath)} are configured. " +
                "Clear the password (the enrolled key is the better credential) or move the key aside.");
        }

        if (password.Length > 0)
        {
            return (null, null);
        }

        if (await BanterKeyStore.ValidateAsync(keyPath, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return (null, problem);
        }

        return (await BanterKeyStore.LoadAsync(keyPath, cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>
    /// What to call the model in banners, descriptions and status. The real resolution happens in
    /// ChatClientFactory (empty model = the endpoint's own default), so this is display only.
    /// </summary>
    public static string DisplayModel(BanterOptions options) =>
        options.Model.Length > 0 ? options.Model : "(endpoint default)";

    /// <summary>
    /// System prompt for room conversations. The room is a shared, multi-party space; the default
    /// single-user prompt would have the agent addressing "you" and dumping walls of text into it.
    /// </summary>
    public static string BuildSystemPrompt(BanterOptions options, AgentOptions agentDefaults) =>
        options.SystemPrompt.Length > 0
            ? options.SystemPrompt
            : agentDefaults.SystemPrompt +
              "\n\nYou are speaking in a shared chat room with several humans and agents. " +
              "Messages arrive prefixed with the sender's name. Keep replies short and " +
              "conversational — a sentence or two unless asked for detail — and do not prefix " +
              "your replies with your own name.";

    public static AgentLocality ParseLocality(string value) => value.Trim().ToLowerInvariant() switch
    {
        "local" => AgentLocality.Local,
        "frontier" => AgentLocality.Frontier,
        _ => AgentLocality.Unknown,
    };

    public static DataSensitivity ParseClearance(string value) => value.Trim().ToLowerInvariant() switch
    {
        "public" => DataSensitivity.Public,
        "internal" => DataSensitivity.Internal,
        "sensitive" => DataSensitivity.Sensitive,
        _ => DataSensitivity.Unknown,
    };

    /// <summary>SDK options from the configured shape plus the already-resolved pieces.
    /// <paramref name="model"/> is the display label, used only for the roster description.</summary>
    public static BanterAgentOptions BuildAgentOptions(
        BanterOptions options, string server, string user, string password, byte[]? privateKey,
        IReadOnlyList<string> rooms, string model) => new()
    {
        Server = new Uri(server),
        User = user,
        Password = privateKey is null ? password : "",
        PrivateKey = privateKey,
        Rooms = rooms,
        ClientName = "DaggerAgent",
        RespondToEveryMessage = options.RespondToEveryMessage,
        Locality = ParseLocality(options.Locality),
        Clearance = ParseClearance(options.Clearance),
        Skills = options.Skills.Count > 0 ? options.Skills : ["code", "tools"],
        Description = options.Description.Length > 0 ? options.Description : $"DaggerAgent ({model})",
        CostTier = options.CostTier,
        WantsDelegator = options.WantsDelegator,
    };

    /// <summary>Effective room list: the configured one, or #main when nothing is configured.</summary>
    public static string[] ResolveRooms(BanterOptions options) =>
        options.Rooms.Count > 0 ? options.Rooms.ToArray() : ["#main"];

    /// <summary>
    /// Redeem a one-time enrolment code and keep the key at <paramref name="keyPath"/>.
    /// An existing key file is refused: overwriting would strand the identity it belongs to —
    /// the server still has its public half and nothing else can produce the private one.
    /// </summary>
    /// <exception cref="InvalidOperationException">Refused locally (key exists) or by the server.</exception>
    public static async Task<(AgentIdentityPayload Identity, string KeyPath)> EnrolAsync(
        string server, string code, string keyPath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(keyPath))
        {
            throw new InvalidOperationException(
                $"{Path.GetFullPath(keyPath)} already exists. Move it aside first if you mean to replace it.");
        }

        var endpoint = new Uri(server);
        var (identity, privateKey) = await AgentEnrolment
            .EnrolAsync(BanterTransports.Client(endpoint), endpoint, code, cancellationToken)
            .ConfigureAwait(false);

        await BanterKeyStore.SaveAsync(keyPath, privateKey, cancellationToken).ConfigureAwait(false);
        return (identity, Path.GetFullPath(keyPath));
    }

    /// <summary>The public half's fingerprint, so an operator can match this machine against the
    /// agents page without touching the private key.</summary>
    public static string Fingerprint(byte[] privateKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKey, out _);
        return AgentKeys.Fingerprint(ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Fingerprint of the key at <paramref name="keyPath"/>, or null when the file is absent or
    /// unusable. For status displays only — real errors surface through
    /// <see cref="ResolveCredentialAsync"/> at connect time.
    /// </summary>
    public static async Task<string?> TryFingerprintAsync(string keyPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await BanterKeyStore.ValidateAsync(keyPath, cancellationToken).ConfigureAwait(false) is not null)
                return null;
            return Fingerprint(await BanterKeyStore.LoadAsync(keyPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or CryptographicException)
        {
            return null;
        }
    }
}
