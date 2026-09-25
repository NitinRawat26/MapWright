using System.Net;
using System.Text;
using MapWright.Output.Report;

namespace MapWright.Output.Renderers;

/// <summary>Self-contained, printable HTML report for leadership review and audit.</summary>
public sealed class HtmlMappingRenderer : IMappingRenderer
{
    public string Format => "html";
    public string FileExtension => ".html";

    private const string Styles = """
        :root { --ink:#1f2933; --muted:#616e7c; --line:#d9e2ec; --bg:#f5f7fa; }
        * { box-sizing:border-box; }
        body { margin:0; font:14px/1.45 "Segoe UI", Inter, Roboto, Arial, sans-serif; color:var(--ink); background:var(--bg); }
        header { background:#263238; color:#fff; padding:24px 32px; }
        header h1 { margin:0 0 4px; font-size:22px; }
        header p { margin:0; color:#cfd8dc; }
        main { padding:24px 32px; }
        section { background:#fff; border:1px solid var(--line); border-radius:8px; padding:16px 20px; margin-bottom:20px; }
        h2 { margin:0 0 12px; font-size:17px; }
        .cards { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:16px; }
        .card { border:1px solid var(--line); border-radius:8px; padding:10px 14px; min-width:150px; }
        .card b { display:block; font-size:20px; }
        .card span { color:var(--muted); font-size:12px; }
        dl { display:grid; grid-template-columns:260px 1fr; gap:6px 16px; margin:0; }
        dt { font-weight:600; }
        dd { margin:0; white-space:pre-line; }
        .scroll { overflow-x:auto; }
        table { border-collapse:collapse; width:max-content; min-width:100%; }
        th, td { border:1px solid var(--line); padding:6px 8px; vertical-align:top; text-align:left; white-space:pre-line; max-width:420px; }
        thead th { background:#37474f; color:#fff; position:sticky; top:0; }
        thead tr.groups th { text-align:center; }
        .g-identity { background:#37474f !important; } .g-source { background:#1565c0 !important; } .g-target { background:#2e7d32 !important; }
        .g-semantics { background:#6a1b9a !important; } .g-transformation { background:#ef6c00 !important; } .g-confidence { background:#00838f !important; }
        .g-risk { background:#c62828 !important; } .g-review { background:#4e342e !important; }
        .band-High { background:#c8e6c9; } .band-Medium { background:#ffe0b2; } .band-Low { background:#ffcdd2; } .band-unmapped { background:#e0e0e0; }
        .outcome-Pass { background:#c8e6c9; } .outcome-Fail { background:#ffcdd2; } .outcome-Skipped { background:#e0e0e0; }
        .empty { color:var(--muted); font-style:italic; }
        @media print { body { background:#fff; } .scroll { overflow:visible; } thead th { position:static; } }
        """;

    public void Render(MappingReport report, Stream output)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(RenderToString(report));
    }

    public static string RenderToString(MappingReport report)
    {
        var d = report.Document;
        var s = report.Summary;
        var html = new StringBuilder();

        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(E(d.Title)).Append("</title>\n<style>\n").Append(Styles).Append("\n</style>\n</head>\n<body>\n")
            .Append("<header><h1>").Append(E(d.Title)).Append("</h1><p>")
            .Append(E($"{d.Source.Name} v{d.Source.Version} → {d.Target.Name} v{d.Target.Version} · mapping {d.Id} v{d.Version}"))
            .Append("</p></header>\n<main>\n");

        html.Append("<section id=\"summary\"><h2>Summary</h2>\n<div class=\"cards\">");
        Card(html, Display.Percent(s.RequiredCoveragePercent), "Required target coverage");
        Card(html, $"{s.MappedTargetFields}/{s.TotalTargetFields}", "Target fields mapped");
        Card(html, $"{s.ByConfidenceBand[Core.Spec.ConfidenceBand.Low]}", "Low-confidence mappings");
        Card(html, $"{s.ByReviewStatus[Core.Spec.ReviewStatus.NeedsReview]}", "Awaiting review");
        Card(html, $"{s.UnmappedTargetFields + s.OrphanSourceFields}", "Gaps");
        Card(html, $"{s.ValidationFailed}", "Validation failures");
        html.Append("</div>\n<dl>");
        foreach (var (key, value) in report.SummaryItems)
        {
            html.Append("<dt>").Append(E(key)).Append("</dt><dd>").Append(E(value)).Append("</dd>");
        }

        html.Append("</dl></section>\n");

        AppendTable(html, "mapping", report.Mapping, row => $"band-{row.Tag(MappingSheet.BandTag)}",
            [report.Mapping.IndexOf(MappingSheet.ConfidenceHeader), report.Mapping.IndexOf(MappingSheet.BandHeader)]);
        AppendTable(html, "gaps", report.Gaps);
        AppendTable(html, "value-maps", report.ValueMaps);
        AppendTable(html, "findings", report.Findings);
        AppendTable(html, "validation", report.Validation, row => $"outcome-{row.Tag("outcome")}",
            [report.Validation.IndexOf("Outcome")]);
        AppendTable(html, "change-log", report.ChangeLog);

        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Card(StringBuilder html, string value, string label) =>
        html.Append("<div class=\"card\"><b>").Append(E(value)).Append("</b><span>").Append(E(label)).Append("</span></div>");

    private static void AppendTable(
        StringBuilder html, string id, ReportTable table, Func<ReportRow, string>? cellClass = null, int[]? classedColumns = null)
    {
        html.Append("<section id=\"").Append(id).Append("\"><h2>").Append(E(table.Title)).Append("</h2>\n");
        if (table.Rows.Count == 0)
        {
            html.Append("<p class=\"empty\">None.</p></section>\n");
            return;
        }

        html.Append("<div class=\"scroll\"><table>\n<thead>\n");
        if (table.Groups is { } groups)
        {
            html.Append("<tr class=\"groups\">");
            foreach (var group in groups)
            {
                html.Append("<th colspan=\"").Append(group.ColumnCount).Append("\" class=\"g-").Append(group.Key).Append("\">")
                    .Append(E(group.Title)).Append("</th>");
            }

            html.Append("</tr>\n");
        }

        html.Append("<tr>");
        foreach (var column in table.Columns)
        {
            html.Append("<th>").Append(E(column.Header)).Append("</th>");
        }

        html.Append("</tr>\n</thead>\n<tbody>\n");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            for (var c = 0; c < row.Cells.Count; c++)
            {
                html.Append(cellClass is not null && classedColumns is not null && classedColumns.Contains(c)
                    ? $"<td class=\"{E(cellClass(row))}\">"
                    : "<td>");
                html.Append(E(row.Cells[c])).Append("</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table></div></section>\n");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
