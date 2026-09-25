using MapWright.Core.Profile;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Replay;
using MapWright.Core.Spec;

namespace MapWright.Tests;

internal static class SamplePayloads
{
    public static SampleDocument Load(string system, string file) =>
        SampleReader.Read(file, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "systems", system, "samples", file)));
}

public sealed class TransformEngineTests
{
    private static readonly SystemProfile Sales = SampleProfiles.Load("sales-alpha");
    private static readonly SystemProfile Uw = SampleProfiles.Load("uw-core");
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(Sales, Uw);
    private static readonly MappingDocument UwToSales = SampleProfiles.Map(Uw, Sales);

    private static string[] Values(TransformResult result, string path) =>
        [.. result.Values.Where(v => v.Path == path).OrderBy(v => string.Join(",", v.Indexes)).Select(v => v.Value)];

    private static ValidationResult RowFor(TransformResult result, MappingDocument mapping, string targetPath) =>
        result.Rows.Single(r => r.MappingId == mapping.Row(targetPath).Id);

    [Fact]
    public void Source_values_use_profile_paths_with_list_positions()
    {
        var json = SourceValues.Read(SamplePayloads.Load("sales-alpha", "llc-two-owners.json"));
        var ssn = json["$.owners[*].ssn"].ToList();
        Assert.Equal([[0], [1]], ssn.Select(v => v.Indexes.ToArray()));
        Assert.Equal(ScalarKind.Number, json["$.processing.annualCardVolume"].Single().Kind);

        var xml = SourceValues.Read(SamplePayloads.Load("uw-core", "corp-application.xml"));
        Assert.Equal("EIN", xml["/UnderwritingRequest/Merchant/TaxId/@type"].Single().Value);
        Assert.Equal(3, xml["/UnderwritingRequest/Officers/Officer/FullName"].Count());
        Assert.Empty(xml["/Envelope/Header/ClientId"]);
    }

