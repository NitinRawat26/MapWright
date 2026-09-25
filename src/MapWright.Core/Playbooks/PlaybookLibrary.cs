namespace MapWright.Core.Playbooks;

/// <summary>A set of playbooks loaded from files. Later this is backed by the SQLite playbook store.</summary>
public sealed class PlaybookLibrary(IReadOnlyList<Playbook> playbooks)
{
    public IReadOnlyList<Playbook> All { get; } = playbooks;

    /// <summary>Per id: the published version, else the newest draft or in-review version. Retired and abandoned versions are skipped.</summary>
    public IReadOnlyList<Playbook> Active { get; } = [.. playbooks
        .Where(p => p.Status is not (PlaybookStatus.Retired or PlaybookStatus.Abandoned))
        .GroupBy(p => p.Id)
        .Select(g => g.FirstOrDefault(p => p.Status == PlaybookStatus.Published)
            ?? g.OrderByDescending(p => System.Version.TryParse(p.Version, out var v) ? v : new System.Version()).First())
        .OrderBy(p => p.Id, StringComparer.Ordinal)];

    public IEnumerable<Playbook> Domains => Active.Where(p => p.Domain is not null);

    /// <summary>Loads every playbook file (.yaml, .yml, .json) under the given files or directories.</summary>
    public static PlaybookLibrary Load(IEnumerable<string> paths) => new([.. Read(paths).Select(f => f.Playbook)]);

    public static IReadOnlyList<PlaybookFile> Read(IEnumerable<string> paths) => [.. Files(paths).Select(PlaybookSerializer.Read)];

    /// <summary>The given files, plus every .yaml, .yml and .json file under the given directories.</summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => PlaybookSerializer.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Order(StringComparer.Ordinal));
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
            else
            {
                throw new PlaybookException($"Playbook path not found: {path}");
            }
        }

        return files;
    }

    /// <summary>The best match across all active domain playbooks, or null.</summary>
    public DetectionResult? Detect(FieldContext field) => Domains
        .Select(p => PlaybookMatcher.Detect(p, field))
        .OfType<DetectionResult>()
        .OrderByDescending(r => r.Score)
        .ThenByDescending(r => r.MatchedTokens)
        .FirstOrDefault();
}
