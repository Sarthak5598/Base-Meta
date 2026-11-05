using Npgsql;

namespace DataLens.Infrastructure.Postgres;

/// <summary>
/// Accepts the two forms people paste: Npgsql key/value ("Host=...;Username=...") and the
/// "postgresql://user:pass@host:5432/db?sslmode=require" URI. Always returns key/value.
/// </summary>
public static class ConnectionStringParser
{
    private const int DefaultConnectTimeoutSeconds = 5;

    /// <summary>For connecting: the string as given, plus a short connect timeout if none was set.</summary>
    public static NpgsqlConnectionStringBuilder Parse(string input)
    {
        var builder = Normalize(input);

        // Fail fast on unreachable hosts instead of waiting for Npgsql's 15s default.
        // ContainsKey is true for every known setting, ShouldSerialize only when it was set explicitly.
        if (!builder.ShouldSerialize("Timeout"))
            builder.Timeout = DefaultConnectTimeoutSeconds;

        return builder;
    }

    /// <summary>
    /// Reads and validates the string, accepting either form, and adds nothing of ours. This is what gets
    /// stored and shown, so our defaults can change later without touching saved connections.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("The connection string is empty.");

        input = input.Trim();
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = IsUri(input) ? FromUri(input) : new NpgsqlConnectionStringBuilder(input);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Could not read the connection string: {ex.Message}", ex);
        }
        catch (UriFormatException ex)
        {
            throw new FormatException($"Could not read the connection URI: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(builder.Host))
            throw new FormatException("The connection string has no host.");
        if (string.IsNullOrEmpty(builder.Database))
            throw new FormatException("The connection string has no database.");

        return builder;
    }

    /// <summary>Key/value form with the password replaced, safe to show in a UI or log.</summary>
    public static string Mask(string connectionString)
    {
        var builder = Normalize(connectionString);
        if (!string.IsNullOrEmpty(builder.Password))
            builder.Password = "****";
        return builder.ConnectionString;
    }

    private static bool IsUri(string s) =>
        s.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase);

    private static NpgsqlConnectionStringBuilder FromUri(string s)
    {
        var uri = new Uri(s);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(
                separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
            if (separator >= 0)
                builder.Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                builder.SslMode = Enum.Parse<SslMode>(value.Replace("-", ""), ignoreCase: true);
        }

        return builder;
    }
}
