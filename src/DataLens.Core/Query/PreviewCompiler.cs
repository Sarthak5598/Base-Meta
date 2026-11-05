using DataLens.Core.Models;

namespace DataLens.Core.Query;

/// <summary>
/// Turns "show me page N of this table, sorted by X" into SQL. Pure function, no database access.
/// Identifiers (schema, table, columns) can't be parameters in Postgres, so they are only ever taken
/// from the schema snapshot and always double-quoted. Values (limit, offset) are parameters.
/// </summary>
public static class PreviewCompiler
{
    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Contains('\0'))
            throw new ArgumentException("Invalid identifier.", nameof(identifier));
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public static CompiledQuery Compile(
        TableMetadata table, string? sortColumn, SortDirection direction, int limit, int offset)
    {
        if (table.Columns.Count == 0)
            throw new ArgumentException($"{table.Schema}.{table.Name} has no readable columns.", nameof(table));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (sortColumn is not null && table.Columns.All(c => c.Name != sortColumn))
            throw new ArgumentException($"Unknown column '{sortColumn}'.", nameof(sortColumn));

        var select = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));

        // OFFSET paging needs a total order, or rows can repeat or vanish between pages.
        // The user's sort comes first, then the primary key (or the first column) as tie-breaker.
        var order = new List<string>();
        if (sortColumn is not null)
            order.Add($"{Quote(sortColumn)} {(direction == SortDirection.Desc ? "DESC" : "ASC")} NULLS LAST");

        var tieBreakers = table.PrimaryKey.Count > 0
            ? table.PrimaryKey
            : sortColumn is null ? new[] { table.Columns[0].Name } : Array.Empty<string>();
        order.AddRange(tieBreakers.Where(c => c != sortColumn).Select(c => $"{Quote(c)} ASC"));

        var sql =
            $"SELECT {select} FROM {Quote(table.Schema)}.{Quote(table.Name)}" +
            (order.Count > 0 ? $" ORDER BY {string.Join(", ", order)}" : "") +
            " LIMIT @p_limit OFFSET @p_offset";

        return new CompiledQuery(sql, new Dictionary<string, object?>
        {
            ["p_limit"] = limit,
            ["p_offset"] = offset
        });
    }
}
