using DataLens.Core.Models;

namespace DataLens.Core;

public sealed record ConnectResult(
    ConnectionTestResult Test,
    SavedConnection? Connection,
    StoredSnapshot? Snapshot);

/// <summary>Connect flow (test, save, first sync) and re-sync. Used by both the CLI and the API.</summary>
public sealed class ConnectionService(IAppStore store, IDatabaseConnector connector)
{
    private const int MaxNameLength = 100;

    public async Task<ConnectResult> ConnectAsync(
        string name, string connectionString, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new DataLensException(DataLensError.BadRequest, "Give the connection a name.");
        if (name.Length > MaxNameLength)
            throw new DataLensException(DataLensError.BadRequest, $"The name can be at most {MaxNameLength} characters.");

        var test = await connector.TestAsync(connectionString, ct);
        if (!test.Ok)
            return new ConnectResult(test, null, null);

        var saved = await store.SaveConnectionAsync(name, connectionString, test.CanWrite, ct);
        var snapshot = await SyncAsync(saved.Id, ct);
        return new ConnectResult(test, saved, snapshot);
    }

    public async Task<StoredSnapshot> SyncAsync(string nameOrId, CancellationToken ct = default)
    {
        var saved = await store.FindConnectionAsync(nameOrId, ct)
            ?? throw new DataLensException(DataLensError.NotFound, $"No saved connection called '{nameOrId}'.");

        var connectionString = await store.GetConnectionStringAsync(saved.Id, ct)
            ?? throw new DataLensException(DataLensError.NotFound, "The saved connection string is missing.");

        var metadata = await connector.DescribeAsync(connectionString, ct);
        return await store.SaveSnapshotAsync(saved.Id, metadata, ct);
    }
}
