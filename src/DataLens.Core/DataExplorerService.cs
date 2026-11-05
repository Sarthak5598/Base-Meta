using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Core;

public sealed record PreviewRequest(
    string Connection,
    string Schema,
    string Table,
    int Page = 1,
    int PageSize = DataExplorerService.DefaultPageSize,
    string? SortColumn = null,
    SortDirection SortDirection = SortDirection.Asc);

public sealed record ExplorerPage(
    string Schema,
    string Table,
    IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<object?[]> Rows,
    int Page,
    int PageSize,
    bool HasMore,
    long DurationMs,
    int SnapshotVersion);

/// <summary>
/// Browse a table page by page. Everything is checked against the schema snapshot first, so a table or
/// column that is not in the snapshot never reaches SQL.
/// </summary>
public sealed class DataExplorerService(IAppStore store, IDatabaseConnector connector)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>OFFSET paging gets slower the deeper it goes. Past this, the user should sort or filter.</summary>
    public const int MaxOffset = 100_000;

    public async Task<ExplorerPage> PreviewAsync(PreviewRequest request, CancellationToken ct = default)
    {
        if (request.Page < 1)
            throw new DataLensException(DataLensError.BadRequest, "Page must be 1 or more.");
        if (request.PageSize is < 1 or > MaxPageSize)
            throw new DataLensException(DataLensError.BadRequest, $"Page size must be between 1 and {MaxPageSize}.");

        var offset = (request.Page - 1) * request.PageSize;
        if (offset > MaxOffset)
            throw new DataLensException(
                DataLensError.BadRequest,
                $"Browsing stops at row {MaxOffset:N0}. Sort the table to bring the rows you want to the front.");

        var (connection, snapshot) = await SnapshotLookup.LoadAsync(store, request.Connection, ct);

        var table = snapshot.Metadata.FindTable(request.Schema, request.Table)
            ?? throw new DataLensException(
                DataLensError.NotFound, $"{request.Schema}.{request.Table} is not in the schema snapshot. Try re-syncing.");

        if (request.SortColumn is not null && table.Columns.All(c => c.Name != request.SortColumn))
            throw new DataLensException(DataLensError.BadRequest, $"'{request.SortColumn}' is not a column of {table.Name}.");

        var connectionString = await store.GetConnectionStringAsync(connection.Id, ct)
            ?? throw new DataLensException(DataLensError.NotFound, "The saved connection string is missing.");

        // Ask for one row more than the page: if it comes back, there is a next page.
        var query = PreviewCompiler.Compile(
            table, request.SortColumn, request.SortDirection, request.PageSize + 1, offset);
        var result = await connector.ExecuteAsync(
            connectionString, query, ExecOptions.Default with { MaxRows = request.PageSize + 1 }, ct);

        return new ExplorerPage(
            table.Schema, table.Name, table.Columns,
            result.Rows.Take(request.PageSize).ToList(),
            request.Page, request.PageSize,
            HasMore: result.Rows.Count > request.PageSize,
            result.DurationMs, snapshot.Version);
    }
}
