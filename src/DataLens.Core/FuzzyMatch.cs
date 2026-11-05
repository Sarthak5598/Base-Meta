using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataLens.Core;

/// <summary>
/// Ranks values against what someone has typed so far, forgiving typos: "shiped" finds "shipped".
/// Pure and in memory, so it works on any database. (Postgres has pg_trgm for this, but it is an extension
/// that a read-only user cannot install.)
/// </summary>
public static class FuzzyMatch
{
    /// <param name="Matches">Contain what was typed: exact first, then starting with it, then a word starting with it, then anywhere.</param>
    /// <param name="Close">Not a match, but a typo or two away from one: "did you mean".</param>
    public sealed record Result(IReadOnlyList<string> Matches, IReadOnlyList<string> Close);

    /// <summary>Longer than this and a value is only matched as plain text, never by edit distance.</summary>
    public const int MaxFuzzyLength = 200;

    public static Result Rank(IEnumerable<string> candidates, string? query)
    {
        var q = Normalize(query ?? "");
        if (q.Length == 0)
            return new Result(candidates.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(), []);

        var edits = MaxEdits(q.Length);
        var matches = new List<(string Value, double Score)>();
        var close = new List<(string Value, int Distance, int LengthGap)>();

        foreach (var value in candidates)
        {
            var n = Normalize(value);

            if (Tier(n, q) is { } score)
            {
                matches.Add((value, score));
                continue;
            }

            if (edits > 0 && n.Length <= MaxFuzzyLength && Closest(n, q) is { } d && d <= edits)
                close.Add((value, d, Math.Abs(n.Length - q.Length)));
        }

        return new Result(
            matches.OrderByDescending(m => m.Score).ThenBy(m => m.Value, StringComparer.OrdinalIgnoreCase).Select(m => m.Value).ToList(),
            close.OrderBy(c => c.Distance).ThenBy(c => c.LengthGap).ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase).Select(c => c.Value).ToList());
    }

    /// <summary>How many typos to forgive. None for one or two letters, which would match nearly anything.</summary>
    public static int MaxEdits(int queryLength) => queryLength switch
    {
        <= 2 => 0,
        <= 5 => 1,
        <= 9 => 2,
        _ => 3
    };

    // Higher is better. Shorter values rank above longer ones within a tier: "paid" before "paid in full".
    private static double? Tier(string value, string q)
    {
        if (value == q) return 1000;
        if (value.StartsWith(q, StringComparison.Ordinal)) return 900 - (value.Length - q.Length) * 0.5;
        if (Tokens(value).Any(t => t.StartsWith(q, StringComparison.Ordinal))) return 800 - value.Length * 0.1;
        var at = value.IndexOf(q, StringComparison.Ordinal);
        return at >= 0 ? 700 - at * 0.1 : null;
    }

    /// <summary>
    /// The fewest typos between the query and the value, a word of it, or the start of either. The start of a
    /// word is included so that a half-typed word with a typo still finds it.
    /// </summary>
    private static int? Closest(string value, string q)
    {
        var best = int.MaxValue;
        foreach (var part in Tokens(value).Prepend(value).Distinct())
        {
            best = Math.Min(best, Distance(q, part));

            // The start of the word, one letter either side: a letter missing or doubled while typing
            // ("refnd" for "refund...") makes what was typed shorter or longer than the part it aims at.
            for (var length = Math.Max(1, q.Length - 1); length <= q.Length + 1 && length < part.Length; length++)
                best = Math.Min(best, Distance(q, part[..length]));
        }
        return best < q.Length ? best : null;   // all-typo is not a match
    }

    /// <summary>Optimal string alignment distance: insert, delete, replace, or swap two neighbours, each counting one.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    /// <summary>Lower case, no accents, single spaces: "  Café  Noir" and "cafe noir" are the same.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastSpace = true;   // also trims the start
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            lastSpace = false;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static readonly Regex NotWordChars = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private static IEnumerable<string> Tokens(string s) =>
        NotWordChars.Split(s).Where(t => t.Length > 0);
}
