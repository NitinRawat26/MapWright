using System.Xml;
using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Samples;

/// <summary>
/// Reads XML samples using local names. Repeated sibling elements become an Array node, attributes become
/// '@name' children and text next to attributes becomes a 'text()' child.
/// </summary>
public static class XmlSampleReader
{
    public const string TextName = "text()";

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly HashSet<string> SoapNamespaces =
    [
        "http://schemas.xmlsoap.org/soap/envelope/",
        "http://www.w3.org/2003/05/soap-envelope",
    ];

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static SampleDocument Read(string name, string content, bool unwrapSoapEnvelope = true)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ProfileException($"Sample '{name}' is not valid XML (line {ex.LineNumber}): {ex.Message}", ex);
        }

        var findings = new List<ProfileFinding>();
        var root = document.Root!;

        if (unwrapSoapEnvelope && root.Name.LocalName == "Envelope" && SoapNamespaces.Contains(root.Name.NamespaceName))
        {
            var body = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")
                ?? throw new ProfileException($"Sample '{name}' is a SOAP envelope without a Body.");
            var payload = body.Elements().FirstOrDefault()
                ?? throw new ProfileException($"Sample '{name}' has an empty SOAP Body.");
            var header = root.Elements().Any(e => e.Name.LocalName == "Header") ? " The SOAP Header was ignored." : "";
            findings.Add(new()
            {
                Kind = ProfileFindingKind.SoapEnvelopeUnwrapped,
                Message = $"Profiled <{payload.Name.LocalName}> from the SOAP Body.{header}",
                Inputs = [name],
            });
            root = payload;
        }

        var namespaces = root.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Append(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None && ns != Xsi)
            .Select(ns => ns.NamespaceName)
            .Distinct()
            .ToList();
        if (namespaces.Count > 0)
        {
            findings.Add(new()
            {
                Kind = ProfileFindingKind.NamespacesIgnored,
                Message = $"Paths use local names; namespace(s) {string.Join(", ", namespaces)} were not included.",
                Inputs = [name],
            });
        }

        return new(name, PayloadFormat.Xml, Convert(root, "/" + root.Name.LocalName, name, findings), findings);
    }

    private static SampleNode Convert(XElement element, string path, string sample, List<ProfileFinding> findings)
    {
        var name = element.Name.LocalName;
        var isNil = (string?)element.Attribute(Xsi + "nil") is "true" or "1";
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Xsi).ToList();
        var elements = element.Elements().ToList();
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();

        if (attributes.Count == 0 && elements.Count == 0)
        {
            return Scalar(name, isNil ? null : text);
        }

        var children = new List<SampleNode>();
        children.AddRange(attributes.Select(a => Scalar(a.Name.LocalName, a.Value.Trim(), isAttribute: true)));

        if (elements.Count == 0)
        {
            children.Add(Scalar(TextName, isNil ? null : text, isText: true));
        }
        else if (text.Length > 0)
        {
            findings.Add(new()
            {
                Kind = ProfileFindingKind.MixedContentIgnored,
                Path = path,
                Message = "Element mixes text and child elements; the text was ignored.",
                Inputs = [sample],
            });
        }

        foreach (var group in elements.GroupBy(e => e.Name.LocalName))
        {
            var childPath = $"{path}/{group.Key}";
            var items = group.Select(e => Convert(e, childPath, sample, findings)).ToList();
            children.Add(items.Count == 1
                ? items[0]
                : new() { Name = group.Key, Type = SampleNodeType.Array, Children = items });
        }

        return new() { Name = name, Type = SampleNodeType.Object, Children = children };
    }

    private static SampleNode Scalar(string name, string? value, bool isAttribute = false, bool isText = false) => new()
    {
        Name = name,
        Type = SampleNodeType.Value,
        Value = value,
        Scalar = value is null ? ScalarKind.Null : ScalarKind.String,
        IsAttribute = isAttribute,
        IsText = isText,
    };
}
