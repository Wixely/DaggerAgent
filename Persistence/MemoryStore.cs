using System.Text.Json;
using Dapper;
using Daggeragent.Configuration;
using Daggeragent.Llm;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Persistence;

public sealed record MemoryRecord(string Id, string? JobId, DateTimeOffset CreatedAt, string Text, string EmbeddingModel);

public sealed record RecalledMemory(MemoryRecord Record, double Similarity);

public sealed class MemoryStore
{
    private readonly string _connectionString;
    private readonly MemoryOptions _options;
    private readonly EmbeddingClientFactory _embeddings;
    private readonly ILogger<MemoryStore> _log;

    public MemoryStore(
        IOptions<JobsOptions> jobsOptions,
        IOptions<MemoryOptions> memoryOptions,
        EmbeddingClientFactory embeddings,
        ILogger<MemoryStore> log)
    {
        // Reuse the same SQLite database as jobs.
        _connectionString = ResolveConnectionString(jobsOptions.Value.ConnectionString);
        _options = memoryOptions.Value;
        _embeddings = embeddings;
        _log = log;
    }

    public bool Enabled => _options.Enabled;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        await using var conn = Open();
        await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS memories (
    id              TEXT PRIMARY KEY,
    job_id          TEXT NULL,
    created_at      TEXT NOT NULL,
    text            TEXT NOT NULL,
    embedding_json  TEXT NOT NULL,
    embedding_dims  INTEGER NOT NULL,
    embedding_model TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_memories_created_at ON memories(created_at DESC);
").ConfigureAwait(false);
        _log.LogInformation("Memory store initialised (provider embedding model: {Model})", _options.EmbeddingModel);
    }

    public async Task<string?> SaveAsync(string? jobId, string text, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        float[] embedding;
        try
        {
            var generator = _embeddings.Create();
            var result = await generator.GenerateAsync(new[] { text }, cancellationToken: cancellationToken).ConfigureAwait(false);
            embedding = result[0].Vector.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Embedding generation failed; memory not saved");
            return null;
        }

        var id = Guid.NewGuid().ToString("N");
        await using var conn = Open();
        await conn.ExecuteAsync(@"
INSERT INTO memories (id, job_id, created_at, text, embedding_json, embedding_dims, embedding_model)
VALUES (@Id, @JobId, @CreatedAt, @Text, @Json, @Dims, @Model)",
            new
            {
                Id = id,
                JobId = jobId,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Text = text,
                Json = JsonSerializer.Serialize(embedding),
                Dims = embedding.Length,
                Model = _options.EmbeddingModel,
            }).ConfigureAwait(false);

        _log.LogInformation("Saved memory {MemoryId} ({Chars} chars, {Dims}-dim embedding)", id, text.Length, embedding.Length);
        return id;
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int? limit = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Array.Empty<RecalledMemory>();
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<RecalledMemory>();

        float[] queryVector;
        try
        {
            var generator = _embeddings.Create();
            var result = await generator.GenerateAsync(new[] { query }, cancellationToken: cancellationToken).ConfigureAwait(false);
            queryVector = result[0].Vector.ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Embedding generation failed during recall");
            return Array.Empty<RecalledMemory>();
        }

        await using var conn = Open();
        var rows = await conn.QueryAsync<MemoryRow>(@"
SELECT id, job_id, created_at, text, embedding_json, embedding_dims, embedding_model FROM memories").ConfigureAwait(false);

        var scored = new List<RecalledMemory>();
        foreach (var row in rows)
        {
            var vec = JsonSerializer.Deserialize<float[]>(row.embedding_json);
            if (vec is null || vec.Length != queryVector.Length) continue;
            var sim = CosineSimilarity(queryVector, vec);
            if (sim < _options.SimilarityFloor) continue;
            scored.Add(new RecalledMemory(
                new MemoryRecord(row.id, row.job_id, DateTimeOffset.Parse(row.created_at), row.text, row.embedding_model),
                sim));
        }

        return scored
            .OrderByDescending(r => r.Similarity)
            .Take(limit ?? _options.MaxRecallResults)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }

    private SqliteConnection Open() => SqliteConnectionFactory.Open(_connectionString);

    private static string ResolveConnectionString(string raw)
    {
        var sb = new SqliteConnectionStringBuilder(raw);
        if (!string.IsNullOrEmpty(sb.DataSource) && sb.DataSource != ":memory:" && !Path.IsPathRooted(sb.DataSource))
        {
            sb.DataSource = Path.Combine(Directory.GetCurrentDirectory(), sb.DataSource);
        }
        return sb.ToString();
    }

    private sealed class MemoryRow
    {
        public string id { get; set; } = "";
        public string? job_id { get; set; }
        public string created_at { get; set; } = "";
        public string text { get; set; } = "";
        public string embedding_json { get; set; } = "";
        public int embedding_dims { get; set; }
        public string embedding_model { get; set; } = "";
    }
}
