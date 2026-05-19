namespace Daggeragent.Configuration;

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>
    /// Per-model pricing in USD per million tokens. Keyed by model id (matched
    /// case-insensitively). A model not in this map contributes 0 cost. Local models
    /// like LM Studio / Ollama should typically be left out (free).
    /// </summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelPrice
{
    /// <summary> USD per 1,000,000 input tokens. </summary>
    public decimal InputPerMillion { get; set; }

    /// <summary> USD per 1,000,000 output tokens. </summary>
    public decimal OutputPerMillion { get; set; }
}
