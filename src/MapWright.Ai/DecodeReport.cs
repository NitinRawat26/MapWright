using MapWright.Core.Playbooks;

namespace MapWright.Ai;

/// <summary>What was recognised in one profile: playbook matches first, then any AI suggestions for the rest.</summary>
public sealed record DecodeReport
{
    public required string System { get; init; }
    public required IReadOnlyList<PlaybookMatch> Recognised { get; init; }
    public IReadOnlyList<AiSuggestion> AiSuggestions { get; init; } = [];
    public IReadOnlyList<string> Remaining { get; init; } = [];
}

public sealed record PlaybookMatch(string Path, DetectionResult Detection);
