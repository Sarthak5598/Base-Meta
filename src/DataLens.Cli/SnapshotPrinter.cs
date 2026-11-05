using System.Text;
using DataLens.Core.Models;

namespace DataLens.Cli;

/// <summary>Human-readable view of a snapshot, including how tables link to each other.</summary>
internal static class SnapshotPrinter
{
    public static string Render(SavedConnection connection, StoredSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{connection.Name}  snapshot v{snapshot.Version}  synced {snapshot.SyncedAt:u}");
        sb.AppendLine();

        foreach (var table in snapshot.Metadata.Tables)
        {
            sb.AppendLine($"{table.Schema}.{table.Name}  ({table.Kind.ToString().ToLowerInvariant()})");

            foreach (var column in table.Columns)
            {
                var flags = new List<string>();
                if (column.IsPrimaryKey) flags.Add("PK");
                if (!column.Nullable) flags.Add("not null");

                var fk = table.ForeignKeys.FirstOrDefault(f => f.Columns.Contains(column.Name));
                if (fk is not null)
                {
                    var i = fk.Columns.ToList().IndexOf(column.Name);
                    flags.Add($"-> {fk.RefSchema}.{fk.RefTable}.{fk.RefColumns[i]}");
                }

                sb.AppendLine(
                    $"  {column.Name,-18} {column.RawType,-28} {column.Kind.ToString().ToLowerInvariant(),-10} {string.Join(", ", flags)}");
            }

            foreach (var index in table.Indexes)
                sb.AppendLine($"  index {index.Name} ({string.Join(", ", index.Columns)}){(index.IsUnique ? " unique" : "")}");

            sb.AppendLine();
        }

        var links = snapshot.Metadata.Tables
            .SelectMany(t => t.ForeignKeys.Select(f => (t, f)))
            .ToList();
        sb.AppendLine($"Relationships ({links.Count}):");
        foreach (var (t, f) in links)
            sb.AppendLine(
                $"  {t.Schema}.{t.Name}({string.Join(", ", f.Columns)}) -> {f.RefSchema}.{f.RefTable}({string.Join(", ", f.RefColumns)})");

        return sb.ToString();
    }
}
