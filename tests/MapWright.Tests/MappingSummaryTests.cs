using MapWright.Core.Spec;

namespace MapWright.Tests;

public class MappingSummaryTests
{
    [Fact]
    public void Sample_summary_counts()
    {
        var s = MappingSummary.From(TestSpecs.Sample());

        Assert.Equal(18, s.TotalTargetFields);
        Assert.Equal(16, s.MappedTargetFields);
        Assert.Equal(17, s.RequiredTargetFields);
        Assert.Equal(16, s.RequiredTargetFieldsMapped);
        Assert.Equal(94.1m, s.RequiredCoveragePercent);
        Assert.Equal(14, s.ByConfidenceBand[ConfidenceBand.High]);
        Assert.Equal(2, s.ByConfidenceBand[ConfidenceBand.Medium]);
        Assert.Equal(0, s.ByConfidenceBand[ConfidenceBand.Low]);
        Assert.Equal(4, s.ByReviewStatus[ReviewStatus.NeedsReview]);
        Assert.Equal(2, s.OrphanSourceFields);
        Assert.Equal((1, 2), (s.Conflicts, s.Assumptions));
        Assert.Equal((4, 1, 1), (s.ValidationPassed, s.ValidationFailed, s.ValidationSkipped));
    }

    [Fact]
    public void Mapped_rows_are_counted_by_what_produced_them()
    {
        Evidence Of(EvidenceKind kind) => new() { Kind = kind, Reference = "ollama/qwen3" };
        FieldMapping Row(string id, params EvidenceKind[] kinds) => TestSpecs.OneToOne(id, $"$.{id}", $"/B/{id}") with { Evidence = [.. kinds.Select(Of)] };
        var unmapped = Row("M5") with { Type = MappingType.Unmapped, Sources = [] };
        var doc = TestSpecs.Minimal(
            Row("M1", EvidenceKind.Playbook, EvidenceKind.Sample),
            Row("M2", EvidenceKind.Playbook, EvidenceKind.AiSuggestion),
            Row("M3", EvidenceKind.NameSimilarity),
            Row("M4", EvidenceKind.Reviewer),
            unmapped,
            Row("M6", EvidenceKind.Schema));

        var byOrigin = MappingSummary.From(doc).ByOrigin;

        Assert.Equal(
            [(MappingOrigin.Playbook, 1), (MappingOrigin.NameMatch, 1), (MappingOrigin.Ai, 1), (MappingOrigin.Reviewer, 1), (MappingOrigin.Other, 1)],
            byOrigin.Select(p => (p.Key, p.Value)));
        Assert.Null(MappingSummary.OriginOf(unmapped));
    }

    [Fact]
    public void Coverage_is_100_when_no_required_targets()
    {
        var mapping = TestSpecs.OneToOne("M1", "$.a", "/B/A");
        var doc = TestSpecs.Minimal(mapping with { Target = mapping.Target with { Required = false } });

        Assert.Equal(100m, MappingSummary.From(doc).RequiredCoveragePercent);
    }

    [Theory]
    [InlineData(100, ConfidenceBand.High)]
    [InlineData(85, ConfidenceBand.High)]
    [InlineData(84, ConfidenceBand.Medium)]
    [InlineData(60, ConfidenceBand.Medium)]
    [InlineData(59, ConfidenceBand.Low)]
    public void Confidence_bands_use_policy_thresholds(int percent, ConfidenceBand expected)
    {
        Assert.Equal(expected, new ConfidencePolicy().BandFor(percent));
    }
}
