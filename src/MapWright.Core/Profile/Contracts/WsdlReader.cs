using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Reads the input message of one WSDL 1.1 or 2.0 operation, using the XML Schema in its types section. A WSDL 1.1
/// RPC-style operation is read as its SOAP body: a wrapper element named after the operation with one child
/// element per message part.
/// </summary>
public static class WsdlReader
{
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";

    /// <param name="operation">The operation to profile; optional when the service has one operation.</param>
    /// <param name="files">Other uploaded files that the types section's <c>xs:import</c> and <c>xs:include</c> may point to.</param>
    public static ContractDocument Read(string name, string content, string? operation = null, ContractFiles? files = null)
    {
        var root = XsdReader.Load(name, content, "WSDL").Root!;
        var ns = root.Name.Namespace;
        if (root.Name != Wsdl11 + "definitions" && root.Name != Wsdl20 + "description")
        {
            throw new ProfileException($"'{name}' is not a WSDL document (expected <wsdl:definitions> or <wsdl:description>).");
        }

        var operations = ns == Wsdl11 ? Wsdl11Operations(root) : Wsdl20Operations(root);
        if (operations.Count == 0)
        {
            throw new ProfileException($"'{name}' has no operation with an input element.");
        }

        var selected = operation is null
            ? operations.Count == 1
                ? operations[0]
                : throw new ProfileException($"'{name}' has several operations; choose one with --root: {string.Join(", ", operations.Select(o => o.Name))}.")
            : operations.FirstOrDefault(o => string.Equals(o.Name, operation, StringComparison.OrdinalIgnoreCase)) is { } match
                ? match
                : throw new ProfileException($"'{name}' has no operation '{operation}'. Operations: {string.Join(", ", operations.Select(o => o.Name))}.");

        var schemas = root.Element(ns + "types")?.Elements(XsdReader.Xs + "schema").ToList() ?? [];
        var builder = new ContractBuilder(name, InputKind.Wsdl, PayloadFormat.Xml);
        var used = new List<ProfileInput>();
        var resolved = new HashSet<XElement>();
        var expanded = XsdReader.Expand(name, schemas, files ?? ContractFiles.None, builder, used, resolved);
        var walker = new XsdReader.Walker(selected.Wrapper is null ? expanded : [selected.Wrapper, .. expanded], builder, resolved);
        if (selected.Encoded)
        {
            builder.Finding(ProfileFindingKind.SchemaSimplified, "/" + selected.Element,
                $"Operation '{selected.Name}' uses SOAP encoding (use=\"encoded\"); its parts were read as literal XML, so check arrays and references.");
        }

        if (!walker.HasElement(selected.Element))
        {
            throw new ProfileException($"'{name}': operation '{selected.Name}' uses element '{selected.Element}', which its types section does not declare.");
        }

        walker.Root(selected.Element);
        var label = selected.Wrapper is null
            ? $"operation {selected.Name} input <{selected.Element}>"
            : $"RPC operation {selected.Name} input <{selected.Element}>, one child per message part";
        return builder.Build(ContractDocument.Hash(content), label) with { Referenced = used };
    }

    private sealed record Operation(string Name, string Element, XElement? Wrapper = null, bool Encoded = false);

    private static List<Operation> Wsdl11Operations(XElement root)
    {
        var messages = root.Elements(Wsdl11 + "message")
            .Where(m => (string?)m.Attribute("name") is not null)
            .GroupBy(m => (string)m.Attribute("name")!)
            .ToDictionary(g => g.Key, g => g.First());
        var operations = new List<Operation>();
        foreach (var portType in root.Elements(Wsdl11 + "portType"))
        {
            foreach (var operation in portType.Elements(Wsdl11 + "operation"))
            {
                var operationName = (string?)operation.Attribute("name");
                var message = (string?)operation.Element(Wsdl11 + "input")?.Attribute("message");
                if (operationName is null || message is null || !messages.TryGetValue(XsdReader.Local(message), out var definition))
                {
                    continue;
                }

                var parts = definition.Elements(Wsdl11 + "part").ToList();
                var (rpc, encoded) = Style(root, (string?)portType.Attribute("name"), operationName);
                if (!rpc && parts.All(p => p.Attribute("type") is null)
                    && parts.Select(p => (string?)p.Attribute("element")).FirstOrDefault(e => e is not null) is { } element)
                {
                    operations.Add(new(operationName, XsdReader.Local(element)));
                }
                else if (rpc || parts.Count > 0)
                {
                    operations.Add(new(operationName, operationName, Wrapper(operationName, parts), encoded));
                }
            }
        }

        return [.. operations.DistinctBy(o => o.Name)];
    }

