using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace MapWright.Core.Playbooks;

/// <summary>
/// The YAML form of a playbook: the same structure and property names as the JSON, plus comments. Plain scalars
/// follow the YAML core schema (null, true/false, numbers); anything else is a string, and a number or boolean is
/// accepted where the playbook expects a string, so <c>version: 1.0</c> reads as "1.0".
/// Anchors, aliases and tags other than <c>!!str</c> are not supported.
/// </summary>
public static partial class PlaybookYaml
{
    private static readonly JsonSerializerOptions ReadOptions = new(MapWrightJson.Options) { Converters = { new LenientStringConverter() } };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    public static Playbook Deserialize(string yaml)
    {
        var (json, lines) = Convert(yaml);
        try
        {
            return JsonSerializer.Deserialize<Playbook>(json, ReadOptions) ?? throw new PlaybookException("Playbook is empty (null).");
        }
        catch (JsonException ex)
        {
            var path = ex.Path ?? "$";
            var message = ex.Message.Split(" Path: ", 2)[0];
            throw new PlaybookException($"Invalid playbook at '{path}'{LineOf(lines, path)}: {message}", ex);
        }
    }

    public static string Serialize(Playbook playbook) =>
        Emit(JsonSerializer.SerializeToNode(playbook, MapWrightJson.Options)!);

