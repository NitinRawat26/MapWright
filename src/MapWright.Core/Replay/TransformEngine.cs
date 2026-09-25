using System.Globalization;
using System.Text;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Spec;

namespace MapWright.Core.Replay;

/// <summary>A value produced for a target field. <see cref="Indexes"/> gives its position in each repeating list above it.</summary>
public sealed record TargetValue(string MappingId, string Path, IReadOnlyList<int> Indexes, string Value, string DataType);

public sealed record TransformResult
{
    public required string Sample { get; init; }
    public required IReadOnlyList<TargetValue> Values { get; init; }

    /// <summary>One result per mapping row: pass (value produced), fail (transformation error) or skipped (nothing to map).</summary>
    public required IReadOnlyList<ValidationResult> Rows { get; init; }
}

public sealed class TransformException(string message) : Exception(message);

/// <summary>
/// Runs a mapping spec over one source payload. Each row's transformation is executed exactly as written in the
/// spec: copies, type casts, value maps, conditions, expressions, concatenation and splits.
/// </summary>
public static class TransformEngine
{
    public const int DefaultScale = 4;

    public static TransformResult Run(MappingDocument mapping, SampleDocument payload, SystemProfile? target = null)
    {
        var source = SourceValues.Read(payload);
        var scales = target?.Fields.Where(f => f.MaxScale is not null).ToDictionary(f => f.Path, f => f.MaxScale!.Value, StringComparer.Ordinal)
            ?? [];
        var values = new List<TargetValue>();
        var rows = new List<ValidationResult>();

        foreach (var row in mapping.Mappings)
        {
            var scale = scales.TryGetValue(row.Target.Path, out var s) ? s : DefaultScale;
            rows.Add(RunRow(row, source, scale, values));
        }

        return new() { Sample = payload.Name, Values = values, Rows = rows };
    }

    private static ValidationResult RunRow(FieldMapping row, ILookup<string, SourceValue> source, int scale, List<TargetValue> values)
    {
        if (row.Type == MappingType.Unmapped)
        {
            return Result(row, ValidationOutcome.Skipped, "Unmapped: no source field.");
        }

        var produced = new List<TargetValue>();
        var notes = new List<string>();
        try
        {
            foreach (var (indexes, inputs) in Instances(row, source, notes))
            {
                if (Evaluate(row, inputs, scale, notes) is { } value)
                {
                    produced.Add(new(row.Id, row.Target.Path, indexes, value, row.Target.DataType));
                }
            }
        }
        catch (Exception ex) when (ex is TransformException or ExpressionException)
        {
            return Result(row, ValidationOutcome.Fail, ex.Message);
        }

        if (produced.Count == 0)
        {
            return Result(row, ValidationOutcome.Skipped, notes.Count > 0 ? string.Join(" ", notes) : "No source value in this sample.");
        }

        values.AddRange(produced);
        var message = $"{produced.Count} value(s) produced.{(notes.Count > 0 ? " " + string.Join(" ", notes.Distinct(StringComparer.Ordinal)) : "")}";
        return Result(row, ValidationOutcome.Pass, message, Shown(row, produced[0].Value));
    }

