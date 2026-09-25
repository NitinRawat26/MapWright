using MapWright.Core.Spec;

namespace MapWright.Tests;

public class MappingSpecValidatorTests
{
    private static IEnumerable<string> ErrorCodes(MappingDocument doc) =>
        MappingSpecValidator.Validate(doc).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    [Fact]
    public void Sample_spec_is_valid()
    {
        Assert.Empty(MappingSpecValidator.Validate(TestSpecs.Sample()));
    }

    [Fact]
    public void Duplicate_mapping_ids_and_target_paths_are_errors()
    {
        var doc = TestSpecs.Minimal(TestSpecs.OneToOne("M1", "$.a", "/B/A"), TestSpecs.OneToOne("M1", "$.b", "/B/A"));

        Assert.Contains("MW011", ErrorCodes(doc));
        Assert.Contains("MW012", ErrorCodes(doc));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Confidence_out_of_range_is_an_error(int confidence)
    {
        var doc = TestSpecs.Minimal(TestSpecs.OneToOne("M1", "$.a", "/B/A", confidence));

        Assert.Contains("MW013", ErrorCodes(doc));
    }

    [Theory]
    [InlineData(MappingType.ManyToOne)]
    [InlineData(MappingType.Unmapped)]
    [InlineData(MappingType.Constant)]
    public void Source_count_must_match_mapping_type(MappingType type)
    {
        var doc = TestSpecs.Minimal(TestSpecs.OneToOne("M1", "$.a", "/B/A") with { Type = type });

        Assert.Contains("MW015", ErrorCodes(doc));
    }

    [Fact]
    public void Enum_map_without_values_is_an_error()
    {
        var mapping = TestSpecs.OneToOne("M1", "$.a", "/B/A") with { Transformation = new() { Type = TransformationType.EnumMap } };

        Assert.Contains("MW020", ErrorCodes(TestSpecs.Minimal(mapping)));
    }

    [Fact]
    public void Required_unmapped_target_without_resolution_is_a_warning()
    {
        var mapping = TestSpecs.OneToOne("M1", "$.a", "/B/A") with { Type = MappingType.Unmapped, Sources = [] };

        var issue = Assert.Single(MappingSpecValidator.Validate(TestSpecs.Minimal(mapping)));
        Assert.Equal(("MW021", IssueSeverity.Warning), (issue.Code, issue.Severity));
    }

    [Fact]
    public void Only_high_confidence_may_be_auto_accepted()
    {
        var mapping = TestSpecs.OneToOne("M1", "$.a", "/B/A", confidence: 84) with { Review = new() { Status = ReviewStatus.AutoAccepted } };

        Assert.Contains("MW022", ErrorCodes(TestSpecs.Minimal(mapping)));
        Assert.DoesNotContain("MW022", ErrorCodes(TestSpecs.Minimal(mapping with { ConfidencePercent = 85 })));
    }

    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("123456789", true)]
    [InlineData("***-**-6789", false)]
    [InlineData("*****6789", false)]
    public void Sensitive_samples_must_be_masked(string sample, bool expectError)
    {
        var baseMapping = TestSpecs.OneToOne("M1", "$.ssn", "/B/SSN");
        var mapping = baseMapping with
        {
            Sources = [baseMapping.Sources[0] with { SampleValue = sample }],
            Risk = new() { Sensitivity = Sensitivity.SensitivePii },
        };

        Assert.Equal(expectError, ErrorCodes(TestSpecs.Minimal(mapping)).Contains("MW030"));
    }

    [Fact]
    public void Findings_and_validation_results_must_reference_known_mappings()
    {
        var doc = TestSpecs.Minimal() with
        {
            Findings = [new() { Id = "F1", Kind = FindingKind.Assumption, Description = "x", MappingIds = ["M404"] }],
            ValidationRuns =
            [
                new()
                {
                    Id = "V1", RanAt = DateTimeOffset.UnixEpoch, SamplePayload = "s.json",
                    Results = [new() { MappingId = "M405", Outcome = ValidationOutcome.Pass }],
                },
            ],
        };

        Assert.Contains("MW041", ErrorCodes(doc));
        Assert.Contains("MW050", ErrorCodes(doc));
    }

    [Fact]
    public void Unsupported_spec_version_is_an_error()
    {
        Assert.Contains("MW001", ErrorCodes(TestSpecs.Minimal() with { SpecVersion = "9.9" }));
    }
}
