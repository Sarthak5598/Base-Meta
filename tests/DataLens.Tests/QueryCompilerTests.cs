using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Tests;

/// <summary>A small shop schema in snapshot form, so compiler tests need no database.</summary>
internal static class Shop
{
    private static ColumnMetadata C(string name, string raw, bool pk = false, bool nullable = false) =>
        new(name, raw, TypeMapper.Map(raw), nullable, pk);

    private static TableMetadata T(string name, ColumnMetadata[] cols, params ForeignKeyMetadata[] fks) =>
        new("public", name, TableKind.Table, cols, cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList(),
            fks, Array.Empty<IndexMetadata>());

    private static ForeignKeyMetadata Fk(string name, string col, string refTable) =>
        new(name, new[] { col }, "public", refTable, new[] { "id" });

    public static readonly DbMetadata Meta = new(new[]
    {
        T("users", new[]
        {
            C("id", "integer", pk: true), C("name", "text"), C("email", "text"), C("country", "text"),
            C("created_at", "timestamp with time zone")
        }),
        T("orders", new[]
        {
            C("id", "integer", pk: true), C("user_id", "integer"), C("status", "text"),
            C("created_at", "timestamp with time zone"), C("shipped_on", "date", nullable: true),
            C("paid", "boolean"), C("note", "jsonb", nullable: true), C("local_time", "timestamp without time zone")
        }, Fk("orders_user_id_fkey", "user_id", "users")),
        T("order_items", new[]
        {
            C("id", "integer", pk: true), C("order_id", "integer"), C("product_id", "integer"),
            C("quantity", "integer"), C("unit_price", "numeric"), C("weight", "double precision"), C("big", "bigint")
        }, Fk("order_items_order_id_fkey", "order_id", "orders"), Fk("order_items_product_id_fkey", "product_id", "products")),
        T("products", new[] { C("id", "integer", pk: true), C("category_id", "integer"), C("name", "text"), C("price", "numeric") },
            Fk("products_category_id_fkey", "category_id", "categories")),
        T("categories", new[] { C("id", "integer", pk: true), C("name", "text") })
    });

    public static TableRef Ref(string table) => new("public", table);
    public static ColumnRef Col(string name, string? table = null) => new(name, table is null ? null : Ref(table));
}

