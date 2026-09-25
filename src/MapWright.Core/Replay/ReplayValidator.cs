using System.Globalization;
using MapWright.Core.Matching;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Replay;

/// <summary>
/// Turns one replayed sample into a <see cref="ValidationRun"/>: each row's transformation outcome, the produced
/// values checked against the target profile (required, type, format, allowed values, digit-code shape), and the
/// domain playbooks' validation rules evaluated over the produced values. Rules whose inputs the target system has
/// no field for are left out.
/// </summary>
public static class ReplayValidator
{
    public static ValidationRun Validate(
        MappingDocument mapping, TransformResult result, SystemProfile target, PlaybookLibrary playbooks, string id, DateTimeOffset ranAt)
    {
        var fields = target.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var produced = result.Values.ToLookup(v => v.MappingId, StringComparer.Ordinal);
        var rows = result.Rows.ToDictionary(r => r.MappingId, StringComparer.Ordinal);
        var results = new List<ValidationResult>();

        foreach (var row in mapping.Mappings)
        {
            var field = fields.GetValueOrDefault(row.Target.Path);
            var values = produced[row.Id].ToList();
            var outcome = rows.GetValueOrDefault(row.Id);

            if (values.Count == 0 && outcome is not { Outcome: ValidationOutcome.Fail } && field?.Required is Requirement.Required or Requirement.LikelyRequired)
            {
                var why = field.Required == Requirement.Required ? "required" : "likely required (present in every target sample)";
                results.Add(new()
                {
                    MappingId = row.Id,
                    Outcome = ValidationOutcome.Fail,
                    Expected = "a value",
                    Message = $"{outcome?.Message ?? "No value produced."} The target field is {why}.",
                });
                continue;
            }

            if (field is not null && values.Count > 0 && outcome is not { Outcome: ValidationOutcome.Fail })
            {
                var check = CheckProfile(row, field, values);
                results.Add(outcome is null ? check : check with { Message = $"{outcome.Message} {check.Message}" });
            }
            else if (outcome is not null)
            {
                results.Add(outcome);
            }
        }

        results.AddRange(CheckRules(mapping, result, target, playbooks));
        return new() { Id = id, RanAt = ranAt, SamplePayload = result.Sample, Results = results };
    }

    /// <summary>The next free run id (V001, V002, ...), skipping <paramref name="offset"/> runs not yet added.</summary>
    public static string NextRunId(MappingDocument mapping, int offset = 0) => $"V{mapping.ValidationRuns.Count + offset + 1:000}";

    private static ValidationResult CheckProfile(FieldMapping row, ProfileField field, IReadOnlyList<TargetValue> values)
    {
        var expected = Expected(field);
        var problems = values
            .Select(v => (Value: v.Value, Problem: Problem(field, v.Value)))
            .Where(p => p.Problem is not null)
            .ToList();

        if (problems.Count == 0)
        {
            return new()
            {
                MappingId = row.Id,
                Outcome = ValidationOutcome.Pass,
                Expected = expected,
                Actual = TransformEngine.Shown(row, values[0].Value),
                Message = $"Matches the target profile ({values.Count} value(s)).",
            };
        }

        return new()
        {
            MappingId = row.Id,
            Outcome = ValidationOutcome.Fail,
            Expected = expected,
            Actual = TransformEngine.Shown(row, problems[0].Value),
            Message = $"{problems.Count} of {values.Count} value(s) do not match the target profile: {string.Join("; ", problems.Select(p => p.Problem).Distinct(StringComparer.Ordinal))}.",
        };
    }

    private static string Expected(ProfileField field)
    {
        var parts = new List<string> { JsonName(field.DataType) };
        if (field.Format is { } format)
        {
            parts.Add(format);
        }

        if (field.AllowedValues.Count > 0)
        {
            parts.Add($"one of {string.Join(", ", field.AllowedValues)}");
        }
        else if (DigitShapes(field) is { Count: > 0 } shapes)
        {
            parts.Add($"shape {string.Join(" or ", shapes)}");
        }

        return string.Join(", ", parts);
    }

    private static string? Problem(ProfileField field, string value)
    {
        var typeOk = field.DataType switch
        {
            FieldDataType.Integer => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var i) && i == decimal.Truncate(i),
            FieldDataType.Decimal => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            FieldDataType.Boolean => bool.TryParse(value, out _),
            FieldDataType.Date => field.Format is null or "ISO 8601"
                ? DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _)
                : DateOnly.TryParseExact(value, field.Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldDataType.DateTime => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
            _ => true,
        };
        if (!typeOk)
        {
            return $"not a valid {JsonName(field.DataType)}{(field.Format is { } f ? $" ({f})" : "")}";
        }

