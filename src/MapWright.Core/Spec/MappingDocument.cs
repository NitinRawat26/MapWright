namespace MapWright.Core.Spec;

/// <summary>
/// Machine-readable mapping spec between two systems. This is the single source of truth;
/// every human-facing output (Excel, CSV, HTML) is rendered from it.
/// </summary>
public sealed record MappingDocument
{
    public const string CurrentSpecVersion = "1.0";

    public required string SpecVersion { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }

    public required SystemRef Source { get; init; }
    public required SystemRef Target { get; init; }

    public ConfidencePolicy ConfidencePolicy { get; init; } = new();

    public IReadOnlyList<InputArtifact> Inputs { get; init; } = [];
    public IReadOnlyList<PlaybookRef> Playbooks { get; init; } = [];
    public required IReadOnlyList<FieldMapping> Mappings { get; init; }
    public IReadOnlyList<OrphanSourceField> OrphanSourceFields { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<ValidationRun> ValidationRuns { get; init; } = [];
    public IReadOnlyList<ChangeLogEntry> ChangeLog { get; init; } = [];
}

public sealed record SystemRef
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required PayloadFormat Format { get; init; }
    public string? Description { get; init; }
}

public sealed record ConfidencePolicy
{
    public int HighThreshold { get; init; } = 85;
    public int MediumThreshold { get; init; } = 60;

    public ConfidenceBand BandFor(int confidencePercent) =>
        confidencePercent >= HighThreshold ? ConfidenceBand.High
        : confidencePercent >= MediumThreshold ? ConfidenceBand.Medium
        : ConfidenceBand.Low;
}

public sealed record InputArtifact
{
    public required string Name { get; init; }
    public required InputKind Kind { get; init; }
    public required SystemSide Side { get; init; }
    public string? Notes { get; init; }
}

public sealed record PlaybookRef
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}
