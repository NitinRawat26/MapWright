using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>Reads the input message of one WSDL 1.1 or 2.0 operation, using the XML Schema in its types section.</summary>
public static class WsdlReader
{
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";

    /// <param name="operation">The operation to profile; optional when the service has one operation.</param>
    public static ContractDocument Read(string name, string content, string? operation = null)
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
            : operations.FirstOrDefault(o => string.Equals(o.Name, operation, StringComparison.OrdinalIgnoreCase)) is { Name: not null } match
                ? match
                : throw new ProfileException($"'{name}' has no operation '{operation}'. Operations: {string.Join(", ", operations.Select(o => o.Name))}.");

        var schemas = root.Element(ns + "types")?.Elements(XsdReader.Xs + "schema").ToList() ?? [];
        var builder = new ContractBuilder(name, InputKind.Wsdl, PayloadFormat.Xml);
        var walker = new XsdReader.Walker(schemas, builder);
        if (!walker.HasElement(selected.Element))
        {
            throw new ProfileException($"'{name}': operation '{selected.Name}' uses element '{selected.Element}', which its types section does not declare.");
        }

        walker.Root(selected.Element);
        return builder.Build(ContractDocument.Hash(content), $"operation {selected.Name} input <{selected.Element}>");
    }

    private static List<(string Name, string Element)> Wsdl11Operations(XElement root)
    {
        var messages = root.Elements(Wsdl11 + "message")
            .Where(m => (string?)m.Attribute("name") is not null)
            .GroupBy(m => (string)m.Attribute("name")!)
            .ToDictionary(g => g.Key, g => g.First());
        var operations = new List<(string, string)>();
        foreach (var operation in root.Elements(Wsdl11 + "portType").Elements(Wsdl11 + "operation"))
        {
            var operationName = (string?)operation.Attribute("name");
            var message = (string?)operation.Element(Wsdl11 + "input")?.Attribute("message");
            if (operationName is null || message is null || !messages.TryGetValue(XsdReader.Local(message), out var definition))
            {
                continue;
            }

            if (definition.Elements(Wsdl11 + "part").Select(p => (string?)p.Attribute("element")).FirstOrDefault(e => e is not null) is { } element)
            {
                operations.Add((operationName, XsdReader.Local(element)));
            }
            else if (definition.Elements(Wsdl11 + "part").Any())
            {
                throw new ProfileException($"Operation '{operationName}' uses RPC-style typed parts; only document/literal element parts are supported.");
            }
        }

        return [.. operations.DistinctBy(o => o.Item1)];
    }

    private static List<(string Name, string Element)> Wsdl20Operations(XElement root) =>
        [.. root.Elements(Wsdl20 + "interface").Elements(Wsdl20 + "operation")
            .Select(o => ((string?)o.Attribute("name"), (string?)o.Element(Wsdl20 + "input")?.Attribute("element")))
            .Where(o => o.Item1 is not null && o.Item2 is not null)
            .Select(o => (o.Item1!, XsdReader.Local(o.Item2!)))
            .DistinctBy(o => o.Item1)];
}
