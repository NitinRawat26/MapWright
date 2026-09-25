namespace MapWright.Core.Spec;

public enum IssueSeverity
{
    Error,
    Warning,
}

public sealed record SpecIssue(IssueSeverity Severity, string Code, string Location, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToUpperInvariant()} {Code} [{Location}] {Message}";
}

/// <summary>Semantic checks on a mapping spec that JSON deserialization alone cannot enforce.</summary>
public static class MappingSpecValidator
{
    /// <summary>Masked samples may reveal at most this many digits (e.g. "***-**-6789").</summary>
    public const int MaxVisibleDigitsInSensitiveSample = 4;

    public static IReadOnlyList<SpecIssue> Validate(MappingDocument document)
    {
        var issues = new List<SpecIssue>();
        void Error(string code, string location, string message) => issues.Add(new(IssueSeverity.Error, code, location, message));
        void Warn(string code, string location, string message) => issues.Add(new(IssueSeverity.Warning, code, location, message));

        if (document.SpecVersion != MappingDocument.CurrentSpecVersion)
        {
            Error("MW001", "specVersion", $"Unsupported spec version '{document.SpecVersion}'; expected '{MappingDocument.CurrentSpecVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.Id))
        {
            Error("MW002", "id", "Document id is required.");
        }

        var policy = document.ConfidencePolicy;
        if (policy.MediumThreshold < 0 || policy.HighThreshold > 100 || policy.MediumThreshold >= policy.HighThreshold)
        {
            Error("MW003", "confidencePolicy", "Thresholds must satisfy 0 <= mediumThreshold < highThreshold <= 100.");
        }

        var mappingIds = new HashSet<string>(StringComparer.Ordinal);
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in document.Mappings)
        {
            var at = $"mappings[{mapping.Id}]";

            if (string.IsNullOrWhiteSpace(mapping.Id))
            {
                Error("MW010", "mappings", "Every mapping needs an id.");
            }
            else if (!mappingIds.Add(mapping.Id))
            {
                Error("MW011", at, "Duplicate mapping id.");
            }

            if (!targetPaths.Add(mapping.Target.Path))
            {
                Error("MW012", at, $"Target path '{mapping.Target.Path}' is mapped more than once; there must be one row per target field.");
            }

            if (mapping.ConfidencePercent is < 0 or > 100)
            {
                Error("MW013", at, "confidencePercent must be between 0 and 100.");
            }

            if (string.IsNullOrWhiteSpace(mapping.Reasoning))
            {
                Error("MW014", at, "reasoning is required.");
            }

            ValidateCardinality(mapping, at, Error);

            if (mapping.Transformation.Type == TransformationType.EnumMap && mapping.Transformation.ValueMap.Count == 0)
            {
                Error("MW020", at, "EnumMap transformation requires at least one valueMap entry.");
            }

            if (mapping.Type == MappingType.Unmapped && mapping.Target.Required && string.IsNullOrWhiteSpace(mapping.SuggestedResolution))
            {
                Warn("MW021", at, "Required target field is unmapped and has no suggestedResolution.");
            }

            if (mapping.Review.Status == ReviewStatus.AutoAccepted && policy.BandFor(mapping.ConfidencePercent) != ConfidenceBand.High)
            {
                Error("MW022", at, $"Only High-confidence mappings (>= {policy.HighThreshold}%) may be auto-accepted.");
            }

            if (mapping.Review.Status is ReviewStatus.Approved or ReviewStatus.Rejected or ReviewStatus.Overridden
                && string.IsNullOrWhiteSpace(mapping.Review.Reviewer))
            {
                Warn("MW023", at, $"Review status '{mapping.Review.Status}' should record a reviewer.");
            }

            if (mapping.Risk.Sensitivity != Sensitivity.None)
            {
                foreach (var field in mapping.Sources.Append(mapping.Target))
                {
                    if (field.SampleValue is { } sample && sample.Count(char.IsAsciiDigit) > MaxVisibleDigitsInSensitiveSample)
                    {
                        Error("MW030", at, $"Sample value for sensitive field '{field.Name}' looks unmasked; mask all but the last 4 digits.");
                    }
                }
            }
        }

        var findingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in document.Findings)
        {
            if (!findingIds.Add(finding.Id))
            {
                Error("MW040", $"findings[{finding.Id}]", "Duplicate finding id.");
            }

            foreach (var id in finding.MappingIds.Where(id => !mappingIds.Contains(id)))
            {
                Error("MW041", $"findings[{finding.Id}]", $"References unknown mapping id '{id}'.");
            }
        }

        foreach (var run in document.ValidationRuns)
        {
            foreach (var result in run.Results.Where(r => !mappingIds.Contains(r.MappingId)))
            {
                Error("MW050", $"validationRuns[{run.Id}]", $"References unknown mapping id '{result.MappingId}'.");
            }
        }

        return issues;
    }

    private static void ValidateCardinality(FieldMapping mapping, string at, Action<string, string, string> error)
    {
        var count = mapping.Sources.Count;
        var problem = mapping.Type switch
        {
            MappingType.OneToOne when count != 1 => "OneToOne requires exactly one source field.",
            MappingType.ManyToOne when count < 2 => "ManyToOne requires at least two source fields.",
            MappingType.OneToMany when count != 1 => "OneToMany requires exactly one source field.",
            MappingType.Derived when count < 1 => "Derived requires at least one source field.",
            MappingType.Unmapped when count != 0 => "Unmapped must not list source fields.",
            MappingType.Constant when count != 0 => "Constant must not list source fields.",
            MappingType.Constant when mapping.Transformation.DefaultValue is null && mapping.Transformation.Expression is null
                => "Constant requires transformation.defaultValue or transformation.expression.",
            _ => null,
        };

        if (problem is not null)
        {
            error("MW015", at, problem);
        }
    }
}
