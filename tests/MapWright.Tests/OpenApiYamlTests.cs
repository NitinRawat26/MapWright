using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using MapWright.Output.Readers;

namespace MapWright.Tests;

public sealed class OpenApiYamlTests
{
    private static string Contract(string file) =>
        Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-alpha", file.EndsWith(".yaml", StringComparison.Ordinal) ? "openapi-yaml" : "contracts", file);

    private static string Fields(ContractDocument document) => JsonSerializer.Serialize(document.Fields);

    private static ProfileField Field(ContractDocument document, string path) =>
        document.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", document.Fields.Select(f => f.Path))}");

    private const string Api = """
        # Anchors, aliases and merge keys, as editors and generators write them.
        openapi: 3.1
        info: { title: Boarding, version: 1.0 }
        paths:
          /applications:
            post:
              operationId: submitApplication
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Application' }
        components:
          schemas:
            Address: &address
              type: object
              required: [line1, postalCode]
              properties:
                line1: { type: string, maxLength: 60 }
                postalCode: { type: string, pattern: '^[0-9]{5}$' }
            Application:
              type: object
              required: [legalName, status]
              properties:
                legalName: { type: string, maxLength: 100 }
                status: { type: string, enum: [NEW, 'NO', 'null'] }
                active: { type: boolean, default: false }
                monthlyVolume: { type: number, minimum: 0, maximum: 1000000 }
                homeAddress: *address
                mailingAddress:
                  <<: *address
                  required: [line1]
        """;

    [Fact]
    public void Reads_a_yaml_document_like_the_same_document_in_json()
    {
        var json = File.ReadAllText(Contract("sales-alpha-api.openapi.json"));
        var yaml = File.ReadAllText(Contract("sales-alpha-api.openapi.yaml"));

        Assert.Equal(InputKind.OpenApi, ContractReader.Detect("sales-alpha-api.openapi.yaml", yaml));
        var fromJson = ContractReader.Read("api", json, InputKind.OpenApi, "submitApplication");
        var fromYaml = ContractReader.Read("api", yaml, InputKind.OpenApi, "submitApplication");

        Assert.NotEmpty(fromYaml.Fields);
        Assert.Equal(Fields(fromJson), Fields(fromYaml));
        Assert.Equal(fromJson.Root, fromYaml.Root);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))), fromYaml.Sha256);
        Assert.Contains("several operations", Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.yaml", yaml)).Message);
    }

    [Fact]
    public void Resolves_anchors_aliases_and_merge_keys_and_keeps_quoted_text_as_text()
    {
        var doc = OpenApiReader.Read("api.yaml", Api);

        Assert.Equal("POST /applications request body", doc.Root);
        Assert.Equal(Requirement.Required, Field(doc, "$.legalName").Required);
        Assert.Equal(100, Field(doc, "$.legalName").MaxLength);
        Assert.Equal(["NEW", "NO", "null"], Field(doc, "$.status").AllowedValues);
        Assert.Equal(FieldDataType.Boolean, Field(doc, "$.active").DataType);
        Assert.Equal(1000000m, Field(doc, "$.monthlyVolume").MaxValue);
        Assert.Equal(60, Field(doc, "$.homeAddress.line1").MaxLength);
        Assert.Equal(Requirement.Required, Field(doc, "$.homeAddress.postalCode").Required);
        Assert.Equal(60, Field(doc, "$.mailingAddress.line1").MaxLength);
        Assert.NotEqual(Requirement.Required, Field(doc, "$.mailingAddress.postalCode").Required);
    }

    [Fact]
    public void Reports_yaml_errors_with_their_line()
    {
        var broken = Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.yaml", "openapi: 3.0.0\npaths:\n  /a: [unclosed\n"));
        Assert.Contains("'api.yaml' is not valid YAML (line", broken.Message);

        var twoDocuments = Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.yaml", "openapi: 3.0.0\n---\nopenapi: 3.0.0\n"));
        Assert.Contains("2 YAML documents", twoDocuments.Message);

        Assert.Contains("not an OpenAPI document", Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.yaml", "title: x\n")).Message);
        Assert.Contains("refer to itself", Assert.Throws<ProfileException>(() => OpenApiReader.Read("api.yml", "openapi: 3.0.0\nx: &a\n  y: *a\n")).Message);
    }

    [Fact]
    public void Builds_a_profile_from_a_yaml_document_and_samples()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-alpha", "samples", "sole-prop.json");
        var set = ProfileInputs.Read(
            [
                new("sales-alpha-api.openapi.yaml", File.ReadAllBytes(Contract("sales-alpha-api.openapi.yaml"))),
                new("sole-prop.json", File.ReadAllBytes(sample)),
            ],
            "submitApplication");

        var contract = Assert.Single(set.Contracts);
        Assert.Equal(InputKind.OpenApi, contract.Kind);
        var profile = ProfileBuilder.Build(new() { System = "SalesAlpha CRM", Samples = set.Samples, Contracts = set.Contracts });
        Assert.Equal(PayloadFormat.Json, profile.Format);
        Assert.DoesNotContain(profile.Findings, f => f.Kind == ProfileFindingKind.UndeclaredField);
    }
}
