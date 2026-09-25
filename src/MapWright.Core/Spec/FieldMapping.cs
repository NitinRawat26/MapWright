namespace MapWright.Core.Spec;

/// <summary>One row of the mapping sheet. There is one row per target field.</summary>
public sealed record FieldMapping
{
    public required string Id { get; init; }
    public required MappingType Type { get; init; }

    /// <summary>Zero (unmapped/constant), one, or many (many-to-one) source fields.</summary>
    public IReadOnlyList<FieldDescriptor> Sources { get; init; } = [];
    public required FieldDescriptor Target { get; init; }

    public string? BusinessConcept { get; init; }
    public string? DomainPlaybook { get; init; }

    public Transformation Transformation { get; init; } = new() { Type = TransformationType.Direct };

    public required int ConfidencePercent { get; init; }
    public required string Reasoning { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    public Risk Risk { get; init; } = new();
    public Review Review { get; init; } = new() { Status = ReviewStatus.NeedsReview };

    /// <summary>Suggested resolution when the target field has no source (gap report).</summary>
    public string? SuggestedResolution { get; init; }
}

public sealed record FieldDescriptor
{
    public required string Name { get; init; }
    /// <summary>JSONPath for JSON systems, XPath for XML systems.</summary>
    public required string Path { get; init; }
    public required string DataType { get; init; }
    public string? Format { get; init; }
    public int? MaxLength { get; init; }
    public bool Required { get; init; }
    public Cardinality Cardinality { get; init; } = Cardinality.Single;
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
    /// <summary>Must already be masked for sensitive fields.</summary>
    public string? SampleValue { get; init; }
    public string? Description { get; init; }
}

public sealed record Transformation
{
    public required TransformationType Type { get; init; }
    /// <summary>Human-readable rule, e.g. "AnnualCardVolume ÷ 12".</summary>
    public string? Rule { get; init; }
    /// <summary>Machine-executable expression.</summary>
    public string? Expression { get; init; }
    /// <summary>Which source path feeds each name in <see cref="Expression"/>, e.g. annual = $.processing.annualCardVolume.</summary>
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }
    /// <summary>Target value shape for digit codes (9 = digit), e.g. "999999999" or "999-99-9999".</summary>
    public string? Pattern { get; init; }
    public string? Condition { get; init; }
    public string? DefaultValue { get; init; }
    public IReadOnlyList<ValueMapEntry> ValueMap { get; init; } = [];
}

public sealed record ValueMapEntry
{
    public required string SourceValue { get; init; }
    public required string TargetValue { get; init; }
    public string? Notes { get; init; }
}

public sealed record Evidence
{
    public required EvidenceKind Kind { get; init; }
    /// <summary>Where the evidence came from, e.g. "integration-guide.pdf p.12".</summary>
    public required string Reference { get; init; }
    public string? Detail { get; init; }
}

public sealed record Risk
{
    public RiskLevel DataLoss { get; init; } = RiskLevel.None;
    public string? DataLossNote { get; init; }
    public Sensitivity Sensitivity { get; init; } = Sensitivity.None;
    public IReadOnlyList<string> TargetValidationRules { get; init; } = [];
}

public sealed record Review
{
    public required ReviewStatus Status { get; init; }
    public string? Reviewer { get; init; }
    public DateOnly? ReviewedOn { get; init; }
    public string? Comments { get; init; }
    public string? OpenQuestion { get; init; }
}
