using System.Text.Json;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Samples;

public static class JsonSampleReader
{
    public const string RootName = "$";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SampleDocument Read(string name, string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, ParseOptions);
            return new(name, PayloadFormat.Json, Convert(RootName, document.RootElement), []);
        }
        catch (JsonException ex)
        {
            throw new ProfileException($"Sample '{name}' is not valid JSON (line {ex.LineNumber + 1}): {ex.Message}", ex);
        }
    }

    private static SampleNode Convert(string name, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new()
        {
            Name = name,
            Type = SampleNodeType.Object,
            Children = [.. element.EnumerateObject().Select(p => Convert(p.Name, p.Value))],
        },
        JsonValueKind.Array => new()
        {
            Name = name,
            Type = SampleNodeType.Array,
            Children = [.. element.EnumerateArray().Select(item => Convert(name, item))],
        },
        JsonValueKind.String => Scalar(name, element.GetString(), ScalarKind.String),
        JsonValueKind.Number => Scalar(name, element.GetRawText(), ScalarKind.Number),
        JsonValueKind.True or JsonValueKind.False => Scalar(name, element.GetRawText(), ScalarKind.Boolean),
        _ => Scalar(name, null, ScalarKind.Null),
    };

    private static SampleNode Scalar(string name, string? value, ScalarKind kind) =>
        new() { Name = name, Type = SampleNodeType.Value, Value = value, Scalar = kind };
}
