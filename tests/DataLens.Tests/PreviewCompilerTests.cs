using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Tests;

public class PreviewCompilerTests
{
    private static ColumnMetadata Col(string name, bool pk = false) =>
        new(name, "integer", ColumnKind.Number, false, pk);

    private static TableMetadata Table(string schema, string name, string[] pk, params string[] columns) =>
        new(schema, name, TableKind.Table, columns.Select(c => Col(c, pk.Contains(c))).ToList(),
            pk, Array.Empty<ForeignKeyMetadata>(), Array.Empty<IndexMetadata>());

    private static readonly TableMetadata Orders = Table("public", "orders", new[] { "id" }, "id", "user_id", "status");

    [Fact]
    public void Default_order_is_the_primary_key()
    {
        var q = PreviewCompiler.Compile(Orders, null, SortDirection.Asc, 51, 0);
        Assert.Equal(
            "SELECT \"id\", \"user_id\", \"status\" FROM \"public\".\"orders\" ORDER BY \"id\" ASC LIMIT @p_limit OFFSET @p_offset",
            q.Sql);
        Assert.Equal(51, q.Parameters["p_limit"]);
        Assert.Equal(0, q.Parameters["p_offset"]);
    }

    [Fact]
    public void Sort_column_comes_first_and_the_primary_key_breaks_ties()
    {
        var q = PreviewCompiler.Compile(Orders, "status", SortDirection.Desc, 10, 20);
        Assert.Contains("ORDER BY \"status\" DESC NULLS LAST, \"id\" ASC LIMIT", q.Sql);
        Assert.Equal(20, q.Parameters["p_offset"]);
    }

    [Fact]
    public void Sorting_by_the_primary_key_does_not_repeat_it()
    {
        var q = PreviewCompiler.Compile(Orders, "id", SortDirection.Desc, 10, 0);
        Assert.Contains("ORDER BY \"id\" DESC NULLS LAST LIMIT", q.Sql);
    }

    [Fact]
    public void Composite_primary_key_is_used_in_order()
    {
        var t = Table("public", "pairs", new[] { "a", "b" }, "a", "b", "c");
        Assert.Contains("ORDER BY \"a\" ASC, \"b\" ASC LIMIT", PreviewCompiler.Compile(t, null, SortDirection.Asc, 5, 0).Sql);
    }

    [Fact]
    public void Table_without_a_primary_key_falls_back_to_the_first_column()
    {
        var view = Table("public", "order_totals", Array.Empty<string>(), "order_id", "total");
        Assert.Contains("ORDER BY \"order_id\" ASC LIMIT", PreviewCompiler.Compile(view, null, SortDirection.Asc, 5, 0).Sql);
    }

    [Fact]
    public void Identifiers_are_quoted_and_embedded_quotes_are_escaped()
    {
        var evil = Table("we\"ird", "t\"; DROP TABLE x; --", new[] { "id" }, "id", "a\"b");
        var q = PreviewCompiler.Compile(evil, "a\"b", SortDirection.Asc, 5, 0);

        // The hostile text appears only inside one double-quoted identifier, with its quote doubled,
        // so Postgres reads it as a (weird) table name and never as a second statement.
        Assert.Equal(
            "SELECT \"id\", \"a\"\"b\" FROM \"we\"\"ird\".\"t\"\"; DROP TABLE x; --\" " +
            "ORDER BY \"a\"\"b\" ASC NULLS LAST, \"id\" ASC LIMIT @p_limit OFFSET @p_offset",
            q.Sql);
    }

    [Fact]
    public void Values_are_parameters_and_never_in_the_sql()
    {
        var q = PreviewCompiler.Compile(Orders, null, SortDirection.Asc, 51, 12345);
        Assert.DoesNotContain("12345", q.Sql);
        Assert.DoesNotContain("51", q.Sql);
    }

    [Fact]
    public void Unknown_sort_column_is_rejected_even_if_the_caller_forgot_to_check()
    {
        Assert.Throws<ArgumentException>(() =>
            PreviewCompiler.Compile(Orders, "id; DROP TABLE orders", SortDirection.Asc, 5, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, -1)]
    public void Rejects_a_bad_limit_or_offset(int limit, int offset) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PreviewCompiler.Compile(Orders, null, SortDirection.Asc, limit, offset));

    [Fact]
    public void Rejects_a_table_with_no_columns()
    {
        var empty = Table("public", "nothing", Array.Empty<string>());
        Assert.Throws<ArgumentException>(() => PreviewCompiler.Compile(empty, null, SortDirection.Asc, 5, 0));
    }
}
