using DataLens.Core.Query;

namespace DataLens.Core.Models;

public enum ChartType
{
    Table,
    Bar,
    Line,
    Pie,

    /// <summary>One headline figure, or a few side by side when the result has several numbers in one row.</summary>
    Number
}

/// <summary>How to draw a result. A chart is the result plus a type plus which columns go where.</summary>
/// <param name="X">Result column for the horizontal axis, or the slice names of a pie.</param>
/// <param name="Y">Result column holding the numbers.</param>
public sealed record ChartConfig(ChartType Type = ChartType.Table, string? X = null, string? Y = null);

/// <summary>A saved question: the query model and the chart choice. Results are never stored.</summary>
public sealed record SavedQuery(
    string Id,
    string ConnectionId,
    string Name,
    QueryDefinition Query,
    ChartConfig Chart,
    DateTimeOffset CreatedAt);

public sealed record DashboardCard(string QueryId, int Position);

public sealed record Dashboard(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DashboardCard> Cards);
