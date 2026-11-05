using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataLens.Core.Models;

namespace DataLens.Core.Query;

public sealed record ResultColumn(string Name, ColumnKind Kind);

public sealed record CompiledSelect(CompiledQuery Query, IReadOnlyList<ResultColumn> Columns);

/// <summary>
/// Query model in, SQL text and parameters out. A pure function: it only looks at the schema snapshot,
/// never at the database.
///
/// Two rules keep it safe. Identifiers (tables, columns) can't be parameters in Postgres, so they only ever
/// come from the snapshot and are always double-quoted. Values are always parameters, never part of the text.
/// Anything that does not check out against the snapshot is rejected with a message, before SQL exists.
/// </summary>
public static class QueryCompiler
{
    public const int MaxLimit = 10_000;
    public const int MaxTables = 6;
    public const int MaxFilters = 25;
    public const int MaxAggregations = 10;
    public const int MaxGroupBy = 4;
    public const int MaxColumns = 100;
    public const int MaxInValues = 200;
    private const int MaxAliasLength = 63;   // Postgres truncates longer names silently

    public static CompiledSelect Compile(QueryDefinition query, DbMetadata metadata) =>
        new Builder(query, metadata).Build();

    private static DataLensException Bad(string message) => new(DataLensError.BadRequest, message);

    private static string Q(string identifier) => PreviewCompiler.Quote(identifier);

    private sealed record QTable(TableMetadata Meta, string Alias);

    private sealed record Resolved(QTable Table, ColumnMetadata Column)
    {
        public string Sql => $"{Q(Table.Alias)}.{Q(Column.Name)}";
        public string Label => $"{Table.Meta.Name}.{Column.Name}";
    }

    private sealed class Builder(QueryDefinition q, DbMetadata meta)
    {
        private readonly List<QTable> _tables = [];
        private readonly Dictionary<string, object?> _params = new();
        private readonly List<ResultColumn> _out = [];
        private readonly List<string> _select = [];
        private readonly HashSet<string> _outNames = new(StringComparer.Ordinal);
        private int _p;

        public CompiledSelect Build()
        {
            if (q.Limit < 1 || q.Limit > MaxLimit)
                throw Bad($"The row limit must be between 1 and {MaxLimit:N0}.");

            var joinSql = ResolveTables();
            var groups = q.GroupBy ?? [];
            var aggs = q.Aggregations ?? [];
            var columns = q.Columns ?? [];
            var filters = q.Filters ?? [];

            if (groups.Count > MaxGroupBy) throw Bad($"At most {MaxGroupBy} groupings are supported.");
            if (aggs.Count > MaxAggregations) throw Bad($"At most {MaxAggregations} summaries are supported.");
            if (columns.Count > MaxColumns) throw Bad($"At most {MaxColumns} columns can be selected.");
            if (filters.Count > MaxFilters) throw Bad($"At most {MaxFilters} filters are supported.");

            var grouped = groups.Count > 0 || aggs.Count > 0;
            if (grouped && columns.Count > 0)
                throw Bad("Choose either individual columns or a summary, not both.");

            var groupOrdinals = new List<int>();
            if (grouped)
            {
                foreach (var g in groups)
                {
                    var r = Resolve(g.Column);
                    var expr = r.Sql;
                    var kind = r.Column.Kind;
                    if (g.Bucket is { } bucket)
                    {
                        if (r.Column.Kind is not (ColumnKind.Date or ColumnKind.Timestamp))
                            throw Bad($"'{r.Label}' is not a date, so it cannot be grouped by {bucket.ToString().ToLowerInvariant()}.");
                        expr = $"date_trunc('{BucketSql(bucket)}', {r.Sql})";
                        kind = ColumnKind.Timestamp;
                    }
                    AddOutput(expr, OutputName(r), kind);
                    groupOrdinals.Add(_select.Count);
                }

                foreach (var a in aggs) AddAggregation(a);
            }
            else
            {
                var picked = columns.Count == 0
                    ? _tables.SelectMany(t => t.Meta.Columns.Select(c => new Resolved(t, c))).ToList()
                    : columns.Select(Resolve).ToList();
                foreach (var r in picked) AddOutput(r.Sql, OutputName(r), r.Column.Kind);
            }

            var where = filters.Select(BuildFilter).ToList();

            var order = new List<string>();
            foreach (var sort in q.OrderBy ?? [])
            {
                if (!_outNames.Contains(sort.Column))
                    throw Bad($"Cannot sort by '{sort.Column}': it is not one of the result columns.");
                order.Add($"{Q(sort.Column)} {(sort.Direction == SortDirection.Desc ? "DESC" : "ASC")} NULLS LAST");
            }

            _params["p_limit"] = q.Limit;

            var source = _tables[0].Meta;
            var sql = new List<string>
            {
                $"SELECT {string.Join(", ", _select)}",
                $"FROM {Q(source.Schema)}.{Q(source.Name)} AS {Q("t0")}"
            };
            sql.AddRange(joinSql);
            if (where.Count > 0) sql.Add($"WHERE {string.Join(" AND ", where)}");
            if (groupOrdinals.Count > 0) sql.Add($"GROUP BY {string.Join(", ", groupOrdinals)}");
            if (order.Count > 0) sql.Add($"ORDER BY {string.Join(", ", order)}");
            sql.Add("LIMIT @p_limit");

            return new CompiledSelect(new CompiledQuery(string.Join("\n", sql), _params), _out);
        }

