namespace DataLens.Core.Models;

public sealed record ConnectionTestResult(
    bool Ok,
    string? Error,
    bool CanWrite,
    IReadOnlyList<string> Warnings)
{
    public static ConnectionTestResult Failed(string error) =>
        new(false, error, false, Array.Empty<string>());
}

/// <summary>A saved connection. The connection string itself is only available decrypted inside the store.</summary>
public sealed record SavedConnection(
    string Id,
    string Name,
    string MaskedConnectionString,
    bool CanWrite,
    DateTimeOffset CreatedAt);

public sealed record StoredSnapshot(
    string ConnectionId,
    int Version,
    DateTimeOffset SyncedAt,
    DbMetadata Metadata);
