using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MapWright.Output.Readers;

/// <summary>A table found in a document, with the heading or caption written just before it.</summary>
public sealed record DocumentTable(string? Caption, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>What a PDF or Word specification contains: its tables and the text outside them.</summary>
public sealed record DocumentContent(string Name, string Sha256, IReadOnlyList<DocumentTable> Tables, string Text)
{
    /// <summary>The fields of the tables that have a path or field-name column, or null when none has.</summary>
    public ContractDocument? Contract()
    {
        var fieldTables = Tables.Where(t => FieldSpecReader.HasHeader(t.Rows)).ToList();
        if (fieldTables.Count == 0)
        {
            return null;
        }

        var labelled = fieldTables.Select((t, i) => new FieldTable(
            t.Rows,
            fieldTables.Count > 1 ? DocumentReader.Group(t.Caption) : null,
            $"Table {Tables.ToList().IndexOf(t) + 1}")).ToList();
        return FieldSpecReader.Read(Name, labelled, Sha256, InputKind.Documentation);
    }
}

/// <summary>
/// Reads PDF and Word (.docx) specifications. Tables are read by rules: a Word table as it is, a PDF table from
/// the positions of its words under a header row. Everything else is kept as text for an optional AI pass.
/// </summary>
public static partial class DocumentReader
{
    public static IReadOnlyList<string> Extensions { get; } = [".pdf", ".docx"];

    public static bool IsDocument(string name) => Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    public static DocumentContent Read(string name, byte[] content)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" => Pdf(name, content, sha),
            ".docx" => Word(name, content, sha),
            _ => throw new ProfileException($"'{name}' is not a PDF or Word (.docx) document."),
        };
    }

    /// <summary>
    /// A parent name from a table caption, e.g. "4.2 Merchant details" gives <c>merchantDetails</c>. Used only
    /// when a document has several field tables whose rows give plain field names.
    /// </summary>
    public static string? Group(string? caption)
    {
        if (caption is null)
        {
            return null;
        }

        var text = CaptionPrefix().Replace(caption.Trim(), "");
        var words = Words().Matches(text).Select(m => m.Value)
            .Where(w => !IgnoredCaptionWords.Contains(w.ToLowerInvariant()))
            .ToList();
        if (words.Count == 0 || words.Count > 4)
        {
            return null;
        }

        var name = string.Concat(words.Select((w, i) => i == 0
            ? char.ToLowerInvariant(w[0]) + w[1..]
            : char.ToUpperInvariant(w[0]) + w[1..]));
        return char.IsLetter(name[0]) ? name : null;
    }

    private static readonly HashSet<string> IgnoredCaptionWords = ["table", "fields", "field", "section", "object", "attributes", "elements", "the", "of", "request", "definitions", "definition"];

    private static DocumentContent Word(string name, byte[] content, string sha)
    {
        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(new MemoryStream(content), false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or InvalidOperationException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            throw new ProfileException($"'{name}' is not a readable Word (.docx) document: {ex.Message}", ex);
        }

        using (document)
        {
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new ProfileException($"'{name}' has no document body.");
            var tables = new List<DocumentTable>();
            var text = new StringBuilder();
            string? caption = null;
            foreach (var element in body.ChildElements)
            {
                if (element is Paragraph paragraph && paragraph.InnerText.Trim() is { Length: > 0 } line)
                {
                    text.AppendLine(line);
                    caption = line;
                }
                else if (element is Table table)
                {
                    IReadOnlyList<IReadOnlyList<string>> rows = [.. table.Elements<TableRow>()
                        .Select(r => (IReadOnlyList<string>)[.. r.Elements<TableCell>()
                            .Select(c => string.Join("\n", c.Elements<Paragraph>().Select(p => p.InnerText.Trim()).Where(t => t.Length > 0)))])
                        .Where(r => r.Any(c => c.Length > 0))];
                    tables.Add(new(caption, rows));
                    if (!FieldSpecReader.HasHeader(rows))
                    {
                        foreach (var row in rows)
                        {
                            text.AppendLine(string.Join(" | ", row));
                        }
                    }
                }
            }

            return new(name, sha, tables, text.ToString());
        }
    }

    private sealed record Cell(double Left, double Right, string Text);

    private sealed record Line(double Top, IReadOnlyList<Cell> Cells, IReadOnlyList<Cell> Words)
    {
        public string Text => string.Join(" ", Cells.Select(c => c.Text));
    }

    private static DocumentContent Pdf(string name, byte[] content, string sha)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(content);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ProfileException($"'{name}' is not a readable PDF: {ex.Message}", ex);
        }

        using (document)
        {
            var tables = new List<DocumentTable>();
            var text = new StringBuilder();
            string? caption = null;
            List<double>? columns = null;
            List<string[]>? rows = null;
            string[]? header = null;

            void Close()
            {
                if (rows is not null)
                {
                    tables.Add(new(caption, [.. rows.Select(r => (IReadOnlyList<string>)[.. r.Select(c => c.Trim())])]));
                }

                rows = null;
                columns = null;
                header = null;
            }

            foreach (var page in document.GetPages())
            {
                foreach (var line in Lines(page).Where(l => !PageNumber().IsMatch(l.Text)))
                {
                    var cells = line.Cells.Select(c => c.Text).ToList();
                    if (FieldSpecReader.HasHeader([cells]))
                    {
                        if (header is not null && cells.SequenceEqual(header))
                        {
                            continue;
                        }

                        Close();
                        columns = [.. line.Cells.Select(c => c.Left)];
                        header = [.. cells];
                        rows = [header];
                        continue;
                    }

                    if (columns is not null && rows is not null && Row(line, columns) is { } row)
                    {
                        if (row[0].Length == 0 && rows.Count > 1)
                        {
                            var previous = rows[^1];
                            for (var i = 0; i < row.Length; i++)
                            {
                                previous[i] = row[i].Length == 0 ? previous[i] : $"{previous[i]} {row[i]}".Trim();
                            }
                        }
                        else
                        {
                            rows.Add(row);
                        }

                        continue;
                    }

                    Close();
                    text.AppendLine(line.Text);
                    caption = line.Text;
                }
            }

            Close();
            return new(name, sha, tables, text.ToString());
        }
    }

    /// <summary>A line's cells placed under the header columns, or null when the line is not a table row.</summary>
    private static string[]? Row(Line line, List<double> columns)
    {
        const double Tolerance = 2;
        var row = new string[columns.Count];
        Array.Fill(row, "");
        foreach (var word in line.Words)
        {
            if (word.Left < columns[0] - Tolerance)
            {
                return null;
            }

            var index = columns.FindLastIndex(c => c <= word.Left + Tolerance);
            row[index] = $"{row[index]} {word.Text}".Trim();
        }

        var filled = row.Count(c => c.Length > 0);
        var heading = filled == 1 && row[0].Length > 0 && columns.Count > 1 && line.Cells[^1].Right > columns[1];
        return filled == 0 || heading || (filled == 1 && row[0].Length > 0 && row[0].Contains(' ')) ? null : row;
    }

    /// <summary>Words grouped into lines top to bottom, and into cells where the gap between words is wide.</summary>
    private static List<Line> Lines(Page page)
    {
        var lines = new List<List<Word>>();
        foreach (var word in page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var height = Math.Max(word.BoundingBox.Height, 1);
            var line = lines.LastOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= height * 0.5);
            if (line is null)
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        return [.. lines.Select(words =>
        {
            var ordered = words.OrderBy(w => w.BoundingBox.Left).ToList();
            var size = ordered.Max(w => w.Letters.Count > 0 ? w.Letters.Max(l => l.PointSize) : w.BoundingBox.Height);
            var gap = Math.Max(size * 0.6, 3.5);
            var cells = new List<Cell>();
            foreach (var word in ordered)
            {
                if (cells.Count > 0 && word.BoundingBox.Left - cells[^1].Right <= gap)
                {
                    cells[^1] = cells[^1] with { Right = word.BoundingBox.Right, Text = $"{cells[^1].Text} {word.Text}" };
                }
                else
                {
                    cells.Add(new(word.BoundingBox.Left, word.BoundingBox.Right, word.Text));
                }
            }

            return new Line(ordered.Max(w => w.BoundingBox.Top), cells, [.. ordered.Select(w => new Cell(w.BoundingBox.Left, w.BoundingBox.Right, w.Text))]);
        })];
    }

    [GeneratedRegex(@"^(?:(?:table|section|appendix)\s+)?[0-9A-Z]?(?:[0-9]+[.)]?)+(?:\s*[:.\-–]\s*|\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex CaptionPrefix();

    [GeneratedRegex(@"^(?:page\s+)?\d+(?:\s*(?:of|/)\s*\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex Words();
}
