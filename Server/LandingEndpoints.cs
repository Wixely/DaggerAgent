using Daggeragent.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

public static class LandingEndpoints
{
    public static IEndpointRouteBuilder MapLanding(this IEndpointRouteBuilder app)
    {
        app.MapGet("/favicon.ico", () => Results.File(EmbeddedAssets.IconBytes, "image/x-icon"));

        app.MapGet("/", (IOptions<ServerOptions> server, IOptions<OpenAIOptions> openAi) =>
        {
            var html = BuildLandingHtml(server.Value.Path, openAi.Value);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        return app;
    }

    private static string BuildLandingHtml(string agentBasePath, OpenAIOptions openAi)
    {
        var endpoints = new (string Verb, string Path, string What)[]
        {
            ("GET",  $"{agentBasePath}/healthz",        "Liveness + dep checks"),
            ("GET",  $"{agentBasePath}/jobs",           "List recent jobs"),
            ("POST", $"{agentBasePath}/jobs",           "Start a new job"),
            ("GET",  $"{agentBasePath}/jobs/{{id}}",    "Inspect a job"),
            ("POST", $"{agentBasePath}/jobs/{{id}}/messages", "Continue a job"),
            ("POST", $"{agentBasePath}/jobs/{{id}}/resume",   "Resume a paused job"),
            ("DELETE", $"{agentBasePath}/jobs/{{id}}",  "Cancel/delete a job"),
            ("GET",  "/v1/models",                       "OpenAI-compat: list models"),
            ("POST", "/v1/chat/completions",             "OpenAI-compat: chat (stream supported)"),
            ("GET",  "/api/version",                     "Ollama-compat: version"),
            ("GET",  "/api/tags",                        "Ollama-compat: list models"),
            ("POST", "/api/chat",                        "Ollama-compat: chat (stream supported)"),
            ("POST", "/api/generate",                    "Ollama-compat: completion (stream supported)"),
        };

        var rows = string.Join("\n",
            endpoints.Select(e =>
                $"<tr><td><code>{e.Verb}</code></td><td><code>{System.Net.WebUtility.HtmlEncode(e.Path)}</code></td><td>{e.What}</td></tr>"));

        return $@"<!doctype html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <title>DaggerAgent</title>
  <link rel=""icon"" type=""image/x-icon"" href=""/favicon.ico"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <style>
    body {{ font-family: ui-sans-serif, system-ui, sans-serif; max-width: 860px; margin: 2em auto; padding: 0 1em; color: #1a1a1a; line-height: 1.5; }}
    header {{ display: flex; align-items: center; gap: 0.75em; margin-bottom: 1.5em; }}
    header img {{ width: 48px; height: 48px; }}
    h1 {{ margin: 0; font-size: 1.6em; }}
    .meta {{ color: #666; font-size: 0.95em; margin-top: 0.25em; }}
    table {{ border-collapse: collapse; width: 100%; margin-top: 1em; }}
    th, td {{ text-align: left; padding: 0.45em 0.7em; border-bottom: 1px solid #eee; vertical-align: top; }}
    th {{ background: #f6f6f6; font-weight: 600; }}
    code {{ font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.95em; }}
    td:first-child {{ width: 5em; }}
    td:nth-child(2) {{ width: 22em; }}
    footer {{ color: #888; font-size: 0.85em; margin-top: 2em; }}
  </style>
</head>
<body>
  <header>
    <img src=""/favicon.ico"" alt=""DaggerAgent"">
    <div>
      <h1>DaggerAgent</h1>
      <div class=""meta"">upstream: <code>{System.Net.WebUtility.HtmlEncode(openAi.Provider)}</code> @ <code>{System.Net.WebUtility.HtmlEncode(openAi.BaseUrl)}</code>
        &middot; model: <code>{System.Net.WebUtility.HtmlEncode(openAi.DefaultModel)}</code></div>
    </div>
  </header>

  <p>Service mode is live. Available endpoints:</p>

  <table>
    <thead><tr><th>verb</th><th>path</th><th>what</th></tr></thead>
    <tbody>
{rows}
    </tbody>
  </table>

  <footer>If <code>Auth:ApiKeys</code> is non-empty, every endpoint except <code>/</code>, <code>/favicon.ico</code>, <code>/agent/healthz</code>, <code>/v1/models</code>, <code>/api/version</code> requires <code>X-Api-Key</code> or <code>Authorization: Bearer</code>.</footer>
</body>
</html>";
    }
}