    /// <summary>
    /// Lines up the row's sources by list position: owners[0].firstName goes with owners[0].lastName. Single values
    /// are shared by every position. A repeating source into a single target keeps the first value.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<int> Indexes, IReadOnlyList<SourceValue?> Inputs)> Instances(
        FieldMapping row, ILookup<string, SourceValue> source, List<string> notes)
    {
        if (row.Type == MappingType.Constant)
        {
            yield return ([], []);
            yield break;
        }

        var found = row.Sources.Select(f => source[f.Path].ToList()).ToList();
        if (found.All(f => f.Count == 0))
        {
            yield break;
        }

        var keys = found.SelectMany(f => f).Where(v => v.Indexes.Count > 0).Select(v => Key(v.Indexes)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
        {
            yield return ([], [.. found.Select(f => f.FirstOrDefault())]);
            yield break;
        }

        if (row.Target.Cardinality == Cardinality.Single && keys.Count > 1)
        {
            notes.Add($"{keys.Count} source values for a single target field; kept the first.");
            keys = [keys[0]];
        }

        foreach (var key in keys)
        {
            var inputs = found.Select(f => f.FirstOrDefault(v => Key(v.Indexes) == key) ?? f.FirstOrDefault(v => v.Indexes.Count == 0)).ToList();
            var indexes = inputs.First(v => v is not null && Key(v.Indexes) == key)!.Indexes;
            yield return (row.Target.Cardinality == Cardinality.Single ? [] : indexes, inputs);
        }
    }

    private static string Key(IReadOnlyList<int> indexes) => string.Join(",", indexes);

    private static string? Evaluate(FieldMapping row, IReadOnlyList<SourceValue?> inputs, int scale, List<string> notes)
    {
        var t = row.Transformation;
        var first = inputs.FirstOrDefault()?.Value;

        switch (t.Type)
        {
            case TransformationType.Direct or TransformationType.Rename:
                return first;
            case TransformationType.EnumMap:
                return first is null ? null : Lookup(t, first) ?? throw new TransformException($"No value-map entry for '{Shown(row, first)}'.");
            case TransformationType.Conditional when t.Cases is { Count: > 0 } cases:
                return Decide(row, cases, inputs);
            case TransformationType.Conditional when inputs.Count == 1:
                return first is null ? null : Lookup(t, first) ?? t.DefaultValue ?? throw new TransformException($"No condition covers '{Shown(row, first)}'.");
            case TransformationType.Conditional:
                throw new TransformException("A condition over several fields needs transformation.cases to run.");
            case TransformationType.Concat:
                var parts = inputs.Select(i => i?.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                return parts.Count == 0 ? null : string.Join(" ", parts);
            case TransformationType.Split when t.Expression is null:
                return first is null ? null : SplitName(row, first);
            case TransformationType.TypeCast:
                return first is null ? null : Cast(row, first, notes);
            case TransformationType.Default or TransformationType.Lookup when t.Expression is null:
                return first ?? t.DefaultValue;
        }

        if (row.Type == MappingType.Constant && t.Expression is null)
        {
            return t.DefaultValue;
        }

        if (t.Expression is null)
        {
            throw new TransformException($"{JsonName(t.Type)} has no executable expression.");
        }

        var expr = Expressions.Parse(t.Expression);
        var names = expr.Variables().Distinct(StringComparer.Ordinal).ToList();
        var bound = new Dictionary<string, ExprValue>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var index = t.Inputs is not null && t.Inputs.TryGetValue(name, out var path)
                ? row.Sources.ToList().FindIndex(f => f.Path == path)
                : names.Count == 1 && inputs.Count == 1 ? 0 : -1;
            if (index < 0)
            {
                throw new TransformException($"Expression '{t.Expression}' uses '{name}', which is not bound to a source field.");
            }

            if (inputs[index]?.Value is not { } text)
            {
                return null;
            }

            bound[name] = ExprValue.Of(Number(row.Sources[index].Path, text));
        }

        var result = Expressions.Evaluate(expr, bound).Number ?? throw new TransformException($"Expression '{t.Expression}' did not give a number.");
        return FormatNumber(row, result, scale, notes);
    }

    /// <summary>The first case whose clauses all hold, else the default; no value when none of the sources has one.</summary>
    private static string? Decide(FieldMapping row, IReadOnlyList<ConditionalCase> cases, IReadOnlyList<SourceValue?> inputs)
    {
        if (inputs.All(i => i?.Value is null))
        {
            return null;
        }

        string? ValueOf(string path)
        {
            var index = row.Sources.ToList().FindIndex(f => f.Path == path);
            return index < 0
                ? throw new TransformException($"Condition uses '{path}', which is not one of the row's source fields.")
                : inputs[index]?.Value;
        }

        bool Holds(ConditionalClause clause) => ValueOf(clause.Source) is { } value
            && clause.In.Any(v => v == value || string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));

        return cases.FirstOrDefault(c => c.When.All(Holds))?.Then
            ?? row.Transformation.DefaultValue
            ?? throw new TransformException($"No condition covers {string.Join(", ", row.Sources.Select((s, i) => $"{s.Name} = '{(inputs[i]?.Value is { } v ? Shown(row, v) : "(none)")}'"))}.");
    }

    private static string? Lookup(Transformation t, string value) =>
        (t.ValueMap.FirstOrDefault(e => e.SourceValue == value)
            ?? t.ValueMap.FirstOrDefault(e => string.Equals(e.SourceValue, value.Trim(), StringComparison.OrdinalIgnoreCase)))?.TargetValue;

    /// <summary>Full name split at the last space: first name before it, last name after it.</summary>
    private static string SplitName(FieldMapping row, string full)
    {
        var text = full.Trim();
        var space = text.LastIndexOf(' ');
        var last = row.BusinessConcept?.EndsWith("LastName", StringComparison.OrdinalIgnoreCase) == true;
        return space < 0 ? (last ? "" : text) : last ? text[(space + 1)..] : text[..space];
    }

    private static string Cast(FieldMapping row, string value, List<string> notes)
    {
        var from = row.Sources[0];
        var to = row.Target;

        if (from.Format is { } sourceFormat && to.Format is { } targetFormat && sourceFormat != targetFormat && to.DataType is "date" or "dateTime")
        {
            return ReformatDate(value, sourceFormat, targetFormat);
        }

        if (row.Transformation.Pattern is { } pattern)
        {
            return Reshape(value, pattern);
        }

        return to.DataType switch
        {
            "integer" => FormatNumber(row, Number(from.Path, value), 0, notes),
            "decimal" => Number(from.Path, value).ToString(CultureInfo.InvariantCulture),
            "boolean" => bool.TryParse(value, out var b) ? (b ? "true" : "false") : throw new TransformException($"'{Shown(row, value)}' is not true or false."),
            _ => value,
        };
    }

    private static string ReformatDate(string value, string sourceFormat, string targetFormat)
    {
        const string iso = "ISO 8601";
        if (sourceFormat == iso)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                ? (targetFormat == iso ? dt.ToString("o", CultureInfo.InvariantCulture) : dt.ToString(targetFormat, CultureInfo.InvariantCulture))
                : throw new TransformException("Value is not an ISO 8601 date-time.");
        }

        if (!DateOnly.TryParseExact(value, sourceFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new TransformException($"Date does not match the source format {sourceFormat}.");
        }

        return targetFormat == iso ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString(targetFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Puts the value's digits into the target shape, e.g. 900-12-3456 into 999999999.</summary>
    private static string Reshape(string value, string pattern)
    {
        var digits = value.Where(char.IsAsciiDigit).ToList();
        if (digits.Count != pattern.Count(c => c == '9'))
        {
            throw new TransformException($"Value has {digits.Count} digit(s); target pattern {pattern} needs {pattern.Count(c => c == '9')}.");
        }

        var builder = new StringBuilder(pattern.Length);
        var next = 0;
        foreach (var c in pattern)
        {
            builder.Append(c == '9' ? digits[next++] : c);
        }

        return builder.ToString();
    }

    private static decimal Number(string path, string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new TransformException($"{path} is not a number.");

    private static string FormatNumber(FieldMapping row, decimal value, int scale, List<string> notes)
    {
        var digits = row.Target.DataType == "integer" ? 0 : scale;
        var rounded = Math.Round(value, digits, MidpointRounding.AwayFromZero);
        if (rounded != value)
        {
            notes.Add($"Rounded to {digits} decimal place(s).");
        }

        return rounded.ToString(digits == 0 ? "0" : "0.############################", CultureInfo.InvariantCulture);
    }

    internal static string Shown(FieldMapping row, string value) =>
        row.Risk.Sensitivity != Sensitivity.None || row.Sources.Concat([row.Target]).Any(f => SensitiveDataPolicy.Default.MatchField(f.Name, null) is not null)
            ? SensitiveDataPolicy.Mask(value)
            : value;

    private static ValidationResult Result(FieldMapping row, ValidationOutcome outcome, string message, string? actual = null) =>
        new() { MappingId = row.Id, Outcome = outcome, Actual = actual, Message = message };

    private static string JsonName<T>(T value) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
