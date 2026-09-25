using System.Text;
using MapWright.Output.Report;

namespace MapWright.Output.Renderers;

/// <summary>Flat RFC 4180 export of the main mapping sheet (one row per target field).</summary>
public sealed class CsvMappingRenderer : IMappingRenderer
{
    public const string FlatSeparator = " | ";

    public string Format => "csv";
    public string FileExtension => ".csv";

    public void Render(MappingReport report, Stream output)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        writer.NewLine = "\r\n";

        var table = report.Mapping;
        writer.WriteLine(string.Join(",", table.Columns.Select(c => Escape(c.Header))));
        foreach (var row in table.Rows)
        {
            writer.WriteLine(string.Join(",", row.Cells.Select(Escape)));
        }
    }

    public static string Escape(string value)
    {
        var flat = value.Replace(Display.MultiValueSeparator, FlatSeparator);
        if (IsFormulaLike(flat))
        {
            flat = "'" + flat;
        }

        return flat.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + flat.Replace("\"", "\"\"") + "\""
            : flat;
    }

    /// <summary>Spreadsheet apps evaluate cells starting with these characters as formulas.</summary>
    private static bool IsFormulaLike(string value) =>
        value.Length > 0
        && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
        && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
}
