using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;
using Npgsql;

namespace DataLens.Tests;

/// <summary>
/// The safety guarantees, proved against a real database (see PostgresConnectorTests for how to run them).
/// Note most of these use the ADMIN connection on purpose: DataLens must stay read-only even when the
/// database user could write.
/// </summary>
public sealed class PostgresExecutionTests : IDisposable
{
    private static string RO => PostgresConnectorTests.ReadOnly!;
    private static string RW => PostgresConnectorTests.ReadWrite!;

    private readonly PostgresConnector _connector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CompiledQuery Sql(string sql, Dictionary<string, object?>? p = null) =>
        new(sql, p ?? new Dictionary<string, object?>());

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionStringParser.Parse(connectionString).ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ---- the read-only guarantee ------------------------------------------------------------

    [PostgresConnectorTests.PgTheory]
    [InlineData("UPDATE users SET name = 'hacked'")]
    [InlineData("DELETE FROM orders")]
    [InlineData("INSERT INTO categories (name) VALUES ('x')")]
    [InlineData("DROP TABLE legacy_events")]
    [InlineData("TRUNCATE order_items CASCADE")]
    [InlineData("SELECT 1; DELETE FROM users")]   // a second statement hidden after a harmless one
    public async Task Writes_are_blocked_even_for_a_user_who_is_allowed_to_write(string sql)
    {
        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            _connector.ExecuteAsync(RW, Sql(sql), ExecOptions.Default));

        Assert.Equal(QueryFailure.WriteBlocked, ex.Reason);

