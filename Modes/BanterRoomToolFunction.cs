using System.Text.Json;
using Banter.Protocol;
using Microsoft.Extensions.AI;

namespace Daggeragent.Modes;

/// <summary>
/// One of the Banter room's own tools — <c>ask_operator</c>, the delegator's
/// <c>open_side_room</c> / <c>invite_agent</c>, or a tool the server granted — offered to the
/// model beside DaggerAgent's registry (issue #10).
///
/// <para>Invocation goes back through <c>BanterAgent.InvokeToolAsync</c>, which routes
/// local-first and then to the server's tool broker, and answers an unknown name with a result
/// naming what IS available rather than throwing. Errors come back the same way — as text the
/// model can read and route around, exactly as a pure-SDK agent experiences them — so nothing
/// here throws on a refusal.</para>
/// </summary>
public sealed class BanterRoomToolFunction : AIFunction
{
    private readonly ToolDescriptorPayload _descriptor;
    private readonly string _name;
    private readonly string _description;
    private readonly string _room;
    private readonly Func<string, string, string, CancellationToken, Task<ToolResultPayload>> _invoke;
    private readonly JsonElement _schema;

    /// <param name="invoke">(name, JSON arguments, room, ct) → the SDK's tool result.</param>
    /// <param name="name">What the model calls it, when that must differ from the room's name for
    /// it (a clash with one of DaggerAgent's own tools). Calls still go out under the room's name.</param>
    /// <param name="description">The description to offer, when it has to name a renamed tool.</param>
    public BanterRoomToolFunction(
        ToolDescriptorPayload descriptor,
        string room,
        Func<string, string, string, CancellationToken, Task<ToolResultPayload>> invoke,
        string? name = null,
        string? description = null)
    {
        _descriptor = descriptor;
        _name = name ?? descriptor.Name;
        _description = description ?? descriptor.Description;
        _room = room;
        _invoke = invoke;
        _schema = ParseSchema(descriptor.Schema);
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The SDK side wants the raw JSON arguments string; MEAI hands us the parsed dictionary.
        var argsJson = JsonSerializer.Serialize<IDictionary<string, object?>>(arguments);
        var result = await _invoke(_descriptor.Name, argsJson, _room, cancellationToken).ConfigureAwait(false);
        return result.IsError ? $"error: {result.Content}" : result.Content;
    }

    private static JsonElement ParseSchema(string schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(schema) ? "{\"type\":\"object\"}" : schema);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A server tool with a broken schema should not take the whole catalogue down;
            // an argument-free object schema at least leaves it callable.
            using var doc = JsonDocument.Parse("{\"type\":\"object\"}");
            return doc.RootElement.Clone();
        }
    }
}
