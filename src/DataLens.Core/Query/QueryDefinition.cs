using System.Text.Json;

namespace DataLens.Core.Query;

// The query model: a plain description of a question. The UI builds one, it is validated against the
// schema snapshot, and the compiler turns it into SQL. It is also exactly what gets saved as JSON.
// (This is our small version of Metabase's MBQL.)

public sealed record TableRef(string Schema, string Table);

/// <param name="Table">Which table the column belongs to. Null means the question's source table.</param>
public sealed record ColumnRef(string Name, TableRef? Table = null);

/// <summary>
/// Bring in <c>Table</c>, linked to <c>From</c> (a table already in the question) through the foreign key
/// named <c>ForeignKey</c>. The key may live on either table. Users never write the ON clause.
/// </summary>
public sealed record JoinDef(TableRef Table, TableRef From, string ForeignKey);

public enum FilterOp
{
    Eq,
    NotEq,
    Lt,
    Lte,
    Gt,
    Gte,
    Between,
    In,
    Contains,
    StartsWith,
    EndsWith,
    IsNull,
    NotNull
}

/// <param name="Values">None for isNull/notNull, two for between, any number for in, otherwise one.</param>
public sealed record Filter(ColumnRef Column, FilterOp Op, IReadOnlyList<JsonElement>? Values = null);

public enum AggFn
{
    Count,
    CountDistinct,
    Sum,
    Avg,
    Min,
    Max
}

/// <param name="Column">Not needed for a plain count of rows.</param>
/// <param name="Alias">Name of the result column. Defaults to something like sum_total.</param>
public sealed record Aggregation(AggFn Fn, ColumnRef? Column = null, string? Alias = null);

public enum DateBucket
{
    Day,
    Week,
    Month,
    Quarter,
    Year
}

public sealed record Grouping(ColumnRef Column, DateBucket? Bucket = null);

/// <param name="Column">The name of a column in the results (including summary names like sum_total).</param>
public sealed record SortSpec(string Column, SortDirection Direction = SortDirection.Asc);

public sealed record QueryDefinition(
    TableRef Source,
    IReadOnlyList<JoinDef>? Joins = null,
    IReadOnlyList<ColumnRef>? Columns = null,
    IReadOnlyList<Filter>? Filters = null,
    IReadOnlyList<Grouping>? GroupBy = null,
    IReadOnlyList<Aggregation>? Aggregations = null,
    IReadOnlyList<SortSpec>? OrderBy = null,
    int Limit = 1000);
