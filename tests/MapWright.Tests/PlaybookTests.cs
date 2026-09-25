using System.Text.Json;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

internal static class StarterPlaybooks
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "playbooks");

    public static PlaybookLibrary Library() => PlaybookLibrary.Load([Directory]);

    public static Playbook Get(string id) => Library().All.Single(p => p.Id == id);

    public static IEnumerable<object[]> Files() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetRelativePath(Directory, f) });
}

public sealed class StarterPlaybookTests
{
    [Fact]
    public void Starter_set_has_the_five_domain_playbooks_and_the_onboarding_process()
    {
        var ids = StarterPlaybooks.Library().All.Select(p => p.Reference).Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "domain/channel-mix@1.0.0", "domain/entity-type@1.0.0", "domain/principals@1.0.0",
                "domain/processing-volume@1.0.0", "domain/tax-id@1.0.0", "process/onboard-new-system@1.0.0",
            ],
            ids);
    }

    [Fact]
    public void Starter_set_is_valid_without_warnings()
    {
        Assert.Empty(PlaybookValidator.Validate(StarterPlaybooks.Library().All));
    }

    [Theory]
    [MemberData(nameof(StarterPlaybooks.Files), MemberType = typeof(StarterPlaybooks))]
    public void Starter_playbook_tests_pass(string file)
    {
        var playbook = PlaybookSerializer.Load(Path.Combine(StarterPlaybooks.Directory, file));

        var results = PlaybookTestRunner.Run(playbook);

        Assert.All(results, r => Assert.True(r.Passed, r.ToString()));
        if (playbook.Kind == PlaybookKind.Domain)
        {
            Assert.NotEmpty(results);
        }
    }

    [Theory]
    [MemberData(nameof(StarterPlaybooks.Files), MemberType = typeof(StarterPlaybooks))]
    public void Starter_playbook_round_trips(string file)
    {
        var path = Path.Combine(StarterPlaybooks.Directory, file);
        var playbook = PlaybookSerializer.Load(path);

        var again = PlaybookSerializer.Deserialize(PlaybookSerializer.Serialize(playbook));

        Assert.Equal(PlaybookSerializer.Serialize(playbook), PlaybookSerializer.Serialize(again));
    }

    [Fact]
    public void Process_ai_step_is_optional_and_capped_below_auto_accept()
    {
        var process = StarterPlaybooks.Get("process/onboard-new-system").Process!;

        var ai = Assert.Single(process.Steps, s => s.Kind == StepKind.AiAssist);
        Assert.True(ai.Optional);
        Assert.True(ai.MaxConfidence < process.Thresholds.AutoAcceptAt);
        Assert.True(process.Steps.ToList().IndexOf(ai) > process.Steps.ToList().FindIndex(s => s.Kind == StepKind.Match));
    }
}

public sealed class PlaybookDetectionTests
{
    private static readonly PlaybookLibrary Library = StarterPlaybooks.Library();

    private static Dictionary<string, DetectionResult?> DetectProfile(string system) =>
        FieldContext.FromProfile(ProfileSerializer.Load(Path.Combine(AppContext.BaseDirectory, "samples", "systems", system, "profile.json")))
            .ToDictionary(f => f.Path!, Library.Detect);

    [Fact]
    public void Owners_and_officers_are_both_principals_but_officers_need_review()
    {
        var sales = DetectProfile("sales-alpha");
        var uw = DetectProfile("uw-core");

        var owners = sales["$.owners"]!;
        var officers = uw["/UnderwritingRequest/Officers/Officer"]!;
        Assert.Equal(("Principal", false), (owners.BusinessConcept, owners.RequiresReview));
        Assert.Equal(("Principal", true), (officers.BusinessConcept, officers.RequiresReview));
        Assert.True(owners.Score > officers.Score);
        Assert.Contains(officers.Questions, q => q.Contains("beneficial owners", StringComparison.Ordinal));
        Assert.Null(uw["/UnderwritingRequest/Officers"]);
    }

