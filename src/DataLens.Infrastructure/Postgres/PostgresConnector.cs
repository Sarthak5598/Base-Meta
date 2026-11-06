using System.Diagnostics;
using System.Net.Sockets;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using Npgsql;

namespace DataLens.Infrastructure.Postgres;

public sealed class PostgresConnector : IDatabaseConnector
{
    // Connection strings that needed Npgsql's type loading switched off (see OpenAsync).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoTypeLoading = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NpgsqlDataSource> RelaxedSources = new();

    /// <summary>
    /// Opens a connection. Npgsql reads type information from the catalogs (pg_type, pg_proc) when it connects,
    /// and locked-down servers, such as public read-only databases, refuse that with "permission denied for table
    /// pg_proc". Then we connect again without type loading: built-in types (numbers, text, dates, JSON, arrays of
    /// those) work the same, and anything exotic comes back as text. The result is remembered per server.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenAsync(NpgsqlConnectionStringBuilder builder, CancellationToken ct)
    {
        var key = builder.ConnectionString;
        if (!NoTypeLoading.ContainsKey(key))
        {
            var conn = new NpgsqlConnection(key);
            try
            {
                await conn.OpenAsync(ct);
                return conn;
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                await conn.DisposeAsync();
                NoTypeLoading[key] = true;
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        // A data source per server keeps Npgsql's connection pool working for these connections too.
        var source = RelaxedSources.GetOrAdd(key, k =>
        {
            var b = new NpgsqlDataSourceBuilder(k);
            b.ConfigureTypeLoading(t => t.EnableTypeLoading(false));
            return b.Build();
        });
        return await source.OpenConnectionAsync(ct);
    }

    public async Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = ConnectionStringParser.Parse(connectionString);
        }
        catch (FormatException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }

        try
        {
            await using var conn = await OpenAsync(builder, ct);

            await using (var ping = new NpgsqlCommand("SELECT 1", conn))
                await ping.ExecuteScalarAsync(ct);

            var canWrite = await CanWriteAsync(conn, ct);
            var warnings = new List<string>();
            if (canWrite)
                warnings.Add(
                    "This user has write access. DataLens only runs read-only queries, " +
                    "but a read-only database user is recommended.");

            return new ConnectionTestResult(true, null, canWrite, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectionTestResult.Failed(Explain(ex));
        }
    }

    public async Task<DbMetadata> DescribeAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = ConnectionStringParser.Parse(connectionString);
        await using var conn = await OpenAsync(builder, ct);

        // One read-only transaction so all four reads see the same state of the catalogs.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
            await ro.ExecuteNonQueryAsync(ct);

        var columns = await ReadColumnsAsync(conn, tx, ct);
        var constraints = await ReadConstraintsAsync(conn, tx, ct);
        var indexes = await ReadIndexesAsync(conn, tx, ct);

        await tx.RollbackAsync(ct);
        return Assemble(columns, constraints, indexes);
    }

