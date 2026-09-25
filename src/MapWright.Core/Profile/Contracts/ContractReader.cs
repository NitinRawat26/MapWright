using System.Text.Json;
using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>Recognises contract inputs (as opposed to sample payloads) and reads them.</summary>
public static class ContractReader
{
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";

    /// <summary>The contract kind of a file, or null when it looks like a sample payload.</summary>
    public static InputKind? Detect(string name, string content)
    {
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".xsd":
                return InputKind.Xsd;
            case ".wsdl":
                return InputKind.Wsdl;
            case ".csv":
                return InputKind.FieldSpec;
            case ".yaml" or ".yml":
                return InputKind.OpenApi;
        }

        var first = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').FirstOrDefault();
        try
        {
            if (first == '<')
            {
                var root = XsdReader.Load(name, content, "XML").Root!;
                return root.Name == XsdReader.Xs + "schema" ? InputKind.Xsd
                    : root.Name == Wsdl11 + "definitions" || root.Name == Wsdl20 + "description" ? InputKind.Wsdl
                    : null;
            }

            if (first == '{')
            {
                using var document = JsonSchemaReader.Parse(name, content, "JSON");
                var root = document.RootElement;
                if (JsonSchemaReader.Text(root, "openapi") is not null || JsonSchemaReader.Text(root, "swagger") is not null)
                {
                    return InputKind.OpenApi;
                }

                return JsonSchemaReader.Text(root, "$schema") is not null
                    || (JsonSchemaReader.Text(root, "type") == "object" && root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                    ? InputKind.JsonSchema
                    : null;
            }
        }
        catch (ProfileException)
        {
            return null;
        }

        return null;
    }

    /// <param name="root">XSD root element, WSDL operation, or OpenAPI operation/schema to profile.</param>
    public static ContractDocument Read(string name, string content, InputKind kind, string? root = null) => kind switch
    {
        InputKind.JsonSchema => JsonSchemaReader.Read(name, content),
        InputKind.OpenApi when content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').FirstOrDefault() != '{' =>
            throw new ProfileException($"OpenAPI document '{name}' must be JSON; YAML is not supported yet, so convert it to JSON first."),
        InputKind.OpenApi => OpenApiReader.Read(name, content, root),
        InputKind.Xsd => XsdReader.Read(name, content, root),
        InputKind.Wsdl => WsdlReader.Read(name, content, root),
        InputKind.FieldSpec => FieldSpecReader.ReadCsv(name, content),
        _ => throw new ProfileException($"'{name}': {kind} inputs are not supported yet."),
    };
}
