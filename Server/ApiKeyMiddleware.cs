using Daggeragent.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

public static class ApiKeyMiddleware
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

            // Disabled when no keys are configured — keeps dev/localhost frictionless.
            if (options.ApiKeys.Count == 0)
            {
                await next();
                return;
            }

            var path = context.Request.Path.Value ?? "";
            // Bypass on exact match, or on segment-match (`/foo` matches `/foo/anything`).
            // Plain prefix match would let `/` short-circuit every request — that's a foot-gun
            // since we want the landing page reachable but not everything-else-unauthenticated.
            if (options.BypassPaths.Any(b =>
                path.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                (b.Length > 1 && path.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase))))
            {
                await next();
                return;
            }

            // Accept the configured header or a Bearer-style Authorization header (some OpenAI
            // and Ollama clients send credentials there even when the upstream doesn't require it).
            string? presented = context.Request.Headers[options.HeaderName].FirstOrDefault();
            if (string.IsNullOrEmpty(presented))
            {
                var authz = context.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrEmpty(authz) && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    presented = authz.Substring("Bearer ".Length).Trim();
                }
            }

            if (string.IsNullOrEmpty(presented) || !options.ApiKeys.Contains(presented, StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":{{\"message\":\"Missing or invalid API key. Send it in the '{options.HeaderName}' header or as 'Authorization: Bearer <key>'.\",\"type\":\"unauthorized\"}}}}");
                return;
            }

            await next();
        });
    }
}
