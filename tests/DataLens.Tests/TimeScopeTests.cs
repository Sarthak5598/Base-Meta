using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Tests;

public class TimeScopeTests
{
    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static QueryDefinition PerMonth(string table = "orders", string column = "created_at", DateBucket bucket = DateBucket.Month) =>
        new(Shop.Ref(table),
            GroupBy: new[] { new Grouping(Shop.Col(column), bucket) },
            Aggregations: new[] { new Aggregation(AggFn.Count) });

    private static (QueryDefinition Query, TimeInfo Info) Apply(QueryDefinition q, TimeFilter? f) =>
        TimeScope.Apply(q, Shop.Meta, f);

    // ---- when nothing changes --------------------------------------------------------------

    [Fact]
    public void No_filter_or_an_empty_one_changes_nothing()
    {
        var q = PerMonth();
        Assert.Same(q, Apply(q, null).Query);
        Assert.Same(q, Apply(q, new TimeFilter()).Query);
        Assert.Equal(new TimeInfo(null, false, false), Apply(q, null).Info);
    }

    [Fact]
    public void A_table_without_a_date_column_is_left_alone()
    {
        var q = new QueryDefinition(Shop.Ref("order_items"), Aggregations: new[] { new Aggregation(AggFn.Count) });
        var (scoped, info) = Apply(q, new TimeFilter(D(2026, 1, 1), D(2026, 3, 31), DateBucket.Week));

        Assert.Same(q, scoped);
        Assert.Null(info.Column);
        Assert.False(info.RangeApplied);
        Assert.False(info.GranularityApplied);
    }

    [Fact]
    public void An_unknown_source_table_is_left_for_the_compiler_to_report()
    {
        var q = new QueryDefinition(Shop.Ref("nope"));
        Assert.Same(q, Apply(q, new TimeFilter(D(2026, 1, 1))).Query);
    }

    [Fact]
    public void The_original_question_is_never_modified()
    {
        var q = PerMonth() with { Filters = new[] { new Filter(Shop.Col("status"), FilterOp.Eq, new[] { J("paid") }) } };
        var (scoped, _) = Apply(q, new TimeFilter(D(2026, 1, 1), D(2026, 3, 31), DateBucket.Year));

        Assert.NotSame(q, scoped);
        Assert.Single(q.Filters!);
        Assert.Equal(DateBucket.Month, q.GroupBy![0].Bucket);
        Assert.Equal(2, scoped.Filters!.Count);
        Assert.Equal(DateBucket.Year, scoped.GroupBy![0].Bucket);
    }

    // ---- the date range --------------------------------------------------------------------

    [Fact]
    public void A_range_becomes_a_between_filter_on_the_date_column()
    {
        var (scoped, info) = Apply(PerMonth(), new TimeFilter(D(2026, 1, 1), D(2026, 3, 31)));

        var f = Assert.Single(scoped.Filters!);
        Assert.Equal("created_at", f.Column.Name);
        Assert.Equal(FilterOp.Between, f.Op);
        Assert.Equal(new[] { "2026-01-01", "2026-03-31" }, f.Values!.Select(v => v.GetString()));
        Assert.Equal(new TimeInfo("orders.created_at", true, false), info);
    }

    [Fact]
    public void One_open_end_becomes_at_least_or_at_most()
    {
        var from = Assert.Single(Apply(PerMonth(), new TimeFilter(From: D(2026, 1, 1))).Query.Filters!);
        Assert.Equal(FilterOp.Gte, from.Op);
        Assert.Equal("2026-01-01", from.Values![0].GetString());

        var to = Assert.Single(Apply(PerMonth(), new TimeFilter(To: D(2026, 3, 31))).Query.Filters!);
        Assert.Equal(FilterOp.Lte, to.Op);
        Assert.Equal("2026-03-31", to.Values![0].GetString());
    }