public class QueryCompilerTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static IReadOnlyList<JsonElement> V(params object?[] values) => values.Select(J).ToList();

    private static QueryDefinition Q(string table, Func<QueryDefinition, QueryDefinition>? with = null)
    {
        var q = new QueryDefinition(Shop.Ref(table));
        return with is null ? q : with(q);
    }

    private static CompiledSelect Compile(QueryDefinition q) => QueryCompiler.Compile(q, Shop.Meta);

    private static string Sql(QueryDefinition q) => Compile(q).Query.Sql;

    private static DataLensException Rejects(QueryDefinition q)
    {
        var ex = Assert.Throws<DataLensException>(() => Compile(q));
        Assert.Equal(DataLensError.BadRequest, ex.Error);
        return ex;
    }

    // ---- plain selects ---------------------------------------------------------------------

    [Fact]
    public void No_columns_selects_every_column_of_the_table()
    {
        var c = Compile(Q("categories"));
        Assert.Equal(
            "SELECT \"t0\".\"id\" AS \"id\", \"t0\".\"name\" AS \"name\"\n" +
            "FROM \"public\".\"categories\" AS \"t0\"\n" +
            "LIMIT @p_limit", c.Query.Sql);
        Assert.Equal(1000, c.Query.Parameters["p_limit"]);
        Assert.Equal(new[] { "id", "name" }, c.Columns.Select(x => x.Name));
        Assert.Equal(new[] { ColumnKind.Number, ColumnKind.Text }, c.Columns.Select(x => x.Kind));
    }

    [Fact]
    public void Picks_only_the_columns_asked_for_in_that_order()
    {
        var c = Compile(Q("users", q => q with { Columns = new[] { Shop.Col("name"), Shop.Col("id") }, Limit = 25 }));
        Assert.StartsWith("SELECT \"t0\".\"name\" AS \"name\", \"t0\".\"id\" AS \"id\"\nFROM", c.Query.Sql);
        Assert.Equal(25, c.Query.Parameters["p_limit"]);
    }

    [Fact]
    public void Sorting_uses_result_column_names()
    {
        var sql = Sql(Q("users", q => q with
        {
            OrderBy = new[] { new SortSpec("country"), new SortSpec("name", SortDirection.Desc) }
        }));
        Assert.Contains("ORDER BY \"country\" ASC NULLS LAST, \"name\" DESC NULLS LAST\nLIMIT", sql);
    }

    // ---- summaries -------------------------------------------------------------------------

    [Fact]
    public void Counts_rows_per_group_sorted_by_the_count()
    {
        var sql = Sql(Q("orders", q => q with
        {
            GroupBy = new[] { new Grouping(Shop.Col("status")) },
            Aggregations = new[] { new Aggregation(AggFn.Count) },
            OrderBy = new[] { new SortSpec("count", SortDirection.Desc) }
        }));
        Assert.Equal(
            "SELECT \"t0\".\"status\" AS \"status\", COUNT(*) AS \"count\"\n" +
            "FROM \"public\".\"orders\" AS \"t0\"\n" +
            "GROUP BY 1\n" +
            "ORDER BY \"count\" DESC NULLS LAST\n" +
            "LIMIT @p_limit", sql);
    }

    [Fact]
    public void A_summary_without_grouping_is_one_row()
    {
        var c = Compile(Q("order_items", q => q with { Aggregations = new[] { new Aggregation(AggFn.Sum, Shop.Col("unit_price")) } }));
        Assert.Equal(
            "SELECT SUM(\"t0\".\"unit_price\") AS \"sum_unit_price\"\nFROM \"public\".\"order_items\" AS \"t0\"\nLIMIT @p_limit",
            c.Query.Sql);
        Assert.Equal(ColumnKind.Number, c.Columns[0].Kind);
    }

    [Theory]
    [InlineData(AggFn.Sum, "SUM(\"t0\".\"unit_price\")", "sum_unit_price")]
    [InlineData(AggFn.Avg, "AVG(\"t0\".\"unit_price\")", "avg_unit_price")]
    [InlineData(AggFn.Min, "MIN(\"t0\".\"unit_price\")", "min_unit_price")]
    [InlineData(AggFn.Max, "MAX(\"t0\".\"unit_price\")", "max_unit_price")]
    [InlineData(AggFn.Count, "COUNT(\"t0\".\"unit_price\")", "count_unit_price")]
    [InlineData(AggFn.CountDistinct, "COUNT(DISTINCT \"t0\".\"unit_price\")", "distinct_unit_price")]
    public void Every_summary_function(AggFn fn, string expression, string name)
    {
        var c = Compile(Q("order_items", q => q with { Aggregations = new[] { new Aggregation(fn, Shop.Col("unit_price")) } }));
        Assert.Contains($"{expression} AS \"{name}\"", c.Query.Sql);
        Assert.Equal(name, c.Columns[0].Name);
    }

    [Fact]
    public void Min_and_max_keep_the_kind_of_the_column()
    {
        var c = Compile(Q("orders", q => q with
        {
            Aggregations = new[] { new Aggregation(AggFn.Max, Shop.Col("created_at")), new Aggregation(AggFn.Min, Shop.Col("status")) }
        }));
        Assert.Equal(new[] { ColumnKind.Timestamp, ColumnKind.Text }, c.Columns.Select(x => x.Kind));
    }

    [Fact]
    public void Summary_names_can_be_chosen_and_duplicates_are_made_unique()
    {
        var c = Compile(Q("orders", q => q with
        {
            Aggregations = new[]
            {
                new Aggregation(AggFn.Count), new Aggregation(AggFn.Count),
                new Aggregation(AggFn.Count, null, "  orders placed ")
            }
        }));
        Assert.Equal(new[] { "count", "count_2", "orders placed" }, c.Columns.Select(x => x.Name));
    }

    [Theory]
    [InlineData(DateBucket.Day, "day")]
    [InlineData(DateBucket.Week, "week")]
    [InlineData(DateBucket.Month, "month")]
    [InlineData(DateBucket.Quarter, "quarter")]
    [InlineData(DateBucket.Year, "year")]
    public void Groups_a_date_by_a_period(DateBucket bucket, string sqlName)
    {
        var c = Compile(Q("orders", q => q with
        {
            GroupBy = new[] { new Grouping(Shop.Col("created_at"), bucket) },
            Aggregations = new[] { new Aggregation(AggFn.Count) }
        }));
        Assert.Contains($"date_trunc('{sqlName}', \"t0\".\"created_at\") AS \"created_at\"", c.Query.Sql);
        Assert.Equal(ColumnKind.Timestamp, c.Columns[0].Kind);
    }

    [Fact]
    public void Grouping_alone_lists_the_distinct_values()
    {
        var sql = Sql(Q("users", q => q with { GroupBy = new[] { new Grouping(Shop.Col("country")) } }));
        Assert.Contains("SELECT \"t0\".\"country\" AS \"country\"\nFROM", sql);
        Assert.Contains("GROUP BY 1", sql);
    }

    [Fact]
    public void Groups_by_several_columns_in_order()
    {
        var sql = Sql(Q("orders", q => q with
        {
            GroupBy = new[] { new Grouping(Shop.Col("status")), new Grouping(Shop.Col("paid")) },
            Aggregations = new[] { new Aggregation(AggFn.Count) }
        }));
        Assert.Contains("GROUP BY 1, 2", sql);
    }

    // ---- filters ---------------------------------------------------------------------------

    private static CompiledSelect Filtered(string table, string column, FilterOp op, params object?[] values) =>
        Compile(Q(table, q => q with { Filters = new[] { new Filter(Shop.Col(column), op, V(values)) } }));

    [Theory]
    [InlineData(FilterOp.Eq, "=")]
    [InlineData(FilterOp.NotEq, "<>")]
    [InlineData(FilterOp.Lt, "<")]
    [InlineData(FilterOp.Lte, "<=")]
    [InlineData(FilterOp.Gt, ">")]
    [InlineData(FilterOp.Gte, ">=")]
    public void Comparison_filters_use_a_parameter(FilterOp op, string symbol)
    {
        var c = Filtered("order_items", "quantity", op, 3);
        Assert.Contains($"WHERE \"t0\".\"quantity\" {symbol} @p1\n", c.Query.Sql);
        Assert.Equal(3L, c.Query.Parameters["p1"]);
    }

    [Fact]
    public void Values_never_appear_in_the_sql_text()
    {
        var c = Filtered("users", "name", FilterOp.Eq, "x'; DROP TABLE users; --");
        Assert.DoesNotContain("DROP", c.Query.Sql);
        Assert.Equal("x'; DROP TABLE users; --", c.Query.Parameters["p1"]);
    }

    [Fact]
    public void Between_takes_two_values()
    {
        var c = Filtered("order_items", "quantity", FilterOp.Between, 2, 5);
        Assert.Contains("WHERE (\"t0\".\"quantity\" BETWEEN @p1 AND @p2)", c.Query.Sql);
        Assert.Equal(new object?[] { 2L, 5L }, new[] { c.Query.Parameters["p1"], c.Query.Parameters["p2"] });
    }

    [Fact]
    public void In_sends_one_typed_array()
    {
        var c = Filtered("users", "country", FilterOp.In, "US", "DE", "IN");
        Assert.Contains("WHERE \"t0\".\"country\" = ANY(@p1)", c.Query.Sql);
        Assert.Equal(new[] { "US", "DE", "IN" }, Assert.IsType<string[]>(c.Query.Parameters["p1"]));

        var numbers = Filtered("order_items", "quantity", FilterOp.In, 1, 2);
        Assert.Equal(new long[] { 1, 2 }, Assert.IsType<long[]>(numbers.Query.Parameters["p1"]));
    }

    [Theory]
    [InlineData(FilterOp.Contains, "%50\\%\\_off%")]
    [InlineData(FilterOp.StartsWith, "50\\%\\_off%")]
    [InlineData(FilterOp.EndsWith, "%50\\%\\_off")]
    public void Text_matches_are_case_insensitive_and_escape_wildcards(FilterOp op, string expectedPattern)
    {
        // the user typed % and _ literally: they must not act as wildcards
        var c = Filtered("products", "name", op, "50%_off");
        Assert.Contains("\"t0\".\"name\" ILIKE @p1", c.Query.Sql);
        Assert.Equal(expectedPattern, c.Query.Parameters["p1"]);
    }

    [Fact]
    public void Null_checks_take_no_value()
    {
        Assert.Contains("WHERE \"t0\".\"shipped_on\" IS NULL", Filtered("orders", "shipped_on", FilterOp.IsNull).Query.Sql);
        Assert.Contains("WHERE \"t0\".\"shipped_on\" IS NOT NULL", Filtered("orders", "shipped_on", FilterOp.NotNull).Query.Sql);
        Assert.DoesNotContain("p1", Filtered("orders", "shipped_on", FilterOp.IsNull).Query.Parameters.Keys);
    }

    [Fact]
    public void Several_filters_are_all_required_and_numbered_in_order()
    {
        var c = Compile(Q("orders", q => q with
        {
            Filters = new[]
            {
                new Filter(Shop.Col("status"), FilterOp.Eq, V("paid")),
                new Filter(Shop.Col("user_id"), FilterOp.Gt, V(10)),
                new Filter(Shop.Col("paid"), FilterOp.Eq, V(true))
            }
        }));
        Assert.Contains("WHERE \"t0\".\"status\" = @p1 AND \"t0\".\"user_id\" > @p2 AND \"t0\".\"paid\" = @p3\n", c.Query.Sql);
        Assert.Equal(true, c.Query.Parameters["p3"]);
    }

    [Fact]
    public void Values_are_converted_to_the_column_type()
    {
        // integer column: a long (keeps indexes usable), also from text typed into a box
        Assert.Equal(7L, Filtered("orders", "user_id", FilterOp.Eq, "7").Query.Parameters["p1"]);
        // numeric: decimal; double precision: double
        Assert.Equal(12.5m, Filtered("order_items", "unit_price", FilterOp.Gt, 12.5).Query.Parameters["p1"]);
        Assert.Equal(1.5d, Filtered("order_items", "weight", FilterOp.Lt, 1.5).Query.Parameters["p1"]);
        // bigint beyond 2^53 survives
        Assert.Equal(9_007_199_254_740_993L, Filtered("order_items", "big", FilterOp.Eq, "9007199254740993").Query.Parameters["p1"]);
        // boolean from a string
        Assert.Equal(false, Filtered("orders", "paid", FilterOp.Eq, "false").Query.Parameters["p1"]);
        // date
        Assert.Equal(new DateOnly(2026, 3, 31), Filtered("orders", "shipped_on", FilterOp.Gte, "2026-03-31").Query.Parameters["p1"]);
    }

    [Fact]
    public void Timestamps_use_the_parameter_type_that_matches_the_column()
    {
        var withZone = Filtered("orders", "created_at", FilterOp.Gte, "2026-03-01T10:30:00Z").Query.Parameters["p1"];
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(withZone));

        var withoutZone = Assert.IsType<DateTime>(Filtered("orders", "local_time", FilterOp.Gte, "2026-03-01T10:30:00Z").Query.Parameters["p1"]);
        Assert.Equal(DateTimeKind.Unspecified, withoutZone.Kind);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 30, 0), withoutZone);
    }

    [Fact]
    public void A_date_against_a_timestamp_means_the_whole_day()
    {
        // "created on 2026-03-31" must include 18:00 that day, not only midnight
        var eq = Filtered("orders", "created_at", FilterOp.Eq, "2026-03-31");
        Assert.Contains("(\"t0\".\"created_at\" >= @p1 AND \"t0\".\"created_at\" < @p2)", eq.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), eq.Query.Parameters["p2"]);

        // "up to and including 2026-03-31" runs to the end of that day
        var lte = Filtered("orders", "created_at", FilterOp.Lte, "2026-03-31");
        Assert.Contains("\"t0\".\"created_at\" < @p1", lte.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), lte.Query.Parameters["p1"]);

        // "after 2026-03-31" starts the next day
        var gt = Filtered("orders", "created_at", FilterOp.Gt, "2026-03-31");
        Assert.Contains("\"t0\".\"created_at\" >= @p1", gt.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), gt.Query.Parameters["p1"]);

        // between two dates includes the whole last day
        var between = Filtered("orders", "created_at", FilterOp.Between, "2026-03-01", "2026-03-31");
        Assert.Contains("(\"t0\".\"created_at\" >= @p1 AND \"t0\".\"created_at\" < @p2)", between.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), between.Query.Parameters["p1"]);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), between.Query.Parameters["p2"]);
    }

    [Fact]
    public void An_exact_time_is_compared_as_given()
    {
        var c = Filtered("orders", "created_at", FilterOp.Lte, "2026-03-31T10:00:00Z");
        Assert.Contains("\"t0\".\"created_at\" <= @p1", c.Query.Sql);
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 10, 0, 0, TimeSpan.Zero), c.Query.Parameters["p1"]);
    }

    // ---- joins -----------------------------------------------------------------------------

    [Fact]
    public void Joins_a_table_that_this_one_points_to()
    {
        var c = Compile(Q("orders", q => q with
        {
            Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") },
            GroupBy = new[] { new Grouping(Shop.Col("country", "users")) },
            Aggregations = new[] { new Aggregation(AggFn.Count) }
        }));
        Assert.Equal(
            "SELECT \"t1\".\"country\" AS \"users.country\", COUNT(*) AS \"count\"\n" +
            "FROM \"public\".\"orders\" AS \"t0\"\n" +
            "LEFT JOIN \"public\".\"users\" AS \"t1\" ON \"t0\".\"user_id\" = \"t1\".\"id\"\n" +
            "GROUP BY 1\n" +
            "LIMIT @p_limit", c.Query.Sql);
        Assert.Equal("users.country", c.Columns[0].Name);
    }

    [Fact]
    public void Joins_a_table_that_points_back_at_this_one()
    {
        var sql = Sql(Q("users", q => q with
        {
            Joins = new[] { new JoinDef(Shop.Ref("orders"), Shop.Ref("users"), "orders_user_id_fkey") },
            Aggregations = new[] { new Aggregation(AggFn.Count, Shop.Col("id", "orders")) }
        }));
        Assert.Contains("LEFT JOIN \"public\".\"orders\" AS \"t1\" ON \"t1\".\"user_id\" = \"t0\".\"id\"", sql);
        Assert.Contains("COUNT(\"t1\".\"id\") AS \"count_id\"", sql);
    }

    [Fact]
    public void Joins_can_chain_through_tables_already_in_the_question()
    {
        var sql = Sql(Q("order_items", q => q with
        {
            Joins = new[]
            {
                new JoinDef(Shop.Ref("orders"), Shop.Ref("order_items"), "order_items_order_id_fkey"),
                new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey"),
                new JoinDef(Shop.Ref("products"), Shop.Ref("order_items"), "order_items_product_id_fkey")
            },
            GroupBy = new[] { new Grouping(Shop.Col("country", "users")), new Grouping(Shop.Col("name", "products")) },
            Aggregations = new[] { new Aggregation(AggFn.Sum, Shop.Col("quantity")) }
        }));
        Assert.Contains("FROM \"public\".\"order_items\" AS \"t0\"", sql);
        Assert.Contains("LEFT JOIN \"public\".\"orders\" AS \"t1\" ON \"t0\".\"order_id\" = \"t1\".\"id\"", sql);
        Assert.Contains("LEFT JOIN \"public\".\"users\" AS \"t2\" ON \"t1\".\"user_id\" = \"t2\".\"id\"", sql);
        Assert.Contains("LEFT JOIN \"public\".\"products\" AS \"t3\" ON \"t0\".\"product_id\" = \"t3\".\"id\"", sql);
        Assert.Contains("\"t2\".\"country\" AS \"users.country\", \"t3\".\"name\" AS \"products.name\", SUM(\"t0\".\"quantity\") AS \"sum_quantity\"", sql);
    }

    [Fact]
    public void With_joins_unsummarised_columns_are_prefixed_so_names_cannot_collide()
    {
        var c = Compile(Q("orders", q => q with
        {
            Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") },
            Columns = new[] { Shop.Col("id"), Shop.Col("id", "users"), Shop.Col("status") }
        }));
        Assert.Equal(new[] { "orders.id", "users.id", "orders.status" }, c.Columns.Select(x => x.Name));
    }

    [Fact]
    public void With_joins_and_no_columns_every_table_contributes_its_columns()
    {
        var c = Compile(Q("orders", q => q with
        {
            Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") }
        }));
        Assert.Contains("orders.status", c.Columns.Select(x => x.Name));
        Assert.Contains("users.email", c.Columns.Select(x => x.Name));
    }

    [Fact]
    public void Filters_and_sorting_can_use_joined_tables()
    {
        var sql = Sql(Q("orders", q => q with
        {
            Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") },
            Filters = new[] { new Filter(Shop.Col("country", "users"), FilterOp.Eq, V("DE")) },
            Columns = new[] { Shop.Col("id"), Shop.Col("name", "users") },
            OrderBy = new[] { new SortSpec("users.name") }
        }));
        Assert.Contains("WHERE \"t1\".\"country\" = @p1", sql);
        Assert.Contains("ORDER BY \"users.name\" ASC NULLS LAST", sql);
    }

    // ---- hostile names ---------------------------------------------------------------------

    [Fact]
    public void A_summary_name_with_quotes_stays_inside_its_quotes()
    {
        var c = Compile(Q("orders", q => q with
        {
            Aggregations = new[] { new Aggregation(AggFn.Count, null, "x\" FROM users; DROP TABLE orders; --") }
        }));
        Assert.Contains("COUNT(*) AS \"x\"\" FROM users; DROP TABLE orders; --\"", c.Query.Sql);
        Assert.Equal(1, c.Query.Sql.Split("FROM \"public\"").Length - 1);   // still exactly one real FROM
    }

    [Fact]
    public void Columns_and_tables_that_are_not_in_the_snapshot_never_reach_sql()
    {
        Rejects(Q("orders", q => q with { Columns = new[] { Shop.Col("id\" FROM users; --") } }));
        Rejects(Q("orders; DROP TABLE orders"));
        Rejects(new QueryDefinition(new TableRef("pg_catalog", "pg_user")));
    }

    // ---- rejected questions ----------------------------------------------------------------

    public static IEnumerable<object[]> BadQuestions()
    {
        IReadOnlyList<JsonElement> v(params object?[] values) => values.Select(x => JsonSerializer.SerializeToElement(x)).ToList();
        var join = new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey");

        yield return Row("unknown table", Q("nope"), "not in the schema snapshot");
        yield return Row("unknown column", Q("orders", q => q with { Columns = new[] { Shop.Col("nope") } }), "'nope' is not a column of orders");
        yield return Row("column of a table not in the question", Q("orders", q => q with { Columns = new[] { Shop.Col("email", "users") } }), "'users' is not part of this question");
        yield return Row("sum of text", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.Sum, Shop.Col("status")) } }), "not a number");
        yield return Row("average of a date", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.Avg, Shop.Col("created_at")) } }), "not a number");
        yield return Row("sum without a column", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.Sum) } }), "needs a column");
        yield return Row("distinct without a column", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.CountDistinct) } }), "needs a column");
        yield return Row("max of a boolean", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.Max, Shop.Col("paid")) } }), "Cannot take the largest");
        yield return Row("bucket on text", Q("orders", q => q with { GroupBy = new[] { new Grouping(Shop.Col("status"), DateBucket.Month) } }), "not a date");
        yield return Row("columns and a summary", Q("orders", q => q with { Columns = new[] { Shop.Col("id") }, Aggregations = new[] { new Aggregation(AggFn.Count) } }), "not both");
        yield return Row("sort by something not in the results", Q("orders", q => q with { OrderBy = new[] { new SortSpec("nope") } }), "not one of the result columns");
        yield return Row("sort by a column that was not selected", Q("orders", q => q with { Columns = new[] { Shop.Col("id") }, OrderBy = new[] { new SortSpec("status") } }), "not one of the result columns");
        yield return Row("limit zero", Q("orders", q => q with { Limit = 0 }), "between 1 and");
        yield return Row("limit too big", Q("orders", q => q with { Limit = QueryCompiler.MaxLimit + 1 }), "between 1 and");
        yield return Row("text filter on a number column", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Contains, v("1")) } }), "cannot be filtered with 'contains'");
        yield return Row("greater-than on a boolean", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("paid"), FilterOp.Gt, v(true)) } }), "cannot be filtered");
        yield return Row("any value filter on jsonb", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("note"), FilterOp.Eq, v("x")) } }), "cannot be filtered");
        yield return Row("null check on jsonb is fine but eq is not", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("note"), FilterOp.In, v("x")) } }), "cannot be filtered");
        yield return Row("too few values", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Between, v(1)) } }), "needs 2 values");
        yield return Row("too many values", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Eq, v(1, 2)) } }), "needs 1 value");
        yield return Row("values on a null check", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("shipped_on"), FilterOp.IsNull, v(1)) } }), "needs 0 values");
        yield return Row("no values at all", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Eq) } }), "needs 1 value");
        yield return Row("empty in list", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.In, v()) } }), "between 1 and");
        yield return Row("not a number", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Eq, v("abc")) } }), "expected a whole number");
        yield return Row("fraction for an integer", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Eq, v(1.5)) } }), "expected a whole number");
        yield return Row("number given a bool", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("user_id"), FilterOp.Eq, v(true)) } }), "expected a number");
        yield return Row("not a boolean", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("paid"), FilterOp.Eq, v("maybe")) } }), "expected true or false");
        yield return Row("not a date", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("shipped_on"), FilterOp.Eq, v("soon")) } }), "expected a date");
        yield return Row("not a timestamp", Q("orders", q => q with { Filters = new[] { new Filter(Shop.Col("created_at"), FilterOp.Eq, v(5)) } }), "expected a date");
        yield return Row("join the same table twice", Q("orders", q => q with { Joins = new[] { join, join } }), "already part of this question");
        yield return Row("join the source table to itself", Q("orders", q => q with { Joins = new[] { new JoinDef(Shop.Ref("orders"), Shop.Ref("orders"), "orders_user_id_fkey") } }), "already part of this question");
        yield return Row("join from a table that is not in the question", Q("orders", q => q with { Joins = new[] { new JoinDef(Shop.Ref("products"), Shop.Ref("categories"), "products_category_id_fkey") } }), "is not part of this question yet");
        yield return Row("wrong foreign key name", Q("orders", q => q with { Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "nope") } }), "not linked by a foreign key called 'nope'");
        yield return Row("tables that are not linked by that key", Q("orders", q => q with { Joins = new[] { new JoinDef(Shop.Ref("categories"), Shop.Ref("orders"), "orders_user_id_fkey") } }), "not linked");
        yield return Row("join an unknown table", Q("orders", q => q with { Joins = new[] { new JoinDef(Shop.Ref("nope"), Shop.Ref("orders"), "x") } }), "not in the schema snapshot");
        yield return Row("too many filters", Q("orders", q => q with { Filters = Enumerable.Repeat(new Filter(Shop.Col("id"), FilterOp.IsNull), QueryCompiler.MaxFilters + 1).ToList() }), "At most");
        yield return Row("alias too long", Q("orders", q => q with { Aggregations = new[] { new Aggregation(AggFn.Count, null, new string('a', 64)) } }), "at most 63");

        static object[] Row(string name, QueryDefinition q, string message) => new object[] { name, q, message };
    }

    [Theory]
    [MemberData(nameof(BadQuestions))]
    public void Rejects_a_bad_question_with_a_readable_message(string name, QueryDefinition q, string message)
    {
        var ex = Rejects(q);
        Assert.Contains(message, ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void Money_columns_can_be_summed_but_not_filtered()
    {
        var meta = new DbMetadata(new[]
        {
            new TableMetadata("public", "pay", TableKind.Table,
                new[] { new ColumnMetadata("amount", "money", ColumnKind.Number, false, false) },
                Array.Empty<string>(), Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>())
        });
        var src = new TableRef("public", "pay");

        QueryCompiler.Compile(new QueryDefinition(src, Aggregations: new[] { new Aggregation(AggFn.Sum, new ColumnRef("amount")) }), meta);
        var ex = Assert.Throws<DataLensException>(() => QueryCompiler.Compile(
            new QueryDefinition(src, Filters: new[] { new Filter(new ColumnRef("amount"), FilterOp.Gt, V(5)) }), meta));
        Assert.Contains("money", ex.Message);
    }

    [Fact]
    public void Works_for_views_without_a_primary_key()
    {
        var meta = new DbMetadata(new[]
        {
            new TableMetadata("public", "totals", TableKind.View,
                new[] { new ColumnMetadata("total", "numeric", ColumnKind.Number, true, false) },
                Array.Empty<string>(), Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>())
        });
        var c = QueryCompiler.Compile(new QueryDefinition(new TableRef("public", "totals")), meta);
        Assert.Contains("FROM \"public\".\"totals\"", c.Query.Sql);
    }

    [Fact]
    public void The_query_model_round_trips_through_json_including_filter_values()
    {
        var q = Q("orders", x => x with
        {
            Joins = new[] { new JoinDef(Shop.Ref("users"), Shop.Ref("orders"), "orders_user_id_fkey") },
            Filters = new[]
            {
                new Filter(Shop.Col("status"), FilterOp.In, V("paid", "shipped")),
                new Filter(Shop.Col("created_at"), FilterOp.Between, V("2026-01-01", "2026-03-31"))
            },
            GroupBy = new[] { new Grouping(Shop.Col("created_at"), DateBucket.Month) },
            Aggregations = new[] { new Aggregation(AggFn.CountDistinct, Shop.Col("id", "users"), "buyers") },
            OrderBy = new[] { new SortSpec("buyers", SortDirection.Desc) },
            Limit = 50
        });

        var json = JsonSerializer.Serialize(q, DataLensJson.Options);
        var back = JsonSerializer.Deserialize<QueryDefinition>(json, DataLensJson.Options)!;

        Assert.Equal(Compile(q).Query.Sql, Compile(back).Query.Sql);
        Assert.Contains("\"op\":\"in\"", json);
        Assert.Contains("\"fn\":\"countDistinct\"", json);
        Assert.Contains("\"bucket\":\"month\"", json);
        Assert.Equal(
            Compile(q).Query.Parameters.Select(p => p.Value?.ToString()),
            Compile(back).Query.Parameters.Select(p => p.Value?.ToString()));
    }

    [Fact]
    public void A_query_written_by_hand_in_json_works()
    {
        // this is what the browser sends
        const string json = """
            {
              "source": { "schema": "public", "table": "orders" },
              "groupBy": [ { "column": { "name": "created_at" }, "bucket": "month" } ],
              "aggregations": [ { "fn": "count" } ],
              "filters": [ { "column": { "name": "status" }, "op": "eq", "values": ["paid"] } ],
              "orderBy": [ { "column": "created_at", "direction": "asc" } ],
              "limit": 12
            }
            """;
        var q = JsonSerializer.Deserialize<QueryDefinition>(json, DataLensJson.Options)!;
        var c = Compile(q);
        Assert.Contains("date_trunc('month'", c.Query.Sql);
        Assert.Contains("WHERE \"t0\".\"status\" = @p1", c.Query.Sql);
        Assert.Equal(12, c.Query.Parameters["p_limit"]);
    }
}
