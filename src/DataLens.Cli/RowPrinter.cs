using System.Text;
using System.Text.Json;
using DataLens.Core;

namespace DataLens.Cli;

/// <summary>Plain-text table for a page of rows.</summary>
internal static class RowPrinter
{
    private const int MaxCellWidth = 28;

    public static string Render(ExplorerPage page)
    {
        var headers = page.Columns.Select(c => c.Name).ToArray();
        var cells = page.Rows.Select(r => r.Select(Format).ToArray()).ToList();

        var widths = headers.Select((h, i) =>
            Math.Min(MaxCellWidth, Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length)))).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => Fit(h, widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
            sb.AppendLine(string.Join("  ", row.Select((c, i) => Fit(c, widths[i]))));

        var first = (page.Page - 1) * page.PageSize + 1;
        sb.AppendLine();
        sb.Append(
            $"{page.Schema}.{page.Table}  rows {first}-{first + page.Rows.Count - 1}" +
            $"{(page.HasMore ? "  (more: --page " + (page.Page + 1) + ")" : "  (end)")}  {page.DurationMs} ms");
        return sb.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s.ReplaceLineEndings(" "),
        _ => JsonSerializer.Serialize(value)
    };

    private static string Fit(string s, int width) =>
        s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);
}
