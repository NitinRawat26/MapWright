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
