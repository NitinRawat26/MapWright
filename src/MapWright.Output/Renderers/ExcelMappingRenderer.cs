using System.Globalization;
using ClosedXML.Excel;
using MapWright.Core.Spec;
using MapWright.Output.Report;

namespace MapWright.Output.Renderers;

/// <summary>Sign-off workbook: Summary, Mapping, and one tab per supporting section.</summary>
public sealed class ExcelMappingRenderer : IMappingRenderer
{
    public string Format => "xlsx";
    public string FileExtension => ".xlsx";

    private static readonly Dictionary<string, XLColor> GroupColors = new()
    {
        ["identity"] = XLColor.FromHtml("#37474F"),
        ["source"] = XLColor.FromHtml("#1565C0"),
        ["target"] = XLColor.FromHtml("#2E7D32"),
        ["semantics"] = XLColor.FromHtml("#6A1B9A"),
        ["transformation"] = XLColor.FromHtml("#EF6C00"),
        ["confidence"] = XLColor.FromHtml("#00838F"),
        ["risk"] = XLColor.FromHtml("#C62828"),
        ["review"] = XLColor.FromHtml("#4E342E"),
    };

    private static readonly Dictionary<string, XLColor> BandColors = new()
    {
        [nameof(ConfidenceBand.High)] = XLColor.FromHtml("#C8E6C9"),
        [nameof(ConfidenceBand.Medium)] = XLColor.FromHtml("#FFE0B2"),
        [nameof(ConfidenceBand.Low)] = XLColor.FromHtml("#FFCDD2"),
        ["unmapped"] = XLColor.FromHtml("#E0E0E0"),
    };

    private static readonly Dictionary<string, XLColor> OriginColors = new()
    {
        [MappingSheet.AiOrigin] = XLColor.FromHtml("#E1BEE7"),
    };

    private static readonly Dictionary<string, XLColor> OutcomeColors = new()
    {
        [nameof(ValidationOutcome.Pass)] = XLColor.FromHtml("#C8E6C9"),
        [nameof(ValidationOutcome.Fail)] = XLColor.FromHtml("#FFCDD2"),
        [nameof(ValidationOutcome.Skipped)] = XLColor.FromHtml("#E0E0E0"),
    };

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#263238");

    public void Render(MappingReport report, Stream output)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = report.Document.Title;
        workbook.Properties.Subject = $"{report.Document.Source.Name} → {report.Document.Target.Name}";

        WriteSummary(workbook.AddWorksheet("Summary"), report);
        WriteMapping(workbook.AddWorksheet(report.Mapping.Title), report.Mapping);
        foreach (var table in report.SupportingTables)
        {
            var sheet = workbook.AddWorksheet(table.Title);
            WriteTable(sheet, table, firstRow: 1);
            if (table == report.Validation)
            {
                ColorByTag(sheet, table, firstDataRow: 2, "outcome", table.IndexOf("Outcome"), OutcomeColors);
            }
        }

        workbook.SaveAs(output);
    }

    private static void WriteSummary(IXLWorksheet sheet, MappingReport report)
    {
        sheet.Cell(1, 1).Value = report.Document.Title;
        sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        var row = 3;
        foreach (var (key, value) in report.SummaryItems)
        {
            sheet.Cell(row, 1).Value = key;
            sheet.Cell(row, 1).Style.Font.SetBold();
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 90;
        sheet.Column(2).Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
    }

    private static void WriteMapping(IXLWorksheet sheet, ReportTable table)
    {
        var column = 1;
        foreach (var group in table.Groups ?? [])
        {
            var range = sheet.Range(1, column, 1, column + group.ColumnCount - 1);
            range.Merge();
            range.Value = group.Title;
            range.Style.Font.SetBold().Font.SetFontColor(XLColor.White)
                .Fill.SetBackgroundColor(GroupColors.GetValueOrDefault(group.Key, HeaderFill))
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            column += group.ColumnCount;
        }

        WriteTable(sheet, table, firstRow: 2);
        sheet.SheetView.Freeze(2, 2);
        ColorByTag(sheet, table, firstDataRow: 3, MappingSheet.BandTag, table.IndexOf(MappingSheet.BandHeader), BandColors);
        ColorByTag(sheet, table, firstDataRow: 3, MappingSheet.BandTag, table.IndexOf(MappingSheet.ConfidenceHeader), BandColors);
        ColorByTag(sheet, table, firstDataRow: 3, MappingSheet.OriginTag, table.IndexOf(MappingSheet.OriginHeader), OriginColors);
    }

    private static void WriteTable(IXLWorksheet sheet, ReportTable table, int firstRow)
    {
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var header = sheet.Cell(firstRow, c + 1);
            header.Value = table.Columns[c].Header;
            header.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(HeaderFill)
                .Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            sheet.Column(c + 1).Width = table.Columns[c].Width;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = table.Rows[r].Cells;
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = sheet.Cell(firstRow + 1 + r, c + 1);
                if (table.Columns[c].Kind == CellKind.Number
                    && double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    cell.Value = number;
                }
                else
                {
                    cell.Value = cells[c];
                }

                cell.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            }
        }

        if (table.Rows.Count > 0)
        {
            sheet.Range(firstRow, 1, firstRow + table.Rows.Count, table.Columns.Count).SetAutoFilter();
        }

        if (firstRow == 1)
        {
            sheet.SheetView.FreezeRows(1);
        }
    }

    private static void ColorByTag(
        IXLWorksheet sheet, ReportTable table, int firstDataRow, string tag, int columnIndex, Dictionary<string, XLColor> colors)
    {
        if (columnIndex < 0)
        {
            return;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            if (table.Rows[r].Tag(tag) is { } value && colors.TryGetValue(value, out var color))
            {
                sheet.Cell(firstDataRow + r, columnIndex + 1).Style.Fill.SetBackgroundColor(color);
            }
        }
    }
}
