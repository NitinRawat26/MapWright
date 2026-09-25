using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class ProfileBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static SystemProfile Build(ProfileOptions? options, params (string Name, string Content)[] samples) =>
        ProfileBuilder.Build(
            new() { System = "Test", Samples = [.. samples.Select(s => new SampleInput(s.Name, s.Content))] },
            options,
            new FixedTime());

    internal static SystemProfile Build(params (string Name, string Content)[] samples) => Build(null, samples);

    private static ProfileField Field(SystemProfile profile, string path) =>
        profile.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", profile.Fields.Select(f => f.Path))}");

    [Fact]
    public void Json_scalar_types_are_inferred()
    {
        var profile = Build(("a.json", """
            { "count": 3, "rate": 1.25, "active": true, "opened": "2019-04-01",
              "at": "2026-09-18T14:22:05Z", "code": "00042", "name": "Blue" }
            """));

        Assert.Equal(PayloadFormat.Json, profile.Format);
        Assert.Equal(Now, profile.CreatedAt);
        Assert.Equal(FieldDataType.Integer, Field(profile, "$.count").DataType);
        Assert.Equal(FieldDataType.Decimal, Field(profile, "$.rate").DataType);
        Assert.Equal(2, Field(profile, "$.rate").MaxScale);
        Assert.Equal(FieldDataType.Boolean, Field(profile, "$.active").DataType);
        Assert.Equal((FieldDataType.Date, "yyyy-MM-dd"), (Field(profile, "$.opened").DataType, Field(profile, "$.opened").Format));
        Assert.Equal(FieldDataType.DateTime, Field(profile, "$.at").DataType);
        Assert.Equal(FieldDataType.String, Field(profile, "$.code").DataType);
        Assert.Equal(FieldDataType.Object, Field(profile, "$").DataType);
    }

    [Fact]
    public void Json_arrays_use_wildcard_paths_and_track_occurrences()
    {
        var profile = Build(
            ("a.json", """{ "owners": [ { "name": "A" }, { "name": "B" } ], "tags": ["x", "y", "z"] }"""),
            ("b.json", """{ "owners": [ { "name": "C" } ], "tags": [] }"""));

        var owners = Field(profile, "$.owners");
        Assert.Equal((FieldNodeKind.Object, Cardinality.Array, 2), (owners.Kind, owners.Cardinality, owners.MaxOccurs));

        var name = Field(profile, "$.owners[*].name");
        Assert.Equal("$.owners", name.ParentPath);
        Assert.Equal(3, name.Presence!.ParentInstances);
        Assert.Equal(Requirement.LikelyRequired, name.Required);

        var tags = Field(profile, "$.tags");
        Assert.Equal((FieldNodeKind.Value, Cardinality.Array, FieldDataType.String, 3), (tags.Kind, tags.Cardinality, tags.DataType, tags.MaxOccurs));
    }

    [Fact]
    public void Json_names_needing_quotes_use_bracket_notation()
    {
        var profile = Build(("a.json", """{ "dba name": "x", "it's": 1 }"""));

        Assert.Contains(profile.Fields, f => f.Path == "$['dba name']");
        Assert.Contains(profile.Fields, f => f.Path == @"$['it\'s']");
    }

    [Fact]
    public void Presence_distinguishes_optional_from_likely_required()
    {
        var profile = Build(
            ("a.json", """{ "owners": [ { "name": "A", "title": "CEO", "email": "a@x.example" }, { "name": "B", "email": null } ] }"""),
            ("b.json", """{ "owners": [ { "name": "C", "email": "c@x.example" } ] }"""));

        Assert.Equal(Requirement.LikelyRequired, Field(profile, "$.owners[*].name").Required);

        var title = Field(profile, "$.owners[*].title");
        Assert.Equal(Requirement.Optional, title.Required);
        Assert.Equal((3, 1), (title.Presence!.ParentInstances, title.Presence.PresentInstances));

        var email = Field(profile, "$.owners[*].email");
        Assert.Equal(Requirement.Optional, email.Required);
        Assert.Equal(1, email.Presence!.NullCount);
    }

    [Fact]
    public void Fields_are_listed_parent_before_children()
    {
        var profile = Build(
            ("a.json", """{ "a": { "x": 1 }, "b": 2 }"""),
            ("b.json", """{ "a": { "x": 1, "y": 2 }, "b": 2 }"""));

        Assert.Equal(["$", "$.a", "$.a.x", "$.a.y", "$.b"], profile.Fields.Select(f => f.Path));
    }

    [Fact]
    public void Json_type_conflicts_are_reported()
    {
        var profile = Build(("a.json", """{ "v": 500000 }"""), ("b.json", """{ "v": "500000" }"""));

        var finding = Assert.Single(profile.Findings, f => f.Kind == ProfileFindingKind.TypeConflict);
        Assert.Equal("$.v", finding.Path);
        Assert.Equal(["a.json", "b.json"], finding.Inputs);
        Assert.Equal(FieldDataType.String, Field(profile, "$.v").DataType);
    }

    [Fact]
    public void Object_versus_value_conflicts_are_reported()
    {
        var profile = Build(("a.json", """{ "v": { "x": 1 } }"""), ("b.json", """{ "v": "flat" }"""));

        Assert.Contains(profile.Findings, f => f.Kind == ProfileFindingKind.KindConflict && f.Path == "$.v");
        Assert.Equal(FieldNodeKind.Object, Field(profile, "$.v").Kind);
    }

    [Fact]
    public void Xml_repeated_elements_become_arrays()
    {
        var profile = Build(("a.xml", """
            <App><Officers><Officer><Name>A</Name></Officer><Officer><Name>B</Name></Officer></Officers></App>
            """));

        var officer = Field(profile, "/App/Officers/Officer");
        Assert.Equal((Cardinality.Array, 2), (officer.Cardinality, officer.MaxOccurs));
        Assert.Equal("/App/Officers/Officer", Field(profile, "/App/Officers/Officer/Name").ParentPath);
        Assert.Equal(2, Field(profile, "/App/Officers/Officer/Name").Presence!.Occurrences);
    }

    [Fact]
    public void Xml_single_child_of_plural_wrapper_is_inferred_as_array()
    {
        const string xml = "<App><Officers><Officer><Name>A</Name></Officer></Officers><Address><Line>1</Line></Address></App>";

        var profile = Build(("a.xml", xml));

        Assert.Equal(Cardinality.Array, Field(profile, "/App/Officers/Officer").Cardinality);
        Assert.Null(Field(profile, "/App/Officers/Officer").MaxOccurs);
        Assert.Equal(Cardinality.Single, Field(profile, "/App/Address/Line").Cardinality);
        Assert.Contains(profile.Findings, f => f.Kind == ProfileFindingKind.InferredCardinality && f.Path == "/App/Officers/Officer");

        var strict = Build(new ProfileOptions { InferWrappedArrays = false }, ("a.xml", xml));
        Assert.Equal(Cardinality.Single, Field(strict, "/App/Officers/Officer").Cardinality);
        Assert.DoesNotContain(strict.Findings, f => f.Kind == ProfileFindingKind.InferredCardinality);
    }

    [Fact]
    public void Xml_attributes_and_text_get_their_own_paths()
    {
        var profile = Build(("a.xml", """<App channel="WEB"><TaxId type="EIN">900123456</TaxId><Empty/></App>"""));

        Assert.Equal("WEB", Field(profile, "/App/@channel").SampleValue);
        Assert.Equal("EIN", Field(profile, "/App/TaxId/@type").SampleValue);
        Assert.Equal(FieldNodeKind.Object, Field(profile, "/App/TaxId").Kind);
        Assert.Equal("/App/TaxId", Field(profile, "/App/TaxId/text()").ParentPath);
        Assert.Equal(Requirement.Optional, Field(profile, "/App/Empty").Required);
        Assert.Equal(1, Field(profile, "/App/Empty").Presence!.EmptyCount);
    }

    [Fact]
    public void Xml_text_types_are_inferred_and_widened()
    {
        var profile = Build(
            ("a.xml", "<R><Zip>02110</Zip><Amount>12</Amount><Flag>true</Flag><Count>5</Count><Nil xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/></R>"),
            ("b.xml", "<R><Zip>94105</Zip><Amount>12.50</Amount><Flag>false</Flag><Count>7</Count><Nil>x</Nil></R>"));

        Assert.Equal(FieldDataType.String, Field(profile, "/R/Zip").DataType);
        Assert.Equal(FieldDataType.Decimal, Field(profile, "/R/Amount").DataType);
        Assert.Equal((12m, 12.50m), (Field(profile, "/R/Amount").MinValue, Field(profile, "/R/Amount").MaxValue));
        Assert.Equal(FieldDataType.Boolean, Field(profile, "/R/Flag").DataType);
        Assert.Equal(FieldDataType.Integer, Field(profile, "/R/Count").DataType);
        Assert.Equal(1, Field(profile, "/R/Nil").Presence!.NullCount);
        Assert.Empty(profile.Findings);
    }

    [Fact]
    public void Soap_envelope_is_unwrapped_and_namespaces_reported_once()
    {
        const string soap = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:u="urn:uw">
              <s:Header><u:Auth>x</u:Auth></s:Header>
              <s:Body><u:Request><u:Name>A</u:Name></u:Request></s:Body>
            </s:Envelope>
            """;
        const string plain = """<Request xmlns="urn:uw"><Name>B</Name></Request>""";

        var profile = Build(("a.xml", soap), ("b.xml", plain));

        Assert.Equal(["/Request", "/Request/Name"], profile.Fields.Select(f => f.Path));
        var soapFinding = Assert.Single(profile.Findings, f => f.Kind == ProfileFindingKind.SoapEnvelopeUnwrapped);
        Assert.Contains("Header was ignored", soapFinding.Message);
        var ns = Assert.Single(profile.Findings, f => f.Kind == ProfileFindingKind.NamespacesIgnored);
        Assert.Equal(["a.xml", "b.xml"], ns.Inputs);

        var raw = Build(new ProfileOptions { UnwrapSoapEnvelope = false }, ("a.xml", soap));
        Assert.Equal("/Envelope", raw.Fields[0].Path);
    }

    [Theory]
    [InlineData("03/15/2014", "02/01/2023", "MM/dd/yyyy", false)]
    [InlineData("15/03/2014", "01/02/2023", "dd/MM/yyyy", false)]
    [InlineData("03/04/2014", "02/01/2023", "MM/dd/yyyy or dd/MM/yyyy", true)]
    public void Slash_dates_are_disambiguated(string first, string second, string format, bool ambiguous)
    {
        var profile = Build(("a.xml", $"<R><D>{first}</D></R>"), ("b.xml", $"<R><D>{second}</D></R>"));

        var field = Field(profile, "/R/D");
        Assert.Equal((FieldDataType.Date, format), (field.DataType, field.Format));
        Assert.Equal(ambiguous, profile.Findings.Any(f => f.Kind == ProfileFindingKind.AmbiguousDateFormat));
    }

    [Fact]
    public void Mixed_date_formats_are_reported()
    {
        var profile = Build(("a.json", """{ "d": "2014-03-15" }"""), ("b.json", """{ "d": "03/15/2014" }"""));

        Assert.Equal("yyyy-MM-dd | MM/dd/yyyy", Field(profile, "$.d").Format);
        Assert.Contains(profile.Findings, f => f.Kind == ProfileFindingKind.MixedFormats);
    }

    [Fact]
    public void Sensitive_fields_are_masked_and_hide_values()
    {
        var profile = Build(("a.xml", """
            <R>
              <SSN>900102020</SSN>
              <TaxId><Number>900778899</Number></TaxId>
              <Reference>123-45-6789</Reference>
              <Amount>250000.00</Amount>
            </R>
            """));

        var ssn = Field(profile, "/R/SSN");
        Assert.True(ssn.Sensitive);
        Assert.Equal("*****2020", ssn.SampleValue);
        Assert.Empty(ssn.ObservedValues);
        Assert.Null(ssn.MinValue);
        Assert.Equal(FieldDataType.String, ssn.DataType);
        Assert.Equal(["999999999"], ssn.ValueShapes);

        Assert.Contains("Parent 'TaxId'", Field(profile, "/R/TaxId/Number").SensitivityReason);
        Assert.False(Field(profile, "/R/TaxId").Sensitive);

        var reference = Field(profile, "/R/Reference");
        Assert.True(reference.Sensitive);
        Assert.Equal("***-**-6789", reference.SampleValue);

        Assert.False(Field(profile, "/R/Amount").Sensitive);
        Assert.Empty(SystemProfileValidator.Validate(profile));
    }

    [Fact]
    public void Values_can_be_left_out_entirely()
    {
        var profile = Build(new ProfileOptions { RetainValues = false }, ("a.json", """{ "name": "Blue", "kind": "LLC" }"""));

        Assert.All(profile.Fields, f =>
        {
            Assert.Null(f.SampleValue);
            Assert.Empty(f.ObservedValues);
        });
        Assert.Equal(["Aaaa"], Field(profile, "$.name").ValueShapes);
    }

    [Fact]
    public void Observed_values_are_capped()
    {
        var items = string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"v{i}\""));
        var profile = Build(new ProfileOptions { MaxObservedValues = 3 }, ("a.json", $$"""{ "codes": [{{items}}] }"""));

        var codes = Field(profile, "$.codes");
        Assert.Equal(["v1", "v2", "v3"], codes.ObservedValues);
        Assert.True(codes.ObservedValuesTruncated);
    }

    [Fact]
    public void Inputs_record_kind_format_and_hash()
    {
        var profile = Build(("a.json", "{}"));

        var input = Assert.Single(profile.Inputs);
        Assert.Equal((InputKind.SamplePayload, PayloadFormat.Json), (input.Kind, input.Format));
        Assert.Equal("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", input.Sha256);
    }

    [Fact]
    public void Samples_of_different_formats_are_rejected()
    {
        var ex = Assert.Throws<ProfileException>(() => Build(("a.json", "{}"), ("b.xml", "<R/>")));
        Assert.Contains("share a format", ex.Message);
    }

    [Theory]
    [InlineData("a.json", "{ \"a\": ", "not valid JSON")]
    [InlineData("a.xml", "<R><A></R>", "not valid XML")]
    [InlineData("a.txt", "hello", "neither JSON nor XML")]
    [InlineData("a.xml", "<!DOCTYPE R [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><R>&x;</R>", "not valid XML")]
    public void Unreadable_samples_are_rejected(string name, string content, string message)
    {
        var ex = Assert.Throws<ProfileException>(() => Build((name, content)));
        Assert.Contains(message, ex.Message);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Request_needs_unique_samples_and_a_system_name()
    {
        Assert.Throws<ProfileException>(() => Build());
        Assert.Throws<ProfileException>(() => Build(("a.json", "{}"), ("A.json", "{}")));
        Assert.Throws<ProfileException>(() => ProfileBuilder.Build(new() { System = " ", Samples = [new("a.json", "{}")] }));
    }
}
