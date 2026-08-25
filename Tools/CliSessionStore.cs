using System.Collections.Concurrent;
using Dapper;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

/// <summary>
/// Map of (jobId, cli-kind) → external CLI session id + the cwd that produced it.
/// Lets successive calls to <c>delegate_to_claude</c> / <c>delegate_to_codex</c> in the same
/// DaggerAgent job pass <c>--resume &lt;session&gt;</c> so the spawned CLI continues its own
/// conversation instead of starting cold each time.
///
/// The cwd tag matters because Claude scopes sessions to the project directory: a session id
/// captured in cwd A returns "No conversation found with session ID …" when handed to a Claude
/// run from cwd B. <see cref="Get"/> only returns the session when the requested cwd matches
/// what was stored — cross-dir continues fall back to a fresh session automatically.
///
/// Backed by the <c>cli_sessions</c> table so the link survives a restart. Everything else in
/// the conversation chain is already durable (history is persisted, jobs resume by id, the
/// orphan sweep re-queues interrupted jobs); when this map was memory-only it was the one link
/// that broke on restart, and it broke silently — the next delegation just started cold and
/// returned a plausible answer from an amnesiac CLI.
///
/// The dictionary stays in front as a read-through cache: <see cref="Get"/> is called on a
/// synchronous path, and a process only ever resumes jobs it owns, so a cache hit needs no
/// round trip.
///
/// Persistence is best-effort. A resumable session id is an optimisation, not a correctness
/// requirement, so any SQLite failure degrades to the old in-memory behaviour (logged once)
/// rather than failing the delegation that triggered it.
/// </summary>
public sealed class CliSessionStore
{
    /// <summary>
    /// Job-scoped, so <see cref="Persistence.SqliteJobStore"/> owns the DDL alongside
    /// <c>jobs</c> / <c>job_events</c> and clears these rows when a job is deleted. Re-asserted
    /// lazily here because a host may reach a delegation tool without having initialised the
    /// job store.
    /// </summary>
    internal const string Ddl = @"
CREATE TABLE IF NOT EXISTS cli_sessions (
    job_id      TEXT NOT NULL,
    cli         TEXT NOT NULL,
    cwd         TEXT NOT NULL,
    session_id  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    PRIMARY KEY (job_id, cli)
);";

    private readonly ConcurrentDictionary<(string JobId, string Cli), Entry> _cache = new();
    private readonly string _connectionString;
    private readonly ILogger<CliSessionStore> _log;
    private readonly object _schemaGate = new();
    // Read outside the lock by the double-checked EnsureSchema, and set by whichever thread
    // first sees a failure. Worst case without volatile is a redundant CREATE TABLE IF NOT
    // EXISTS or a duplicate warning, but the flags are cheap to get right.
    private volatile bool _schemaReady;
    private volatile bool _persistenceOff;

    public CliSessionStore(IOptions<JobsOptions> jobsOptions, ILogger<CliSessionStore> log)
    {
        _connectionString = ResolveConnectionString(jobsOptions.Value.ConnectionString);
        _log = log;
    }

    public string? Get(string jobId, string cli, string cwd) =>
        Lookup(jobId, cli) is { } e && string.Equals(e.Cwd, cwd, StringComparison.OrdinalIgnoreCase)
            ? e.SessionId
            : null;

    /// <summary>
    /// Diagnostic-only: returns the cwd a stashed session was created in, if any. Lets the
    /// caller log a "dropping session because cwd changed from X to Y" message without
    /// re-keying the dictionary first.
    /// </summary>
    public string? GetStoredCwd(string jobId, string cli) => Lookup(jobId, cli)?.Cwd;

    public void Set(string jobId, string cli, string cwd, string sessionId)
    {
        _cache[(jobId, cli)] = new Entry(sessionId, cwd);
        _ = Persist(conn => conn.Execute(@"
INSERT INTO cli_sessions (job_id, cli, cwd, session_id, updated_at)
VALUES (@JobId, @Cli, @Cwd, @SessionId, @UpdatedAt)
ON CONFLICT(job_id, cli) DO UPDATE SET
    cwd        = excluded.cwd,
    session_id = excluded.session_id,
    updated_at = excluded.updated_at;",
            new
            {
                JobId = jobId,
                Cli = cli,
                Cwd = cwd,
                SessionId = sessionId,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }));
    }

    public bool Clear(string jobId, string cli)
    {
        var dropped = _cache.TryRemove((jobId, cli), out _);
        // Also true when nothing was cached but a row survived a restart — the caller asked
        // whether a session was actually dropped, not whether this process had seen it.
        var rows = Persist(conn => conn.Execute(
            "DELETE FROM cli_sessions WHERE job_id = @JobId AND cli = @Cli",
            new { JobId = jobId, Cli = cli }));
        return dropped || rows > 0;
    }

    private Entry? Lookup(string jobId, string cli)
    {
        if (_cache.TryGetValue((jobId, cli), out var cached)) return cached;

        // Miss. Either nothing was ever stored, or this process restarted and lost the map.
        // Misses are not negatively cached: they are cheap (primary-key lookup), and a caller
        // that never delegates never gets here at all.
        var row = Persist(conn => conn.QuerySingleOrDefault<Row>(
            "SELECT cwd AS Cwd, session_id AS SessionId FROM cli_sessions WHERE job_id = @JobId AND cli = @Cli",
            new { JobId = jobId, Cli = cli }));
        if (row is null) return null;

        var entry = new Entry(row.SessionId, row.Cwd);
        _cache[(jobId, cli)] = entry;
        _log.LogInformation(
            "Rehydrated {Cli} session for job={JobId} from disk (cwd={Cwd}) — resuming across restart",
            cli, jobId, entry.Cwd);
        return entry;
    }

    private T? Persist<T>(Func<SqliteConnection, T> work)
    {
        if (_persistenceOff) return default;
        try
        {
            EnsureSchema();
            using var conn = SqliteConnectionFactory.Open(_connectionString);
            return work(conn);
        }
        catch (Exception ex)
        {
            _persistenceOff = true;
            _log.LogWarning(ex,
                "CLI session persistence unavailable — delegated CLI sessions will not survive a restart");
            return default;
        }
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_schemaGate)
        {
            if (_schemaReady) return;
            using var conn = SqliteConnectionFactory.Open(_connectionString);
            conn.Execute(Ddl);
            _schemaReady = true;
        }
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

    private readonly record struct Entry(string SessionId, string Cwd);

    /// <summary>Dapper row shape; column names are aliased to these in the SELECT.</summary>
    private sealed class Row
    {
        public string Cwd { get; set; } = "";
        public string SessionId { get; set; } = "";
    }
}
