using System.Text.Json;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Reads the JSON request body of one OpenAPI 3.x or Swagger 2.0 operation, or one named schema, into profile fields.
/// </summary>
public static class OpenApiReader
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <param name="root">An operationId, "METHOD /path" or a schema name; optional when only one operation has a JSON request body.</param>
    public static ContractDocument Read(string name, string content, string? root = null)
    {
        using var document = JsonSchemaReader.Parse(name, content, "OpenAPI document");
        var spec = document.RootElement;
        if (spec.ValueKind != JsonValueKind.Object || (JsonSchemaReader.Text(spec, "openapi") is null && JsonSchemaReader.Text(spec, "swagger") is null))
        {
            throw new ProfileException($"'{name}' is not an OpenAPI document (no 'openapi' or 'swagger' version).");
        }

        var builder = new ContractBuilder(name, InputKind.OpenApi, PayloadFormat.Json);
        var walker = new JsonSchemaReader.Walker(spec, builder);
        var operations = Operations(spec, walker).ToList();
        var schemas = Schemas(spec);

        (string Label, JsonElement Schema) selected;
        if (root is not null)
        {
            var match = operations.FirstOrDefault(o =>
                string.Equals(o.Id, root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Route, root, StringComparison.OrdinalIgnoreCase));
            if (match.Route is not null)
            {
                selected = ($"{match.Route} request body", match.Schema);
            }
            else if (schemas.FirstOrDefault(s => string.Equals(s.Name, root, StringComparison.Ordinal)) is { Name: not null } schema)
            {
                selected = ($"schema {schema.Name}", schema.Schema);
            }
            else
            {
                throw new ProfileException(
                    $"'{name}' has no operation or schema '{root}'. Operations with a JSON request body: {Describe(operations)}.");
            }
        }
        else if (operations.Count == 1)
        {
            selected = ($"{operations[0].Route} request body", operations[0].Schema);
        }
        else
        {
            throw new ProfileException(operations.Count == 0
                ? $"'{name}' has no operation with a JSON request body; choose a schema with --root."
                : $"'{name}' has several operations with a JSON request body; choose one with --root: {Describe(operations)}.");
        }

        walker.Root(selected.Schema);
        return builder.Build(ContractDocument.Hash(content), selected.Label);
    }

    private static string Describe(IEnumerable<(string Route, string? Id, JsonElement Schema)> operations) =>
        string.Join(", ", operations.Select(o => o.Id is null ? o.Route : $"{o.Route} ({o.Id})")) is { Length: > 0 } list ? list : "none";

    private static IEnumerable<(string Route, string? Id, JsonElement Schema)> Operations(JsonElement spec, JsonSchemaReader.Walker walker)
    {
        if (!spec.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var path in paths.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
        {
            foreach (var method in path.Value.EnumerateObject().Where(m => Methods.Contains(m.Name) && m.Value.ValueKind == JsonValueKind.Object))
            {
                if (RequestSchema(method.Value, walker) is { } schema)
                {
                    yield return ($"{method.Name.ToUpperInvariant()} {path.Name}", JsonSchemaReader.Text(method.Value, "operationId"), schema);
                }
            }
        }
    }

    private static JsonElement? RequestSchema(JsonElement operation, JsonSchemaReader.Walker walker)
    {
        if (operation.TryGetProperty("requestBody", out var body)
            && walker.TryResolve(body, "requestBody", out var resolved, out _)
            && resolved.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object)
        {
            return content.EnumerateObject()
                .Where(c => c.Name.Contains("json", StringComparison.OrdinalIgnoreCase) && c.Value.TryGetProperty("schema", out _))
                .Select(c => (JsonElement?)c.Value.GetProperty("schema"))
                .FirstOrDefault();
        }

        if (operation.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            return parameters.EnumerateArray()
                .Where(p => JsonSchemaReader.Text(p, "in") == "body" && p.TryGetProperty("schema", out _))
                .Select(p => (JsonElement?)p.GetProperty("schema"))
                .FirstOrDefault();
        }

        return null;
    }

    private static List<(string Name, JsonElement Schema)> Schemas(JsonElement spec)
    {
        var holder = spec.TryGetProperty("components", out var components) && components.TryGetProperty("schemas", out var s) ? s
            : spec.TryGetProperty("definitions", out var d) ? d
            : default;
        return holder.ValueKind == JsonValueKind.Object ? [.. holder.EnumerateObject().Select(p => (p.Name, p.Value))] : [];
    }
}