    [Fact]
    public void Json_payload_is_transformed_into_target_values()
    {
        var result = TransformEngine.Run(SalesToUw, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"), Uw);

        Assert.Equal(["Blue Harbor Coffee LLC"], Values(result, "/UnderwritingRequest/Merchant/LegalName"));
        Assert.Equal(["EIN"], Values(result, "/UnderwritingRequest/Merchant/TaxId/@type"));
        Assert.Equal(["LLC"], Values(result, "/UnderwritingRequest/Merchant/OwnershipType"));
        Assert.Equal(["5814"], Values(result, "/UnderwritingRequest/Merchant/MCC"));
        Assert.Equal(["Maria Lopez", "Sam Okafor"], Values(result, "/UnderwritingRequest/Officers/Officer/FullName"));
        Assert.Equal(["900123456", "900654321"], Values(result, "/UnderwritingRequest/Officers/Officer/SSN"));
        Assert.Equal(["60", "40"], Values(result, "/UnderwritingRequest/Officers/Officer/OwnershipPct"));
        Assert.Equal(["41666.67"], Values(result, "/UnderwritingRequest/Processing/MonthlyVolume"));
        Assert.Equal(["30"], Values(result, "/UnderwritingRequest/Processing/CardNotPresentPct"));
        Assert.Contains("Rounded to 2 decimal place(s).", RowFor(result, SalesToUw, "/UnderwritingRequest/Processing/MonthlyVolume").Message);
    }

    [Fact]
    public void Rows_report_pass_fail_or_skipped()
    {
        var result = TransformEngine.Run(SalesToUw, SamplePayloads.Load("sales-alpha", "sole-prop.json"), Uw);

        Assert.Equal(SalesToUw.Mappings.Select(m => m.Id), result.Rows.Select(r => r.MappingId));
        Assert.Equal(ValidationOutcome.Skipped, RowFor(result, SalesToUw, "/UnderwritingRequest/Merchant/WebsiteUrl").Outcome);
        Assert.Equal(ValidationOutcome.Skipped, RowFor(result, SalesToUw, "/UnderwritingRequest/Officers/Officer/Title").Outcome);
        Assert.Equal(["SSN"], Values(result, "/UnderwritingRequest/Merchant/TaxId/@type"));
        Assert.Equal(["SP"], Values(result, "/UnderwritingRequest/Merchant/OwnershipType"));
        Assert.DoesNotContain(result.Rows, r => r.Outcome == ValidationOutcome.Fail);
    }

    [Fact]
    public void Xml_payload_is_transformed_into_json_target_values()
    {
        var result = TransformEngine.Run(UwToSales, SamplePayloads.Load("uw-core", "corp-application.xml"), Sales);

        Assert.Equal(["CORP"], Values(result, "$.account.entityType"));
        Assert.Equal(["Dana", "Luis", "Ken"], Values(result, "$.owners[*].firstName"));
        Assert.Equal(["900-10-2020", "900-30-4040", "900-50-6060"], Values(result, "$.owners[*].ssn"));
        Assert.Equal(["0.5", "0.3", "0.2"], Values(result, "$.owners[*].ownershipPercent"));
        Assert.Equal(["3000000"], Values(result, "$.processing.annualCardVolume"));
        Assert.Equal(["20"], Values(result, "$.processing.ecommPercent"));
    }

    [Fact]
    public void A_single_xml_element_still_feeds_a_repeating_target()
    {
        var result = TransformEngine.Run(UwToSales, SamplePayloads.Load("uw-core", "sole-prop-application.xml"), Sales);

        var first = Assert.Single(result.Values, v => v.Path == "$.owners[*].firstName");
        Assert.Equal("Priya", first.Value);
        Assert.Empty(first.Indexes);
        Assert.Equal(["1"], Values(result, "$.owners[*].ownershipPercent"));
    }

    [Fact]
    public void Unknown_codes_fail_the_value_map_but_conditions_fall_back_to_otherwise()
    {
        var payload = SampleReader.Read("trust.json", """{ "account": { "entityType": "TRUST" } }""");
        var result = TransformEngine.Run(SalesToUw, payload, Uw);

        var map = RowFor(result, SalesToUw, "/UnderwritingRequest/Merchant/OwnershipType");
        Assert.Equal(ValidationOutcome.Fail, map.Outcome);
        Assert.Equal("No value-map entry for 'TRUST'.", map.Message);
        Assert.Equal(["EIN"], Values(result, "/UnderwritingRequest/Merchant/TaxId/@type"));
    }

    [Fact]
    public void Sensitive_values_are_masked_in_row_results()
    {
        var result = TransformEngine.Run(SalesToUw, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"), Uw);

        var ssn = RowFor(result, SalesToUw, "/UnderwritingRequest/Officers/Officer/SSN");
        Assert.Equal(ValidationOutcome.Pass, ssn.Outcome);
        Assert.DoesNotContain("900123456", ssn.Actual);
        Assert.EndsWith("3456", ssn.Actual);
        Assert.Equal("Blue Harbor Coffee LLC", RowFor(result, SalesToUw, "/UnderwritingRequest/Merchant/LegalName").Actual);
    }

    [Fact]
    public void Bad_digit_codes_fail_without_showing_the_value()
    {
        var payload = SampleReader.Read("bad.json", """{ "owners": [ { "ssn": "900-12-345" } ] }""");
        var result = TransformEngine.Run(SalesToUw, payload, Uw);

        var ssn = RowFor(result, SalesToUw, "/UnderwritingRequest/Officers/Officer/SSN");
        Assert.Equal(ValidationOutcome.Fail, ssn.Outcome);
        Assert.Equal("Value has 8 digit(s); target pattern 999999999 needs 9.", ssn.Message);
    }

    [Fact]
    public void Expressions_need_every_name_bound_to_a_source()
    {
        var row = SalesToUw.Row("/UnderwritingRequest/Processing/CardNotPresentPct");
        var broken = SalesToUw with
        {
            Mappings = [row with { Transformation = row.Transformation with { Inputs = null } }],
        };

        var result = TransformEngine.Run(broken, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"), Uw);

        var only = Assert.Single(result.Rows);
        Assert.Equal(ValidationOutcome.Fail, only.Outcome);
        Assert.Equal("Expression 'moto + ecomm' uses 'moto', which is not bound to a source field.", only.Message);
    }

    [Fact]
    public void Dates_are_reformatted_between_formats()
    {
        var row = new FieldMapping
        {
            Id = "M001",
            Type = MappingType.OneToOne,
            Sources = [new() { Path = "$.since", Name = "since", DataType = "date", Format = "yyyy-MM-dd" }],
            Target = new() { Path = "/R/Since", Name = "Since", DataType = "date", Format = "MM/dd/yyyy" },
            Transformation = new() { Type = TransformationType.TypeCast },
            ConfidencePercent = 90,
            Reasoning = "test",
        };
        var mapping = SalesToUw with { Mappings = [row] };

        var result = TransformEngine.Run(mapping, SampleReader.Read("d.json", """{ "since": "2014-03-15" }"""));

        Assert.Equal("03/15/2014", Assert.Single(result.Values).Value);
    }

    [Fact]
    public void A_repeating_source_into_a_single_target_keeps_the_first_value()
    {
        var row = new FieldMapping
        {
            Id = "M001",
            Type = MappingType.OneToOne,
            Sources = [new() { Path = "$.owners[*].firstName", Name = "firstName", DataType = "string", Cardinality = Cardinality.Array }],
            Target = new() { Path = "/R/Contact", Name = "Contact", DataType = "string" },
            ConfidencePercent = 60,
            Reasoning = "test",
        };
        var mapping = SalesToUw with { Mappings = [row] };

        var result = TransformEngine.Run(mapping, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"));

        Assert.Equal("Maria", Assert.Single(result.Values).Value);
        Assert.Contains("2 source values for a single target field; kept the first.", result.Rows[0].Message);
    }
}