        // ---- tables and joins ------------------------------------------------------------------

        private TableMetadata FindTable(TableRef r) =>
            meta.FindTable(r.Schema, r.Table)
            ?? throw Bad($"{r.Schema}.{r.Table} is not in the schema snapshot. Try re-syncing.");

        private List<string> ResolveTables()
        {
            var joins = q.Joins ?? [];
            if (joins.Count + 1 > MaxTables) throw Bad($"At most {MaxTables} tables can be combined.");

            _tables.Add(new QTable(FindTable(q.Source), "t0"));
            var sql = new List<string>();

            foreach (var j in joins)
            {
                var target = FindTable(j.Table);
                if (_tables.Any(t => t.Meta.Schema == target.Schema && t.Meta.Name == target.Name))
                    throw Bad($"{target.Name} is already part of this question.");

                var from = _tables.FirstOrDefault(t => t.Meta.Schema == j.From.Schema && t.Meta.Name == j.From.Table)
                    ?? throw Bad($"Cannot add {target.Name} through {j.From.Table}: {j.From.Table} is not part of this question yet.");

                var alias = $"t{_tables.Count}";
                var on = JoinCondition(from, target, alias, j.ForeignKey);
                _tables.Add(new QTable(target, alias));
                sql.Add($"LEFT JOIN {Q(target.Schema)}.{Q(target.Name)} AS {Q(alias)} ON {on}");
            }

            return sql;
        }

        /// <summary>The foreign key can be on either side: from → target, or target → from.</summary>
        private static string JoinCondition(QTable from, TableMetadata target, string targetAlias, string fkName)
        {
            var fk = from.Meta.ForeignKeys.FirstOrDefault(f =>
                f.Name == fkName && f.RefSchema == target.Schema && f.RefTable == target.Name);
            if (fk is not null)
                return Pairs(from.Alias, fk.Columns, targetAlias, fk.RefColumns);

            fk = target.ForeignKeys.FirstOrDefault(f =>
                f.Name == fkName && f.RefSchema == from.Meta.Schema && f.RefTable == from.Meta.Name);
            if (fk is not null)
                return Pairs(targetAlias, fk.Columns, from.Alias, fk.RefColumns);

            throw Bad($"{from.Meta.Name} and {target.Name} are not linked by a foreign key called '{fkName}'.");
        }

