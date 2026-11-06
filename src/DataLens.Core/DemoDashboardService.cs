using DataLens.Core.Models;
using DataLens.Core.Query;

namespace DataLens.Core;

/// <summary>
/// Builds a full sample dashboard on the sample "shop" database, or on the public AACT clinical trials
/// database, so there is something to look at straight away. Every card is an ordinary saved question: nothing special, and each can be edited.
/// Several of them combine three, and one five, tables through foreign keys.
/// </summary>
public sealed class DemoDashboardService(IAppStore store, IContentStore content)
{
    public const string DashboardName = "Shop overview";
    public const string TrialsDashboardName = "Clinical trials overview";

    private sealed record Spec(string Name, QueryDefinition Query, ChartConfig Chart);

    /// <returns>The dashboard, and whether it was just made. Asking again returns the one that exists.</returns>
    public async Task<(Dashboard Dashboard, bool Created)> CreateAsync(string connection, CancellationToken ct = default)
    {
        var (saved, snapshot) = await SnapshotLookup.LoadAsync(store, connection, ct);

        var (name, specs) = Plan(snapshot.Metadata);

        var existing = (await content.ListDashboardsAsync(ct)).FirstOrDefault(d => d.Name == name);
        if (existing is not null) return (existing, false);

        // every card must be valid before anything is saved, so a half-built dashboard cannot be left behind
        foreach (var spec in specs) QueryCompiler.Compile(spec.Query, snapshot.Metadata);

        var dashboard = await content.CreateDashboardAsync(name, ct);
        foreach (var spec in specs)
        {
            var query = await content.SaveQueryAsync(saved.Id, spec.Name, spec.Query, spec.Chart, ct);
            dashboard = await content.AddCardAsync(dashboard.Id, query.Id, ct) ?? dashboard;
        }
        return (dashboard, true);
    }

    /// <summary>Which sample dashboard fits this database: the sample shop, or the AACT clinical trials database.</summary>
    private static (string Name, List<Spec> Specs) Plan(DbMetadata meta)
    {
        if (meta.FindTable("public", "orders") is null && FindStudies(meta) is { } studies)
            return (TrialsDashboardName, TrialSpecs(meta, studies));
        return (DashboardName, Specs(meta));   // explains what is missing if it is neither
    }

    /// <summary>True when a sample dashboard can be built on this database.</summary>
    public static bool Supports(DbMetadata meta) =>
        FindStudies(meta) is not null ||
        new[] { "orders", "users", "order_items", "products", "categories", "order_totals", "legacy_events" }
            .All(t => meta.FindTable("public", t) is not null);

    // AACT keeps everything in the "ctgov" schema, with one row per study in "studies", keyed by nct_id
    private static TableMetadata? FindStudies(DbMetadata meta) =>
        meta.Tables.FirstOrDefault(t => t.Name == "studies" && t.Schema == "ctgov" && t.Columns.Any(c => c.Name == "nct_id"))
        ?? meta.Tables.FirstOrDefault(t => t.Name == "studies" && t.Columns.Any(c => c.Name == "nct_id"));

