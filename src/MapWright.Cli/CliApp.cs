using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;

namespace MapWright.Cli;

public static class CliApp
{
    public const int Success = 0;
    public const int InvalidSpec = 1;
    public const int InvalidInput = 1;
    public const int UsageError = 2;

    private static readonly string Usage = $"""
        MapWright — playbook-driven data mapping for merchant acquiring systems

        Usage:
          mapwright validate <spec.json>
          mapwright render <spec.json> [--out <dir>] [--format <list>]
          mapwright profile <sample|dir>... --system <name> [--version <v>] [--description <text>]
                            [--out <profile.json>] [--no-values]

        Render options:
          --out <dir>       Output directory (default: directory of the spec)
          --format <list>   Comma-separated: {string.Join(",", MappingRenderers.All.Select(r => r.Format))} (default: all)

        Profile options:
          <sample|dir>      JSON or XML sample payloads; directories contribute their *.json and *.xml files
          --system <name>   System name recorded in the profile (required)
          --out <file>      Write the profile here (default: print to stdout)
          --no-values       Do not store sample or observed values in the profile
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
            "profile" => Profile(args[1..], stdout, stderr),
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

    private static int Profile(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? system = null;
        string? version = null;
        string? description = null;
        string? outPath = null;
        var retainValues = true;
        var inputs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--system" when i + 1 < args.Length:
                    system = args[++i];
                    break;
                case "--version" when i + 1 < args.Length:
                    version = args[++i];
                    break;
                case "--description" when i + 1 < args.Length:
                    description = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--no-values":
                    retainValues = false;
                    break;
                case var arg when !arg.StartsWith("--", StringComparison.Ordinal):
                    inputs.Add(arg);
                    break;
                default:
                    return Fail(stderr, $"Unexpected argument '{args[i]}'.");
            }
        }

        if (system is null)
        {
            return Fail(stderr, "profile expects --system <name>.");
        }

        if (inputs.Count == 0)
        {
            return Fail(stderr, "profile expects at least one sample file or directory.");
        }

        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".xml")
                    .Order(StringComparer.Ordinal));
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
            else
            {
                stderr.WriteLine($"Sample not found: {input}");
                return InvalidInput;
            }
        }

        if (files.Count == 0)
        {
            stderr.WriteLine("No .json or .xml samples found.");
            return InvalidInput;
        }

        SystemProfile profile;
        try
        {
            profile = ProfileBuilder.Build(
                new()
                {
                    System = system,
                    Version = version,
                    Description = description,
                    Samples = [.. files.Select(f => new SampleInput(Path.GetFileName(f), File.ReadAllText(f)))],
                },
                new() { RetainValues = retainValues });
        }
        catch (ProfileException ex)
        {
            stderr.WriteLine(ex.Message);
            return InvalidInput;
        }

        if (outPath is null)
        {
            stdout.WriteLine(ProfileSerializer.Serialize(profile));
            return Success;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(outPath)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        ProfileSerializer.Save(profile, outPath);
        stdout.WriteLine(
            $"Wrote {outPath}: {profile.Fields.Count} field(s) from {profile.Inputs.Count} {profile.Format.ToString().ToUpperInvariant()} sample(s), " +
            $"{profile.Fields.Count(f => f.Sensitive)} sensitive field(s) masked, {profile.Findings.Count} finding(s).");
        foreach (var finding in profile.Findings)
        {
            stdout.WriteLine($"  {finding.Kind}{(finding.Path is null ? "" : $" [{finding.Path}]")} {finding.Message}");
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
