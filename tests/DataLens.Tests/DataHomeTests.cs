using System.Security.Cryptography;
using DataLens.Core;
using DataLens.Infrastructure.Security;
using DataLens.Infrastructure.Storage;

namespace DataLens.Tests;

public sealed class DataHomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "datalens-home-" + Guid.NewGuid().ToString("N"));

    public DataHomeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A pretend repository: DataLens.sln at the top, projects below it.</summary>
    private string MakeRepo()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src", "DataLens.Api"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "DataLens.Cli"));
        File.WriteAllText(Path.Combine(repo, "DataLens.sln"), "");
        return repo;
    }

    [Fact]
    public void The_web_app_and_the_command_line_agree_even_though_dotnet_run_starts_them_in_different_folders()
    {
        // `dotnet run --project src/DataLens.Api` starts in the project folder; the CLI starts where you typed it.
        // This was the bug: a connection saved in the web app was invisible to the CLI.
        var repo = MakeRepo();

        var fromWebApp = DataHome.Resolve(null, Path.Combine(repo, "src", "DataLens.Api"));
        var fromCli = DataHome.Resolve(null, repo);
        var fromDeepInside = DataHome.Resolve(null, Path.Combine(repo, "src", "DataLens.Cli"));

        Assert.Equal(Path.Combine(repo, ".datalens"), fromWebApp);
        Assert.Equal(fromWebApp, fromCli);
        Assert.Equal(fromWebApp, fromDeepInside);
    }

    [Fact]
    public void A_folder_named_in_the_environment_always_wins_and_is_made_absolute()
    {
        var repo = MakeRepo();
        var chosen = Path.Combine(_root, "elsewhere");

        Assert.Equal(chosen, DataHome.Resolve(chosen, repo));
        Assert.True(Path.IsPathRooted(DataHome.Resolve("relative/folder", repo)));
        Assert.Equal(Path.GetFullPath("relative/folder"), DataHome.Resolve("relative/folder", repo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_setting_is_the_same_as_no_setting(string? configured)
    {
        var repo = MakeRepo();
        Assert.Equal(Path.Combine(repo, ".datalens"), DataHome.Resolve(configured, repo));
    }

    [Fact]
    public void Without_a_repository_it_uses_the_folder_it_started_in()
    {
        // a container, for example: no DataLens.sln anywhere above
        var plain = Path.Combine(_root, "app");
        Directory.CreateDirectory(plain);

        Assert.Null(DataHome.FindRepositoryRoot(plain));
        Assert.Equal(Path.Combine(plain, ".datalens"), DataHome.Resolve(null, plain));
    }

    [Fact]
    public void The_nearest_solution_file_is_the_root()
    {
        var repo = MakeRepo();
        var nested = Path.Combine(repo, "src", "DataLens.Api", "deeper", "still");
        Directory.CreateDirectory(nested);
        Assert.Equal(repo, DataHome.FindRepositoryRoot(nested));
    }

    // ---- connections are found by name whatever the case, and a miss says what exists ----------------

    private SqliteAppStore NewStore() =>
        new(Path.Combine(_root, "store-" + Guid.NewGuid().ToString("N")[..6], "app.db"), new ConnectionProtector(RandomNumberGenerator.GetBytes(32)));

    private const string Cs = "Host=h;Database=d;Username=u;Password=p";

    [Fact]
    public async Task A_connection_is_found_by_name_ignoring_case_and_by_id()
    {
        var store = NewStore();
        var saved = await store.SaveConnectionAsync("Shop", Cs, false);

        Assert.Equal(saved.Id, (await store.FindConnectionAsync("Shop"))!.Id);
        Assert.Equal(saved.Id, (await store.FindConnectionAsync("shop"))!.Id);
        Assert.Equal(saved.Id, (await store.FindConnectionAsync("SHOP"))!.Id);
        Assert.Equal(saved.Id, (await store.FindConnectionAsync(saved.Id))!.Id);
        Assert.Null(await store.FindConnectionAsync("sho"));
    }

    [Fact]
    public async Task When_two_names_differ_only_by_case_the_exact_one_wins()
    {
        var store = NewStore();
        var lower = await store.SaveConnectionAsync("shop", Cs, false);
        var upper = await store.SaveConnectionAsync("Shop", Cs, false);

        Assert.NotEqual(lower.Id, upper.Id);
        Assert.Equal(lower.Id, (await store.FindConnectionAsync("shop"))!.Id);
        Assert.Equal(upper.Id, (await store.FindConnectionAsync("Shop"))!.Id);
    }

    [Fact]
    public async Task A_miss_lists_the_connections_that_do_exist()
    {
        var store = NewStore();
        await store.SaveConnectionAsync("Shop", Cs, false);
        await store.SaveConnectionAsync("Analytics", Cs, false);

        var ex = await ConnectionNotFound.CreateAsync(store, "nope");

        Assert.Equal(DataLensError.NotFound, ex.Error);
        Assert.Contains("'nope'", ex.Message);
        Assert.Contains("Analytics, Shop", ex.Message);   // listed by name
    }

    [Fact]
    public async Task A_miss_with_nothing_saved_says_so_which_points_at_the_wrong_folder()
    {
        var ex = await ConnectionNotFound.CreateAsync(NewStore(), "shop");
        Assert.Contains("no connections are saved here yet", ex.Message);
    }

    [Fact]
    public async Task The_services_use_that_message()
    {
        var store = NewStore();
        await store.SaveConnectionAsync("Shop", Cs, false);

        var ex = await Assert.ThrowsAsync<DataLensException>(() => new ConnectionService(store, new FakeConnector()).SyncAsync("shp"));
        Assert.Contains("Saved connections: Shop", ex.Message);
    }
}
