using System.Net;
using System.Text;
using System.Text.Json;

namespace DataLens.Tests;

public sealed class ApiQuestionTests : IDisposable
{
    private readonly ApiApp _app = new();
    private readonly HttpClient _http;

    public ApiQuestionTests() => _http = _app.CreateClient();

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = Json(json);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Get(string path) => SendAsync(HttpMethod.Get, path);
    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string json) => SendAsync(HttpMethod.Post, path, json);
    private Task<(HttpStatusCode Status, JsonElement Body)> Put(string path, string json) => SendAsync(HttpMethod.Put, path, json);
    private Task<(HttpStatusCode Status, JsonElement Body)> Delete(string path) => SendAsync(HttpMethod.Delete, path);

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    // A question: orders per status, as the browser would send it
    private const string OrdersByStatus = """
        { "source": {"schema":"public","table":"orders"},
          "groupBy": [ { "column": {"name":"status"} } ],
          "aggregations": [ { "fn": "count" } ],
          "orderBy": [ { "column": "status", "direction": "asc" } ],
          "limit": 100 }
        """;

    private async Task<string> ConnectShopAsync()
    {
        var (status, body) = await Post("/api/connections",
            JsonSerializer.Serialize(new { name = "shop", connectionString = PostgresConnectorTests.ReadOnly }));
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("connection").GetProperty("id").GetString()!;
    }

    private static string SaveBody(string connectionId, string name, string query = OrdersByStatus, string chart = """{"type":"bar","x":"status","y":"count"}""") =>
        $$"""{ "connectionId": "{{connectionId}}", "name": "{{name}}", "query": {{query}}, "chart": {{chart}} }""";

    // ---- no database needed ----------------------------------------------------------------

    [Fact]
    public async Task Starts_with_no_questions_and_no_dashboards()
    {
        Assert.Equal(0, (await Get("/api/queries")).Body.GetArrayLength());
        Assert.Equal(0, (await Get("/api/dashboards")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Running_a_question_on_an_unknown_connection_is_a_404()
    {
        var (status, body) = await Post("/api/connections/nope/query/run", OrdersByStatus);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("nope", Error(body));
    }

    [Fact]
    public async Task Saving_without_a_name_or_a_database_is_a_400()
    {
        var (s1, b1) = await Post("/api/queries", $$"""{ "connectionId": "x", "name": "  ", "query": {{OrdersByStatus}} }""");
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("Give it a name.", Error(b1));

        var (s2, b2) = await Post("/api/queries", $$"""{ "connectionId": "nope", "name": "x", "query": {{OrdersByStatus}} }""");
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("which database", Error(b2));

        var (s3, _) = await Post("/api/queries", """{ "connectionId": "x", "name": "x" }""");
        Assert.Equal(HttpStatusCode.BadRequest, s3);
    }

    [Fact]
    public async Task Missing_questions_and_dashboards_are_404s_with_a_message()
    {
        foreach (var (method, path, json) in new (HttpMethod, string, string?)[]
                 {
                     (HttpMethod.Get, "/api/queries/q_nope", null),
                     (HttpMethod.Delete, "/api/queries/q_nope", null),
                     (HttpMethod.Post, "/api/queries/q_nope/run", null),
                     (HttpMethod.Get, "/api/dashboards/d_nope", null),
                     (HttpMethod.Delete, "/api/dashboards/d_nope", null),
                     (HttpMethod.Post, "/api/dashboards/d_nope/cards", """{"queryId":"q_x"}"""),
                 })
        {
            var (status, body) = await SendAsync(method, path, json);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.False(string.IsNullOrWhiteSpace(Error(body)), $"{method} {path}");
        }
    }

    [Fact]
    public async Task Dashboards_can_be_created_listed_and_deleted_without_any_question()
    {
        var (created, dashboard) = await Post("/api/dashboards", """{"name":"Sales"}""");
        Assert.Equal(HttpStatusCode.Created, created);
        var id = dashboard.GetProperty("id").GetString()!;

        Assert.Equal("Sales", (await Get($"/api/dashboards/{id}")).Body.GetProperty("name").GetString());
        Assert.Equal(1, (await Get("/api/dashboards")).Body.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await Delete($"/api/dashboards/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/dashboards/{id}")).Status);
    }

    [Fact]
    public async Task A_dashboard_needs_a_name()
    {
        var (status, body) = await Post("/api/dashboards", """{"name":""}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Give it a name.", Error(body));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/dashboards", $$"""{"name":"{{new string('x', 101)}}"}""")).Status);
    }

    // ---- against a real database -----------------------------------------------------------

    [PostgresConnectorTests.PgFact]
    public async Task Compile_shows_the_sql_and_the_result_columns_without_running_anything()
    {
        var id = await ConnectShopAsync();
        var (status, body) = await Post($"/api/connections/{id}/query/compile", OrdersByStatus);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("GROUP BY 1", body.GetProperty("sql").GetString());
        Assert.Equal("status", body.GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Equal("text", body.GetProperty("columns")[0].GetProperty("kind").GetString());
        Assert.Equal("count", body.GetProperty("columns")[1].GetProperty("name").GetString());
        Assert.Equal("number", body.GetProperty("columns")[1].GetProperty("kind").GetString());
        Assert.Equal(100, body.GetProperty("parameters").GetProperty("p_limit").GetInt32());
    }

    [PostgresConnectorTests.PgFact]
    public async Task Compile_explains_what_is_wrong_with_a_question()
    {
        var id = await ConnectShopAsync();
        var (status, body) = await Post($"/api/connections/{id}/query/compile", """
            { "source": {"schema":"public","table":"orders"},
              "aggregations": [ { "fn": "sum", "column": {"name":"status"} } ] }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not a number", Error(body));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Run_returns_rows_columns_sql_and_timing()
    {
        var id = await ConnectShopAsync();
        var (status, body) = await Post($"/api/connections/{id}/query/run", OrdersByStatus);

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = body.GetProperty("rows");
        Assert.Equal(4, rows.GetArrayLength());   // paid, pending, refunded, shipped
        Assert.Equal("paid", rows[0][0].GetString());
        Assert.Equal(JsonValueKind.Number, rows[0][1].ValueKind);
        Assert.Equal(2000, rows.EnumerateArray().Sum(r => r[1].GetInt32()));
        Assert.False(body.GetProperty("limitReached").GetBoolean());
        Assert.True(body.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Contains("FROM \"public\".\"orders\"", body.GetProperty("sql").GetString());
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_join_question_works_end_to_end_as_json()
    {
        var id = await ConnectShopAsync();
        var (status, body) = await Post($"/api/connections/{id}/query/run", """
            { "source": {"schema":"public","table":"orders"},
              "joins": [ { "table": {"schema":"public","table":"users"},
                           "from":  {"schema":"public","table":"orders"},
                           "foreignKey": "orders_user_id_fkey" } ],
              "groupBy": [ { "column": { "name": "country", "table": {"schema":"public","table":"users"} } } ],
              "aggregations": [ { "fn": "count" } ],
              "filters": [ { "column": {"name":"status"}, "op": "in", "values": ["paid","shipped"] } ],
              "orderBy": [ { "column": "count", "direction": "desc" } ] }
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("users.country", body.GetProperty("columns")[0].GetProperty("name").GetString());
        var counts = body.GetProperty("rows").EnumerateArray().Select(r => r[1].GetInt32()).ToList();
        Assert.Equal(counts.OrderByDescending(c => c), counts);
        Assert.True(counts.Sum() > 0 && counts.Sum() < 2000);
    }

    [PostgresConnectorTests.PgFact]
    public async Task Save_list_open_update_run_and_delete_a_question()
    {
        var id = await ConnectShopAsync();

        var (created, saved) = await Post("/api/queries", SaveBody(id, "Orders by status"));
        Assert.Equal(HttpStatusCode.Created, created);
        var qid = saved.GetProperty("id").GetString()!;
        Assert.Equal("bar", saved.GetProperty("chart").GetProperty("type").GetString());

        Assert.Equal("Orders by status", (await Get($"/api/queries/{qid}")).Body.GetProperty("name").GetString());
        Assert.Equal(1, (await Get("/api/queries")).Body.GetArrayLength());
        Assert.Equal(1, (await Get($"/api/queries?connection={id}")).Body.GetArrayLength());
        Assert.Equal(1, (await Get("/api/queries?connection=shop")).Body.GetArrayLength());   // by name works too
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/queries?connection=nope")).Status);

        // run it: always live
        var (_, run) = await Post($"/api/queries/{qid}/run", "{}");
        Assert.Equal("Orders by status", run.GetProperty("query").GetProperty("name").GetString());
        Assert.Equal(4, run.GetProperty("result").GetProperty("rows").GetArrayLength());

        // rename and change the chart
        var (updatedStatus, updated) = await Put($"/api/queries/{qid}", SaveBody(id, "Order mix", chart: """{"type":"pie","x":"status","y":"count"}"""));
        Assert.Equal(HttpStatusCode.OK, updatedStatus);
        Assert.Equal("Order mix", updated.GetProperty("name").GetString());
        Assert.Equal("pie", updated.GetProperty("chart").GetProperty("type").GetString());
        Assert.Equal(1, (await Get("/api/queries")).Body.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await Delete($"/api/queries/{qid}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/queries/{qid}")).Status);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_broken_question_is_refused_at_save_and_nothing_is_stored()
    {
        var id = await ConnectShopAsync();
        const string broken = """{ "source": {"schema":"public","table":"orders"}, "columns": [ {"name":"nope"} ] }""";

        var (status, body) = await Post("/api/queries", SaveBody(id, "Broken", broken));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'nope' is not a column of orders", Error(body));
        Assert.Equal(0, (await Get("/api/queries")).Body.GetArrayLength());

        // updating a good one into a broken one is refused too, and the good one is unchanged
        var (_, saved) = await Post("/api/queries", SaveBody(id, "Good"));
        var qid = saved.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Put($"/api/queries/{qid}", SaveBody(id, "Good", broken))).Status);
        Assert.Equal("Good", (await Get($"/api/queries/{qid}")).Body.GetProperty("name").GetString());
        Assert.Equal("orders", (await Get($"/api/queries/{qid}")).Body.GetProperty("query").GetProperty("source").GetProperty("table").GetString());
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_dashboard_runs_its_questions_live()
    {
        var id = await ConnectShopAsync();
        var q1 = (await Post("/api/queries", SaveBody(id, "Orders by status"))).Body.GetProperty("id").GetString()!;
        var q2 = (await Post("/api/queries", SaveBody(id, "All categories", """{ "source": {"schema":"public","table":"categories"}, "limit": 10 }""",
            """{"type":"table"}"""))).Body.GetProperty("id").GetString()!;
        var d = (await Post("/api/dashboards", """{"name":"Overview"}""")).Body.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/dashboards/{d}/cards", $$"""{"queryId":"{{q2}}"}""")).Status);
        var (_, withBoth) = await Post($"/api/dashboards/{d}/cards", $$"""{"queryId":"{{q1}}"}""");
        var cards = withBoth.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("queryId").GetString()).ToList();
        Assert.Equal(new[] { q2, q1 }, cards);   // in the order added

        // what the dashboard page does: run each card
        var results = new List<int>();
        foreach (var card in cards)
        {
            var (status, run) = await Post($"/api/queries/{card}/run", "{}");
            Assert.Equal(HttpStatusCode.OK, status);
            results.Add(run.GetProperty("result").GetProperty("rows").GetArrayLength());
        }
        Assert.Equal(new[] { 5, 4 }, results);

        // unknown question to add
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"/api/dashboards/{d}/cards", """{"queryId":"q_nope"}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post($"/api/dashboards/{d}/cards", "{}")).Status);

        // remove one card; delete a question and it leaves the dashboard
        Assert.Equal(1, (await Delete($"/api/dashboards/{d}/cards/{q2}")).Body.GetProperty("cards").GetArrayLength());
        await Delete($"/api/queries/{q1}");
        Assert.Equal(0, (await Get($"/api/dashboards/{d}")).Body.GetProperty("cards").GetArrayLength());
    }

    [PostgresConnectorTests.PgFact]
    public async Task No_response_ever_contains_the_database_password()
    {
        var id = await ConnectShopAsync();
        var (_, saved) = await Post("/api/queries", SaveBody(id, "Orders by status"));
        var qid = saved.GetProperty("id").GetString()!;
        var d = (await Post("/api/dashboards", """{"name":"x"}""")).Body.GetProperty("id").GetString()!;
        await Post($"/api/dashboards/{d}/cards", $$"""{"queryId":"{{qid}}"}""");

        foreach (var path in new[]
                 {
                     "/api/connections", $"/api/connections/{id}/schema", "/api/queries", $"/api/queries/{qid}",
                     "/api/dashboards", $"/api/dashboards/{d}"
                 })
            Assert.DoesNotContain("readonly", (await _http.GetStringAsync(path)));

        var run = await _http.PostAsync($"/api/queries/{qid}/run", Json("{}"));
        Assert.DoesNotContain("readonly", await run.Content.ReadAsStringAsync());
    }

    // ---- a dashboard's date range and granularity ------------------------------------------

    private const string OrdersPerMonth = """
        { "source": {"schema":"public","table":"orders"},
          "groupBy": [ { "column": {"name":"created_at"}, "bucket": "month" } ],
          "aggregations": [ { "fn": "count" } ] }
        """;

    private async Task<string> SaveOrdersPerMonthAsync(string connectionId) =>
        (await Post("/api/queries", SaveBody(connectionId, "Orders per month", OrdersPerMonth, """{"type":"line","x":"created_at","y":"count"}""")))
            .Body.GetProperty("id").GetString()!;

    [PostgresConnectorTests.PgFact]
    public async Task Running_a_saved_question_with_a_date_range_and_granularity()
    {
        var id = await ConnectShopAsync();
        var qid = await SaveOrdersPerMonthAsync(id);
        var today = DateTime.UtcNow.Date;

        var (plainStatus, plain) = await Post($"/api/queries/{qid}/run", "{}");
        var (scopedStatus, scoped) = await Post($"/api/queries/{qid}/run",
            $$"""{ "from": "{{today.AddDays(-60):yyyy-MM-dd}}", "to": "{{today:yyyy-MM-dd}}", "granularity": "week" }""");

        Assert.Equal(HttpStatusCode.OK, plainStatus);
        Assert.Equal(HttpStatusCode.OK, scopedStatus);

        var plainRows = plain.GetProperty("result").GetProperty("rows");
        var scopedRows = scoped.GetProperty("result").GetProperty("rows");
        Assert.True(plainRows.GetArrayLength() >= 12);                                  // a year of months
        Assert.True(scopedRows.GetArrayLength() is >= 8 and <= 10);                     // 60 days of weeks
        Assert.All(scopedRows.EnumerateArray(), r => Assert.Equal(DayOfWeek.Monday, DateTime.Parse(r[0].GetString()!).DayOfWeek));

        var time = scoped.GetProperty("result").GetProperty("time");
        Assert.Equal("orders.created_at", time.GetProperty("column").GetString());
        Assert.True(time.GetProperty("rangeApplied").GetBoolean());
        Assert.True(time.GetProperty("granularityApplied").GetBoolean());

        // the saved question itself is untouched
        var stored = (await Get($"/api/queries/{qid}")).Body.GetProperty("query");
        Assert.Equal("month", stored.GetProperty("groupBy")[0].GetProperty("bucket").GetString());
        Assert.False(stored.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() > 0);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_run_with_no_body_at_all_still_works()
    {
        var id = await ConnectShopAsync();
        var qid = await SaveOrdersPerMonthAsync(id);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/queries/{qid}/run");
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_backwards_date_range_is_a_400_with_a_message()
    {
        var id = await ConnectShopAsync();
        var qid = await SaveOrdersPerMonthAsync(id);

        var (status, body) = await Post($"/api/queries/{qid}/run", """{ "from": "2026-03-31", "to": "2026-01-01" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("start date is after the end date", Error(body));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_bad_date_or_granularity_is_a_400_not_a_crash()
    {
        var id = await ConnectShopAsync();
        var qid = await SaveOrdersPerMonthAsync(id);

        foreach (var body in new[] { """{ "from": "soon" }""", """{ "granularity": "fortnight" }""", """{ "to": 5 }""", "not json" })
        {
            var (status, json) = await Post($"/api/queries/{qid}/run", body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("could not be read", Error(json));   // a message the browser can show, not an empty 400
        }
    }

    // ---- filter suggestions ----------------------------------------------------------------

    private static string ValuesUrl(string id, string table, string column, string? search = null, int? limit = null) =>
        $"/api/connections/{id}/values?schema=public&table={table}&column={column}"
        + (search is null ? "" : $"&search={Uri.EscapeDataString(search)}") + (limit is null ? "" : $"&limit={limit}");

    [PostgresConnectorTests.PgFact]
    public async Task Values_lists_what_is_in_a_column_and_narrows_as_you_type()
    {
        var id = await ConnectShopAsync();

        var (status, all) = await Get(ValuesUrl(id, "orders", "status"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { "paid", "pending", "refunded", "shipped" }, all.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.False(all.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, all.GetProperty("close").GetArrayLength());

        var (_, narrowed) = await Get(ValuesUrl(id, "orders", "status", "pa"));
        Assert.Equal(new[] { "paid" }, narrowed.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_typo_comes_back_as_did_you_mean()
    {
        var id = await ConnectShopAsync();
        var (_, r) = await Get(ValuesUrl(id, "orders", "status", "shiped"));

        Assert.Equal(0, r.GetProperty("values").GetArrayLength());
        Assert.Equal(new[] { "shipped" }, r.GetProperty("close").EnumerateArray().Select(v => v.GetString()));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Number_columns_come_back_as_json_numbers()
    {
        var id = await ConnectShopAsync();
        var (_, r) = await Get(ValuesUrl(id, "orders", "user_id", limit: 3));

        Assert.All(r.GetProperty("values").EnumerateArray(), v => Assert.Equal(JsonValueKind.Number, v.ValueKind));
        Assert.Equal(new[] { 1, 2, 3 }, r.GetProperty("values").EnumerateArray().Select(v => v.GetInt32()));
        Assert.True(r.GetProperty("hasMore").GetBoolean());
    }

    [PostgresConnectorTests.PgFact]
    public async Task Bad_value_requests_get_a_message()
    {
        var id = await ConnectShopAsync();

        foreach (var (url, expected) in new (string, HttpStatusCode)[]
                 {
                     (ValuesUrl(id, "orders", "nope"), HttpStatusCode.BadRequest),
                     (ValuesUrl(id, "orders", "created_at"), HttpStatusCode.BadRequest),
                     (ValuesUrl(id, "orders", "status", limit: 0), HttpStatusCode.BadRequest),
                     (ValuesUrl(id, "orders", "status", limit: 5000), HttpStatusCode.BadRequest),
                     (ValuesUrl(id, "orders", "status", new string('x', 101)), HttpStatusCode.BadRequest),
                     (ValuesUrl(id, "secrets", "x"), HttpStatusCode.NotFound),
                     (ValuesUrl("nope", "orders", "status"), HttpStatusCode.NotFound),
                 })
        {
            var (code, body) = await Get(url);
            Assert.Equal(expected, code);
            Assert.False(string.IsNullOrWhiteSpace(Error(body)), url);
        }
    }
}
