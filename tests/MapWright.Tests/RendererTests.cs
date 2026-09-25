using System.Text;
using ClosedXML.Excel;
using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;

namespace MapWright.Tests;

public class RendererTests
{
    private static readonly MappingReport SampleReport = MappingReport.Build(TestSpecs.Sample());

    private static string RenderText(IMappingRenderer renderer, MappingReport report)
    {
        using var stream = new MemoryStream();
        renderer.Render(report, stream);
        return new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('\uFEFF');
    }

    [Fact]
    public void Mapping_sheet_includes_every_required_column()
    {
        string[] required =
        [
            "Source Field Name", "Source Field Path", "Source Datatype",
            "Target Field Name", "Target Field Path", "Target Datatype",
            "Confidence %", "Reasoning", "Transformation Rule",
        ];

        var headers = MappingSheet.Definitions.Select(c => c.Header).ToList();

        Assert.All(required, h => Assert.Contains(h, headers));
        Assert.Equal(headers.Count, headers.Distinct().Count());
    }

    [Fact]
    public void Csv_has_header_and_one_row_per_target_field()
    {
        var lines = RenderText(new CsvMappingRenderer(), SampleReport).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(1 + 18, lines.Length);
        Assert.StartsWith("Mapping ID,Mapping Type,Source Field Name,Source Field Path,Source Datatype", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("M006,Many:1,owners[].firstName | owners[].lastName,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "line1 | line2")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("@cmd", "'@cmd")]
    public void Csv_escaping(string input, string expected)
    {
        Assert.Equal(expected, CsvMappingRenderer.Escape(input));
    }

    [Fact]
    public void Excel_workbook_has_all_tabs_and_typed_cells()
    {
        using var stream = new MemoryStream();
        new ExcelMappingRenderer().Render(SampleReport, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);

        Assert.Equal(
            ["Summary", "Mapping", "Gaps", "Value Maps", "Conflicts & Assumptions", "Validation", "Change Log"],
            workbook.Worksheets.Select(w => w.Name));

        var sheet = workbook.Worksheet("Mapping");
        Assert.Equal("Source: SalesAlpha CRM", sheet.Cell(1, 3).GetString());
        Assert.Equal("Mapping ID", sheet.Cell(2, 1).GetString());
        Assert.Equal("M018", sheet.Cell(2 + 18, 1).GetString());
        Assert.True(sheet.Cell(3 + 18, 1).IsEmpty());

        var confidenceColumn = SampleReport.Mapping.IndexOf(MappingSheet.ConfidenceHeader) + 1;
        Assert.Equal(98d, sheet.Cell(3, confidenceColumn).GetDouble());

        Assert.Equal(5, workbook.Worksheet("Value Maps").RangeUsed()!.RowCount() - 1);
        Assert.Equal(4, workbook.Worksheet("Gaps").RangeUsed()!.RowCount() - 1);
    }

    [Fact]
    public void Gap_report_lists_required_targets_first_then_orphans()
    {
        var gapTypes = SampleReport.Gaps.Rows.Select(r => r.Cells[0]).ToList();

        Assert.Equal(
            ["Unmapped required target", "Unmapped optional target", "Orphan source field", "Orphan source field"],
            gapTypes);
    }

    [Fact]
    public void Html_contains_all_sections()
    {
        var html = RenderText(new HtmlMappingRenderer(), SampleReport);

        foreach (var id in new[] { "summary", "mapping", "gaps", "value-maps", "findings", "validation", "change-log" })
        {
            Assert.Contains($"<section id=\"{id}\">", html);
        }

        Assert.Contains("<th colspan=\"9\" class=\"g-target\">Target: UW Core</th>", html);
        Assert.Contains("class=\"band-Medium\"", html);
        Assert.Contains("class=\"outcome-Fail\"", html);
    }

    [Fact]
    public void Html_encodes_spec_content()
    {
        var mapping = TestSpecs.OneToOne("M1", "$.a", "/B/A") with { Reasoning = "<script>alert(1)</script>" };
        var report = MappingReport.Build(TestSpecs.Minimal(mapping) with { Title = "A & B" });

        var html = RenderText(new HtmlMappingRenderer(), report);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<title>A &amp; B</title>", html);
    }

    [Fact]
    public void Empty_sections_render_placeholder()
    {
        var html = RenderText(new HtmlMappingRenderer(), MappingReport.Build(TestSpecs.Minimal()));

        Assert.Contains("<section id=\"gaps\"><h2>Gaps</h2>\n<p class=\"empty\">None.</p>", html);
    }

    [Fact]
    public void Unmapped_rows_have_no_confidence_or_band()
    {
        var row = SampleReport.Mapping.Rows.Single(r => r.Cells[0] == "M017");

        Assert.Equal("", row.Cells[SampleReport.Mapping.IndexOf(MappingSheet.ConfidenceHeader)]);
        Assert.Equal("", row.Cells[SampleReport.Mapping.IndexOf(MappingSheet.BandHeader)]);
        Assert.Equal("unmapped", row.Tag(MappingSheet.BandTag));
    }

    [Fact]
    public void Renderer_lookup_is_case_insensitive()
    {
        Assert.IsType<ExcelMappingRenderer>(MappingRenderers.Find("XLSX"));
        Assert.IsType<PdfMappingRenderer>(MappingRenderers.Find("PDF"));
        Assert.Null(MappingRenderers.Find("docx"));
    }

    [Fact]
    public void Display_labels()
    {
        Assert.Equal("Many:1", Display.Of(MappingType.ManyToOne));
        Assert.Equal("Sensitive PII", Display.Of(Sensitivity.SensitivePii));
        Assert.Equal("Needs review", Display.Of(ReviewStatus.NeedsReview));
        Assert.Equal("Period Conversion", Display.Humanize(TransformationType.PeriodConversion));
        Assert.Equal("XSD", Display.Of(InputKind.Xsd));
    }
}
