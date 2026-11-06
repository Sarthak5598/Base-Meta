using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using Npgsql;
using static DataLens.Tests.PostgresConnectorTests;

namespace DataLens.Tests;

/// <summary>
/// Public read-only databases (RNAcentral, for one) refuse reads of pg_proc, and Npgsql reads it when it
/// connects: "permission denied for table pg_proc". DataLens must connect anyway. Catalog permissions are per
/// database, so this builds a scratch database, locks it down there, and drops it, leaving the sample alone.
/// </summary>
public class PostgresLockedDownTests
{
    [PgFact]
    public async Task A_server_that_denies_reading_pg_proc_still_connects_syncs_and_runs_queries()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dbName = $"datalens_locked_{suffix}";
        var role = $"locked_{suffix}";

        var admin = new NpgsqlConnectionStringBuilder(ReadWrite!);
        async Task Exec(string database, string sql)
        {
            var b = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database, Pooling = false };
            await using var conn = new NpgsqlConnection(b.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await Exec("postgres", $"CREATE DATABASE {dbName}");
        try
        {
            await Exec("postgres", $"CREATE ROLE {role} LOGIN PASSWORD 'pw' NOSUPERUSER");
            await Exec(dbName, $"""
                CREATE TABLE things (id int PRIMARY KEY, label text NOT NULL);
                INSERT INTO things VALUES (1, 'a'), (2, 'b');
                GRANT USAGE ON SCHEMA public TO {role};
                GRANT SELECT ON things TO {role};
                REVOKE SELECT ON pg_catalog.pg_proc FROM PUBLIC;
                """);

            var locked = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = dbName, Username = role, Password = "pw"
            }.ConnectionString;
            var connector = new PostgresConnector();

            var test = await connector.TestAsync(locked);
            Assert.True(test.Ok, test.Error);

            var schema = await connector.DescribeAsync(locked);
            Assert.Contains(schema.Tables, t => t.Name == "things");

            var result = await connector.ExecuteAsync(
                locked,
                new CompiledQuery("SELECT id, label FROM things ORDER BY id", new Dictionary<string, object?>()),
                ExecOptions.Default);
            Assert.Equal(2, result.Rows.Count);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec("postgres", $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await Exec("postgres", $"DROP ROLE IF EXISTS {role}");
        }
    }
}
