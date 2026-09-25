using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;

namespace MapWright.Ai;

/// <summary>Fields the AI read from a document's text, as a contract whose fields all carry a review finding.</summary>
public sealed record AiDocumentResult
{
    public ContractDocument? Contract { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Asks an AI provider to list the fields a specification document describes in prose. Long digit runs and
/// e-mail addresses are masked before the text is sent; confidence is capped and every field needs review.
/// </summary>
public sealed partial class AiDocumentReader(
    IAiProvider provider, int maxConfidence = AiFieldAssistant.DefaultMaxConfidence, int chunkSize = 12_000, int maxChunks = 5)
{
    public const string Suffix = " (read by AI)";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Instructions = """
        You read interface specifications of merchant-acquiring systems. List every data field the document in
        "text" defines for the payload. For each give "path" in the style asked for in "pathStyle", its "type"
        (string, integer, decimal, boolean, date, datetime or object), "required" (yes, no or conditional),
        "maxLength", "allowedValues" (codes only), "repeats" (true for a list), a short "description", an
        "evidence" quote of at most 20 words copied from the text, and "confidence" from 0 to 100. Only list fields
        the text defines; never invent fields or values. Leave out a property the text does not state.
        """;

    public async Task<AiDocumentResult> ReadAsync(
        string name, string sha256, string text, PayloadFormat? format, IReadOnlyCollection<string> knownPaths, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var chunks = Chunks(Mask(text)).ToList();
        if (chunks.Count == 0)
        {
            return new() { Warnings = [$"'{name}' has no text outside its field tables for AI to read."] };
        }

        if (chunks.Count > maxChunks)
        {
            warnings.Add($"'{name}' is long; AI read only its first {maxChunks * chunkSize:N0} characters.");
            chunks = chunks[..maxChunks];
        }

        var pathStyle = format switch
        {
            PayloadFormat.Xml => "XML element paths such as /Application/Merchant/LegalName (attributes as @name)",
            PayloadFormat.Json => "dotted JSON paths such as merchant.legalName, with [] after a list, e.g. owners[].firstName",
            _ => "dotted JSON paths such as merchant.legalName (owners[].firstName for lists), or XML element paths such as /Application/Merchant/LegalName when the document describes XML",
        };

        var items = new List<Item>();
        string? providerName = null, model = null;
        foreach (var chunk in chunks)
        {
            var input = new JsonObject { ["document"] = name, ["pathStyle"] = pathStyle, ["text"] = chunk };
            var reply = await provider.GenerateJsonAsync(new(Instructions, input.ToJsonString(), ResponseSchema), cancellationToken).ConfigureAwait(false);
            (providerName, model) = (reply.Provider, reply.Model);
            try
            {
                items.AddRange(JsonSerializer.Deserialize<ReplyDto>(reply.Json, ReadOptions)?.Fields ?? []);
            }
            catch (JsonException ex)
            {
                warnings.Add($"{reply.Provider}: unreadable answer ({ex.Message}).");
            }
        }

        var rows = new List<IReadOnlyList<string>> { (IReadOnlyList<string>)["path", "type", "required", "maxLength", "allowedValues", "repeats", "description"] };
        var answers = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (Blank(item.Path) is not { } raw || raw.Any(char.IsWhiteSpace))
            {
                warnings.Add($"{providerName}: ignored a field without a usable path ('{item.Path}').");
                continue;
            }

            var path = FieldSpecReader.PathOf(raw);
            if (knownPaths.Contains(path) || !answers.TryAdd(path, item))
            {
                continue;
            }

            rows.Add([
                raw,
                Blank(item.Type) ?? "",
                Blank(item.Required) ?? "",
                item.MaxLength is > 0 ? item.MaxLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                string.Join(" | ", (item.AllowedValues ?? []).Select(v => v.Trim()).Where(v => v.Length > 0)),
                item.Repeats == true ? "yes" : "",
                Blank(item.Description) ?? "",
            ]);
        }

        if (answers.Count == 0)
        {
            warnings.Add($"AI found no new fields in '{name}'.");
            return new() { Warnings = warnings };
        }

        ContractDocument contract;
        try
        {
            contract = FieldSpecReader.Read(name + Suffix, [new FieldTable(rows, Label: "AI answer")], sha256, InputKind.Documentation);
        }
        catch (ProfileException ex)
        {
            warnings.Add($"{providerName}: the fields AI read from '{name}' could not be used: {ex.Message}");
            return new() { Warnings = warnings };
        }

        var findings = new List<ProfileFinding>(contract.Findings);
        foreach (var field in contract.Fields.Where(f => answers.ContainsKey(f.Path)))
        {
            var item = answers[field.Path];
            var confidence = Math.Clamp(item.Confidence ?? 0, 0, maxConfidence);
            var evidence = Blank(item.Evidence) is { } quote ? $" Evidence: \"{Shorten(quote)}\"." : "";
            findings.Add(new()
            {
                Kind = ProfileFindingKind.AiExtracted,
                Path = field.Path,
                Message = $"Read from the document by AI ({providerName} {model}, {confidence}% confidence, capped at {maxConfidence}%); review it before relying on it.{evidence}",
                Inputs = [name],
            });
        }

        return new() { Contract = contract with { Findings = findings }, Warnings = warnings };
    }

    /// <summary>The document text with e-mail addresses and runs of six or more digits (IDs, phone numbers) masked.</summary>
    public static string Mask(string text) =>
        LongNumber().Replace(Email().Replace(text, "[email]"), m => m.Value.Count(char.IsDigit) >= 6 ? Digit().Replace(m.Value, "#") : m.Value);

    private IEnumerable<string> Chunks(string text)
    {
        var chunk = new StringBuilder();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0))
        {
            if (chunk.Length > 0 && chunk.Length + line.Length > chunkSize)
            {
                yield return chunk.ToString();
                chunk.Clear();
            }

            chunk.AppendLine(line.Length > chunkSize ? line[..chunkSize] : line);
        }

        if (chunk.Length > 0)
        {
            yield return chunk.ToString();
        }
    }

    private static string Shorten(string quote) => quote.Length <= 160 ? quote : quote[..160] + "…";

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static JsonObject ResponseSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string" },
                        ["type"] = new JsonObject { ["type"] = "string" },
                        ["required"] = new JsonObject { ["type"] = "string" },
                        ["maxLength"] = new JsonObject { ["type"] = "integer" },
                        ["allowedValues"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["repeats"] = new JsonObject { ["type"] = "boolean" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["evidence"] = new JsonObject { ["type"] = "string" },
                        ["confidence"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray("path", "confidence"),
                },
            },
        },
        ["required"] = new JsonArray("fields"),
    };

    private sealed record ReplyDto(List<Item>? Fields);

    private sealed record Item(
        string? Path, string? Type, string? Required, int? MaxLength, List<string>? AllowedValues, bool? Repeats,
        string? Description, string? Evidence, int? Confidence);

    [GeneratedRegex(@"\d")]
    private static partial Regex Digit();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\w#])\d[\d\- ]{4,}\d(?![\w#])")]
    private static partial Regex LongNumber();
}
