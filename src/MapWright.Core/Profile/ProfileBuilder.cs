using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile;

public sealed record SampleInput(string Name, string Content);

public sealed record ProfileRequest
{
    public required string System { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<SampleInput> Samples { get; init; }

    /// <summary>Schemas, service contracts and field specs; they are authoritative over what samples show.</summary>
    public IReadOnlyList<ContractDocument> Contracts { get; init; } = [];
}

public sealed record ProfileOptions
{
    /// <summary>When false, no sample or observed values are written to the profile.</summary>
    public bool RetainValues { get; init; } = true;
    public int MaxObservedValues { get; init; } = 10;
    public int MaxValueShapes { get; init; } = 5;
    public bool UnwrapSoapEnvelope { get; init; } = true;

    /// <summary>Treat &lt;Officers&gt;&lt;Officer/&gt;&lt;/Officers&gt; as a list even when only one child was ever seen.</summary>
    public bool InferWrappedArrays { get; init; } = true;
    public SensitiveDataPolicy Sensitivity { get; init; } = SensitiveDataPolicy.Default;
}

/// <summary>
/// Builds a <see cref="SystemProfile"/> by merging sample payloads of one format with the system's contracts
/// (JSON Schema, OpenAPI, XSD, WSDL, field specs).
/// </summary>
public static partial class ProfileBuilder
{
    public static SystemProfile Build(ProfileRequest request, ProfileOptions? options = null, TimeProvider? time = null)
    {
        options ??= new();
        if (string.IsNullOrWhiteSpace(request.System))
        {
            throw new ProfileException("A system name is required.");
        }

        if (request.Samples.Count == 0 && request.Contracts.Count == 0)
        {
            throw new ProfileException("At least one sample payload or contract is required.");
        }

        if (request.Samples.Select(s => s.Name).Concat(request.Contracts.Select(c => c.Name))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ProfileException($"Input name '{duplicate.Key}' is used more than once.");
        }

        var observed = request.Samples.Count == 0 ? null : FromSamples(request, options, time);
        return request.Contracts.Count == 0 ? observed! : Merge(request, observed, time);
    }

