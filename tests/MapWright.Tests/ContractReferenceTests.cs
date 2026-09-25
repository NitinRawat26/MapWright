using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using MapWright.Output.Readers;

namespace MapWright.Tests;

public sealed class ContractReferenceTests
{
    private static string Dir(string system, string folder) =>
        Path.Combine(AppContext.BaseDirectory, "samples", "systems", system, folder);

    private static ContractFiles Files(params (string Name, string Content)[] files) => new(files);

    private static (string Name, string Content) File(string system, string folder, string name) =>
        (name, System.IO.File.ReadAllText(Path.Combine(Dir(system, folder), name)));

    private static string[] Shape(ContractDocument document) =>
        [.. document.Fields.Select(f => $"{f.Path}|{f.Kind}|{f.Cardinality}|{f.DataType}|{f.Required}|{f.MaxLength}|{f.MaxValue}|{string.Join(",", f.AllowedValues)}")];

    private static InputFile Upload(string name, string content) => new(name, System.Text.Encoding.UTF8.GetBytes(content));

    [Fact]
    public void Json_schema_split_across_two_files_reads_like_the_single_file()
    {
        var single = File("sales-alpha", "contracts", "sales-alpha-application.schema.json");
        var main = File("sales-alpha", "split-contracts", "sales-alpha-application.schema.json");
        var defs = File("sales-alpha", "split-contracts", "sales-alpha-defs.json");

        var whole = ContractReader.Read(single.Name, single.Content, InputKind.JsonSchema);
        var split = ContractReader.Read(main.Name, main.Content, InputKind.JsonSchema, files: Files(main, defs));

        Assert.Equal(Shape(whole), Shape(split));
        Assert.DoesNotContain(split.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference);
        var referenced = Assert.Single(split.Referenced);
        Assert.Equal(("sales-alpha-defs.json", InputKind.JsonSchema), (referenced.Name, referenced.Kind));
        Assert.Contains("Referenced by sales-alpha-application.schema.json", referenced.Notes);
    }

    [Fact]
    public void Missing_referenced_file_is_still_a_finding()
    {
        var main = File("sales-alpha", "split-contracts", "sales-alpha-application.schema.json");

        var document = ContractReader.Read(main.Name, main.Content, InputKind.JsonSchema);

        Assert.Contains(document.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference
            && f.Message.Contains("'sales-alpha-defs.json#/$defs/account' is not inside", StringComparison.Ordinal));
        Assert.Empty(document.Referenced);
    }

    [Fact]
    public void Openapi_yaml_reads_relative_references_and_references_back_to_itself()
    {
        const string api = """
            openapi: 3.0.3
            info: { title: Boarding, version: '1' }
            paths:
              /applications:
                $ref: 'paths/applications.yaml'
            components:
              schemas:
                Money: { type: number, maximum: 1000000 }
            """;
        const string paths = """
            post:
              operationId: submit
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '../schemas/application.yaml' }
            """;
        const string application = """
            type: object
            required: [merchant]
            properties:
              merchant: { $ref: '#/definitions/Merchant' }
              volume: { $ref: '../api.yaml#/components/schemas/Money' }
            definitions:
              Merchant:
                type: object
                properties:
                  legalName: { type: string, maxLength: 100 }
                  parent: { $ref: '#/definitions/Merchant' }
            """;

        var document = ContractReader.Read("api.yaml", api, InputKind.OpenApi, files: Files(
            ("api.yaml", api), ("paths/applications.yaml", paths), ("schemas/application.yaml", application)));

        Assert.Equal("POST /applications request body", document.Root);
        Assert.Equal(Requirement.Required, document.Fields.Single(f => f.Path == "$.merchant").Required);
        Assert.Equal(100, document.Fields.Single(f => f.Path == "$.merchant.legalName").MaxLength);
        Assert.Equal(1000000m, document.Fields.Single(f => f.Path == "$.volume").MaxValue);
        Assert.Contains(document.Findings, f => f.Message == "Recursive reference 'schemas/application.yaml#/definitions/Merchant' was profiled once.");
        Assert.Equal(["paths/applications.yaml", "schemas/application.yaml"], document.Referenced.Select(r => r.Name));
    }

    [Fact]
    public void Reference_by_file_name_must_match_one_upload()
    {
        const string schema = """{ "$schema": "x", "type": "object", "properties": { "a": { "$ref": "https://example.com/defs/common.json#/$defs/A" } } }""";
        const string common = """{ "$defs": { "A": { "type": "string", "maxLength": 5 } } }""";

        var found = ContractReader.Read("main.json", schema, InputKind.JsonSchema, files: Files(("main.json", schema), ("common.json", common)));
        var ambiguous = ContractReader.Read("main.json", schema, InputKind.JsonSchema, files: Files(
            ("main.json", schema), ("v1/common.json", common), ("v2/common.json", common)));

        Assert.Equal(5, found.Fields.Single(f => f.Path == "$.a").MaxLength);
        Assert.Contains(ambiguous.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference
            && f.Message.Contains("matches several uploaded files (v1/common.json, v2/common.json)", StringComparison.Ordinal));
        Assert.Equal(FieldDataType.Unknown, ambiguous.Fields.Single(f => f.Path == "$.a").DataType);
    }

