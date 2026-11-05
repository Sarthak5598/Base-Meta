using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Core;

/// <summary>
/// Talks to a source database. PostgreSQL is the only implementation for now;
/// the interface exists so a second engine does not require rewriting callers.
/// </summary>
public interface IDatabaseConnector
{
    Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default);

    /// <summary>Reads tables, columns, keys and indexes. Does not read any row data.</summary>
    Task<DbMetadata> DescribeAsync(string connectionString, CancellationToken ct = default);

    /// <summary>
    /// Runs one query. Always inside a read-only transaction that is rolled back, with a server-side
    /// timeout and a hard row cap, whatever permissions the database user has.
    /// </summary>
    /// <exception cref="Query.QueryFailedException">Timeout, permission problem, missing object, and so on.</exception>
    Task<QueryResult> ExecuteAsync(
        string connectionString, CompiledQuery query, ExecOptions options, CancellationToken ct = default);
}

/// <summary>Our own database: saved connections and schema snapshots.</summary>
public interface IAppStore
{
    Task<SavedConnection> SaveConnectionAsync(
        string name, string connectionString, bool canWrite, CancellationToken ct = default);

    Task<IReadOnlyList<SavedConnection>> ListConnectionsAsync(CancellationToken ct = default);

    Task<SavedConnection?> FindConnectionAsync(string nameOrId, CancellationToken ct = default);

    /// <summary>Returns the decrypted connection string. Never expose this to a client.</summary>
    Task<string?> GetConnectionStringAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Replaces any earlier snapshot for the connection and returns the new version number.</summary>
    Task<StoredSnapshot> SaveSnapshotAsync(
        string connectionId, DbMetadata metadata, CancellationToken ct = default);

    Task<StoredSnapshot?> GetLatestSnapshotAsync(string connectionId, CancellationToken ct = default);
}
