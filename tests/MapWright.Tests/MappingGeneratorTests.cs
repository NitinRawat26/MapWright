using MapWright.Core.Matching;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

internal static class SampleProfiles
{
    public static SystemProfile Load(string system) =>
        ProfileSerializer.Load(Path.Combine(AppContext.BaseDirectory, "samples", "systems", system, "profile.json"));

    /// <summary>A profile built from inline JSON samples.</summary>
    public static SystemProfile FromJson(string system, params string[] samples) => ProfileBuilder.Build(new()
    {
        System = system,
        Samples = [.. samples.Select((s, i) => new SampleInput($"{system}-{i + 1}.json", s))],
    });

    public static MappingDocument Map(SystemProfile source, SystemProfile target) =>
        MappingGenerator.Generate(source, target, StarterPlaybooks.Library(), new() { CreatedAt = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero) });

    public static FieldMapping Row(this MappingDocument document, string targetPath) =>
        document.Mappings.Single(m => m.Target.Path == targetPath);
}

public sealed class MappingGeneratorTests
{
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(SampleProfiles.Load("sales-alpha"), SampleProfiles.Load("uw-core"));
    private static readonly MappingDocument UwToSales = SampleProfiles.Map(SampleProfiles.Load("uw-core"), SampleProfiles.Load("sales-alpha"));

