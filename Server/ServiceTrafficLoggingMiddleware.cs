using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Daggeragent.Server;

public static class ServiceTrafficLoggingMiddleware
{
    private const int MaxLoggedBodyChars = 32 * 1024;

    // Header redaction (below) doesn't help the request BODY: POST /endpoints and /mcp-config
    // carry apiKey / authHeader / secret env-vars in cleartext JSON. Redact those values so they
    // don't land in the Info-level traffic log.
    private static readonly Regex SecretJsonField = new(
        "(\"(?:apiKey|authHeader|password|secret|token|apiToken)\"\\s*:\\s*)\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RedactSecrets(string body) =>
        string.IsNullOrEmpty(body) ? body : SecretJsonField.Replace(body, "$1\"<redacted>\"");

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-API-Key",
        "Proxy-Authorization",
    };

    public static IApplicationBuilder UseServiceTrafficLogging(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var log = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Daggeragent.Server.ServiceTraffic");

            var request = context.Request;
            var requestId = context.TraceIdentifier;
            var requestBody = RedactSecrets(await ReadRequestBodyAsync(request, context.RequestAborted).ConfigureAwait(false));

            log.LogInformation(
                "HTTP request in {RequestId}: {Method} {Path}{QueryString} from {RemoteIp} protocol={Protocol} scheme={Scheme} contentType={ContentType} contentLength={ContentLength} headers={Headers} body={Body}",
                requestId,
                request.Method,
                request.Path,
                request.QueryString,
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                request.Protocol,
                request.Scheme,
                request.ContentType ?? "-",
                request.ContentLength,
                FormatHeaders(request.Headers),
                requestBody);

            var response = context.Response;
            var originalBody = response.Body;
            var countingBody = new CountingResponseBodyStream(originalBody);
            response.Body = countingBody;

            var sw = Stopwatch.StartNew();
            try
            {
                await next().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                log.LogError(
                    ex,
                    "HTTP response failed {RequestId}: {Method} {Path}{QueryString} statusCode={StatusCode} bytesWritten={BytesWritten} elapsedMs={ElapsedMs:F1}",
                    requestId,
                    request.Method,
                    request.Path,
                    request.QueryString,
                    response.StatusCode,
                    countingBody.BytesWritten,
                    sw.Elapsed.TotalMilliseconds);
                throw;
            }
            finally
            {
                sw.Stop();
                response.Body = originalBody;
                log.LogInformation(
                    "HTTP response out {RequestId}: {Method} {Path}{QueryString} statusCode={StatusCode} contentType={ContentType} contentLength={ContentLength} bytesWritten={BytesWritten} elapsedMs={ElapsedMs:F1}",
                    requestId,
                    request.Method,
                    request.Path,
                    request.QueryString,
                    response.StatusCode,
                    response.ContentType ?? "-",
                    response.ContentLength,
                    countingBody.BytesWritten,
                    sw.Elapsed.TotalMilliseconds);
            }
        });
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0)
        {
            return "<empty>";
        }

        if (!IsTextLikeContent(request.ContentType))
        {
            return $"<not logged: content type {request.ContentType ?? "unknown"}>";
        }

        request.EnableBuffering();

        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        var buffer = new char[MaxLoggedBodyChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
        request.Body.Position = 0;

        if (read <= MaxLoggedBodyChars)
        {
            return new string(buffer, 0, read);
        }

        return new string(buffer, 0, MaxLoggedBodyChars) + $"... <truncated after {MaxLoggedBodyChars} chars>";
    }

    private static bool IsTextLikeContent(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> FormatHeaders(IHeaderDictionary headers)
    {
        var formatted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            formatted[key] = SensitiveHeaders.Contains(key)
                ? "<redacted>"
                : FormatHeaderValue(value);
        }

        return formatted;
    }

    private static string FormatHeaderValue(StringValues value)
    {
        return value.Count == 0 ? "" : value.ToString();
    }

    private sealed class CountingResponseBodyStream : Stream
    {
        private readonly Stream _inner;

        public CountingResponseBodyStream(Stream inner)
        {
            _inner = inner;
        }

        public long BytesWritten { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override bool CanTimeout => _inner.CanTimeout;
        public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
        public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
    }
}
