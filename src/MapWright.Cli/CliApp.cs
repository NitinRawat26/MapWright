using MapWright.Ai;
using MapWright.Core;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;
using System.Text.Json;

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
          mapwright playbook validate <playbook|dir>...
          mapwright playbook test <playbook|dir>...
          mapwright playbook detect <profile.json> [--playbooks <playbook|dir>]... [--ai ask|yes|no]
                            [--out <report.json>]

        Render options:
          --out <dir>       Output directory (default: directory of the spec)
          --format <list>   Comma-separated: {string.Join(",", MappingRenderers.All.Select(r => r.Format))} (default: all)

        Profile options:
          <sample|dir>      JSON or XML sample payloads; directories contribute their *.json and *.xml files
          --system <name>   System name recorded in the profile (required)
          --out <file>      Write the profile here (default: print to stdout)
          --no-values       Do not store sample or observed values in the profile

        Playbook commands:
          validate          Check playbooks and the references between them
          test              Validate, then run each playbook's tests and rule examples
          detect            Show which business concept each profile field is recognised as
          --playbooks       Playbook files or directories for detect (default: ./playbooks)
          --ai <mode>       After the playbooks, offer AI for the remaining fields: ask (default), yes or no
          --out <file>      Write playbook matches, AI suggestions and remaining fields as JSON

        AI (optional; without it MapWright uses playbooks only):
          {AiProviders.VertexProjectVariable}    Google Cloud project; enables Vertex AI (credentials from
                                       GOOGLE_APPLICATION_CREDENTIALS or other Application Default Credentials)
          {AiProviders.VertexLocationVariable}   Default {VertexAiOptions.DefaultLocation}
          {AiProviders.VertexModelVariable}      Default {VertexAiOptions.DefaultModel}
          {AiProviders.OllamaUrlVariable}        Ollama server URL, e.g. http://localhost:11434; the fallback after Vertex AI
          {AiProviders.OllamaModelVariable}      Default {OllamaOptions.DefaultModel}
          {AiProviders.TimeoutVariable} Per-request timeout (default {(int)AiProviders.DefaultTimeout.TotalSeconds})
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, TextReader.Null, stdout, stderr, ai: null);

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, IAiProvider? ai)
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
            "playbook" => Playbook(args[1..], stdin, stdout, stderr, ai),
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

    private static int Playbook(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, IAiProvider? ai)
    {
        if (args.Length == 0)
        {
            return Fail(stderr, "playbook expects validate, test or detect.");
        }

        return args[0] switch
        {
            "validate" or "test" when args.Length > 1 && !args[1..].Any(a => a.StartsWith("--", StringComparison.Ordinal)) =>
                ValidatePlaybooks(args[1..], runTests: args[0] == "test", stdout, stderr),
            "validate" or "test" => Fail(stderr, $"playbook {args[0]} expects playbook files or directories."),
            "detect" => Detect(args[1..], stdin, stdout, stderr, ai),
            _ => Fail(stderr, $"Unknown playbook command '{args[0]}'."),
        };
    }

    private static int ValidatePlaybooks(string[] paths, bool runTests, TextWriter stdout, TextWriter stderr)
    {
        if (!TryLoadPlaybooks(paths, stdout, stderr, out var library))
        {
            return InvalidInput;
        }

        if (!runTests)
        {
            return Success;
        }

        var results = library.All.SelectMany(PlaybookTestRunner.Run).ToList();
        foreach (var failure in results.Where(r => !r.Passed))
        {
            stderr.WriteLine(failure);
        }

        var failed = results.Count(r => !r.Passed);
        (failed == 0 ? stdout : stderr).WriteLine($"{results.Count - failed} of {results.Count} playbook test(s) passed.");
        return failed == 0 ? Success : InvalidInput;
    }

    private static int Detect(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, IAiProvider? ai)
    {
        string? profilePath = null;
        string? outPath = null;
        var aiMode = "ask";
        var playbookPaths = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--playbooks" when i + 1 < args.Length:
                    playbookPaths.Add(args[++i]);
                    break;
                case "--ai" when i + 1 < args.Length && args[i + 1] is "ask" or "yes" or "no":
                    aiMode = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case var arg when !arg.StartsWith("--", StringComparison.Ordinal) && profilePath is null:
                    profilePath = arg;
                    break;
                default:
                    return Fail(stderr, $"Unexpected argument '{args[i]}'.");
            }
        }

        if (profilePath is null)
        {
            return Fail(stderr, "playbook detect expects a profile path.");
        }

        SystemProfile profile;
        try
        {
            profile = ProfileSerializer.Load(profilePath);
        }
        catch (Exception ex) when (ex is ProfileException or IOException)
        {
            stderr.WriteLine(ex.Message);
            return InvalidInput;
        }

        if (!TryLoadPlaybooks(playbookPaths.Count > 0 ? playbookPaths : ["playbooks"], TextWriter.Null, stderr, out var library))
        {
            return InvalidInput;
        }

        var fields = FieldContext.FromProfile(profile).Where(f => f.Kind == FieldNodeKind.Value || f.Cardinality == Cardinality.Array).ToList();
        var recognised = new List<PlaybookMatch>();
        var remaining = new List<string>();
        stdout.WriteLine($"{profile.System}: {fields.Count} field(s) checked against {library.Domains.Count()} domain playbook(s).");
        foreach (var field in fields)
        {
            if (library.Detect(field) is not { } result)
            {
                remaining.Add(field.Path!);
                stdout.WriteLine($"  {field.Path}  -");
                continue;
            }

            recognised.Add(new(field.Path!, result));
            var qualifiers = result.Qualifiers.Count == 0 ? "" : $" [{string.Join(", ", result.Qualifiers.Select(q => $"{q.Key}={q.Value}"))}]";
            stdout.WriteLine($"  {field.Path}  {result.BusinessConcept}{qualifiers} {result.Score}%{(result.RequiresReview ? " review" : "")}");
            foreach (var question in result.Questions)
            {
                stdout.WriteLine($"      ? {question}");
            }
        }

        stdout.WriteLine($"{recognised.Count} of {fields.Count} field(s) recognised; {remaining.Count} remaining.");

        var suggestions = new List<AiSuggestion>();
        if (remaining.Count > 0 && aiMode != "no")
        {
            if (DecodeWithAi(profile, remaining, library, aiMode, ai, stdin, stdout, stderr) is { } decoded)
            {
                suggestions.AddRange(decoded.Suggestions);
                remaining = [.. decoded.Unresolved];
            }
        }

        if (outPath is not null)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(outPath)) is { } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var report = new DecodeReport { System = profile.System, Recognised = recognised, AiSuggestions = suggestions, Remaining = remaining };
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, MapWrightJson.Options) + Environment.NewLine);
            stdout.WriteLine($"Wrote {outPath}");
        }

        return Success;
    }

    private static AiDecodeResult? DecodeWithAi(
        SystemProfile profile, IReadOnlyList<string> remaining, PlaybookLibrary library, string mode,
        IAiProvider? ai, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (ai is null)
        {
            if (mode == "yes")
            {
                stderr.WriteLine($"No AI provider is configured (set {AiProviders.VertexProjectVariable} or {AiProviders.OllamaUrlVariable}); continuing with playbooks only.");
            }

            return null;
        }

        if (mode == "ask")
        {
            stdout.Write($"Do you want to use AI to decode the remaining {remaining.Count} field(s)? Masked field details are sent to {ai.Name}. [y/N] ");
            stdout.Flush();
            var answer = stdin.ReadLine()?.Trim();
            stdout.WriteLine();
            if (answer is null || !(answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                stdout.WriteLine("Skipped AI; continuing with playbooks only.");
                return null;
            }
        }

        var cap = library.Active
            .SelectMany(p => p.Process?.Steps ?? [])
            .FirstOrDefault(s => s.Kind == StepKind.AiAssist)?.MaxConfidence ?? AiFieldAssistant.DefaultMaxConfidence;

        AiDecodeResult result;
        try
        {
            result = new AiFieldAssistant(ai, cap).DecodeAsync(profile, remaining, library.Domains).GetAwaiter().GetResult();
        }
        catch (AiProviderException ex)
        {
            stderr.WriteLine($"AI assist failed: {ex.Message}");
            stderr.WriteLine("Continuing with playbooks only.");
            return null;
        }

        foreach (var warning in result.Warnings)
        {
            stderr.WriteLine($"  warning: {warning}");
        }

        stdout.WriteLine($"AI suggestions (capped at {cap}%, all need review):");
        foreach (var s in result.Suggestions)
        {
            var concept = s.BusinessConcept ?? (s.ProposedConcept is { } proposed ? $"new: {proposed}" : "unclassified");
            stdout.WriteLine($"  {s.Path}  {concept} {s.ConfidencePercent}% review ({s.Provider}/{s.Model})");
            stdout.WriteLine($"      {s.Meaning}. {s.Reasoning}");
            if (s.Question is { } question)
            {
                stdout.WriteLine($"      ? {question}");
            }
        }

        stdout.WriteLine($"{result.Suggestions.Count} AI suggestion(s); {result.Unresolved.Count} field(s) still unresolved.");
        return result;
    }

    private static bool TryLoadPlaybooks(IReadOnlyList<string> paths, TextWriter stdout, TextWriter stderr, out PlaybookLibrary library)
    {
        library = null!;
        try
        {
            library = PlaybookLibrary.Load(paths);
        }
        catch (Exception ex) when (ex is PlaybookException or IOException)
        {
            stderr.WriteLine(ex.Message);
            return false;
        }

        var issues = PlaybookValidator.Validate(library.All);
        foreach (var issue in issues)
        {
            (issue.Severity == IssueSeverity.Error ? stderr : stdout).WriteLine(issue);
        }

        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        if (errors > 0)
        {
            stderr.WriteLine($"{errors} playbook error(s).");
            return false;
        }

        stdout.WriteLine($"{library.All.Count} playbook(s) valid ({issues.Count} warning(s)).");
        return true;
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
