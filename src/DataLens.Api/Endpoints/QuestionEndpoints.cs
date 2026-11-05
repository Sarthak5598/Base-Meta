using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Api.Endpoints;

public sealed record SaveQueryRequest(string? ConnectionId, string? Name, QueryDefinition? Query, ChartConfig? Chart);

public static class QuestionEndpoints
{
    private const int MaxNameLength = 100;

    public static void MapQuestionEndpoints(this RouteGroupBuilder api)
    {
        // Check a question and see its SQL without running it. Cheap: it never touches the source database.
        api.MapPost("/connections/{id}/query/compile", async (
            string id, QueryDefinition query, QuestionService questions, CancellationToken ct) =>
            Results.Ok(await questions.CompileAsync(id, query, ct)));

        api.MapPost("/connections/{id}/query/run", async (
            string id, QueryDefinition query, QuestionService questions, CancellationToken ct) =>
            Results.Ok(await questions.RunAsync(id, query, ct)));

        // The values that exist in a column, for filter suggestions. Names are query parameters (they may contain
        // slashes), the search is optional, and the answer is read live from the source database.
        api.MapGet("/connections/{id}/values", async (
            string id, string schema, string table, string column, FieldValuesService values, CancellationToken ct,
            string? search = null, int limit = FieldValuesService.DefaultLimit) =>
            Results.Ok(await values.GetAsync(id, schema, table, column, search, limit, ct)));

        // ---- saved questions -----------------------------------------------------------------

        api.MapGet("/queries", async (IContentStore content, IAppStore store, CancellationToken ct, string? connection = null) =>
        {
            string? connectionId = null;
            if (connection is not null)
                connectionId = (await store.FindConnectionAsync(connection, ct))?.Id
                    ?? throw new DataLensException(DataLensError.NotFound, $"No saved connection '{connection}'.");
            return Results.Ok(await content.ListQueriesAsync(connectionId, ct));
        });

        api.MapPost("/queries", async (SaveQueryRequest body, IContentStore content, IAppStore store, QuestionService questions, CancellationToken ct) =>
        {
            var (name, query, chart) = Validate(body);
            var connection = await store.FindConnectionAsync(body.ConnectionId ?? "", ct)
                ?? throw new DataLensException(DataLensError.BadRequest, "Choose which database this question is about.");

            // Only save questions that are valid right now, so a saved one is never broken from the start.
            await questions.CompileAsync(connection.Id, query, ct);

            var saved = await content.SaveQueryAsync(connection.Id, name, query, chart, ct);
            return Results.Created($"/api/queries/{saved.Id}", saved);
        });

        api.MapGet("/queries/{id}", async (string id, IContentStore content, CancellationToken ct) =>
            Results.Ok(await content.GetQueryAsync(id, ct) ?? throw MissingQuery(id)));

        api.MapPut("/queries/{id}", async (string id, SaveQueryRequest body, IContentStore content, IAppStore store, QuestionService questions, CancellationToken ct) =>
        {
            var (name, query, chart) = Validate(body);
            var existing = await content.GetQueryAsync(id, ct) ?? throw MissingQuery(id);
            await questions.CompileAsync(existing.ConnectionId, query, ct);
            return Results.Ok(await content.UpdateQueryAsync(id, name, query, chart, ct) ?? throw MissingQuery(id));
        });

        api.MapDelete("/queries/{id}", async (string id, IContentStore content, CancellationToken ct) =>
            await content.DeleteQueryAsync(id, ct) ? Results.NoContent() : throw MissingQuery(id));

        // Run a saved question: this is what a dashboard card does. Always live, never a stored result.
        // The body is optional: a dashboard sends its date range and granularity, which apply to this run only.
        // Read by hand so that no body at all (no content type either) is a plain run, not "unknown endpoint".
        api.MapPost("/queries/{id}/run", async (string id, HttpRequest request, IContentStore content, QuestionService questions, CancellationToken ct) =>
        {
            var saved = await content.GetQueryAsync(id, ct) ?? throw MissingQuery(id);
            var time = request.ContentLength is > 0
                ? await request.ReadFromJsonAsync<TimeFilter>(ct)
                : null;
            return Results.Ok(new { query = saved, result = await questions.RunAsync(saved.ConnectionId, saved.Query, time, ct) });
        });

        // ---- dashboards ----------------------------------------------------------------------

        api.MapGet("/dashboards", async (IContentStore content, CancellationToken ct) =>
            Results.Ok(await content.ListDashboardsAsync(ct)));

        api.MapPost("/dashboards", async (NameRequest body, IContentStore content, CancellationToken ct) =>
        {
            var dashboard = await content.CreateDashboardAsync(CheckName(body.Name), ct);
            return Results.Created($"/api/dashboards/{dashboard.Id}", dashboard);
        });

        api.MapGet("/dashboards/{id}", async (string id, IContentStore content, CancellationToken ct) =>
            Results.Ok(await content.GetDashboardAsync(id, ct) ?? throw MissingDashboard(id)));

        api.MapDelete("/dashboards/{id}", async (string id, IContentStore content, CancellationToken ct) =>
            await content.DeleteDashboardAsync(id, ct) ? Results.NoContent() : throw MissingDashboard(id));

        api.MapPost("/dashboards/{id}/cards", async (string id, AddCardRequest body, IContentStore content, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.QueryId))
                throw new DataLensException(DataLensError.BadRequest, "Choose a question to add.");
            if (await content.GetDashboardAsync(id, ct) is null) throw MissingDashboard(id);
            return Results.Ok(await content.AddCardAsync(id, body.QueryId, ct) ?? throw MissingQuery(body.QueryId));
        });

        api.MapDelete("/dashboards/{id}/cards/{queryId}", async (string id, string queryId, IContentStore content, CancellationToken ct) =>
            Results.Ok(await content.RemoveCardAsync(id, queryId, ct) ?? throw MissingDashboard(id)));
    }

    public sealed record NameRequest(string? Name);

    public sealed record AddCardRequest(string? QueryId);

    private static (string Name, QueryDefinition Query, ChartConfig Chart) Validate(SaveQueryRequest body)
    {
        var query = body.Query ?? throw new DataLensException(DataLensError.BadRequest, "The question is missing.");
        return (CheckName(body.Name), query, body.Chart ?? new ChartConfig());
    }

    private static string CheckName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new DataLensException(DataLensError.BadRequest, "Give it a name.");
        if (name.Length > MaxNameLength)
            throw new DataLensException(DataLensError.BadRequest, $"The name can be at most {MaxNameLength} characters.");
        return name;
    }

    private static DataLensException MissingQuery(string id) =>
        new(DataLensError.NotFound, $"No saved question '{id}'.");

    private static DataLensException MissingDashboard(string id) =>
        new(DataLensError.NotFound, $"No dashboard '{id}'.");
}
