using System.Globalization;
using MapWright.Core.Spec;

namespace MapWright.Output.Report;

/// <summary>Column layout of the main mapping sheet. Every renderer uses this single definition.</summary>
public static class MappingSheet
{
    public const string BandTag = "band";
    public const string StatusTag = "status";

    public const string ConfidenceHeader = "Confidence %";
    public const string BandHeader = "Confidence Band";
    public const string StatusHeader = "Review Status";

    private sealed record Column(string Group, ReportColumn Definition, Func<FieldMapping, ConfidencePolicy, string> Value);

    private static readonly Column[] Columns =
    [
        new("identity", new("Mapping ID", Width: 12), (m, _) => m.Id),
        new("identity", new("Mapping Type", Width: 12), (m, _) => Display.Of(m.Type)),

        .. FieldColumns("source", "Source", m => m.Sources),
        .. FieldColumns("target", "Target", m => [m.Target]),

        new("semantics", new("Business Concept", Width: 26), (m, _) => m.BusinessConcept ?? ""),
        new("semantics", new("Domain Playbook", Width: 22), (m, _) => m.DomainPlaybook ?? ""),

        new("transformation", new("Transformation Type", Width: 18), (m, _) => Display.Humanize(m.Transformation.Type)),
        new("transformation", new("Transformation Rule", Width: 34), (m, _) => m.Transformation.Rule ?? ""),
        new("transformation", new("Transformation Expression", Width: 34), (m, _) => m.Transformation.Expression ?? ""),
        new("transformation", new("Condition", Width: 26), (m, _) => m.Transformation.Condition ?? ""),
        new("transformation", new("Default Value", Width: 14), (m, _) => m.Transformation.DefaultValue ?? ""),

        new("confidence", new(ConfidenceHeader, CellKind.Number, 12),
            (m, _) => m.Type == MappingType.Unmapped ? "" : m.ConfidencePercent.ToString(CultureInfo.InvariantCulture)),
        new("confidence", new(BandHeader, Width: 12), (m, p) => Display.Of(m, p)),
        new("confidence", new("Reasoning", Width: 50), (m, _) => m.Reasoning),
        new("confidence", new("Evidence Sources", Width: 40),
            (m, _) => Display.Join(m.Evidence.Select(e => Display.Join([$"{Display.Humanize(e.Kind)}: {e.Reference}", e.Detail], " — ")))),

        new("risk", new("Data Loss Risk", Width: 30),
            (m, _) => Display.Join([Display.Humanize(m.Risk.DataLoss), m.Risk.DataLossNote], " — ")),
        new("risk", new("PII / Sensitivity", Width: 14), (m, _) => Display.Of(m.Risk.Sensitivity)),
        new("risk", new("Target Validation Rules", Width: 32), (m, _) => Display.Join(m.Risk.TargetValidationRules)),

        new("review", new(StatusHeader, Width: 14), (m, _) => Display.Of(m.Review.Status)),
        new("review", new("Reviewer", Width: 16), (m, _) => m.Review.Reviewer ?? ""),
        new("review", new("Review Date", Width: 12), (m, _) => Display.Date(m.Review.ReviewedOn)),
        new("review", new("Reviewer Comments", Width: 34), (m, _) => m.Review.Comments ?? ""),
        new("review", new("Open Question", Width: 34), (m, _) => m.Review.OpenQuestion ?? ""),
    ];

    private static IEnumerable<Column> FieldColumns(string group, string prefix, Func<FieldMapping, IReadOnlyList<FieldDescriptor>> fields)
    {
        Column Make(string header, double width, Func<FieldDescriptor, string?> value) =>
            new(group, new($"{prefix} {header}", Width: width), (m, _) => Display.Join(fields(m).Select(value)));

        yield return Make("Field Name", 22, f => f.Name);
        yield return Make("Field Path", 34, f => f.Path);
        yield return Make("Datatype", 12, f => f.DataType);
        yield return Make("Format / Length", 16, Display.FormatAndLength);
        yield return Make("Required", 10, f => Display.YesNo(f.Required));
        yield return Make("Cardinality", 11, f => Display.Humanize(f.Cardinality));
        yield return Make("Allowed Values", 22, f => string.Join(", ", f.AllowedValues));
        yield return Make("Sample Value (masked)", 20, f => f.SampleValue);
        yield return Make("Description", 34, f => f.Description);
    }

    public static IReadOnlyList<ReportColumn> Definitions { get; } = Columns.Select(c => c.Definition).ToList();

    public static ReportTable Build(MappingDocument document)
    {
        var groupTitles = new Dictionary<string, string>
        {
            ["identity"] = "Identity",
            ["source"] = $"Source: {document.Source.Name}",
            ["target"] = $"Target: {document.Target.Name}",
            ["semantics"] = "Semantics",
            ["transformation"] = "Transformation",
            ["confidence"] = "Confidence",
            ["risk"] = "Risk",
            ["review"] = "Review",
        };

        var groups = Columns
            .GroupBy(c => c.Group)
            .Select(g => new ColumnGroup(groupTitles[g.Key], g.Count(), g.Key))
            .ToList();

        var rows = document.Mappings
            .Select(m => new ReportRow(
                Columns.Select(c => c.Value(m, document.ConfidencePolicy)).ToList(),
                new Dictionary<string, string>
                {
                    [BandTag] = m.Type == MappingType.Unmapped ? "unmapped" : document.ConfidencePolicy.BandFor(m.ConfidencePercent).ToString(),
                    [StatusTag] = m.Review.Status.ToString(),
                }))
            .ToList();

        return new ReportTable("Mapping", Definitions, rows, groups);
    }
}
