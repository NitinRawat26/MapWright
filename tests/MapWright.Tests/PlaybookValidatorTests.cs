using MapWright.Core.Playbooks;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class PlaybookValidatorTests
{
    private static readonly Playbook Principals = StarterPlaybooks.Get("domain/principals");
    private static readonly Playbook Onboarding = StarterPlaybooks.Get("process/onboard-new-system");
    private static DomainDefinition Domain => Principals.Domain!;
    private static ProcessDefinition Process => Onboarding.Process!;

    private static IEnumerable<string> Errors(Playbook playbook) =>
        PlaybookValidator.Validate(playbook).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    private static IEnumerable<string> Errors(IReadOnlyList<Playbook> playbooks) =>
        PlaybookValidator.Validate(playbooks).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    private static Playbook WithDomain(DomainDefinition domain) => Principals with { Domain = domain };

    [Fact]
    public void Header_rules()
    {
        Assert.Contains("PB001", Errors(Principals with { SpecVersion = "9.9" }));
        Assert.Contains("PB002", Errors(Principals with { Id = "Principals" }));
        Assert.Contains("PB002", Errors(Principals with { Id = "process/principals" }));
        Assert.Contains("PB003", Errors(Principals with { Version = "1.0" }));
        Assert.Contains("PB004", Errors(Principals with { Name = " " }));
        Assert.Contains("PB005", Errors(Principals with { Process = Process }));
        Assert.Contains("PB005", Errors(Principals with { Kind = PlaybookKind.Process }));
    }

    [Fact]
    public void Published_without_change_note_is_a_warning()
    {
        var issue = Assert.Single(PlaybookValidator.Validate(Principals with { Version = "1.1.0" }));

        Assert.Equal(("PB006", IssueSeverity.Warning), (issue.Code, issue.Severity));
        Assert.StartsWith("domain/principals@1.1.0", issue.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Concept_and_vocabulary_rules()
    {
        Assert.Contains("PB010", Errors(WithDomain(Domain with { Concept = Domain.Concept with { Name = "principal" } })));
        Assert.Contains("PB010", Errors(WithDomain(Domain with { Concept = Domain.Concept with { Attributes = [.. Domain.Concept.Attributes, Domain.Concept.Attributes[0]] } })));
        Assert.Contains("PB011", Errors(WithDomain(Domain with { Vocabulary = [new() { Term = "x", AppliesTo = "Nope" }] })));
        Assert.Contains("PB012", Errors(WithDomain(Domain with { Vocabulary = [new() { Term = "--" }] })));
        Assert.Contains("PB012", Errors(WithDomain(Domain with
        {
            Vocabulary = [new() { Term = "person", AppliesTo = "FullName" }, new() { Term = "Persons", AppliesTo = "LastName" }],
        })));
    }

    [Fact]
    public void Qualifier_rules()
    {
        var unit = Domain.Qualifiers[0];

        Assert.Contains("PB013", Errors(WithDomain(Domain with { Qualifiers = [unit, unit] })));
        Assert.Contains("PB013", Errors(WithDomain(Domain with { Qualifiers = [unit with { Default = "basisPoints" }] })));
        Assert.Contains("PB013", Errors(WithDomain(Domain with { Qualifiers = [unit with { Values = [] }] })));
        Assert.Contains("PB013", Errors(WithDomain(Domain with { Qualifiers = [unit with { Values = [new() { Value = "x", MinValue = 5, MaxValue = 1 }] }] })));
        Assert.Contains("PB013", Errors(WithDomain(Domain with
        {
            Derivations = [Domain.Derivations[0] with { Output = new() { Attribute = "OwnershipPercent", Qualifiers = new Dictionary<string, string> { ["unit"] = "bps" } } }],
        })));
    }

    [Theory]
    [InlineData(SignalKind.NamePattern, "(unclosed")]
    [InlineData(SignalKind.NamePattern, null)]
    [InlineData(SignalKind.ValueInMap, "NO-SUCH-MAP")]
    [InlineData(SignalKind.DataType, "integer|money")]
    [InlineData(SignalKind.Cardinality, "many")]
    [InlineData(SignalKind.ValueRange, null)]
    public void Signal_rules(SignalKind kind, string? pattern)
    {
        var signal = new DetectionSignal { Id = "S1", Kind = kind, Pattern = pattern, Weight = 10 };

        Assert.Contains("PB014", Errors(WithDomain(Domain with { Signals = [signal] })));
    }

    [Fact]
    public void Signal_weight_and_duplicate_ids()
    {
        var signal = new DetectionSignal { Id = "S1", Kind = SignalKind.NamePattern, Pattern = "x", Weight = 500 };

        Assert.Contains("PB014", Errors(WithDomain(Domain with { Signals = [signal] })));
        Assert.Contains("PB009", Errors(WithDomain(Domain with { Signals = [signal with { Weight = 5 }, signal with { Weight = 5 }] })));
    }

    [Theory]
    [InlineData("fraction *", "PB015")]
    [InlineData("fraction * 100 > 1", "PB015")]
    [InlineData("other * 100", "PB015")]
    public void Derivation_expression_rules(string expression, string code)
    {
        Assert.Contains(code, Errors(WithDomain(Domain with { Derivations = [Domain.Derivations[0] with { Expression = expression, Examples = [] }] })));
    }

    [Fact]
    public void Numeric_derivations_need_an_expression_and_examples_need_inputs()
    {
        var rule = Domain.Derivations[0];

        Assert.Contains("PB015", Errors(WithDomain(Domain with { Derivations = [rule with { Expression = null, Examples = [] }] })));
        Assert.Contains("PB015", Errors(WithDomain(Domain with { Derivations = [rule with { Examples = [rule.Examples[0] with { Inputs = new Dictionary<string, System.Text.Json.JsonElement>() }] }] })));
        Assert.Contains("PB015", Errors(WithDomain(Domain with { Derivations = [rule with { Inputs = [rule.Inputs[0] with { Name = "sum" }] }] })));
        Assert.Contains("PB011", Errors(WithDomain(Domain with { Derivations = [rule with { Output = new() { Attribute = "Nope" } }] })));
    }

    [Fact]
    public void Unused_inputs_are_warnings()
    {
        var rule = Domain.Derivations[0] with { Inputs = [.. Domain.Derivations[0].Inputs, new() { Name = "extra", Attribute = "OwnershipPercent" }] };

        var issues = PlaybookValidator.Validate(WithDomain(Domain with { Derivations = [rule] }));

        Assert.Contains(issues, i => i is { Code: "PB015", Severity: IssueSeverity.Warning } && i.Message.Contains("extra", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Validation_rules_must_be_comparisons_with_boolean_examples()
    {
        var rule = Domain.Validations[0];

        Assert.Contains("PB015", Errors(WithDomain(Domain with { Validations = [rule with { Expression = "sum(ownership)", Examples = [] }] })));
        Assert.Contains("PB015", Errors(WithDomain(Domain with { Validations = [rule with { Examples = [rule.Examples[0] with { Expected = System.Text.Json.JsonSerializer.SerializeToElement(1) }] }] })));
    }

    [Fact]
    public void Conditional_and_value_map_rules()
    {
        var taxId = StarterPlaybooks.Get("domain/tax-id");
        var domain = taxId.Domain!;
        var rule = domain.Conditions[0];
        var map = domain.ValueMaps[0];

        Assert.Contains("PB016", Errors(taxId with { Domain = domain with { Conditions = [rule with { Cases = [] }] } }));
        Assert.Contains("PB016", Errors(taxId with { Domain = domain with { Conditions = [rule with { Otherwise = "PASSPORT" }] } }));
        Assert.Contains("PB016", Errors(taxId with { Domain = domain with { Conditions = [rule with { Cases = [rule.Cases[0] with { When = [new() { Concept = "entityType", In = ["X"] }] }] }] } }));
        Assert.Contains("PB016", Errors(taxId with { Domain = domain with { Conditions = [rule with { Examples = [new() { Given = new Dictionary<string, string> { ["LegalEntity.Mcc"] = "5411" } }] }] } }));
        Assert.Contains("PB017", Errors(taxId with { Domain = domain with { ValueMaps = [map with { Values = [.. map.Values, map.Values[0]] }] } }));
        Assert.Contains("PB017", Errors(taxId with
        {
            Domain = domain with { ValueMaps = [map with { Values = [map.Values[0], map.Values[1] with { Aliases = ["FEIN"] }] }] },
        }));
    }

    [Fact]
    public void Tests_and_publishing_rules()
    {
        Assert.Contains("PB020", Errors(WithDomain(Domain with { Tests = [Domain.Tests[0] with { Expect = "Merchant.Name" }] })));
        Assert.Contains("PB011", Errors(WithDomain(Domain with { Tests = [Domain.Tests[0] with { Expect = "Principal.Nope" }] })));
        Assert.Contains("PB021", Errors(WithDomain(Domain with { Tests = [] })));
        Assert.DoesNotContain("PB021", Errors(Principals with { Status = PlaybookStatus.Draft, Domain = Domain with { Tests = [] } }));
        Assert.Contains("PB019", Errors(WithDomain(Domain with { Confidence = new() { MatchThreshold = 120 } })));
    }

    [Fact]
    public void Ai_steps_must_be_optional_and_capped()
    {
        var ai = Process.Steps.Single(s => s.Kind == StepKind.AiAssist);
        Playbook Replace(ProcessStep step) => Onboarding with
        {
            Process = Process with { Steps = [.. Process.Steps.Select(s => s.Id == ai.Id ? step : s)] },
        };

        Assert.Contains("PB031", Errors(Replace(ai with { Optional = false })));
        Assert.Contains("PB031", Errors(Replace(ai with { MaxConfidence = 95 })));
        Assert.Contains("PB031", Errors(Replace(ai with { MaxConfidence = null })));
    }

    [Fact]
    public void Process_structure_rules()
    {
        var steps = Process.Steps;
        var publish = steps[^1];
        var review = steps.Single(s => s.Kind == StepKind.Review);

        Assert.Contains("PB030", Errors(Onboarding with { Process = Process with { Steps = [] } }));
        Assert.Contains("PB030", Errors(Onboarding with { Process = Process with { Steps = [steps[0], steps[0]] } }));
        Assert.Contains("PB032", Errors(Onboarding with { Process = Process with { Steps = [publish, .. steps.SkipLast(1)] } }));
        Assert.Contains("PB032", Errors(Onboarding with { Process = Process with { Steps = [.. steps.Where(s => s.Kind != StepKind.Review)] } }));
        Assert.Contains("PB033", Errors(Onboarding with { Process = Process with { Thresholds = new() { AutoAcceptAt = 80, ReviewBelow = 90 } } }));
        Assert.Contains("PB034", Errors(Onboarding with
        {
            Process = Process with { Steps = [.. steps.Select(s => s.Kind == StepKind.Validate ? s with { Gates = [s.Gates[1] with { Value = 150 }] } : s)] },
        }));
        Assert.Contains("PB035", Errors(Onboarding with { Process = Process with { Steps = [.. steps.Select(s => s == review ? s with { Reviewers = [] } : s)] } }));
        Assert.Contains("PB035", Errors(Onboarding with { Process = Process with { Steps = [.. steps.Select(s => s == review ? s with { Uses = ["domain/principals"] } : s)] } }));
        Assert.Contains("PB036", Errors(Onboarding with { Process = Process with { Outputs = ["pdf"] } }));
        Assert.Contains("PB036", Errors(Onboarding with { Process = Process with { Inputs = [Process.Inputs[0] with { Kinds = [] }] } }));
    }

    [Fact]
    public void Library_rules()
    {
        var all = StarterPlaybooks.Library().All;
        var entityType = all.Single(p => p.Id == "domain/entity-type");
        var taxId = all.Single(p => p.Id == "domain/tax-id");

        Assert.Contains("PB040", Errors([.. all, Principals]));
        Assert.Contains("PB041", Errors([.. all, Principals with { Version = "2.0.0", ChangeNotes = [] }]));
        Assert.DoesNotContain("PB041", Errors([.. all, Principals with { Version = "2.0.0", Status = PlaybookStatus.Draft }]));
        Assert.Contains("PB042", Errors([.. all.Where(p => p.Id != "domain/channel-mix")]));
        Assert.Contains("PB043", Errors([.. all.Where(p => p.Id is not "domain/entity-type" and not "process/onboard-new-system")]));

        var badCondition = taxId.Domain!.Conditions[0] with
        {
            Cases = [new() { When = [new() { Concept = "LegalEntity.EntityType", In = ["SOLE_PROP"] }], Then = "SSN" }],
        };
        Assert.Contains("PB044", Errors([.. all.Where(p => p != taxId), taxId with { Domain = taxId.Domain with { Conditions = [badCondition] } }]));

        var clash = entityType with { Domain = entityType.Domain! with { Concept = taxId.Domain.Concept with { Attributes = [.. entityType.Domain.Concept.Attributes, taxId.Domain.Concept.Attributes[0]] } } };
        Assert.Contains("PB046", Errors([.. all.Where(p => p != entityType), clash]));
    }

    [Fact]
    public void Active_prefers_published_then_newest_and_skips_retired()
    {
        var draft = Principals with { Version = "1.10.0", Status = PlaybookStatus.Draft };
        var retired = Principals with { Id = "domain/old", Status = PlaybookStatus.Retired };
        var draftOnly = Principals with { Id = "domain/new", Version = "0.2.0", Status = PlaybookStatus.Draft };
        var olderDraft = draftOnly with { Version = "0.10.0" };

        var library = new PlaybookLibrary([Principals, draft, retired, draftOnly, olderDraft]);

        Assert.Equal(["domain/new@0.10.0", "domain/principals@1.0.0"], library.Active.Select(p => p.Reference));
    }
}
