namespace DataLens.Core.Models;

/// <summary>
/// The "snapshot": what we learned about the structure of a source database.
/// Plain data, serialised as JSON into the app database.
/// </summary>
public sealed record DbMetadata(
    IReadOnlyList<TableMetadata> Tables)
{
    public TableMetadata? FindTable(string schema, string name) =>
        Tables.FirstOrDefault(t => t.Schema == schema && t.Name == name);
}

public sealed record TableMetadata(
    string Schema,
    string Name,
    TableKind Kind,
    IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeyMetadata> ForeignKeys,
    IReadOnlyList<IndexMetadata> Indexes);

public enum TableKind
{
    Table,
    View
}

/// <param name="RawType">The type as Postgres reports it, e.g. "character varying".</param>
/// <param name="Kind">Our small, engine-neutral type set. Used by validation and charts later.</param>
public sealed record ColumnMetadata(
    string Name,
    string RawType,
    ColumnKind Kind,
    bool Nullable,
    bool IsPrimaryKey);

public enum ColumnKind
{
    Number,
    Text,
    Boolean,
    Date,
    Timestamp,
    Other
}

/// <summary>
/// A declared foreign key: <c>Columns</c> of this table point at <c>RefColumns</c> of <c>RefSchema.RefTable</c>.
/// Composite keys have more than one column in each list, in matching order.
/// </summary>
public sealed record ForeignKeyMetadata(
    string Name,
    IReadOnlyList<string> Columns,
    string RefSchema,
    string RefTable,
    IReadOnlyList<string> RefColumns);

public sealed record IndexMetadata(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique);
