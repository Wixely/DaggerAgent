using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class WebTools
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static WebTools()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaggerAgent/1.0 (+https://github.com/Wixely/DaggerAgent)");
    }

    private readonly WebOptions _options;

    public WebTools(IOptions<WebOptions> options)
    {
        _options = options.Value;
    }

    public IEnumerable<AITool> Build()
    {
        yield return AIFunctionFactory.Create(HttpGet, name: "http_get", description:
            "Fetch a URL via HTTP GET. Text responses (text/*, application/json, application/xml, etc.) " +
            "are returned as UTF-8. Binary responses (image/*, application/octet-stream, audio, video, etc.) " +
            "are returned base64-encoded with a 'base64:' marker — pass the base64 onward to a vision-capable " +
            "model or write_file to save it. Bounded by maxBytes. Read-only.");

        yield return AIFunctionFactory.Create(HttpGetBytes, name: "http_get_bytes", description:
            "Fetch a URL via HTTP GET and return the body as base64 unconditionally — no Content-Type " +
            "detection, no chance of text-decoding mangling the bytes. Use this as a fallback when http_get " +
            "returns corrupted-looking content (server sent the wrong Content-Type, missing charset, etc.) " +
            "or when you specifically need raw bytes (images, PDFs, archives). Read-only.");
    }

    [Description("HTTP GET a URL.")]
    private async Task<string> HttpGet(
        [Description("Full URL (http or https).")] string url,
        [Description("Maximum number of response bytes to read. Default 200000.")] int maxBytes = 200_000,
        [Description("Request timeout in seconds. Default 30.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var fetch = await FetchAsync(url, maxBytes, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (fetch.Error is not null) return fetch.Error;

        var isText = IsTextual(fetch.ContentType, fetch.Body!);
        var sb = BuildHeader(fetch, encoding: isText ? "text" : "base64");
        if (isText)
            sb.Append(ResolveTextEncoding(fetch.ContentType).GetString(fetch.Body!));
        else
            sb.Append("base64:").Append(Convert.ToBase64String(fetch.Body!));
        return sb.ToString();
    }

    [Description("HTTP GET a URL and always return base64.")]
    private async Task<string> HttpGetBytes(
        [Description("Full URL (http or https).")] string url,
        [Description("Maximum number of response bytes to read. Default 200000.")] int maxBytes = 200_000,
        [Description("Request timeout in seconds. Default 30.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var fetch = await FetchAsync(url, maxBytes, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (fetch.Error is not null) return fetch.Error;

        var sb = BuildHeader(fetch, encoding: "base64");
        sb.Append("base64:").Append(Convert.ToBase64String(fetch.Body!));
        return sb.ToString();
    }

    private sealed record FetchResult(
        int StatusCode,
        MediaTypeHeaderValue? ContentType,
        long? ContentLength,
        byte[]? Body,
        bool Truncated,
        int MaxBytes,
        string? Error);

    private async Task<FetchResult> FetchAsync(string url, int maxBytes, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new FetchResult(0, null, null, null, false, maxBytes, "Error: only http/https URLs are accepted.");
            }

            // Clamp agent-supplied caps to server-side policy maxes so the LLM can't override
            // them upward to fetch gigabytes or hold connections open for minutes.
            maxBytes = Math.Clamp(maxBytes, 1, _options.MaxResponseBytes);
            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, _options.MaxTimeoutSeconds);

            // Host-pattern allowlist / blocklist (applied to the literal hostname before DNS).
            var host = uri.Host;
            if (_options.AllowedHostPatterns.Count > 0 &&
                !_options.AllowedHostPatterns.Any(p => host.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return new FetchResult(0, null, null, null, false, maxBytes,
                    $"Error: host '{host}' not in Web.AllowedHostPatterns allowlist.");
            }
            if (_options.BlockedHostPatterns.Any(p => host.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return new FetchResult(0, null, null, null, false, maxBytes,
                    $"Error: host '{host}' matches Web.BlockedHostPatterns denylist.");
            }

            // Private-network guard: resolve hostname (or parse if it's already an IP literal)
            // and reject if any candidate IP falls in a private range. Default-deny so SSRF
            // against localhost / RFC1918 / cloud metadata requires explicit opt-in.
            if (!_options.AllowPrivateNetworks)
            {
                IPAddress[] addrs;
                try
                {
                    addrs = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new FetchResult(0, null, null, null, false, maxBytes,
                        $"Error: DNS resolution failed for '{host}': {ex.Message}");
                }
                foreach (var addr in addrs)
                {
                    if (IsPrivateAddress(addr))
                    {
                        return new FetchResult(0, null, null, null, false, maxBytes,
                            $"Error: '{host}' resolves to private network address {addr}. " +
                            $"Set Web.AllowPrivateNetworks=true to permit.");
                    }
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
            var total = 0;
            using var ms = new MemoryStream();
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
                total += read;
            }
            return new FetchResult(
                StatusCode: (int)resp.StatusCode,
                ContentType: resp.Content.Headers.ContentType,
                ContentLength: resp.Content.Headers.ContentLength,
                Body: ms.ToArray(),
                Truncated: total >= maxBytes,
                MaxBytes: maxBytes,
                Error: null);
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(0, null, null, null, false, maxBytes, $"Error: request timed out after {timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return new FetchResult(0, null, null, null, false, maxBytes, $"Error: {ex.Message}");
        }
    }

    private static StringBuilder BuildHeader(FetchResult fetch, string encoding)
    {
        var sb = new StringBuilder();
        sb.Append("http_status: ").AppendLine(fetch.StatusCode.ToString());
        sb.Append("content_type: ").AppendLine(fetch.ContentType?.ToString() ?? "");
        if (fetch.ContentLength is { } cl) sb.Append("content_length: ").AppendLine(cl.ToString());
        sb.Append("encoding: ").AppendLine(encoding);
        sb.Append("body_bytes: ").AppendLine((fetch.Body?.Length ?? 0).ToString());
        if (fetch.Truncated) sb.Append("truncated_at: ").AppendLine(fetch.MaxBytes.ToString());
        sb.AppendLine("---body---");
        return sb;
    }

    /// <summary>
    /// Decide whether the response body should be returned as decoded text or base64-encoded.
    /// Prefers the declared Content-Type; falls back to a NUL-byte sniff for missing/wrong headers.
    /// </summary>
    private static bool IsTextual(MediaTypeHeaderValue? ct, ReadOnlySpan<byte> bytes)
    {
        var mt = ct?.MediaType?.ToLowerInvariant();
        if (mt is not null)
        {
            if (mt.StartsWith("text/", StringComparison.Ordinal)) return true;
            if (mt.StartsWith("image/", StringComparison.Ordinal)) return false;
            if (mt.StartsWith("audio/", StringComparison.Ordinal)) return false;
            if (mt.StartsWith("video/", StringComparison.Ordinal)) return false;
            if (mt.StartsWith("font/", StringComparison.Ordinal)) return false;
            if (mt is "application/octet-stream" or "application/pdf" or "application/zip"
                  or "application/gzip" or "application/x-tar" or "application/wasm") return false;
            if (mt.StartsWith("application/", StringComparison.Ordinal))
            {
                // Most application/* with a structured suffix or known text subtype is text.
                if (mt.EndsWith("+json", StringComparison.Ordinal) ||
                    mt.EndsWith("+xml", StringComparison.Ordinal)  ||
                    mt.EndsWith("+yaml", StringComparison.Ordinal) ||
                    mt is "application/json" or "application/xml" or "application/javascript"
                       or "application/ecmascript" or "application/x-yaml" or "application/yaml"
                       or "application/x-www-form-urlencoded" or "application/xhtml+xml")
                    return true;
            }
        }
        // No usable hint: sniff for NUL bytes in the first 1KB — a reliable binary marker.
        var probe = bytes.Length > 1024 ? bytes[..1024] : bytes;
        foreach (var b in probe)
        {
            if (b == 0) return false;
        }
        return true;
    }

    private static Encoding ResolveTextEncoding(MediaTypeHeaderValue? ct)
    {
        if (ct?.CharSet is { } cs)
        {
            try { return Encoding.GetEncoding(cs.Trim('"')); } catch { /* fall through */ }
        }
        return Encoding.UTF8;
    }

    /// <summary>
    /// Decide whether an IP belongs to a range that should be unreachable to LLM-driven
    /// outbound fetches by default. Covers RFC1918, CGNAT, loopback, link-local
    /// (incl. AWS/Azure/GCP metadata at 169.254.169.254), benchmark, multicast, IPv6
    /// loopback / link-local / ULA / site-local / multicast / unspecified.
    /// </summary>
    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0.0.0.0/8 (this network) — also catches the IPAddress.Any sentinel
            if (b[0] == 0) return true;
            // 10.0.0.0/8 — RFC1918
            if (b[0] == 10) return true;
            // 100.64.0.0/10 — CGNAT
            if (b[0] == 100 && (b[1] & 0xC0) == 0x40) return true;
            // 127.0.0.0/8 — loopback
            if (b[0] == 127) return true;
            // 169.254.0.0/16 — link-local (includes 169.254.169.254 cloud metadata)
            if (b[0] == 169 && b[1] == 254) return true;
            // 172.16.0.0/12 — RFC1918
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16 — RFC1918
            if (b[0] == 192 && b[1] == 168) return true;
            // 198.18.0.0/15 — benchmark testing
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            // 224.0.0.0/4 — multicast
            if ((b[0] & 0xF0) == 0xE0) return true;
            // 255.255.255.255 — broadcast
            if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6UniqueLocal) return true;
            if (ip.IsIPv6Multicast) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            return false;
        }
        return false;
    }
}
