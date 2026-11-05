using System.Security.Cryptography;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

/// <summary>A connector that records what it was asked and returns canned answers.</summary>
internal sealed class FakeConnector : IDatabaseConnector
{
    public ConnectionTestResult TestResult { get; set; } = new(true, null, false, Array.Empty<string>());
    public DbMetadata Metadata { get; set; } = new(Array.Empty<TableMetadata>());
    public int RowsToReturn { get; set; } = 3;
    public int DescribeCalls { get; private set; }
    public CompiledQuery? LastQuery { get; private set; }
    public ExecOptions? LastOptions { get; private set; }
    public List<CompiledQuery> Queries { get; } = new();
    /// <summary>When set, decides what the "database" answers.</summary>
    public Func<CompiledQuery, QueryResult>? OnExecute { get; set; }

    public Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult(TestResult);

    public Task<DbMetadata> DescribeAsync(string connectionString, CancellationToken ct = default)
    {
        DescribeCalls++;
        return Task.FromResult(Metadata);
    }

    public Task<QueryResult> ExecuteAsync(
        string connectionString, CompiledQuery query, ExecOptions options, CancellationToken ct = default)
    {
        LastQuery = query;
        LastOptions = options;
        Queries.Add(query);
        if (OnExecute is not null) return Task.FromResult(OnExecute(query));
        var rows = Enumerable.Range(1, Math.Min(RowsToReturn, options.MaxRows))
            .Select(i => new object?[] { i, "row " + i }).ToList();
        return Task.FromResult(new QueryResult(new[] { "id", "name" }, rows, false, 7));
    }
}

public abstract class ServiceTestBase : IDisposable
{
    protected const string Cs = "Host=db;Database=shop;Username=ro;Password=pw";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));
    internal readonly FakeConnector Connector = new();
    protected readonly SqliteAppStore Store;

    protected ServiceTestBase()
    {
        Store = new SqliteAppStore(Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        Connector.Metadata = new DbMetadata(new[]
        {
            new TableMetadata("public", "people", TableKind.Table,
                new[]
                {
                    new ColumnMetadata("id", "integer", ColumnKind.Number, false, true),
                    new ColumnMetadata("name", "text", ColumnKind.Text, true, false)
                },
                new[] { "id" }, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>())
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public class ConnectionServiceTests : ServiceTestBase
{
    private ConnectionService Service => new(Store, Connector);

    [Fact]
    public async Task Connect_tests_saves_and_syncs()
    {
        var result = await Service.ConnectAsync("shop", Cs);

        Assert.True(result.Test.Ok);
        Assert.Equal("shop", result.Connection!.Name);
        Assert.Equal(1, result.Snapshot!.Version);
        Assert.Single(result.Snapshot.Metadata.Tables);
        Assert.NotNull(await Store.GetLatestSnapshotAsync(result.Connection.Id));
    }

    [Fact]
    public async Task A_failed_test_saves_nothing()
    {
        Connector.TestResult = ConnectionTestResult.Failed("Wrong username or password.");

        var result = await Service.ConnectAsync("shop", Cs);

        Assert.False(result.Test.Ok);
        Assert.Null(result.Connection);
        Assert.Empty(await Store.ListConnectionsAsync());
        Assert.Equal(0, Connector.DescribeCalls);
    }

    [Fact]
    public async Task The_write_access_flag_is_saved_with_the_connection()
    {
        Connector.TestResult = new(true, null, true, new[] { "careful" });
        var result = await Service.ConnectAsync("shop", Cs);
        Assert.True(result.Connection!.CanWrite);
        Assert.Equal(new[] { "careful" }, result.Test.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_is_rejected(string name)
    {
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Service.ConnectAsync(name, Cs));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
    }

    [Fact]
    public async Task Sync_again_bumps_the_version()
    {
        var first = await Service.ConnectAsync("shop", Cs);
        var second = await Service.SyncAsync("shop");
        Assert.Equal(first.Snapshot!.Version + 1, second.Version);
    }

    [Fact]
    public async Task Sync_of_an_unknown_connection_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Service.SyncAsync("nope"));
        Assert.Equal(DataLensError.NotFound, ex.Error);
    }
}

public class DataExplorerServiceTests : ServiceTestBase
{
    private DataExplorerService Explorer => new(Store, Connector);

    private async Task ConnectAsync() => await new ConnectionService(Store, Connector).ConnectAsync("shop", Cs);

    private static PreviewRequest Req(int page = 1, int size = 2, string? sort = null) =>
        new("shop", "public", "people", page, size, sort);

    [Fact]
    public async Task Returns_one_page_and_reports_whether_there_is_another()
    {
        await ConnectAsync();
        Connector.RowsToReturn = 10;

        var page = await Explorer.PreviewAsync(Req(page: 1, size: 2));

        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.HasMore);
        Assert.Equal(new[] { "id", "name" }, page.Columns.Select(c => c.Name));
        Assert.Equal(7, page.DurationMs);
    }

    [Fact]
    public async Task The_last_page_has_no_more()
    {
        await ConnectAsync();
        Connector.RowsToReturn = 2;
        Assert.False((await Explorer.PreviewAsync(Req(size: 2))).HasMore);
    }

    [Fact]
    public async Task Asks_for_one_extra_row_and_caps_reading_at_that()
    {
        await ConnectAsync();
        await Explorer.PreviewAsync(Req(page: 3, size: 2));

        Assert.Equal(3, Connector.LastOptions!.MaxRows);
        Assert.Equal(3, Connector.LastQuery!.Parameters["p_limit"]);
        Assert.Equal(4, Connector.LastQuery.Parameters["p_offset"]);
    }

    [Fact]
    public async Task Sort_is_passed_through_for_a_real_column()
    {
        await ConnectAsync();
        await Explorer.PreviewAsync(Req(sort: "name"));
        Assert.Contains("ORDER BY \"name\"", Connector.LastQuery!.Sql);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, DataExplorerService.MaxPageSize + 1)]
    [InlineData(1_000_000, 10)]
    public async Task Rejects_bad_paging_before_touching_the_database(int page, int size)
    {
        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Explorer.PreviewAsync(Req(page, size)));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Null(Connector.LastQuery);
    }

    [Fact]
    public async Task Rejects_a_sort_column_that_is_not_in_the_snapshot()
    {
        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Explorer.PreviewAsync(Req(sort: "id; DROP TABLE people")));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Null(Connector.LastQuery);
    }

    [Fact]
    public async Task A_table_that_is_not_in_the_snapshot_is_not_found_and_never_queried()
    {
        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DataLensException>(() =>
            Explorer.PreviewAsync(new PreviewRequest("shop", "public", "secrets")));
        Assert.Equal(DataLensError.NotFound, ex.Error);
        Assert.Null(Connector.LastQuery);
    }

    [Fact]
    public async Task Unknown_connection_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Explorer.PreviewAsync(Req()));
        Assert.Equal(DataLensError.NotFound, ex.Error);
    }

    [Fact]
    public async Task A_connection_that_was_never_synced_says_so()
    {
        await Store.SaveConnectionAsync("shop", Cs, false);
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Explorer.PreviewAsync(Req()));
        Assert.Equal(DataLensError.NotFound, ex.Error);
        Assert.Contains("synced", ex.Message);
    }
}