    [Fact]
    public void Xsd_include_reads_like_the_single_schema()
    {
        var single = File("uw-core", "contracts", "uw-core-intake.xsd");
        var main = File("uw-core", "split-contracts", "uw-core-intake-main.xsd");
        var types = File("uw-core", "split-contracts", "uw-core-simple-types.xsd");

        var whole = ContractReader.Read(single.Name, single.Content, InputKind.Xsd);
        var split = ContractReader.Read(main.Name, main.Content, InputKind.Xsd, files: Files(main, types));
        var alone = ContractReader.Read(main.Name, main.Content, InputKind.Xsd);

        Assert.Equal(Shape(whole), Shape(split));
        Assert.DoesNotContain(split.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference);
        Assert.Equal(("uw-core-simple-types.xsd", InputKind.Xsd), (split.Referenced.Single().Name, split.Referenced.Single().Kind));
        Assert.Contains(alone.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference && f.Message.Contains("uw-core-simple-types.xsd", StringComparison.Ordinal));
    }

    [Fact]
    public void Xs_import_without_a_location_uses_the_upload_with_that_namespace()
    {
        const string main = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common">
              <xs:import namespace="urn:common"/>
              <xs:element name="Request">
                <xs:complexType><xs:sequence><xs:element name="Code" type="c:Code"/></xs:sequence></xs:complexType>
              </xs:element>
            </xs:schema>
            """;
        const string common = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common">
              <xs:simpleType name="Code"><xs:restriction base="xs:string"><xs:maxLength value="4"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """;

        var document = ContractReader.Read("main.xsd", main, InputKind.Xsd, files: Files(("main.xsd", main), ("common-types.xsd", common)));

        Assert.Equal(4, document.Fields.Single(f => f.Path == "/Request/Code").MaxLength);
        Assert.Equal("common-types.xsd", document.Referenced.Single().Name);
    }

    [Fact]
    public void Referenced_file_that_is_not_a_schema_is_an_error()
    {
        const string main = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="data.xml"/><xs:element name="A" type="xs:string"/></xs:schema>""";

        var ex = Assert.Throws<ProfileException>(() => ContractReader.Read("main.xsd", main, InputKind.Xsd, files: Files(("data.xml", "<Data/>"))));

        Assert.Contains("'data.xml', which 'main.xsd' refers to, is not an XML Schema", ex.Message);
    }

    [Fact]
    public void Wsdl_import_of_an_inline_schema_by_namespace_is_not_a_finding()
    {
        var wsdl = System.IO.File.ReadAllText(Path.Combine(Dir("uw-core", "contracts"), "uw-core-intake.wsdl"))
            .Replace("<xs:element name=\"UnderwritingRequest\"", "<xs:import namespace=\"urn:uwcore:intake:4.2\"/>\n<xs:element name=\"UnderwritingRequest\"", StringComparison.Ordinal);

        var document = ContractReader.Read("service.wsdl", wsdl, InputKind.Wsdl);

        Assert.DoesNotContain(document.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference);
    }

    [Fact]
    public void Uploaded_files_that_are_referenced_are_read_as_part_of_their_contract()
    {
        var folder = Dir("uw-core", "split-contracts");
        var uploads = Directory.GetFiles(folder).Order(StringComparer.Ordinal)
            .Select(f => Upload(Path.GetFileName(f), System.IO.File.ReadAllText(f)))
            .Concat(Directory.GetFiles(Dir("uw-core", "samples")).Select(f => Upload(Path.GetFileName(f), System.IO.File.ReadAllText(f))))
            .ToList();

        var set = ProfileInputs.Read(uploads, null);
        var profile = ProfileBuilder.Build(new() { System = "UW Core", Samples = set.Samples, Contracts = set.Contracts });
        var single = ProfileInputs.Read(
            [.. Directory.GetFiles(Dir("uw-core", "contracts")).Where(f => f.EndsWith(".wsdl", StringComparison.Ordinal))
                .Select(f => Upload(Path.GetFileName(f), System.IO.File.ReadAllText(f)))],
            null);

        var contract = Assert.Single(set.Contracts);
        Assert.Equal(InputKind.Wsdl, contract.Kind);
        Assert.Equal(Shape(single.Contracts.Single()), Shape(contract));
        Assert.All(set.Samples, s => Assert.EndsWith(".xml", s.Name, StringComparison.Ordinal));
        Assert.Equal(
            ["uw-core-intake-main.xsd", "uw-core-simple-types.xsd"],
            profile.Inputs.Where(i => i.Notes?.StartsWith("Referenced by uw-core-intake.wsdl", StringComparison.Ordinal) == true).Select(i => i.Name));
        Assert.DoesNotContain(profile.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference);
    }

    [Fact]
    public void Referenced_json_definitions_file_is_not_taken_for_a_sample()
    {
        var main = File("sales-alpha", "split-contracts", "sales-alpha-application.schema.json");
        var defs = File("sales-alpha", "split-contracts", "sales-alpha-defs.json");

        var set = ProfileInputs.Read([Upload(defs.Name, defs.Content), Upload(main.Name, main.Content)], null);

        Assert.Empty(set.Samples);
        Assert.Equal(main.Name, Assert.Single(set.Contracts).Name);
    }
}