    [Fact]
    public void Generated_specs_pass_the_spec_validator_in_both_directions()
    {
        Assert.DoesNotContain(MappingSpecValidator.Validate(SalesToUw), i => i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(MappingSpecValidator.Validate(UwToSales), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void There_is_one_row_per_target_value_field_in_profile_order()
    {
        var target = SampleProfiles.Load("uw-core");
        var values = target.Fields.Where(f => f.Kind == FieldNodeKind.Value).Select(f => f.Path);

        Assert.Equal(values, SalesToUw.Mappings.Select(m => m.Target.Path));
        Assert.Equal("M001", SalesToUw.Mappings[0].Id);
    }

    [Fact]
    public void Header_records_both_systems_their_inputs_and_the_playbooks_used()
    {
        Assert.Equal("salesalpha-crm__uw-core", SalesToUw.Id);
        Assert.Equal("SalesAlpha CRM → UW Core mapping", SalesToUw.Title);
        Assert.Equal(PayloadFormat.Json, SalesToUw.Source.Format);
        Assert.Equal(PayloadFormat.Xml, SalesToUw.Target.Format);
        Assert.Equal(3, SalesToUw.Inputs.Count(i => i.Side == SystemSide.Source));
        Assert.Equal(2, SalesToUw.Inputs.Count(i => i.Side == SystemSide.Target));
        Assert.Contains(SalesToUw.Playbooks, p => p.Name == "domain/principals" && p.Version == "1.0.0");
        Assert.Contains(SalesToUw.Playbooks, p => p.Name == "process/onboard-new-system");
        Assert.Equal(MappingGenerator.Author, Assert.Single(SalesToUw.ChangeLog).Author);
    }

    [Fact]
    public void Fields_recognised_as_the_same_concept_are_paired()
    {
        var number = SalesToUw.Row("/UnderwritingRequest/Merchant/TaxId/Number");

        Assert.Equal(MappingType.OneToOne, number.Type);
        Assert.Equal("$.account.taxId", Assert.Single(number.Sources).Path);
        Assert.Equal("LegalEntity.TaxId", number.BusinessConcept);
        Assert.Equal("domain/tax-id@1.0.0", number.DomainPlaybook);
        Assert.Equal(Sensitivity.SensitivePii, number.Risk.Sensitivity);
        Assert.Contains(number.Evidence, e => e.Kind == EvidenceKind.Playbook && e.Detail!.StartsWith("Source $.account.taxId", StringComparison.Ordinal));
        Assert.Contains(number.Evidence, e => e.Kind == EvidenceKind.Sample);
    }

    [Fact]
    public void Digit_codes_written_differently_are_reformatted()
    {
        var ssn = SalesToUw.Row("/UnderwritingRequest/Officers/Officer/SSN");

        Assert.Equal(TransformationType.TypeCast, ssn.Transformation.Type);
        Assert.Equal("Reformat 999-99-9999 → 999999999", ssn.Transformation.Rule);
    }

    [Fact]
    public void Codes_are_translated_through_the_playbook_value_map()
    {
        var ownership = SalesToUw.Row("/UnderwritingRequest/Merchant/OwnershipType");

        Assert.Equal(TransformationType.EnumMap, ownership.Transformation.Type);
        Assert.Equal(
            ["CORP→C", "LLC→LLC", "SOLE_PROP→SP"],
            ownership.Transformation.ValueMap.Select(v => $"{v.SourceValue}→{v.TargetValue}").Order(StringComparer.Ordinal));
        Assert.Contains("not seen in target samples", ownership.Transformation.ValueMap.Single(v => v.SourceValue == "LLC").Notes);
        Assert.Equal(ReviewStatus.NeedsReview, ownership.Review.Status);
        Assert.Contains("Target spelling unknown for source value(s) LLC.", ownership.Reasoning);

        var reverse = UwToSales.Row("$.account.entityType");
        Assert.Equal(["C→CORP", "SP→SOLE_PROP"], reverse.Transformation.ValueMap.Select(v => $"{v.SourceValue}→{v.TargetValue}"));
        Assert.Equal(ReviewStatus.AutoAccepted, reverse.Review.Status);
    }

    [Fact]
    public void Only_high_confidence_rows_without_review_reasons_are_auto_accepted()
    {
        var averageTicket = SalesToUw.Row("/UnderwritingRequest/Processing/AverageTicket");
        Assert.Equal(ReviewStatus.AutoAccepted, averageTicket.Review.Status);
        Assert.Equal(TransformationType.Direct, averageTicket.Transformation.Type);

        var source = SampleProfiles.FromJson("A", """{ "owner": { "title": "CEO" } }""");
        var target = SampleProfiles.FromJson("B", """{ "principal": { "title": "CEO" } }""");
        var title = SampleProfiles.Map(source, target).Row("$.principal.title");
        Assert.Equal(85, title.ConfidencePercent);
        Assert.Equal(ReviewStatus.NeedsReview, title.Review.Status);
        Assert.Contains("Confidence 85% is below auto-accept (90%).", title.Reasoning);

        Assert.All(SalesToUw.Mappings.Where(m => m.Review.Status == ReviewStatus.AutoAccepted), m => Assert.True(m.ConfidencePercent >= 90));
    }

    [Fact]
    public void Target_fields_without_a_source_are_unmapped_with_a_resolution()
    {
        var high = SalesToUw.Row("/UnderwritingRequest/Processing/HighTicket");

        Assert.Equal(MappingType.Unmapped, high.Type);
        Assert.Empty(high.Sources);
        Assert.Equal(0, high.ConfidencePercent);
        Assert.Equal(ReviewStatus.NeedsReview, high.Review.Status);
        Assert.Equal("Ask the source team for ProcessingVolume.HighTicket, or agree a default.", high.SuggestedResolution);
    }

    [Fact]
    public void Financial_sample_values_are_masked_in_the_spec()
    {
        var high = SalesToUw.Row("/UnderwritingRequest/Processing/HighTicket");

        Assert.Equal(Sensitivity.Financial, high.Risk.Sensitivity);
        Assert.True(high.Target.SampleValue!.Count(char.IsAsciiDigit) <= 4);
    }

    [Fact]
    public void Different_qualifiers_are_not_paired_as_copies()
    {
        var source = SampleProfiles.FromJson("A", """{ "annualCardVolume": 1200000 }""");
        var target = SampleProfiles.FromJson("B", """{ "monthlyCardVolume": 100000, "yearlyVolume": 1200000 }""");

        var document = SampleProfiles.Map(source, target);

        Assert.False(document.Row("$.monthlyCardVolume") is { Type: MappingType.OneToOne, Transformation.Type: TransformationType.Direct or TransformationType.Rename });
        var yearly = document.Row("$.yearlyVolume");
        Assert.Equal(MappingType.OneToOne, yearly.Type);
        Assert.Equal(TransformationType.Rename, yearly.Transformation.Type);
    }

    [Fact]
    public void Repeating_source_into_a_single_target_is_a_high_data_loss_risk()
    {
        var source = SampleProfiles.FromJson("A", """{ "owners": [ { "email": "a@one.example" }, { "email": "b@one.example" } ] }""");
        var target = SampleProfiles.FromJson("B", """{ "principal": { "email": "a@one.example" } }""");

        var email = SampleProfiles.Map(source, target).Row("$.principal.email");

        Assert.Equal(MappingType.OneToOne, email.Type);
        Assert.Equal(RiskLevel.High, email.Risk.DataLoss);
        Assert.Equal(Cardinality.Array, email.Sources[0].Cardinality);
        Assert.Equal(ReviewStatus.NeedsReview, email.Review.Status);
    }

    [Fact]
    public void Decimal_into_integer_is_a_type_cast_with_data_loss()
    {
        var source = SampleProfiles.FromJson("A", """{ "averageTicket": 42.1 }""", """{ "averageTicket": 12.5 }""");
        var target = SampleProfiles.FromJson("B", """{ "avgTicket": 42 }""");

        var ticket = SampleProfiles.Map(source, target).Row("$.avgTicket");

        Assert.Equal(TransformationType.TypeCast, ticket.Transformation.Type);
        Assert.Equal("Convert decimal → integer", ticket.Transformation.Rule);
        Assert.Equal(RiskLevel.Medium, ticket.Risk.DataLoss);
        Assert.Equal(ReviewStatus.NeedsReview, ticket.Review.Status);
    }
}

public sealed class MappingRuleTests
{
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(SampleProfiles.Load("sales-alpha"), SampleProfiles.Load("uw-core"));
    private static readonly MappingDocument UwToSales = SampleProfiles.Map(SampleProfiles.Load("uw-core"), SampleProfiles.Load("sales-alpha"));

    [Fact]
    public void Annual_volume_is_converted_to_monthly()
    {
        var monthly = SalesToUw.Row("/UnderwritingRequest/Processing/MonthlyVolume");

        Assert.Equal(MappingType.OneToOne, monthly.Type);
        Assert.Equal("$.processing.annualCardVolume", Assert.Single(monthly.Sources).Path);
        Assert.Equal(TransformationType.PeriodConversion, monthly.Transformation.Type);
        Assert.Equal("annual / 12", monthly.Transformation.Expression);
        Assert.Contains("annual = $.processing.annualCardVolume", monthly.Transformation.Rule);
        Assert.Contains("Assumption: Assumes volume is spread evenly", monthly.Reasoning);
        Assert.Contains(monthly.Evidence, e => e.Detail == "Derivation VOL-PERIOD-01: annual / 12");

        var annual = UwToSales.Row("$.processing.annualCardVolume");
        Assert.Equal("monthly * 12", annual.Transformation.Expression);
    }

    [Fact]
    public void Card_not_present_is_the_sum_of_moto_and_ecommerce()
    {
        var cnp = SalesToUw.Row("/UnderwritingRequest/Processing/CardNotPresentPct");

        Assert.Equal(MappingType.ManyToOne, cnp.Type);
        Assert.Equal(["$.processing.motoPercent", "$.processing.ecommPercent"], cnp.Sources.Select(s => s.Path));
        Assert.Equal(TransformationType.Aggregate, cnp.Transformation.Type);
        Assert.Equal("moto + ecomm", cnp.Transformation.Expression);
        Assert.Contains("MIX-CNP-02 (100 - cp) also apply", cnp.Reasoning);
    }

    [Fact]
    public void Fraction_ownership_is_converted_to_percent()
    {
        var pct = SalesToUw.Row("/UnderwritingRequest/Officers/Officer/OwnershipPct");

        Assert.Equal(TransformationType.UnitConversion, pct.Transformation.Type);
        Assert.Equal("fraction * 100", pct.Transformation.Expression);
        Assert.Equal("$.owners[*].ownershipPercent", Assert.Single(pct.Sources).Path);

        Assert.Equal("percent / 100", UwToSales.Row("$.owners[*].ownershipPercent").Transformation.Expression);
    }

    [Fact]
    public void Full_name_is_concatenated_from_first_and_last_name()
    {
        var name = SalesToUw.Row("/UnderwritingRequest/Officers/Officer/FullName");

        Assert.Equal(MappingType.ManyToOne, name.Type);
        Assert.Equal(TransformationType.Concat, name.Transformation.Type);
        Assert.Equal(["$.owners[*].firstName", "$.owners[*].lastName"], name.Sources.Select(s => s.Path));
    }

    [Fact]
    public void Tax_id_type_is_decided_by_the_entity_type()
    {
        var type = SalesToUw.Row("/UnderwritingRequest/Merchant/TaxId/@type");

        Assert.Equal(MappingType.Derived, type.Type);
        Assert.Equal("$.account.entityType", Assert.Single(type.Sources).Path);
        Assert.Equal(TransformationType.Conditional, type.Transformation.Type);
        Assert.Equal("entityType in (SOLE_PROP) → SSN; otherwise → EIN", type.Transformation.Condition);
        Assert.Equal("EIN", type.Transformation.DefaultValue);
        Assert.Equal(
            ["CORP→EIN", "LLC→EIN", "SOLE_PROP→SSN"],
            type.Transformation.ValueMap.Select(v => $"{v.SourceValue}→{v.TargetValue}").Order(StringComparer.Ordinal));
        Assert.Equal(ReviewStatus.NeedsReview, type.Review.Status);
    }

    [Fact]
    public void Lossy_rules_carry_their_risk_and_always_need_review()
    {
        var ecomm = UwToSales.Row("$.processing.ecommPercent");

        Assert.Equal(TransformationType.Split, ecomm.Transformation.Type);
        Assert.Equal(RiskLevel.High, ecomm.Risk.DataLoss);
        Assert.Contains("MOTO is 0", ecomm.Risk.DataLossNote);
        Assert.Equal(ReviewStatus.NeedsReview, ecomm.Review.Status);
        Assert.True(ecomm.ConfidencePercent < 90);

        Assert.Equal(MappingType.Unmapped, UwToSales.Row("$.processing.motoPercent").Type);
    }

    [Fact]
    public void Derivation_inputs_need_the_exact_qualifier()
    {
        var source = SampleProfiles.FromJson("A", """{ "cardVolume": 250000 }""");
        var target = SampleProfiles.FromJson("B", """{ "volumeInCents": 25000000 }""");

        var row = SampleProfiles.Map(source, target).Row("$.volumeInCents");

        Assert.NotEqual(TransformationType.UnitConversion, row.Transformation.Type);
    }
}

public sealed class MappingCompletionTests
{
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(SampleProfiles.Load("sales-alpha"), SampleProfiles.Load("uw-core"));

    [Fact]
    public void Fields_no_playbook_covers_fall_back_to_name_matching_and_need_review()
    {
        var legal = SalesToUw.Row("/UnderwritingRequest/Merchant/LegalName");
        Assert.Equal("$.account.legalName", Assert.Single(legal.Sources).Path);
        Assert.Equal(70, legal.ConfidencePercent);
        Assert.Equal(ReviewStatus.NeedsReview, legal.Review.Status);
        Assert.Contains(legal.Evidence, e => e.Kind == EvidenceKind.NameSimilarity);

        var dba = SalesToUw.Row("/UnderwritingRequest/Merchant/DoingBusinessAs");
        Assert.Equal("$.account.dbaName", Assert.Single(dba.Sources).Path);
        Assert.Equal(TransformationType.Rename, dba.Transformation.Type);
        Assert.Equal(60, dba.ConfidencePercent);

        Assert.All(
            SalesToUw.Mappings.Where(m => m.Evidence.Any(e => e.Kind == EvidenceKind.NameSimilarity)),
            m => Assert.True(m.ConfidencePercent <= NameMatcher.MaximumConfidence));
    }

    [Theory]
    [InlineData("legalName", "LegalName", 70)]
    [InlineData("dbaName", "DoingBusinessAs", 60)]
    [InlineData("incorporationDate", "requestId", 0)]
    [InlineData("merchantPhone", "phone", 54)]
    public void Name_scores(string source, string target, int expected)
    {
        static ProfileField Field(string name) => new()
        {
            Path = $"$.{name}",
            Name = name,
            Kind = FieldNodeKind.Value,
            DataType = FieldDataType.String,
            Cardinality = Cardinality.Single,
        };

        Assert.Equal(expected, NameMatcher.Score(Field(source), Field(target)));
    }

    [Fact]
    public void Unused_source_fields_are_orphans()
    {
        var orphans = SalesToUw.OrphanSourceFields.Select(o => o.Field.Path).ToList();

        Assert.Contains("$.account.salesRepId", orphans);
        Assert.Contains("$.owners[*].email", orphans);
        Assert.DoesNotContain("$.account.taxId", orphans);
        Assert.DoesNotContain("$.processing.motoPercent", orphans);
        Assert.StartsWith("Recognised as Principal.Email", SalesToUw.OrphanSourceFields.Single(o => o.Field.Path == "$.owners[*].email").SuggestedResolution);
    }

    [Fact]
    public void List_pairing_on_a_related_term_is_an_assumption_every_child_row_reviews()
    {
        var officerRows = SalesToUw.Mappings.Where(m => m.Target.Path.StartsWith("/UnderwritingRequest/Officers/Officer/", StringComparison.Ordinal) && m.Type != MappingType.Unmapped).ToList();
        var finding = Assert.Single(SalesToUw.Findings, f => f.Description.StartsWith("Each item of source list $.owners", StringComparison.Ordinal));

        Assert.Equal(FindingKind.Assumption, finding.Kind);
        Assert.Equal(officerRows.Select(m => m.Id), finding.MappingIds);
        Assert.Contains("beneficial owners", finding.Description);
        Assert.All(officerRows, m => Assert.Equal(ReviewStatus.NeedsReview, m.Review.Status));
        Assert.All(officerRows, m => Assert.Single(m.Reasoning.Split("List pairing").Skip(1)));
    }

    [Fact]
    public void Rule_assumptions_and_qualifier_conflicts_become_findings()
    {
        Assert.Contains(SalesToUw.Findings, f => f.Kind == FindingKind.Assumption && f.Sources.Contains("domain/processing-volume@1.0.0 VOL-PERIOD-01"));
        Assert.Contains(SalesToUw.Findings, f => f.Kind == FindingKind.Assumption && f.Sources.Contains("domain/tax-id@1.0.0 TIN-TYPE-01"));

        var conflict = Assert.Single(SalesToUw.Findings, f => f.Kind == FindingKind.Conflict);
        Assert.Contains("$.owners[*].ownershipPercent", conflict.Description);
        Assert.Equal([SalesToUw.Row("/UnderwritingRequest/Officers/Officer/OwnershipPct").Id], conflict.MappingIds);

        Assert.Equal(SalesToUw.Findings.Select((_, i) => $"F{i + 1:000}"), SalesToUw.Findings.Select(f => f.Id));
    }

    [Fact]
    public void Profile_type_conflicts_on_mapped_fields_become_findings()
    {
        var source = SampleProfiles.FromJson("A", """{ "legalName": "Acme" }""", """{ "legalName": 42 }""");
        var target = SampleProfiles.FromJson("B", """{ "legalName": "Acme" }""");

        var document = SampleProfiles.Map(source, target);

        var conflict = Assert.Single(document.Findings, f => f.Kind == FindingKind.Conflict);
        Assert.StartsWith("A $.legalName:", conflict.Description);
        Assert.Equal([document.Row("$.legalName").Id], conflict.MappingIds);
    }

    [Fact]
    public void A_target_value_that_never_changes_is_suggested_as_a_constant()
    {
        var channel = SalesToUw.Row("/UnderwritingRequest/@sourceChannel");

        Assert.Equal(MappingType.Unmapped, channel.Type);
        Assert.Contains("Every target sample holds 'SALES_ALPHA'", channel.SuggestedResolution);
    }

    [Fact]
    public void Only_high_confidence_rows_without_review_are_auto_accepted()
    {
        var policy = SalesToUw.ConfidencePolicy;

        Assert.All(
            SalesToUw.Mappings.Where(m => m.Review.Status == ReviewStatus.AutoAccepted),
            m =>
            {
                Assert.True(m.ConfidencePercent >= policy.HighThreshold);
                Assert.Equal(RiskLevel.None, m.Risk.DataLoss);
                Assert.DoesNotContain("Needs review", m.Reasoning);
            });
        Assert.Equal(
            ["M006", "M017", "M018", "M020", "M021"],
            SalesToUw.Mappings.Where(m => m.Review.Status == ReviewStatus.AutoAccepted).Select(m => m.Id));
    }
}
