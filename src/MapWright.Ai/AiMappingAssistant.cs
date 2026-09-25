using System.Text.Json;
using System.Text.Json.Nodes;
using MapWright.Core.Matching;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Ai;

public sealed record AiPairingResult
{
    /// <summary>The mapping with AI-suggested rows in place of the unmapped rows they resolve.</summary>
    public required MappingDocument Document { get; init; }

    /// <summary>Ids of the rows the AI filled in.</summary>
    public IReadOnlyList<string> Suggested { get; init; } = [];

    /// <summary>Target paths still unmapped after the AI pass.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Asks an AI provider to pick source fields for target fields the playbooks left unmapped. It sees only masked
/// field metadata; every row it fills in is capped below auto-accept and needs review.
/// </summary>
public sealed class AiMappingAssistant(IAiProvider provider, int maxConfidence = AiFieldAssistant.DefaultMaxConfidence)
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Instructions = """
        You help map merchant-acquiring data from a source system to a target system. For each field in "targets",
        choose the source field(s) in "sources" that supply it, using names, parents, children, types, value shapes,
        code values and the playbooks' concepts and guidance. Prefer sources marked "used": false, but a used source
        may also feed another target. Give "transformation" as one of: direct, rename, typeCast, unitConversion,
        periodConversion, concat, split, aggregate, enumMap, lookup, conditional, default, derived; and an
        "expression" when values must be calculated. Values of sensitive fields are withheld; never invent values.
        Give "confidence" from 0 to 100, one or two sentences of "reasoning", and a "question" for a reviewer when
        you are unsure. If no source fits a target, return it with an empty "sources" list. Use the exact paths given.
        """;

    public IAiProvider Provider => provider;

