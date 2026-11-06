using System.Security.Cryptography;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

public class DemoDashboardServiceTests : ServiceTestBase
{
    private DemoDashboardService Demo => new(Store, Store);

    private async Task ConnectShopAsync()
    {
        Connector.Metadata = Shop.Meta;
        await new ConnectionService(Store, Connector).ConnectAsync("shop", Cs);
    }

    [Fact]
    public async Task Builds_a_full_dashboard_of_twelve_cards()
    {
        await ConnectShopAsync();

        var (dashboard, created) = await Demo.CreateAsync("shop");

        Assert.True(created);
        Assert.Equal("Shop overview", dashboard.Name);
        Assert.Equal(12, dashboard.Cards.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, dashboard.Cards.Select(c => c.Position));

        var queries = await Store.ListQueriesAsync();
        var byType = queries.GroupBy(q => q.Chart.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, byType[ChartType.Number]);
        Assert.Equal(4, byType[ChartType.Line]);
        Assert.Equal(5, byType[ChartType.Bar]);
        Assert.Equal(1, byType[ChartType.Pie]);
        Assert.Equal(1, byType[ChartType.Table]);
    }

    [Fact]
    public async Task Every_card_is_a_valid_question_that_compiles()
    {
        await ConnectShopAsync();
        await Demo.CreateAsync("shop");

        foreach (var q in await Store.ListQueriesAsync())
            QueryCompiler.Compile(q.Query, Shop.Meta);   // would throw with a message if a card were broken
    }

    [Fact]
    public async Task Some_cards_combine_three_and_five_tables()
    {
        await ConnectShopAsync();
        await Demo.CreateAsync("shop");

        var tableCounts = (await Store.ListQueriesAsync()).ToDictionary(q => q.Name, q => 1 + (q.Query.Joins?.Count ?? 0));

        Assert.Equal(2, tableCounts["Orders by country"]);
        Assert.Equal(3, tableCounts["Units sold by category"]);
        Assert.Equal(5, tableCounts["Coffee units sold by country"]);
        Assert.All(tableCounts.Values, n => Assert.InRange(n, 1, QueryCompiler.MaxTables));
    }

    [Fact]
    public async Task The_overview_card_is_one_row_of_four_figures()
    {
        await ConnectShopAsync();
        await Demo.CreateAsync("shop");

        var overview = (await Store.ListQueriesAsync()).Single(q => q.Name == "Overview");
        var compiled = QueryCompiler.Compile(overview.Query, Shop.Meta);

        Assert.Equal(ChartType.Number, overview.Chart.Type);
        Assert.Equal(new[] { "Orders", "Revenue", "Average order value", "Customers" }, compiled.Columns.Select(c => c.Name));
        Assert.Null(overview.Query.GroupBy);   // no groups: a single row
    }

    [Fact]
    public async Task Asking_again_returns_the_same_dashboard_and_adds_nothing()
    {
        await ConnectShopAsync();
        var (first, _) = await Demo.CreateAsync("shop");

        var (second, created) = await Demo.CreateAsync("shop");

        Assert.False(created);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(12, (await Store.ListQueriesAsync()).Count);
        Assert.Single(await Store.ListDashboardsAsync());
    }

    [Fact]
    public async Task A_database_that_is_not_the_sample_shop_is_refused_and_nothing_is_left_behind()
    {
        await new ConnectionService(Store, Connector).ConnectAsync("other", Cs);   // the base class's one-table "people" schema

        var ex = await Assert.ThrowsAsync<DataLensException>(() => Demo.CreateAsync("other"));

        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Contains("does not look like the sample shop", ex.Message);
        Assert.Empty(await Store.ListDashboardsAsync());
        Assert.Empty(await Store.ListQueriesAsync());
    }

