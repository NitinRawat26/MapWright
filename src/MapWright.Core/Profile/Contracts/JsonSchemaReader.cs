using System.Globalization;
using System.Text.Json;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Reads a JSON Schema into profile fields: properties, required, arrays, enum/const, formats, length and range
/// limits, local $ref, allOf, and oneOf/anyOf (merged, with their fields optional).
/// </summary>
public static class JsonSchemaReader
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ContractDocument Read(string name, string content)
    {
        using var document = Parse(name, content, "JSON Schema");
        var builder = new ContractBuilder(name, InputKind.JsonSchema, PayloadFormat.Json);
        new Walker(document.RootElement, builder).Root(document.RootElement);
        return builder.Build(ContractDocument.Hash(content), null);
    }

    internal static JsonDocument Parse(string name, string content, string what)
    {
        try
        {
            return JsonDocument.Parse(content, ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new ProfileException($"{what} '{name}' is not valid JSON (line {ex.LineNumber + 1}): {ex.Message}", ex);
        }
    }

    internal static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal sealed class Walker(JsonElement document, ContractBuilder builder)
    {
        private const int MaxDepth = 32;
        private readonly Stack<string> _refs = new();

        public void Root(JsonElement schema)
        {
            var root = builder.Add(JsonPaths.Root, JsonPaths.Root, null);
            root.Required = Requirement.Required;
            root.Details[ProfileAttribute.Required] = "Document root.";
            Node(schema, root, JsonPaths.Root, 0);
        }

        /// <summary>Follows local $ref pointers; returns false when the reference cannot be followed.</summary>
        public bool TryResolve(JsonElement schema, string path, out JsonElement resolved, out string? reference)
        {
            resolved = schema;
            reference = null;
            var hops = 0;
            while (Text(resolved, "$ref") is { } pointer)
            {
                if (++hops > MaxDepth || _refs.Contains(pointer))
                {
                    builder.Finding(ProfileFindingKind.SchemaSimplified, path, $"Recursive reference '{pointer}' was profiled once.");
                    return false;
                }

                if (!pointer.StartsWith('#') || Pointer(pointer) is not { } target)
                {
                    builder.Finding(ProfileFindingKind.UnresolvedReference, path, $"Reference '{pointer}' is not inside '{builder.Input}'; its fields are unknown.");
                    return false;
                }

                reference ??= pointer;
                resolved = target;
            }

            return true;
        }

        public JsonElement? Pointer(string pointer)
        {
            var current = document;
            foreach (var raw in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength())
                {
                    current = current[index];
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        public void Node(JsonElement schema, ContractField field, string prefix, int depth)
        {
            if (!TryResolve(schema, field.Path, out var resolved, out var reference))
            {
                return;
            }

            if (depth > MaxDepth)
            {
                builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, $"Nesting deeper than {MaxDepth} levels was not profiled.");
                return;
            }

            if (reference is not null)
            {
                _refs.Push(reference);
            }

            var parts = new List<JsonElement>();
            var alternatives = false;
            Collect(resolved, field.Path, parts, ref alternatives);
            if (alternatives)
            {
                builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, "oneOf/anyOf alternatives were merged; their fields are optional.");
            }

            Describe(parts, field);
            var type = Type(parts, field.Path);
            if (type == "array")
            {
                field.Cardinality = Cardinality.Array;
                field.Details[ProfileAttribute.Cardinality] = "type 'array'.";
                field.MaxOccurs = Integer(parts, "maxItems");
                var items = parts.Select(p => p.TryGetProperty("items", out var i) ? i : default)
                    .FirstOrDefault(i => i.ValueKind == JsonValueKind.Object);
                if (items.ValueKind != JsonValueKind.Object)
                {
                    field.Details[ProfileAttribute.DataType] = "Array without an items schema.";
                }
                else if (TryResolve(items, field.Path, out var item, out var itemRef))
                {
                    if (itemRef is not null)
                    {
                        _refs.Push(itemRef);
                    }

                    var itemParts = new List<JsonElement>();
                    var itemAlternatives = false;
                    Collect(item, field.Path, itemParts, ref itemAlternatives);
                    alternatives |= itemAlternatives;
                    Describe(itemParts, field);
                    var itemType = Type(itemParts, field.Path);
                    if (itemType == "array")
                    {
                        builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, "Arrays of arrays were not profiled.");
                    }
                    else if (itemType == "object")
                    {
                        Object(itemParts, field, prefix + "[*]", alternatives, depth);
                    }
                    else
                    {
                        Scalar(itemParts, itemType, field);
                    }

                    if (itemRef is not null)
                    {
                        _refs.Pop();
                    }
                }
            }
            else if (type == "object")
            {
                Object(parts, field, prefix, alternatives, depth);
            }
            else
            {
                Scalar(parts, type, field);
            }

            if (reference is not null)
            {
                _refs.Pop();
            }
        }

        private void Collect(JsonElement schema, string path, List<JsonElement> parts, ref bool alternatives)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            parts.Add(schema);
            foreach (var keyword in (string[])["allOf", "oneOf", "anyOf"])
            {
                if (!schema.TryGetProperty(keyword, out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                alternatives |= keyword != "allOf";
                foreach (var sub in list.EnumerateArray())
                {
                    if (TryResolve(sub, path, out var resolved, out _))
                    {
                        Collect(resolved, path, parts, ref alternatives);
                    }
                }
            }
        }

        private void Object(List<JsonElement> parts, ContractField field, string prefix, bool optional, int depth)
        {
            field.MakeObject();
            field.Details.TryAdd(ProfileAttribute.DataType, "type 'object'.");
            var required = parts
                .Where(p => p.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
                .SelectMany(p => p.GetProperty("required").EnumerateArray())
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var part in parts)
            {
                if (!part.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in properties.EnumerateObject())
                {
                    var childPath = JsonPaths.Child(prefix, property.Name);
                    var known = builder.Find(childPath) is not null;
                    var child = builder.Add(childPath, property.Name, field);
                    var isRequired = !optional && required.Contains(property.Name);
                    if (!known || isRequired)
                    {
                        child.Required = isRequired ? Requirement.Required : Requirement.Optional;
                        child.Details[ProfileAttribute.Required] = isRequired
                            ? "Listed in 'required'."
                            : optional ? "Declared in a oneOf/anyOf alternative." : "Not listed in 'required'.";
                    }

                    Node(property.Value, child, childPath, depth + 1);
                }
            }
        }

        private static void Scalar(List<JsonElement> parts, string? type, ContractField field)
        {
            var format = First(parts, p => Text(p, "format"));
            (field.DataType, field.Format) = (type, format) switch
            {
                ("string", "date") => (FieldDataType.Date, "yyyy-MM-dd"),
                ("string", "date-time") => (FieldDataType.DateTime, "ISO 8601"),
                ("string", _) => (FieldDataType.String, null),
                ("integer", _) => (FieldDataType.Integer, null),
                ("number", _) => (FieldDataType.Decimal, null),
                ("boolean", _) => (FieldDataType.Boolean, null),
                _ => (FieldDataType.Unknown, null),
            };
            field.Details[ProfileAttribute.DataType] = type is null ? "No type declared." : $"type '{type}'{(format is null ? "" : $", format '{format}'")}.";
            if (field.Format is not null)
            {
                field.Details[ProfileAttribute.Format] = $"format '{format}'.";
            }

            var values = parts
                .SelectMany(p => p.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray()
                    : p.TryGetProperty("const", out var c) ? [c] : Enumerable.Empty<JsonElement>())
                .Where(v => v.ValueKind != JsonValueKind.Null)
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText())
                .ToList();
            if (values.Count > 0)
            {
                field.AllowedValues = values;
                field.Details[ProfileAttribute.AllowedValues] = "enum/const.";
            }

            field.MinLength ??= Integer(parts, "minLength");
            field.MaxLength ??= Integer(parts, "maxLength");
            field.MinValue ??= Number(parts, "minimum");
            field.MaxValue ??= Number(parts, "maximum");
            if (Number(parts, "multipleOf") is { } step && field.DataType == FieldDataType.Decimal)
            {
                field.MaxScale = step.Scale == 0 ? 0 : (step / 1.000000000000000000000000000m).Scale;
            }
        }

        private void Describe(List<JsonElement> parts, ContractField field) =>
            field.Description ??= First(parts, p => Text(p, "description")) ?? First(parts, p => Text(p, "title"));

        private string? Type(List<JsonElement> parts, string path)
        {
            foreach (var part in parts)
            {
                if (!part.TryGetProperty("type", out var type))
                {
                    continue;
                }

                if (type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString();
                }

                if (type.ValueKind == JsonValueKind.Array)
                {
                    var types = type.EnumerateArray().Select(t => t.GetString()).Where(t => t is not null and not "null").Distinct().ToList();
                    if (types.Count == 1)
                    {
                        return types[0];
                    }

                    builder.Finding(ProfileFindingKind.SchemaSimplified, path, $"Several types allowed ({string.Join(", ", types)}); the type is unknown.");
                    return null;
                }
            }

            return parts.Any(p => p.TryGetProperty("properties", out _)) ? "object"
                : parts.Any(p => p.TryGetProperty("items", out _)) ? "array"
                : null;
        }

        private static T? First<T>(List<JsonElement> parts, Func<JsonElement, T?> read)
            where T : class =>
            parts.Select(read).FirstOrDefault(v => v is not null);

        private static int? Integer(List<JsonElement> parts, string keyword) =>
            Number(parts, keyword) is { } value && value == decimal.Truncate(value) && value is >= 0 and <= int.MaxValue ? (int)value : null;

        private static decimal? Number(List<JsonElement> parts, string keyword) =>
            parts.Select(p => p.TryGetProperty(keyword, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : (decimal?)null)
                .FirstOrDefault(v => v is not null);
    }
}
