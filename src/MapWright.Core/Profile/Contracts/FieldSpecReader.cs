using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Reads a field specification table (CSV, or rows from a spreadsheet) with one row per field. Only a path
/// column is required; type, required, format, length, range, scale, allowed values, description, sensitive and
/// repeats columns are recognised by common header names.
/// </summary>
public static partial class FieldSpecReader
{
    private enum Column
    {
        Path,
        Type,
        Required,
        Format,
        MinLength,
        MaxLength,
        Min,
        Max,
        Scale,
        AllowedValues,
        Description,
        Sensitive,
        Repeats,
    }

    private static readonly (Column Column, string[] Names)[] Headers =
    [
        (Column.Path, ["path", "fieldpath", "xpath", "jsonpath", "elementpath", "field", "element"]),
        (Column.Type, ["type", "datatype", "fieldtype"]),
        (Column.Required, ["required", "mandatory", "optionality", "requirement", "req", "mo", "usage"]),
        (Column.Format, ["format", "dateformat"]),
        (Column.MinLength, ["minlength", "minlen"]),
        (Column.MaxLength, ["maxlength", "maxlen", "length", "size", "fieldlength"]),
        (Column.Min, ["min", "minvalue", "minimum"]),
        (Column.Max, ["max", "maxvalue", "maximum"]),
        (Column.Scale, ["scale", "decimals", "decimalplaces"]),
        (Column.AllowedValues, ["allowedvalues", "validvalues", "values", "enum", "enumeration", "codes", "codevalues", "listofvalues", "lov", "picklist"]),
        (Column.Description, ["description", "definition", "businessdescription", "notes", "comments", "remarks"]),
        (Column.Sensitive, ["sensitive", "pii", "sensitivity", "classification", "dataclassification"]),
        (Column.Repeats, ["repeats", "repeating", "multiple", "cardinality", "occurs", "maxoccurs", "array", "list"]),
    ];

    public static ContractDocument ReadCsv(string name, string content) =>
        Read(name, Csv(content), ContractDocument.Hash(content));

    /// <summary>True when one of the first rows has a recognisable path column.</summary>
    public static bool HasHeader(IReadOnlyList<IReadOnlyList<string>> rows) => FindHeader(rows) is not null;

    public static ContractDocument Read(string name, IReadOnlyList<IReadOnlyList<string>> rows, string? sha256 = null) =>
        Read(name, [new FieldTable(rows)], sha256 ?? ContractDocument.Hash(string.Join("\n", rows.Select(r => string.Join("\t", r)))), InputKind.FieldSpec);

