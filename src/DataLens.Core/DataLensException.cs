namespace DataLens.Core;

public enum DataLensError
{
    /// <summary>The thing asked for does not exist (connection, snapshot, table).</summary>
    NotFound,

    /// <summary>The request itself is wrong (bad page, unknown sort column, empty name).</summary>
    BadRequest
}

/// <summary>An expected failure with a message that is safe to show to the user.</summary>
public sealed class DataLensException(DataLensError error, string message) : Exception(message)
{
    public DataLensError Error { get; } = error;
}
