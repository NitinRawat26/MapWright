using System.Security.Cryptography;
using ClosedXML.Excel;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;

namespace MapWright.Output.Readers;

/// <summary>Reads a field spec from the first worksheet of an .xlsx workbook that has a path column.</summary>
public static class FieldSpecWorkbook
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
                    return FieldSpecReader.Read(name, rows, Convert.ToHexStringLower(SHA256.HashData(content)));
                }
            }
        }

        throw new ProfileException($"Field spec '{name}' has no worksheet with a path column.");
    }
}
