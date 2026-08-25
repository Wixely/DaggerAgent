using System.Text.Json;

namespace Daggeragent.Tools;

/// <summary>
/// Gets the text out of a tool result.
///
/// <para>Needed because <c>AIFunctionFactory</c> marshals return values through
/// System.Text.Json: a tool declared as returning <c>string</c> hands back a
/// <see cref="JsonElement"/> of kind String, not a <c>string</c>. Anything doing
/// <c>result as string</c> therefore gets null for every built-in tool — which is why result
/// offloading silently never fired despite a 16 000-char threshold.</para>
/// </summary>
internal static class ToolResultText
{
    /// <summary>
    /// The result as text, or null when there is nothing to show. A JSON string is unwrapped to
    /// its value; any other JSON shape returns its raw text, since a large object or array costs
    /// the same context as a large string.
    /// </summary>
    public static string? AsText(object? result) => result switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement je => je.GetRawText(),
        _ => result.ToString(),
    };

    /// <summary>Length of <see cref="AsText"/>, or 0 when there is no text.</summary>
    public static int Length(object? result) => AsText(result)?.Length ?? 0;
}
