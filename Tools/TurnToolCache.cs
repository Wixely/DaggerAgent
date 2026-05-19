using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Per-turn memoisation of tool results. The same tool called with the same arguments
/// inside one <see cref="Agent.LlmAgent"/> turn returns the cached result instead of
/// re-invoking. A fresh instance is created per turn, so cache lifetime never crosses
/// turn boundaries.
/// </summary>
public sealed class TurnToolCache
{
    /// <summary>
    /// On the Nth identical call of the same tool+args within one turn, CachingAIFunction
    /// returns a synthetic "loop detected" result to the model instead of running the tool
    /// or replaying the cached value. Stops small models from grinding through their turn
    /// budget by repeating the same query.
    /// </summary>
    public const int LoopThreshold = 3;

    private readonly ConcurrentDictionary<string, object?> _entries = new();
    private readonly ConcurrentDictionary<string, int> _callCounts = new();

    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public int EntryCount => _entries.Count;

    public bool TryGet(string key, out object? value)
    {
        if (_entries.TryGetValue(key, out value)) { HitCount++; return true; }
        MissCount++;
        return false;
    }

    public void Set(string key, object? value) => _entries[key] = value;

    /// <summary>
    /// Increment and return the call count for this key (1 for the first call).
    /// </summary>
    public int IncrementCallCount(string key) =>
        _callCounts.AddOrUpdate(key, 1, (_, n) => n + 1);

    public static string KeyFor(string toolName, AIFunctionArguments arguments)
    {
        // AIFunctionArguments implements IEnumerable<KeyValuePair<string, object?>>.
        // Serialise a sorted snapshot so call order doesn't change the cache key.
        var dict = arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return toolName + "\0" + JsonSerializer.Serialize(dict);
    }
}

/// <summary>
/// Decorates an <see cref="AIFunction"/> with per-turn result caching. Wraps the
/// underlying tool while forwarding Name/Description/JsonSchema so the LLM sees an
/// identical tool declaration.
/// </summary>
public sealed class CachingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly TurnToolCache _cache;

    public CachingAIFunction(AIFunction inner, TurnToolCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => _inner.JsonSchema;
    public override System.Text.Json.JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var key = TurnToolCache.KeyFor(Name, arguments);
        var count = _cache.IncrementCallCount(key);
        if (count >= TurnToolCache.LoopThreshold)
        {
            return $"Loop detected: you have called '{Name}' with these exact arguments {count} times this turn. " +
                   "The result will not change. Stop calling this tool — answer with what you have, " +
                   "try different arguments, or use a different approach.";
        }
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }
        var result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, result);
        return result;
    }
}
