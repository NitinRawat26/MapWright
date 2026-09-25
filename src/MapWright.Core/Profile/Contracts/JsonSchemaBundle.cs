using System.Text.Json;
using System.Text.Json.Nodes;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Copies the uploaded files a JSON Schema or OpenAPI document refers to into the document itself, under
/// <see cref="Holder"/>, and points its external <c>$ref</c>s there, so they resolve like local ones.
/// </summary>
internal static class JsonSchemaBundle
{
    public const string Holder = "x-mapwright-files";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IEnumerable<string> References(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (property.Value.GetString() is { Length: > 0 } reference && !reference.StartsWith('#'))
                        {
                            yield return reference;
                        }
                    }
                    else
                    {
                        foreach (var reference in References(property.Value))
                        {
                            yield return reference;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var reference in element.EnumerateArray().SelectMany(References))
                {
                    yield return reference;
                }

                break;
        }
    }

    /// <summary>The document with its references to uploaded files made local; unchanged when it has none.</summary>
    public static string Bundle(string name, string json, ContractFiles files, ContractBuilder builder, List<ProfileInput> used)
    {
        using (var document = JsonSchemaReader.Parse(name, json, "JSON document"))
        {
            if (!References(document.RootElement).Any())
            {
                return json;
            }
        }

        if (JsonNode.Parse(json, null, ParseOptions) is not JsonObject root)
        {
            return json;
        }

        var holder = new JsonObject();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Target(string reference, string file)
        {
            var hash = reference.IndexOf('#');
            var location = hash < 0 ? reference : reference[..hash];
            var fragment = hash < 0 ? "" : reference[(hash + 1)..];
            var target = file;
            if (location.Length > 0)
            {
                target = files.Find(file, location, out var problem);
                if (problem is not null)
                {
                    builder.Finding(ProfileFindingKind.UnresolvedReference, null, problem);
                }

                if (target is null)
                {
                    return reference;
                }
            }

            if (target.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return "#" + fragment;
            }

            if (!keys.TryGetValue(target, out var key))
            {
                key = target.Replace("%", "%25", StringComparison.Ordinal).Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                keys.Add(target, key);
                var content = files.Content(target);
                var text = OpenApiReader.IsJson(content) ? content : OpenApiYaml.ToJson(target, content);
                JsonSchemaReader.Parse(target, text, "Referenced file").Dispose();
                var node = JsonNode.Parse(text, null, ParseOptions);
                holder[target] = node;
                used.Add(ContractFiles.Used(target, content, InputKind.JsonSchema, PayloadFormat.Json, name));
                Rewrite(node, target);
            }

            return $"#/{Holder}/{key}{fragment}";
        }

        void Rewrite(JsonNode? node, string file)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key == "$ref" && value is JsonValue text && text.TryGetValue<string>(out var reference))
                        {
                            obj[key] = Target(reference, file);
                        }
                        else
                        {
                            Rewrite(value, file);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Rewrite(item, file);
                    }

                    break;
            }
        }

        Rewrite(root, name);
        if (holder.Count == 0)
        {
            return json;
        }

        root[Holder] = holder;
        return root.ToJsonString();
    }

    /// <summary>A bundled pointer as the author wrote it, e.g. <c>common.json#/$defs/Address</c>.</summary>
    public static string Display(string pointer)
    {
        var prefix = $"#/{Holder}/";
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
        {
            return pointer;
        }

        var rest = pointer[prefix.Length..];
        var slash = rest.IndexOf('/');
        var key = slash < 0 ? rest : rest[..slash];
        var file = Uri.UnescapeDataString(key).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return slash < 0 ? file : $"{file}#{rest[slash..]}";
    }
}
