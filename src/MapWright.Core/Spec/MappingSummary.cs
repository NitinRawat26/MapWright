namespace MapWright.Core.Spec;

public sealed record MappingSummary
{
    public required int TotalTargetFields { get; init; }
    public required int MappedTargetFields { get; init; }
    public required int UnmappedTargetFields { get; init; }
    public required int RequiredTargetFields { get; init; }
    public required int RequiredTargetFieldsMapped { get; init; }
    public required decimal RequiredCoveragePercent { get; init; }
    public required IReadOnlyDictionary<ConfidenceBand, int> ByConfidenceBand { get; init; }
    public required IReadOnlyDictionary<ReviewStatus, int> ByReviewStatus { get; init; }
    public required int OrphanSourceFields { get; init; }
    public required int Conflicts { get; init; }
    public required int Assumptions { get; init; }
    public required int ValidationPassed { get; init; }
    public required int ValidationFailed { get; init; }
    public required int ValidationSkipped { get; init; }

    public static MappingSummary From(MappingDocument document)
    {
        var mappings = document.Mappings;
        var mapped = mappings.Where(m => m.Type != MappingType.Unmapped).ToList();
        var required = mappings.Where(m => m.Target.Required).ToList();
        var requiredMapped = required.Count(m => m.Type != MappingType.Unmapped);
        var results = document.ValidationRuns.SelectMany(r => r.Results).ToList();

        return new MappingSummary
        {
            TotalTargetFields = mappings.Count,
            MappedTargetFields = mapped.Count,
            UnmappedTargetFields = mappings.Count - mapped.Count,
            RequiredTargetFields = required.Count,
            RequiredTargetFieldsMapped = requiredMapped,
            RequiredCoveragePercent = required.Count == 0
                ? 100m
                : Math.Round(100m * requiredMapped / required.Count, 1),
            ByConfidenceBand = Enum.GetValues<ConfidenceBand>().ToDictionary(
                band => band,
                band => mapped.Count(m => document.ConfidencePolicy.BandFor(m.ConfidencePercent) == band)),
            ByReviewStatus = Enum.GetValues<ReviewStatus>().ToDictionary(
                status => status,
                status => mappings.Count(m => m.Review.Status == status)),
            OrphanSourceFields = document.OrphanSourceFields.Count,
            Conflicts = document.Findings.Count(f => f.Kind == FindingKind.Conflict),
            Assumptions = document.Findings.Count(f => f.Kind == FindingKind.Assumption),
            ValidationPassed = results.Count(r => r.Outcome == ValidationOutcome.Pass),
            ValidationFailed = results.Count(r => r.Outcome == ValidationOutcome.Fail),
            ValidationSkipped = results.Count(r => r.Outcome == ValidationOutcome.Skipped),
        };
    }
}
