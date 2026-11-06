using System.Text.Json;
using System.Text.Json.Serialization;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Cli;

internal static class Commands
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> ConnectAsync(ConnectionService service, string name, string connectionString)
    {
        var result = await service.ConnectAsync(name, connectionString);
        if (!result.Test.Ok)
        {
            Console.Error.WriteLine($"Connection failed: {result.Test.Error}");
            return 1;
        }

        Console.WriteLine("Connected.");
        foreach (var warning in result.Test.Warnings)
            Console.WriteLine($"  warning: {warning}");
        Console.WriteLine($"Saved as '{result.Connection!.Name}' ({result.Connection.Id}): {result.Connection.MaskedConnectionString}");
        Console.WriteLine(Summary(result.Snapshot!));
        return 0;
    }

    public static async Task<int> SyncAsync(ConnectionService service, string nameOrId)
    {
        Console.WriteLine("Reading structure...");
        Console.WriteLine(Summary(await service.SyncAsync(nameOrId)));
        return 0;
    }

    private static string Summary(StoredSnapshot snapshot)
    {
        var tables = snapshot.Metadata.Tables.Count(t => t.Kind == TableKind.Table);
        var views = snapshot.Metadata.Tables.Count - tables;
        var columns = snapshot.Metadata.Tables.Sum(t => t.Columns.Count);
        var fks = snapshot.Metadata.Tables.Sum(t => t.ForeignKeys.Count);
        return $"Snapshot v{snapshot.Version}: {tables} tables, {views} views, {columns} columns, {fks} foreign keys.";
    }

    public static async Task<int> ShowAsync(IAppStore store, string nameOrId, bool asJson)
    {
        var saved = await store.FindConnectionAsync(nameOrId) ?? throw await ConnectionNotFound.CreateAsync(store, nameOrId);

        var snapshot = await store.GetLatestSnapshotAsync(saved.Id);
        if (snapshot is null)
        {
            Console.Error.WriteLine("No snapshot yet. Run: datalens sync " + saved.Name);
            return 1;
        }

        Console.WriteLine(asJson
            ? JsonSerializer.Serialize(snapshot, PrettyJson)
            : SnapshotPrinter.Render(saved, snapshot));
        return 0;
    }

    public static async Task<int> DemoAsync(DemoDashboardService demo, string connection)
    {
        var (dashboard, created) = await demo.CreateAsync(connection);
        Console.WriteLine(created
            ? $"Created the dashboard '{dashboard.Name}' with {dashboard.Cards.Count} cards. Open the web app to see it."
            : $"'{dashboard.Name}' already exists ({dashboard.Cards.Count} cards).");
        return 0;
    }

    public static async Task<int> ListAsync(IAppStore store, string dataFolder)
    {
        Console.WriteLine($"Data folder: {dataFolder}");
        var list = await store.ListConnectionsAsync();
        if (list.Count == 0)
        {
            Console.WriteLine("No saved connections. Add one with: datalens connect <name> <connection-string>");
            return 0;
        }

        foreach (var c in list)
            Console.WriteLine($"{c.Name,-20} {c.Id}  {(c.CanWrite ? "write access" : "read-only")}  {c.MaskedConnectionString}");
        return 0;
    }

    /// <summary>rows &lt;name&gt; &lt;schema.table&gt; [--page n] [--size n] [--sort col] [--desc]</summary>
    public static async Task<int> RowsAsync(DataExplorerService explorer, string[] args)
    {
        var target = args[2].Split('.', 2);
        if (target.Length != 2)
        {
            Console.Error.WriteLine("Name the table as schema.table, for example public.orders");
            return 1;
        }

        string? Option(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        var request = new PreviewRequest(
            args[1], target[0], target[1],
            Page: int.TryParse(Option("--page"), out var page) ? page : 1,
            PageSize: int.TryParse(Option("--size"), out var size) ? size : 20,
            SortColumn: Option("--sort"),
            SortDirection: args.Contains("--desc") ? SortDirection.Desc : SortDirection.Asc);

        var result = await explorer.PreviewAsync(request);
        Console.WriteLine(RowPrinter.Render(result));
        return 0;
    }
}
