using MapWright.Core.Matching;
using MapWright.Core.Playbooks;
using MapWright.Core.Replay;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class MultiFieldConditionTests
{
    private const string TaxIdType = "/UnderwritingRequest/Merchant/TaxId/@type";

    private static readonly ConditionalRule TwoFieldRule = new()
    {
        Id = "TIN-TYPE-01",
        Description = "Sole proprietors, and LLCs run by a managing member, use the owner's SSN; others use an EIN.",
        Output = new() { Attribute = "TaxIdType" },
        Cases =
        [
            new() { When = [new() { Concept = "LegalEntity.EntityType", In = ["SOLE_PROPRIETORSHIP"] }], Then = "SSN" },
            new()
            {
                When =
                [
                    new() { Concept = "LegalEntity.EntityType", In = ["LLC"] },
                    new() { Concept = "Principal.Title", In = ["Managing Member"] },
                ],
                Then = "SSN",
            },
        ],
        Otherwise = "EIN",
    };

    private static readonly MappingDocument Generated = MappingGenerator.Generate(
        SampleProfiles.Load("sales-alpha"),
        SampleProfiles.Load("uw-core"),
        new PlaybookLibrary([.. StarterPlaybooks.Library().All.Select(p => p.Id == "domain/tax-id"
            ? p with { Domain = p.Domain! with { Conditions = [TwoFieldRule] } }
            : p)]),
        new() { CreatedAt = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero) });

    private static FieldMapping Row(params ConditionalCase[] cases) => new()
    {
        Id = "M001",
        Type = MappingType.ManyToOne,
        Sources =
        [
            new() { Path = "$.type", Name = "type", DataType = "string" },
            new() { Path = "$.country", Name = "country", DataType = "string" },
        ],
        Target = new() { Path = "/R/Kind", Name = "Kind", DataType = "string" },
        Transformation = new() { Type = TransformationType.Conditional, Cases = cases, DefaultValue = "EIN" },
        ConfidencePercent = 70,
        Reasoning = "test",
    };

    private static readonly ConditionalCase UsSoleProp = new()
    {
        When = [new() { Source = "$.type", In = ["SP"] }, new() { Source = "$.country", In = ["US"] }],
        Then = "SSN",
    };

    private static TransformResult Run(FieldMapping row, string json) =>
        TransformEngine.Run(TestSpecs.Minimal(row), Core.Profile.Samples.SampleReader.Read("s.json", json));

    [Theory]
    [InlineData("""{ "type": "SP", "country": "US" }""", "SSN")]
    [InlineData("""{ "type": " sp ", "country": "us" }""", "SSN")]
    [InlineData("""{ "type": "SP", "country": "CA" }""", "EIN")]
    [InlineData("""{ "type": "SP" }""", "EIN")]
    [InlineData("""{ "country": "US" }""", "EIN")]
    public void First_case_whose_clauses_all_hold_gives_the_value_else_the_default(string json, string expected)
    {
        var result = Run(Row(UsSoleProp), json);

        Assert.Equal(ValidationOutcome.Pass, Assert.Single(result.Rows).Outcome);
        Assert.Equal(expected, Assert.Single(result.Values).Value);
    }

    [Fact]
    public void No_source_value_gives_no_value_and_no_default_is_a_failure()
    {
        Assert.Empty(Run(Row(UsSoleProp), """{ "other": 1 }""").Values);

        var row = Row(UsSoleProp) with { Transformation = Row(UsSoleProp).Transformation with { DefaultValue = null } };
        var result = Run(row, """{ "type": "LLC", "country": "US" }""");

        Assert.Equal(ValidationOutcome.Fail, result.Rows[0].Outcome);
        Assert.Equal("No condition covers type = 'LLC', country = 'US'.", result.Rows[0].Message);
    }

    [Fact]
    public void Several_fields_without_cases_fail_with_a_reason()
    {
        var row = Row() with { Transformation = new() { Type = TransformationType.Conditional, Condition = "type in (SP) and country in (US) → SSN" } };

        var result = Run(row, """{ "type": "SP", "country": "US" }""");

        Assert.Equal("A condition over several fields needs transformation.cases to run.", result.Rows[0].Message);
    }

    [Fact]
    public void Validator_checks_cases()
    {
        var missing = Row() with { Transformation = new() { Type = TransformationType.Conditional, DefaultValue = "EIN" } };
        var unknown = Row(new ConditionalCase { When = [new() { Source = "$.state", In = ["TX"] }], Then = "SSN" }, new ConditionalCase { When = [], Then = "SSN" });

        var warnings = MappingSpecValidator.Validate(TestSpecs.Minimal(missing));
        var errors = MappingSpecValidator.Validate(TestSpecs.Minimal(unknown)).Where(i => i.Severity == IssueSeverity.Error).ToList();

        Assert.Contains(warnings, i => i is { Code: "MW024", Severity: IssueSeverity.Warning });
        Assert.Equal(2, errors.Count(i => i.Code == "MW025"));
        Assert.Contains(errors, i => i.Message == "Clause source '$.state' is not one of the row's sources.");
        Assert.DoesNotContain(MappingSpecValidator.Validate(TestSpecs.Minimal(Row(UsSoleProp))), i => i.Code is "MW024" or "MW025");
    }

    [Fact]
    public void Generator_writes_cases_for_a_playbook_rule_over_several_concepts()
    {
        var row = Generated.Row(TaxIdType);

        Assert.Equal(MappingType.ManyToOne, row.Type);
        Assert.Equal(["$.account.entityType", "$.owners[*].title"], row.Sources.Select(s => s.Path));
        Assert.Empty(row.Transformation.ValueMap);
        Assert.Equal("EIN", row.Transformation.DefaultValue);
        var cases = Assert.IsAssignableFrom<IReadOnlyList<ConditionalCase>>(row.Transformation.Cases);
        Assert.Equal(
            ["$.account.entityType in (SOLE_PROP) → SSN", "$.account.entityType in (LLC) and $.owners[*].title in (Managing Member) → SSN"],
            cases.Select(c => $"{string.Join(" and ", c.When.Select(w => $"{w.Source} in ({string.Join(", ", w.In)})"))} → {c.Then}"));
        Assert.DoesNotContain(MappingSpecValidator.Validate(Generated), i => i.Code is "MW024" or "MW025");
    }

    [Theory]
    [InlineData("sole-prop.json", "SSN")]
    [InlineData("llc-two-owners.json", "SSN")]
    [InlineData("corp-three-owners.json", "EIN")]
    public void Generated_condition_replays_on_the_samples(string sample, string expected)
    {
        var result = TransformEngine.Run(Generated, SamplePayloads.Load("sales-alpha", sample), SampleProfiles.Load("uw-core"));

        Assert.Equal([expected], result.Values.Where(v => v.Path == TaxIdType).Select(v => v.Value));
        Assert.Equal(ValidationOutcome.Pass, result.Rows.Single(r => r.MappingId == Generated.Row(TaxIdType).Id).Outcome);
    }

    [Fact]
    public void Single_field_conditions_are_unchanged()
    {
        var row = SampleProfiles.Map(SampleProfiles.Load("sales-alpha"), SampleProfiles.Load("uw-core")).Row(TaxIdType);

        Assert.Null(row.Transformation.Cases);
        Assert.NotEmpty(row.Transformation.ValueMap);
    }

    [Fact]
    public void Cases_round_trip_through_the_spec_file()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Minimal(Row(UsSoleProp)));

        Assert.Contains("\"cases\"", json, StringComparison.Ordinal);
        Assert.Equal("SSN", MappingSpecSerializer.Deserialize(json).Mappings[0].Transformation.Cases![0].Then);
        Assert.DoesNotContain("\"cases\"", MappingSpecSerializer.Serialize(TestSpecs.Minimal(TestSpecs.OneToOne("M1", "$.a", "/B/A"))), StringComparison.Ordinal);
    }
}
