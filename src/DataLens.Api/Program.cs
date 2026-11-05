using System.Text.Json;
using System.Text.Json.Serialization;
using DataLens.Api.Endpoints;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;
using DataLens.Infrastructure.Postgres;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Local tool, no login yet: listen on this machine only, unless told otherwise.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://localhost:5080");

// Same folder the CLI uses, so a connection added in one shows up in the other.
var home = builder.Configuration["DATALENS_HOME"]   // environment variable, or a test's own setting
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".datalens");

builder.Services.AddSingleton(ConnectionProtector.Create(Path.Combine(home, "key")));
builder.Services.AddSingleton(sp =>
    new SqliteAppStore(Path.Combine(home, "app.db"), sp.GetRequiredService<ConnectionProtector>()));
builder.Services.AddSingleton<IAppStore>(sp => sp.GetRequiredService<SqliteAppStore>());
builder.Services.AddSingleton<IContentStore>(sp => sp.GetRequiredService<SqliteAppStore>());
builder.Services.AddSingleton<IDatabaseConnector, PostgresConnector>();
builder.Services.AddSingleton<ConnectionService>();
builder.Services.AddSingleton<DataExplorerService>();
builder.Services.AddSingleton<QuestionService>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

// Turn expected failures into a status code and a message the UI can show.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        var (status, message) = ex switch
        {
            DataLensException { Error: DataLensError.NotFound } e => (404, e.Message),
            DataLensException e => (400, e.Message),
            FormatException e => (400, e.Message),
            QueryFailedException { Reason: QueryFailure.Timeout } e => (504, e.Message),
            QueryFailedException { Reason: QueryFailure.PermissionDenied } e => (403, e.Message),
            QueryFailedException { Reason: QueryFailure.ObjectNotFound } e => (409, e.Message),
            QueryFailedException { Reason: QueryFailure.WriteBlocked } e => (400, e.Message),
            QueryFailedException e => (502, e.Message),
            _ => (500, "Something went wrong on the server.")
        };
        if (status == 500)
            app.Logger.LogError(ex, "Unhandled error on {Path}", context.Request.Path);

        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});

var api = app.MapGroup("/api");

api.MapGet("/connections", async (IAppStore store, CancellationToken ct) =>
    await store.ListConnectionsAsync(ct));

api.MapPost("/connections", async (ConnectRequest body, ConnectionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.ConnectionString))
        return Results.BadRequest(new { error = "Paste a connection string." });

    var result = await service.ConnectAsync(body.Name ?? "", body.ConnectionString, ct);
    if (!result.Test.Ok)
        return Results.BadRequest(new { error = result.Test.Error });

    return Results.Created($"/api/connections/{result.Connection!.Id}", new
    {
        connection = result.Connection,
        warnings = result.Test.Warnings,
        snapshot = Summarise(result.Snapshot!)
    });
});

api.MapGet("/connections/{id}/schema", async (string id, IAppStore store, CancellationToken ct) =>
{
    var connection = await store.FindConnectionAsync(id, ct)
        ?? throw new DataLensException(DataLensError.NotFound, $"No saved connection '{id}'.");
    var snapshot = await store.GetLatestSnapshotAsync(connection.Id, ct)
        ?? throw new DataLensException(DataLensError.NotFound, "This connection has not been synced yet.");
    return Results.Ok(new { connection, snapshot });
});

api.MapPost("/connections/{id}/schema/refresh", async (string id, ConnectionService service, CancellationToken ct) =>
    Results.Ok(Summarise(await service.SyncAsync(id, ct))));

// schema and table are query parameters, not path segments: names can contain slashes and dots.
api.MapGet("/connections/{id}/rows", async (
    string id, string schema, string table, DataExplorerService explorer, CancellationToken ct,
    int page = 1, int pageSize = DataExplorerService.DefaultPageSize, string? sort = null, string dir = "asc") =>
{
    if (!Enum.TryParse<SortDirection>(dir, ignoreCase: true, out var direction))
        throw new DataLensException(DataLensError.BadRequest, "dir must be asc or desc.");

    return Results.Ok(await explorer.PreviewAsync(
        new PreviewRequest(id, schema, table, page, pageSize, sort, direction), ct));
});

api.MapQuestionEndpoints();

api.MapFallback(() => Results.NotFound(new { error = "Unknown endpoint." }));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallback(async context =>
{
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (index.Exists)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
    }
    else
    {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync(
            "DataLens API is running. The web UI has not been built yet: cd web && npm install && npm run build");
    }
});

app.Run();

static object Summarise(StoredSnapshot snapshot) => new
{
    snapshot.Version,
    snapshot.SyncedAt,
    Tables = snapshot.Metadata.Tables.Count(t => t.Kind == TableKind.Table),
    Views = snapshot.Metadata.Tables.Count(t => t.Kind == TableKind.View),
    Columns = snapshot.Metadata.Tables.Sum(t => t.Columns.Count),
    ForeignKeys = snapshot.Metadata.Tables.Sum(t => t.ForeignKeys.Count)
};

record ConnectRequest(string? Name, string? ConnectionString);

// lets the integration tests start the app in memory
public partial class Program;
