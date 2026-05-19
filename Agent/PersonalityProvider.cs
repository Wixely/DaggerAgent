using Daggeragent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Agent;

/// <summary>
/// Loads project-specific agent instructions from a markdown file (default: dagger.md)
/// in the working directory. Re-read on every CreateState so edits don't require a restart.
/// Absent file is gracefully ignored.
/// </summary>
public sealed class PersonalityProvider
{
    private readonly ToolsOptions _toolsOptions;
    private readonly AgentOptions _agentOptions;
    private readonly HostLaunchInfo _launchInfo;
    private readonly ILogger<PersonalityProvider> _log;
    private string? _lastLoggedHash;

    public PersonalityProvider(
        IOptions<ToolsOptions> toolsOptions,
        IOptions<AgentOptions> agentOptions,
        HostLaunchInfo launchInfo,
        ILogger<PersonalityProvider> log)
    {
        _toolsOptions = toolsOptions.Value;
        _agentOptions = agentOptions.Value;
        _launchInfo = launchInfo;
        _log = log;
    }

    public string LoadCurrent()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            return "";
        }

        string content;
        try
        {
            content = File.ReadAllText(path).Trim();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read personality file at {Path}", path);
            return "";
        }

        var hash = $"{path}|{content.Length}";
        if (hash != _lastLoggedHash)
        {
            _log.LogInformation("Loaded agent personality from {Path} ({Length} chars)", path, content.Length);
            _lastLoggedHash = hash;
        }
        return content;
    }

    private string ResolvePath()
    {
        var configured = _toolsOptions.WorkingDirectory;
        var root = string.IsNullOrWhiteSpace(configured)
            ? _launchInfo.OriginalWorkingDirectory
            : Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(_launchInfo.OriginalWorkingDirectory, configured));
        return Path.Combine(root, _agentOptions.PersonalityFile);
    }
}