    /// <summary>Converts playbook JSON text to YAML, keeping its properties and their order as written.</summary>
    public static string FromJson(string json)
    {
        PlaybookSerializer.Deserialize(json);
        var node = JsonNode.Parse(json, NodeOptions, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? throw new PlaybookException("Playbook is empty (null).");
        return Emit(node);
    }

    /// <summary>Converts playbook YAML text to indented JSON, keeping its properties and their order; comments are dropped.</summary>
    public static string ToJson(string yaml)
    {
        var playbook = Deserialize(yaml);
        var json = JsonNode.Parse(Convert(yaml).Json)!.ToJsonString(MapWrightJson.Options);
        try
        {
            PlaybookSerializer.Deserialize(json);
            return json;
        }
        catch (PlaybookException)
        {
            // Unquoted numbers or booleans where the playbook expects text: write the typed form instead.
            return PlaybookSerializer.Serialize(playbook);
        }
    }

    /// <summary>
    /// Applies a change made outside the YAML (a status change, a new version, an added term) to the YAML text,
    /// keeping its comments and layout. Changed values are replaced in place and new list items are appended;
    /// returns null when the change cannot be applied that way, so the YAML is regenerated instead.
    /// </summary>
    public static string? Update(string? yaml, Playbook before, Playbook after)
    {
        if (yaml is null)
        {
            return null;
        }

        var expected = PlaybookSerializer.Serialize(after);
        if (PlaybookSerializer.Serialize(before) == expected)
        {
            return yaml;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            var edits = new List<Edit>();
            var old = JsonSerializer.SerializeToNode(before, MapWrightJson.Options)!;
            var updated = JsonSerializer.SerializeToNode(after, MapWrightJson.Options)!;
            if (stream.Documents.Count != 1 || !Diff(yaml, stream.Documents[0].RootNode, old, updated, edits))
            {
                return null;
            }

            var text = new StringBuilder(yaml);
            foreach (var edit in edits.OrderByDescending(e => e.Index))
            {
                text.Remove(edit.Index, edit.Length).Insert(edit.Index, edit.Text);
            }

            var patched = text.ToString();
            return PlaybookSerializer.Serialize(Deserialize(patched)) == expected ? patched : null;
        }
        catch (Exception ex) when (ex is YamlException or PlaybookException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ YAML → JSON

    private static (string Json, Dictionary<string, Mark> Lines) Convert(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = ex.Message.Contains("): ", StringComparison.Ordinal) ? ex.Message.Split("): ", 2)[1] : ex.Message;
            throw new PlaybookException($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            throw new PlaybookException("Playbook is empty.");
        }

        if (stream.Documents.Count > 1)
        {
            throw new PlaybookException("A playbook file holds one YAML document; remove the extra '---'.");
        }

        var lines = new Dictionary<string, Mark>(StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = MapWrightJson.Options.Encoder }))
        {
            Write(writer, stream.Documents[0].RootNode, "$", lines, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        }

        return (Encoding.UTF8.GetString(buffer.WrittenSpan), lines);
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node, string path, Dictionary<string, Mark> lines, HashSet<YamlNode> seen)
    {
        if (!seen.Add(node))
        {
            throw new PlaybookException($"YAML anchors and aliases are not supported (line {node.Start.Line}).");
        }

        lines.TryAdd(path, node.Start);
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        throw new PlaybookException($"YAML keys must be plain text (line {key.Start.Line}).");
                    }

                    writer.WritePropertyName(name);
                    Write(writer, value, $"{path}.{name}", lines, seen);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    Write(writer, sequence.Children[i], $"{path}[{i}]", lines, seen);
                }

                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var typed = scalar.Style == ScalarStyle.Plain && scalar.Tag.IsEmpty;
        if (!scalar.Tag.IsEmpty && scalar.Tag.Value is not ("tag:yaml.org,2002:str" or "!!str"))
        {
            throw new PlaybookException($"YAML tag '{scalar.Tag.Value}' is not supported (line {scalar.Start.Line}).");
        }

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

    private static string LineOf(Dictionary<string, Mark> lines, string path)
    {
        for (var p = path; p.Length > 0; p = Parent(p))
        {
            if (lines.TryGetValue(p, out var mark) && p != "$")
            {
                return $" (line {mark.Line})";
            }
        }

        return "";
    }

    private static string Parent(string path)
    {
        var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
        return cut <= 0 ? "" : path[..cut];
    }

    // ------------------------------------------------------------------ JSON → YAML

    private static string Emit(JsonNode node)
    {
        var output = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(output, new EmitterSettings(bestIndent: 2, bestWidth: 100_000, isCanonical: false, maxSimpleKeyLength: 1024, indentSequences: true));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        Emit(emitter, node);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return output.ToString();
    }

    private static void Emit(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                emitter.Emit(new MappingStart(null, null, true, obj.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in obj)
                {
                    emitter.Emit(Scalar(key));
                    Emit(emitter, value);
                }

                emitter.Emit(new MappingEnd());
                break;
            case JsonArray array:
                emitter.Emit(new SequenceStart(null, null, true, array.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array)
                {
                    Emit(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                emitter.Emit(Scalar(value.GetValue<string>()));
                break;
            case null:
                emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
                break;
            default:
                emitter.Emit(new Scalar(null, null, node.ToJsonString(), ScalarStyle.Plain, true, false));
                break;
        }
    }

    /// <summary>Text that would read back as something else (a number, date, boolean or null) is quoted.</summary>
    private static Scalar Scalar(string text)
    {
        var style = text.Contains('\n', StringComparison.Ordinal) ? ScalarStyle.Literal
            : LooksTyped().IsMatch(text) ? ScalarStyle.DoubleQuoted
            : ScalarStyle.Any;
        return new Scalar(null, null, text, style, true, true);
    }

    /// <summary>A scalar on one line, e.g. <c>published</c> or <c>"1.1.0"</c>, or null when it needs several lines.</summary>
    private static string? Inline(JsonNode node)
    {
        var yaml = Emit(new JsonObject { ["k"] = node.DeepClone() });
        return yaml.StartsWith("k: ", StringComparison.Ordinal) && yaml.IndexOf('\n') == yaml.Length - 1 && !yaml.StartsWith("k: |", StringComparison.Ordinal)
            && !yaml.StartsWith("k: >", StringComparison.Ordinal)
            ? yaml[3..^1]
            : null;
    }

    // ------------------------------------------------------------------ in-place updates

    private sealed record Edit(int Index, int Length, string Text);

    private static bool Diff(string yaml, YamlNode node, JsonNode? old, JsonNode? updated, List<Edit> edits)
    {
        if (JsonNode.DeepEquals(old, updated))
        {
            return true;
        }

        var mark = edits.Count;
        var applied = (node, old, updated) switch
        {
            (YamlMappingNode mapping, JsonObject from, JsonObject to) => DiffMapping(yaml, mapping, from, to, edits),
            (YamlSequenceNode sequence, JsonArray from, JsonArray to) => DiffSequence(yaml, sequence, from, to, edits),
            (YamlScalarNode { Style: not (ScalarStyle.Literal or ScalarStyle.Folded) } scalar, JsonValue, JsonValue value) when Inline(value) is { } text =>
                Add(edits, new((int)scalar.Start.Index, (int)(scalar.End.Index - scalar.Start.Index), text)),
            _ => false,
        };
        if (!applied)
        {
            edits.RemoveRange(mark, edits.Count - mark);
        }

        return applied;
    }

    private static bool Add(List<Edit> edits, Edit edit)
    {
        edits.Add(edit);
        return true;
    }

    /// <summary>
    /// Changed values are edited in place where possible and otherwise rewritten with their key; removed keys lose
    /// their lines and new keys go after the last one. Keys the YAML leaves out (defaults, computed values such as
    /// the reference) are checked when the result is read back.
    /// </summary>
    private static bool DiffMapping(string yaml, YamlMappingNode mapping, JsonObject from, JsonObject to, List<Edit> edits)
    {
        var block = mapping.Style != MappingStyle.Flow;
        var children = new Dictionary<string, (YamlNode Key, YamlNode Value)>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
            {
                children[name] = (key, value);
            }
        }

        var removed = from.Select(p => p.Key).Where(k => !to.ContainsKey(k) && children.ContainsKey(k)).ToHashSet(StringComparer.Ordinal);
        var added = new JsonObject();
        foreach (var (name, value) in to)
        {
            if (from.TryGetPropertyValue(name, out var before) && JsonNode.DeepEquals(before, value))
            {
                continue;
            }

            if (!children.TryGetValue(name, out var child))
            {
                added[name] = value?.DeepClone();
            }
            else if (!Diff(yaml, child.Value, before, value, edits) && !(block && ReplaceEntry(yaml, child.Key, child.Value, name, value, edits)))
            {
                return false;
            }
        }

        if (removed.Count == 0 && added.Count == 0)
        {
            return true;
        }

        var kept = mapping.Children.Where(c => c.Key is not YamlScalarNode { Value: { } k } || !removed.Contains(k)).ToList();
        if (!block || kept.Count == 0)
        {
            return false;
        }

        foreach (var name in removed)
        {
            var (key, value) = children[name];
            var at = (int)key.Start.Index;
            if (!StartsLine(yaml, at))
            {
                return false;
            }

            var start = LineStart(yaml, at);
            edits.Add(new(start, NextLine(yaml, ContentEnd(yaml, key, value)) - start, ""));
        }

        if (added.Count > 0)
        {
            var first = (int)mapping.Children[0].Key.Start.Index;
            var last = kept[^1];
            var lineEnd = NextLine(yaml, ContentEnd(yaml, last.Key, last.Value));
            var at = lineEnd > 0 && yaml[lineEnd - 1] == '\n' ? lineEnd - 1 : lineEnd;
            edits.Add(new(at, 0, "\n" + Indented(Emit(added), first - LineStart(yaml, first), indentFirst: true)));
        }

        return true;
    }

    /// <summary>Items are matched by value, so an item added or removed in the middle leaves the others' comments.</summary>
    private static bool DiffSequence(string yaml, YamlSequenceNode sequence, JsonArray from, JsonArray to, List<Edit> edits)
    {
        if (sequence.Children.Count != from.Count)
        {
            return false;
        }

        if (sequence.Style == SequenceStyle.Flow || from.Count == 0 || to.Count == 0)
        {
            return from.Count == to.Count && from.Count > 0 && Enumerable.Range(0, from.Count).All(i => Diff(yaml, sequence.Children[i], from[i], to[i], edits));
        }

        var anchors = Common(from, to);
        anchors.Add((from.Count, to.Count));
        var (f, t) = (0, 0);
        foreach (var (anchorFrom, anchorTo) in anchors)
        {
            var paired = Math.Min(anchorFrom - f, anchorTo - t);
            for (var k = 0; k < paired; k++, f++, t++)
            {
                if (!Diff(yaml, sequence.Children[f], from[f], to[t], edits) && !ReplaceItem(yaml, sequence.Children[f], to[t], edits))
                {
                    return false;
                }
            }

            for (; f < anchorFrom; f++)
            {
                var dash = Dash(yaml, sequence.Children[f]);
                if (dash < 0 || !StartsLine(yaml, dash))
                {
                    return false;
                }

                var start = LineStart(yaml, dash);
                edits.Add(new(start, NextLine(yaml, ContentEnd(yaml, sequence.Children[f])) - start, ""));
            }

            if (t < anchorTo)
            {
                var items = Emit(new JsonArray([.. to.Skip(t).Take(anchorTo - t).Select(i => i?.DeepClone())]));
                t = anchorTo;
                if (anchorFrom < from.Count)
                {
                    var dash = Dash(yaml, sequence.Children[anchorFrom]);
                    if (dash < 0 || !StartsLine(yaml, dash))
                    {
                        return false;
                    }

                    edits.Add(new(LineStart(yaml, dash), 0, Indented(items, dash - LineStart(yaml, dash), indentFirst: true) + "\n"));
                }
                else
                {
                    var lastDash = Dash(yaml, sequence.Children[^1]);
                    if (lastDash < 0)
                    {
                        return false;
                    }

                    var lineEnd = NextLine(yaml, ContentEnd(yaml, sequence.Children[^1]));
                    var at = lineEnd > 0 && yaml[lineEnd - 1] == '\n' ? lineEnd - 1 : lineEnd;
                    edits.Add(new(at, 0, "\n" + Indented(items, lastDash - LineStart(yaml, lastDash), indentFirst: true)));
                }
            }

            (f, t) = (anchorFrom + 1, anchorTo + 1);
        }

        return true;
    }

    /// <summary>Index pairs of the longest run of items that are equal in both lists, in order.</summary>
    private static List<(int From, int To)> Common(JsonArray from, JsonArray to)
    {
        var lengths = new int[from.Count + 1, to.Count + 1];
        for (var i = from.Count - 1; i >= 0; i--)
        {
            for (var j = to.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = JsonNode.DeepEquals(from[i], to[j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        for (int i = 0, j = 0; i < from.Count && j < to.Count;)
        {
            if (JsonNode.DeepEquals(from[i], to[j]))
            {
                pairs.Add((i++, j++));
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return pairs;
    }

    /// <summary>Rewrites <c>key: value</c> in place; comments inside the old value are lost, the rest are kept.</summary>
    private static bool ReplaceEntry(string yaml, YamlNode key, YamlNode value, string name, JsonNode? node, List<Edit> edits)
    {
        var start = (int)key.Start.Index;
        var text = Indented(Emit(new JsonObject { [name] = node?.DeepClone() }), start - LineStart(yaml, start), indentFirst: false);
        edits.Add(new(start, ContentEnd(yaml, key, value) - start, text));
        return true;
    }

    private static bool ReplaceItem(string yaml, YamlNode item, JsonNode? node, List<Edit> edits)
    {
        var dash = Dash(yaml, item);
        if (dash < 0)
        {
            return false;
        }

        var text = Indented(Emit(new JsonArray(node?.DeepClone())), dash - LineStart(yaml, dash), indentFirst: false);
        edits.Add(new(dash, ContentEnd(yaml, item) - dash, text));
        return true;
    }

    private static string Indented(string rendered, int indent, bool indentFirst)
    {
        var pad = new string(' ', indent);
        var lines = rendered.TrimEnd('\n').Split('\n').Select((l, i) => l.Length == 0 || (i == 0 && !indentFirst) ? l : pad + l);
        return string.Join('\n', lines);
    }

    /// <summary>Where the item's <c>-</c> is, or -1 when it has none (a flow sequence).</summary>
    private static int Dash(string yaml, YamlNode item)
    {
        var i = (int)item.Start.Index - 1;
        while (i >= 0 && yaml[i] is ' ' or '\t')
        {
            i--;
        }

        return i >= 0 && yaml[i] == '-' ? i : -1;
    }

    private static int LineStart(string yaml, int index) => index == 0 ? 0 : yaml.LastIndexOf('\n', index - 1) + 1;

    private static bool StartsLine(string yaml, int index) => yaml.AsSpan(LineStart(yaml, index), index - LineStart(yaml, index)).IsWhiteSpace();

    /// <summary>The start of the line after the one holding <paramref name="index"/>.</summary>
    private static int NextLine(string yaml, int index)
    {
        var lineEnd = yaml.IndexOf('\n', index);
        return lineEnd < 0 ? yaml.Length : lineEnd + 1;
    }

    /// <summary>Just past the last character of the nodes, without trailing whitespace such as a block scalar's newlines.</summary>
    private static int ContentEnd(string yaml, params YamlNode[] nodes)
    {
        var start = (int)nodes[0].Start.Index;
        var end = (int)nodes.Max(End);
        while (end > start + 1 && char.IsWhiteSpace(yaml[end - 1]))
        {
            end--;
        }

        return end;
    }

    private static long End(YamlNode node) => node switch
    {
        YamlMappingNode mapping => mapping.Children.Select(c => Math.Max(End(c.Key), End(c.Value))).DefaultIfEmpty(node.End.Index).Max(),
        YamlSequenceNode sequence => sequence.Children.Select(End).DefaultIfEmpty(node.End.Index).Max(),
        _ => node.End.Index,
    };

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    /// <summary>Empty, null/boolean words (YAML 1.1 and 1.2), or starting like a number or date.</summary>
    [GeneratedRegex(@"^($|~|null$|true$|false$|yes$|no$|on$|off$|y$|n$|[-+.]?[0-9]|[-+]?\.(inf|nan)$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooksTyped();

    /// <summary>Reads an unquoted YAML number or boolean into a text property, keeping the text as written.</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Expected text but found {reader.TokenType}."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
