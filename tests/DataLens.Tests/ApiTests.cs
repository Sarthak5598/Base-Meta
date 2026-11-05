using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DataLens.Tests;

/// <summary>Starts the real API in memory with its own empty data folder.</summary>
public sealed class ApiApp : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "datalens-api-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.UseSetting("DATALENS_HOME", _dir);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public class ApiTests : IDisposable
{
    private readonly ApiApp _app = new();
    private readonly HttpClient _http;

    public ApiTests() => _http = _app.CreateClient();

    public void Dispose()
    {
        _http.Dispose();
        _app.Dispose();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---- no database needed ------------------------------------------------------------------

    [Fact]
    public async Task Starts_with_no_connections()
    {
        var r = await _http.GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, (await JsonAsync(r)).GetArrayLength());
    }

    [Fact]
    public async Task Connecting_without_a_connection_string_is_a_400_with_a_message()
    {
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Paste a connection string.", (await JsonAsync(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_garbage_connection_string_is_a_400_not_a_crash()
    {
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "x", connectionString = "not a connection string" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(r)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Rows_of_an_unknown_connection_is_a_404()
    {
        var r = await _http.GetAsync("/api/connections/nope/rows?schema=public&table=orders");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("nope", (await JsonAsync(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_json_404_and_not_the_web_page()
    {
        var r = await _http.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
    }

    // ---- against a real database -------------------------------------------------------------

    private async Task<string> ConnectShopAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "shop", connectionString = PostgresConnectorTests.ReadOnly });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await JsonAsync(r)).GetProperty("connection").GetProperty("id").GetString()!;
    }

    [PostgresConnectorTests.PgFact]
    public async Task Connect_returns_a_summary_and_never_the_password()
    {
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "shop", connectionString = PostgresConnectorTests.ReadOnly });
        var text = await r.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(6, json.GetProperty("snapshot").GetProperty("tables").GetInt32());
        Assert.Equal(4, json.GetProperty("snapshot").GetProperty("foreignKeys").GetInt32());
        Assert.Equal(0, json.GetProperty("warnings").GetArrayLength());
        Assert.DoesNotContain("readonly", text);   // the password

        // nor does listing, or the schema
        var id = json.GetProperty("connection").GetProperty("id").GetString();
        Assert.DoesNotContain("readonly", await _http.GetStringAsync("/api/connections"));
        Assert.DoesNotContain("readonly", await _http.GetStringAsync($"/api/connections/{id}/schema"));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Connecting_as_a_user_who_can_write_succeeds_with_a_warning()
    {
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "admin", connectionString = PostgresConnectorTests.ReadWrite });
        var json = await JsonAsync(r);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(1, json.GetProperty("warnings").GetArrayLength());
        Assert.True(json.GetProperty("connection").GetProperty("canWrite").GetBoolean());
    }

    [PostgresConnectorTests.PgFact]
    public async Task A_wrong_password_is_a_400_with_a_readable_message()
    {
        var bad = PostgresConnectorTests.ReadOnly!.Replace("Password=readonly", "Password=nope");
        var r = await _http.PostAsJsonAsync("/api/connections", new { name = "shop", connectionString = bad });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Wrong username or password.", (await JsonAsync(r)).GetProperty("error").GetString());
        Assert.Equal(0, (await JsonAsync(await _http.GetAsync("/api/connections"))).GetArrayLength());   // nothing saved
    }

    [PostgresConnectorTests.PgFact]
    public async Task Schema_lists_tables_with_their_relationships()
    {
        var id = await ConnectShopAsync();
        var json = await JsonAsync(await _http.GetAsync($"/api/connections/{id}/schema"));

        var tables = json.GetProperty("snapshot").GetProperty("metadata").GetProperty("tables");
        var orders = tables.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "orders");
        Assert.Equal("users", orders.GetProperty("foreignKeys")[0].GetProperty("refTable").GetString());
        Assert.Equal("number", orders.GetProperty("columns")[0].GetProperty("kind").GetString());
        Assert.Equal("table", orders.GetProperty("kind").GetString());
    }

    [PostgresConnectorTests.PgFact]
    public async Task Rows_returns_a_page_with_typed_columns()
    {
        var id = await ConnectShopAsync();
        var r = await _http.GetAsync($"/api/connections/{id}/rows?schema=public&table=orders&page=2&pageSize=5");
        var json = await JsonAsync(r);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(5, json.GetProperty("rows").GetArrayLength());
        Assert.Equal(6, json.GetProperty("rows")[0][0].GetInt32());   // page 2 of 5 starts at id 6
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal("timestamp", json.GetProperty("columns")[3].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("rows")[0][3].ValueKind);   // dates as ISO strings
    }

    [PostgresConnectorTests.PgFact]
    public async Task Rows_can_be_sorted_descending_and_by_name_or_id()
    {
        var id = await ConnectShopAsync();
        var byName = await JsonAsync(await _http.GetAsync($"/api/connections/{id}/rows?schema=public&table=users&pageSize=3&sort=name&dir=desc"));
        var names = byName.GetProperty("rows").EnumerateArray().Select(r => r[1].GetString()).ToList();
        Assert.Equal(names.OrderByDescending(n => n, StringComparer.Ordinal), names, StringComparer.Ordinal);

        // by id (or name, from the connection list) both resolve
        var byId = await _http.GetAsync($"/api/connections/shop/rows?schema=public&table=users&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
    }

    [PostgresConnectorTests.PgTheory]
    [InlineData("schema=public&table=orders&page=0", HttpStatusCode.BadRequest)]
    [InlineData("schema=public&table=orders&pageSize=9999", HttpStatusCode.BadRequest)]
    [InlineData("schema=public&table=orders&dir=sideways", HttpStatusCode.BadRequest)]
    [InlineData("schema=public&table=orders&sort=nope", HttpStatusCode.BadRequest)]
    [InlineData("schema=public&table=orders&sort=id%3B%20DROP%20TABLE%20orders", HttpStatusCode.BadRequest)]
    [InlineData("schema=public&table=secrets", HttpStatusCode.NotFound)]
    [InlineData("schema=pg_catalog&table=pg_user", HttpStatusCode.NotFound)]
    [InlineData("schema=public&table=orders&page=100000&pageSize=200", HttpStatusCode.BadRequest)]
    public async Task Bad_requests_are_rejected_with_a_message(string query, HttpStatusCode expected)
    {
        var id = await ConnectShopAsync();
        var r = await _http.GetAsync($"/api/connections/{id}/rows?{query}");
        Assert.Equal(expected, r.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(r)).GetProperty("error").GetString()));
    }

    [PostgresConnectorTests.PgFact]
    public async Task Refresh_resyncs_and_bumps_the_version()
    {
        var id = await ConnectShopAsync();
        var r = await _http.PostAsync($"/api/connections/{id}/schema/refresh", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await JsonAsync(r)).GetProperty("version").GetInt32());
    }
}