    /// <summary>
    /// Cards for the AACT clinical trials database. Written from AACT's documented layout, so each card is
    /// only built when the table and columns it needs are really there: a changed layout drops a card
    /// instead of failing the whole dashboard. AACT declares few foreign keys, so every card is one table.
    /// </summary>
    private static List<Spec> TrialSpecs(DbMetadata meta, TableMetadata studies)
    {
        var schema = studies.Schema;
        var specs = new List<Spec>();
        TableRef Tr(string t) => new(schema, t);
        bool Has(string table, string column) => meta.FindTable(schema, table)?.Columns.Any(c => c.Name == column) == true;
        ColumnRef C(string column) => new(column, null);
        string First(string table, params string[] columns) => columns.FirstOrDefault(c => Has(table, c)) ?? "";
        var desc = (string column) => new[] { new SortSpec(column, SortDirection.Desc) };
        var count = (string alias) => new Aggregation(AggFn.Count, null, alias);
        Filter[]? leadOnly(string table) => Has(table, "lead_or_collaborator")
            ? [new Filter(C("lead_or_collaborator"), FilterOp.Eq, new[] { System.Text.Json.JsonSerializer.SerializeToElement("lead") })]
            : null;

        void Add(bool possible, string name, QueryDefinition query, ChartConfig chart)
        {
            if (possible) specs.Add(new Spec(name, query, chart));
        }

        // one card, three figures
        var figures = new List<Aggregation> { count("Studies") };
        if (Has("studies", "enrollment"))
        {
            figures.Add(new Aggregation(AggFn.Sum, C("enrollment"), "Participants"));
            figures.Add(new Aggregation(AggFn.Avg, C("enrollment"), "Average enrollment"));
        }
        Add(true, "Overview", new QueryDefinition(Tr("studies"), Aggregations: figures), new ChartConfig(ChartType.Number));

        Add(Has("studies", "start_date"), "Studies started per year",
            new QueryDefinition(Tr("studies"), GroupBy: new[] { new Grouping(C("start_date"), DateBucket.Year) },
                Aggregations: new[] { count("Studies") }),
            new ChartConfig(ChartType.Line, "start_date", "Studies"));

        Add(Has("studies", "study_first_submitted_date"), "Studies registered over time",
            new QueryDefinition(Tr("studies"), GroupBy: new[] { new Grouping(C("study_first_submitted_date"), DateBucket.Month) },
                Aggregations: new[] { count("Studies") }),
            new ChartConfig(ChartType.Line, "study_first_submitted_date", "Studies"));

        Add(Has("studies", "overall_status"), "Studies by status",
            new QueryDefinition(Tr("studies"), GroupBy: new[] { new Grouping(C("overall_status")) },
                Aggregations: new[] { count("Studies") }, OrderBy: desc("Studies")),
            new ChartConfig(ChartType.Bar, "overall_status", "Studies"));

        Add(Has("studies", "study_type"), "Studies by type",
            new QueryDefinition(Tr("studies"), GroupBy: new[] { new Grouping(C("study_type")) },
                Aggregations: new[] { count("Studies") }),
            new ChartConfig(ChartType.Pie, "study_type", "Studies"));

        Add(Has("studies", "phase"), "Studies by phase",
            new QueryDefinition(Tr("studies"), GroupBy: new[] { new Grouping(C("phase")) },
                Aggregations: new[] { count("Studies") }, OrderBy: desc("Studies")),
            new ChartConfig(ChartType.Bar, "phase", "Studies"));

        Add(Has("sponsors", "name"), "Top sponsors",
            new QueryDefinition(Tr("sponsors"), Filters: leadOnly("sponsors"), GroupBy: new[] { new Grouping(C("name")) },
                Aggregations: new[] { count("Studies") }, OrderBy: desc("Studies"), Limit: 10),
            new ChartConfig(ChartType.Bar, "name", "Studies"));

        Add(Has("sponsors", "agent_class"), "Who sponsors trials",
            new QueryDefinition(Tr("sponsors"), Filters: leadOnly("sponsors"), GroupBy: new[] { new Grouping(C("agent_class")) },
                Aggregations: new[] { count("Studies") }),
            new ChartConfig(ChartType.Pie, "agent_class", "Studies"));

        var condition = First("conditions", "downcase_name", "name");
        Add(condition != "", "Top conditions",
            new QueryDefinition(Tr("conditions"), GroupBy: new[] { new Grouping(C(condition)) },
                Aggregations: new[] { count("Studies") }, OrderBy: desc("Studies"), Limit: 10),
            new ChartConfig(ChartType.Bar, condition, "Studies"));

        Add(Has("countries", "name"), "Top countries",
            new QueryDefinition(Tr("countries"), GroupBy: new[] { new Grouping(C("name")) },
                Aggregations: new[] { count("Studies") }, OrderBy: desc("Studies"), Limit: 10),
            new ChartConfig(ChartType.Bar, "name", "Studies"));

        Add(Has("interventions", "intervention_type"), "Intervention types",
            new QueryDefinition(Tr("interventions"), GroupBy: new[] { new Grouping(C("intervention_type")) },
                Aggregations: new[] { count("Interventions") }),
            new ChartConfig(ChartType.Pie, "intervention_type", "Interventions"));

        var listColumns = new[] { "nct_id", "brief_title", "overall_status", "phase", "enrollment", "start_date" }
            .Where(c => Has("studies", c)).Select(C).ToArray();
        Add(Has("studies", "enrollment"), "Biggest studies",
            new QueryDefinition(Tr("studies"), Columns: listColumns,
                Filters: new[] { new Filter(C("enrollment"), FilterOp.NotNull) },
                OrderBy: desc("enrollment"), Limit: 8),
            new ChartConfig(ChartType.Table));

        return specs;
    }

