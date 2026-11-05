using System.Collections.Concurrent;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Core;

/// <param name="Values">Distinct values, sorted by how well they match, with no nulls.</param>
/// <param name="HasMore">There are more matching values than were returned: keep typing to narrow them down.</param>
/// <param name="Close">"Did you mean": spelled a little differently from what was typed. Only for small text columns.</param>
public sealed record FieldValues(IReadOnlyList<object?> Values, bool HasMore, IReadOnlyList<object?> Close);

/// <summary>
/// Values that exist in a column, for filter suggestions. Read live from the source database; nothing is
/// stored for good. The timeout is short because a column with millions of distinct values should not
/// make a dropdown hang.
///
/// Typo-tolerant matching needs the whole list of values, so for a text column with up to
/// <see cref="PoolSize"/> distinct values the list is read once, kept in memory for a few minutes, and
/// ranked here. Beyond that size ranking part of the list would mislead, so the database does a plain
/// "contains" search instead and there is no "did you mean".
/// </summary>
public sealed class FieldValuesService(IAppStore store, IDatabaseConnector connector, TimeProvider? clock = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxSearchLength = 100;
    public const int PoolSize = 1000;
    public const int MaxClose = 8;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PoolLifetime = TimeSpan.FromMinutes(5);
    private const int MaxPools = 256;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Pool> _pools = new();

    private sealed record Pool(IReadOnlyList<string> Values, bool Complete, DateTimeOffset At);

    public async Task<FieldValues> GetAsync(
        string connection, string schema, string table, string column, string? search,
        int limit = DefaultLimit, CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxLimit)
            throw new DataLensException(DataLensError.BadRequest, $"The number of suggestions must be between 1 and {MaxLimit}.");
        if (search is { Length: > MaxSearchLength })
            throw new DataLensException(DataLensError.BadRequest, $"Search text can be at most {MaxSearchLength} characters.");
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var (saved, snapshot) = await SnapshotLookup.LoadAsync(store, connection, ct);

        var tableMeta = snapshot.Metadata.FindTable(schema, table)
            ?? throw new DataLensException(DataLensError.NotFound, $"{schema}.{table} is not in the schema snapshot. Try re-syncing.");

        var columnMeta = tableMeta.Columns.FirstOrDefault(c => c.Name == column);

        var connectionString = await store.GetConnectionStringAsync(saved.Id, ct)
            ?? throw new DataLensException(DataLensError.NotFound, "The saved connection string is missing.");

        // typing something into a small text column: rank the whole list, forgiving typos
        if (search is not null && columnMeta?.Kind == ColumnKind.Text)
        {
            var pool = await GetPoolAsync(saved.Id, connectionString, tableMeta, column, ct);
            if (pool.Complete)
            {
                var ranked = FuzzyMatch.Rank(pool.Values, search);
                return new FieldValues(
                    ranked.Matches.Take(limit).Cast<object?>().ToList(),
                    HasMore: ranked.Matches.Count > limit,
                    ranked.Close.Take(MaxClose).Cast<object?>().ToList());
            }
        }

        // otherwise the database does the work: the first values in order, or those containing the text
        var query = FieldValuesCompiler.Compile(tableMeta, column, search, limit + 1);
        var result = await connector.ExecuteAsync(connectionString, query, new ExecOptions(Timeout, limit + 1), ct);

        return new FieldValues(
            result.Rows.Take(limit).Select(row => row[0]).ToList(),
            HasMore: result.Rows.Count > limit,
            []);
    }

    /// <summary>Every distinct value of a column, if there are few enough. Remembered for a few minutes.</summary>
    private async Task<Pool> GetPoolAsync(
        string connectionId, string connectionString, TableMetadata table, string column, CancellationToken ct)
    {
        var key = $"{connectionId}|{table.Schema}.{table.Name}.{column}";
        var now = _clock.GetUtcNow();
        if (_pools.TryGetValue(key, out var cached) && now - cached.At < PoolLifetime) return cached;

        var query = FieldValuesCompiler.Compile(table, column, null, PoolSize + 1);
        var result = await connector.ExecuteAsync(connectionString, query, new ExecOptions(Timeout, PoolSize + 1), ct);

        var pool = new Pool(
            result.Rows.Take(PoolSize).Select(r => Convert.ToString(r[0]) ?? "").ToList(),
            Complete: result.Rows.Count <= PoolSize,
            now);

        if (_pools.Count >= MaxPools) _pools.Clear();   // simple bound: refilling is cheap
        _pools[key] = pool;
        return pool;
    }
}
