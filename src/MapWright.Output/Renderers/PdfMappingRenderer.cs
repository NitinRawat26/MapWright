using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MapWright.Output.Report;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MapWright.Output.Renderers;

/// <summary>Printable PDF of the mapping report (landscape A4): summary, the main mapping columns and every supporting table.</summary>
public sealed partial class PdfMappingRenderer : IMappingRenderer
{
    public string Format => "pdf";
    public string FileExtension => ".pdf";

    /// <summary>Mapping columns shown in the PDF. The full sheet does not fit a page; the Excel workbook has every column.</summary>
    public static IReadOnlyList<string> MappingColumns { get; } =
    [
        "Mapping ID", "Mapping Type", MappingSheet.OriginHeader, "Source Field Path", "Target Field Path", "Target Required", "Transformation Type",
        "Transformation Rule", MappingSheet.ConfidenceHeader, MappingSheet.BandHeader, "PII / Sensitivity", MappingSheet.StatusHeader,
    ];

    public void Render(MappingReport report, Stream output) => output.Write(RenderToBytes(report));

    public static byte[] RenderToBytes(MappingReport report) => new Writer(report).Write();

    /// <summary>
    /// The built-in PDF fonts are written with their standard encoding, which matches ASCII only: accented letters lose
    /// their accents (é → e), common symbols get an ASCII stand-in and anything else becomes '?'.
    /// </summary>
    public static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            clean.Append(c switch
            {
                '→' => "->",
                '←' => "<-",
                '—' or '–' or '‑' => "-",
                '…' => "...",
                '≥' => ">=",
                '≤' => "<=",
                '‘' or '’' => "'",
                '“' or '”' => "\"",
                '•' => "*",
                '×' => "x",
                '÷' => "/",
                'ß' => "ss",
                'Æ' => "AE",
                'æ' => "ae",
                'Œ' => "OE",
                'œ' => "oe",
                'Ø' => "O",
                'ø' => "o",
                '·' => "-",
                '\u00a0' => " ",
                '\t' => " ",
                '\n' => "\n",
                _ when c < ' ' => "",
                _ when c > '~' => Unaccented(c),
                _ => c.ToString(),
            });
        }

        return clean.ToString();
    }

    /// <summary>Places a long word may wrap: after a path separator, dot, dash or underscore.</summary>
    [GeneratedRegex(@"(?<=[/.\-_\]])")]
    private static partial Regex SoftBreak();

    private static string Unaccented(char c)
    {
        var bases = c.ToString().Normalize(NormalizationForm.FormD)
            .Where(b => b is >= ' ' and <= '~' && CharUnicodeInfo.GetUnicodeCategory(b) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        return bases.Length > 0 ? new string(bases) : "?";
    }

    private sealed class Writer(MappingReport report)
    {
        private const double PageWidth = 842;
        private const double PageHeight = 595;
        private const double Margin = 36;
        private const double Bottom = Margin + 14;
        private const double FontSize = 7.5;
        private const double LineHeight = 9.5;
        private const double Padding = 3;
        private const int MaxLinesPerCell = 30;

        private static readonly (byte R, byte G, byte B) Ink = (0x1f, 0x29, 0x33);
        private static readonly (byte R, byte G, byte B) HeaderFill = (0x37, 0x47, 0x4f);

        private static readonly Dictionary<string, (byte R, byte G, byte B)> Fills = new(StringComparer.OrdinalIgnoreCase)
        {
            ["High"] = (0xc8, 0xe6, 0xc9),
            ["Medium"] = (0xff, 0xe0, 0xb2),
            ["Low"] = (0xff, 0xcd, 0xd2),
            ["unmapped"] = (0xe0, 0xe0, 0xe0),
            [MappingSheet.AiOrigin] = (0xe1, 0xbe, 0xe7),
            ["Pass"] = (0xc8, 0xe6, 0xc9),
            ["Fail"] = (0xff, 0xcd, 0xd2),
            ["Skipped"] = (0xe0, 0xe0, 0xe0),
        };

        private readonly PdfDocumentBuilder _builder = new();
        private readonly List<PdfPageBuilder> _pages = [];
        private readonly Dictionary<(char, bool), double> _widths = [];
        private readonly HashSet<char> _unsupported = [];
        private PdfDocumentBuilder.AddedFont _regular = null!;
        private PdfDocumentBuilder.AddedFont _bold = null!;
        private PdfPageBuilder _page = null!;
        private double _y;

        public byte[] Write()
        {
            var d = report.Document;
            _builder.DocumentInformation.Title = Clean(d.Title);
            _builder.DocumentInformation.Producer = "MapWright";
            _regular = _builder.AddStandard14Font(Standard14Font.Helvetica);
            _bold = _builder.AddStandard14Font(Standard14Font.HelveticaBold);
            NewPage();
            for (var c = ' '; c <= '~'; c++)
            {
                try
                {
                    CharWidth(c, false);
                    CharWidth(c, true);
                }
                catch (InvalidOperationException)
                {
                    _unsupported.Add(c);
                }
            }

            foreach (var line in Wrap(d.Title, PageWidth - (2 * Margin), true, 16))
            {
                Text(line, Margin, _y - 16, 16, _bold);
                _y -= 20;
            }

            Text($"{d.Source.Name} v{d.Source.Version} -> {d.Target.Name} v{d.Target.Version} · mapping {d.Id} v{d.Version}", Margin, _y - 10, 9, _regular);
            _y -= 22;

            Table(new ReportTable(
                "Summary",
                [new("Item", Width: 22), new("Value", Width: 78)],
                [.. report.SummaryItems.Select(i => new ReportRow([i.Key, i.Value]))]));

            var mapping = report.Mapping;
            var columns = MappingColumns.Select(mapping.IndexOf).Where(i => i >= 0).ToList();
            var main = new ReportTable(
                $"{mapping.Title} (main columns; the Excel workbook has all {mapping.Columns.Count})",
                [.. columns.Select(i => mapping.Columns[i])],
                [.. mapping.Rows.Select(r => new ReportRow([.. columns.Select(i => r.Cells[i])], r.Tags))]);
            var confidence = main.IndexOf(MappingSheet.ConfidenceHeader);
            var band = main.IndexOf(MappingSheet.BandHeader);
            var origin = main.IndexOf(MappingSheet.OriginHeader);
            Table(main, (row, column) =>
                column == confidence || column == band ? row.Tag(MappingSheet.BandTag)
                : column == origin ? row.Tag(MappingSheet.OriginTag)
                : null);

            foreach (var table in report.SupportingTables)
            {
                var outcome = table.IndexOf("Outcome");
                Table(table, (row, column) => column == outcome ? row.Tag("outcome") : null);
            }

            for (var i = 0; i < _pages.Count; i++)
            {
                _pages[i].SetTextAndFillColor(0x61, 0x6e, 0x7c);
                _pages[i].AddText(Safe($"{d.Title} · {d.Id} v{d.Version} · page {i + 1} of {_pages.Count}"), 7, new PdfPoint(Margin, Margin - 14), _regular);
            }

            return _builder.Build();
        }

        private void NewPage()
        {
            _page = _builder.AddPage(PageWidth, PageHeight);
            _page.SetStrokeColor(0xd9, 0xe2, 0xec);
            _page.SetTextAndFillColor(Ink.R, Ink.G, Ink.B);
            _pages.Add(_page);
            _y = PageHeight - Margin;
        }

        private void Heading(string title, double size = 12)
        {
            if (_y - 40 < Bottom)
            {
                NewPage();
            }

            Text(title, Margin, _y - size - 4, size, _bold);
            _y -= size + 12;
        }

        private void Table(ReportTable table, Func<ReportRow, int, string?>? fill = null)
        {
            Heading(table.Title);
            if (table.Rows.Count == 0)
            {
                Text("None.", Margin, _y - FontSize, FontSize, _regular);
                _y -= 18;
                return;
            }

            var widths = Widths(table);
            HeaderRow(table, widths);

            foreach (var row in table.Rows)
            {
                var lines = row.Cells.Select((cell, c) => Wrap(cell, widths[c] - (2 * Padding), false, FontSize)).ToList();
                var height = (Math.Max(1, lines.Max(l => l.Count)) * LineHeight) + (2 * Padding);
                if (_y - height < Bottom)
                {
                    NewPage();
                    Heading($"{table.Title} (continued)", 9);
                    HeaderRow(table, widths);
                }

                var x = Margin;
                for (var c = 0; c < lines.Count; c++)
                {
                    Cell(x, widths[c], height, lines[c], fill?.Invoke(row, c) is { } key && Fills.TryGetValue(key, out var color) ? color : null, _regular);
                    x += widths[c];
                }

                _y -= height;
            }

            _y -= 14;
        }

        /// <summary>Shares the page width by the columns' widths, but never splits a header word.</summary>
        private List<double> Widths(ReportTable table)
        {
            var available = PageWidth - (2 * Margin);
            var total = table.Columns.Sum(c => c.Width);
            var widths = table.Columns.Select(c => c.Width / total * available).ToList();
            var minimum = table.Columns.Select(c => c.Header.Split(' ').Max(w => Measure(w, true, FontSize)) + (2 * Padding) + 1).ToList();
            var narrow = Enumerable.Range(0, widths.Count).Where(i => widths[i] < minimum[i]).ToHashSet();
            var rest = Enumerable.Range(0, widths.Count).Where(i => !narrow.Contains(i)).Sum(i => widths[i]);
            var scale = rest > 0 ? (available - narrow.Sum(i => minimum[i])) / rest : 1;
            return [.. widths.Select((w, i) => narrow.Contains(i) ? minimum[i] : w * scale)];
        }

        private void HeaderRow(ReportTable table, List<double> widths)
        {
            var lines = table.Columns.Select((column, c) => Wrap(column.Header, widths[c] - (2 * Padding), true, FontSize)).ToList();
            var height = (lines.Max(l => l.Count) * LineHeight) + (2 * Padding);
            var x = Margin;
            for (var c = 0; c < lines.Count; c++)
            {
                Cell(x, widths[c], height, lines[c], HeaderFill, _bold, white: true);
                x += widths[c];
            }

            _y -= height;
        }

        private void Cell(double x, double width, double height, List<string> lines, (byte R, byte G, byte B)? background, PdfDocumentBuilder.AddedFont font, bool white = false)
        {
            var corner = new PdfPoint(x, _y - height);
            if (background is { } b)
            {
                _page.SetTextAndFillColor(b.R, b.G, b.B);
                _page.DrawRectangle(corner, width, height, 0.5, true);
            }

            _page.DrawRectangle(corner, width, height, 0.5, false);
            if (white)
            {
                _page.SetTextAndFillColor(0xff, 0xff, 0xff);
            }
            else
            {
                _page.SetTextAndFillColor(Ink.R, Ink.G, Ink.B);
            }

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > 0)
                {
                    Text(lines[i], x + Padding, _y - Padding - ((i + 1) * LineHeight) + 2, FontSize, font);
                }
            }

            _page.SetTextAndFillColor(Ink.R, Ink.G, Ink.B);
        }

        private void Text(string text, double x, double y, double size, PdfDocumentBuilder.AddedFont font) =>
            _page.AddText(Safe(text), size, new PdfPoint(x, y), font);

        private string Safe(string text) =>
            string.Concat(Clean(text).Select(c => c != '\n' && _unsupported.Contains(c) ? '?' : c));

        private List<string> Wrap(string text, double width, bool bold, double size)
        {
            var lines = new List<string>();
            foreach (var paragraph in Safe(text).Split('\n'))
            {
                var line = new StringBuilder();
                foreach (var word in paragraph.Split(' '))
                {
                    var separator = line.Length == 0 ? "" : " ";
                    foreach (var piece in word.Length > 0 && Measure(word, bold, size) > width ? SoftBreak().Split(word) : [word])
                    {
                        if (Measure($"{line}{separator}{piece}", bold, size) <= width)
                        {
                            line.Append(separator).Append(piece);
                        }
                        else
                        {
                            if (line.Length > 0)
                            {
                                lines.Add(line.ToString());
                                line.Clear();
                            }

                            foreach (var c in piece)
                            {
                                if (line.Length > 0 && Measure(line.ToString() + c, bold, size) > width)
                                {
                                    lines.Add(line.ToString());
                                    line.Clear();
                                }

                                line.Append(c);
                            }
                        }

                        separator = "";
                    }
                }

                lines.Add(line.ToString());
            }

            if (lines.Count > MaxLinesPerCell)
            {
                lines = [.. lines.Take(MaxLinesPerCell - 1), "... (see the Excel workbook)"];
            }

            return lines;
        }

        private double Measure(string text, bool bold, double size) => text.Sum(c => CharWidth(c, bold)) * size;

        private double CharWidth(char c, bool bold)
        {
            if (!_widths.TryGetValue((c, bold), out var width))
            {
                var letters = _page.MeasureText(c.ToString(), 1, new PdfPoint(0, 0), bold ? _bold : _regular);
                width = letters.Count == 0 ? 0.5 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
                _widths[(c, bold)] = width;
            }

            return width;
        }
    }
}
