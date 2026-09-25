using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Ai;

/// <summary>An AI proposal for a field the playbooks did not recognise. Always needs review.</summary>
public sealed record AiSuggestion
{
    public required string Path { get; init; }

    /// <summary>A concept or concept attribute defined by a playbook, e.g. <c>Principal.Email</c>.</summary>
    public string? BusinessConcept { get; init; }

    /// <summary>The playbook that defines <see cref="BusinessConcept"/>.</summary>
    public string? DomainPlaybook { get; init; }

    /// <summary>A concept no playbook defines yet: a candidate for a new playbook or attribute.</summary>
    public string? ProposedConcept { get; init; }

    public required string Meaning { get; init; }
    public required int ConfidencePercent { get; init; }
    public required string Reasoning { get; init; }
    public string? Question { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public ReviewStatus Review { get; init; } = ReviewStatus.NeedsReview;
}

public sealed record AiDecodeResult
{
    public IReadOnlyList<AiSuggestion> Suggestions { get; init; } = [];

    /// <summary>Fields the AI gave no usable answer for.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Asks an AI provider about profile fields the playbooks left unmatched. The AI sees only masked field
/// metadata and the playbooks' concepts and guidance; its answers are capped below auto-accept.
/// </summary>
public sealed partial class AiFieldAssistant(IAiProvider provider, int maxConfidence = AiFieldAssistant.DefaultMaxConfidence, int batchSize = 40)
{
    public const int DefaultMaxConfidence = 70;
    public const int MaxValuesPerField = 10;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Instructions = """
        You help map merchant-acquiring data between systems. For each field in "fields", decide what business
        data it holds. Use the concepts defined by the playbooks in "concepts" when one fits and answer with
        "Concept.Attribute" (or just "Concept" for a collection or group). When none fits, leave "concept" empty
        and put a short "Concept.Attribute" name of your own in "newConcept". Follow each playbook's guidance.
        Values of sensitive fields are withheld; never guess or invent values. Give "confidence" from 0 to 100,
        one or two sentences of "reasoning" that cite the field name, parents, children or values you relied on,
        and a "question" for a reviewer when you are unsure. Only short code-like values are shown; free-text values
        are withheld. Return one suggestion per field path, using the
        exact path given.
        """;

    public IAiProvider Provider => provider;
    public int MaxConfidence => maxConfidence;

    public async Task<AiDecodeResult> DecodeAsync(
        SystemProfile profile, IReadOnlyCollection<string> paths, IEnumerable<Playbook> domainPlaybooks, CancellationToken cancellationToken = default)
    {
        var playbooks = domainPlaybooks.Where(p => p.Domain is not null).ToList();
        var concepts = ConceptCatalog(playbooks);
        var contexts = FieldContext.FromProfile(profile).ToDictionary(f => f.Path!, StringComparer.Ordinal);
        var fields = profile.Fields.Where(f => paths.Contains(f.Path)).ToList();

        var suggestions = new List<AiSuggestion>();
        var warnings = new List<string>();
        foreach (var batch in fields.Chunk(batchSize))
        {
            var input = new JsonObject
            {
                ["system"] = profile.System,
                ["format"] = profile.Format.ToString().ToLowerInvariant(),
                ["concepts"] = new JsonArray([.. playbooks.Select(Describe)]),
                ["fields"] = new JsonArray([.. batch.Select(f => MaskedField(f, contexts[f.Path]))]),
            };

            var reply = await provider.GenerateJsonAsync(new(Instructions, input.ToJsonString(), ResponseSchema([.. batch.Select(f => f.Path)])), cancellationToken).ConfigureAwait(false);
            suggestions.AddRange(Parse(reply, batch.Select(f => f.Path).ToHashSet(StringComparer.Ordinal), concepts, warnings)
                .Where(s => suggestions.All(existing => existing.Path != s.Path)));
        }

        var answered = suggestions.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        return new()
        {
            Suggestions = suggestions,
            Unresolved = [.. fields.Select(f => f.Path).Where(p => !answered.Contains(p))],
            Warnings = warnings,
        };
    }

    /// <summary>Field metadata sent to the AI. Sensitive fields carry no values, samples or ranges.</summary>
    internal static JsonObject MaskedField(ProfileField field, FieldContext context)
    {
        var sensitive = field.Sensitive || SensitiveDataPolicy.Default.MatchField(field.Name, context.Ancestors.FirstOrDefault()) is not null;
        var obj = new JsonObject
        {
            ["path"] = field.Path,
            ["name"] = field.Name,
            ["parents"] = new JsonArray([.. context.Ancestors.Select(a => (JsonNode)a)]),
            ["children"] = new JsonArray([.. context.Children.Select(c => (JsonNode)c)]),
            ["kind"] = JsonNaming(field.Kind),
            ["dataType"] = JsonNaming(field.DataType),
            ["cardinality"] = JsonNaming(field.Cardinality),
            ["sensitive"] = sensitive,
        };
        if (field.Description is { } description)
        {
            obj["description"] = description;
        }

        if (field.Format is { } format)
        {
            obj["dataFormat"] = format;
        }

        if (field.ValueShapes.Count > 0)
        {
            obj["valueShapes"] = new JsonArray([.. field.ValueShapes.Take(MaxValuesPerField).Select(s => (JsonNode)s)]);
        }

        if (!sensitive)
        {
            var values = field.AllowedValues.Concat(field.ObservedValues).Distinct(StringComparer.Ordinal).Take(MaxValuesPerField).ToList();
            if (values.Count > 0 && values.All(IsCodeLike))
            {
                obj["values"] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
            }

            if (field.MinValue is { } min)
            {
                obj["minValue"] = min;
            }

            if (field.MaxValue is { } max)
            {
                obj["maxValue"] = max;
            }
        }

        return obj;
    }

