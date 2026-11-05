using System.Collections;
using System.Globalization;

namespace DataLens.Infrastructure.Postgres;

/// <summary>
/// Turns whatever Npgsql returns into plain values that serialise to JSON without surprises:
/// string, bool, number, null, or a list of those.
/// </summary>
public static class ValueNormalizer
{
    /// <summary>JavaScript numbers are exact up to 2^53 - 1. Larger integers are sent as strings.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>A single text cell can be huge. The explorer shows at most this many characters.</summary>
    public const int MaxTextLength = 10_000;

    private const int MaxBytesShown = 32;

    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string s => s.Length > MaxTextLength ? s[..MaxTextLength] + "…" : s,
        bool or int or short or byte or sbyte or decimal => value,
        // not Math.Abs: that throws for long.MinValue
        long l => l is > MaxSafeInteger or < -MaxSafeInteger ? l.ToString(CultureInfo.InvariantCulture) : l,
        float f => double.IsFinite(f) ? f : f.ToString(CultureInfo.InvariantCulture),
        double d => double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        byte[] bytes => Hex(bytes),
        IEnumerable items => items.Cast<object?>().Select(Normalize).ToList(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Hex(byte[] bytes)
    {
        var shown = bytes.Length > MaxBytesShown ? bytes[..MaxBytesShown] : bytes;
        return @"\x" + Convert.ToHexString(shown).ToLowerInvariant() + (bytes.Length > MaxBytesShown ? "…" : "");
    }
}
