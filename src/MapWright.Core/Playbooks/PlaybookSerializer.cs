using System.Text.Json;

namespace MapWright.Core.Playbooks;

public enum PlaybookFormat
{
    Json,
    Yaml,
}

public static class PlaybookSerializer
{
    public static IReadOnlyList<string> Extensions { get; } = [".yaml", ".yml", ".json"];

    /// <summary>JSON when the text starts with '{', otherwise YAML.</summary>
    public static PlaybookFormat Detect(string text) =>
        text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{') ? PlaybookFormat.Json : PlaybookFormat.Yaml;

    /// <summary>Reads a playbook from JSON or YAML text.</summary>
    public static Playbook Deserialize(string text)
    {
        if (Detect(text) == PlaybookFormat.Yaml)
        {
            return PlaybookYaml.Deserialize(text);
        }

        try
        {
            return JsonSerializer.Deserialize<Playbook>(text, MapWrightJson.Options)
                ?? throw new PlaybookException("Playbook is empty (null).");
        }
        catch (JsonException ex)
        {
            var location = ex.Path is null ? "" : $" at '{ex.Path}'";
            throw new PlaybookException($"Invalid playbook{location}: {ex.Message}", ex);
        }
    }

    public static string Serialize(Playbook playbook) =>
        JsonSerializer.Serialize(playbook, MapWrightJson.Options);

    public static string SerializeYaml(Playbook playbook) => PlaybookYaml.Serialize(playbook);

    public static string Serialize(Playbook playbook, PlaybookFormat format) =>
        format == PlaybookFormat.Yaml ? SerializeYaml(playbook) : Serialize(playbook);

    /// <summary>YAML for .yaml and .yml files, JSON otherwise.</summary>
    public static PlaybookFormat FormatOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" ? PlaybookFormat.Yaml : PlaybookFormat.Json;

    public static Playbook Load(string path) => Read(path).Playbook;

    /// <summary>Loads a playbook file and keeps its text when it is YAML, so its comments can be stored with it.</summary>
    public static PlaybookFile Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return new(path, Deserialize(text), Detect(text) == PlaybookFormat.Yaml ? text : null);
        }
        catch (PlaybookException ex)
        {
            throw new PlaybookException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    public static void Save(Playbook playbook, string path)
    {
        var text = Serialize(playbook, FormatOf(path));
        File.WriteAllText(path, text.EndsWith('\n') ? text : text + Environment.NewLine);
    }
}

/// <summary>A playbook read from a file; <see cref="Yaml"/> is the file's text when it was written in YAML.</summary>
public sealed record PlaybookFile(string Path, Playbook Playbook, string? Yaml);

public sealed class PlaybookException(string message, Exception? inner = null) : Exception(message, inner);
