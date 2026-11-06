namespace DataLens.Infrastructure.Storage;

/// <summary>
/// Where DataLens keeps its own data (the app database and the encryption key). The web app and the command
/// line must agree on this, or a connection saved in one is invisible to the other.
///
/// <c>dotnet run --project X</c> starts a web project in X's own folder but a console project where you typed
/// the command, so "the current folder" is not a safe answer. Instead: the folder named in DATALENS_HOME, or
/// otherwise a <c>.datalens</c> folder at the repository root (found by walking up to <c>DataLens.sln</c>),
/// or beside the program when there is no repository, as in a container.
/// </summary>
public static class DataHome
{
    public const string FolderName = ".datalens";

    public static string Resolve(string? configured, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(FindRepositoryRoot(startDirectory) ?? Path.GetFullPath(startDirectory), FolderName);
    }

    public static string? FindRepositoryRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DataLens.sln"))) return dir.FullName;
        return null;
    }
}
