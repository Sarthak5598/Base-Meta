using DataLens.Core.Models;

namespace DataLens.Core.Query;

/// <summary>
/// The SQL behind "suggest values for this filter": the distinct values of one column, optionally only
/// those containing some text. Like the rest of the compiler it is pure: names come from the snapshot and
/// are quoted, the search text is a parameter, and anything unusual is rejected before SQL exists.
/// </summary>
public static class FieldValuesCompiler
{
    public static CompiledQuery Compile(TableMetadata table, string column, string? search, int limit)
    {
        var col = table.Columns.FirstOrDefault(c => c.Name == column)
            ?? throw new DataLensException(DataLensError.BadRequest, $"'{column}' is not a column of {table.Name}.");

        // dates are picked with a date control and the rest has no useful list of values
        if (col.Kind is not (ColumnKind.Text or ColumnKind.Number))
            throw new DataLensException(DataLensError.BadRequest,
                $"Suggestions are only offered for text and number columns, and '{column}' is {col.RawType}.");

        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));

        var quoted = PreviewCompiler.Quote(col.Name);
        var parameters = new Dictionary<string, object?>();
        var where = $"{quoted} IS NOT NULL";

        if (!string.IsNullOrEmpty(search))
        {
            // typed % and _ mean themselves, not "anything"
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            parameters["p_search"] = $"%{escaped}%";
            where += $" AND {quoted}::text ILIKE @p_search";   // ::text so a number column can be searched too
        }

        parameters["p_limit"] = limit;

        var sql =
            $"SELECT DISTINCT {quoted} FROM {PreviewCompiler.Quote(table.Schema)}.{PreviewCompiler.Quote(table.Name)}\n" +
            $"WHERE {where}\n" +
            "ORDER BY 1\n" +
            "LIMIT @p_limit";

        return new CompiledQuery(sql, parameters);
    }
}
