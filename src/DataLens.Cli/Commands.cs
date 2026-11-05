using System.Text.Json;
using System.Text.Json.Serialization;
using DataLens.Core;
using DataLens.Core.Models;

namespace DataLens.Cli;

internal static class Commands
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> ConnectAsync(
        IAppStore store, IDatabaseConnector connector, string name, string connectionString)
    {
        var test = await connector.TestAsync(connectionString);
        if (!test.Ok)
        {
            Console.Error.WriteLine($"Connection failed: {test.Error}");
            return 1;
        }

        Console.WriteLine("Connected.");
        foreach (var warning in test.Warnings)
            Console.WriteLine($"  warning: {warning}");

        var saved = await store.SaveConnectionAsync(name, connectionString, test.CanWrite);
        Console.WriteLine($"Saved as '{saved.Name}' ({saved.Id}): {saved.MaskedConnectionString}");

        return await SyncAsync(store, connector, saved.Name);
    }

    public static async Task<int> SyncAsync(IAppStore store, IDatabaseConnector connector, string nameOrId)
    {
        var saved = await store.FindConnectionAsync(nameOrId);
        if (saved is null)
        {
            Console.Error.WriteLine($"No saved connection called '{nameOrId}'.");
            return 1;
        }

        var connectionString = await store.GetConnectionStringAsync(saved.Id)
            ?? throw new InvalidOperationException("The saved connection string is missing.");

        Console.WriteLine("Reading structure...");
        var metadata = await connector.DescribeAsync(connectionString);
        var snapshot = await store.SaveSnapshotAsync(saved.Id, metadata);

        var tables = metadata.Tables.Count(t => t.Kind == TableKind.Table);
        var views = metadata.Tables.Count - tables;
        var columns = metadata.Tables.Sum(t => t.Columns.Count);
        var fks = metadata.Tables.Sum(t => t.ForeignKeys.Count);
        Console.WriteLine(
            $"Snapshot v{snapshot.Version}: {tables} tables, {views} views, {columns} columns, {fks} foreign keys.");
        return 0;
    }

    public static async Task<int> ShowAsync(IAppStore store, string nameOrId, bool asJson)
    {
        var saved = await store.FindConnectionAsync(nameOrId);
        if (saved is null)
        {
            Console.Error.WriteLine($"No saved connection called '{nameOrId}'.");
            return 1;
        }

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

    public static async Task<int> ListAsync(IAppStore store)
    {
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
}