    [Fact]
    public void The_last_day_of_the_range_is_included_whole()
    {
        // through the real compiler: "to 31 March" must run to the end of the 31st, not stop at midnight
        var (scoped, _) = Apply(PerMonth(), new TimeFilter(D(2026, 1, 1), D(2026, 3, 31)));
        var c = QueryCompiler.Compile(scoped, Shop.Meta);

        Assert.Contains("(\"t0\".\"created_at\" >= @p1 AND \"t0\".\"created_at\" < @p2)", c.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), c.Query.Parameters["p1"]);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), c.Query.Parameters["p2"]);
    }

    [Fact]
    public void The_range_is_added_to_the_filters_the_question_already_has()
    {
        var q = PerMonth() with { Filters = new[] { new Filter(Shop.Col("status"), FilterOp.Eq, new[] { J("paid") }) } };
        var (scoped, _) = Apply(q, new TimeFilter(D(2026, 1, 1), D(2026, 3, 31)));

        Assert.Equal(new[] { "status", "created_at" }, scoped.Filters!.Select(f => f.Column.Name));
        Assert.Contains("WHERE \"t0\".\"status\" = @p1 AND (\"t0\".\"created_at\"", QueryCompiler.Compile(scoped, Shop.Meta).Query.Sql);
    }

    [Fact]
    public void A_start_after_the_end_is_refused()
    {
        var ex = Assert.Throws<DataLensException>(() => Apply(PerMonth(), new TimeFilter(D(2026, 3, 31), D(2026, 1, 1))));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        Assert.Contains("start date is after the end date", ex.Message);
    }

    [Fact]
    public void One_day_is_a_valid_range()
    {
        var (scoped, _) = Apply(PerMonth(), new TimeFilter(D(2026, 3, 5), D(2026, 3, 5)));
        Assert.Equal(FilterOp.Between, scoped.Filters![0].Op);
    }

    // ---- granularity -----------------------------------------------------------------------

    [Theory]
    [InlineData(DateBucket.Day)]
    [InlineData(DateBucket.Week)]
    [InlineData(DateBucket.Quarter)]
    [InlineData(DateBucket.Year)]
    public void Granularity_replaces_the_period_of_the_time_grouping(DateBucket chosen)
    {
        var (scoped, info) = Apply(PerMonth(), new TimeFilter(Granularity: chosen));
        Assert.Equal(chosen, scoped.GroupBy![0].Bucket);
        Assert.True(info.GranularityApplied);
        Assert.False(info.RangeApplied);
        Assert.Empty(scoped.Filters ?? []);
    }

    [Fact]
    public void Granularity_leaves_other_groupings_alone()
    {
        var q = new QueryDefinition(Shop.Ref("orders"),
            GroupBy: new[] { new Grouping(Shop.Col("created_at"), DateBucket.Month), new Grouping(Shop.Col("status")) },
            Aggregations: new[] { new Aggregation(AggFn.Count) });

        var (scoped, _) = Apply(q, new TimeFilter(Granularity: DateBucket.Week));

        Assert.Equal(DateBucket.Week, scoped.GroupBy![0].Bucket);
        Assert.Null(scoped.GroupBy[1].Bucket);
        Assert.Equal("status", scoped.GroupBy[1].Column.Name);
    }

    [Fact]
    public void Granularity_means_nothing_to_a_question_that_does_not_group_by_time_but_the_range_still_applies()
    {
        var byStatus = new QueryDefinition(Shop.Ref("orders"),
            GroupBy: new[] { new Grouping(Shop.Col("status")) }, Aggregations: new[] { new Aggregation(AggFn.Count) });

        var (scoped, info) = Apply(byStatus, new TimeFilter(D(2026, 1, 1), D(2026, 3, 31), DateBucket.Week));

        Assert.Null(scoped.GroupBy![0].Bucket);
        Assert.Equal(new TimeInfo("orders.created_at", true, false), info);   // range yes, granularity no
        Assert.Single(scoped.Filters!);
    }

    [Fact]
    public void Granularity_only_touches_the_column_the_range_is_applied_to()
    {
        // two bucketed dates: the first one is "the" time column, the other is left as asked
        var q = new QueryDefinition(Shop.Ref("orders"),
            GroupBy: new[] { new Grouping(Shop.Col("created_at"), DateBucket.Month), new Grouping(Shop.Col("shipped_on"), DateBucket.Year) },
            Aggregations: new[] { new Aggregation(AggFn.Count) });

        var (scoped, _) = Apply(q, new TimeFilter(Granularity: DateBucket.Day));

        Assert.Equal(DateBucket.Day, scoped.GroupBy![0].Bucket);
        Assert.Equal(DateBucket.Year, scoped.GroupBy[1].Bucket);
    }

    // ---- which column is "the time" --------------------------------------------------------

    [Fact]
    public void Without_a_time_grouping_the_tables_created_at_is_used()
    {
        var q = new QueryDefinition(Shop.Ref("users"), Aggregations: new[] { new Aggregation(AggFn.Count) });
        var (scoped, info) = Apply(q, new TimeFilter(From: D(2026, 1, 1)));
        Assert.Equal("users.created_at", info.Column);
        Assert.Equal("created_at", scoped.Filters![0].Column.Name);
    }

    [Fact]
    public void A_question_that_groups_by_another_date_uses_that_one()
    {
        var (scoped, info) = Apply(PerMonth(column: "shipped_on"), new TimeFilter(D(2026, 1, 1), D(2026, 3, 31)));
        Assert.Equal("orders.shipped_on", info.Column);
        Assert.Equal("shipped_on", scoped.Filters![0].Column.Name);
    }

    [Fact]
    public void A_time_grouping_on_a_joined_table_scopes_that_tables_column()
    {
        var q = new QueryDefinition(Shop.Ref("orders"),
            Joins: new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") },
            GroupBy: new[] { new Grouping(Shop.Col("created_at", "users"), DateBucket.Month) },
            Aggregations: new[] { new Aggregation(AggFn.Count) });

        var (scoped, info) = Apply(q, new TimeFilter(D(2026, 1, 1), D(2026, 3, 31), DateBucket.Week));

        Assert.Equal("users.created_at", info.Column);
        Assert.Equal("users", scoped.Filters![0].Column.Table!.Table);
        Assert.Equal(DateBucket.Week, scoped.GroupBy![0].Bucket);
        // and it compiles: the filter lands on the joined table's alias
        Assert.Contains("\"t1\".\"created_at\" >=", QueryCompiler.Compile(scoped, Shop.Meta).Query.Sql);
    }

    [Fact]
    public void Picks_the_most_date_like_column_and_falls_back_to_the_first_date()
    {
        TableMetadata T(params (string Name, string Raw)[] cols) =>
            new("public", "t", TableKind.Table,
                cols.Select(c => new ColumnMetadata(c.Name, c.Raw, TypeMapper.Map(c.Raw), false, false)).ToList(),
                Array.Empty<string>(), Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>());

        Assert.Equal("created_at", TimeScope.PickTimeColumn(T(("id", "integer"), ("born", "date"), ("created_at", "timestamp with time zone")))!.Name);
        Assert.Equal("happened_at", TimeScope.PickTimeColumn(T(("user_id", "integer"), ("happened_at", "timestamp with time zone")))!.Name);
        Assert.Equal("born", TimeScope.PickTimeColumn(T(("id", "integer"), ("born", "date")))!.Name);   // no date-like name: the first date
        Assert.Null(TimeScope.PickTimeColumn(T(("id", "integer"), ("name", "text"))));
    }

    // ---- over the wire ---------------------------------------------------------------------

    [Fact]
    public void The_filter_reads_from_the_json_a_browser_sends()
    {
        var f = JsonSerializer.Deserialize<TimeFilter>("""{ "from": "2026-01-01", "to": "2026-03-31", "granularity": "week" }""", DataLensJson.Options)!;
        Assert.Equal(new TimeFilter(D(2026, 1, 1), D(2026, 3, 31), DateBucket.Week), f);

        var empty = JsonSerializer.Deserialize<TimeFilter>("{}", DataLensJson.Options)!;
        Assert.True(empty.IsEmpty);

        var openEnded = JsonSerializer.Deserialize<TimeFilter>("""{ "from": "2026-01-01", "to": null }""", DataLensJson.Options)!;
        Assert.Null(openEnded.To);
    }
}
