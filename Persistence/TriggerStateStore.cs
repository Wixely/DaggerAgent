using Dapper;
using Daggeragent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Persistence;

/// <summary>
/// Per-source polling state. Two responsibilities:
///   - Remember the last-poll timestamp so we can pass "since" to MCP servers.
///   - Remember which individual matches (ticket/comment ids) we've already turned
///     into agent jobs, so re-emission of the same match doesn't fire duplicate work.
/// </summary>
public sealed class TriggerStateStore
{
    private readonly string _connectionString;
    private readonly ILogger<TriggerStateStore> _log;

    public TriggerStateStore(IOptions<JobsOptions> jobsOptions, ILogger<TriggerStateStore> log)
    {
        _connectionString = ResolveConnectionString(jobsOptions.Value.ConnectionString);
        _log = log;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS trigger_state (
    source_id        TEXT PRIMARY KEY,
    last_polled_utc  TEXT NOT NULL,
    updated_at       TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS trigger_seen (
    source_id    TEXT NOT NULL,
    match_key    TEXT NOT NULL,
    seen_at_utc  TEXT NOT NULL,
    job_id       TEXT NULL,
    PRIMARY KEY (source_id, match_key)
);
CREATE INDEX IF NOT EXISTS ix_trigger_seen_seen_at ON trigger_seen(seen_at_utc DESC);
").ConfigureAwait(false);
        _log.LogInformation("Trigger state store initialised");
    }

    public async Task<DateTimeOffset?> GetLastPolledAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT last_polled_utc FROM trigger_state WHERE source_id = @Id", new { Id = sourceId }).ConfigureAwait(false);
        return row is null ? null : DateTimeOffset.Parse(row);
    }

    public async Task SetLastPolledAsync(string sourceId, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(@"
INSERT INTO trigger_state (source_id, last_polled_utc, updated_at)
VALUES (@Id, @When, @When)
ON CONFLICT(source_id) DO UPDATE SET
    last_polled_utc = excluded.last_polled_utc,
    updated_at      = excluded.updated_at;",
            new { Id = sourceId, When = when.ToString("O") }).ConfigureAwait(false);
    }

    /// <summary> Returns true if the match was newly inserted; false if it was already seen. </summary>
    public async Task<bool> ClaimMatchAsync(string sourceId, string matchKey, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO trigger_seen (source_id, match_key, seen_at_utc) VALUES (@Sid, @Key, @At)",
            new { Sid = sourceId, Key = matchKey, At = DateTimeOffset.UtcNow.ToString("O") }).ConfigureAwait(false);
        return rows == 1;
    }

    public async Task AttachJobIdAsync(string sourceId, string matchKey, string jobId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(@"
UPDATE trigger_seen SET job_id = @Job WHERE source_id = @Sid AND match_key = @Key",
            new { Sid = sourceId, Key = matchKey, Job = jobId }).ConfigureAwait(false);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ResolveConnectionString(string raw)
    {
        var sb = new SqliteConnectionStringBuilder(raw);
        if (!string.IsNullOrEmpty(sb.DataSource) && sb.DataSource != ":memory:" && !Path.IsPathRooted(sb.DataSource))
        {
            sb.DataSource = Path.Combine(Directory.GetCurrentDirectory(), sb.DataSource);
        }
        return sb.ToString();
    }
}
