using System.Security.Cryptography;
using DataLens.Core.Models;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

public sealed class SqliteAppStoreTests : IDisposable
{
    private const string Cs = "Host=db;Database=shop;Username=ro;Password=hunter2";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAppStore _store;

    public SqliteAppStoreTests()
    {
        _store = new SqliteAppStore(
            Path.Combine(_dir, "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static DbMetadata Sample(string tableName = "orders") => new(new[]
    {
        new TableMetadata(
            "public", tableName, TableKind.Table,
            new[]
            {
                new ColumnMetadata("id", "integer", ColumnKind.Number, false, true),
                new ColumnMetadata("user_id", "integer", ColumnKind.Number, false, false)
            },
            new[] { "id" },
            new[] { new ForeignKeyMetadata("fk", new[] { "user_id" }, "public", "users", new[] { "id" }) },
            new[] { new IndexMetadata("orders_pkey", new[] { "id" }, true) })
    });

    [Fact]
    public async Task Saves_a_connection_and_returns_the_decrypted_string()
    {
        var saved = await _store.SaveConnectionAsync("shop", Cs, canWrite: false);

        Assert.StartsWith("c_", saved.Id);
        Assert.DoesNotContain("hunter2", saved.MaskedConnectionString);
        Assert.Contains("hunter2", (await _store.GetConnectionStringAsync(saved.Id))!);
    }

    [Fact]
    public async Task The_password_is_not_readable_in_the_database_file()
    {
        await _store.SaveConnectionAsync("shop", Cs, canWrite: false);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dir, "app.db"));
        Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Saving_the_same_name_updates_in_place_and_keeps_the_id()
    {
        var first = await _store.SaveConnectionAsync("shop", Cs, canWrite: false);
        var second = await _store.SaveConnectionAsync("shop", Cs.Replace("hunter2", "newpw"), canWrite: true);

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.CanWrite);
        Assert.Single(await _store.ListConnectionsAsync());
        Assert.Contains("newpw", (await _store.GetConnectionStringAsync(first.Id))!);
    }

    [Fact]
    public async Task Finds_by_name_or_id()
    {
        var saved = await _store.SaveConnectionAsync("shop", Cs, false);
        Assert.Equal(saved.Id, (await _store.FindConnectionAsync("shop"))!.Id);
        Assert.Equal(saved.Id, (await _store.FindConnectionAsync(saved.Id))!.Id);
        Assert.Null(await _store.FindConnectionAsync("nope"));
    }

    [Fact]
    public async Task Snapshot_round_trips_including_relationships()
    {
        var conn = await _store.SaveConnectionAsync("shop", Cs, false);
        await _store.SaveSnapshotAsync(conn.Id, Sample());

        var loaded = (await _store.GetLatestSnapshotAsync(conn.Id))!;
        var table = loaded.Metadata.FindTable("public", "orders")!;

        Assert.Equal(1, loaded.Version);
        Assert.Equal(new[] { "id" }, table.PrimaryKey);
        Assert.Equal(ColumnKind.Number, table.Columns[0].Kind);
        Assert.Equal("users", table.ForeignKeys[0].RefTable);
        Assert.True(table.Indexes[0].IsUnique);
    }

    [Fact]
    public async Task Saving_again_replaces_the_snapshot_and_bumps_the_version()
    {
        var conn = await _store.SaveConnectionAsync("shop", Cs, false);
        await _store.SaveSnapshotAsync(conn.Id, Sample("orders"));
        var second = await _store.SaveSnapshotAsync(conn.Id, Sample("orders_v2"));

        var loaded = (await _store.GetLatestSnapshotAsync(conn.Id))!;
        Assert.Equal(2, second.Version);
        Assert.Equal(2, loaded.Version);
        Assert.Single(loaded.Metadata.Tables);
        Assert.Equal("orders_v2", loaded.Metadata.Tables[0].Name);
    }

    [Fact]
    public async Task No_snapshot_before_the_first_sync()
    {
        var conn = await _store.SaveConnectionAsync("shop", Cs, false);
        Assert.Null(await _store.GetLatestSnapshotAsync(conn.Id));
    }
}
