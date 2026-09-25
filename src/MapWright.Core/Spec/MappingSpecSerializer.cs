using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapWright.Core.Spec;

public static class MappingSpecSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static MappingDocument Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MappingDocument>(json, Options)
                ?? throw new MappingSpecException("Mapping spec is empty (null).");
        }
        catch (JsonException ex)
        {
            var location = ex.Path is null ? "" : $" at '{ex.Path}'";
            throw new MappingSpecException($"Invalid mapping spec{location}: {ex.Message}", ex);
        }
    }

    public static string Serialize(MappingDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static MappingDocument Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(MappingDocument document, string path) =>
        File.WriteAllText(path, Serialize(document) + Environment.NewLine);
}

public sealed class MappingSpecException(string message, Exception? inner = null) : Exception(message, inner);