    /// <summary>Whether the SOAP binding of an operation is RPC style, and whether its input uses SOAP encoding.</summary>
    private static (bool Rpc, bool Encoded) Style(XElement root, string? portType, string operation)
    {
        foreach (var binding in root.Elements(Wsdl11 + "binding").Where(b => portType is null || XsdReader.Local((string?)b.Attribute("type") ?? "") == portType))
        {
            if (binding.Elements(Wsdl11 + "operation").FirstOrDefault(o => (string?)o.Attribute("name") == operation) is not { } bound)
            {
                continue;
            }

            var style = Extension(bound, "operation")?.Attribute("style") ?? Extension(binding, "binding")?.Attribute("style");
            var body = bound.Element(Wsdl11 + "input") is { } input ? Extension(input, "body") : null;
            return ((string?)style == "rpc", (string?)body?.Attribute("use") == "encoded");
        }

        return (false, false);
    }

    private static XElement? Extension(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName && e.Name.Namespace != Wsdl11);

    /// <summary>An XML Schema declaring the RPC body wrapper: one element per part, typed as the part is.</summary>
    private static XElement Wrapper(string operation, IEnumerable<XElement> parts)
    {
        var prefixes = new Dictionary<XNamespace, string> { [XsdReader.Xs] = "xs" };
        string Qualify(XElement part, string qualifiedName)
        {
            var colon = qualifiedName.IndexOf(':');
            var ns = colon < 0 ? part.GetDefaultNamespace() : part.GetNamespaceOfPrefix(qualifiedName[..colon]) ?? XNamespace.None;
            if (ns == XNamespace.None)
            {
                return XsdReader.Local(qualifiedName);
            }

            if (!prefixes.TryGetValue(ns, out var prefix))
            {
                prefix = $"p{prefixes.Count}";
                prefixes.Add(ns, prefix);
            }

            return $"{prefix}:{XsdReader.Local(qualifiedName)}";
        }

        var children = new List<XElement>();
        foreach (var part in parts)
        {
            var partName = (string?)part.Attribute("name") ?? "part";
            children.Add((string?)part.Attribute("type") is { } type
                ? new XElement(XsdReader.Xs + "element", new XAttribute("name", partName), new XAttribute("type", Qualify(part, type)))
                : (string?)part.Attribute("element") is { } element
                    ? new XElement(XsdReader.Xs + "element", new XAttribute("ref", Qualify(part, element)))
                    : new XElement(XsdReader.Xs + "element", new XAttribute("name", partName)));
        }

        return new XElement(
            XsdReader.Xs + "schema",
            prefixes.Select(p => new XAttribute(XNamespace.Xmlns + p.Value, p.Key.NamespaceName)),
            new XElement(
                XsdReader.Xs + "element",
                new XAttribute("name", operation),
                new XElement(XsdReader.Xs + "complexType", new XElement(XsdReader.Xs + "sequence", children))));
    }

    private static List<Operation> Wsdl20Operations(XElement root) =>
        [.. root.Elements(Wsdl20 + "interface").Elements(Wsdl20 + "operation")
            .Select(o => ((string?)o.Attribute("name"), (string?)o.Element(Wsdl20 + "input")?.Attribute("element")))
            .Where(o => o.Item1 is not null && o.Item2 is not null)
            .Select(o => new Operation(o.Item1!, XsdReader.Local(o.Item2!)))
            .DistinctBy(o => o.Name)];
}
