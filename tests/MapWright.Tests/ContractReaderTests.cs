using ClosedXML.Excel;
using MapWright.Cli;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using MapWright.Output.Readers;

namespace MapWright.Tests;

public sealed class ContractReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string SystemDir(string system) =>
        Path.Combine(AppContext.BaseDirectory, "samples", "systems", system);

    private static ProfileField Field(ContractDocument document, string path) =>
        document.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", document.Fields.Select(f => f.Path))}");

    private static ProfileField Field(SystemProfile profile, string path) =>
        profile.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", profile.Fields.Select(f => f.Path))}");

    private static SystemProfile Build(IReadOnlyList<ContractDocument> contracts, params (string Name, string Content)[] samples) =>
        ProfileBuilder.Build(
            new() { System = "Test", Samples = [.. samples.Select(s => new SampleInput(s.Name, s.Content))], Contracts = contracts },
            null,
            new FixedTime());

    private const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["id", "owners"],
          "properties": {
            "id": { "type": "string", "maxLength": 12, "description": "Application id." },
            "createdOn": { "type": "string", "format": "date" },
            "status": { "type": "string", "enum": ["NEW", "DONE"] },
            "amount": { "type": ["number", "null"], "minimum": 0, "maximum": 1000, "multipleOf": 0.01 },
            "owners": { "type": "array", "maxItems": 4, "items": { "$ref": "#/$defs/owner" } },
            "tags": { "type": "array", "items": { "type": "string" } },
            "contact": {
              "allOf": [
                { "$ref": "#/$defs/named" },
                { "type": "object", "properties": { "email": { "type": "string" } } }
              ]
            },
            "payout": {
              "oneOf": [
                { "type": "object", "required": ["iban"], "properties": { "iban": { "type": "string" } } },
                { "type": "object", "required": ["routing"], "properties": { "routing": { "type": "string" } } }
              ]
            },
            "external": { "$ref": "other.json#/address" },
            "parent": { "$ref": "#/$defs/node" }
          },
          "$defs": {
            "owner": {
              "type": "object",
              "required": ["ssn"],
              "properties": { "ssn": { "type": "string" }, "pct": { "type": "integer" } }
            },
            "named": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } },
            "node": { "type": "object", "properties": { "label": { "type": "string" }, "child": { "$ref": "#/$defs/node" } } }
          }
        }
        """;

    [Fact]
    public void Json_schema_declares_types_requiredness_and_constraints()
    {
        var doc = JsonSchemaReader.Read("app.schema.json", Schema);

        Assert.Equal((InputKind.JsonSchema, PayloadFormat.Json), (doc.Kind, doc.Format));
        Assert.Equal(Requirement.Required, Field(doc, "$").Required);
        var id = Field(doc, "$.id");
        Assert.Equal((FieldDataType.String, Requirement.Required, 12, "Application id."), (id.DataType, id.Required, id.MaxLength, id.Description));
        Assert.Equal((FieldDataType.Date, "yyyy-MM-dd", Requirement.Optional), (Field(doc, "$.createdOn").DataType, Field(doc, "$.createdOn").Format, Field(doc, "$.createdOn").Required));
        Assert.Equal(["NEW", "DONE"], Field(doc, "$.status").AllowedValues);
        var amount = Field(doc, "$.amount");
        Assert.Equal((FieldDataType.Decimal, 0m, 1000m, 2), (amount.DataType, amount.MinValue, amount.MaxValue, amount.MaxScale));

        var owners = Field(doc, "$.owners");
        Assert.Equal((FieldNodeKind.Object, Cardinality.Array, 4), (owners.Kind, owners.Cardinality, owners.MaxOccurs));
        Assert.Equal(("$.owners", Requirement.Required), (Field(doc, "$.owners[*].ssn").ParentPath, Field(doc, "$.owners[*].ssn").Required));
        Assert.True(Field(doc, "$.owners[*].ssn").Sensitive);
        Assert.Equal((FieldNodeKind.Value, Cardinality.Array, FieldDataType.String), (Field(doc, "$.tags").Kind, Field(doc, "$.tags").Cardinality, Field(doc, "$.tags").DataType));

        Assert.Equal(Requirement.Required, Field(doc, "$.contact.name").Required);
        Assert.Equal(Requirement.Optional, Field(doc, "$.contact.email").Required);
        Assert.Equal(Requirement.Optional, Field(doc, "$.payout.iban").Required);
        Assert.Equal(Requirement.Optional, Field(doc, "$.payout.routing").Required);

        Assert.Contains(doc.Findings, f => f.Kind == ProfileFindingKind.SchemaSimplified && f.Path == "$.payout");
        Assert.Contains(doc.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference && f.Path == "$.external");
        Assert.Contains(doc.Findings, f => f.Kind == ProfileFindingKind.SchemaSimplified && f.Path == "$.parent.child" && f.Message.Contains("Recursive"));
        Assert.Equal(FieldDataType.String, Field(doc, "$.parent.label").DataType);
        Assert.DoesNotContain(doc.Fields, f => f.Path.StartsWith("$.parent.child.", StringComparison.Ordinal));

        Assert.All(doc.Fields.Where(f => f.Path != "$"), f => Assert.Contains(f.Provenance, p => p.Kind == InputKind.JsonSchema && p.Inputs.SequenceEqual(["app.schema.json"])));
        Assert.Equal(64, doc.Sha256!.Length);
    }

    [Fact]
    public void Json_schema_rejects_invalid_json()
    {
        var ex = Assert.Throws<ProfileException>(() => JsonSchemaReader.Read("bad.json", "{ \"type\": "));
        Assert.Contains("not valid JSON", ex.Message);
    }

    private const string OpenApi = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Boarding", "version": "1" },
          "paths": {
            "/applications": {
              "post": {
                "operationId": "submitApplication",
                "requestBody": { "$ref": "#/components/requestBodies/Application" }
              }
            },
            "/owners/{id}": {
              "put": {
                "operationId": "replaceOwner",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Owner" } } } }
              },
              "get": { "operationId": "getOwner" }
            }
          },
          "components": {
            "requestBodies": {
              "Application": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Application" } } } }
            },
            "schemas": {
              "Application": {
                "type": "object",
                "required": ["legalName"],
                "properties": { "legalName": { "type": "string" }, "owner": { "$ref": "#/components/schemas/Owner" } }
              },
              "Owner": { "type": "object", "properties": { "firstName": { "type": "string" } } }
            }
          }
        }
        """;

    [Fact]
    public void Open_api_profiles_the_chosen_operation_or_schema()
    {
        var byId = OpenApiReader.Read("api.json", OpenApi, "submitApplication");
        Assert.Equal("POST /applications request body", byId.Root);
        Assert.Equal(Requirement.Required, Field(byId, "$.legalName").Required);
        Assert.Equal(FieldDataType.String, Field(byId, "$.owner.firstName").DataType);
        Assert.All(byId.Fields, f => Assert.Equal(["api.json"], f.SeenIn));

        var byRoute = OpenApiReader.Read("api.json", OpenApi, "put /owners/{id}");
        Assert.Equal(["$", "$.firstName"], byRoute.Fields.Select(f => f.Path));

        var bySchema = OpenApiReader.Read("api.json", OpenApi, "Owner");
        Assert.Equal("schema Owner", bySchema.Root);

        var ambiguous = Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.json", OpenApi));
        Assert.Contains("--root", ambiguous.Message);
        Assert.Contains("POST /applications (submitApplication)", ambiguous.Message);
        Assert.Contains("no operation or schema 'nope'", Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.json", OpenApi, "nope")).Message);
    }

    [Fact]
    public void Swagger_2_body_parameters_are_read_and_yaml_is_explained()
    {
        const string swagger = """
            {
              "swagger": "2.0",
              "paths": { "/apps": { "post": { "parameters": [ { "in": "body", "name": "body", "schema": { "$ref": "#/definitions/App" } } ] } } },
              "definitions": { "App": { "type": "object", "properties": { "mcc": { "type": "string", "pattern": "^[0-9]{4}$" } } } }
            }
            """;

        var doc = OpenApiReader.Read("swagger.json", swagger);
        Assert.Equal("POST /apps request body", doc.Root);
        Assert.Equal(FieldDataType.String, Field(doc, "$.mcc").DataType);

        var yaml = Assert.Throws<ProfileException>(() => ContractReader.Read("api.yaml", "openapi: 3.0.0", InputKind.OpenApi));
        Assert.Contains("YAML is not supported", yaml.Message);
        Assert.Contains("not an OpenAPI document", Assert.Throws<ProfileException>(() => OpenApiReader.Read("x.json", "{}")).Message);
    }

    private const string Xsd = """
        <?xml version="1.0"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:test" targetNamespace="urn:test" elementFormDefault="qualified">
          <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
          <xs:element name="Request">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="Id" type="t:Code"/>
                <xs:element ref="t:Amount" minOccurs="0"/>
                <xs:element name="Owner" maxOccurs="unbounded">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="Name" type="xs:string"/>
                      <xs:element name="Born" type="xs:date"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
                <xs:choice>
                  <xs:element name="Ein" type="xs:string"/>
                  <xs:element name="Ssn" type="xs:string"/>
                </xs:choice>
                <xs:element name="TaxId" type="t:TaxId"/>
                <xs:group ref="t:Extra"/>
              </xs:sequence>
              <xs:attribute name="channel" use="required">
                <xs:simpleType>
                  <xs:restriction base="xs:string"><xs:enumeration value="WEB"/><xs:enumeration value="POS"/></xs:restriction>
                </xs:simpleType>
              </xs:attribute>
              <xs:attribute name="version" type="xs:string" fixed="2"/>
            </xs:complexType>
          </xs:element>
          <xs:element name="Amount">
            <xs:annotation><xs:documentation>Monthly amount.</xs:documentation></xs:annotation>
            <xs:simpleType>
              <xs:restriction base="xs:decimal"><xs:minInclusive value="0"/><xs:maxInclusive value="99999"/><xs:fractionDigits value="2"/></xs:restriction>
            </xs:simpleType>
          </xs:element>
          <xs:complexType name="TaxId">
            <xs:simpleContent>
              <xs:extension base="xs:string"><xs:attribute name="type" type="xs:string" use="required"/></xs:extension>
            </xs:simpleContent>
          </xs:complexType>
          <xs:simpleType name="Code">
            <xs:restriction base="xs:string"><xs:minLength value="2"/><xs:maxLength value="8"/></xs:restriction>
          </xs:simpleType>
          <xs:group name="Extra">
            <xs:sequence><xs:element name="Note" type="xs:string" minOccurs="0" maxOccurs="3"/></xs:sequence>
          </xs:group>
        </xs:schema>
        """;

    [Fact]
    public void Xsd_declares_elements_attributes_and_facets()
    {
        var doc = XsdReader.Read("request.xsd", Xsd);

        Assert.Equal(("element Request", PayloadFormat.Xml), (doc.Root, doc.Format));
        Assert.Equal("/Request", doc.Fields[0].Path);
        var id = Field(doc, "/Request/Id");
        Assert.Equal((FieldDataType.String, Requirement.Required, 2, 8), (id.DataType, id.Required, id.MinLength, id.MaxLength));
        var amount = Field(doc, "/Request/Amount");
        Assert.Equal((FieldDataType.Decimal, Requirement.Optional, 0m, 99999m, 2, "Monthly amount."),
            (amount.DataType, amount.Required, amount.MinValue, amount.MaxValue, amount.MaxScale, amount.Description));

        Assert.Equal((FieldNodeKind.Object, Cardinality.Array), (Field(doc, "/Request/Owner").Kind, Field(doc, "/Request/Owner").Cardinality));
        Assert.Equal((FieldDataType.Date, "yyyy-MM-dd"), (Field(doc, "/Request/Owner/Born").DataType, Field(doc, "/Request/Owner/Born").Format));
        Assert.Equal(Requirement.Optional, Field(doc, "/Request/Ein").Required);
        Assert.Equal(Requirement.Optional, Field(doc, "/Request/Ssn").Required);
        Assert.True(Field(doc, "/Request/Ssn").Sensitive);

        Assert.Equal(FieldNodeKind.Object, Field(doc, "/Request/TaxId").Kind);
        Assert.Equal(Requirement.Required, Field(doc, "/Request/TaxId/@type").Required);
        Assert.Equal((FieldDataType.String, "/Request/TaxId"), (Field(doc, "/Request/TaxId/text()").DataType, Field(doc, "/Request/TaxId/text()").ParentPath));
        Assert.Equal((Cardinality.Array, 3), (Field(doc, "/Request/Note").Cardinality, Field(doc, "/Request/Note").MaxOccurs));

        Assert.Equal(["WEB", "POS"], Field(doc, "/Request/@channel").AllowedValues);
        Assert.Equal((Requirement.Optional, "2"), (Field(doc, "/Request/@version").Required, Field(doc, "/Request/@version").AllowedValues.Single()));

        Assert.Contains(doc.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference && f.Message.Contains("common.xsd"));
        Assert.Contains(doc.Findings, f => f.Kind == ProfileFindingKind.NamespacesIgnored);
        Assert.All(doc.Fields, f => Assert.Contains(f.Provenance, p => p.Kind == InputKind.Xsd));
    }

    [Fact]
    public void Xsd_root_selection_and_unsafe_input_are_handled()
    {
        const string two = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="A" type="xs:string"/>
              <xs:element name="B" type="xs:string"/>
            </xs:schema>
            """;

        Assert.Contains("--root: A, B", Assert.Throws<ProfileException>(() => XsdReader.Read("two.xsd", two)).Message);
        Assert.Equal("/B", Assert.Single(XsdReader.Read("two.xsd", two, "B").Fields).Path);
        Assert.Contains("no global element 'C'", Assert.Throws<ProfileException>(() => XsdReader.Read("two.xsd", two, "C")).Message);

        const string dtd = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>""";
        Assert.Contains("not valid XML", Assert.Throws<ProfileException>(() => XsdReader.Read("dtd.xsd", dtd)).Message);
        Assert.Contains("not an XML Schema", Assert.Throws<ProfileException>(() => XsdReader.Read("x.xsd", "<root/>")).Message);
    }

    [Fact]
    public void Wsdl_profiles_the_input_element_of_an_operation()
    {
        const string wsdl = """
            <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" targetNamespace="urn:t">
              <wsdl:types>
                <xs:schema targetNamespace="urn:t">
                  <xs:element name="Submit"><xs:complexType><xs:sequence><xs:element name="Name" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
                  <xs:element name="Status"><xs:complexType><xs:sequence><xs:element name="Id" type="xs:int"/></xs:sequence></xs:complexType></xs:element>
                </xs:schema>
              </wsdl:types>
              <wsdl:message name="SubmitIn"><wsdl:part name="body" element="t:Submit"/></wsdl:message>
              <wsdl:message name="StatusIn"><wsdl:part name="body" element="t:Status"/></wsdl:message>
              <wsdl:portType name="P">
                <wsdl:operation name="SubmitApplication"><wsdl:input message="t:SubmitIn"/></wsdl:operation>
                <wsdl:operation name="GetStatus"><wsdl:input message="t:StatusIn"/></wsdl:operation>
              </wsdl:portType>
            </wsdl:definitions>
            """;

        Assert.Contains("SubmitApplication, GetStatus", Assert.Throws<ProfileException>(() => WsdlReader.Read("svc.wsdl", wsdl)).Message);

        var doc = WsdlReader.Read("svc.wsdl", wsdl, "getstatus");
        Assert.Equal((InputKind.Wsdl, "operation GetStatus input <Status>"), (doc.Kind, doc.Root));
        Assert.Equal(["/Status", "/Status/Id"], doc.Fields.Select(f => f.Path));
        Assert.Equal(FieldDataType.Integer, Field(doc, "/Status/Id").DataType);

        const string wsdl20 = """
            <description xmlns="http://www.w3.org/ns/wsdl" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t">
              <types><xs:schema><xs:element name="Ping" type="xs:string"/></xs:schema></types>
              <interface name="I"><operation name="Ping"><input element="t:Ping"/></operation></interface>
            </description>
            """;
        Assert.Equal("/Ping", Assert.Single(WsdlReader.Read("v2.wsdl", wsdl20).Fields).Path);
    }

    private const string FieldSpecCsv = """
        SalesAlpha field specification v3
        Field Path,Data Type,Mandatory,Format,Valid Values,Description,PII,Repeats
        applicationId,String(20),Y,,,"Application id, from the CRM",N,
        submittedOn,Date,C,DD/MM/YYYY,,,,
        entityType,String,Y,,"CORP = Corporation; LLC = Limited liability company",,,
        annualVolume,"Decimal(12,2)",Y,,,,,
        employees,Numeric(5),N,,,,,
        owners[].name,String(40),Y,,,,,
        owners[].govId,String,Y,,,,Yes,
        tags,String,N,,,,,Y
        """;

    [Fact]
    public void Csv_field_spec_reads_common_column_names()
    {
        var doc = FieldSpecReader.ReadCsv("spec.csv", FieldSpecCsv);

        Assert.Equal((InputKind.FieldSpec, PayloadFormat.Json), (doc.Kind, doc.Format));
        Assert.Equal(["$", "$.applicationId", "$.submittedOn", "$.entityType", "$.annualVolume", "$.employees", "$.owners", "$.owners[*].name", "$.owners[*].govId", "$.tags"],
            doc.Fields.Select(f => f.Path));
        var id = Field(doc, "$.applicationId");
        Assert.Equal((FieldDataType.String, 20, Requirement.Required, "Application id, from the CRM"), (id.DataType, id.MaxLength, id.Required, id.Description));
        var submitted = Field(doc, "$.submittedOn");
        Assert.Equal((FieldDataType.Date, "dd/MM/yyyy", Requirement.Optional), (submitted.DataType, submitted.Format, submitted.Required));
        Assert.Contains("conditional", submitted.Provenance.Single(p => p.Attribute == ProfileAttribute.Required).Detail);
        Assert.Equal(["CORP", "LLC"], Field(doc, "$.entityType").AllowedValues);
        Assert.Equal((FieldDataType.Decimal, 2), (Field(doc, "$.annualVolume").DataType, Field(doc, "$.annualVolume").MaxScale));
        Assert.Equal(FieldDataType.Integer, Field(doc, "$.employees").DataType);

        var owners = Field(doc, "$.owners");
        Assert.Equal((FieldNodeKind.Object, Cardinality.Array), (owners.Kind, owners.Cardinality));
        Assert.Equal(Requirement.Unknown, owners.Required);
        Assert.True(Field(doc, "$.owners[*].govId").Sensitive);
        Assert.Equal(Cardinality.Array, Field(doc, "$.tags").Cardinality);
        Assert.Contains("Row 3", id.Provenance.Single(p => p.Attribute == ProfileAttribute.DataType).Detail);
    }

    [Fact]
    public void Csv_field_spec_handles_xml_paths_and_reports_bad_tables()
    {
        var doc = FieldSpecReader.ReadCsv("uw.csv", "xpath;type;required\n/uw:Request/uw:Merchant/uw:Name;AN(40);M\n/uw:Request/@id;string;M\n");
        Assert.Equal(PayloadFormat.Xml, doc.Format);
        Assert.Equal(["/Request", "/Request/Merchant", "/Request/Merchant/Name", "/Request/@id"], doc.Fields.Select(f => f.Path));
        Assert.Equal((40, Requirement.Required), (Field(doc, "/Request/Merchant/Name").MaxLength, Field(doc, "/Request/Merchant/Name").Required));
        Assert.Equal("id", Field(doc, "/Request/@id").Name);

        Assert.Contains("no path column", Assert.Throws<ProfileException>(() => FieldSpecReader.ReadCsv("x.csv", "name,type\na,string")).Message);
        Assert.Contains("more than once (rows 2, 3)", Assert.Throws<ProfileException>(() => FieldSpecReader.ReadCsv("x.csv", "path\na\na")).Message);
        Assert.Contains("mixes JSON paths", Assert.Throws<ProfileException>(() => FieldSpecReader.ReadCsv("x.csv", "path\n$.a\n/B")).Message);

        var unknown = FieldSpecReader.ReadCsv("x.csv", "path,type\na,blob");
        Assert.Contains(unknown.Findings, f => f.Kind == ProfileFindingKind.SchemaSimplified && f.Message.Contains("'blob'"));
    }

    [Fact]
    public void Excel_field_spec_uses_the_sheet_with_a_path_column()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Cover").Cell(1, 1).Value = "Interface specification";
        var sheet = workbook.AddWorksheet("Fields");
        sheet.Cell(1, 1).Value = "JSON Path";
        sheet.Cell(1, 2).Value = "Type";
        sheet.Cell(1, 3).Value = "Required";
        sheet.Cell(1, 4).Value = "Max Length";
        sheet.Cell(2, 1).Value = "$.merchant.legalName";
        sheet.Cell(2, 2).Value = "string";
        sheet.Cell(2, 3).Value = "Yes";
        sheet.Cell(2, 4).Value = 60;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var doc = FieldSpecWorkbook.Read("spec.xlsx", stream.ToArray());

        var name = Field(doc, "$.merchant.legalName");
        Assert.Equal((FieldDataType.String, Requirement.Required, 60), (name.DataType, name.Required, name.MaxLength));
        Assert.Equal(FieldNodeKind.Object, Field(doc, "$.merchant").Kind);
        Assert.Contains("not a readable .xlsx", Assert.Throws<ProfileException>(() => FieldSpecWorkbook.Read("bad.xlsx", [1, 2, 3])).Message);
    }

    [Theory]
    [InlineData("a.xsd", "<x/>", InputKind.Xsd)]
    [InlineData("a.wsdl", "<x/>", InputKind.Wsdl)]
    [InlineData("a.csv", "path", InputKind.FieldSpec)]
    [InlineData("a.xml", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>", InputKind.Xsd)]
    [InlineData("a.xml", "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>", InputKind.Wsdl)]
    [InlineData("a.json", "{\"openapi\":\"3.1.0\"}", InputKind.OpenApi)]
    [InlineData("a.json", "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\"}", InputKind.JsonSchema)]
    [InlineData("a.json", "{\"type\":\"object\",\"properties\":{}}", InputKind.JsonSchema)]
    [InlineData("a.json", "{\"type\":\"object\",\"properties\":\"x\"}", null)]
    [InlineData("a.json", "{\"legalName\":\"Acme\"}", null)]
    [InlineData("a.json", "[{\"a\":1}]", null)]
    [InlineData("a.xml", "<Request><Id>1</Id></Request>", null)]
    [InlineData("a.xml", "<broken", null)]
    public void Contracts_are_told_apart_from_samples(string name, string content, InputKind? expected) =>
        Assert.Equal(expected, ContractReader.Detect(name, content));

    private const string MergeSchema = """
        {
          "type": "object",
          "required": ["id", "status", "count", "taxId"],
          "properties": {
            "id": { "type": "string", "maxLength": 3 },
            "status": { "type": "string", "enum": ["NEW", "DONE"] },
            "count": { "type": "string", "description": "Declared as text." },
            "opened": { "type": "string" },
            "flag": { "type": "boolean" },
            "taxId": { "type": "string" },
            "missing": { "type": "string" },
            "tags": { "type": "string" }
          }
        }
        """;

    [Fact]
    public void Contracts_take_precedence_and_disagreements_become_findings()
    {
        var schema = JsonSchemaReader.Read("app.schema.json", MergeSchema);
        var profile = Build([schema],
            ("a.json", """{ "id": "A-1001", "status": "OPEN", "count": 3, "opened": "2026-01-31", "flag": "yes", "taxId": "12-3456789", "tags": ["x"], "extra": 1 }"""),
            ("b.json", """{ "id": "A-2", "status": "NEW", "count": 4, "opened": "2026-02-01", "flag": "no", "taxId": "98-7654321", "tags": ["y"] }"""));

        Assert.Empty(SystemProfileValidator.Validate(profile));
        Assert.Equal(["a.json", "b.json", "app.schema.json"], profile.Inputs.Select(i => i.Name));
        Assert.Equal(InputKind.JsonSchema, profile.Inputs[2].Kind);

        var id = Field(profile, "$.id");
        Assert.Equal((Requirement.Required, 3), (id.Required, id.MaxLength));
        Assert.Equal(["a.json", "b.json", "app.schema.json"], id.SeenIn);
        Assert.Equal(InputKind.JsonSchema, id.Provenance.Single(p => p.Attribute == ProfileAttribute.Required).Kind);
        Assert.NotNull(id.Presence);

        var count = Field(profile, "$.count");
        Assert.Equal((FieldDataType.String, "Declared as text."), (count.DataType, count.Description));

        var opened = Field(profile, "$.opened");
        Assert.Equal((FieldDataType.Date, "yyyy-MM-dd"), (opened.DataType, opened.Format));
        Assert.Equal(InputKind.SamplePayload, opened.Provenance.Single(p => p.Attribute == ProfileAttribute.DataType).Kind);

        var taxId = Field(profile, "$.taxId");
        Assert.True(taxId.Sensitive);
        Assert.DoesNotContain(profile.Fields.SelectMany(f => f.ObservedValues.Append(f.SampleValue ?? "")), v => v.Contains("3456789", StringComparison.Ordinal));

        var missing = Field(profile, "$.missing");
        Assert.Equal((Requirement.Optional, (Presence?)null), (missing.Required, missing.Presence));
        Assert.Equal(["app.schema.json"], missing.SeenIn);
        Assert.Equal(Field(profile, "$.extra").Path, profile.Fields[^1].Path);

        string? Finding(ProfileFindingKind kind, string path) => profile.Findings.SingleOrDefault(f => f.Kind == kind && f.Path == path)?.Message;
        Assert.Contains("up to 6 characters", Finding(ProfileFindingKind.ContractMismatch, "$.id"));
        Assert.Contains("OPEN", Finding(ProfileFindingKind.ContractMismatch, "$.status"));
        Assert.Contains("declares a boolean", Finding(ProfileFindingKind.TypeConflict, "$.flag"));
        Assert.Contains("repeat", Finding(ProfileFindingKind.ContractMismatch, "$.tags"));
        Assert.Contains("not declared in app.schema.json", Finding(ProfileFindingKind.UndeclaredField, "$.extra"));
        Assert.Null(Finding(ProfileFindingKind.TypeConflict, "$.count"));
    }

    [Fact]
    public void Required_contract_fields_absent_from_samples_are_reported()
    {
        var spec = FieldSpecReader.ReadCsv("spec.csv", "path,required,format\nid,Y,\nopened,,\nclosed,N,");
        var profile = Build([spec], ("a.json", """{ "opened": "01/31/2026" }"""));

        Assert.Contains(profile.Findings, f => f.Kind == ProfileFindingKind.ContractMismatch && f.Path == "$.id" && f.Message.Contains("no sample contains it"));
        var opened = Field(profile, "$.opened");
        Assert.Equal((FieldDataType.Date, "MM/dd/yyyy", Requirement.LikelyRequired), (opened.DataType, opened.Format, opened.Required));
        Assert.Equal(InputKind.SamplePayload, opened.Provenance.Single(p => p.Attribute == ProfileAttribute.Required).Kind);
        Assert.Equal(Requirement.Optional, Field(profile, "$.closed").Required);
    }

    [Fact]
    public void Contracts_alone_build_a_profile_and_formats_must_agree()
    {
        var profile = Build([XsdReader.Read("request.xsd", Xsd)]);
        Assert.Empty(SystemProfileValidator.Validate(profile));
        Assert.Equal(("request.xsd", "Profiled element Request."), (profile.Inputs.Single().Name, profile.Inputs.Single().Notes));
        Assert.All(profile.Fields, f => Assert.Null(f.Presence));

        var mixed = Assert.Throws<ProfileException>(() => Build([XsdReader.Read("request.xsd", Xsd)], ("a.json", "{}")));
        Assert.Contains("share a format", mixed.Message);
        Assert.Contains("used more than once", Assert.Throws<ProfileException>(() => Build([FieldSpecReader.ReadCsv("a.json", "path\nx")], ("a.json", "{}"))).Message);
    }

    [Fact]
    public void Committed_sample_contracts_agree_with_their_samples()
    {
        string[] Files(string system, string folder) => [.. Directory.GetFiles(Path.Combine(SystemDir(system), folder)).Order(StringComparer.Ordinal)];
        SampleInput[] Samples(string system) => [.. Files(system, "samples").Select(f => new SampleInput(Path.GetFileName(f), File.ReadAllText(f)))];
        ContractDocument Read(string file) => ContractReader.Read(Path.GetFileName(file), File.ReadAllText(file),
            ContractReader.Detect(Path.GetFileName(file), File.ReadAllText(file))!.Value, "submitApplication");

        var sales = ProfileBuilder.Build(new() { System = "SalesAlpha CRM", Samples = Samples("sales-alpha"), Contracts = [.. Files("sales-alpha", "contracts").Select(Read)] });
        var salesSamplesOnly = ProfileSerializer.Load(Path.Combine(SystemDir("sales-alpha"), "profile.json"));
        Assert.Empty(SystemProfileValidator.Validate(sales));
        Assert.Empty(sales.Findings);
        Assert.Equal(salesSamplesOnly.Fields.Select(f => f.Path).Order(StringComparer.Ordinal), sales.Fields.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["CORP", "LLC", "PARTNERSHIP", "SOLE_PROP", "NON_PROFIT"], Field(sales, "$.account.entityType").AllowedValues);
        Assert.Equal(Requirement.Optional, Field(sales, "$.account.dbaName").Required);
        Assert.Equal(6, sales.Inputs.Count);

        var uwContracts = Files("uw-core", "contracts").Select(f => ContractReader.Read(Path.GetFileName(f), File.ReadAllText(f), ContractReader.Detect(f, File.ReadAllText(f))!.Value)).ToList();
        var uw = ProfileBuilder.Build(new() { System = "UW Core", Samples = Samples("uw-core"), Contracts = uwContracts });
        Assert.Empty(SystemProfileValidator.Validate(uw));
        Assert.DoesNotContain(uw.Findings, f => f.Kind is ProfileFindingKind.ContractMismatch or ProfileFindingKind.TypeConflict or ProfileFindingKind.UndeclaredField);
        Assert.Equal(Requirement.Optional, Field(uw, "/UnderwritingRequest/Merchant/EstablishedDate").Required);
        Assert.Equal((FieldDataType.Date, "MM/dd/yyyy"), (Field(uw, "/UnderwritingRequest/Merchant/EstablishedDate").DataType, Field(uw, "/UnderwritingRequest/Merchant/EstablishedDate").Format));
        Assert.Equal(10, Field(uw, "/UnderwritingRequest/Officers/Officer").MaxOccurs);
        Assert.Equal(uwContracts[0].Fields.Select(f => f.Path), uwContracts[1].Fields.Select(f => f.Path));
    }

    [Fact]
    public void Profile_command_reads_contracts_next_to_samples()
    {
        var dir = Directory.CreateTempSubdirectory("mapwright-contracts-").FullName;
        try
        {
            var outPath = Path.Combine(dir, "profile.json");
            var output = new StringWriter();
            var errors = new StringWriter();

            var code = CliApp.Run(["profile", Path.Combine(SystemDir("sales-alpha"), "samples"), Path.Combine(SystemDir("sales-alpha"), "contracts"),
                "--system", "SalesAlpha CRM", "--root", "submitApplication", "--out", outPath], output, errors);

            Assert.True(code == CliApp.Success, errors.ToString());
            Assert.Contains("from 3 JSON sample(s) and 3 JSON contract(s)", output.ToString());
            var profile = ProfileSerializer.Load(outPath);
            Assert.Equal([InputKind.SamplePayload, InputKind.SamplePayload, InputKind.SamplePayload, InputKind.OpenApi, InputKind.JsonSchema, InputKind.FieldSpec],
                profile.Inputs.Select(i => i.Kind));

            errors.GetStringBuilder().Clear();
            Assert.Equal(CliApp.InvalidInput, CliApp.Run(["profile", Path.Combine(SystemDir("sales-alpha"), "contracts"), "--system", "S"], output, errors));
            Assert.Contains("choose one with --root", errors.ToString());

            var xsdOut = Path.Combine(dir, "uw.json");
            Assert.Equal(CliApp.Success, CliApp.Run(["profile", Path.Combine(SystemDir("uw-core"), "contracts", "uw-core-intake.xsd"), "--system", "UW", "--out", xsdOut], output, errors));
            Assert.Contains("from 1 XML contract(s)", output.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
