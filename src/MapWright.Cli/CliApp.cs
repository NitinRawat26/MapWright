using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;

namespace MapWright.Cli;

public static class CliApp
{
    public const int Success = 0;
    public const int InvalidSpec = 1;
    public const int UsageError = 2;

    private static readonly string Usage = $"""
        MapWright — playbook-driven data mapping for merchant acquiring systems

        Usage:
          mapwright validate <spec.json>
          mapwright render <spec.json> [--out <dir>] [--format <list>]

        Options:
          --out <dir>       Output directory (default: directory of the spec)
          --format <list>   Comma-separated: {string.Join(",", MappingRenderers.All.Select(r => r.Format))} (default: all)
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? UsageError : Success;
        }

        return args[0] switch
        {
            "validate" => Validate(args[1..], stdout, stderr),
            "render" => Render(args[1..], stdout, stderr),
            _ => Fail(stderr, $"Unknown command '{args[0]}'."),
        };
    }

    private static int Validate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
        {
            return Fail(stderr, "validate expects exactly one spec path.");
        }

        return TryLoad(args[0], stdout, stderr, out _) ? Success : InvalidSpec;
    }

    private static int Render(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? specPath = null;
        string? outDir = null;
        string? formats = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--format" when i + 1 < args.Length:
                    formats = args[++i];
                    break;
                case var arg when !arg.StartsWith("--", StringComparison.Ordinal) && specPath is null:
                    specPath = arg;
                    break;
                default:
                    return Fail(stderr, $"Unexpected argument '{args[i]}'.");
            }
        }

        if (specPath is null)
        {
            return Fail(stderr, "render expects a spec path.");
        }

        var renderers = new List<IMappingRenderer>();
        foreach (var format in (formats ?? string.Join(",", MappingRenderers.All.Select(r => r.Format)))
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MappingRenderers.Find(format) is not { } renderer)
            {
                return Fail(stderr, $"Unknown format '{format}'.");
            }

            renderers.Add(renderer);
        }

        if (!TryLoad(specPath, stdout, stderr, out var document))
        {
            return InvalidSpec;
        }

        var directory = outDir ?? Path.GetDirectoryName(Path.GetFullPath(specPath))!;
        Directory.CreateDirectory(directory);
        var report = MappingReport.Build(document);

        foreach (var renderer in renderers)
        {
            var path = Path.Combine(directory, SafeFileName(document.Id) + renderer.FileExtension);
            using (var stream = File.Create(path))
            {
                renderer.Render(report, stream);
            }

            stdout.WriteLine($"Wrote {path}");
        }

        return Success;
    }

    private static bool TryLoad(string path, TextWriter stdout, TextWriter stderr, out MappingDocument document)
    {
        document = null!;
        if (!File.Exists(path))
        {
            stderr.WriteLine($"Spec not found: {path}");
            return false;
        }

        try
        {
            document = MappingSpecSerializer.Load(path);
        }
        catch (MappingSpecException ex)
        {
            stderr.WriteLine(ex.Message);
            return false;
        }

        var issues = MappingSpecValidator.Validate(document);
        foreach (var issue in issues)
        {
            (issue.Severity == IssueSeverity.Error ? stderr : stdout).WriteLine(issue);
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        if (errors > 0)
        {
            stderr.WriteLine($"{path}: {errors} error(s); fix them before rendering.");
            return false;
        }

        stdout.WriteLine($"{path}: valid ({document.Mappings.Count} mappings, {issues.Count} warning(s)).");
        return true;
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine("Run 'mapwright --help' for usage.");
        return UsageError;
    }
}
