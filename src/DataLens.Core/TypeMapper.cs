using DataLens.Core.Models;

namespace DataLens.Core;

/// <summary>Maps Postgres <c>information_schema</c> data types to our small type set.</summary>
public static class TypeMapper
{
    public static ColumnKind Map(string postgresType) => postgresType switch
    {
        "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision" or "money"
            => ColumnKind.Number,
        "text" or "character varying" or "character" or "uuid" or "citext" or "name"
            => ColumnKind.Text,
        "boolean" => ColumnKind.Boolean,
        "date" => ColumnKind.Date,
        "timestamp without time zone" or "timestamp with time zone" => ColumnKind.Timestamp,
        _ => ColumnKind.Other
    };
}