        private static string Pairs(string leftAlias, IReadOnlyList<string> left, string rightAlias, IReadOnlyList<string> right) =>
            string.Join(" AND ", left.Select((c, i) => $"{Q(leftAlias)}.{Q(c)} = {Q(rightAlias)}.{Q(right[i])}"));

        private Resolved Resolve(ColumnRef r)
        {
            var table = r.Table is null
                ? _tables[0]
                : _tables.FirstOrDefault(t => t.Meta.Schema == r.Table.Schema && t.Meta.Name == r.Table.Table)
                  ?? throw Bad($"'{r.Table.Table}' is not part of this question. Add it first.");

            var column = table.Meta.Columns.FirstOrDefault(c => c.Name == r.Name)
                ?? throw Bad($"'{r.Name}' is not a column of {table.Meta.Name}.");
            return new Resolved(table, column);
        }

        // ---- the select list -------------------------------------------------------------------

        /// <summary>With several tables, names are prefixed (orders.status) so they cannot collide.</summary>
        private string OutputName(Resolved r) => _tables.Count > 1 ? r.Label : r.Column.Name;

        private void AddOutput(string expression, string name, ColumnKind kind)
        {
            var unique = name;
            for (var n = 2; !_outNames.Add(unique); n++) unique = $"{name}_{n}";
            _select.Add($"{expression} AS {Q(unique)}");
            _out.Add(new ResultColumn(unique, kind));
        }

        private void AddAggregation(Aggregation a)
        {
            Resolved Required(string what)
            {
                if (a.Column is null) throw Bad($"{what} needs a column.");
                return Resolve(a.Column);
            }

            string expr, name;
            ColumnKind kind = ColumnKind.Number;
            switch (a.Fn)
            {
                case AggFn.Count when a.Column is null:
                    expr = "COUNT(*)";
                    name = "count";
                    break;
                case AggFn.Count:
                {
                    var r = Resolve(a.Column!);
                    expr = $"COUNT({r.Sql})";
                    name = $"count_{r.Column.Name}";
                    break;
                }
                case AggFn.CountDistinct:
                {
                    var r = Required("Count of distinct values");
                    expr = $"COUNT(DISTINCT {r.Sql})";
                    name = $"distinct_{r.Column.Name}";
                    break;
                }
                case AggFn.Sum or AggFn.Avg:
                {
                    var word = a.Fn == AggFn.Sum ? "sum" : "average";
                    var r = Required($"The {word}");
                    if (r.Column.Kind != ColumnKind.Number)
                        throw Bad($"Cannot take the {word} of '{r.Label}': it is {r.Column.RawType}, not a number.");
                    expr = $"{(a.Fn == AggFn.Sum ? "SUM" : "AVG")}({r.Sql})";
                    name = $"{(a.Fn == AggFn.Sum ? "sum" : "avg")}_{r.Column.Name}";
                    break;
                }
                case AggFn.Min or AggFn.Max:
                {
                    var word = a.Fn == AggFn.Min ? "smallest" : "largest";
                    var r = Required($"The {word} value");
                    if (r.Column.Kind is ColumnKind.Boolean or ColumnKind.Other)
                        throw Bad($"Cannot take the {word} value of '{r.Label}': it is {r.Column.RawType}.");
                    expr = $"{(a.Fn == AggFn.Min ? "MIN" : "MAX")}({r.Sql})";
                    name = $"{(a.Fn == AggFn.Min ? "min" : "max")}_{r.Column.Name}";
                    kind = r.Column.Kind;
                    break;
                }
                default:
                    throw Bad("Unknown summary.");
            }

            if (!string.IsNullOrWhiteSpace(a.Alias))
            {
                name = a.Alias.Trim();
                if (name.Length > MaxAliasLength) throw Bad($"A summary name can be at most {MaxAliasLength} characters.");
            }
            AddOutput(expr, name, kind);
        }