    /// <summary>
    /// Reads several field tables of one input, e.g. the tables of a specification document. Tables without a
    /// path column are skipped. A table's <see cref="FieldTable.Group"/> prefixes its plain field names.
    /// Documentation lists a field twice as a finding; a field spec as an error.
    /// </summary>
    public static ContractDocument Read(string name, IReadOnlyList<FieldTable> tables, string sha256, InputKind kind)
    {
        var entries = new List<(string Where, int Row, string Path, PayloadFormat Format, bool RepeatsFromPath, Func<Column, string?> Cell)>();
        var found = false;
        foreach (var table in tables)
        {
            var rows = table.Rows;
            if (FindHeader(rows) is not var (headerRow, columns))
            {
                continue;
            }

            found = true;
            var paths = rows.Skip(headerRow + 1)
                .Select(row => columns[Column.Path] < row.Count ? row[columns[Column.Path]].Trim() : "")
                .Where(p => p.Length > 0)
                .ToList();
            var group = table.Group is { } g && paths.All(IsPlainName) ? g : null;
            for (var r = headerRow + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                string? Cell(Column column) =>
                    columns.TryGetValue(column, out var index) && index < row.Count && row[index].Trim() is { Length: > 0 } text ? text : null;

                if (Cell(Column.Path) is not { } raw)
                {
                    continue;
                }

                if (group is not null)
                {
                    raw = $"{group}.{raw.Trim()}";
                }

                var (path, pathFormat, repeats) = Normalize(raw);
                entries.Add((table.Label is null ? $"Row {r + 1}" : $"{table.Label} row {r + 1}", r + 1, path, pathFormat, repeats, Cell));
            }
        }

        if (!found)
        {
            throw new ProfileException(tables.Count == 1
                ? $"Field spec '{name}' has no path column; expected a header such as {string.Join(", ", Headers[0].Names.Select(n => $"'{n}'"))}."
                : $"'{name}' has no field table; expected a header row with a column such as {string.Join(", ", Headers[0].Names.Select(n => $"'{n}'"))}.");
        }

        if (entries.Count == 0)
        {
            throw new ProfileException($"Field spec '{name}' lists no fields.");
        }

        if (entries.Select(e => e.Format).Distinct().Count() > 1)
        {
            throw new ProfileException($"Field spec '{name}' mixes JSON paths ($.a.b) and XML paths (/A/B).");
        }

        var duplicates = entries.GroupBy(e => e.Path, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        if (kind != InputKind.Documentation && duplicates.FirstOrDefault() is { } duplicate)
        {
            var where = duplicate.All(e => e.Where.StartsWith("Row ", StringComparison.Ordinal))
                ? $"rows {string.Join(", ", duplicate.Select(e => e.Row))}"
                : string.Join(", ", duplicate.Select(e => e.Where));
            throw new ProfileException($"Field spec '{name}' lists '{duplicate.Key}' more than once ({where}).");
        }

        var skipped = duplicates.SelectMany(g => g.Skip(1)).ToList();
        entries = [.. entries.Except(skipped)];
        var format = entries[0].Format;
        var builder = new ContractBuilder(name, kind, format);
        foreach (var extra in skipped)
        {
            builder.Finding(ProfileFindingKind.SchemaSimplified, extra.Path, $"{extra.Where}: listed again; the first definition was used.");
        }

        var parents = entries.Select(e => Parent(e.Path, format).Path).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var field = Ensure(builder, entry.Path, format);
            var cell = entry.Cell;
            var repeated = Repeats(cell(Column.Repeats), out var maxOccurs);
            if (entry.RepeatsFromPath || repeated)
            {
                field.Cardinality = Cardinality.Array;
                field.MaxOccurs = entry.RepeatsFromPath ? null : maxOccurs;
                field.Details[ProfileAttribute.Cardinality] = entry.RepeatsFromPath ? $"{entry.Where}: '[*]' in the path." : $"{entry.Where}: repeats '{cell(Column.Repeats)}'.";
            }
            else if (cell(Column.Repeats) is { } once)
            {
                field.Details[ProfileAttribute.Cardinality] = $"{entry.Where}: repeats '{once}'.";
            }

            Type(cell(Column.Type), field, parents.Contains(entry.Path), entry.Where, builder);
            if (cell(Column.Format) is { } dateFormat && field.DataType is FieldDataType.Date or FieldDataType.DateTime)
            {
                field.Format = NormalizeDateFormat(dateFormat);
                field.Details[ProfileAttribute.Format] = $"{entry.Where}: format '{dateFormat}'.";
            }

            if (cell(Column.Required) is { } required)
            {
                (field.Required, var note) = ParseRequired(required);
                field.Details[ProfileAttribute.Required] = $"{entry.Where}: '{required}'{note}.";
            }

            field.MinLength = Int(cell(Column.MinLength)) ?? field.MinLength;
            field.MaxLength = Int(cell(Column.MaxLength)) ?? field.MaxLength;
            field.MinValue = Decimal(cell(Column.Min));
            field.MaxValue = Decimal(cell(Column.Max));
            field.MaxScale = Int(cell(Column.Scale)) ?? field.MaxScale;
            if (cell(Column.AllowedValues) is { } values)
            {
                field.AllowedValues = Values(values);
                field.Details[ProfileAttribute.AllowedValues] = $"{entry.Where}: allowed values.";
            }

            field.Description = cell(Column.Description);
            if (cell(Column.Sensitive) is { } sensitive && IsYes(sensitive, "pii", "spi", "sensitive", "confidential", "restricted", "secret"))
            {
                field.SensitivityReason = $"Marked '{sensitive}' in the field spec.";
            }
        }

        return builder.Build(sha256, null);
    }

    private static (int Row, Dictionary<Column, int> Columns)? FindHeader(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        for (var r = 0; r < Math.Min(rows.Count, 10); r++)
        {
            var names = rows[r].Select(c => string.Concat(c.Where(char.IsLetterOrDigit)).ToLowerInvariant()).ToList();
            var columns = new Dictionary<Column, int>();
            foreach (var (column, aliases) in Headers)
            {
                var index = aliases.Select(a => names.IndexOf(a)).FirstOrDefault(i => i >= 0, -1);
                if (index >= 0 && !columns.ContainsValue(index))
                {
                    columns[column] = index;
                }
            }

            if (columns.ContainsKey(Column.Path))
            {
                return (r, columns);
            }
        }

        return null;
    }

