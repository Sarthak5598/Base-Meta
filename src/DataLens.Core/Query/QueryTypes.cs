namespace DataLens.Core.Query;

/// <summary>SQL text plus its parameter values. Values are never spliced into the SQL.</summary>
public sealed record CompiledQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <param name="Timeout">Server-side statement timeout.</param>
/// <param name="MaxRows">Hard cap on rows read. Reading stops here, even if the query would return more.</param>
public sealed record ExecOptions(TimeSpan Timeout, int MaxRows)
{
    public static ExecOptions Default { get; } = new(TimeSpan.FromSeconds(15), 10_000);
}

/// <summary>Rows come back as plain JSON-friendly values: string, number, bool, null or a list.</summary>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    long DurationMs);

public enum QueryFailure
{
    Timeout,
    PermissionDenied,
    ObjectNotFound,
    WriteBlocked,
    Other
}

/// <summary>A query failed in a way the user can understand. The message is safe to show.</summary>
public sealed class QueryFailedException(QueryFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public QueryFailure Reason { get; } = reason;
}

public enum SortDirection
{
    Asc,
    Desc
}