    [Fact]
    public void Volume_period_and_channel_mix_are_recognised_on_both_sides()
    {
        var sales = DetectProfile("sales-alpha");
        var uw = DetectProfile("uw-core");

        Assert.Equal("annual", sales["$.processing.annualCardVolume"]!.Qualifiers["period"]);
        Assert.Equal("monthly", uw["/UnderwritingRequest/Processing/MonthlyVolume"]!.Qualifiers["period"]);
        Assert.Equal("ChannelMix.Moto", sales["$.processing.motoPercent"]!.BusinessConcept);
        Assert.Equal("ChannelMix.Ecommerce", sales["$.processing.ecommPercent"]!.BusinessConcept);
        Assert.Equal("ChannelMix.CardNotPresent", uw["/UnderwritingRequest/Processing/CardNotPresentPct"]!.BusinessConcept);
        Assert.Equal("ChannelMix.CardPresent", uw["/UnderwritingRequest/Processing/CardPresentPct"]!.BusinessConcept);
    }

    [Fact]
    public void Principal_ssn_is_not_the_business_tax_id()
    {
        var sales = DetectProfile("sales-alpha");
        var uw = DetectProfile("uw-core");

        Assert.Equal("Principal.Ssn", sales["$.owners[*].ssn"]!.BusinessConcept);
        Assert.Equal("Principal.Ssn", uw["/UnderwritingRequest/Officers/Officer/SSN"]!.BusinessConcept);
        Assert.Equal("LegalEntity.TaxId", sales["$.account.taxId"]!.BusinessConcept);
        Assert.Equal("LegalEntity.TaxId", uw["/UnderwritingRequest/Merchant/TaxId/Number"]!.BusinessConcept);
        Assert.Equal("LegalEntity.TaxIdType", uw["/UnderwritingRequest/Merchant/TaxId/@type"]!.BusinessConcept);
        Assert.Null(uw["/UnderwritingRequest/Merchant/TaxId"]);
    }

    [Fact]
    public void Ownership_named_percent_with_fraction_values_is_flagged()
    {
        var result = DetectProfile("sales-alpha")["$.owners[*].ownershipPercent"]!;

        Assert.Equal("fraction", result.Qualifiers["unit"]);
        Assert.Contains(ReviewTrigger.QualifierConflict, result.Triggers);
        Assert.True(result.RequiresReview);
        Assert.Contains(result.Warnings, w => w.Contains("'percent'", StringComparison.Ordinal));
    }

    [Fact]
    public void Entity_type_codes_from_both_systems_resolve_to_the_same_canonical_codes()
    {
        var map = StarterPlaybooks.Get("domain/entity-type").Domain!.ValueMaps.Single();

        Assert.Equal("CORPORATION", PlaybookMatcher.ResolveCode(map, "CORP"));
        Assert.Equal("CORPORATION", PlaybookMatcher.ResolveCode(map, "C"));
        Assert.Equal("SOLE_PROPRIETORSHIP", PlaybookMatcher.ResolveCode(map, "SOLE_PROP"));
        Assert.Equal("SOLE_PROPRIETORSHIP", PlaybookMatcher.ResolveCode(map, "SP"));
        Assert.Equal("LLC", PlaybookMatcher.ResolveCode(map, "l.l.c."));
        Assert.Null(PlaybookMatcher.ResolveCode(map, "RETAIL"));
    }

    [Fact]
    public void Unrelated_fields_are_left_for_other_playbooks_or_ai()
    {
        var sales = DetectProfile("sales-alpha");

        Assert.Null(sales["$.account.legalName"]);
        Assert.Null(sales["$.account.address.city"]);
        Assert.Null(sales["$.bank.accountNumber"]);
    }