    // At most as many queries per server as the pool has connections. The rest wait their turn here, instead of
    // opening more connections than the pool allows and failing when it runs out.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public async Task<QueryResult> ExecuteAsync(
        string connectionString, CompiledQuery query, ExecOptions options, CancellationToken ct = default)
    {
        var builder = ConnectionStringParser.Parse(connectionString);
        var gate = Gates.GetOrAdd(builder.ConnectionString, _ => new SemaphoreSlim(Math.Max(1, builder.MaxPoolSize)));
        await gate.WaitAsync(ct);
        try
        {
            return await ExecuteCoreAsync(builder, query, options, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<QueryResult> ExecuteCoreAsync(
        NpgsqlConnectionStringBuilder builder, CompiledQuery query, ExecOptions options, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Npgsql pools physical connections per connection string, so opening one per call is cheap.
            await using var conn = await OpenAsync(builder, ct);

            // The guarantee that DataLens never changes the user's data, even if the database user
            // is allowed to: every query runs read-only and is rolled back, never committed.
            await using var tx = await conn.BeginTransactionAsync(ct);
            await RunAsync(conn, tx, "SET TRANSACTION READ ONLY", ct);
            await RunAsync(conn, tx, "SET LOCAL TIME ZONE 'UTC'", ct);   // same buckets whatever the server's zone
            await RunAsync(conn, tx, $"SET LOCAL statement_timeout = {(int)options.Timeout.TotalMilliseconds}", ct);

            await using var cmd = new NpgsqlCommand(query.Sql, conn, tx);
            cmd.CommandTimeout = (int)Math.Ceiling(options.Timeout.TotalSeconds) + 5;
            foreach (var (name, value) in query.Parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var columns = new List<string>();
            var rows = new List<object?[]>();
            var truncated = false;

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                for (var i = 0; i < reader.FieldCount; i++)
                    columns.Add(reader.GetName(i));

                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count >= options.MaxRows)
                    {
                        // More rows exist than we are willing to read. Stop the server producing them.
                        truncated = true;
                        cmd.Cancel();
                        break;
                    }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                        row[i] = ValueNormalizer.Normalize(reader.GetValue(i));
                    rows.Add(row);
                }
            }
            catch (PostgresException ex) when (truncated && ex.SqlState == "57014")
            {
                // The cancel we sent ourselves arriving. Expected, not an error.
            }

            await tx.RollbackAsync(ct);
            stopwatch.Stop();
            return new QueryResult(columns, rows, truncated, stopwatch.ElapsedMilliseconds);
        }
        catch (PostgresException ex)
        {
            throw new QueryFailedException(Classify(ex), Describe(ex, options), ex);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new QueryFailedException(QueryFailure.Other, Explain(ex), ex);
        }
    }

    private static async Task RunAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static QueryFailure Classify(PostgresException ex) => ex.SqlState switch
    {
        "57014" => QueryFailure.Timeout,
        "42501" => QueryFailure.PermissionDenied,
        "42P01" or "42703" or "3F000" => QueryFailure.ObjectNotFound,
        "25006" => QueryFailure.WriteBlocked,
        _ => QueryFailure.Other
    };

    private static string Describe(PostgresException ex, ExecOptions options) => Classify(ex) switch
    {
        QueryFailure.Timeout => $"The query took longer than {options.Timeout.TotalSeconds:0.#} seconds and was stopped.",
        QueryFailure.PermissionDenied => "This database user is not allowed to read that table.",
        _ when ex.SqlState == "53300" => TooManyConnections,
        QueryFailure.ObjectNotFound => "A table or column no longer exists. Re-sync the connection and try again.",
        QueryFailure.WriteBlocked => "That statement would change data, which DataLens does not allow.",
        _ => ex.MessageText
    };

    // ---- connection test helpers -------------------------------------------------------------

    private static async Task<bool> CanWriteAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
              COALESCE((SELECT rolsuper FROM pg_roles WHERE rolname = current_user), false)
              OR EXISTS (
                SELECT 1 FROM pg_tables
                WHERE schemaname <> 'information_schema' AND schemaname NOT LIKE 'pg\_%'
                  AND has_table_privilege(current_user, format('%I.%I', schemaname, tablename),
                                          'INSERT,UPDATE,DELETE'))
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private const string TooManyConnections =
        "The database server has no free connection slots right now. Other people or tools are using them all " +
        "(common on shared public databases). Wait a minute and try again.";

    private static string Explain(Exception ex) => ex switch
    {
        PostgresException { SqlState: "53300" } => TooManyConnections,
        PostgresException { SqlState: "28P01" } => "Wrong username or password.",
        PostgresException { SqlState: "28000" } => "This user is not allowed to connect to that database.",
        PostgresException { SqlState: "3D000" } => "That database does not exist on the server.",
        NpgsqlException { InnerException: SocketException } => "Cannot reach the database server. Check the host and port.",
        NpgsqlException { InnerException: TimeoutException } => "The database server did not answer in time.",
        TimeoutException => "The database server did not answer in time.",
        _ => $"Could not connect: {ex.Message}"
    };

    // ---- catalog reads -----------------------------------------------------------------------

    private sealed record ColumnRow(
        string Schema, string Table, bool IsView, string Column, string RawType, bool Nullable);

    private sealed record ConstraintRow(
        string Schema, string Table, string Name, char Type, string[] Columns,
        string? RefSchema, string? RefTable, string[] RefColumns);

    private sealed record IndexRow(string Schema, string Table, string Name, bool Unique, string[] Columns);

    private static async Task<List<ColumnRow>> ReadColumnsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // information_schema only lists what the connecting role may see, which is what we want.
        const string sql = """
            SELECT c.table_schema, c.table_name, t.table_type, c.column_name, c.data_type, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type IN ('BASE TABLE', 'VIEW')
              AND c.table_schema <> 'information_schema' AND c.table_schema NOT LIKE 'pg\_%'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;
        var rows = new List<ColumnRow>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ColumnRow(
                r.GetString(0), r.GetString(1), r.GetString(2) == "VIEW",
                r.GetString(3), r.GetString(4), r.GetString(5) == "YES"));
        return rows;
    }

    private static async Task<List<ConstraintRow>> ReadConstraintsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // Primary keys ('p') and foreign keys ('f'). unnest ... WITH ORDINALITY keeps column order,
        // which matters for composite keys.
        const string sql = """
            SELECT ns.nspname::text, cl.relname::text, con.conname::text, con.contype::text,
                   ARRAY(SELECT att.attname::text
                         FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k.attnum
                         ORDER BY k.ord) AS cols,
                   fns.nspname::text, fcl.relname::text,
                   ARRAY(SELECT att.attname::text
                         FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_attribute att ON att.attrelid = con.confrelid AND att.attnum = k.attnum
                         ORDER BY k.ord) AS refcols
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = cl.relnamespace
            LEFT JOIN pg_class fcl ON fcl.oid = con.confrelid
            LEFT JOIN pg_namespace fns ON fns.oid = fcl.relnamespace
            WHERE con.contype IN ('p', 'f')
              AND ns.nspname <> 'information_schema' AND ns.nspname NOT LIKE 'pg\_%'
            ORDER BY ns.nspname, cl.relname, con.conname
            """;
        var rows = new List<ConstraintRow>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ConstraintRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)[0],
                r.GetFieldValue<string[]>(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetFieldValue<string[]>(7)));
        return rows;
    }

    private static async Task<List<IndexRow>> ReadIndexesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // attnum 0 means an expression column, which we skip.
        const string sql = """
            SELECT ns.nspname::text, t.relname::text, i.relname::text, ix.indisunique,
                   ARRAY(SELECT att.attname::text
                         FROM unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_attribute att ON att.attrelid = t.oid AND att.attnum = k.attnum
                         WHERE k.attnum > 0
                         ORDER BY k.ord) AS cols
            FROM pg_index ix
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace ns ON ns.oid = t.relnamespace
            WHERE ix.indisvalid
              AND ns.nspname <> 'information_schema' AND ns.nspname NOT LIKE 'pg\_%'
            ORDER BY ns.nspname, t.relname, i.relname
            """;
        var rows = new List<IndexRow>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new IndexRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
                r.GetFieldValue<string[]>(4)));
        return rows;
    }

    // ---- assembling --------------------------------------------------------------------------

    private static DbMetadata Assemble(
        List<ColumnRow> columns, List<ConstraintRow> constraints, List<IndexRow> indexes)
    {
        var tables = new List<TableMetadata>();

        foreach (var group in columns.GroupBy(c => (c.Schema, c.Table)))
        {
            var (schema, table) = group.Key;

            var pk = constraints
                .Where(c => c.Type == 'p' && c.Schema == schema && c.Table == table)
                .Select(c => (IReadOnlyList<string>)c.Columns)
                .FirstOrDefault() ?? Array.Empty<string>();

            var foreignKeys = constraints
                .Where(c => c.Type == 'f' && c.Schema == schema && c.Table == table && c.RefTable is not null)
                .Select(c => new ForeignKeyMetadata(c.Name, c.Columns, c.RefSchema!, c.RefTable!, c.RefColumns))
                .ToList();

            var tableIndexes = indexes
                .Where(i => i.Schema == schema && i.Table == table)
                .Select(i => new IndexMetadata(i.Name, i.Columns, i.Unique))
                .ToList();

            var cols = group
                .Select(c => new ColumnMetadata(
                    c.Column, c.RawType, TypeMapper.Map(c.RawType), c.Nullable, pk.Contains(c.Column)))
                .ToList();

            tables.Add(new TableMetadata(
                schema, table, group.First().IsView ? TableKind.View : TableKind.Table,
                cols, pk, foreignKeys, tableIndexes));
        }

        return new DbMetadata(tables);
    }
}
