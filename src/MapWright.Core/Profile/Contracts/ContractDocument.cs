using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Contracts;

/// <summary>The fields one formal input (schema, service contract or field spec) declares for a system.</summary>
public sealed record ContractDocument
{
    public required string Name { get; init; }
    public required InputKind Kind { get; init; }
    public required PayloadFormat Format { get; init; }
    public required IReadOnlyList<ProfileField> Fields { get; init; }
    public IReadOnlyList<ProfileFinding> Findings { get; init; } = [];
    public string? Sha256 { get; init; }

    /// <summary>Which part of the input was profiled, e.g. "POST /applications request body".</summary>
    public string? Root { get; init; }

    /// <summary>Other uploaded files this contract's references were read from.</summary>
    public IReadOnlyList<ProfileInput> Referenced { get; init; } = [];

    internal static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    internal static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

internal sealed class ContractField(string path, string name, string? parentPath, string? parentName)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public string? ParentPath { get; } = parentPath;
    public string? ParentName { get; } = parentName;
    public FieldNodeKind Kind { get; set; } = FieldNodeKind.Value;
    public Cardinality Cardinality { get; set; } = Cardinality.Single;
    public FieldDataType DataType { get; set; } = FieldDataType.Unknown;
    public string? Format { get; set; }
    public Requirement Required { get; set; } = Requirement.Unknown;
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public int? MaxScale { get; set; }
    public int? MaxOccurs { get; set; }
    public List<string> AllowedValues { get; set; } = [];
    public string? Description { get; set; }
    public string? SensitivityReason { get; set; }
    public Dictionary<ProfileAttribute, string> Details { get; } = [];

    public void MakeObject()
    {
        Kind = FieldNodeKind.Object;
        DataType = FieldDataType.Object;
    }

    public ProfileField ToField(InputKind kind, string input)
    {
        var isValue = Kind == FieldNodeKind.Value;
        var sensitivity = SensitivityReason ?? (isValue ? SensitiveDataPolicy.Default.MatchField(Name, ParentName) : null);
        if (sensitivity is not null)
        {
            Details[ProfileAttribute.Sensitivity] = sensitivity;
        }

        if (MinLength is not null || MaxLength is not null || MinValue is not null || MaxValue is not null || MaxScale is not null)
        {
            Details.TryAdd(ProfileAttribute.Constraints, "Declared length, range or scale.");
        }

        return new()
        {
            Path = Path,
            Name = Name,
            ParentPath = ParentPath,
            Kind = Kind,
            Cardinality = Cardinality,
            DataType = DataType,
            Format = Format,
            Required = Required,
            MinLength = isValue ? MinLength : null,
            MaxLength = isValue ? MaxLength : null,
            MinValue = isValue ? MinValue : null,
            MaxValue = isValue ? MaxValue : null,
            MaxScale = isValue ? MaxScale : null,
            MaxOccurs = Cardinality == Cardinality.Array ? MaxOccurs : null,
            AllowedValues = isValue ? [.. AllowedValues.Distinct(StringComparer.Ordinal)] : [],
            Sensitive = sensitivity is not null,
            SensitivityReason = sensitivity,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            SeenIn = [input],
            Provenance = [.. Details.OrderBy(d => d.Key).Select(d => new AttributeSource
            {
                Attribute = d.Key,
                Kind = kind,
                Inputs = [input],
                Detail = d.Value,
            })],
        };
    }
}

/// <summary>Collects the fields and findings of one contract input in declaration order.</summary>
internal sealed class ContractBuilder(string input, InputKind kind, PayloadFormat format)
{
    private readonly Dictionary<string, ContractField> _byPath = new(StringComparer.Ordinal);
    private readonly List<ContractField> _fields = [];
    private readonly List<ProfileFinding> _findings = [];

    public string Input => input;

    public ContractField Add(string path, string name, ContractField? parent)
    {
        if (!_byPath.TryGetValue(path, out var field))
        {
            field = new(path, name, parent?.Path, parent?.Name);
            _byPath.Add(path, field);
            _fields.Add(field);
        }

        return field;
    }

    public ContractField? Find(string path) => _byPath.GetValueOrDefault(path);

    public void Finding(ProfileFindingKind findingKind, string? path, string message)
    {
        if (!_findings.Any(f => f.Kind == findingKind && f.Path == path && f.Message == message))
        {
            _findings.Add(new() { Kind = findingKind, Path = path, Message = message, Inputs = [input] });
        }
    }

    public ContractDocument Build(string sha256, string? root) => new()
    {
        Name = input,
        Kind = kind,
        Format = format,
        Fields = [.. _fields.Select(f => f.ToField(kind, input))],
        Findings = [.. _findings],
        Sha256 = sha256,
        Root = root,
    };
}

internal static partial class JsonPaths
{
    public const string Root = "$";

    public static string Child(string prefix, string name) =>
        SimpleName().IsMatch(name)
            ? $"{prefix}.{name}"
            : $"{prefix}['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex SimpleName();
}