    public async Task<AiPairingResult> PairAsync(
        MappingDocument document, SystemProfile source, SystemProfile target, IEnumerable<Playbook> domainPlaybooks, CancellationToken cancellationToken = default)
    {
        var unmapped = document.Mappings.Where(m => m.Type == MappingType.Unmapped).ToList();
        if (unmapped.Count == 0)
        {
            return new() { Document = document };
        }

        var sourceFields = source.Fields.Where(f => f.Kind == FieldNodeKind.Value).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var targetFields = target.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var used = document.Mappings.SelectMany(m => m.Sources).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var sourceContexts = FieldContext.FromProfile(source).ToDictionary(f => f.Path!, StringComparer.Ordinal);
        var targetContexts = FieldContext.FromProfile(target).ToDictionary(f => f.Path!, StringComparer.Ordinal);
        List<string> targetPaths = [.. unmapped.Select(m => m.Target.Path).Where(targetFields.ContainsKey).Distinct(StringComparer.Ordinal)];

        var input = new JsonObject
        {
            ["sourceSystem"] = source.System,
            ["targetSystem"] = target.System,
            ["concepts"] = new JsonArray([.. domainPlaybooks.Where(p => p.Domain is not null).Select(AiFieldAssistant.Describe)]),
            ["targets"] = new JsonArray([.. targetPaths.Select(p => (JsonNode)AiFieldAssistant.MaskedField(targetFields[p], targetContexts[p]))]),
            ["sources"] = new JsonArray([.. sourceFields.Values.Select(f =>
            {
                var field = AiFieldAssistant.MaskedField(f, sourceContexts[f.Path]);
                field["used"] = used.Contains(f.Path);
                return (JsonNode)field;
            })]),
        };

        var reply = await provider.GenerateJsonAsync(new(Instructions, input.ToJsonString(), ResponseSchema(targetPaths, [.. sourceFields.Keys])), cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var pairings = Parse(reply, warnings);

        var rows = document.Mappings.ToList();
        var suggested = new List<string>();
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pairing in pairings)
        {
            if (!answered.Add(pairing.Target!))
            {
                warnings.Add($"{reply.Provider}: ignored a repeated pairing for '{pairing.Target}'.");
                continue;
            }

            var index = rows.FindIndex(m => m.Type == MappingType.Unmapped && m.Target.Path == pairing.Target);
            if (index < 0)
            {
                warnings.Add($"{reply.Provider}: ignored a pairing for '{pairing.Target}', which is not an unmapped target field.");
                continue;
            }

            var sources = (pairing.Sources ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
            if (sources.Count == 0)
            {
                continue;
            }

            if (sources.FirstOrDefault(p => !sourceFields.ContainsKey(p)) is { } unknown)
            {
                warnings.Add($"{reply.Provider}: ignored a pairing for '{pairing.Target}' with unknown source '{unknown}'.");
                continue;
            }

            rows[index] = Suggest(rows[index], [.. sources.Select(p => sourceFields[p])], pairing, reply);
            suggested.Add(rows[index].Id);
        }

        var nowUsed = rows.SelectMany(m => m.Sources).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        List<string> unresolved = [.. rows.Where(m => m.Type == MappingType.Unmapped).Select(m => m.Target.Path)];
        return new()
        {
            Document = document with
            {
                Mappings = rows,
                OrphanSourceFields = [.. document.OrphanSourceFields.Where(o => !nowUsed.Contains(o.Field.Path))],
                AiPass = new()
                {
                    Provider = $"{reply.Provider}/{reply.Model}",
                    MaxConfidence = maxConfidence,
                    SuggestedRows = suggested,
                    Unmatched = unresolved,
                    Warnings = warnings,
                },
            },
            Suggested = suggested,
            Unresolved = unresolved,
            Warnings = warnings,
        };
    }

    private FieldMapping Suggest(FieldMapping row, IReadOnlyList<ProfileField> sources, PairingDto pairing, AiReply reply)
    {
        var transformation = Enum.GetValues<TransformationType>()
            .Select(t => (TransformationType?)t)
            .FirstOrDefault(t => t.ToString()!.Equals(pairing.Transformation?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? (sources is [var only] && only.Name == row.Target.Name ? TransformationType.Direct : TransformationType.Rename);
        var reasoning = Blank(pairing.Reasoning) ?? "No reasoning given.";
        return row with
        {
            Type = sources.Count > 1 ? MappingType.ManyToOne : MappingType.OneToOne,
            Sources = [.. sources.Select(f => Masked(MappingGenerator.Describe(f)))],
            Target = Masked(row.Target),
            Transformation = new() { Type = transformation, Expression = Blank(pairing.Expression) },
            ConfidencePercent = Math.Clamp(pairing.Confidence ?? 0, 0, maxConfidence),
            Reasoning = $"AI suggestion ({reply.Provider}/{reply.Model}); no playbook rule covers it. {reasoning}",
            Evidence = [new() { Kind = EvidenceKind.AiSuggestion, Reference = $"{reply.Provider}/{reply.Model}", Detail = reasoning }],
            Review = new() { Status = ReviewStatus.NeedsReview, OpenQuestion = Blank(pairing.Question) },
            SuggestedResolution = "Confirm the AI suggestion, then add the terms or rule to a playbook so it is found without AI next time.",
        };
    }

    /// <summary>AI rows are not classified by a playbook, so any sample with more than 4 digits is masked.</summary>
    private static FieldDescriptor Masked(FieldDescriptor field) =>
        field.SampleValue is { } sample && sample.Count(char.IsAsciiDigit) > MappingSpecValidator.MaxVisibleDigitsInSensitiveSample
            ? field with { SampleValue = SensitiveDataPolicy.Mask(sample) }
            : field;

    /// <summary>
    /// The answer's shape. Paths and transformation names are listed as the only allowed values, and there is at
    /// most one pairing per target, so a provider that enforces the schema can't invent paths or keep repeating.
    /// </summary>
    internal static JsonObject ResponseSchema(IReadOnlyList<string> targets, IReadOnlyList<string> sources) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pairings"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = targets.Count,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["target"] = AiFieldAssistant.OneOf(targets),
                        ["sources"] = new JsonObject { ["type"] = "array", ["items"] = AiFieldAssistant.OneOf(sources) },
                        ["transformation"] = AiFieldAssistant.OneOf([.. Enum.GetValues<TransformationType>().Select(t => JsonNamingPolicy.CamelCase.ConvertName(t.ToString()))]),
                        ["expression"] = new JsonObject { ["type"] = "string" },
                        ["confidence"] = new JsonObject { ["type"] = "integer" },
                        ["reasoning"] = new JsonObject { ["type"] = "string" },
                        ["question"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("target", "sources", "confidence", "reasoning"),
                },
            },
        },
        ["required"] = new JsonArray("pairings"),
    };

    private static List<PairingDto> Parse(AiReply reply, List<string> warnings)
    {
        try
        {
            return [.. (JsonSerializer.Deserialize<ReplyDto>(reply.Json, ReadOptions)?.Pairings ?? []).Where(p => p.Target is not null)];
        }
        catch (JsonException ex)
        {
            warnings.Add($"{reply.Provider}: unreadable answer ({ex.Message}).");
            return [];
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ReplyDto(IReadOnlyList<PairingDto>? Pairings);

    private sealed record PairingDto(
        string? Target, IReadOnlyList<string>? Sources, string? Transformation, string? Expression, int? Confidence, string? Reasoning, string? Question);
}
