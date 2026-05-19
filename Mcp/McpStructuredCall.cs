using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Daggeragent.Mcp;

/// <summary>
/// Calls an MCP tool and unwraps its first text content block as JSON of type T.
/// Used by the polling layer for tools we own on both sides (so we can rely on the
/// JSON shape) — bypasses the LLM entirely.
/// </summary>
public static class McpStructuredCall
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<T?> CallAsync<T>(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(toolName, args ?? new Dictionary<string, object?>(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError == true)
        {
            var msg = ExtractTextContent(result);
            throw new InvalidOperationException($"MCP tool '{toolName}' returned an error: {msg}");
        }

        // Prefer StructuredContent if the server populated it; fall back to text.
        if (result.StructuredContent is JsonElement structured)
        {
            return structured.Deserialize<T>(Json);
        }

        var text = ExtractTextContent(result);
        if (string.IsNullOrWhiteSpace(text)) return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    private static string ExtractTextContent(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0) return "";
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock tc && !string.IsNullOrEmpty(tc.Text))
                return tc.Text;
        }
        return "";
    }
}
