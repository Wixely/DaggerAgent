using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Decorator that intercepts any string tool result larger than the configured threshold
/// and stashes it in <see cref="ToolResultStore"/>. The model receives a short placeholder
/// pointing at the result id + a preview, and reads the rest via the dedicated
/// <c>read_tool_result</c> / <c>head_tool_result</c> / <c>tail_tool_result</c> /
/// <c>grep_tool_result</c> / <c>list_tool_results</c> tools.
///
/// Tools whose <i>job</i> is to consume offloaded results must NOT themselves be wrapped —
/// otherwise reading a 16K slice would offload its own response. The set of consumer tool
/// names lives on <see cref="ToolResultTools.ConsumerToolNames"/> and the wrap helper
/// <see cref="ToolResultTools.ShouldOffload"/> exists so the wrap site doesn't have to
/// duplicate the list.
/// </summary>
public sealed class OffloadingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly ToolResultStore _store;
    private readonly string _jobId;
    private readonly int _threshold;

    public OffloadingAIFunction(AIFunction inner, ToolResultStore store, string jobId, int threshold)
    {
        _inner = inner;
        _store = store;
        _jobId = jobId;
        _threshold = threshold;
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
        var result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        var text = result as string;
        if (text is null || text.Length <= _threshold) return result;

        var entry = _store.Save(_jobId, _inner.Name, text);
        var preview = text.Length > 400 ? text[..400] + "…(truncated)" : text;
        return
            $"[Offload] '{_inner.Name}' returned {text.Length:N0} chars — too large to inline. " +
            $"Saved as result id '{entry.ResultId}'. " +
            $"Read with: read_tool_result(id='{entry.ResultId}', offset=0, limit=2000), " +
            $"head_tool_result(id='{entry.ResultId}', lines=50), " +
            $"tail_tool_result(id='{entry.ResultId}', lines=50), or " +
            $"grep_tool_result(id='{entry.ResultId}', pattern='...'). " +
            $"List all stashed results with list_tool_results().\n\n" +
            $"Preview (first {Math.Min(400, text.Length)} of {text.Length:N0} chars):\n{preview}";
    }
}