    /// <summary>
    /// Short codes and numbers (e.g. <c>CORP</c>, <c>5411</c>, <c>0.5</c>) help the AI and identify no one.
    /// Free text such as names, e-mail addresses, phone numbers and addresses is never sent.
    /// </summary>
    internal static bool IsCodeLike(string value) =>
        CodeLike().IsMatch(value) && !SensitiveDataPolicy.LooksLikeIdentifier(value);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,12}$")]
    private static partial Regex CodeLike();

    /// <summary>A string schema that allows only <paramref name="values"/>.</summary>
    internal static JsonObject OneOf(IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]),
    };

    /// <summary>The answer's shape: at most one suggestion per field, and only for the paths asked about.</summary>
    internal static JsonObject ResponseSchema(IReadOnlyList<string> paths) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["suggestions"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = paths.Count,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = OneOf(paths),
                        ["concept"] = new JsonObject { ["type"] = "string" },
                        ["newConcept"] = new JsonObject { ["type"] = "string" },
                        ["meaning"] = new JsonObject { ["type"] = "string" },
                        ["confidence"] = new JsonObject { ["type"] = "integer" },
                        ["reasoning"] = new JsonObject { ["type"] = "string" },
                        ["question"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("path", "meaning", "confidence", "reasoning"),
                },
            },
        },
        ["required"] = new JsonArray("suggestions"),
    };

    private IEnumerable<AiSuggestion> Parse(
        AiReply reply, HashSet<string> paths, Dictionary<string, (string Concept, string Playbook)> concepts, List<string> warnings)
    {
        ReplyDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ReplyDto>(reply.Json, ReadOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"{reply.Provider}: unreadable answer ({ex.Message}).");
            yield break;
        }

        foreach (var item in dto?.Suggestions ?? [])
        {
            if (item.Path is null || !paths.Contains(item.Path))
            {
                warnings.Add($"{reply.Provider}: ignored an answer for unknown path '{item.Path}'.");
                continue;
            }

            string? concept = null, playbook = null, proposed = null;
            if (Blank(item.Concept) is { } named)
            {
                if (concepts.TryGetValue(named, out var known))
                {
                    (concept, playbook) = known;
                }
                else
                {
                    proposed = named;
                }
            }

            proposed ??= concept is null ? Blank(item.NewConcept) : null;
            if (concept is null && proposed is null && Blank(item.Meaning) is null)
            {
                continue;
            }

            var confidence = Math.Clamp(item.Confidence ?? 0, 0, maxConfidence);
            yield return new()
            {
                Path = item.Path,
                BusinessConcept = concept,
                DomainPlaybook = playbook,
                ProposedConcept = proposed,
                Meaning = Blank(item.Meaning) ?? proposed ?? concept!,
                ConfidencePercent = confidence,
                Reasoning = Blank(item.Reasoning) ?? "No reasoning given.",
                Question = Blank(item.Question),
                Provider = reply.Provider,
                Model = reply.Model,
            };
        }
    }

    private static Dictionary<string, (string Concept, string Playbook)> ConceptCatalog(IEnumerable<Playbook> playbooks)
    {
        var catalog = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var playbook in playbooks)
        {
            var concept = playbook.Domain!.Concept;
            catalog.TryAdd(concept.Name, (concept.Name, playbook.Reference));
            foreach (var attribute in concept.Attributes)
            {
                catalog.TryAdd($"{concept.Name}.{attribute.Name}", ($"{concept.Name}.{attribute.Name}", playbook.Reference));
            }
        }

        return catalog;
    }

    internal static JsonObject Describe(Playbook playbook)
    {
        var domain = playbook.Domain!;
        var obj = new JsonObject
        {
            ["playbook"] = playbook.Reference,
            ["concept"] = domain.Concept.Name,
            ["attributes"] = new JsonArray([.. domain.Concept.Attributes.Select(a => (JsonNode)(a.Description is null ? a.Name : $"{a.Name}: {a.Description}"))]),
        };
        if ((domain.Concept.Description ?? playbook.Description) is { } description)
        {
            obj["description"] = description;
        }

        if (domain.AiGuidance is { } guidance)
        {
            obj["guidance"] = guidance;
        }

        return obj;
    }

    private static string JsonNaming<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ReplyDto(IReadOnlyList<SuggestionDto>? Suggestions);

    private sealed record SuggestionDto(
        string? Path, string? Concept, string? NewConcept, string? Meaning, int? Confidence, string? Reasoning, string? Question);
}