    private static SystemProfile FromSamples(ProfileRequest request, ProfileOptions options, TimeProvider? time)
    {
        var documents = request.Samples
            .Select(s => SampleReader.Read(s.Name, s.Content, options.UnwrapSoapEnvelope))
            .ToList();

        var formats = documents.Select(d => d.Format).Distinct().ToList();
        if (formats.Count > 1)
        {
            throw new ProfileException(
                $"All samples of one system must share a format; got {string.Join(" and ", documents.Select(d => $"'{d.Name}' ({d.Format})"))}.");
        }

        var accumulator = new Accumulator(formats[0], options);
        foreach (var document in documents)
        {
            accumulator.Add(document);
        }

        return new()
        {
            SpecVersion = SystemProfile.CurrentSpecVersion,
            System = request.System,
            Version = request.Version,
            Format = formats[0],
            Description = request.Description,
            CreatedAt = (time ?? TimeProvider.System).GetUtcNow(),
            Inputs = [.. request.Samples.Select(s => new ProfileInput
            {
                Name = s.Name,
                Kind = InputKind.SamplePayload,
                Format = formats[0],
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.Content))),
            })],
            Fields = accumulator.Fields(out var fieldFindings),
            Findings = MergeFindings([.. documents.SelectMany(d => d.Findings), .. fieldFindings]),
        };
    }

    private static List<ProfileFinding> MergeFindings(IEnumerable<ProfileFinding> findings) =>
        [.. findings
            .GroupBy(f => (f.Kind, f.Path, f.Message))
            .Select(g => g.First() with { Inputs = [.. g.SelectMany(f => f.Inputs).Distinct()] })];

    private sealed class FieldStats(string path, string name, string? parentPath, SampleNode node)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public string? ParentPath { get; } = parentPath;
        public bool IsAttribute { get; } = node.IsAttribute;
        public bool IsText { get; } = node.IsText;
        public List<string> SeenIn { get; } = [];
        public bool SawObject { get; set; }
        public bool SawValue { get; set; }
        public bool IsArray { get; set; }
        public bool InferredArray { get; set; }
        public int MaxOccurs { get; set; }
        public string? MaxOccursIn { get; set; }
        public int Instances { get; set; }
        public int PresentInstances { get; set; }
        public int Values { get; set; }
        public int Nulls { get; set; }
        public int Empties { get; set; }
        public FieldDataType Type { get; set; }
        public HashSet<ScalarKind> NativeKinds { get; } = [];
        public List<string> Formats { get; } = [];
        public bool SlashFirstOver12 { get; set; }
        public bool SlashSecondOver12 { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public decimal? MinValue { get; set; }
        public decimal? MaxValue { get; set; }
        public int MaxScale { get; set; }
        public List<string> Distinct { get; } = [];
        public List<string> Shapes { get; } = [];
        public bool ShapesOverflow { get; set; }
        public string? FirstValue { get; set; }
        public bool IdentifierLike { get; set; }
    }

    private sealed class Accumulator(PayloadFormat format, ProfileOptions options)
    {
        private readonly Dictionary<string, FieldStats> _fields = new(StringComparer.Ordinal);
        private readonly List<FieldStats> _order = [];
        private int _samples;
        private string _sample = "";

        private bool IsXml => format == PayloadFormat.Xml;

        public void Add(SampleDocument document)
        {
            _samples++;
            _sample = document.Name;
            var path = IsXml ? "/" + document.Root.Name : JsonSampleReader.RootName;
            var root = Touch(path, document.Root, parentPath: null);
            root.PresentInstances++;
            Visit(document.Root, root, path);
        }

        private FieldStats Touch(string path, SampleNode node, string? parentPath)
        {
            if (!_fields.TryGetValue(path, out var stats))
            {
                stats = new(path, node.Name, parentPath, node);
                _fields.Add(path, stats);
                _order.Add(stats);
            }

            if (!stats.SeenIn.Contains(_sample))
            {
                stats.SeenIn.Add(_sample);
            }

            return stats;
        }

        private void Visit(SampleNode node, FieldStats stats, string prefix)
        {
            switch (node.Type)
            {
                case SampleNodeType.Object:
                    stats.SawObject = true;
                    stats.Instances++;
                    var present = new HashSet<FieldStats>();
                    foreach (var child in node.Children)
                    {
                        var childPath = ChildPath(prefix, child);
                        var childStats = Touch(childPath, child, stats.Path);
                        present.Add(childStats);
                        Visit(child, childStats, childPath);
                    }

                    foreach (var child in present)
                    {
                        child.PresentInstances++;
                    }

                    break;

                case SampleNodeType.Array:
                    stats.IsArray = true;
                    if (node.Children.Count > stats.MaxOccurs || stats.MaxOccursIn is null)
                    {
                        stats.MaxOccurs = Math.Max(stats.MaxOccurs, node.Children.Count);
                        stats.MaxOccursIn = _sample;
                    }

                    var itemPrefix = IsXml ? prefix : prefix + "[*]";
                    foreach (var item in node.Children)
                    {
                        if (item.Type == SampleNodeType.Array)
                        {
                            var nested = Touch(itemPrefix, item, stats.Path);
                            nested.PresentInstances++;
                            Visit(item, nested, itemPrefix);
                        }
                        else
                        {
                            Visit(item, stats, itemPrefix);
                        }
                    }

                    break;

                default:
                    stats.SawValue = true;
                    stats.Instances++;
                    Observe(stats, node);
                    break;
            }
        }

        private string ChildPath(string prefix, SampleNode child)
        {
            if (IsXml)
            {
                return child.IsAttribute ? $"{prefix}/@{child.Name}" : $"{prefix}/{child.Name}";
            }

            return SimpleJsonName().IsMatch(child.Name)
                ? $"{prefix}.{child.Name}"
                : $"{prefix}['{child.Name.Replace("'", "\\'", StringComparison.Ordinal)}']";
        }

        private void Observe(FieldStats stats, SampleNode node)
        {
            if (node.Scalar == ScalarKind.Null || node.Value is null)
            {
                stats.Nulls++;
                return;
            }

            var text = node.Value;
            if (text.Length == 0)
            {
                stats.Empties++;
                return;
            }

            stats.Values++;
            stats.FirstValue ??= text;
            stats.MinLength = Math.Min(stats.MinLength ?? int.MaxValue, text.Length);
            stats.MaxLength = Math.Max(stats.MaxLength ?? 0, text.Length);
            if (!IsXml)
            {
                stats.NativeKinds.Add(node.Scalar);
            }

            var inferred = ValueInference.Infer(text, node.Scalar, textIsUntyped: IsXml);
            stats.Type = ValueInference.Widen(stats.Type, inferred.Type);
            if (inferred.Format is { } valueFormat && !stats.Formats.Contains(valueFormat))
            {
                stats.Formats.Add(valueFormat);
            }

            stats.SlashFirstOver12 |= inferred.SlashFirst > 12;
            stats.SlashSecondOver12 |= inferred.SlashSecond > 12;

            if (inferred.Type is FieldDataType.Integer or FieldDataType.Decimal
                && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                stats.MinValue = Math.Min(stats.MinValue ?? decimal.MaxValue, number);
                stats.MaxValue = Math.Max(stats.MaxValue ?? decimal.MinValue, number);
                stats.MaxScale = Math.Max(stats.MaxScale, ValueInference.Scale(text));
            }

            if (!stats.Distinct.Contains(text) && stats.Distinct.Count <= options.MaxObservedValues)
            {
                stats.Distinct.Add(text);
            }

            if (!stats.ShapesOverflow)
            {
                var shape = ValueInference.Shape(text);
                if (shape is null)
                {
                    stats.ShapesOverflow = true;
                }
                else if (!stats.Shapes.Contains(shape))
                {
                    stats.Shapes.Add(shape);
                    stats.ShapesOverflow = stats.Shapes.Count > options.MaxValueShapes;
                }
            }

            stats.IdentifierLike |= SensitiveDataPolicy.LooksLikeIdentifier(text);
        }

        public IReadOnlyList<ProfileField> Fields(out List<ProfileFinding> findings)
        {
            var found = new List<ProfileFinding>();
            var children = _order.Where(f => f.ParentPath is not null).ToLookup(f => f.ParentPath!);

            if (IsXml && options.InferWrappedArrays)
            {
                InferWrappedArrays(children, found);
            }

            var fields = new List<ProfileField>(_order.Count);
            void AddTree(FieldStats stats)
            {
                fields.Add(Finish(stats, found));
                foreach (var child in children[stats.Path])
                {
                    AddTree(child);
                }
            }

            foreach (var root in _order.Where(f => f.ParentPath is null))
            {
                AddTree(root);
            }

            findings = found;
            return fields;
        }

        private void InferWrappedArrays(ILookup<string, FieldStats> children, List<ProfileFinding> findings)
        {
            foreach (var wrapper in _order.Where(f => f.SawObject && !f.IsArray))
            {
                var elements = children[wrapper.Path].Where(c => !c.IsAttribute && !c.IsText).ToList();
                if (elements is [{ IsArray: false } item]
                    && wrapper.Name.Length > item.Name.Length
                    && wrapper.Name.StartsWith(item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    item.InferredArray = true;
                    findings.Add(new()
                    {
                        Kind = ProfileFindingKind.InferredCardinality,
                        Path = item.Path,
                        Message = $"<{item.Name}> was never repeated, but its wrapper <{wrapper.Name}> suggests a list; confirm with a schema or a sample containing several.",
                        Inputs = [.. item.SeenIn],
                    });
                }
            }
        }

        private ProfileField Finish(FieldStats stats, List<ProfileFinding> findings)
        {
            var parent = stats.ParentPath is null ? null : _fields[stats.ParentPath];
            var parentInstances = parent?.Instances ?? _samples;
            var kind = stats.SawObject ? FieldNodeKind.Object : FieldNodeKind.Value;
            var cardinality = stats.IsArray || stats.InferredArray ? Cardinality.Array : Cardinality.Single;
            var inputs = stats.SeenIn.ToList();

            if (stats.SawObject && stats.SawValue)
            {
                findings.Add(new()
                {
                    Kind = ProfileFindingKind.KindConflict,
                    Path = stats.Path,
                    Message = "Sent as an object in some instances and as a plain value in others.",
                    Inputs = inputs,
                });
            }

            if (stats.NativeKinds.Count > 1)
            {
                findings.Add(new()
                {
                    Kind = ProfileFindingKind.TypeConflict,
                    Path = stats.Path,
                    Message = $"Values are sent as {string.Join(" and ", stats.NativeKinds.Select(k => k.ToString().ToLowerInvariant()))} across samples.",
                    Inputs = inputs,
                });
            }

            var (dataType, dateFormat) = ResolveType(stats, findings, inputs);
            var isValue = kind == FieldNodeKind.Value;

            var sensitivity = !isValue ? null
                : options.Sensitivity.MatchField(stats.Name, parent?.Name)
                    ?? (stats.IdentifierLike ? "Values look like identifiers (9 or 13-19 digits)." : null);
            var sensitive = sensitivity is not null;
            var typeDetail = $"Inferred from {stats.Values} non-empty value(s).";
            if (sensitive && dataType == FieldDataType.Integer)
            {
                dataType = FieldDataType.String;
                typeDetail += " Numeric identifiers are kept as strings to preserve leading zeros.";
            }
            var numeric = dataType is FieldDataType.Integer or FieldDataType.Decimal;

            var required = stats.PresentInstances < parentInstances || stats.Nulls + stats.Empties > 0
                ? Requirement.Optional
                : parentInstances == 0 ? Requirement.Unknown : Requirement.LikelyRequired;

            var provenance = new List<AttributeSource>
            {
                Source(ProfileAttribute.DataType, inputs, kind == FieldNodeKind.Object
                    ? $"Structured element in {inputs.Count} sample(s)."
                    : typeDetail),
                Source(ProfileAttribute.Cardinality, inputs, CardinalityDetail(stats, parent)),
                Source(ProfileAttribute.Required, inputs, required switch
                {
                    Requirement.Optional when stats.PresentInstances < parentInstances =>
                        $"Absent from {parentInstances - stats.PresentInstances} of {parentInstances} parent instance(s).",
                    Requirement.Optional => $"Sent empty or null {stats.Nulls + stats.Empties} time(s).",
                    _ => $"Present in all {parentInstances} parent instance(s); samples alone cannot prove it is required.",
                }),
            };
            if (dateFormat is not null)
            {
                provenance.Add(Source(ProfileAttribute.Format, inputs, "Detected from value patterns."));
            }

            if (sensitive)
            {
                provenance.Add(Source(ProfileAttribute.Sensitivity, inputs, sensitivity));
            }

            return new()
            {
                Path = stats.Path,
                Name = stats.Name,
                ParentPath = stats.ParentPath,
                Kind = kind,
                Cardinality = cardinality,
                DataType = dataType,
                Format = dateFormat,
                Required = required,
                MinLength = isValue ? stats.MinLength : null,
                MaxLength = isValue ? stats.MaxLength : null,
                MinValue = numeric && !sensitive ? stats.MinValue : null,
                MaxValue = numeric && !sensitive ? stats.MaxValue : null,
                MaxScale = dataType == FieldDataType.Decimal && !sensitive ? stats.MaxScale : null,
                MaxOccurs = stats.IsArray ? stats.MaxOccurs : null,
                ObservedValues = options.RetainValues && isValue && !sensitive
                    ? [.. stats.Distinct.Take(options.MaxObservedValues)]
                    : [],
                ObservedValuesTruncated = options.RetainValues && isValue && !sensitive && stats.Distinct.Count > options.MaxObservedValues,
                ValueShapes = stats.ShapesOverflow ? [] : [.. stats.Shapes],
                SampleValue = options.RetainValues && stats.FirstValue is { } first
                    ? sensitive ? SensitiveDataPolicy.Mask(first) : first
                    : null,
                Sensitive = sensitive,
                SensitivityReason = sensitivity,
                Presence = new()
                {
                    ParentInstances = parentInstances,
                    PresentInstances = stats.PresentInstances,
                    Occurrences = stats.Instances,
                    NullCount = stats.Nulls,
                    EmptyCount = stats.Empties,
                },
                SeenIn = inputs,
                Provenance = provenance,
            };
        }

        private static (FieldDataType Type, string? Format) ResolveType(FieldStats stats, List<ProfileFinding> findings, List<string> inputs)
        {
            if (stats.SawObject)
            {
                return (FieldDataType.Object, null);
            }

            if (stats.Type is not (FieldDataType.Date or FieldDataType.DateTime))
            {
                return (stats.Type, null);
            }

            var formats = new List<string>();
            foreach (var format in stats.Formats)
            {
                if (format != InferredValue.SlashDateFormat)
                {
                    formats.Add(format);
                    continue;
                }

                switch (stats.SlashFirstOver12, stats.SlashSecondOver12)
                {
                    case (true, true):
                        return (FieldDataType.String, null);
                    case (true, false):
                        formats.Add("dd/MM/yyyy");
                        break;
                    case (false, true):
                        formats.Add("MM/dd/yyyy");
                        break;
                    default:
                        formats.Add("MM/dd/yyyy or dd/MM/yyyy");
                        findings.Add(new()
                        {
                            Kind = ProfileFindingKind.AmbiguousDateFormat,
                            Path = stats.Path,
                            Message = "Day and month are both 12 or less in every sample; confirm whether dates are MM/dd/yyyy or dd/MM/yyyy.",
                            Inputs = inputs,
                        });
                        break;
                }
            }

            if (formats.Count > 1)
            {
                findings.Add(new()
                {
                    Kind = ProfileFindingKind.MixedFormats,
                    Path = stats.Path,
                    Message = $"Dates use more than one format: {string.Join(", ", formats)}.",
                    Inputs = inputs,
                });
            }

            return (stats.Type, string.Join(" | ", formats));
        }

        private string CardinalityDetail(FieldStats stats, FieldStats? parent)
        {
            if (stats.IsArray)
            {
                return IsXml
                    ? $"Repeated up to {stats.MaxOccurs} time(s) in '{stats.MaxOccursIn}'."
                    : $"JSON array with up to {stats.MaxOccurs} item(s) in '{stats.MaxOccursIn}'.";
            }

            return stats.InferredArray
                ? $"Inferred from wrapper <{parent?.Name}> containing only <{stats.Name}> elements."
                : "Never repeated in samples.";
        }

        private static AttributeSource Source(ProfileAttribute attribute, List<string> inputs, string? detail) => new()
        {
            Attribute = attribute,
            Kind = InputKind.SamplePayload,
            Inputs = inputs,
            Detail = detail,
        };
    }

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex SimpleJsonName();
}
