using DataLens.Cli;
using DataLens.Core;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

const string Usage = """
    DataLens CLI

      datalens connect <name> <connection-string>   test, save, and sync a database
      datalens sync <name>                          re-read the structure of a saved database
      datalens show <name> [--json]                 print the stored snapshot
      datalens rows <name> <schema.table> [--page n] [--size n] [--sort column] [--desc]
                                                    print a page of rows
      datalens demo <name>                          build the sample dashboard on the sample shop database
      datalens list                                 list saved connections

    Connection strings: "Host=localhost;Database=shop;Username=datalens_ro;Password=..."
                     or "postgresql://user:pass@host:5432/db"

    Data lives in the .datalens folder at the repository root (override with DATALENS_HOME),
    the same folder the web app uses.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

var home = DataHome.Resolve(Environment.GetEnvironmentVariable("DATALENS_HOME"), Directory.GetCurrentDirectory());
var protector = ConnectionProtector.Create(Path.Combine(home, "key"));
var sqlite = new SqliteAppStore(Path.Combine(home, "app.db"), protector);
IAppStore store = sqlite;
IDatabaseConnector connector = new PostgresConnector();
var connections = new ConnectionService(store, connector);
var explorer = new DataExplorerService(store, connector);
var demo = new DemoDashboardService(store, sqlite);

try
{
    switch (args[0])
    {
        case "connect" when args.Length == 3:
            return await Commands.ConnectAsync(connections, args[1], args[2]);
        case "sync" when args.Length == 2:
            return await Commands.SyncAsync(connections, args[1]);
        case "show" when args.Length is 2 or 3:
            return await Commands.ShowAsync(store, args[1], asJson: args.Length == 3 && args[2] == "--json");
        case "rows" when args.Length >= 3:
            return await Commands.RowsAsync(explorer, args);
        case "demo" when args.Length == 2:
            return await Commands.DemoAsync(demo, args[1]);
        case "list" when args.Length == 1:
            return await Commands.ListAsync(store, home);
        default:
            Console.Error.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex) when (ex is DataLensException or DataLens.Core.Query.QueryFailedException or FormatException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}
