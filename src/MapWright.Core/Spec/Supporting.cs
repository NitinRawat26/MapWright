namespace MapWright.Core.Spec;

public sealed record OrphanSourceField
{
    public required FieldDescriptor Field { get; init; }
    public string? SuggestedResolution { get; init; }
}

public sealed record Finding
{
    public required string Id { get; init; }
    public required FindingKind Kind { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> MappingIds { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
    public string? Resolution { get; init; }
}

public sealed record ValidationRun
{
    public required string Id { get; init; }
    public required DateTimeOffset RanAt { get; init; }
    public required string SamplePayload { get; init; }
    public required IReadOnlyList<ValidationResult> Results { get; init; }
}

public sealed record ValidationResult
{
    public required string MappingId { get; init; }
    public required ValidationOutcome Outcome { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Message { get; init; }
}

public sealed record ChangeLogEntry
{
    public required string Version { get; init; }
    public required DateOnly Date { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
}