    [Fact]
    public void Evidence_explains_the_score()
    {
        var result = Library.Detect(new() { Name = "ssn", Ancestors = ["owners"], ValueShapes = ["999-99-9999"] })!;

        Assert.Equal(95, result.Score);
        Assert.Collection(
            result.Evidence,
            e => Assert.Contains("equivalent term 'Ssn' (+60)", e, StringComparison.Ordinal),
            e => Assert.Contains("Ancestor 'owners'", e, StringComparison.Ordinal),
            e => Assert.Contains("PRN-SIG-SSN-SHAPE", e, StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_context_attributes_do_not_match_on_their_own()
    {
        var principals = StarterPlaybooks.Get("domain/principals");

        Assert.Null(PlaybookMatcher.Detect(principals, new() { Name = "email", Ancestors = ["account"] }));
        Assert.Equal("Principal.Email", PlaybookMatcher.Detect(principals, new() { Name = "email", Ancestors = ["owner"] })?.BusinessConcept);
    }

    [Fact]
    public void Assumed_qualifier_requires_review()
    {
        var result = Library.Detect(new() { Name = "cardVolume", Ancestors = ["processing"], DataType = FieldDataType.Decimal })!;

        Assert.Equal("monthly", result.Qualifiers["period"]);
        Assert.Contains(ReviewTrigger.AssumedQualifier, result.Triggers);
        Assert.True(result.RequiresReview);
        Assert.Contains(result.Questions, q => q.Contains("Which period", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_rejects_process_playbooks()
    {
        Assert.Throws<ArgumentException>(() =>
            PlaybookMatcher.Detect(StarterPlaybooks.Get("process/onboard-new-system"), new() { Name = "x" }));
    }
}

public sealed class PlaybookTestRunnerTests
{
    [Fact]
    public void Failing_expectations_are_reported()
    {
        var principals = StarterPlaybooks.Get("domain/principals");
        var domain = principals.Domain!;
        var broken = principals with
        {
            Domain = domain with
            {
                Tests = [domain.Tests[0] with { Expect = "Principal.Ssn" }],
                Derivations = [domain.Derivations[0] with { Examples = [Example(new() { ["fraction"] = 0.5m }, 5m)] }],
                Validations = [],
                Conditions = [],
            },
        };

        var results = PlaybookTestRunner.Run(broken);

        Assert.All(results, r => Assert.False(r.Passed));
        Assert.Contains("expected Principal.Ssn, got Principal", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("expected 5, got 50", results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expression_errors_fail_the_example_instead_of_throwing()
    {
        var channel = StarterPlaybooks.Get("domain/channel-mix");
        var domain = channel.Domain!;
        var rule = domain.Derivations[0] with { Examples = [Example(new() { ["moto"] = 1m }, 1m)] };

        var result = Assert.Single(PlaybookTestRunner.Run(channel with { Domain = domain with { Tests = [], Derivations = [rule], Validations = [], Conditions = [] } }));

        Assert.False(result.Passed);
        Assert.Contains("No value for 'ecomm'", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conditional_rules_pick_the_first_matching_case_or_otherwise()
    {
        var rule = StarterPlaybooks.Get("domain/tax-id").Domain!.Conditions.Single();

        Assert.Equal("SSN", PlaybookTestRunner.Evaluate(rule, new Dictionary<string, string> { ["LegalEntity.EntityType"] = "sole_proprietorship" }));
        Assert.Equal("EIN", PlaybookTestRunner.Evaluate(rule, new Dictionary<string, string> { ["LegalEntity.EntityType"] = "LLC" }));
        Assert.Equal("EIN", PlaybookTestRunner.Evaluate(rule, new Dictionary<string, string>()));
    }

    private static ExpressionExample Example(Dictionary<string, decimal> inputs, decimal expected) => new()
    {
        Inputs = inputs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
        Expected = JsonSerializer.SerializeToElement(expected),
    };
}

public sealed class ExpressionTests
{
    private static ExprValue Eval(string expression, params (string Name, ExprValue Value)[] inputs) =>
        Expressions.Evaluate(Expressions.Parse(expression), inputs.ToDictionary(i => i.Name, i => i.Value));

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]
    [InlineData("-2 * -3", 6)]
    [InlineData("84000 / 12", 7000)]
    [InlineData("round(10000 / 300, 2)", 33.33)]
    [InlineData("abs(80 + 0 + 20 - 100)", 0)]
    [InlineData("max(1, 5, 3) + min(4, 2)", 7)]
    public void Arithmetic(string expression, double expected)
    {
        Assert.Equal((decimal)expected, Eval(expression).Number);
    }

    [Fact]
    public void Aggregates_work_over_lists()
    {
        var ownership = ExprValue.Of([50m, 30m, 20m]);

        Assert.Equal(100m, Eval("sum(x)", ("x", ownership)).Number);
        Assert.Equal(3m, Eval("count(x)", ("x", ownership)).Number);
        Assert.Equal(true, Eval("sum(x) <= 100", ("x", ownership)).Bool);
        Assert.Equal(false, Eval("max(x) < 50", ("x", ownership)).Bool);
    }

    [Fact]
    public void Comparison_is_detected_and_variables_are_listed()
    {
        var parsed = Expressions.Parse("abs(cp + cnp - 100) <= tolerance");

        Assert.True(parsed.IsComparison);
        Assert.Equal(["cp", "cnp", "tolerance"], parsed.Variables());
        Assert.False(Expressions.Parse("moto + ecomm").IsComparison);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 2")]
    [InlineData("median(x)")]
    [InlineData("1..2")]
    [InlineData("a $ b")]
    public void Parse_errors(string expression)
    {
        Assert.Throws<ExpressionException>(() => Expressions.Parse(expression));
    }

    [Fact]
    public void Runtime_errors()
    {
        Assert.Throws<ExpressionException>(() => Eval("1 / 0"));
        Assert.Throws<ExpressionException>(() => Eval("x + 1", ("x", ExprValue.Of([1m, 2m]))));
        Assert.Throws<ExpressionException>(() => Eval("abs(1, 2)"));
        Assert.Throws<ExpressionException>(() => Eval("min(x)", ("x", ExprValue.Of([]))));
        Assert.Throws<ExpressionException>(() => ExprValue.FromJson(JsonSerializer.SerializeToElement("text")));
    }
}

public sealed class NameTokensTests
{
    [Theory]
    [InlineData("annualCardVolume", "annual card volume")]
    [InlineData("annual_card_volume", "annual card volume")]
    [InlineData("Annual Card Volume", "annual card volume")]
    [InlineData("SSNNumber", "ssn number")]
    [InlineData("OwnershipPct", "ownership pct")]
    [InlineData("eCommercePct", "e commerce pct")]
    [InlineData("line1", "line 1")]
    [InlineData("Officers", "officer")]
    [InlineData("address", "address")]
    public void Split(string text, string expected)
    {
        Assert.Equal(expected.Split(' '), NameTokens.Split(text));
    }

    [Theory]
    [InlineData("card volume", "annualCardVolume", 2)]
    [InlineData("date of birth", "dateOfBirth", 3)]
    [InlineData("dateofbirth", "date_of_birth", 1)]
    [InlineData("ecommerce", "eCommercePct", 1)]
    [InlineData("card present", "cardNotPresentPct", 0)]
    [InlineData("mo", "motoPercent", 0)]
    public void Match(string term, string field, int expected)
    {
        Assert.Equal(expected, NameTokens.Match(NameTokens.Split(term), NameTokens.Split(field)));
    }
}

public sealed class PlaybookSerializerTests
{
    [Fact]
    public void Unknown_properties_are_rejected()
    {
        var json = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.json")).Replace("\"owner\":", "\"ownr\":", StringComparison.Ordinal);

        var ex = Assert.Throws<PlaybookException>(() => PlaybookSerializer.Deserialize(json));
        Assert.Contains("ownr", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_enum_values_are_rejected()
    {
        var json = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.json")).Replace("\"status\": \"published\"", "\"status\": \"live\"", StringComparison.Ordinal);

        Assert.Throws<PlaybookException>(() => PlaybookSerializer.Deserialize(json));
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapwright-{Guid.NewGuid():N}.json");
        try
        {
            var playbook = StarterPlaybooks.Get("domain/channel-mix");
            PlaybookSerializer.Save(playbook, path);

            Assert.Equal(PlaybookSerializer.Serialize(playbook), PlaybookSerializer.Serialize(PlaybookSerializer.Load(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_names_the_file_on_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapwright-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ nope");
        try
        {
            var ex = Assert.Throws<PlaybookException>(() => PlaybookSerializer.Load(path));
            Assert.StartsWith(Path.GetFileName(path), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
