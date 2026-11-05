using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Tests;

public class FieldValuesCompilerTests
{
    private static TableMetadata Orders => Shop.Meta.FindTable("public", "orders")!;

    [Fact]
    public void Lists_the_distinct_values_of_a_column_in_order()
    {
        var q = FieldValuesCompiler.Compile(Orders, "status", null, 51);
        Assert.Equal(
            "SELECT DISTINCT \"status\" FROM \"public\".\"orders\"\n" +
            "WHERE \"status\" IS NOT NULL\n" +
            "ORDER BY 1\n" +
            "LIMIT @p_limit", q.Sql);
        Assert.Equal(51, q.Parameters["p_limit"]);
        Assert.DoesNotContain("p_search", q.Parameters.Keys);
    }

    [Fact]
    public void A_search_is_a_parameter_and_matches_anywhere_ignoring_case()
    {
        var q = FieldValuesCompiler.Compile(Orders, "status", "pai", 51);
        Assert.Contains("AND \"status\"::text ILIKE @p_search", q.Sql);
        Assert.Equal("%pai%", q.Parameters["p_search"]);
        Assert.DoesNotContain("pai", q.Sql);
    }

    [Fact]
    public void Percent_underscore_and_backslash_typed_by_the_user_mean_themselves()
    {
        Assert.Equal("%50\\%\\_off\\\\%", FieldValuesCompiler.Compile(Orders, "status", "50%_off\\", 5).Parameters["p_search"]);
    }

    [Fact]
    public void A_number_column_can_be_searched_as_text()
    {
        var q = FieldValuesCompiler.Compile(Orders, "user_id", "19", 51);
        Assert.Contains("\"user_id\"::text ILIKE @p_search", q.Sql);
    }

    [Fact]
    public void Hostile_text_stays_in_the_parameter_and_hostile_names_are_refused()
    {
        var q = FieldValuesCompiler.Compile(Orders, "status", "'; DROP TABLE orders; --", 5);
        Assert.DoesNotContain("DROP", q.Sql);

        var ex = Assert.Throws<DataLensException>(() => FieldValuesCompiler.Compile(Orders, "status\"; DROP TABLE x; --", null, 5));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
    }

    [Theory]
    [InlineData("created_at")]   // a date: use the date control
    [InlineData("shipped_on")]
    [InlineData("note")]         // jsonb
    public void Only_text_and_number_columns_get_suggestions(string column)
    {
        var ex = Assert.Throws<DataLensException>(() => FieldValuesCompiler.Compile(Orders, column, null, 5));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Contains("only offered for text and number columns", ex.Message);
    }

    [Fact]
    public void Unknown_columns_and_bad_limits_are_refused()
    {
        Assert.Throws<DataLensException>(() => FieldValuesCompiler.Compile(Orders, "nope", null, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValuesCompiler.Compile(Orders, "status", null, 0));
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class FieldValuesServiceTests : ServiceTestBase
{
    private readonly ManualClock _clock = new();

    public FieldValuesServiceTests()
    {
        _service = new FieldValuesService(Store, Connector, _clock);
        // a snapshot with a text column, a number column and a date column
        Connector.Metadata = new DbMetadata(new[]
        {
            new TableMetadata("public", "orders", TableKind.Table,
                new[]
                {
                    new ColumnMetadata("id", "integer", ColumnKind.Number, false, true),
                    new ColumnMetadata("status", "text", ColumnKind.Text, false, false),
                    new ColumnMetadata("country", "text", ColumnKind.Text, false, false),
                    new ColumnMetadata("created_at", "timestamp with time zone", ColumnKind.Timestamp, false, false)
                },
                new[] { "id" }, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>())
        });
    }

    // one instance for the whole test, as in the app: the remembered lists live inside it
    private readonly FieldValuesService _service;

    private Task ConnectAsync() => new ConnectionService(Store, Connector).ConnectAsync("shop", Cs);

    private static QueryResult Values(params object[] values) =>
        new(new[] { "v" }, values.Select(v => new object?[] { v }).ToList(), false, 1);

    private Task<FieldValues> Get(string? search, string column = "status", int limit = 50) =>
        _service.GetAsync("shop", "public", "orders", column, search, limit);

    // ---- no search: the database lists them -------------------------------------------------

    [Fact]
    public async Task With_nothing_typed_the_first_values_come_from_the_database()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("paid", "pending", "refunded");

        var r = await Get(null);

        Assert.Equal(new object?[] { "paid", "pending", "refunded" }, r.Values);
        Assert.False(r.HasMore);
        Assert.Empty(r.Close);
        Assert.DoesNotContain("p_search", Connector.LastQuery!.Parameters.Keys);
    }

    [Fact]
    public async Task It_asks_for_one_more_than_the_limit_to_know_if_there_are_more()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values(Enumerable.Range(1, 4).Select(i => (object)$"v{i}").ToArray());

        var r = await Get(null, limit: 3);

        Assert.Equal(3, r.Values.Count);
        Assert.True(r.HasMore);
        Assert.Equal(4, Connector.LastQuery!.Parameters["p_limit"]);
        Assert.Equal(4, Connector.LastOptions!.MaxRows);
        Assert.Equal(FieldValuesService.Timeout, Connector.LastOptions.Timeout);
    }

    // ---- a small text column: ranked here, typos forgiven -----------------------------------

    [Fact]
    public async Task Typing_in_a_small_text_column_ranks_the_whole_list_and_offers_did_you_mean()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("paid", "pending", "refunded", "shipped");

        var r = await Get("shiped");

        Assert.Empty(r.Values);
        Assert.Equal(new object?[] { "shipped" }, r.Close);
        // it read the list once, with no search text, and ranked it in memory
        var query = Assert.Single(Connector.Queries);
        Assert.DoesNotContain("p_search", query.Parameters.Keys);
        Assert.Equal(FieldValuesService.PoolSize + 1, query.Parameters["p_limit"]);
    }

