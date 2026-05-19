namespace Daggeragent.Configuration;

public sealed class MemoryOptions
{
    public const string SectionName = "Memory";

    /// <summary>
    /// Enable cross-session memory. When true, the agent gains a `recall_past_work` tool
    /// and every successful compression of a conversation is embedded and stored.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Embedding model id. For OpenAI provider try "text-embedding-3-small"; for Ollama
    /// try "nomic-embed-text" or any other embeddings model you've pulled.
    /// </summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary> Max results returned by recall_past_work. </summary>
    public int MaxRecallResults { get; set; } = 5;

    /// <summary> Minimum cosine similarity (0..1) required to surface a recalled memory. </summary>
    public double SimilarityFloor { get; set; } = 0.45;
}
