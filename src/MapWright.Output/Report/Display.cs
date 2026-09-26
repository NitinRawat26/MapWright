using System.Globalization;
using System.Text.RegularExpressions;
using MapWright.Core.Spec;

namespace MapWright.Output.Report;

/// <summary>Human-facing labels for spec values, shared by every renderer.</summary>
public static partial class Display
{
    public const string MultiValueSeparator = "\n";

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    public static string Humanize<TEnum>(TEnum value) where TEnum : struct, Enum =>
        WordBoundary().Replace(value.ToString(), " ");

    public static string Of(MappingType type) => type switch
    {
        MappingType.OneToOne => "1:1",
        MappingType.ManyToOne => "Many:1",
        MappingType.OneToMany => "1:Many",
        _ => Humanize(type),
    };

    public static string Of(Sensitivity sensitivity) => sensitivity switch
    {
        Sensitivity.None => "None",
        Sensitivity.Pii => "PII",
        Sensitivity.SensitivePii => "Sensitive PII",
        Sensitivity.Financial => "Financial",
        Sensitivity.Pci => "PCI",
        _ => Humanize(sensitivity),
    };

    public static string Of(InputKind kind) => kind switch
    {
        InputKind.JsonSchema => "JSON Schema",
        InputKind.Xsd => "XSD",
        InputKind.OpenApi => "OpenAPI",
        InputKind.Wsdl => "WSDL",
        _ => Humanize(kind),
    };

    public static string Of(ReviewStatus status) => status switch
    {
        ReviewStatus.AutoAccepted => "Auto-accepted",
        ReviewStatus.NeedsReview => "Needs review",
        _ => Humanize(status),
    };

    public static string Of(FieldMapping mapping, ConfidencePolicy policy) =>
        mapping.Type == MappingType.Unmapped ? "" : Humanize(policy.BandFor(mapping.ConfidencePercent));

    /// <summary>The AI evidence of a row the AI suggested, or null for rows found by playbooks, names or reviewers.</summary>
    public static Evidence? AiEvidence(FieldMapping mapping) =>
        mapping.Evidence.FirstOrDefault(e => e.Kind == EvidenceKind.AiSuggestion);

    /// <summary>What produced the row: "AI (provider/model)", "Playbook", "Name match" or "Reviewer"; empty for unmapped rows.</summary>
    public static string Origin(FieldMapping mapping)
    {
        if (mapping.Type == MappingType.Unmapped)
        {
            return "";
        }

        if (AiEvidence(mapping) is { } ai)
        {
            return $"AI ({ai.Reference})";
        }

        bool Has(EvidenceKind kind) => mapping.Evidence.Any(e => e.Kind == kind);
        return Has(EvidenceKind.Playbook) ? "Playbook"
            : Has(EvidenceKind.NameSimilarity) ? "Name match"
            : Has(EvidenceKind.Reviewer) ? "Reviewer"
            : "";
    }

    public static string YesNo(bool value) => value ? "Yes" : "No";

    public static string Join(IEnumerable<string?> values, string separator = MultiValueSeparator) =>
        string.Join(separator, values.Where(v => !string.IsNullOrEmpty(v)));

    public static string FormatAndLength(FieldDescriptor field) =>
        Join([field.Format, field.MaxLength is { } max ? $"max {max}" : null], "; ");

    public static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", CultureInfo.InvariantCulture);

    public static string Percent(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
