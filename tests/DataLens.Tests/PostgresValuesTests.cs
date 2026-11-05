using System.Security.Cryptography;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

/// <summary>Filter suggestions against the real sample database, checked against hand-written SQL.</summary>
public sealed class PostgresValuesTests : IDisposable
{
    private static string RO => PostgresConnectorTests.ReadOnly!;

    private readonly PostgresConnector _connector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));
    private FieldValuesService? _service;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<FieldValuesService> ServiceAsync()
    {
        if (_service is not null) return _service;
        var store = new SqliteAppStore(Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
        await new ConnectionService(store, _connector).ConnectAsync("shop", RO);
        return _service = new FieldValuesService(store, _connector);
    }

    private async Task<FieldValues> Get(string table, string column, string? search = null, int limit = 50) =>
        await (await ServiceAsync()).GetAsync("shop", "public", table, column, search, limit);

    private async Task<List<object?[]>> ExpectedAsync(string sql) =>
        (await _connector.ExecuteAsync(RO, new CompiledQuery(sql, new Dictionary<string, object?>()), new ExecOptions(TimeSpan.FromSeconds(15), 10_000))).Rows.ToList();

    private static string[] Strings(IEnumerable<object?> values) => values.Select(v => (string)v!).ToArray();

    [PostgresConnectorTests.PgFact]
    public async Task The_values_of_a_column_are_what_is_really_in_it()
    {
        var status = await Get("orders", "status");
        var expected = (await ExpectedAsync("SELECT DISTINCT status FROM orders ORDER BY 1")).Select(r => (string)r[0]!);

        Assert.Equal(expected, Strings(status.Values));
        Assert.Equal(new[] { "paid", "pending", "refunded", "shipped" }, Strings(status.Values));
        Assert.False(status.HasMore);
        Assert.Empty(status.Close);

        Assert.Equal(new[] { "BR", "DE", "FR", "GB", "IN", "US" }, Strings((await Get("users", "country")).Values));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Typing_narrows_the_list_and_matches_anywhere_in_the_value()
    {
        Assert.Equal(new[] { "paid" }, Strings((await Get("orders", "status", "pa")).Values));
        Assert.Equal(new[] { "pending" }, Strings((await Get("orders", "status", "ing")).Values));   // inside the word
        Assert.Equal(new[] { "shipped" }, Strings((await Get("orders", "status", "ipp")).Values));
        Assert.Equal(new[] { "pending" }, Strings((await Get("orders", "status", "END")).Values));   // any case
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_misspelling_still_finds_the_value_under_did_you_mean()
    {
        foreach (var (typed, expected) in new[] { ("shiped", "shipped"), ("pendng", "pending"), ("refundd", "refunded"), ("paaid", "paid") })
        {
            var r = await Get("orders", "status", typed);
            Assert.Empty(r.Values);
            Assert.Equal(new[] { expected }, Strings(r.Close));
        }

        Assert.Equal(new[] { "logout" }, Strings((await Get("legacy_events", "event", "logot")).Close));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Real_matches_come_before_close_ones_and_the_best_match_comes_first()
    {
        var r = await Get("users", "name", "user 19");

        var expected = (await ExpectedAsync("SELECT DISTINCT name FROM users WHERE name ILIKE '%user 19%'")).Select(x => (string)x[0]!).ToHashSet();
        Assert.Equal(expected, Strings(r.Values).ToHashSet());
        Assert.Equal("User 19", r.Values[0]);                                  // exact first
        Assert.Equal(new[] { "User 190", "User 191" }, Strings(r.Values).Skip(1).Take(2));   // then the rest, shortest first
        Assert.DoesNotContain(r.Close, c => expected.Contains((string)c!));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_number_column_is_searched_by_its_digits_and_comes_back_as_numbers()
    {
        var r = await Get("orders", "user_id", "19", limit: 100);
        var expected = (await ExpectedAsync("SELECT DISTINCT user_id FROM orders WHERE user_id::text LIKE '%19%' ORDER BY 1")).Select(x => Convert.ToInt64(x[0])).ToList();

        Assert.Equal(expected, r.Values.Select(Convert.ToInt64));
        Assert.All(r.Values, v => Assert.True(v is int or long));
        Assert.Empty(r.Close);

        var unfiltered = await Get("orders", "user_id", limit: 5);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, unfiltered.Values.Select(Convert.ToInt64));
        Assert.True(unfiltered.HasMore);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Percent_signs_and_quotes_in_the_search_are_just_text()
    {
        Assert.Empty((await Get("users", "name", "%")).Values);          // text column: matched in memory
        Assert.Empty((await Get("orders", "user_id", "%")).Values);      // number column: matched by the database
        Assert.Empty((await Get("users", "name", "_")).Values);

        Assert.Empty((await Get("users", "name", "'; DROP TABLE users; --")).Values);
        Assert.Empty((await Get("orders", "user_id", "'; DROP TABLE users; --")).Values);
        Assert.Equal(200, Convert.ToInt64((await ExpectedAsync("SELECT count(*) FROM users"))[0][0]));
    }

    [PostgresConnectorTests.PgFact]
    public async Task The_limit_and_has_more_work_on_a_real_column()
    {
        var r = await Get("users", "email", limit: 5);
        Assert.Equal(5, r.Values.Count);
        Assert.True(r.HasMore);

        var all = await Get("users", "email", limit: 200);
        Assert.Equal(200, all.Values.Count);
        Assert.False(all.HasMore);

        var searched = await Get("users", "email", "user1", limit: 10);   // small list, so ranked: 111 emails contain it
        Assert.Equal(10, searched.Values.Count);
        Assert.True(searched.HasMore);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_view_works_and_a_date_column_is_refused()
    {
        Assert.Equal(new[] { "paid", "pending", "refunded", "shipped" }, Strings((await Get("order_totals", "status")).Values));

        var ex = await Assert.ThrowsAsync<DataLensException>(() => Get("orders", "created_at"));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_second_search_is_served_from_memory_not_the_database()
    {
        await Get("orders", "status", "p");       // reads the list once
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) await Get("orders", "status", i % 2 == 0 ? "pen" : "shi");
        watch.Stop();

        // 50 database round trips would take far longer than this on any machine
        Assert.True(watch.ElapsedMilliseconds < 300, $"50 searches took {watch.ElapsedMilliseconds} ms");
    }
}
