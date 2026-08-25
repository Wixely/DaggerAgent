using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Publishes a <see cref="ToolCallEvent"/> either side of a tool invocation, so a host that owns
/// the UI can show what the agent is doing.
///
/// <para>Applied as the OUTERMOST wrapper in <see cref="Agent.LlmAgent"/>'s tool chain
/// (<c>Notifying(Offloading(Caching(f)))</c>) for two reasons: the reported duration then covers
/// the whole call as the model experiences it, including the cache lookup and any result
/// offloading; and caching stays a caching concern rather than doubling as observability.</para>
///
/// <para>A consequence worth knowing: because <see cref="CachingAIFunction"/> sits inside, a
/// cache hit and a loop-detection short-circuit still raise a normal started/completed pair.
/// That is intended — the model did call the tool, and a host showing activity should say so —
/// but it means an event is not proof the underlying tool actually ran.</para>
/// </summary>
public sealed class NotifyingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IToolCallSink _sink;
    private readonly string _jobId;
    private readonly int _depth;

    public NotifyingAIFunction(AIFunction inner, IToolCallSink sink, string jobId, int depth)
    {
        _inner = inner;
        _sink = sink;
        _jobId = jobId;
        _depth = depth;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var started = new ToolCallEvent(_jobId, _depth, Name, ToolCallSink.DigestArgs(arguments));
        _sink.Started(started);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _sink.Completed(started with
            {
                Elapsed = sw.Elapsed,
                Succeeded = true,
                ResultChars = ToolResultText.Length(result),
            });
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Report the failure, then let it propagate unchanged — this decorator observes.
            _sink.Completed(started with
            {
                Elapsed = sw.Elapsed,
                Succeeded = false,
                ResultChars = 0,
                Error = ex.GetType().Name,
            });
            throw;
        }
    }
}
