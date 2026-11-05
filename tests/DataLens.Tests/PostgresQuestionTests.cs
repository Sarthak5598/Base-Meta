using System.Security.Cryptography;
using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

/// <summary>
/// Questions run against the real sample database, and every answer is checked against hand-written SQL,
/// so a compiler that builds valid but wrong SQL cannot pass.
/// </summary>
public sealed class PostgresQuestionTests : IDisposable
{
    private static string RO => PostgresConnectorTests.ReadOnly!;

    private readonly PostgresConnector _connector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
    private static IReadOnlyList<JsonElement> V(params object?[] v) => v.Select(J).ToList();
    private static TableRef T(string t) => new("public", t);
    private static ColumnRef C(string name, string? table = null) => new(name, table is null ? null : T(table));

    private async Task<QuestionService> ServiceAsync()
    {
        var store = new SqliteAppStore(Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        await new ConnectionService(store, _connector).ConnectAsync("shop", RO);
        return new QuestionService(store, _connector);
    }

    private Task<QuestionResult> RunAsync(QueryDefinition q) => ServiceAsync().ContinueWith(t => t.Result.RunAsync("shop", q)).Unwrap();

    /// <summary>The expected answer, from SQL written by hand.</summary>
    private async Task<List<object?[]>> ExpectedAsync(string sql)
    {
        var result = await _connector.ExecuteAsync(
            RO, new CompiledQuery(sql, new Dictionary<string, object?>()), new ExecOptions(TimeSpan.FromSeconds(15), 10_000));
        return result.Rows.ToList();
    }

    private static long L(object? v) => Convert.ToInt64(v);

    // ---- summaries -------------------------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task Count_by_status_matches_hand_written_sql()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            GroupBy: new[] { new Grouping(C("status")) },
            Aggregations: new[] { new Aggregation(AggFn.Count) },
            OrderBy: new[] { new SortSpec("status") }));

        var expected = await ExpectedAsync("SELECT status, count(*) FROM orders GROUP BY status ORDER BY status");

        Assert.Equal(new[] { "status", "count" }, result.Columns.Select(c => c.Name));
        Assert.Equal(expected.Select(r => (string)r[0]!), result.Rows.Select(r => (string)r[0]!));
        Assert.Equal(expected.Select(r => L(r[1])), result.Rows.Select(r => L(r[1])));
        Assert.Equal(2000, result.Rows.Sum(r => L(r[1])));
        Assert.False(result.LimitReached);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Sum_avg_min_max_in_one_row_match_hand_written_sql()
    {
        var result = await RunAsync(new QueryDefinition(T("order_items"), Aggregations: new[]
        {
            new Aggregation(AggFn.Sum, C("quantity")), new Aggregation(AggFn.Avg, C("unit_price")),
            new Aggregation(AggFn.Min, C("unit_price")), new Aggregation(AggFn.Max, C("unit_price")),
            new Aggregation(AggFn.CountDistinct, C("product_id")), new Aggregation(AggFn.Count)
        }));

        var e = (await ExpectedAsync(
            "SELECT sum(quantity), avg(unit_price), min(unit_price), max(unit_price), count(DISTINCT product_id), count(*) FROM order_items"))[0];

        var row = Assert.Single(result.Rows);
        Assert.Equal(L(e[0]), L(row[0]));
        Assert.Equal(Convert.ToDecimal(e[1]), Convert.ToDecimal(row[1]));
        Assert.Equal(Convert.ToDecimal(e[2]), Convert.ToDecimal(row[2]));
        Assert.Equal(Convert.ToDecimal(e[3]), Convert.ToDecimal(row[3]));
        Assert.Equal(L(e[4]), L(row[4]));
        Assert.Equal(L(e[5]), L(row[5]));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Orders_per_month_are_bucketed_in_utc_and_match_hand_written_sql()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            GroupBy: new[] { new Grouping(C("created_at"), DateBucket.Month) },
            Aggregations: new[] { new Aggregation(AggFn.Count) },
            OrderBy: new[] { new SortSpec("created_at") }));

        var expected = await ExpectedAsync(
            "SELECT date_trunc('month', created_at AT TIME ZONE 'UTC') AT TIME ZONE 'UTC', count(*) FROM orders GROUP BY 1 ORDER BY 1");

        Assert.Equal(expected.Count, result.Rows.Count);
        Assert.Equal(expected.Select(r => L(r[1])), result.Rows.Select(r => L(r[1])));
        Assert.Equal(2000, result.Rows.Sum(r => L(r[1])));
        // every bucket starts on the 1st at 00:00 UTC
        Assert.All(result.Rows, r => Assert.Matches(@"^\d{4}-\d{2}-01T00:00:00(\.0+)?Z$", (string)r[0]!));
        Assert.Equal(ColumnKind.Timestamp, result.Columns[0].Kind);
        // oldest first
        Assert.Equal(result.Rows.Select(r => (string)r[0]!).OrderBy(x => x), result.Rows.Select(r => (string)r[0]!));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_grouped_question_with_no_sort_comes_back_in_group_order()
    {
        // the default order is what makes a "per month" line chart run left to right
        var result = await RunAsync(new QueryDefinition(T("orders"),
            GroupBy: new[] { new Grouping(C("created_at"), DateBucket.Month) },
            Aggregations: new[] { new Aggregation(AggFn.Count) }));

        var months = result.Rows.Select(r => DateTimeOffset.Parse((string)r[0]!)).ToList();
        Assert.True(months.Count > 6);
        Assert.Equal(months.OrderBy(m => m), months);

        var byStatus = await RunAsync(new QueryDefinition(T("orders"),
            GroupBy: new[] { new Grouping(C("status")) }, Aggregations: new[] { new Aggregation(AggFn.Count) }));
        var names = byStatus.Rows.Select(r => (string)r[0]!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Sorting_by_a_summary_and_limiting_gives_the_top_n()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            GroupBy: new[] { new Grouping(C("user_id")) },
            Aggregations: new[] { new Aggregation(AggFn.Count, null, "orders") },
            OrderBy: new[] { new SortSpec("orders", SortDirection.Desc), new SortSpec("user_id") },
            Limit: 5));

        var expected = await ExpectedAsync(
            "SELECT user_id, count(*) AS orders FROM orders GROUP BY user_id ORDER BY orders DESC, user_id LIMIT 5");

        Assert.Equal(5, result.Rows.Count);
        Assert.True(result.LimitReached);
        Assert.Equal(expected.Select(r => (L(r[0]), L(r[1]))), result.Rows.Select(r => (L(r[0]), L(r[1]))));
    }

    // ---- joins -----------------------------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task Orders_per_country_through_a_foreign_key_join()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            Joins: new[] { new JoinDef(T("users"), T("orders"), "orders_user_id_fkey") },
            GroupBy: new[] { new Grouping(C("country", "users")) },
            Aggregations: new[] { new Aggregation(AggFn.Count) },
            OrderBy: new[] { new SortSpec("users.country") }));

        var expected = await ExpectedAsync(
            "SELECT u.country, count(*) FROM orders o JOIN users u ON u.id = o.user_id GROUP BY u.country ORDER BY u.country");

        Assert.Equal(new[] { "users.country", "count" }, result.Columns.Select(c => c.Name));
        Assert.Equal(expected.Select(r => ((string)r[0]!, L(r[1]))), result.Rows.Select(r => ((string)r[0]!, L(r[1]))));
        Assert.Equal(2000, result.Rows.Sum(r => L(r[1])));
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_join_works_from_the_other_side_too()
    {
        // users -> orders: one row per user, counting that user's orders (0 for users without any)
        var result = await RunAsync(new QueryDefinition(T("users"),
            Joins: new[] { new JoinDef(T("orders"), T("users"), "orders_user_id_fkey") },
            GroupBy: new[] { new Grouping(C("id")) },
            Aggregations: new[] { new Aggregation(AggFn.Count, C("id", "orders"), "orders") },
            OrderBy: new[] { new SortSpec("users.id") },
            Limit: 10_000));

        var expected = await ExpectedAsync(
            "SELECT u.id, count(o.id) FROM users u LEFT JOIN orders o ON o.user_id = u.id GROUP BY u.id ORDER BY u.id");

        Assert.Equal(200, result.Rows.Count);
        Assert.Equal(expected.Select(r => (L(r[0]), L(r[1]))), result.Rows.Select(r => (L(r[0]), L(r[1]))));
        Assert.Equal(2000, result.Rows.Sum(r => L(r[1])));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Units_sold_per_country_chains_three_tables()
    {
        var result = await RunAsync(new QueryDefinition(T("order_items"),
            Joins: new[]
            {
                new JoinDef(T("orders"), T("order_items"), "order_items_order_id_fkey"),
                new JoinDef(T("users"), T("orders"), "orders_user_id_fkey")
            },
            GroupBy: new[] { new Grouping(C("country", "users")) },
            Aggregations: new[] { new Aggregation(AggFn.Sum, C("quantity")) },
            OrderBy: new[] { new SortSpec("users.country") }));

        var expected = await ExpectedAsync(@"
            SELECT u.country, sum(i.quantity)
            FROM order_items i JOIN orders o ON o.id = i.order_id JOIN users u ON u.id = o.user_id
            GROUP BY u.country ORDER BY u.country");

        Assert.Equal(expected.Select(r => ((string)r[0]!, L(r[1]))), result.Rows.Select(r => ((string)r[0]!, L(r[1]))));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_filter_on_the_joined_table_applies()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            Joins: new[] { new JoinDef(T("users"), T("orders"), "orders_user_id_fkey") },
            Filters: new[] { new Filter(C("country", "users"), FilterOp.Eq, V("DE")) },
            Aggregations: new[] { new Aggregation(AggFn.Count) }));

        var expected = await ExpectedAsync("SELECT count(*) FROM orders o JOIN users u ON u.id = o.user_id WHERE u.country = 'DE'");
        Assert.Equal(L(expected[0][0]), L(result.Rows[0][0]));
        Assert.True(L(result.Rows[0][0]) > 0);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Plain_columns_from_two_tables_come_back_prefixed()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            Joins: new[] { new JoinDef(T("users"), T("orders"), "orders_user_id_fkey") },
            Columns: new[] { C("id"), C("status"), C("name", "users"), C("country", "users") },
            OrderBy: new[] { new SortSpec("orders.id") },
            Limit: 3));

        Assert.Equal(new[] { "orders.id", "orders.status", "users.name", "users.country" }, result.Columns.Select(c => c.Name));
        var expected = await ExpectedAsync("SELECT o.id, o.status, u.name, u.country FROM orders o JOIN users u ON u.id = o.user_id ORDER BY o.id LIMIT 3");
        Assert.Equal(expected.Select(r => string.Join("|", r)), result.Rows.Select(r => string.Join("|", r)));
    }

    // ---- filters ---------------------------------------------------------------------------

    private async Task<long> CountAsync(string table, params Filter[] filters)
    {
        var result = await RunAsync(new QueryDefinition(T(table), Filters: filters, Aggregations: new[] { new Aggregation(AggFn.Count) }));
        return L(result.Rows[0][0]);
    }

    private async Task<long> ExpectedCountAsync(string sql) => L((await ExpectedAsync(sql))[0][0]);

    [PostgresConnectorTests.PgFact]
    public async Task Each_kind_of_filter_counts_the_same_rows_as_hand_written_sql()
    {
        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE status = 'paid'"),
            await CountAsync("orders", new Filter(C("status"), FilterOp.Eq, V("paid"))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE status <> 'paid'"),
            await CountAsync("orders", new Filter(C("status"), FilterOp.NotEq, V("paid"))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE status IN ('paid','shipped')"),
            await CountAsync("orders", new Filter(C("status"), FilterOp.In, V("paid", "shipped"))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE user_id BETWEEN 10 AND 20"),
            await CountAsync("orders", new Filter(C("user_id"), FilterOp.Between, V(10, 20))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE user_id > 150"),
            await CountAsync("orders", new Filter(C("user_id"), FilterOp.Gt, V(150))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE user_id IN (1,2,3)"),
            await CountAsync("orders", new Filter(C("user_id"), FilterOp.In, V(1, 2, 3))));

        // numeric column compared with a decimal
        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM products WHERE price > 30.5"),
            await CountAsync("products", new Filter(C("price"), FilterOp.Gt, V(30.5))));

        Assert.Equal(40, await CountAsync("products", new Filter(C("active"), FilterOp.Eq, V(true))));
        Assert.Equal(0, await CountAsync("products", new Filter(C("active"), FilterOp.Eq, V(false))));

        Assert.Equal(0, await CountAsync("legacy_events", new Filter(C("user_id"), FilterOp.IsNull)));
        Assert.Equal(1000, await CountAsync("legacy_events", new Filter(C("user_id"), FilterOp.NotNull)));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Several_filters_must_all_hold()
    {
        var n = await CountAsync("orders",
            new Filter(C("status"), FilterOp.Eq, V("paid")),
            new Filter(C("user_id"), FilterOp.Lte, V(100)));
        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM orders WHERE status = 'paid' AND user_id <= 100"), n);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Text_matching_ignores_case_and_treats_percent_and_underscore_literally()
    {
        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM users WHERE email ILIKE '%USER1%'"),
            await CountAsync("users", new Filter(C("email"), FilterOp.Contains, V("USER1"))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM users WHERE name ILIKE 'user 1%'"),
            await CountAsync("users", new Filter(C("name"), FilterOp.StartsWith, V("user 1"))));

        Assert.Equal(await ExpectedCountAsync("SELECT count(*) FROM users WHERE email ILIKE '%.com'"),
            await CountAsync("users", new Filter(C("email"), FilterOp.EndsWith, V(".COM"))));

        // typed literally, % and _ match nothing here; if they were wildcards they would match every row
        Assert.Equal(0, await CountAsync("users", new Filter(C("name"), FilterOp.Contains, V("%"))));
        Assert.Equal(0, await CountAsync("users", new Filter(C("name"), FilterOp.Contains, V("_"))));
        Assert.Equal(0, await CountAsync("users", new Filter(C("name"), FilterOp.StartsWith, V("User_1"))));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_date_filter_covers_the_whole_day()
    {
        // pick a real day that has orders, and count how many fall on it, by hand
        var day = (string)(await ExpectedAsync(
            "SELECT to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD') FROM orders ORDER BY id LIMIT 1"))[0][0]!;

        var onThatDay = await ExpectedCountAsync($"SELECT count(*) FROM orders WHERE (created_at AT TIME ZONE 'UTC')::date = '{day}'");
        Assert.True(onThatDay >= 1);

        Assert.Equal(onThatDay, await CountAsync("orders", new Filter(C("created_at"), FilterOp.Eq, V(day))));
        Assert.Equal(onThatDay, await CountAsync("orders", new Filter(C("created_at"), FilterOp.Between, V(day, day))));

        Assert.Equal(
            await ExpectedCountAsync($"SELECT count(*) FROM orders WHERE (created_at AT TIME ZONE 'UTC')::date <= '{day}'"),
            await CountAsync("orders", new Filter(C("created_at"), FilterOp.Lte, V(day))));
        Assert.Equal(
            await ExpectedCountAsync($"SELECT count(*) FROM orders WHERE (created_at AT TIME ZONE 'UTC')::date > '{day}'"),
            await CountAsync("orders", new Filter(C("created_at"), FilterOp.Gt, V(day))));
        Assert.Equal(
            await ExpectedCountAsync($"SELECT count(*) FROM orders WHERE (created_at AT TIME ZONE 'UTC')::date <> '{day}'"),
            await CountAsync("orders", new Filter(C("created_at"), FilterOp.NotEq, V(day))));
    }

    [PostgresConnectorTests.PgFact]
    public async Task An_exact_time_filter_uses_the_time_given()
    {
        Assert.Equal(
            await ExpectedCountAsync("SELECT count(*) FROM orders WHERE created_at >= '2026-06-01T00:00:00Z'"),
            await CountAsync("orders", new Filter(C("created_at"), FilterOp.Gte, V("2026-06-01T00:00:00Z"))));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Hostile_filter_values_are_just_data()
    {
        var n = await CountAsync("users", new Filter(C("name"), FilterOp.Eq, V("x'; DROP TABLE users; --")));
        Assert.Equal(0, n);
        Assert.Equal(200, await CountAsync("users"));   // still there, and still readable
    }

    // ---- views, safety, shape --------------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task A_view_can_be_summarised_like_a_table()
    {
        var result = await RunAsync(new QueryDefinition(T("order_totals"),
            GroupBy: new[] { new Grouping(C("status")) },
            Aggregations: new[] { new Aggregation(AggFn.Sum, C("total")) },
            OrderBy: new[] { new SortSpec("status") }));

        var expected = await ExpectedAsync("SELECT status, sum(total) FROM order_totals GROUP BY status ORDER BY status");
        Assert.Equal(expected.Select(r => ((string)r[0]!, Convert.ToDecimal(r[1]))), result.Rows.Select(r => ((string)r[0]!, Convert.ToDecimal(r[1]))));
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_result_reports_the_sql_and_its_parameters()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"),
            Filters: new[] { new Filter(C("status"), FilterOp.Eq, V("paid")) },
            Aggregations: new[] { new Aggregation(AggFn.Count) }, Limit: 7));

        Assert.Contains("WHERE \"t0\".\"status\" = @p1", result.Sql);
        Assert.Equal("paid", result.Parameters["p1"]);
        Assert.Equal(7, result.Parameters["p_limit"]);
        Assert.True(result.DurationMs >= 0);
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_limit_is_enforced_and_reported()
    {
        var result = await RunAsync(new QueryDefinition(T("orders"), Limit: 25));
        Assert.Equal(25, result.Rows.Count);
        Assert.True(result.LimitReached);

        var all = await RunAsync(new QueryDefinition(T("categories"), Limit: 25));
        Assert.Equal(5, all.Rows.Count);
        Assert.False(all.LimitReached);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Sessions_run_in_utc_whatever_the_server_uses()
    {
        var result = await _connector.ExecuteAsync(
            RO, new CompiledQuery("SELECT current_setting('TimeZone')", new Dictionary<string, object?>()), ExecOptions.Default);
        Assert.Equal("UTC", result.Rows[0][0]);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_question_about_a_table_that_vanished_is_reported_for_resync()
    {
        // snapshot says the table exists, the database disagrees
        var store = new SqliteAppStore(Path.Combine(_dir, "stale.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        var connection = await store.SaveConnectionAsync("shop", RO, false);
        var real = await _connector.DescribeAsync(RO);
        var ghost = real.Tables.First(t => t.Name == "categories") with { Name = "ghost_table" };
        await store.SaveSnapshotAsync(connection.Id, new DbMetadata(real.Tables.Append(ghost).ToList()));

        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            new QuestionService(store, _connector).RunAsync("shop", new QueryDefinition(T("ghost_table"))));
        Assert.Equal(QueryFailure.ObjectNotFound, ex.Reason);
        Assert.Contains("Re-sync", ex.Message);
    }
}
