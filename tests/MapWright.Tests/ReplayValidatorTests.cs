using MapWright.Core.Profile;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Replay;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class ReplayValidatorTests
{
    private static readonly SystemProfile Sales = SampleProfiles.Load("sales-alpha");
    private static readonly SystemProfile Uw = SampleProfiles.Load("uw-core");
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(Sales, Uw);
    private static readonly DateTimeOffset RanAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static ValidationRun Replay(SampleDocument payload, MappingDocument? mapping = null)
    {
        mapping ??= SalesToUw;
        var result = TransformEngine.Run(mapping, payload, Uw);
        return ReplayValidator.Validate(mapping, result, Uw, StarterPlaybooks.Library(), "V001", RanAt);
    }

    private static ValidationResult[] For(ValidationRun run, string targetPath) =>
        [.. run.Results.Where(r => r.MappingId == SalesToUw.Row(targetPath).Id)];

    private static ValidationResult Rule(ValidationRun run, string ruleId) =>
        run.Results.Single(r => r.Message?.StartsWith(ruleId + ":", StringComparison.Ordinal) == true);

    [Fact]
    public void Run_records_the_sample_and_one_result_per_row_plus_rule_checks()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "llc-two-owners.json"));

        Assert.Equal("V001", run.Id);
        Assert.Equal(RanAt, run.RanAt);
        Assert.Equal("llc-two-owners.json", run.SamplePayload);
        Assert.Equal(SalesToUw.Mappings.Select(m => m.Id), run.Results.Take(SalesToUw.Mappings.Count).Select(r => r.MappingId));
        Assert.All(run.Results, r => Assert.Contains(r.MappingId, SalesToUw.Mappings.Select(m => m.Id)));
    }

    [Fact]
    public void Produced_values_are_checked_against_the_target_profile()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "llc-two-owners.json"));

        var ssn = Assert.Single(For(run, "/UnderwritingRequest/Officers/Officer/SSN"));
        Assert.Equal(ValidationOutcome.Pass, ssn.Outcome);
        Assert.Equal("string, shape 999999999", ssn.Expected);
        Assert.Equal("2 value(s) produced. Matches the target profile (2 value(s)).", ssn.Message);

        var dob = Assert.Single(For(run, "/UnderwritingRequest/Officers/Officer/DateOfBirth"));
        Assert.Equal("date, yyyy-MM-dd", dob.Expected);
        Assert.Equal(ValidationOutcome.Pass, dob.Outcome);
    }

    [Fact]
    public void Required_targets_without_a_value_fail_and_optional_ones_are_skipped()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "llc-two-owners.json"));

        var requestId = Assert.Single(For(run, "/UnderwritingRequest/@requestId"));
        Assert.Equal(ValidationOutcome.Fail, requestId.Outcome);
        Assert.Equal("Unmapped: no source field. The target field is likely required (present in every target sample).", requestId.Message);

        Assert.Equal(ValidationOutcome.Skipped, Assert.Single(For(run, "/UnderwritingRequest/Merchant/WebsiteUrl")).Outcome);
    }

    [Fact]
    public void Values_that_break_the_target_profile_fail()
    {
        var payload = SampleReader.Read("bad.json", """
            { "account": { "mcc": "58A4" }, "owners": [ { "dateOfBirth": "14/07/1981" } ] }
            """);
        var run = Replay(payload);

        var mcc = Assert.Single(For(run, "/UnderwritingRequest/Merchant/MCC"));
        Assert.Equal(ValidationOutcome.Fail, mcc.Outcome);
        Assert.Equal("$.account.mcc is not a number.", mcc.Message);

        var dob = Assert.Single(For(run, "/UnderwritingRequest/Officers/Officer/DateOfBirth"));
        Assert.Equal(ValidationOutcome.Fail, dob.Outcome);
        Assert.Contains("do not match the target profile: not a valid date (yyyy-MM-dd)", dob.Message);
        Assert.DoesNotContain("14/07", dob.Actual);
    }

    [Fact]
    public void Playbook_validation_rules_run_over_the_produced_values()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "corp-three-owners.json"));

        var total = Rule(run, "PRN-VAL-01");
        Assert.Equal(ValidationOutcome.Pass, total.Outcome);
        Assert.Equal("sum(ownership) <= 100", total.Expected);
        Assert.Equal("ownership=50|30|20", total.Actual);
        Assert.Equal(SalesToUw.Row("/UnderwritingRequest/Officers/Officer/OwnershipPct").Id, total.MappingId);

        Assert.Equal(ValidationOutcome.Pass, Rule(run, "MIX-VAL-02").Outcome);
        Assert.Equal(ValidationOutcome.Pass, Rule(run, "VOL-VAL-02").Outcome);
        Assert.EndsWith("(warning)", Rule(run, "PRN-VAL-02").Message);
    }

    [Fact]
    public void Rules_missing_a_value_are_skipped_and_rules_for_absent_concepts_are_left_out()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "corp-three-owners.json"));

        var ticket = Rule(run, "VOL-VAL-01");
        Assert.Equal(ValidationOutcome.Skipped, ticket.Outcome);
        Assert.EndsWith("Not checked: no target value for high.", ticket.Message);
        Assert.DoesNotContain(run.Results, r => r.Message?.StartsWith("MIX-VAL-01:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Failing_rules_are_reported()
    {
        var payload = SampleReader.Read("over.json", """
            {
              "owners": [ { "ownershipPercent": 0.7 }, { "ownershipPercent": 0.6 } ],
              "processing": { "cardPresentPercent": 70, "motoPercent": 20, "ecommPercent": 20 }
            }
            """);
        var run = Replay(payload);

        var total = Rule(run, "PRN-VAL-01");
        Assert.Equal(ValidationOutcome.Fail, total.Outcome);
        Assert.Equal("ownership=70|60", total.Actual);
        var mix = Rule(run, "MIX-VAL-02");
        Assert.Equal(ValidationOutcome.Fail, mix.Outcome);
        Assert.Equal("cp=70, cnp=40", mix.Actual);
    }

    [Fact]
    public void Runs_pass_the_spec_validator_when_added_to_the_mapping()
    {
        var run = Replay(SamplePayloads.Load("sales-alpha", "sole-prop.json"));
        var withRun = SalesToUw with { ValidationRuns = [run] };

        Assert.DoesNotContain(MappingSpecValidator.Validate(withRun), i => i.Severity == IssueSeverity.Error);
        Assert.Equal("V002", ReplayValidator.NextRunId(withRun));
        Assert.Equal("V003", ReplayValidator.NextRunId(withRun, 1));
    }
}