        private static string BucketSql(DateBucket b) => b switch
        {
            DateBucket.Day => "day",
            DateBucket.Week => "week",
            DateBucket.Month => "month",
            DateBucket.Quarter => "quarter",
            DateBucket.Year => "year",
            _ => throw Bad("Unknown date grouping.")
        };

        // ---- filters ---------------------------------------------------------------------------

        private string P(object value)
        {
            var name = $"p{++_p}";
            _params[name] = value;
            return "@" + name;
        }

        private static readonly FilterOp[] Ordered = [FilterOp.Lt, FilterOp.Lte, FilterOp.Gt, FilterOp.Gte, FilterOp.Between];
        private static readonly FilterOp[] Texty = [FilterOp.Contains, FilterOp.StartsWith, FilterOp.EndsWith];

        private static bool Allowed(ColumnKind kind, FilterOp op)
        {
            if (op is FilterOp.IsNull or FilterOp.NotNull) return true;
            return kind switch
            {
                ColumnKind.Number or ColumnKind.Date or ColumnKind.Timestamp =>
                    op is FilterOp.Eq or FilterOp.NotEq or FilterOp.In || Ordered.Contains(op),
                ColumnKind.Text => op is FilterOp.Eq or FilterOp.NotEq or FilterOp.In || Texty.Contains(op),
                ColumnKind.Boolean => op is FilterOp.Eq or FilterOp.NotEq,
                _ => false
            };
        }

        private string BuildFilter(Filter f)
        {
            var r = Resolve(f.Column);
            if (!Allowed(r.Column.Kind, f.Op))
                throw Bad($"'{r.Label}' is {r.Column.RawType}, which cannot be filtered with '{f.Op.ToString().ToLowerInvariant()}'.");

            var values = f.Values ?? [];
            void Need(int n)
            {
                if (values.Count != n)
                    throw Bad($"The filter on '{r.Label}' needs {n} value{(n == 1 ? "" : "s")}.");
            }

            switch (f.Op)
            {
                case FilterOp.IsNull:
                    Need(0);
                    return $"{r.Sql} IS NULL";
                case FilterOp.NotNull:
                    Need(0);
                    return $"{r.Sql} IS NOT NULL";

                case FilterOp.In:
                {
                    if (values.Count is < 1 or > MaxInValues)
                        throw Bad($"The filter on '{r.Label}' needs between 1 and {MaxInValues} values.");
                    var parsed = values.Select(v => Scalar(r, v)).ToArray();
                    var array = Array.CreateInstance(parsed[0].GetType(), parsed.Length);
                    for (var i = 0; i < parsed.Length; i++) array.SetValue(parsed[i], i);
                    return $"{r.Sql} = ANY({P(array)})";
                }

                case FilterOp.Contains or FilterOp.StartsWith or FilterOp.EndsWith:
                {
                    Need(1);
                    var text = (string)Scalar(r, values[0]);
                    var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                    var pattern = f.Op switch
                    {
                        FilterOp.Contains => $"%{escaped}%",
                        FilterOp.StartsWith => $"{escaped}%",
                        _ => $"%{escaped}"
                    };
                    return $"{r.Sql} ILIKE {P(pattern)}";
                }

                case FilterOp.Between:
                {
                    Need(2);
                    var from = Scalar(r, values[0]);
                    var to = Scalar(r, values[1]);
                    // "to" a whole day on a timestamp column means up to the end of that day
                    return IsWholeDay(r, values[1])
                        ? $"({r.Sql} >= {P(from)} AND {r.Sql} < {P(NextDay(to))})"
                        : $"({r.Sql} BETWEEN {P(from)} AND {P(to)})";
                }

                default:
                {
                    Need(1);
                    var value = Scalar(r, values[0]);
                    if (IsWholeDay(r, values[0]))
                    {
                        // A date typed against a timestamp stands for the whole day, not for midnight.
                        return f.Op switch
                        {
                            FilterOp.Eq => $"({r.Sql} >= {P(value)} AND {r.Sql} < {P(NextDay(value))})",
                            FilterOp.NotEq => $"({r.Sql} < {P(value)} OR {r.Sql} >= {P(NextDay(value))})",
                            FilterOp.Lte => $"{r.Sql} < {P(NextDay(value))}",
                            FilterOp.Gt => $"{r.Sql} >= {P(NextDay(value))}",
                            FilterOp.Lt => $"{r.Sql} < {P(value)}",
                            _ => $"{r.Sql} >= {P(value)}"
                        };
                    }

                    var op = f.Op switch
                    {
                        FilterOp.Eq => "=",
                        FilterOp.NotEq => "<>",
                        FilterOp.Lt => "<",
                        FilterOp.Lte => "<=",
                        FilterOp.Gt => ">",
                        _ => ">="
                    };
                    return $"{r.Sql} {op} {P(value)}";
                }
            }
        }

