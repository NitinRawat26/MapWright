using MapWright.Output.Report;

namespace MapWright.Output.Renderers;

public interface IMappingRenderer
{
    /// <summary>Format key used on the command line, e.g. "xlsx".</summary>
    string Format { get; }
    string FileExtension { get; }
    void Render(MappingReport report, Stream output);
}

public static class MappingRenderers
{
    public static IReadOnlyList<IMappingRenderer> All { get; } =
        [new ExcelMappingRenderer(), new CsvMappingRenderer(), new HtmlMappingRenderer(), new PdfMappingRenderer()];

    public static IMappingRenderer? Find(string format) =>
        All.FirstOrDefault(r => string.Equals(r.Format, format, StringComparison.OrdinalIgnoreCase));
}
