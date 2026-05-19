namespace Daggeragent.Configuration;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// Allow http_get / http_get_bytes to reach private network ranges: loopback,
    /// RFC1918 (10/8, 172.16/12, 192.168/16), link-local (169.254/16 — includes AWS/Azure/GCP
    /// metadata at 169.254.169.254), CGNAT (100.64/10), benchmark (198.18/15), multicast,
    /// IPv6 ULA + link-local. Default false — strongly recommended off for any
    /// internet-facing or shared deployment so the LLM can't be tricked into SSRF against
    /// your internal services. Set true only when the agent specifically needs to fetch
    /// from localhost or an intranet.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>
    /// Hard cap on the maxBytes parameter agents can pass to http_get / http_get_bytes.
    /// Whatever the agent asks for is clamped to Min(requested, MaxResponseBytes).
    /// Default 5 MiB.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the timeoutSeconds parameter agents can pass. Default 60.
    /// </summary>
    public int MaxTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Optional case-insensitive substring patterns. If non-empty, the request's hostname
    /// MUST contain at least one of these patterns or the fetch is refused. Use for tight
    /// policies like "only fetch from github.com or our own docs site".
    /// </summary>
    public List<string> AllowedHostPatterns { get; set; } = new();

    /// <summary>
    /// Optional case-insensitive substring patterns the agent is NOT allowed to target.
    /// Applied after AllowedHostPatterns and after the private-network check. Useful for
    /// blocking specific known-private hostnames that aren't caught by IP inspection
    /// (e.g. an internal *.corp domain that resolves via a custom DNS).
    /// </summary>
    public List<string> BlockedHostPatterns { get; set; } = new();
}
