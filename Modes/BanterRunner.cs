using Banter.Protocol.Transport;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Modes;

/// <summary>
/// Banter mode: `dagger banter` runs DaggerAgent as an agent inside a Banter room server, and
/// `dagger banter --enrol &lt;code&gt;` redeems a one-time enrolment code into this machine's
/// private key. Enrolment is deliberately its own invocation rather than something the run path
/// does lazily — it happens once, on purpose, while somebody is watching.
/// </summary>
public sealed class BanterRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BanterRunner> _log;

    public BanterRunner(IServiceProvider services, ILogger<BanterRunner> log)
    {
        _services = services;
        _log = log;
    }

    /// <summary>Whether this invocation is the one-shot enrolment, which needs no MCP host.</summary>
    public static bool IsEnrolInvocation(string[] args) =>
        args.Any(a => a.Equals("--enrol", StringComparison.OrdinalIgnoreCase));

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = _services.GetRequiredService<IOptions<BanterOptions>>().Value;

        string? Arg(string name)
        {
            var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.Error.WriteLine("""
                dagger banter - run DaggerAgent as an agent in a Banter room server

                  dagger banter --enrol <code> [--key <path>] [--server tcp://host:port]
                      Redeem a one-time enrolment code from the Banter desktop client's agents
                      page. Generates this machine's keypair, sends only the public half, and
                      keeps the private key at <path> (DPAPI-protected on Windows). Prints the
                      identity and key fingerprint, then exits. The code is spent either way.

                  dagger banter [--server <uri>] [--user <name>] [--key <path> | --pass <secret>]
                                [--rooms #a,#b]
                      Connect and answer. Settings come from the Banter section of appsettings
                      (server, user, rooms, key file, routing attributes, model); flags override.

                An enrolled key is preferred over a password: the credential never travels, a
                captured login cannot be replayed, and revocation is immediate.
                """);
            return 1;
        }

        var server = Arg("--server") ?? options.Server;
        var keyPath = Arg("--key") ?? options.KeyFile;

        if (Arg("--enrol") is { Length: > 0 } code)
        {
            return await EnrolAsync(server, code, keyPath, cancellationToken).ConfigureAwait(false);
        }

        return await RunAgentAsync(options, args, Arg, server, keyPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> EnrolAsync(string server, string code, string keyPath, CancellationToken cancellationToken)
    {
        try
        {
            var (identity, savedPath) = await BanterSetup.EnrolAsync(server, code, keyPath, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"Enrolled as '{identity.Nick}'.");
            Console.WriteLine($"  key        {savedPath}" +
                              (OperatingSystem.IsWindows() ? " (DPAPI-protected for this Windows user)" : ""));
            Console.WriteLine($"  identifies {identity.KeyFingerprint}");
            Console.WriteLine($"  rooms      {string.Join(", ", identity.Rooms)}");
            Console.WriteLine();
            Console.WriteLine("The code is spent. This key is what identifies the agent now, it never leaves this");
            Console.WriteLine($"machine, and an admin can revoke it at any time. Run `dagger banter` to connect" +
                              $"{(identity.Nick == "dagger" ? "" : $" (set Banter:User to \"{identity.Nick}\")")}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or System.Net.Sockets.SocketException or ArgumentException or UriFormatException)
        {
            Console.Error.WriteLine($"error: could not enrol: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> RunAgentAsync(
        BanterOptions options, string[] args, Func<string, string?> arg,
        string server, string keyPath, CancellationToken cancellationToken)
    {
        var user = arg("--user") ?? options.User;
        var password = arg("--pass") ?? options.Password;
        var rooms = (arg("--rooms") ?? arg("--room"))
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? (options.Rooms.Count > 0 ? options.Rooms.ToArray() : ["#main"]);

        // Explicit-flag rules first: --key and --pass together is refused, and either one alone
        // forces its route. With neither, BanterSetup applies the config rules (a password AND
        // an existing key file is refused; no password means the key must exist and validate).
        var explicitKey = arg("--key") is not null;
        var explicitPass = arg("--pass") is not null;
        if (explicitKey && explicitPass)
        {
            Console.Error.WriteLine("error: --key and --pass are two different identities; pass one.");
            return 1;
        }

        var (privateKey, credentialError) = await BanterSetup
            .ResolveCredentialAsync(explicitKey ? "" : password, explicitPass ? "" : keyPath, cancellationToken)
            .ConfigureAwait(false);
        if (credentialError is not null)
        {
            Console.Error.WriteLine($"error: {credentialError}");
            return 1;
        }

        var agentDefaults = _services.GetRequiredService<IOptions<AgentOptions>>().Value;
        var model = BanterSetup.DisplayModel(options);
        var systemPrompt = BanterSetup.BuildSystemPrompt(options, agentDefaults);
        var agentOptions = BanterSetup.BuildAgentOptions(options, server, user, password, privateKey, rooms, model);

        var store = _services.GetRequiredService<IJobStore>();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<MemoryStore>().InitializeAsync(cancellationToken).ConfigureAwait(false);

        var llmAgent = _services.GetRequiredService<LlmAgent>();
        await using var agent = new DaggerBanterAgent(
            agentOptions, llmAgent, options.Model, options.EndpointId, systemPrompt, _log);
        agent.TurnStarted += (room, sender) => _log.LogInformation("[{Room}] answering {Sender}...", room, sender);

        try
        {
            await agent.StartAsync(BanterTransports.Client(agentOptions.Server), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: could not connect to {server} as {user}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"{user} is in {string.Join(", ", rooms)} on {server}, answering with {model}.");
        Console.WriteLine($"Announced as {agentOptions.Locality}, clearance {agentOptions.Clearance}, " +
                          $"skills [{string.Join(", ", agentOptions.Skills)}].");
        if (privateKey is not null)
        {
            Console.WriteLine($"Authenticated with the enrolled key ({BanterSetup.Fingerprint(privateKey)}).");
        }
        else
        {
            Console.WriteLine("Authenticated with a password. Consider enrolling: dagger banter --enrol <code>.");
        }
        Console.WriteLine("Press Ctrl+C to stop.");

        await agent.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
