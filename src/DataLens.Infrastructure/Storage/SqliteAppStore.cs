using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace DataLens.Infrastructure.Storage;

/// <summary>
/// The app database. Plain SQL on SQLite, no ORM. Holds saved connections (encrypted) and the
/// latest schema snapshot for each. It never holds any of the source database's row data.
/// </summary>
public sealed partial class SqliteAppStore : IAppStore, IContentStore
{
    private readonly string _connectionString;
    private readonly ConnectionProtector _protector;
    private bool _initialised;

    public SqliteAppStore(string dbPath, ConnectionProtector protector)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Pooling off: every call opens and closes the file, so nothing keeps it locked.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        _protector = protector;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        if (!_initialised)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS connection (
                  id            TEXT PRIMARY KEY,
                  name          TEXT NOT NULL UNIQUE,
                  masked_cs     TEXT NOT NULL,
                  encrypted_cs  TEXT NOT NULL,
                  can_write     INTEGER NOT NULL,
                  created_at    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS schema_snapshot (
                  connection_id TEXT PRIMARY KEY REFERENCES connection(id) ON DELETE CASCADE,
                  version       INTEGER NOT NULL,
                  metadata_json TEXT NOT NULL,
                  synced_at     TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS saved_query (
                  id            TEXT PRIMARY KEY,
                  connection_id TEXT NOT NULL REFERENCES connection(id) ON DELETE CASCADE,
                  name          TEXT NOT NULL,
                  query_json    TEXT NOT NULL,
                  chart_json    TEXT NOT NULL,
                  created_at    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS dashboard (
                  id            TEXT PRIMARY KEY,
                  name          TEXT NOT NULL,
                  created_at    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS dashboard_card (
                  dashboard_id  TEXT NOT NULL REFERENCES dashboard(id) ON DELETE CASCADE,
                  query_id      TEXT NOT NULL REFERENCES saved_query(id) ON DELETE CASCADE,
                  position      INTEGER NOT NULL,
                  PRIMARY KEY (dashboard_id, query_id)
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialised = true;
        }
        return conn;
    }

    public async Task<SavedConnection> SaveConnectionAsync(
        string name, string connectionString, bool canWrite, CancellationToken ct = default)
    {
        var masked = ConnectionStringParser.Mask(connectionString);
        var encrypted = _protector.Protect(ConnectionStringParser.Normalize(connectionString).ConnectionString);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Same name again means "update this connection", and keeps its id and snapshot.
        cmd.CommandText = """
            INSERT INTO connection (id, name, masked_cs, encrypted_cs, can_write, created_at)
            VALUES ($id, $name, $masked, $enc, $canWrite, $createdAt)
            ON CONFLICT(name) DO UPDATE SET
              masked_cs = excluded.masked_cs,
              encrypted_cs = excluded.encrypted_cs,
              can_write = excluded.can_write
            """;
        cmd.Parameters.AddWithValue("$id", "c_" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$masked", masked);
        cmd.Parameters.AddWithValue("$enc", encrypted);
        cmd.Parameters.AddWithValue("$canWrite", canWrite ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        return (await FindConnectionAsync(name, ct))!;
    }

    public async Task<IReadOnlyList<SavedConnection>> ListConnectionsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, masked_cs, can_write, created_at FROM connection ORDER BY name";
        var list = new List<SavedConnection>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadConnection(r));
        return list;
    }

    public async Task<SavedConnection?> FindConnectionAsync(string nameOrId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, masked_cs, can_write, created_at
            FROM connection
            WHERE name = $v COLLATE NOCASE OR id = $v
            ORDER BY (id = $v) DESC, (name = $v) DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$v", nameOrId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadConnection(r) : null;
    }

    public async Task<string?> GetConnectionStringAsync(string connectionId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT encrypted_cs FROM connection WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", connectionId);
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        return value is null ? null : _protector.Unprotect(value);
    }

    public async Task<StoredSnapshot> SaveSnapshotAsync(
        string connectionId, DbMetadata metadata, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        int version;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT version FROM schema_snapshot WHERE connection_id = $id";
            read.Parameters.AddWithValue("$id", connectionId);
            version = (read.ExecuteScalar() is long v ? (int)v : 0) + 1;
        }

        var syncedAt = DateTimeOffset.UtcNow;
        await using (var write = conn.CreateCommand())
        {
            write.Transaction = tx;
            write.CommandText = """
                INSERT INTO schema_snapshot (connection_id, version, metadata_json, synced_at)
                VALUES ($id, $version, $json, $at)
                ON CONFLICT(connection_id) DO UPDATE SET
                  version = excluded.version,
                  metadata_json = excluded.metadata_json,
                  synced_at = excluded.synced_at
                """;
            write.Parameters.AddWithValue("$id", connectionId);
            write.Parameters.AddWithValue("$version", version);
            write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(metadata, DataLensJson.Options));
            write.Parameters.AddWithValue("$at", syncedAt.ToString("O"));
            await write.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new StoredSnapshot(connectionId, version, syncedAt, metadata);
    }

    public async Task<StoredSnapshot?> GetLatestSnapshotAsync(string connectionId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version, metadata_json, synced_at FROM schema_snapshot WHERE connection_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", connectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var metadata = JsonSerializer.Deserialize<DbMetadata>(r.GetString(1), DataLensJson.Options)
            ?? throw new InvalidOperationException("The stored snapshot is empty.");
        return new StoredSnapshot(connectionId, r.GetInt32(0), DateTimeOffset.Parse(r.GetString(2)), metadata);
    }

    private static SavedConnection ReadConnection(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1,
        DateTimeOffset.Parse(r.GetString(4)));
}