    [Fact]
    public async Task Exact_prefix_and_substring_matches_are_ordered_best_first()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("a paid", "paid", "paidup", "unpaid", "xpaid");

        var r = await Get("paid");

        Assert.Equal(new object?[] { "paid", "paidup", "a paid", "xpaid", "unpaid" }, r.Values);
    }

    [Fact]
    public async Task The_list_is_remembered_so_each_keystroke_does_not_hit_the_database()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("paid", "pending", "refunded", "shipped");

        await Get("p"); await Get("pe"); await Get("pen"); await Get("pend");

        Assert.Single(Connector.Queries);
    }

    [Fact]
    public async Task The_remembered_list_expires_after_a_few_minutes()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("paid", "pending");

        await Get("p");
        _clock.Advance(FieldValuesService.PoolLifetime - TimeSpan.FromSeconds(1));
        await Get("pe");
        Assert.Single(Connector.Queries);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await Get("pen");
        Assert.Equal(2, Connector.Queries.Count);
    }

    [Fact]
    public async Task Did_you_mean_is_limited_to_a_handful()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values(Enumerable.Range(0, 30).Select(i => (object)$"item{i:00}x").ToArray());

        var r = await Get("itemx");   // not a substring of any, one edit from many

        Assert.True(r.Close.Count <= FieldValuesService.MaxClose);
    }

    [Fact]
    public async Task Matches_are_limited_and_say_when_there_are_more()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values(Enumerable.Range(0, 80).Select(i => (object)$"user{i:00}").ToArray());

        var r = await Get("user", limit: 10);

        Assert.Equal(10, r.Values.Count);
        Assert.True(r.HasMore);
    }

    // ---- a big text column: the database searches, no typo help -------------------------------

    [Fact]
    public async Task A_text_column_with_too_many_values_falls_back_to_a_plain_search()
    {
        await ConnectAsync();
        Connector.OnExecute = q => q.Parameters.ContainsKey("p_search")
            ? Values("match1", "match2")
            : Values(Enumerable.Range(0, FieldValuesService.PoolSize + 1).Select(i => (object)$"v{i}").ToArray());

        var r = await Get("matc");

        Assert.Equal(new object?[] { "match1", "match2" }, r.Values);
        Assert.Empty(r.Close);                       // ranking part of a list would mislead
        Assert.Equal(2, Connector.Queries.Count);    // the list check, then the search
        Assert.Equal("%matc%", Connector.LastQuery!.Parameters["p_search"]);
    }

    [Fact]
    public async Task A_number_column_is_always_searched_by_the_database()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values(19, 119, 190);

        var r = await Get("19", column: "id");

        Assert.Equal(new object?[] { 19, 119, 190 }, r.Values);
        Assert.Empty(r.Close);
        Assert.Single(Connector.Queries);
        Assert.Equal("%19%", Connector.LastQuery!.Parameters["p_search"]);
    }

    [Fact]
    public async Task Each_column_keeps_its_own_remembered_list()
    {
        await ConnectAsync();
        Connector.OnExecute = q => q.Sql.Contains("\"country\"") ? Values("Germany", "France") : Values("paid", "pending");

        var status = await Get("pe");
        var country = await Get("fra", column: "country");

        Assert.Equal(new object?[] { "pending" }, status.Values);
        Assert.Equal(new object?[] { "France" }, country.Values);
        Assert.Equal(2, Connector.Queries.Count);       // one list read per column

        await Get("pai"); await Get("ger", column: "country");
        Assert.Equal(2, Connector.Queries.Count);       // and both are reused
    }

    // ---- refusals -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(FieldValuesService.MaxLimit + 1)]
    public async Task A_bad_limit_is_refused_before_anything_runs(int limit)
    {
        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Get(null, limit: limit));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Empty(Connector.Queries);
    }

    [Fact]
    public async Task Very_long_search_text_is_refused()
    {
        await ConnectAsync();
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Get(new string('x', FieldValuesService.MaxSearchLength + 1)));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
    }

    [Fact]
    public async Task Whitespace_only_counts_as_no_search()
    {
        await ConnectAsync();
        Connector.OnExecute = _ => Values("paid");

        await Get("   ");

        Assert.DoesNotContain("p_search", Connector.LastQuery!.Parameters.Keys);
    }

    [Fact]
    public async Task Unknown_tables_columns_and_unsupported_columns_never_reach_the_database()
    {
        await ConnectAsync();

        var table = await Assert.ThrowsAsync<DataLensException>(() => _service.GetAsync("shop", "public", "secrets", "x", null));
        Assert.Equal(DataLensError.NotFound, table.Error);

        var column = await Assert.ThrowsAsync<DataLensException>(() => Get(null, column: "nope"));
        Assert.Equal(DataLensError.BadRequest, column.Error);

        var date = await Assert.ThrowsAsync<DataLensException>(() => Get(null, column: "created_at"));
        Assert.Equal(DataLensError.BadRequest, date.Error);

        Assert.Empty(Connector.Queries);
    }

    [Fact]
    public async Task An_unknown_connection_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<DataLensException>(() => Get(null));
        Assert.Equal(DataLensError.NotFound, ex.Error);
    }
}
