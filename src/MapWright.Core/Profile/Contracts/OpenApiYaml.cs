using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapWright.Core.Spec;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Converts an OpenAPI document written in YAML to JSON. Plain scalars follow the YAML core schema (null,
/// true/false, numbers); everything else is a string. Anchors, aliases and <c>&lt;&lt;</c> merge keys are resolved.
/// </summary>
internal static partial class OpenApiYaml
{
    private const int MaxDepth = 256;

    public static string ToJson(string name, string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = ex.Message.Contains("): ", StringComparison.Ordinal) ? ex.Message.Split("): ", 2)[1] : ex.Message;
            throw new ProfileException($"OpenAPI document '{name}' is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {message}", ex);
        }

        if (stream.Documents.Count != 1)
        {
            throw new ProfileException(stream.Documents.Count == 0
                ? $"OpenAPI document '{name}' is empty."
                : $"OpenAPI document '{name}' holds {stream.Documents.Count} YAML documents; keep one per file.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(name, writer, stream.Documents[0].RootNode, 0);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(string name, Utf8JsonWriter writer, YamlNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ProfileException($"OpenAPI document '{name}' nests deeper than {MaxDepth} levels (line {node.Start.Line}); a YAML alias may refer to itself.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in Entries(name, mapping, depth))
                {
                    writer.WritePropertyName(key);
                    Write(name, writer, value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    Write(name, writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
        }
    }

    /// <summary>The mapping's entries in order, with <c>&lt;&lt;</c> merge keys expanded; explicit keys win.</summary>
    private static List<(string Key, YamlNode Value)> Entries(string name, YamlMappingNode mapping, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ProfileException($"OpenAPI document '{name}' nests deeper than {MaxDepth} levels (line {mapping.Start.Line}); a YAML alias may refer to itself.");
        }

        var entries = new List<(string Key, YamlNode Value)>();
        var merged = new List<(string Key, YamlNode Value)>();
        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: { } text })
            {
                throw new ProfileException($"OpenAPI document '{name}': YAML keys must be plain text (line {key.Start.Line}).");
            }

            if (text == "<<" && key.Tag.IsEmpty && ((YamlScalarNode)key).Style == ScalarStyle.Plain)
            {
                var sources = value is YamlSequenceNode list ? list.Children : [value];
                foreach (var source in sources)
                {
                    if (source is not YamlMappingNode map)
                    {
                        throw new ProfileException($"OpenAPI document '{name}': a '<<' merge key needs a mapping (line {source.Start.Line}).");
                    }

                    merged.AddRange(Entries(name, map, depth + 1));
                }
            }
            else
            {
                entries.Add((text, value));
            }
        }

        var keys = new HashSet<string>(entries.Select(e => e.Key), StringComparer.Ordinal);
        foreach (var entry in merged)
        {
            if (keys.Add(entry.Key))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var typed = scalar.Style == ScalarStyle.Plain && scalar.Tag.IsEmpty;
        if (typed && value is "" or "~" or "null" or "Null" or "NULL")
        {
            writer.WriteNullValue();
        }
        else if (typed && value is "true" or "True" or "TRUE")
        {
            writer.WriteBooleanValue(true);
        }
        else if (typed && value is "false" or "False" or "FALSE")
        {
            writer.WriteBooleanValue(false);
        }
        else if (typed && JsonNumber().IsMatch(value))
        {
            writer.WriteRawValue(value);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();
}
