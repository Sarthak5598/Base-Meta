using DataLens.Core.Models;
using DataLens.Infrastructure.Postgres;

namespace DataLens.Tests;

/// <summary>
/// Runs against a real PostgreSQL with db/init/01-seed.sql applied. Skipped unless these are set:
///   DATALENS_TEST_PG_RO  connection string for the read-only user (datalens_ro)
///   DATALENS_TEST_PG_RW  connection string for a user that can write (shop_admin)
/// With the sample container running:
///   export DATALENS_TEST_PG_RO="Host=localhost;Port=5433;Database=shop;Username=datalens_ro;Password=readonly"
///   export DATALENS_TEST_PG_RW="Host=localhost;Port=5433;Database=shop;Username=shop_admin;Password=admin"
/// </summary>
public class PostgresConnectorTests
{
    internal static readonly string? ReadOnly = Environment.GetEnvironmentVariable("DATALENS_TEST_PG_RO");
    internal static readonly string? ReadWrite = Environment.GetEnvironmentVariable("DATALENS_TEST_PG_RW");

    public sealed class PgFactAttribute : FactAttribute
    {
        public PgFactAttribute()
        {
            if (ReadOnly is null || ReadWrite is null)
                Skip = "Set DATALENS_TEST_PG_RO and DATALENS_TEST_PG_RW to run against Postgres.";
        }
    }

    public sealed class PgTheoryAttribute : TheoryAttribute
    {
        public PgTheoryAttribute()
        {
            if (ReadOnly is null || ReadWrite is null)
                Skip = "Set DATALENS_TEST_PG_RO and DATALENS_TEST_PG_RW to run against Postgres.";
        }
    }

    private readonly PostgresConnector _connector = new();

    [PgFact]
    public async Task Read_only_user_connects_without_a_warning()
    {
        var result = await _connector.TestAsync(ReadOnly!);
        Assert.True(result.Ok, result.Error);
        Assert.False(result.CanWrite);
        Assert.Empty(result.Warnings);
    }

    [PgFact]
    public async Task User_with_write_access_connects_with_a_warning()
    {
        var result = await _connector.TestAsync(ReadWrite!);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.CanWrite);
        Assert.Single(result.Warnings);
    }

    [PgFact]
    public async Task Wrong_password_gives_a_readable_error()
    {
        var bad = ConnectionStringParser.Parse(ReadOnly!);
        bad.Password = "definitely-wrong";
        var result = await _connector.TestAsync(bad.ConnectionString);
        Assert.False(result.Ok);
        Assert.Equal("Wrong username or password.", result.Error);
    }

    [PgFact]
    public async Task Unknown_database_gives_a_readable_error()
    {
        var bad = ConnectionStringParser.Parse(ReadOnly!);
        bad.Database = "no_such_db";
        var result = await _connector.TestAsync(bad.ConnectionString);
        Assert.False(result.Ok);
        Assert.Equal("That database does not exist on the server.", result.Error);
    }

    [PgFact]
    public async Task Unreachable_host_fails_quickly_with_a_readable_error()
    {
        var result = await _connector.TestAsync("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x");
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [PgFact]
    public async Task Describe_finds_tables_views_and_columns()
    {
        var meta = await _connector.DescribeAsync(ReadOnly!);

        var names = meta.Tables.Select(t => t.Name).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "users", "orders", "order_items", "products", "categories", "legacy_events", "order_totals" },
            names);

        Assert.Equal(TableKind.View, meta.FindTable("public", "order_totals")!.Kind);

        var orders = meta.FindTable("public", "orders")!;
        Assert.Equal(TableKind.Table, orders.Kind);
        Assert.Equal(new[] { "id", "user_id", "status", "created_at" }, orders.Columns.Select(c => c.Name));
        Assert.Equal(ColumnKind.Number, orders.Columns.Single(c => c.Name == "user_id").Kind);
        Assert.Equal(ColumnKind.Text, orders.Columns.Single(c => c.Name == "status").Kind);
        Assert.Equal(ColumnKind.Timestamp, orders.Columns.Single(c => c.Name == "created_at").Kind);
    }

    [PgFact]
    public async Task Describe_finds_primary_keys()
    {
        var orders = (await _connector.DescribeAsync(ReadOnly!)).FindTable("public", "orders")!;
        Assert.Equal(new[] { "id" }, orders.PrimaryKey);
        Assert.True(orders.Columns.Single(c => c.Name == "id").IsPrimaryKey);
        Assert.False(orders.Columns.Single(c => c.Name == "status").IsPrimaryKey);
    }

    [PgFact]
    public async Task Describe_finds_foreign_keys_between_tables()
    {
        var meta = await _connector.DescribeAsync(ReadOnly!);

        var orderFk = Assert.Single(meta.FindTable("public", "orders")!.ForeignKeys);
        Assert.Equal(new[] { "user_id" }, orderFk.Columns);
        Assert.Equal("users", orderFk.RefTable);
        Assert.Equal(new[] { "id" }, orderFk.RefColumns);

        var items = meta.FindTable("public", "order_items")!;
        Assert.Equal(
            new[] { "orders", "products" },
            items.ForeignKeys.Select(f => f.RefTable).OrderBy(x => x));
    }

    [PgFact]
    public async Task A_relationship_that_is_not_declared_is_not_reported()
    {
        // legacy_events.user_id looks like a link but has no FOREIGN KEY, so sync must not invent one.
        var events = (await _connector.DescribeAsync(ReadOnly!)).FindTable("public", "legacy_events")!;
        Assert.Empty(events.ForeignKeys);
    }

    [PgFact]
    public async Task Describe_finds_indexes_and_uniqueness()
    {
        var meta = await _connector.DescribeAsync(ReadOnly!);

        var orders = meta.FindTable("public", "orders")!;
        Assert.Contains(orders.Indexes, i => i.Name == "orders_user_id_idx" && !i.IsUnique && i.Columns.SequenceEqual(new[] { "user_id" }));
        Assert.Contains(orders.Indexes, i => i.Name == "orders_pkey" && i.IsUnique);

        Assert.Contains(meta.FindTable("public", "users")!.Indexes, i => i.IsUnique && i.Columns.SequenceEqual(new[] { "email" }));
    }

    [PgFact]
    public async Task Describe_skips_system_schemas()
    {
        var meta = await _connector.DescribeAsync(ReadOnly!);
        Assert.DoesNotContain(meta.Tables, t => t.Schema is "pg_catalog" or "information_schema");
    }
}
