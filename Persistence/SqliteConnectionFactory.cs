using Dapper;
using Microsoft.Data.Sqlite;

namespace Daggeragent.Persistence;

/// <summary>
/// Centralised SQLite connection opener shared by every store. They all hit one
/// <c>data/jobs.db</c>, so concurrent writers — fire-and-forget trigger jobs each saving
/// repeatedly, the background poller, and the startup auto-resume sweep — contend on the same
/// file. The default rollback journal serialises writers and blocks readers against the writer,
/// which shows up as multi-second stalls and the occasional <c>database is locked</c> throw.
///
/// Every connection is opened with:
///   • <c>journal_mode=WAL</c>     — readers run concurrently with the single writer (persists on
///                                   the file; re-asserting it per open is a cheap no-op).
///   • <c>busy_timeout=5000</c>    — a bounded 5s wait on lock contention instead of the 30s
///                                   ADO.NET command-timeout stall (per-connection, so set each time).
///   • <c>synchronous=NORMAL</c>   — the safe, faster durability setting recommended with WAL.
///
/// (On a <c>:memory:</c> data source WAL is silently ignored by SQLite — harmless.)
/// </summary>
internal static class SqliteConnectionFactory
{
    public static SqliteConnection Open(string connectionString)
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;");
        return conn;
    }
}