    /// <summary>The profile path a field spec cell such as <c>owners[].name</c> or <c>/A/B</c> stands for.</summary>
    public static string PathOf(string raw) => Normalize(raw).Path;

    private static bool IsPlainName(string raw) => raw.Trim() is var text && text.IndexOfAny(['.', '/', '$', '[']) < 0;

    private static (string Path, PayloadFormat Format, bool Repeats) Normalize(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith('/') || (!text.StartsWith('$') && text.Contains('/')))
        {
            var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.StartsWith('@') ? "@" + XsdReader.Local(s[1..]) : XsdReader.Local(s));
            return ("/" + string.Join("/", segments), PayloadFormat.Xml, false);
        }

        var json = text.StartsWith('$') ? text : "$." + text;
        json = IndexPattern().Replace(json, "[*]");
        var repeats = json.EndsWith("[*]", StringComparison.Ordinal);
        return (repeats ? json[..^3] : json, PayloadFormat.Json, repeats);
    }

    private static (string? Path, string Name, bool ThroughArray) Parent(string path, PayloadFormat format)
    {
        if (format == PayloadFormat.Xml)
        {
            var slash = path.LastIndexOf('/');
            var name = path[(slash + 1)..].TrimStart('@');
            return (slash <= 0 ? null : path[..slash], name, false);
        }

        if (path == JsonPaths.Root)
        {
            return (null, JsonPaths.Root, false);
        }

        var tokens = JsonToken().Matches(path).ToList();
        var last = tokens.Count - 1;
        if (last < 0)
        {
            return (JsonPaths.Root, path, false);
        }

        var leaf = tokens[last];
        var leafName = leaf.Groups[1].Success ? leaf.Groups[1].Value : leaf.Groups[2].Value.Replace("\\'", "'", StringComparison.Ordinal);
        var parent = path[..leaf.Index];
        var throughArray = parent.EndsWith("[*]", StringComparison.Ordinal);
        if (throughArray)
        {
            parent = parent[..^3];
        }

        return (parent.Length == 0 ? JsonPaths.Root : parent, leafName, throughArray);
    }

    private static ContractField Ensure(ContractBuilder builder, string path, PayloadFormat format)
    {
        if (builder.Find(path) is { } existing)
        {
            return existing;
        }

        var (parentPath, name, throughArray) = Parent(path, format);
        ContractField? parent = null;
        if (parentPath is not null)
        {
            parent = Ensure(builder, parentPath, format);
            if (parent.Kind != FieldNodeKind.Object)
            {
                parent.MakeObject();
                parent.Details.TryAdd(ProfileAttribute.DataType, "Has child fields in the field spec.");
            }

            if (throughArray)
            {
                parent.Cardinality = Cardinality.Array;
                parent.Details[ProfileAttribute.Cardinality] = "'[*]' in child paths.";
            }
        }

        return builder.Add(path, name, parent);
    }

    private static void Type(string? text, ContractField field, bool hasChildren, string where, ContractBuilder builder)
    {
        if (hasChildren)
        {
            field.MakeObject();
            field.Details[ProfileAttribute.DataType] = "Has child fields in the field spec.";
            return;
        }

        if (text is null)
        {
            return;
        }

        var match = TypeText().Match(text.Trim().ToLowerInvariant());
        var word = match.Success ? match.Groups[1].Value.Trim() : text.Trim().ToLowerInvariant();
        var first = match.Success && match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;
        var second = match.Success && match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : (int?)null;

        (field.DataType, field.Format) = word switch
        {
            "string" or "text" or "varchar" or "varchar2" or "nvarchar" or "char" or "nchar" or "alphanumeric" or "an" or "a" or "str" or "character" => (FieldDataType.String, null),
            "int" or "integer" or "long" or "short" or "bigint" or "smallint" or "whole number" => (FieldDataType.Integer, null),
            "number" or "numeric" or "decimal" or "money" or "amount" or "currency" or "float" or "double" or "n" => (FieldDataType.Decimal, null),
            "bool" or "boolean" or "flag" or "y/n" or "yes/no" or "bit" => (FieldDataType.Boolean, null),
            "date" => (FieldDataType.Date, null),
            "datetime" or "date-time" or "date time" or "timestamp" => (FieldDataType.DateTime, "ISO 8601"),
            "object" or "group" or "complex" or "record" or "structure" => (FieldDataType.Object, null),
            _ => (FieldDataType.Unknown, (string?)null),
        };

        if (field.DataType == FieldDataType.Object)
        {
            field.MakeObject();
        }
        else if (field.DataType == FieldDataType.Unknown)
        {
            builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, $"{where}: type '{text}' is not recognised; the type is unknown.");
        }
        else if (first is not null)
        {
            if (field.DataType == FieldDataType.String)
            {
                field.MaxLength = first;
            }
            else if (field.DataType == FieldDataType.Decimal)
            {
                field.MaxScale = second ?? 0;
                if (second == 0 || (second is null && word is "numeric" or "number" or "decimal"))
                {
                    field.DataType = FieldDataType.Integer;
                    field.MaxScale = null;
                }
            }
        }

        field.Details[ProfileAttribute.DataType] = $"{where}: type '{text}'.";
    }

    private static (Requirement Value, string Note) ParseRequired(string text) => text.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "required" or "mandatory" or "m" or "r" or "req" or "1" or "x" => (Requirement.Required, ""),
        "c" or "conditional" or "cond" or "conditionally required" => (Requirement.Optional, " (conditional)"),
        "n" or "no" or "false" or "optional" or "o" or "opt" or "0" => (Requirement.Optional, ""),
        _ => (Requirement.Unknown, " (not recognised)"),
    };

    private static bool Repeats(string? text, out int? maxOccurs)
    {
        maxOccurs = null;
        if (text is null)
        {
            return false;
        }

        var value = text.Trim().ToLowerInvariant();
        if (int.TryParse(value, CultureInfo.InvariantCulture, out var count))
        {
            maxOccurs = count > 1 ? count : null;
            return count > 1;
        }

        if (RangeText().Match(value) is { Success: true } range)
        {
            if (int.TryParse(range.Groups[1].Value, CultureInfo.InvariantCulture, out var upper))
            {
                maxOccurs = upper > 1 ? upper : null;
                return upper > 1;
            }

            return true;
        }

        return IsYes(value, "array", "list", "repeating", "repeats", "multiple", "many", "*", "unbounded");
    }

    private static bool IsYes(string text, params string[] extra) =>
        text.Trim().ToLowerInvariant() is "y" or "yes" or "true" or "1" or "x" || extra.Contains(text.Trim().ToLowerInvariant());

    private static List<string> Values(string text)
    {
        var separators = text.Contains('\n') ? ['\n'] : text.Contains('|') ? ['|'] : text.Contains(';') ? [';'] : new[] { ',' };
        return [.. text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => CodeLabel().Match(v) is { Success: true } m ? m.Groups[1].Value.Trim() : v)
            .Where(v => v.Length > 0)];
    }

    private static string NormalizeDateFormat(string text)
    {
        var value = text.Trim();
        if (value.Contains("iso", StringComparison.OrdinalIgnoreCase))
        {
            return "ISO 8601";
        }

        return value.Replace("YYYY", "yyyy", StringComparison.Ordinal).Replace("DD", "dd", StringComparison.Ordinal);
    }

    private static int? Int(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static decimal? Decimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Parses CSV (comma, semicolon or tab separated, RFC 4180 quoting).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Csv(string content)
    {
        content = content.TrimStart('\uFEFF');
        var firstLine = content.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.MaxBy(d => firstLine.Count(c => c == d));

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < content.Length && content[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(v => v.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        row.Add(cell.ToString());
        if (row.Any(v => v.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }

    [GeneratedRegex(@"\[(\d+|)\]")]
    private static partial Regex IndexPattern();

    [GeneratedRegex(@"\.([A-Za-z_$][A-Za-z0-9_$]*)|\['((?:[^'\\]|\\.)*)'\]")]
    private static partial Regex JsonToken();

    [GeneratedRegex(@"^([a-z][a-z0-9 /_-]*?)\s*(?:\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\))?$")]
    private static partial Regex TypeText();

    [GeneratedRegex(@"^\d+\s*\.\.\s*(\d+|\*|n|unbounded)$")]
    private static partial Regex RangeText();

    [GeneratedRegex(@"^(.+?)\s*(?:=|:|\s-\s|\s–\s)")]
    private static partial Regex CodeLabel();
}

/// <summary>One table of field rows (header row first). <paramref name="Label"/> names it in messages, e.g. "Table 2".</summary>
public sealed record FieldTable(IReadOnlyList<IReadOnlyList<string>> Rows, string? Group = null, string? Label = null);