        // and nothing actually changed
        Assert.Equal(200, await ScalarAsync(RW, "SELECT count(*) FROM users"));
        Assert.Equal(0, await ScalarAsync(RW, "SELECT count(*) FROM users WHERE name = 'hacked'"));
        Assert.Equal(2000, await ScalarAsync(RW, "SELECT count(*) FROM orders"));
        Assert.Equal(5, await ScalarAsync(RW, "SELECT count(*) FROM categories"));
        Assert.Equal(1000, await ScalarAsync(RW, "SELECT count(*) FROM legacy_events"));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Functions_that_write_are_blocked_too()
    {
        // nextval changes a sequence, which a read-only transaction forbids
        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            _connector.ExecuteAsync(RW, Sql("SELECT nextval('users_id_seq')"), ExecOptions.Default));
        Assert.Equal(QueryFailure.WriteBlocked, ex.Reason);
    }

    // ---- timeout and row cap ----------------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task A_slow_query_is_stopped_by_the_timeout()
    {
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            _connector.ExecuteAsync(RO, Sql("SELECT pg_sleep(10)"), new ExecOptions(TimeSpan.FromSeconds(1), 100)));

        Assert.Equal(QueryFailure.Timeout, ex.Reason);
        Assert.Contains("longer than 1 seconds", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_huge_result_is_cut_at_the_row_cap_without_reading_it_all()
    {
        // A three-way cross join would return 8 billion rows. It streams, so the server can be stopped early.
        // (generate_series in FROM would not do: Postgres builds all of its rows before sending the first.)
        var watch = Stopwatch.StartNew();
        var result = await _connector.ExecuteAsync(
            RO, Sql("SELECT a.id FROM orders a, orders b, orders c"), new ExecOptions(TimeSpan.FromSeconds(15), 10));

        Assert.Equal(10, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");

        // the connection went back to the pool in good shape
        var after = await _connector.ExecuteAsync(RO, Sql("SELECT 1"), ExecOptions.Default);
        Assert.Equal(1, after.Rows[0][0]);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_result_exactly_at_the_cap_is_not_marked_truncated()
    {
        var result = await _connector.ExecuteAsync(
            RO, Sql("SELECT g FROM generate_series(1, 10) g"), new ExecOptions(TimeSpan.FromSeconds(15), 10));
        Assert.Equal(10, result.Rows.Count);
        Assert.False(result.Truncated);
    }

    // ---- parameters, types, errors ----------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task Parameters_are_bound_not_pasted_into_the_sql()
    {
        var result = await _connector.ExecuteAsync(
            RO, Sql("SELECT @n::int + 1 AS answer, @s::text AS echo",
                new() { ["n"] = 41, ["s"] = "x'; DROP TABLE users; --" }),
            ExecOptions.Default);

        Assert.Equal(new[] { "answer", "echo" }, result.Columns);
        Assert.Equal(42, result.Rows[0][0]);
        Assert.Equal("x'; DROP TABLE users; --", result.Rows[0][1]);
        Assert.Equal(200, await ScalarAsync(RW, "SELECT count(*) FROM users"));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Real_postgres_types_come_back_as_json_safe_values()
    {
        var result = await _connector.ExecuteAsync(RO, Sql("""
            SELECT 1::bigint AS small, 9007199254740993::bigint AS big, 12.50::numeric AS price,
                   true AS flag, NULL::text AS nothing, ARRAY[1,2,3] AS list,
                   now() AS ts, current_date AS day, gen_random_uuid() AS id,
                   '{"a":1}'::jsonb AS doc, 'NaN'::float8 AS nan, '\xdeadbeef'::bytea AS raw
            """), ExecOptions.Default);

        var row = result.Rows[0];
        Assert.Equal(1L, row[0]);
        Assert.Equal("9007199254740993", row[1]);
        Assert.Equal(12.50m, row[2]);
        Assert.Equal(true, row[3]);
        Assert.Null(row[4]);
        Assert.Equal(new object?[] { 1, 2, 3 }, Assert.IsType<List<object?>>(row[5]));
        Assert.IsType<string>(row[6]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", (string)row[7]!);
        Assert.Equal("NaN", row[10]);
        Assert.Equal(@"\xdeadbeef", row[11]);

        JsonSerializer.Serialize(result.Rows);   // must not throw
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_missing_table_is_reported_as_a_stale_snapshot()
    {
        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            _connector.ExecuteAsync(RO, Sql("SELECT * FROM no_such_table"), ExecOptions.Default));
        Assert.Equal(QueryFailure.ObjectNotFound, ex.Reason);
        Assert.Contains("Re-sync", ex.Message);
    }

    // ---- the explorer, end to end -----------------------------------------------------------

    private async Task<DataExplorerService> ConnectedExplorerAsync()
    {
        var store = new SqliteAppStore(
            Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        await new ConnectionService(store, _connector).ConnectAsync("shop", RO);
        return new DataExplorerService(store, _connector);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Browses_a_real_table_page_by_page()
    {
        var explorer = await ConnectedExplorerAsync();

        var first = await explorer.PreviewAsync(new PreviewRequest("shop", "public", "orders", Page: 1, PageSize: 5));
        Assert.Equal(5, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.Equal(new[] { "id", "user_id", "status", "created_at" }, first.Columns.Select(c => c.Name));
        Assert.Equal(new object?[] { 1, 2, 3, 4, 5 }, first.Rows.Select(r => r[0]).ToArray());   // by primary key

        var last = await explorer.PreviewAsync(new PreviewRequest("shop", "public", "orders", Page: 400, PageSize: 5));
        Assert.Equal(5, last.Rows.Count);
        Assert.False(last.HasMore);
        Assert.Equal(2000, last.Rows[^1][0]);

        var past = await explorer.PreviewAsync(new PreviewRequest("shop", "public", "orders", Page: 401, PageSize: 5));
        Assert.Empty(past.Rows);
        Assert.False(past.HasMore);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Pages_never_overlap_or_skip_rows()
    {
        var explorer = await ConnectedExplorerAsync();
        var seen = new List<int>();
        for (var page = 1; page <= 4; page++)
        {
            // sort by a column with many duplicate values: the primary key tie-breaker keeps pages stable
            var p = await explorer.PreviewAsync(new PreviewRequest("shop", "public", "orders", page, 50, "status"));
            seen.AddRange(p.Rows.Select(r => (int)r[0]!));
        }
        Assert.Equal(200, seen.Count);
        Assert.Equal(200, seen.Distinct().Count());
    }

    [PostgresConnectorTests.PgFact]
    public async Task Sorting_descending_orders_the_rows()
    {
        var explorer = await ConnectedExplorerAsync();
        var page = await explorer.PreviewAsync(
            new PreviewRequest("shop", "public", "orders", 1, 20, "created_at", SortDirection.Desc));

        var times = page.Rows.Select(r => DateTimeOffset.Parse((string)r[3]!)).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Views_and_tables_without_foreign_keys_browse_fine()
    {
        var explorer = await ConnectedExplorerAsync();
        Assert.Equal(10, (await explorer.PreviewAsync(new PreviewRequest("shop", "public", "order_totals", PageSize: 10))).Rows.Count);
        Assert.Equal(10, (await explorer.PreviewAsync(new PreviewRequest("shop", "public", "legacy_events", PageSize: 10))).Rows.Count);
    }
}
