using System.Text.Json;
using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// The other files uploaded with a contract, so its external <c>$ref</c>, <c>xs:import</c> and <c>xs:include</c>
/// references can be read from them. Nothing is fetched from disk or the network.
/// </summary>
public sealed class ContractFiles
{
    private readonly List<(string Name, string Content)> _files;

    public ContractFiles(IEnumerable<(string Name, string Content)> files) => _files = [.. files];

    public static ContractFiles None { get; } = new([]);

    /// <summary>Names of the files another contract refers to; they are read as part of that contract.</summary>
    public IReadOnlySet<string> Referenced()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, content) in _files)
        {
            if (ContractReader.Detect(name, content) is null)
            {
                continue;
            }

            foreach (var location in Locations(name, content))
            {
                if (Find(name, location, out _) is { } target && !target.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    referenced.Add(target);
                }
            }
        }

        return referenced;
    }

    internal string Content(string name) => _files.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Content;

    /// <summary>
    /// The uploaded file a reference points to: the same relative path, or else the only file with that file name.
    /// </summary>
    internal string? Find(string from, string location, out string? problem)
    {
        problem = null;
        var path = location.Split('#', 2)[0].Split('?', 2)[0];
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
        {
            path = uri.AbsolutePath;
        }

        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        if (path.Length == 0)
        {
            return null;
        }

        var relative = Normalize(Path.Combine(Path.GetDirectoryName(from.Replace('\\', '/')) ?? "", path).Replace('\\', '/'));
        if (_files.FirstOrDefault(f => Normalize(f.Name).Equals(relative, StringComparison.OrdinalIgnoreCase)).Name is { } exact)
        {
            return exact;
        }

        var file = FileName(path);
        var matches = _files.Where(f => FileName(f.Name).Equals(file, StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).ToList();
        if (matches.Count > 1)
        {
            problem = $"Reference '{location}' matches several uploaded files ({string.Join(", ", matches)}); upload only one of them.";
            return null;
        }

        return matches.SingleOrDefault();
    }

    /// <summary>The uploaded XML Schema whose targetNamespace is the one an <c>xs:import</c> without a location names.</summary>
    internal string? FindNamespace(string from, string ns, out string? problem)
    {
        problem = null;
        var matches = _files
            .Where(f => !f.Name.Equals(from, StringComparison.OrdinalIgnoreCase) && TargetNamespace(f.Content) == ns)
            .Select(f => f.Name)
            .ToList();
        if (matches.Count > 1)
        {
            problem = $"xs:import of namespace '{ns}' matches several uploaded schemas ({string.Join(", ", matches)}); give it a schemaLocation.";
            return null;
        }

        return matches.SingleOrDefault();
    }

    internal static ProfileInput Used(string name, string content, InputKind kind, PayloadFormat format, string referrer) => new()
    {
        Name = name,
        Kind = kind,
        Format = format,
        Sha256 = ContractDocument.Hash(content),
        Notes = $"Referenced by {referrer}; read as part of it.",
    };

    private IEnumerable<string> Locations(string name, string content)
    {
        var first = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').FirstOrDefault();
        if (first == '<')
        {
            if (Xml(content) is not { } root)
            {
                yield break;
            }

            foreach (var reference in root.Descendants().Where(e => e.Name.Namespace == XsdReader.Xs && e.Name.LocalName is "import" or "include" or "redefine" or "override"))
            {
                if ((string?)reference.Attribute("schemaLocation") is { Length: > 0 } location)
                {
                    yield return location;
                }
                else if ((string?)reference.Attribute("namespace") is { Length: > 0 } ns && FindNamespace(name, ns, out _) is { } target)
                {
                    yield return target;
                }
            }

            yield break;
        }

        string json;
        try
        {
            json = first == '{' ? content : OpenApiYaml.ToJson(name, content);
        }
        catch (ProfileException)
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonSchemaReader.Parse(name, json, "JSON");
        }
        catch (ProfileException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var reference in JsonSchemaBundle.References(document.RootElement).ToList())
            {
                yield return reference;
            }
        }
    }

    private static XElement? Xml(string content)
    {
        try
        {
            return XsdReader.Load("", content, "XML").Root;
        }
        catch (ProfileException)
        {
            return null;
        }
    }

    private static string? TargetNamespace(string content) =>
        content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<') && Xml(content) is { } root && root.Name == XsdReader.Xs + "schema"
            ? (string?)root.Attribute("targetNamespace")
            : null;

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
