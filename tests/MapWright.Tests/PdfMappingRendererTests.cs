using System.Text;
using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;
using UglyToad.PdfPig;

namespace MapWright.Tests;

public sealed class PdfMappingRendererTests
{
    private static (PdfDocument Pdf, List<string> Pages) Open(MappingDocument document)
    {
        var bytes = PdfMappingRenderer.RenderToBytes(MappingReport.Build(document));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var pdf = PdfDocument.Open(bytes);
        return (pdf, [.. pdf.GetPages().Select(p => p.Text)]);
    }

    [Fact]
    public void Pdf_has_the_summary_every_mapping_row_and_the_supporting_tables()
    {
        var document = TestSpecs.Sample();
        var (pdf, pages) = Open(document);
        using var _ = pdf;
        var text = string.Concat(pages);

        Assert.Equal(PdfMappingRenderer.Clean(document.Title), pdf.Information.Title);
        Assert.StartsWith(PdfMappingRenderer.Clean(document.Title), pages[0]);
        Assert.Contains("Summary", pages[0]);
        Assert.Contains("Required Target Coverage", text);
        Assert.All(document.Mappings, m => Assert.Contains(m.Id, text));
        Assert.Contains("/UnderwritingRequest/Merchant/LegalName", text);
        Assert.All(new[] { "Gaps", "Value Maps", "Conflicts & Assumptions", "Validation", "Change Log" }, t => Assert.Contains(t, text));
        Assert.Contains($"page {pages.Count} of {pages.Count}", pages[^1]);
    }

    [Fact]
    public void Long_tables_continue_on_new_pages_with_their_header()
    {
        var document = TestSpecs.Sample();
        var rows = Enumerable.Range(1, 120).Select(i => document.Mappings[0] with { Id = $"R{i:D3}" }).ToList();
        var (pdf, pages) = Open(document with { Mappings = rows });
        using var _ = pdf;

        var continued = pages.Where(p => p.Contains("(continued)")).ToList();
        Assert.True(continued.Count >= 2);
        Assert.All(continued, p => Assert.Contains("Target Field Path", p));
        Assert.All(rows, r => Assert.Single(pages, p => p.Contains(r.Id)));
    }

    [Fact]
    public void Characters_the_pdf_fonts_lack_are_replaced()
    {
        var (pdf, pages) = Open(TestSpecs.Sample() with { Title = "Alpha → Beta — “Café” 日本" });
        using var _ = pdf;

        Assert.StartsWith("Alpha -> Beta - \"Cafe\" ??", pages[0]);
        Assert.Equal("a -> b ... >= ? Uber Strasse AEro", PdfMappingRenderer.Clean("a → b … ≥ \u0001日 Über Straße Æro"));
    }
}
