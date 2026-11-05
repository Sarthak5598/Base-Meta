using System.Security.Cryptography;
using System.Text.Json;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

public sealed class ContentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAppStore _store;
    private string _connectionId = "";

    public ContentStoreTests() =>
        _store = new SqliteAppStore(Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> ConnectionAsync(string name = "shop") =>
        (await _store.SaveConnectionAsync(name, "Host=h;Database=d;Username=u;Password=p", false)).Id;

    private static QueryDefinition Query(string table = "orders") => new(
        new TableRef("public", table),
        Filters: new[]
        {
            new Filter(new ColumnRef("status"), FilterOp.In,
                new[] { JsonSerializer.SerializeToElement("paid"), JsonSerializer.SerializeToElement("shipped") })
        },
        GroupBy: new[] { new Grouping(new ColumnRef("created_at"), DateBucket.Month) },
        Aggregations: new[] { new Aggregation(AggFn.Count) },
        OrderBy: new[] { new SortSpec("created_at") },
        Limit: 12);

    // ---- saved questions -------------------------------------------------------------------

    [Fact]
    public async Task A_saved_question_comes_back_exactly_as_saved()
    {
        _connectionId = await ConnectionAsync();
        var chart = new ChartConfig(ChartType.Line, "created_at", "count");

        var saved = await _store.SaveQueryAsync(_connectionId, "Orders per month", Query(), chart);
        var loaded = (await _store.GetQueryAsync(saved.Id))!;

        Assert.StartsWith("q_", loaded.Id);
        Assert.Equal("Orders per month", loaded.Name);
        Assert.Equal(_connectionId, loaded.ConnectionId);
        Assert.Equal(chart, loaded.Chart);
        Assert.Equal(DateBucket.Month, loaded.Query.GroupBy![0].Bucket);
        Assert.Equal(AggFn.Count, loaded.Query.Aggregations![0].Fn);
        Assert.Equal(12, loaded.Query.Limit);
        // filter values survive as JSON values
        Assert.Equal(new[] { "paid", "shipped" }, loaded.Query.Filters![0].Values!.Select(v => v.GetString()));
    }

    [Fact]
    public async Task Lists_newest_first_and_can_be_limited_to_one_connection()
    {
        var a = await ConnectionAsync("a");
        var b = await ConnectionAsync("b");
        await _store.SaveQueryAsync(a, "first", Query(), new ChartConfig());
        await Task.Delay(5);
        await _store.SaveQueryAsync(b, "second", Query(), new ChartConfig());
        await Task.Delay(5);
        await _store.SaveQueryAsync(a, "third", Query(), new ChartConfig());

        Assert.Equal(new[] { "third", "second", "first" }, (await _store.ListQueriesAsync()).Select(q => q.Name));
        Assert.Equal(new[] { "third", "first" }, (await _store.ListQueriesAsync(a)).Select(q => q.Name));
    }

    [Fact]
    public async Task Update_changes_the_question_and_keeps_the_id()
    {
        _connectionId = await ConnectionAsync();
        var saved = await _store.SaveQueryAsync(_connectionId, "old", Query(), new ChartConfig());

        var updated = (await _store.UpdateQueryAsync(saved.Id, "new", Query("users"), new ChartConfig(ChartType.Pie, "a", "b")))!;

        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal("new", updated.Name);
        Assert.Equal("users", updated.Query.Source.Table);
        Assert.Equal(ChartType.Pie, updated.Chart.Type);
        Assert.Equal(saved.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Missing_questions_are_reported_not_invented()
    {
        Assert.Null(await _store.GetQueryAsync("q_nope"));
        Assert.Null(await _store.UpdateQueryAsync("q_nope", "x", Query(), new ChartConfig()));
        Assert.False(await _store.DeleteQueryAsync("q_nope"));
    }

    [Fact]
    public async Task Deleting_a_connection_is_not_possible_with_questions_orphaned()
    {
        // a question must belong to a real connection
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _store.SaveQueryAsync("c_does_not_exist", "x", Query(), new ChartConfig()));
    }

    // ---- dashboards ------------------------------------------------------------------------

    [Fact]
    public async Task Creates_and_lists_dashboards()
    {
        var d = await _store.CreateDashboardAsync("Sales");
        Assert.StartsWith("d_", d.Id);
        Assert.Empty(d.Cards);

        var list = await _store.ListDashboardsAsync();
        Assert.Equal("Sales", Assert.Single(list).Name);
        Assert.Equal("Sales", (await _store.GetDashboardAsync(d.Id))!.Name);
    }

    [Fact]
    public async Task Cards_keep_the_order_they_were_added_in()
    {
        _connectionId = await ConnectionAsync();
        var q1 = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var q2 = await _store.SaveQueryAsync(_connectionId, "two", Query(), new ChartConfig());
        var q3 = await _store.SaveQueryAsync(_connectionId, "three", Query(), new ChartConfig());
        var d = await _store.CreateDashboardAsync("Sales");

        await _store.AddCardAsync(d.Id, q2.Id);
        await _store.AddCardAsync(d.Id, q3.Id);
        var after = (await _store.AddCardAsync(d.Id, q1.Id))!;

        Assert.Equal(new[] { q2.Id, q3.Id, q1.Id }, after.Cards.Select(c => c.QueryId));
        Assert.Equal(new[] { 0, 1, 2 }, after.Cards.Select(c => c.Position));
    }

    [Fact]
    public async Task Adding_the_same_question_twice_changes_nothing()
    {
        _connectionId = await ConnectionAsync();
        var q = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var d = await _store.CreateDashboardAsync("Sales");

        await _store.AddCardAsync(d.Id, q.Id);
        var again = (await _store.AddCardAsync(d.Id, q.Id))!;
        Assert.Single(again.Cards);
    }

    [Fact]
    public async Task Cannot_add_a_card_for_something_that_does_not_exist()
    {
        _connectionId = await ConnectionAsync();
        var q = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var d = await _store.CreateDashboardAsync("Sales");

        Assert.Null(await _store.AddCardAsync("d_nope", q.Id));
        Assert.Null(await _store.AddCardAsync(d.Id, "q_nope"));
        Assert.Empty((await _store.GetDashboardAsync(d.Id))!.Cards);
    }

    [Fact]
    public async Task Removing_a_card_leaves_the_question_alone()
    {
        _connectionId = await ConnectionAsync();
        var q = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var d = await _store.CreateDashboardAsync("Sales");
        await _store.AddCardAsync(d.Id, q.Id);

        var after = (await _store.RemoveCardAsync(d.Id, q.Id))!;

        Assert.Empty(after.Cards);
        Assert.NotNull(await _store.GetQueryAsync(q.Id));
    }

    [Fact]
    public async Task Deleting_a_question_removes_it_from_every_dashboard()
    {
        _connectionId = await ConnectionAsync();
        var keep = await _store.SaveQueryAsync(_connectionId, "keep", Query(), new ChartConfig());
        var drop = await _store.SaveQueryAsync(_connectionId, "drop", Query(), new ChartConfig());
        var d1 = await _store.CreateDashboardAsync("one");
        var d2 = await _store.CreateDashboardAsync("two");
        foreach (var d in new[] { d1, d2 })
        {
            await _store.AddCardAsync(d.Id, drop.Id);
            await _store.AddCardAsync(d.Id, keep.Id);
        }

        Assert.True(await _store.DeleteQueryAsync(drop.Id));

        foreach (var d in new[] { d1, d2 })
            Assert.Equal(new[] { keep.Id }, (await _store.GetDashboardAsync(d.Id))!.Cards.Select(c => c.QueryId));
    }

    [Fact]
    public async Task Deleting_a_dashboard_keeps_its_questions()
    {
        _connectionId = await ConnectionAsync();
        var q = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var d = await _store.CreateDashboardAsync("Sales");
        await _store.AddCardAsync(d.Id, q.Id);

        Assert.True(await _store.DeleteDashboardAsync(d.Id));

        Assert.Null(await _store.GetDashboardAsync(d.Id));
        Assert.NotNull(await _store.GetQueryAsync(q.Id));
        Assert.False(await _store.DeleteDashboardAsync(d.Id));
    }

    [Fact]
    public async Task Dashboards_list_their_own_cards_only()
    {
        _connectionId = await ConnectionAsync();
        var q1 = await _store.SaveQueryAsync(_connectionId, "one", Query(), new ChartConfig());
        var q2 = await _store.SaveQueryAsync(_connectionId, "two", Query(), new ChartConfig());
        var a = await _store.CreateDashboardAsync("a");
        var b = await _store.CreateDashboardAsync("b");
        await _store.AddCardAsync(a.Id, q1.Id);
        await _store.AddCardAsync(b.Id, q2.Id);

        var all = (await _store.ListDashboardsAsync()).ToDictionary(d => d.Name);
        Assert.Equal(new[] { q1.Id }, all["a"].Cards.Select(c => c.QueryId));
        Assert.Equal(new[] { q2.Id }, all["b"].Cards.Select(c => c.QueryId));
    }
}
