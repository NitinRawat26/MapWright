using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;

namespace MapWright.Output.Readers;

/// <summary>
/// Reads a field spec or data dictionary from the worksheets of an .xlsx workbook that have a path column. With
/// several such sheets, a sheet's name (other than Sheet1, Sheet2...) is the parent of its plain field names.
/// </summary>
public static partial class FieldSpecWorkbook
{
    public static ContractDocument Read(string name, byte[] content)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(new MemoryStream(content));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new ProfileException($"Field spec '{name}' is not a readable .xlsx workbook: {ex.Message}", ex);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var sheets = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>();
        using (workbook)
        {
            foreach (var sheet in workbook.Worksheets)
            {
                if (sheet.RangeUsed() is not { } range)
                {
                    continue;
                }

                IReadOnlyList<IReadOnlyList<string>> rows = [.. range.Rows().Select(r => (IReadOnlyList<string>)[.. r.Cells().Select(c => c.GetFormattedString())])];
                if (FieldSpecReader.HasHeader(rows))
                {
                    sheets.Add((sheet.Name, rows));
                }
            }
        }

        if (sheets.Count == 1)
        {
            return FieldSpecReader.Read(name, sheets[0].Rows, sha);
        }

        if (sheets.Count > 1)
        {
            return FieldSpecReader.Read(
                name,
                [.. sheets.Select(s => new FieldTable(s.Rows, DefaultSheetName().IsMatch(s.Name) ? null : FieldSpecReader.Identifier(s.Name), $"Sheet '{s.Name}'"))],
                sha,
                InputKind.FieldSpec);
        }

        throw new ProfileException($"Field spec '{name}' has no worksheet with a path column.");
    }

    [GeneratedRegex(@"^(sheet|tabelle|feuil|hoja|foglio)\s*\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultSheetName();
}
