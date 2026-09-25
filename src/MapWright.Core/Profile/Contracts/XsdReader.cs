using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>
/// Reads an XML Schema into profile fields for one root element: sequences, choices, groups, attributes,
/// named and inline types, extensions, simple-type facets and annotations. Paths use local names.
/// </summary>
public static class XsdReader
{
    public static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <param name="root">The global element to profile; optional when the schema has one root element.</param>
    public static ContractDocument Read(string name, string content, string? root = null)
    {
        var schema = Load(name, content, "XML Schema").Root!;
        if (schema.Name != Xs + "schema")
        {
            throw new ProfileException($"'{name}' is not an XML Schema (expected <xs:schema>, found <{schema.Name.LocalName}>).");
        }

        var builder = new ContractBuilder(name, InputKind.Xsd, PayloadFormat.Xml);
        var walker = new Walker([schema], builder);
        var element = walker.SelectRoot(root);
        walker.Root(element);
        return builder.Build(ContractDocument.Hash(content), $"element {element}");
    }

    internal static XDocument Load(string name, string content, string what)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), Settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ProfileException($"{what} '{name}' is not valid XML (line {ex.LineNumber}): {ex.Message}", ex);
        }
    }

    internal static string Local(string qualifiedName) => qualifiedName[(qualifiedName.IndexOf(':') + 1)..];

    internal sealed class Walker
    {
        private readonly ContractBuilder _builder;
        private readonly Dictionary<string, XElement> _elements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _attributeGroups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _attributes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
        private readonly Stack<XElement> _typeStack = new();

        public Walker(IReadOnlyList<XElement> schemas, ContractBuilder builder)
        {
            _builder = builder;
            foreach (var schema in schemas)
            {
                foreach (var child in schema.Elements())
                {
                    var key = (string?)child.Attribute("name");
                    var target = child.Name.LocalName switch
                    {
                        "element" => _elements,
                        "complexType" or "simpleType" => _types,
                        "group" => _groups,
                        "attributeGroup" => _attributeGroups,
                        "attribute" => _attributes,
                        _ => null,
                    };
                    if (target is not null && key is not null)
                    {
                        target.TryAdd(key, child);
                    }

                    if (child.Name.LocalName is "import" or "include" or "redefine" or "override")
                    {
                        var location = (string?)child.Attribute("schemaLocation") ?? (string?)child.Attribute("namespace") ?? "?";
                        builder.Finding(ProfileFindingKind.UnresolvedReference, null,
                            $"xs:{child.Name.LocalName} of '{location}' was not loaded; types declared there are unknown.");
                    }
                }

                _referenced.UnionWith(schema.Descendants(Xs + "element").Select(e => (string?)e.Attribute("ref")).OfType<string>().Select(Local));
                if ((string?)schema.Attribute("targetNamespace") is { Length: > 0 } ns)
                {
                    builder.Finding(ProfileFindingKind.NamespacesIgnored, null, $"Paths use local names; namespace(s) {ns} were not included.");
                }
            }
        }

        public string SelectRoot(string? root)
        {
            if (root is not null)
            {
                return _elements.ContainsKey(root)
                    ? root
                    : throw new ProfileException($"'{_builder.Input}' has no global element '{root}'. Global elements: {string.Join(", ", _elements.Keys)}.");
            }

            var candidates = _elements.Keys.Where(k => !_referenced.Contains(k)).ToList();
            return (candidates.Count, _elements.Count) switch
            {
                (_, 0) => throw new ProfileException($"'{_builder.Input}' declares no global element."),
                (1, _) => candidates[0],
                (_, 1) => _elements.Keys.First(),
                _ => throw new ProfileException(
                    $"'{_builder.Input}' declares several root elements; choose one with --root: {string.Join(", ", candidates.Count > 0 ? candidates : [.. _elements.Keys])}."),
            };
        }

        public bool HasElement(string name) => _elements.ContainsKey(name);

        public void Root(string name) => Element(_elements[name], null, optional: false, repeated: false);

        private void Element(XElement site, ContractField? parent, bool optional, bool repeated)
        {
            var declaration = site;
            if ((string?)site.Attribute("ref") is { } reference)
            {
                if (!_elements.TryGetValue(Local(reference), out var global))
                {
                    _builder.Finding(ProfileFindingKind.UnresolvedReference, parent?.Path, $"Element reference '{reference}' is not declared in '{_builder.Input}'.");
                    return;
                }

                declaration = global;
            }

            var name = (string?)declaration.Attribute("name") ?? "?";
            var field = _builder.Add(parent is null ? "/" + name : $"{parent.Path}/{name}", name, parent);
            var min = Occurs(site, "minOccurs") ?? 1;
            var max = (string?)site.Attribute("maxOccurs") ?? "1";
            if (parent is null)
            {
                field.Required = Requirement.Required;
                field.Details[ProfileAttribute.Required] = "Root element.";
            }
            else
            {
                field.Required = min >= 1 && !optional ? Requirement.Required : Requirement.Optional;
                field.Details[ProfileAttribute.Required] = min < 1 ? $"minOccurs={min}." : optional ? "Inside an optional group or xs:choice." : $"minOccurs={min}.";
            }

            var maxOccurs = max == "unbounded" ? (int?)null : int.TryParse(max, CultureInfo.InvariantCulture, out var m) ? m : 1;
            if (maxOccurs is null or > 1 || repeated)
            {
                field.Cardinality = Cardinality.Array;
                field.MaxOccurs = maxOccurs > 1 ? maxOccurs : null;
                field.Details[ProfileAttribute.Cardinality] = repeated && maxOccurs == 1 ? "Inside a repeating group." : $"maxOccurs={max}.";
            }

            field.Description ??= Documentation(declaration);
            if ((string?)declaration.Attribute("fixed") is { } fixedValue)
            {
                field.AllowedValues = [fixedValue];
                field.Details[ProfileAttribute.AllowedValues] = "fixed value.";
            }

            if ((string?)declaration.Attribute("type") is { } type)
            {
                Typed(type, declaration, field);
            }
            else if (declaration.Element(Xs + "complexType") is { } complex)
            {
                Complex(complex, field);
            }
            else if (declaration.Element(Xs + "simpleType") is { } simple)
            {
                Simple(simple, field, "inline simpleType");
            }
            else
            {
                field.Details[ProfileAttribute.DataType] = "No type declared (xs:anyType).";
            }
        }

        private void Typed(string type, XElement context, ContractField field)
        {
            var local = Local(type);
            if (IsBuiltin(type, context))
            {
                Builtin(local, field, $"xs:{local}");
            }
            else if (_types.TryGetValue(local, out var named))
            {
                if (named.Name.LocalName == "complexType")
                {
                    Complex(named, field);
                }
                else
                {
                    Simple(named, field, local);
                }
            }
            else
            {
                _builder.Finding(ProfileFindingKind.UnresolvedReference, field.Path, $"Type '{type}' is not declared in '{_builder.Input}'.");
                field.Details[ProfileAttribute.DataType] = $"Unknown type '{type}'.";
            }
        }

        private static bool IsBuiltin(string type, XElement context)
        {
            var colon = type.IndexOf(':');
            var ns = colon < 0 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(type[..colon]);
            return ns == Xs;
        }

        private void Complex(XElement type, ContractField field)
        {
            field.MakeObject();
            field.Details[ProfileAttribute.DataType] = (string?)type.Attribute("name") is { } typeName ? $"complexType {typeName}." : "Inline complexType.";
            field.Description ??= Documentation(type);
            if (_typeStack.Contains(type))
            {
                _builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, $"Recursive type '{(string?)type.Attribute("name")}' was profiled once.");
                return;
            }

            _typeStack.Push(type);
            if ((string?)type.Attribute("mixed") is "true" or "1")
            {
                _builder.Finding(ProfileFindingKind.MixedContentIgnored, field.Path, "Element mixes text and child elements; the text was ignored.");
            }

            if (type.Element(Xs + "simpleContent") is { } simpleContent)
            {
                SimpleContent(simpleContent, field);
            }
            else
            {
                Attributes(type, field);
                Particles(type, field, optional: false, repeated: false);
            }

            _typeStack.Pop();
        }

        private void SimpleContent(XElement content, ContractField field)
        {
            var attributes = new List<XElement>();
            var derivation = content.Elements().FirstOrDefault(e => e.Name.LocalName is "extension" or "restriction");
            string? baseType = null;
            XElement? context = null;
            var hops = 0;
            while (derivation is not null && hops++ < 16)
            {
                attributes.Add(derivation);
                baseType = (string?)derivation.Attribute("base");
                context = derivation;
                if (baseType is null || IsBuiltin(baseType, derivation)
                    || !_types.TryGetValue(Local(baseType), out var baseDefinition)
                    || baseDefinition.Element(Xs + "simpleContent") is not { } baseContent)
                {
                    break;
                }

                derivation = baseContent.Elements().FirstOrDefault(e => e.Name.LocalName is "extension" or "restriction");
            }

            var hasAttributes = attributes.Any(a => a.Elements().Any(e => e.Name.LocalName is "attribute" or "attributeGroup"));
            var target = field;
            if (hasAttributes)
            {
                foreach (var holder in Enumerable.Reverse(attributes))
                {
                    Attributes(holder, field);
                }

                target = _builder.Add(field.Path + "/text()", "text()", field);
                target.Required = Requirement.Required;
                target.Details[ProfileAttribute.Required] = "Element text.";
            }
            else
            {
                field.Kind = FieldNodeKind.Value;
            }

            if (baseType is not null && context is not null)
            {
                Typed(baseType, context, target);
            }

            if (content.Elements().FirstOrDefault(e => e.Name.LocalName == "restriction") is { } restriction)
            {
                Facets(restriction, target);
            }
        }

        private void Attributes(XElement container, ContractField field)
        {
            foreach (var child in container.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "attribute":
                        Attribute(child, field);
                        break;
                    case "attributeGroup" when (string?)child.Attribute("ref") is { } reference:
                        if (_attributeGroups.TryGetValue(Local(reference), out var group))
                        {
                            Attributes(group, field);
                        }
                        else
                        {
                            _builder.Finding(ProfileFindingKind.UnresolvedReference, field.Path, $"Attribute group '{reference}' is not declared in '{_builder.Input}'.");
                        }

                        break;
                    case "anyAttribute":
                        _builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, "xs:anyAttribute allows attributes that are not profiled.");
                        break;
                    case "complexContent":
                        foreach (var derivation in child.Elements().Where(e => e.Name.LocalName is "extension" or "restriction"))
                        {
                            if (derivation.Name.LocalName == "extension" && BaseComplexType(derivation) is { } baseType)
                            {
                                Attributes(baseType, field);
                            }

                            Attributes(derivation, field);
                        }

                        break;
                }
            }
        }

        private void Attribute(XElement site, ContractField parent)
        {
            var declaration = site;
            if ((string?)site.Attribute("ref") is { } reference)
            {
                if (!_attributes.TryGetValue(Local(reference), out var global))
                {
                    _builder.Finding(ProfileFindingKind.UnresolvedReference, parent.Path, $"Attribute reference '{reference}' is not declared in '{_builder.Input}'.");
                    return;
                }

                declaration = global;
            }

            var use = (string?)site.Attribute("use") ?? "optional";
            if (use == "prohibited" || (string?)declaration.Attribute("name") is not { } name)
            {
                return;
            }

            var field = _builder.Add($"{parent.Path}/@{name}", name, parent);
            field.Required = use == "required" ? Requirement.Required : Requirement.Optional;
            field.Details[ProfileAttribute.Required] = $"use=\"{use}\".";
            field.Description ??= Documentation(declaration);
            if (((string?)declaration.Attribute("fixed") ?? (string?)site.Attribute("fixed")) is { } fixedValue)
            {
                field.AllowedValues = [fixedValue];
                field.Details[ProfileAttribute.AllowedValues] = "fixed value.";
            }

            if ((string?)declaration.Attribute("type") is { } type)
            {
                Typed(type, declaration, field);
            }
            else if (declaration.Element(Xs + "simpleType") is { } simple)
            {
                Simple(simple, field, "inline simpleType");
            }
            else
            {
                Builtin("string", field, "No type declared (xs:anySimpleType).");
            }
        }

        private void Particles(XElement container, ContractField field, bool optional, bool repeated)
        {
            foreach (var child in container.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "element":
                        Element(child, field, optional, repeated);
                        break;
                    case "sequence" or "all" or "choice":
                        var min = Occurs(child, "minOccurs") ?? 1;
                        var max = (string?)child.Attribute("maxOccurs") ?? "1";
                        Particles(child, field,
                            optional || min < 1 || (child.Name.LocalName == "choice" && child.Elements().Count(e => e.Name.LocalName != "annotation") > 1),
                            repeated || max == "unbounded" || (int.TryParse(max, CultureInfo.InvariantCulture, out var m) && m > 1));
                        break;
                    case "group" when (string?)child.Attribute("ref") is { } reference:
                        if (_groups.TryGetValue(Local(reference), out var group))
                        {
                            var groupMax = (string?)child.Attribute("maxOccurs") ?? "1";
                            Particles(group, field, optional || (Occurs(child, "minOccurs") ?? 1) < 1, repeated || groupMax != "1");
                        }
                        else
                        {
                            _builder.Finding(ProfileFindingKind.UnresolvedReference, field.Path, $"Group '{reference}' is not declared in '{_builder.Input}'.");
                        }

                        break;
                    case "any":
                        _builder.Finding(ProfileFindingKind.SchemaSimplified, field.Path, "xs:any allows elements that are not profiled.");
                        break;
                    case "complexContent":
                        foreach (var derivation in child.Elements().Where(e => e.Name.LocalName is "extension" or "restriction"))
                        {
                            if (derivation.Name.LocalName == "extension" && BaseComplexType(derivation) is { } baseType)
                            {
                                Particles(baseType, field, optional, repeated);
                            }

                            Particles(derivation, field, optional, repeated);
                        }

                        break;
                }
            }
        }

        private XElement? BaseComplexType(XElement derivation)
        {
            if ((string?)derivation.Attribute("base") is not { } baseType || IsBuiltin(baseType, derivation))
            {
                return null;
            }

            if (_types.TryGetValue(Local(baseType), out var definition) && definition.Name.LocalName == "complexType")
            {
                return definition;
            }

            _builder.Finding(ProfileFindingKind.UnresolvedReference, null, $"Base type '{baseType}' is not declared in '{_builder.Input}'.");
            return null;
        }

        private void Simple(XElement type, ContractField field, string label)
        {
            field.Description ??= Documentation(type);
            if (_typeStack.Contains(type))
            {
                return;
            }

            _typeStack.Push(type);
            if (type.Element(Xs + "restriction") is { } restriction)
            {
                if ((string?)restriction.Attribute("base") is { } baseType)
                {
                    Typed(baseType, restriction, field);
                }
                else if (restriction.Element(Xs + "simpleType") is { } inner)
                {
                    Simple(inner, field, label);
                }

                Facets(restriction, field);
                field.Details[ProfileAttribute.DataType] = $"{label} ({field.Details.GetValueOrDefault(ProfileAttribute.DataType)?.TrimEnd('.')}).";
            }
            else
            {
                Builtin("string", field, $"{label} (xs:{type.Elements().FirstOrDefault()?.Name.LocalName ?? "string"}).");
            }

            _typeStack.Pop();
        }

        private static void Facets(XElement restriction, ContractField field)
        {
            var values = restriction.Elements(Xs + "enumeration").Select(e => (string?)e.Attribute("value")).OfType<string>().ToList();
            if (values.Count > 0)
            {
                field.AllowedValues = values;
                field.Details[ProfileAttribute.AllowedValues] = "xs:enumeration.";
            }

            int? Int(string facet) => int.TryParse((string?)restriction.Element(Xs + facet)?.Attribute("value"), CultureInfo.InvariantCulture, out var v) ? v : null;
            decimal? Dec(string facet) => decimal.TryParse((string?)restriction.Element(Xs + facet)?.Attribute("value"), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

            if (Int("length") is { } length)
            {
                (field.MinLength, field.MaxLength) = (length, length);
            }

            field.MinLength = Int("minLength") ?? field.MinLength;
            field.MaxLength = Int("maxLength") ?? field.MaxLength;
            field.MinValue = Dec("minInclusive") ?? field.MinValue;
            field.MaxValue = Dec("maxInclusive") ?? field.MaxValue;
            field.MaxScale = Int("fractionDigits") ?? field.MaxScale;
        }

        private static void Builtin(string type, ContractField field, string detail)
        {
            (field.DataType, field.Format) = type switch
            {
                "int" or "integer" or "long" or "short" or "byte" or "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger"
                    or "negativeInteger" or "unsignedLong" or "unsignedInt" or "unsignedShort" or "unsignedByte" => (FieldDataType.Integer, null),
                "decimal" or "float" or "double" => (FieldDataType.Decimal, null),
                "boolean" => (FieldDataType.Boolean, null),
                "date" => (FieldDataType.Date, "yyyy-MM-dd"),
                "dateTime" or "dateTimeStamp" => (FieldDataType.DateTime, "ISO 8601"),
                "anyType" or "anySimpleType" => (FieldDataType.Unknown, (string?)null),
                _ => (FieldDataType.String, (string?)null),
            };
            field.Details[ProfileAttribute.DataType] = detail.EndsWith('.') ? detail : detail + ".";
            if (field.Format is not null)
            {
                field.Details[ProfileAttribute.Format] = $"xs:{type}.";
            }
        }

        private static int? Occurs(XElement element, string attribute) =>
            int.TryParse((string?)element.Attribute(attribute), CultureInfo.InvariantCulture, out var value) ? value : null;

        private static string? Documentation(XElement element) =>
            element.Element(Xs + "annotation")?.Elements(Xs + "documentation").Select(d => d.Value.Trim()).FirstOrDefault(d => d.Length > 0);
    }
}
