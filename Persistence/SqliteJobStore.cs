using System.Text.Json;
using Dapper;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Persistence;

public sealed class SqliteJobStore : IJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteJobStore> _log;

    public SqliteJobStore(IOptions<JobsOptions> options, ILogger<SqliteJobStore> log)
    {
        _connectionString = ResolveConnectionString(options.Value.ConnectionString);
        _log = log;
    }

    private static string ResolveConnectionString(string raw)
    {
        var sb = new SqliteConnectionStringBuilder(raw);
        if (!string.IsNullOrEmpty(sb.DataSource) && sb.DataSource != ":memory:" && !Path.IsPathRooted(sb.DataSource))
        {
            // Use cwd (which Program.cs has already pinned to the exe directory) rather
            // than AppContext.BaseDirectory — for single-file publishes the latter points
            // at the bundle extract tmp dir, which is wrong and ephemeral.
            sb.DataSource = Path.Combine(Directory.GetCurrentDirectory(), sb.DataSource);
        }
        return sb.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectoryExists();
        await using var conn = Open();
        await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS jobs (
    id          TEXT PRIMARY KEY,
    parent_id   TEXT NULL,
    status      TEXT NOT NULL,
    model       TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    state_json  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jobs_parent_id ON jobs(parent_id);
CREATE INDEX IF NOT EXISTS ix_jobs_updated_at ON jobs(updated_at DESC);

CREATE TABLE IF NOT EXISTS job_events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id       TEXT NOT NULL,
    ts           TEXT NOT NULL,
    kind         TEXT NOT NULL,
    payload_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_job_events_job_id ON job_events(job_id, id);
").ConfigureAwait(false);
        _log.LogInformation("SQLite job store initialised at {ConnectionString}", _connectionString);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = Open();
            var result = await conn.ExecuteScalarAsync<long>("SELECT 1").ConfigureAwait(false);
            return result == 1;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SQLite health check failed");
            return false;
        }
    }

    public async Task SaveAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await using var conn = Open();
        await conn.ExecuteAsync(@"
INSERT INTO jobs (id, parent_id, status, model, created_at, updated_at, state_json)
VALUES (@Id, @ParentId, @Status, @Model, @Now, @Now, @Json)
ON CONFLICT(id) DO UPDATE SET
    parent_id   = excluded.parent_id,
    status      = excluded.status,
    model       = excluded.model,
    updated_at  = excluded.updated_at,
    state_json  = excluded.state_json;",
            new
            {
                state.Id,
                state.ParentId,
                Status = state.Status.ToString(),
                state.Model,
                Now = now.ToString("O"),
                Json = json,
            }).ConfigureAwait(false);
    }

    public async Task<ConversationState?> LoadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        var json = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT state_json FROM jobs WHERE id = @Id", new { Id = jobId }).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<ConversationState>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<JobRow>(@"
SELECT id, parent_id, status, model, created_at, updated_at, state_json
FROM jobs ORDER BY updated_at DESC LIMIT @Limit", new { Limit = limit }).ConfigureAwait(false);
        return rows.Select(r => new JobRecord
        {
            Id = r.id,
            ParentId = r.parent_id,
            Status = r.status,
            Model = r.model,
            CreatedAt = DateTimeOffset.Parse(r.created_at),
            UpdatedAt = DateTimeOffset.Parse(r.updated_at),
            StateJson = r.state_json,
        }).ToList();
    }

    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM job_events WHERE job_id = @Id; DELETE FROM jobs WHERE id = @Id;", new { Id = jobId }).ConfigureAwait(false);
    }

    public async Task AppendEventAsync(string jobId, string kind, string payloadJson, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(@"
INSERT INTO job_events (job_id, ts, kind, payload_json)
VALUES (@JobId, @Ts, @Kind, @Payload);",
            new { JobId = jobId, Ts = DateTimeOffset.UtcNow.ToString("O"), Kind = kind, Payload = payloadJson }).ConfigureAwait(false);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureDirectoryExists()
    {
        var sb = new SqliteConnectionStringBuilder(_connectionString);
        if (!string.IsNullOrWhiteSpace(sb.DataSource) && sb.DataSource != ":memory:")
        {
            var fullPath = Path.IsPathRooted(sb.DataSource)
                ? sb.DataSource
                : Path.Combine(Directory.GetCurrentDirectory(), sb.DataSource);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }

    private sealed class JobRow
    {
        public string id { get; set; } = "";
        public string? parent_id { get; set; }
        public string status { get; set; } = "";
        public string model { get; set; } = "";
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string state_json { get; set; } = "";
    }
}
