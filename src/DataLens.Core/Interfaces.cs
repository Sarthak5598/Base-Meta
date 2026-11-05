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

/// <summary>Saved questions and dashboards. Questions are stored as a query model, never as results.</summary>
public interface IContentStore
{
    Task<SavedQuery> SaveQueryAsync(
        string connectionId, string name, QueryDefinition query, ChartConfig chart, CancellationToken ct = default);

    /// <summary>Null when there is no such question.</summary>
    Task<SavedQuery?> UpdateQueryAsync(
        string id, string name, QueryDefinition query, ChartConfig chart, CancellationToken ct = default);

    Task<SavedQuery?> GetQueryAsync(string id, CancellationToken ct = default);

    /// <summary>All questions, or only those of one connection.</summary>
    Task<IReadOnlyList<SavedQuery>> ListQueriesAsync(string? connectionId = null, CancellationToken ct = default);

    /// <summary>Also removes the question from every dashboard.</summary>
    Task<bool> DeleteQueryAsync(string id, CancellationToken ct = default);

    Task<Dashboard> CreateDashboardAsync(string name, CancellationToken ct = default);

    Task<Dashboard?> GetDashboardAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(CancellationToken ct = default);

    Task<bool> DeleteDashboardAsync(string id, CancellationToken ct = default);

    /// <summary>Adds at the end. Adding a question that is already there changes nothing. Null if either does not exist.</summary>
    Task<Dashboard?> AddCardAsync(string dashboardId, string queryId, CancellationToken ct = default);

    Task<Dashboard?> RemoveCardAsync(string dashboardId, string queryId, CancellationToken ct = default);
}