        if (field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(value, StringComparer.Ordinal))
        {
            return "not an allowed value";
        }

        if (DigitShapes(field) is { Count: > 0 } shapes && !shapes.Contains(ValueShape(value), StringComparer.Ordinal))
        {
            return $"shape {ValueShape(value)} is not {string.Join(" or ", shapes)}";
        }

        return null;
    }

    /// <summary>Shapes of digit codes such as SSNs and routing numbers; other shapes vary too much to check.</summary>
    private static List<string> DigitShapes(ProfileField field) =>
        field.DataType == FieldDataType.String && field.ValueShapes.Count > 0
            && field.ValueShapes.All(s => s.Contains('9') && s.All(c => c == '9' || !char.IsLetterOrDigit(c)))
            ? [.. field.ValueShapes]
            : [];

    private static string ValueShape(string value) =>
        new([.. value.Select(c => char.IsAsciiDigit(c) ? '9' : char.IsUpper(c) ? 'A' : char.IsLetter(c) ? 'a' : c)]);

    private static IEnumerable<ValidationResult> CheckRules(MappingDocument mapping, TransformResult result, SystemProfile target, PlaybookLibrary playbooks)
    {
        var recognised = RecognisedField.From(target, playbooks);
        var values = result.Values.ToLookup(v => v.Path, StringComparer.Ordinal);
        var rowFor = mapping.Mappings.GroupBy(m => m.Target.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var playbook in playbooks.Domains)
        {
            foreach (var rule in playbook.Domain!.Validations)
            {
                var bound = rule.Inputs
                    .Select(input => (Input: input, Field: recognised.FirstOrDefault(f => f.Detection is { } d
                        && d.Playbook == playbook.Reference
                        && string.Equals(d.Attribute, input.Attribute, StringComparison.OrdinalIgnoreCase)
                        && input.Qualifiers.All(q => d.Qualifiers.TryGetValue(q.Key, out var v) && v == q.Value))))
                    .ToList();
                if (bound.Any(b => b.Field is null || !rowFor.ContainsKey(b.Field.Path)))
                {
                    continue;
                }

                var mappingId = rowFor[bound[0].Field!.Path].Id;
                var label = $"{rule.Id}: {rule.Description}{(rule.Severity == IssueSeverity.Warning ? " (warning)" : "")}";
                var missing = bound.Where(b => !values[b.Field!.Path].Any()).Select(b => b.Input.Name).ToList();
                if (missing.Count > 0)
                {
                    yield return new()
                    {
                        MappingId = mappingId,
                        Outcome = ValidationOutcome.Skipped,
                        Expected = rule.Expression,
                        Message = $"{label} Not checked: no target value for {string.Join(", ", missing)}.",
                    };
                    continue;
                }

                yield return Evaluate(rule, bound.Select(b => (b.Input.Name, b.Field!, values[b.Field!.Path].ToList())).ToList(), mappingId, label);
            }
        }
    }

    private static ValidationResult Evaluate(
        ValidationRuleDefinition rule, IReadOnlyList<(string Name, RecognisedField Field, List<TargetValue> Values)> inputs, string mappingId, string label)
    {
        var bound = new Dictionary<string, ExprValue>(StringComparer.Ordinal);
        var shown = new List<string>();
        foreach (var (name, field, values) in inputs)
        {
            var numbers = new List<decimal>();
            foreach (var value in values)
            {
                if (!decimal.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    return new() { MappingId = mappingId, Outcome = ValidationOutcome.Fail, Expected = rule.Expression, Message = $"{label} {field.Path} is not a number." };
                }

                numbers.Add(n);
            }

            bound[name] = field.Repeats ? ExprValue.Of(numbers) : ExprValue.Of(numbers[0]);
            shown.Add($"{name}={string.Join("|", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)))}");
        }

        try
        {
            var passed = Expressions.Evaluate(Expressions.Parse(rule.Expression), bound).Bool
                ?? throw new ExpressionException($"'{rule.Expression}' is not a true/false check.");
            return new()
            {
                MappingId = mappingId,
                Outcome = passed ? ValidationOutcome.Pass : ValidationOutcome.Fail,
                Expected = rule.Expression,
                Actual = string.Join(", ", shown),
                Message = label,
            };
        }
        catch (ExpressionException ex)
        {
            return new() { MappingId = mappingId, Outcome = ValidationOutcome.Fail, Expected = rule.Expression, Actual = string.Join(", ", shown), Message = $"{label} {ex.Message}" };
        }
    }

    private static string JsonName(FieldDataType type) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}
