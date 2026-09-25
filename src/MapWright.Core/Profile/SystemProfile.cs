using MapWright.Core.Spec;

namespace MapWright.Core.Profile;

/// <summary>
/// Normalized description of one system's contract, merged from all of its inputs: sample payloads,
/// JSON Schema, OpenAPI, XSD, WSDL and field specs.
/// </summary>
public sealed record SystemProfile
{
    public const string CurrentSpecVersion = "1.0";

    public required string SpecVersion { get; init; }
    public required string System { get; init; }
    public string? Version { get; init; }
    public required PayloadFormat Format { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<ProfileInput> Inputs { get; init; } = [];
    public required IReadOnlyList<ProfileField> Fields { get; init; }
    public IReadOnlyList<ProfileFinding> Findings { get; init; } = [];
}

public sealed record ProfileInput
{
    public required string Name { get; init; }
    public required InputKind Kind { get; init; }
    public PayloadFormat? Format { get; init; }
    public string? Sha256 { get; init; }
    public string? Notes { get; init; }
}

public sealed record ProfileField
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string? ParentPath { get; init; }
    public required FieldNodeKind Kind { get; init; }
    public Cardinality Cardinality { get; init; } = Cardinality.Single;
    public required FieldDataType DataType { get; init; }
    public string? Format { get; init; }
    public Requirement Required { get; init; } = Requirement.Unknown;
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }
    public int? MaxScale { get; init; }
    public int? MaxOccurs { get; init; }
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
    public IReadOnlyList<string> ObservedValues { get; init; } = [];
    public bool ObservedValuesTruncated { get; init; }
    public IReadOnlyList<string> ValueShapes { get; init; } = [];
    public string? SampleValue { get; init; }
    public bool Sensitive { get; init; }
    public string? SensitivityReason { get; init; }
    public string? Description { get; init; }
    public Presence? Presence { get; init; }
    public IReadOnlyList<string> SeenIn { get; init; } = [];
    public IReadOnlyList<AttributeSource> Provenance { get; init; } = [];
}

/// <summary>How often a field was observed relative to its parent.</summary>
public sealed record Presence
{
    public required int ParentInstances { get; init; }
    public required int PresentInstances { get; init; }
    public required int Occurrences { get; init; }
    public int NullCount { get; init; }
    public int EmptyCount { get; init; }
}

/// <summary>Which input established a field attribute, so conflicting inputs can be explained.</summary>
public sealed record AttributeSource
{
    public required ProfileAttribute Attribute { get; init; }
    public required InputKind Kind { get; init; }
    public IReadOnlyList<string> Inputs { get; init; } = [];
    public string? Detail { get; init; }
}

public sealed record ProfileFinding
{
    public required ProfileFindingKind Kind { get; init; }
    public string? Path { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> Inputs { get; init; } = [];
}

public enum FieldNodeKind
{
    Object,
    Value,
}

public enum FieldDataType
{
    Unknown,
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Object,
}

public enum Requirement
{
    Unknown,
    Optional,
    LikelyRequired,
    Required,
}

public enum ProfileAttribute
{
    DataType,
    Cardinality,
    Required,
    Format,
    Sensitivity,
    AllowedValues,
    Constraints,
}

public enum ProfileFindingKind
{
    TypeConflict,
    KindConflict,
    InferredCardinality,
    AmbiguousDateFormat,
    MixedFormats,
    SoapEnvelopeUnwrapped,
    NamespacesIgnored,
    MixedContentIgnored,
    ContractMismatch,
    UndeclaredField,
    UnresolvedReference,
    SchemaSimplified,
    AiExtracted,
}
