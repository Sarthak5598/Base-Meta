namespace DataLens.Core;

/// <summary>The "no such connection" error, with what does exist, so a wrong name or a wrong folder is obvious.</summary>
public static class ConnectionNotFound
{
    public static async Task<DataLensException> CreateAsync(IAppStore store, string nameOrId, CancellationToken ct = default)
    {
        var saved = await store.ListConnectionsAsync(ct);
        var message = saved.Count == 0
            ? $"No saved connection called '{nameOrId}', and no connections are saved here yet."
            : $"No saved connection called '{nameOrId}'. Saved connections: {string.Join(", ", saved.Select(c => c.Name))}.";
        return new DataLensException(DataLensError.NotFound, message);
    }
}
