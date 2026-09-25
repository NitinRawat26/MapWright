using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Replay;

public sealed record TargetWriterOptions
{
    /// <summary>Namespace for every XML element. Profiles use local names, so it is not known from the paths.</summary>
    public string? XmlNamespace { get; init; }
}

/// <summary>
/// Builds the target payload from transformed values. Nesting comes from the paths, repeating lists from the target
/// profile (and <c>[*]</c> in JSON paths), and element/property order follows the target profile's field order.
/// </summary>
public static class TargetWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(IReadOnlyList<TargetValue> values, SystemProfile target, TargetWriterOptions? options = null) =>
        target.Format == PayloadFormat.Xml ? WriteXml(values, target, options ?? new()) : WriteJson(values, target);

    public static string WriteJson(IReadOnlyList<TargetValue> values, SystemProfile target)
    {
        var order = Order(target);
        JsonNode? root = null;

        foreach (var value in values)
        {
            var segments = JsonSegments(value.Path);
            if (segments.Count == 0)
            {
                throw new TransformException($"Target path {value.Path} has no field name.");
            }

            root ??= segments[0].Name is null ? new JsonArray() : new JsonObject();
            var node = root;
            var path = JsonSampleRoot;
            var list = 0;

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var last = i == segments.Count - 1;
                path = segment.Name is null ? path + "[*]" : JoinJson(path, segment.Name);
                JsonNode? next() => last ? Leaf(value) : segments[i + 1].Name is null ? new JsonArray() : new JsonObject();

                if (segment.Name is { } name)
                {
                    var obj = node as JsonObject ?? throw new TransformException($"Target path {value.Path} mixes a list and an object at {path}.");
                    if (last || !obj.TryGetPropertyValue(name, out var child) || child is null)
                    {
                        obj.Remove(name);
                        child = next();
                        obj.Insert(InsertAt(obj.Select(p => JoinJson(ParentOf(path), p.Key)), path, order), name, child);
                    }

                    node = child!;
                }
                else
                {
                    var array = node as JsonArray ?? throw new TransformException($"Target path {value.Path} mixes an object and a list at {path}.");
                    var index = list < value.Indexes.Count ? value.Indexes[list] : 0;
                    list++;
                    while (array.Count <= index)
                    {
                        array.Add(last ? null : next());
                    }

                    if (last)
                    {
                        array[index] = Leaf(value);
                    }

                    node = array[index]!;
                }
            }
        }

        return (root ?? new JsonObject()).ToJsonString(Indented);
    }

    public static string WriteXml(IReadOnlyList<TargetValue> values, SystemProfile target, TargetWriterOptions options)
    {
        var order = Order(target);
        var repeating = target.Fields.Where(f => f.Cardinality == Cardinality.Array && f.Kind == FieldNodeKind.Object)
            .Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        XNamespace ns = options.XmlNamespace ?? "";
        XElement? root = null;

        foreach (var value in values)
        {
            var steps = value.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (steps.Length == 0 || steps[0].StartsWith('@'))
            {
                throw new TransformException($"Target path {value.Path} has no root element.");
            }

            root ??= new XElement(ns + steps[0]);
            if (root.Name.LocalName != steps[0])
            {
                throw new TransformException($"Target path {value.Path} does not start at <{root.Name.LocalName}>.");
            }

            var element = root;
            var path = "/" + steps[0];
            var list = 0;

            for (var i = 1; i < steps.Length; i++)
            {
                var step = steps[i];
                var last = i == steps.Length - 1;
                var parentPath = path;
                path = $"{path}/{step}";

                if (step.StartsWith('@'))
                {
                    element.SetAttributeValue(step[1..], value.Value);
                    break;
                }

                if (step == "text()")
                {
                    element.Add(new XText(value.Value));
                    break;
                }

                var index = 0;
                if (repeating.Contains(path) || (last && value.Indexes.Count > list))
                {
                    index = list < value.Indexes.Count ? value.Indexes[list] : 0;
                    list++;
                }

                var existing = element.Elements(ns + step).ToList();
                while (existing.Count <= index)
                {
                    var child = new XElement(ns + step);
                    if (existing.Count > 0)
                    {
                        existing[^1].AddAfterSelf(child);
                    }
                    else
                    {
                        var before = element.Elements().FirstOrDefault(e => Rank(order, $"{parentPath}/{e.Name.LocalName}") > Rank(order, path));
                        if (before is null)
                        {
                            element.Add(child);
                        }
                        else
                        {
                            before.AddBeforeSelf(child);
                        }
                    }

                    existing.Add(child);
                }

                element = existing[index];
                if (last)
                {
                    element.Value = value.Value;
                }
            }
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root ?? new XElement(ns + "Empty"));
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf8StringWriter(builder), new() { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            document.Save(writer);
        }

        return builder.ToString();
    }

    private const string JsonSampleRoot = "$";

    private readonly record struct JsonSegment(string? Name);

    /// <summary>$.owners[*].firstName → owners, [*], firstName. Also accepts $['odd name'].</summary>
    private static List<JsonSegment> JsonSegments(string path)
    {
        if (!path.StartsWith(JsonSampleRoot, StringComparison.Ordinal))
        {
            throw new TransformException($"Target path {path} is not a JSONPath.");
        }

        var segments = new List<JsonSegment>();
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                {
                    end++;
                }

                segments.Add(new(path[(i + 1)..end]));
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("[*]"))
            {
                segments.Add(new(null));
                i += 3;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                var end = i + 2;
                var name = new StringBuilder();
                while (end < path.Length && path[end] != '\'')
                {
                    if (path[end] == '\\' && end + 1 < path.Length)
                    {
                        end++;
                    }

                    name.Append(path[end++]);
                }

                if (!path.AsSpan(end).StartsWith("']"))
                {
                    throw new TransformException($"Target path {path} has an unclosed ['name'].");
                }

                segments.Add(new(name.ToString()));
                i = end + 2;
            }
            else
            {
                throw new TransformException($"Target path {path} is not a supported JSONPath (only .name, ['name'] and [*]).");
            }
        }

        return segments;
    }

    private static string JoinJson(string prefix, string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? $"{prefix}.{name}"
            : $"{prefix}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static string ParentOf(string jsonPath)
    {
        var dot = jsonPath.LastIndexOf('.');
        var bracket = jsonPath.LastIndexOf("['", StringComparison.Ordinal);
        var cut = Math.Max(dot, bracket);
        return cut <= 0 ? JsonSampleRoot : jsonPath[..cut];
    }

    private static JsonNode? Leaf(TargetValue value) => value.DataType switch
    {
        "integer" or "decimal" when decimal.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        "boolean" when bool.TryParse(value.Value, out var b) => JsonValue.Create(b),
        _ => JsonValue.Create(value.Value),
    };

    private static Dictionary<string, int> Order(SystemProfile target) =>
        target.Fields.Select((f, i) => (f.Path, i)).DistinctBy(p => p.Path).ToDictionary(p => p.Path, p => p.i, StringComparer.Ordinal);

    private static int Rank(Dictionary<string, int> order, string path) => order.TryGetValue(path, out var i) ? i : int.MaxValue;

    private static int InsertAt(IEnumerable<string> siblings, string path, Dictionary<string, int> order)
    {
        var rank = Rank(order, path);
        var position = 0;
        foreach (var sibling in siblings)
        {
            if (Rank(order, sibling) > rank)
            {
                return position;
            }

            position++;
        }

        return position;
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