    [Fact]
    public async Task A_shop_missing_a_link_between_tables_is_refused_whole()
    {
        // same tables, but products no longer point at categories
        var broken = new DbMetadata(Shop.Meta.Tables.Select(t =>
            t.Name == "products" ? t with { ForeignKeys = Array.Empty<ForeignKeyMetadata>() } : t).ToList());
        Connector.Metadata = broken;
        await new ConnectionService(Store, Connector).ConnectAsync("shop", Cs);

        var ex = await Assert.ThrowsAsync<DataLensException>(() => Demo.CreateAsync("shop"));

        Assert.Contains("not linked by a foreign key", ex.Message);
        Assert.Empty(await Store.ListDashboardsAsync());
    }

    [Fact]
    public async Task An_unknown_connection_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Demo.CreateAsync("nope"));
        Assert.Equal(DataLensError.NotFound, ex.Error);
    }
}

public sealed class PostgresDemoDashboardTests : IDisposable
{
    private static string RO => PostgresConnectorTests.ReadOnly!;

    private readonly PostgresConnector _connector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(SqliteAppStore Store, QuestionService Questions, Dashboard Dashboard)> SeedAsync()
    {
        var store = new SqliteAppStore(Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        await new ConnectionService(store, _connector).ConnectAsync("shop", RO);
        var (dashboard, _) = await new DemoDashboardService(store, store).CreateAsync("shop");
        return (store, new QuestionService(store, _connector), dashboard);
    }

    private async Task<List<object?[]>> ExpectedAsync(string sql) =>
        (await _connector.ExecuteAsync(RO, new CompiledQuery(sql, new Dictionary<string, object?>()), new ExecOptions(TimeSpan.FromSeconds(15), 10_000))).Rows.ToList();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [PostgresConnectorTests.PgFact]
    public async Task Every_card_runs_against_the_real_database_under_every_time_setting()
    {
        var (store, questions, dashboard) = await SeedAsync();

        var settings = new TimeFilter?[]
        {
            null,                                                                                  // as saved
            new(new DateOnly(Today.Year, Today.Month, 1).AddMonths(-11), Today, DateBucket.Month), // the default: 12 months by month
            new(null, null, DateBucket.Week),
            new(Today.AddDays(-6), Today, DateBucket.Day),
            new(Today, Today, DateBucket.Day),                                                     // a single day, probably empty
            new(Today.AddDays(-400), Today, DateBucket.Year),
            new(Today.AddDays(30), Today.AddDays(60), DateBucket.Month)                            // the future: nothing, and no error
        };

        foreach (var card in dashboard.Cards)
        {
            var saved = (await store.GetQueryAsync(card.QueryId))!;
            foreach (var time in settings)
            {
                var result = await questions.RunAsync("shop", saved.Query, time);
                Assert.NotNull(result.Columns);   // ran, with no exception
            }
        }
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_overview_figures_match_hand_written_sql()
    {
        var (store, questions, dashboard) = await SeedAsync();
        var overview = (await store.ListQueriesAsync()).Single(q => q.Name == "Overview");

        var row = Assert.Single((await questions.RunAsync("shop", overview.Query)).Rows);
        var expected = (await ExpectedAsync("SELECT count(*), sum(total), avg(total), count(DISTINCT user_id) FROM order_totals"))[0];

        Assert.Equal(Convert.ToInt64(expected[0]), Convert.ToInt64(row[0]));
        Assert.Equal(Convert.ToDecimal(expected[1]), Convert.ToDecimal(row[1]));
        Assert.Equal(Convert.ToDecimal(expected[2]), Convert.ToDecimal(row[2]));
        Assert.Equal(Convert.ToInt64(expected[3]), Convert.ToInt64(row[3]));
        Assert.Equal(2000, Convert.ToInt64(row[0]));
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_three_and_five_table_cards_match_hand_written_sql()
    {
        var (store, questions, _) = await SeedAsync();
        var all = (await store.ListQueriesAsync()).ToDictionary(q => q.Name);

        var byCategory = await questions.RunAsync("shop", all["Units sold by category"].Query);
        var expected3 = await ExpectedAsync(@"
            SELECT c.name, sum(i.quantity) FROM order_items i JOIN products p ON p.id = i.product_id
            JOIN categories c ON c.id = p.category_id GROUP BY c.name ORDER BY sum(i.quantity) DESC, c.name");
        Assert.Equal(expected3.Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))).OrderBy(x => x.Item1),
            byCategory.Rows.Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))).OrderBy(x => x.Item1));
        Assert.Equal(byCategory.Rows.Select(r => Convert.ToInt64(r[1])).OrderByDescending(x => x), byCategory.Rows.Select(r => Convert.ToInt64(r[1])));   // sorted by units
        Assert.Equal(Convert.ToInt64((await ExpectedAsync("SELECT sum(quantity) FROM order_items"))[0][0]), byCategory.Rows.Sum(r => Convert.ToInt64(r[1])));

        var coffee = await questions.RunAsync("shop", all["Coffee units sold by country"].Query);
        var expected5 = await ExpectedAsync(@"
            SELECT u.country, sum(i.quantity) FROM order_items i
            JOIN orders o ON o.id = i.order_id JOIN users u ON u.id = o.user_id
            JOIN products p ON p.id = i.product_id JOIN categories c ON c.id = p.category_id
            WHERE c.name = 'Coffee' GROUP BY u.country");
        Assert.Equal(expected5.Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))).OrderBy(x => x.Item1),
            coffee.Rows.Select(r => ((string)r[0]!, Convert.ToInt64(r[1]))).OrderBy(x => x.Item1));
        Assert.True(coffee.Rows.Count >= 4);
        Assert.True(coffee.Rows.Sum(r => Convert.ToInt64(r[1])) < byCategory.Rows.Sum(r => Convert.ToInt64(r[1])));   // coffee is a slice of all units
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_simple_cards_add_up()
    {
        var (store, questions, _) = await SeedAsync();
        var all = (await store.ListQueriesAsync()).ToDictionary(q => q.Name);

        Assert.Equal(2000, (await questions.RunAsync("shop", all["Orders per month"].Query)).Rows.Sum(r => Convert.ToInt64(r[1])));
        Assert.Equal(2000, (await questions.RunAsync("shop", all["Orders by status"].Query)).Rows.Sum(r => Convert.ToInt64(r[1])));
        Assert.Equal(2000, (await questions.RunAsync("shop", all["Orders by country"].Query)).Rows.Sum(r => Convert.ToInt64(r[1])));
        Assert.Equal(200, (await questions.RunAsync("shop", all["New customers per month"].Query)).Rows.Sum(r => Convert.ToInt64(r[1])));
        Assert.Equal(1000, (await questions.RunAsync("shop", all["Activity by type"].Query)).Rows.Sum(r => Convert.ToInt64(r[1])));

        var top = await questions.RunAsync("shop", all["Top products"].Query);
        Assert.Equal(10, top.Rows.Count);
        Assert.True(top.LimitReached);

        var latest = await questions.RunAsync("shop", all["Latest orders"].Query);
        Assert.Equal(8, latest.Rows.Count);
        Assert.Equal(new[] { "orders.id", "orders.status", "orders.created_at", "users.name", "users.country" }, latest.Columns.Select(c => c.Name));
        var times = latest.Rows.Select(r => DateTimeOffset.Parse((string)r[2]!)).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);   // newest first
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_dashboard_range_shrinks_the_cards_that_have_a_date_and_leaves_the_rest()
    {
        var (store, questions, _) = await SeedAsync();
        var all = (await store.ListQueriesAsync()).ToDictionary(q => q.Name);
        var lastWeek = new TimeFilter(Today.AddDays(-6), Today, DateBucket.Day);

        var orders = await questions.RunAsync("shop", all["Orders by status"].Query, lastWeek);
        Assert.InRange(orders.Rows.Sum(r => Convert.ToInt64(r[1])), 1, 1999);
        Assert.Equal("orders.created_at", orders.Time!.Column);

        var units = await questions.RunAsync("shop", all["Units sold by category"].Query, lastWeek);
        Assert.Null(units.Time!.Column);   // order_items has no date column, so the range is not applied to it
        Assert.Equal(
            Convert.ToInt64((await ExpectedAsync("SELECT sum(quantity) FROM order_items"))[0][0]),
            units.Rows.Sum(r => Convert.ToInt64(r[1])));
    }
}
