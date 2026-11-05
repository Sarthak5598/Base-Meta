using System.Text.Json;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using Microsoft.Data.Sqlite;

namespace DataLens.Infrastructure.Storage;

// Saved questions and dashboards. Only the question (query model + chart choice) is stored, never results.
public sealed partial class SqliteAppStore
{
    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];

    // ---- saved questions -------------------------------------------------------------------

    public async Task<SavedQuery> SaveQueryAsync(
        string connectionId, string name, QueryDefinition query, ChartConfig chart, CancellationToken ct = default)
    {
        var saved = new SavedQuery(NewId("q_"), connectionId, name, query, chart, DateTimeOffset.UtcNow);

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_query (id, connection_id, name, query_json, chart_json, created_at)
            VALUES ($id, $conn, $name, $query, $chart, $at)
            """;
        cmd.Parameters.AddWithValue("$id", saved.Id);
        cmd.Parameters.AddWithValue("$conn", connectionId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$query", JsonSerializer.Serialize(query, DataLensJson.Options));
        cmd.Parameters.AddWithValue("$chart", JsonSerializer.Serialize(chart, DataLensJson.Options));
        cmd.Parameters.AddWithValue("$at", saved.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return saved;
    }

    public async Task<SavedQuery?> UpdateQueryAsync(
        string id, string name, QueryDefinition query, ChartConfig chart, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE saved_query SET name = $name, query_json = $query, chart_json = $chart WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$query", JsonSerializer.Serialize(query, DataLensJson.Options));
        cmd.Parameters.AddWithValue("$chart", JsonSerializer.Serialize(chart, DataLensJson.Options));
        return await cmd.ExecuteNonQueryAsync(ct) == 0 ? null : await GetQueryAsync(id, ct);
    }

    public async Task<SavedQuery?> GetQueryAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = QuerySelect + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadQuery(r) : null;
    }

    public async Task<IReadOnlyList<SavedQuery>> ListQueriesAsync(string? connectionId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = QuerySelect + (connectionId is null ? "" : " WHERE connection_id = $conn") + " ORDER BY created_at DESC";
        if (connectionId is not null) cmd.Parameters.AddWithValue("$conn", connectionId);

        var list = new List<SavedQuery>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadQuery(r));
        return list;
    }

    public async Task<bool> DeleteQueryAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_query WHERE id = $id";   // cards go too (ON DELETE CASCADE)
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private const string QuerySelect =
        "SELECT id, connection_id, name, query_json, chart_json, created_at FROM saved_query";

    private static SavedQuery ReadQuery(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<QueryDefinition>(r.GetString(3), DataLensJson.Options)
            ?? throw new InvalidOperationException("A saved question is empty."),
        JsonSerializer.Deserialize<ChartConfig>(r.GetString(4), DataLensJson.Options) ?? new ChartConfig(),
        DateTimeOffset.Parse(r.GetString(5)));

    // ---- dashboards ------------------------------------------------------------------------

    public async Task<Dashboard> CreateDashboardAsync(string name, CancellationToken ct = default)
    {
        var dashboard = new Dashboard(NewId("d_"), name, DateTimeOffset.UtcNow, Array.Empty<DashboardCard>());

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO dashboard (id, name, created_at) VALUES ($id, $name, $at)";
        cmd.Parameters.AddWithValue("$id", dashboard.Id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$at", dashboard.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return dashboard;
    }

    public async Task<Dashboard?> GetDashboardAsync(string id, CancellationToken ct = default) =>
        (await LoadDashboardsAsync(id, ct)).FirstOrDefault();

    public Task<IReadOnlyList<Dashboard>> ListDashboardsAsync(CancellationToken ct = default) =>
        LoadDashboardsAsync(null, ct);

    private async Task<IReadOnlyList<Dashboard>> LoadDashboardsAsync(string? id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var heads = new List<(string Id, string Name, DateTimeOffset At)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, created_at FROM dashboard" +
                              (id is null ? "" : " WHERE id = $id") + " ORDER BY created_at DESC";
            if (id is not null) cmd.Parameters.AddWithValue("$id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                heads.Add((r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        }

        var cards = new Dictionary<string, List<DashboardCard>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT dashboard_id, query_id, position FROM dashboard_card ORDER BY position";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = r.GetString(0);
                if (!cards.TryGetValue(key, out var list)) cards[key] = list = [];
                list.Add(new DashboardCard(r.GetString(1), r.GetInt32(2)));
            }
        }

        return heads
            .Select(h => new Dashboard(h.Id, h.Name, h.At,
                cards.TryGetValue(h.Id, out var c) ? c : new List<DashboardCard>()))
            .ToList();
    }

    public async Task<bool> DeleteDashboardAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dashboard WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<Dashboard?> AddCardAsync(string dashboardId, string queryId, CancellationToken ct = default)
    {
        if (await GetDashboardAsync(dashboardId, ct) is null || await GetQueryAsync(queryId, ct) is null)
            return null;

        await using (var conn = await OpenAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO dashboard_card (dashboard_id, query_id, position)
                VALUES ($d, $q, COALESCE((SELECT MAX(position) + 1 FROM dashboard_card WHERE dashboard_id = $d), 0))
                """;
            cmd.Parameters.AddWithValue("$d", dashboardId);
            cmd.Parameters.AddWithValue("$q", queryId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await GetDashboardAsync(dashboardId, ct);
    }

    public async Task<Dashboard?> RemoveCardAsync(string dashboardId, string queryId, CancellationToken ct = default)
    {
        await using (var conn = await OpenAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM dashboard_card WHERE dashboard_id = $d AND query_id = $q";
            cmd.Parameters.AddWithValue("$d", dashboardId);
            cmd.Parameters.AddWithValue("$q", queryId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await GetDashboardAsync(dashboardId, ct);
    }
}
