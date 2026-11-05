using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Core;

public sealed record CompiledPreview(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<ResultColumn> Columns);

public sealed record QuestionResult(
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool LimitReached,
    long DurationMs,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>Finds a saved connection and its latest snapshot, or explains what is missing.</summary>
internal static class SnapshotLookup
{
    public static async Task<(SavedConnection Connection, StoredSnapshot Snapshot)> LoadAsync(
        IAppStore store, string nameOrId, CancellationToken ct)
    {
        var connection = await store.FindConnectionAsync(nameOrId, ct)
            ?? throw new DataLensException(DataLensError.NotFound, $"No saved connection called '{nameOrId}'.");

        var snapshot = await store.GetLatestSnapshotAsync(connection.Id, ct)
            ?? throw new DataLensException(DataLensError.NotFound, "This connection has not been synced yet.");

        return (connection, snapshot);
    }
}

/// <summary>Compile and run questions built with the query model.</summary>
public sealed class QuestionService(IAppStore store, IDatabaseConnector connector)
{
    /// <summary>Validate and compile without touching the source database. Cheap, so the UI can call it as you type.</summary>
    public async Task<CompiledPreview> CompileAsync(string connection, QueryDefinition query, CancellationToken ct = default)
    {
        var (_, snapshot) = await SnapshotLookup.LoadAsync(store, connection, ct);
        var compiled = QueryCompiler.Compile(query, snapshot.Metadata);
        return new CompiledPreview(compiled.Query.Sql, compiled.Query.Parameters, compiled.Columns);
    }

    public async Task<QuestionResult> RunAsync(string connection, QueryDefinition query, CancellationToken ct = default)
    {
        var (saved, snapshot) = await SnapshotLookup.LoadAsync(store, connection, ct);
        var compiled = QueryCompiler.Compile(query, snapshot.Metadata);

        var connectionString = await store.GetConnectionStringAsync(saved.Id, ct)
            ?? throw new DataLensException(DataLensError.NotFound, "The saved connection string is missing.");

        var result = await connector.ExecuteAsync(
            connectionString, compiled.Query, ExecOptions.Default with { MaxRows = query.Limit }, ct);

        return new QuestionResult(
            compiled.Columns, result.Rows,
            LimitReached: result.Rows.Count >= query.Limit,
            result.DurationMs, compiled.Query.Sql, compiled.Query.Parameters);
    }
}
