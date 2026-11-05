using System.Text.Json;
using System.Text.RegularExpressions;
using DataLens.Core.Models;

namespace DataLens.Core.Query;

/// <summary>A dashboard's time controls: which dates to look at, and how to bucket a trend.</summary>
/// <param name="From">First day to include. Null means no start.</param>
/// <param name="To">Last day to include, the whole day. Null means no end.</param>
/// <param name="Granularity">Day, week, month and so on. Replaces the period of a question that groups by time.</param>
public sealed record TimeFilter(DateOnly? From = null, DateOnly? To = null, DateBucket? Granularity = null)
{
    public bool IsEmpty => From is null && To is null && Granularity is null;
}

/// <param name="Column">The date column the range was applied to, like orders.created_at. Null when the question has none.</param>
public sealed record TimeInfo(string? Column, bool RangeApplied, bool GranularityApplied);

/// <summary>
/// Applies a dashboard's time controls to a question when it runs. The saved question is never changed:
/// opening it on its own still shows exactly what it was saved with.
///
/// The time column is the one the question already groups by period; otherwise the source table's most
/// date-like column (created_at, ordered_on...). A table with no date column is left alone.
/// </summary>
public static class TimeScope
{
    private static readonly Regex DateLike = new(@"(^|_)(created|updated|ordered|placed|date|time|at|on)($|_)|_at$|_on$|date$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (QueryDefinition Query, TimeInfo Info) Apply(QueryDefinition query, DbMetadata metadata, TimeFilter? filter)
    {
        var none = new TimeInfo(null, false, false);
        if (filter is null || filter.IsEmpty) return (query, none);

        if (filter is { From: { } from, To: { } to } && from > to)
            throw new DataLensException(DataLensError.BadRequest, "The start date is after the end date.");

        // an unknown source table is the compiler's to report, with its usual message
        var source = metadata.FindTable(query.Source.Schema, query.Source.Table);
        if (source is null) return (query, none);

        // the column the question already buckets by time, else the source table's best date column
        var bucketed = query.GroupBy?.FirstOrDefault(g => g.Bucket is not null);
        var column = bucketed?.Column;
        if (column is null && PickTimeColumn(source) is { } picked) column = new ColumnRef(picked.Name);
        if (column is null) return (query, none);

        var label = $"{column.Table?.Table ?? source.Name}.{column.Name}";

        var granularityApplied = false;
        var groupBy = query.GroupBy;
        if (filter.Granularity is { } granularity && groupBy is not null)
        {
            groupBy = groupBy.Select(g =>
            {
                if (g.Bucket is null || !SameColumn(g.Column, column)) return g;
                granularityApplied = true;
                return g with { Bucket = granularity };
            }).ToList();
        }

        var filters = (query.Filters ?? []).ToList();
        var rangeApplied = filter.From is not null || filter.To is not null;
        if (rangeApplied) filters.Add(RangeFilter(column, filter.From, filter.To));

        return (query with { GroupBy = groupBy, Filters = filters }, new TimeInfo(label, rangeApplied, granularityApplied));
    }

    /// <summary>The date or timestamp column most likely to say "when": created_at beats an arbitrary first date.</summary>
    public static ColumnMetadata? PickTimeColumn(TableMetadata table)
    {
        var dates = table.Columns.Where(c => c.Kind is ColumnKind.Date or ColumnKind.Timestamp).ToList();
        return dates.FirstOrDefault(c => DateLike.IsMatch(c.Name)) ?? dates.FirstOrDefault();
    }

    private static bool SameColumn(ColumnRef a, ColumnRef b) =>
        a.Name == b.Name
        && (a.Table is null && b.Table is null
            || a.Table is not null && b.Table is not null && a.Table.Schema == b.Table.Schema && a.Table.Table == b.Table.Table);

    private static Filter RangeFilter(ColumnRef column, DateOnly? from, DateOnly? to)
    {
        static JsonElement Day(DateOnly d) => JsonSerializer.SerializeToElement(d.ToString("yyyy-MM-dd"));

        // a day against a timestamp column means the whole day, so "to" includes its last hour
        return (from, to) switch
        {
            ({ } f, { } t) => new Filter(column, FilterOp.Between, new[] { Day(f), Day(t) }),
            ({ } f, null) => new Filter(column, FilterOp.Gte, new[] { Day(f) }),
            (null, { } t) => new Filter(column, FilterOp.Lte, new[] { Day(t) }),
            _ => throw new InvalidOperationException("A range needs at least one end.")
        };
    }
}