    private static List<Spec> Specs(DbMetadata meta)
    {
        TableMetadata Need(string name) =>
            meta.FindTable("public", name)
            ?? throw new DataLensException(DataLensError.BadRequest,
                $"This database does not look like the sample shop (there is no '{name}' table) or the AACT clinical trials " +
                "database, so a sample dashboard cannot be built on it. Create a dashboard and add your own cards instead.");

        // the name of the foreign key linking two tables, whichever of them holds it
        string Link(string a, string b) =>
            (Need(a).ForeignKeys.FirstOrDefault(f => f.RefSchema == "public" && f.RefTable == b)
             ?? Need(b).ForeignKeys.FirstOrDefault(f => f.RefSchema == "public" && f.RefTable == a))?.Name
            ?? throw new DataLensException(DataLensError.BadRequest, $"'{a}' and '{b}' are not linked by a foreign key.");

        JoinDef Join(string table, string from) => new(T(table), T(from), Link(table, from));

        foreach (var table in new[] { "orders", "users", "order_items", "products", "categories", "order_totals", "legacy_events" })
            Need(table);

        var count = (string alias) => new Aggregation(AggFn.Count, null, alias);
        var sum = (string table, string column, string alias) => new Aggregation(AggFn.Sum, Col(column, table), alias);
        var desc = (string column) => new[] { new SortSpec(column, SortDirection.Desc) };

        return
        [
            // one card, four figures: a single row with several numbers
            new("Overview",
                new QueryDefinition(T("order_totals"), Aggregations: new[]
                {
                    count("Orders"),
                    new Aggregation(AggFn.Sum, Col("total"), "Revenue"),
                    new Aggregation(AggFn.Avg, Col("total"), "Average order value"),
                    new Aggregation(AggFn.CountDistinct, Col("user_id"), "Customers")
                }),
                new ChartConfig(ChartType.Number)),

            new("Orders per month",
                new QueryDefinition(T("orders"), GroupBy: new[] { new Grouping(Col("created_at"), DateBucket.Month) },
                    Aggregations: new[] { count("Orders") }),
                new ChartConfig(ChartType.Line, "created_at", "Orders")),

            new("Revenue per month",
                new QueryDefinition(T("order_totals"), GroupBy: new[] { new Grouping(Col("created_at"), DateBucket.Month) },
                    Aggregations: new[] { new Aggregation(AggFn.Sum, Col("total"), "Revenue") }),
                new ChartConfig(ChartType.Line, "created_at", "Revenue")),

            new("Orders by status",
                new QueryDefinition(T("orders"), GroupBy: new[] { new Grouping(Col("status")) },
                    Aggregations: new[] { count("Orders") }),
                new ChartConfig(ChartType.Pie, "status", "Orders")),

            // two tables
            new("Orders by country",
                new QueryDefinition(T("orders"), Joins: new[] { Join("users", "orders") },
                    GroupBy: new[] { new Grouping(Col("country", "users")) }, Aggregations: new[] { count("Orders") },
                    OrderBy: desc("Orders")),
                new ChartConfig(ChartType.Bar, "users.country", "Orders")),

            // three tables: order lines, their products, and the product's category
            new("Units sold by category",
                new QueryDefinition(T("order_items"), Joins: new[] { Join("products", "order_items"), Join("categories", "products") },
                    GroupBy: new[] { new Grouping(Col("name", "categories")) },
                    Aggregations: new[] { sum("order_items", "quantity", "Units sold") }, OrderBy: desc("Units sold")),
                new ChartConfig(ChartType.Bar, "categories.name", "Units sold")),

            new("Top products",
                new QueryDefinition(T("order_items"), Joins: new[] { Join("products", "order_items") },
                    GroupBy: new[] { new Grouping(Col("name", "products")) },
                    Aggregations: new[] { sum("order_items", "quantity", "Units sold") }, OrderBy: desc("Units sold"), Limit: 10),
                new ChartConfig(ChartType.Bar, "products.name", "Units sold")),

            // five tables: coffee only, by the country of the customer who bought it
            new("Coffee units sold by country",
                new QueryDefinition(T("order_items"),
                    Joins: new[]
                    {
                        Join("orders", "order_items"), Join("users", "orders"),
                        Join("products", "order_items"), Join("categories", "products")
                    },
                    Filters: new[] { new Filter(Col("name", "categories"), FilterOp.Eq, new[] { System.Text.Json.JsonSerializer.SerializeToElement("Coffee") }) },
                    GroupBy: new[] { new Grouping(Col("country", "users")) },
                    Aggregations: new[] { sum("order_items", "quantity", "Units sold") }, OrderBy: desc("Units sold")),
                new ChartConfig(ChartType.Bar, "users.country", "Units sold")),

            new("New customers per month",
                new QueryDefinition(T("users"), GroupBy: new[] { new Grouping(Col("created_at"), DateBucket.Month) },
                    Aggregations: new[] { count("New customers") }),
                new ChartConfig(ChartType.Line, "created_at", "New customers")),

            new("Latest orders",
                new QueryDefinition(T("orders"), Joins: new[] { Join("users", "orders") },
                    Columns: new[] { Col("id"), Col("status"), Col("created_at"), Col("name", "users"), Col("country", "users") },
                    OrderBy: desc("orders.created_at"), Limit: 8),
                new ChartConfig(ChartType.Table)),

            new("Activity by type",
                new QueryDefinition(T("legacy_events"), GroupBy: new[] { new Grouping(Col("event")) },
                    Aggregations: new[] { count("Events") }),
                new ChartConfig(ChartType.Bar, "event", "Events")),

            new("Activity over time",
                new QueryDefinition(T("legacy_events"), GroupBy: new[] { new Grouping(Col("happened_at"), DateBucket.Week) },
                    Aggregations: new[] { count("Events") }),
                new ChartConfig(ChartType.Line, "happened_at", "Events"))
        ];
    }

    private static TableRef T(string table) => new("public", table);

    private static ColumnRef Col(string column, string? table = null) => new(column, table is null ? null : T(table));
}
