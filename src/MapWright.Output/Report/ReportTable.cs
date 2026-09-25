namespace MapWright.Output.Report;

public enum CellKind
{
    Text,
    Number,
}

public sealed record ReportColumn(string Header, CellKind Kind = CellKind.Text, double Width = 18);

/// <summary>A contiguous run of columns sharing a group heading (e.g. "Source: SalesAlpha").</summary>
public sealed record ColumnGroup(string Title, int ColumnCount, string Key);

public sealed record ReportRow(IReadOnlyList<string> Cells, IReadOnlyDictionary<string, string>? Tags = null)
{
    public string? Tag(string key) => Tags is not null && Tags.TryGetValue(key, out var value) ? value : null;
}

public sealed record ReportTable(
    string Title,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows,
    IReadOnlyList<ColumnGroup>? Groups = null)
{
    public int IndexOf(string header)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Header == header)
            {
                return i;
            }
        }

        return -1;
    }
}
