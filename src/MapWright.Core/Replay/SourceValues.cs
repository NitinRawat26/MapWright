using System.Text.RegularExpressions;
using MapWright.Core.Profile.Samples;

namespace MapWright.Core.Replay;

/// <summary>
/// One scalar in a payload: its profile-style path (<c>$.owners[*].ssn</c>, <c>/Request/Officer/SSN</c>) and its
/// position in each repeating list above it, outermost first.
/// </summary>
public sealed record SourceValue(string Path, IReadOnlyList<int> Indexes, string? Value, ScalarKind Kind);

/// <summary>Flattens a sample payload into values keyed by the same paths the system profile uses.</summary>
public static partial class SourceValues
{
    public static ILookup<string, SourceValue> Read(SampleDocument document)
    {
        var values = new List<SourceValue>();
        var xml = document.Format == Spec.PayloadFormat.Xml;
        var root = document.Root;
        Visit(root, xml ? "/" + root.Name : JsonSampleReader.RootName, [], xml, values);
        return values.ToLookup(v => v.Path, StringComparer.Ordinal);
    }

    private static void Visit(SampleNode node, string path, IReadOnlyList<int> indexes, bool xml, List<SourceValue> values)
    {
        switch (node.Type)
        {
            case SampleNodeType.Object:
                foreach (var child in node.Children)
                {
                    Visit(child, ChildPath(path, child, xml), indexes, xml, values);
                }

                break;
            case SampleNodeType.Array:
                var itemPath = xml ? path : path + "[*]";
                for (var i = 0; i < node.Children.Count; i++)
                {
                    Visit(node.Children[i], itemPath, [.. indexes, i], xml, values);
                }

                break;
            default:
                values.Add(new(path, indexes, node.Value, node.Scalar));
                break;
        }
    }

    private static string ChildPath(string prefix, SampleNode child, bool xml)
    {
        if (xml)
        {
            return child.IsAttribute ? $"{prefix}/@{child.Name}" : $"{prefix}/{child.Name}";
        }

        return SimpleJsonName().IsMatch(child.Name)
            ? $"{prefix}.{child.Name}"
            : $"{prefix}['{child.Name.Replace("'", "\\'", StringComparison.Ordinal)}']";
    }

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex SimpleJsonName();
}