        // ---- turning JSON values into typed parameters -----------------------------------------

        private static readonly Regex DateOnlyText = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

        private static bool IsWholeDay(Resolved r, JsonElement v) =>
            r.Column.Kind == ColumnKind.Timestamp && v.ValueKind == JsonValueKind.String
            && DateOnlyText.IsMatch(v.GetString()!);

        private static object NextDay(object v) => v switch
        {
            DateTimeOffset d => d.AddDays(1),
            DateTime d => d.AddDays(1),
            DateOnly d => d.AddDays(1),
            _ => v
        };

        private static object Scalar(Resolved r, JsonElement el)
        {
            DataLensException WrongType(string expected) =>
                Bad($"The filter on '{r.Label}' expected {expected} but got {Describe(el)}.");

            switch (r.Column.Kind)
            {
                case ColumnKind.Number:
                {
                    var raw = r.Column.RawType;
                    if (raw == "money") throw Bad($"Filtering '{r.Label}' (money) is not supported yet.");
                    var text = el.ValueKind switch
                    {
                        JsonValueKind.Number => el.GetRawText(),
                        JsonValueKind.String => el.GetString()!.Trim(),
                        _ => throw WrongType("a number")
                    };
                    if (raw is "smallint" or "integer" or "bigint")
                        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
                            ? l
                            : throw WrongType("a whole number");
                    if (raw is "real" or "double precision")
                        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                            ? d
                            : throw WrongType("a number");
                    return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                        ? m
                        : throw WrongType("a number");
                }

                case ColumnKind.Text:
                    return el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString()!,
                        JsonValueKind.Number => el.GetRawText(),
                        _ => throw WrongType("text")
                    };

                case ColumnKind.Boolean:
                    return el.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
                        _ => throw WrongType("true or false")
                    };

                case ColumnKind.Date:
                {
                    var s = el.ValueKind == JsonValueKind.String ? el.GetString()! : "";
                    return s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d
                        : throw WrongType("a date like 2026-03-31");
                }

                case ColumnKind.Timestamp:
                {
                    var s = el.ValueKind == JsonValueKind.String ? el.GetString()! : "";
                    if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                        throw WrongType("a date or date and time like 2026-03-31 or 2026-03-31T10:00:00Z");
                    // Both branches are cast to object on purpose: with the plain types, C# would quietly turn
                    // the DateTime into a DateTimeOffset and we would send the wrong parameter type.
                    return r.Column.RawType == "timestamp with time zone"
                        ? (object)t
                        : (object)DateTime.SpecifyKind(t.UtcDateTime, DateTimeKind.Unspecified);
                }

                default:
                    throw Bad($"'{r.Label}' ({r.Column.RawType}) cannot be filtered by value.");
            }
        }

        private static string Describe(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => $"\"{el.GetString()}\"",
            JsonValueKind.Null => "nothing",
            _ => el.GetRawText()
        };
    }
}
