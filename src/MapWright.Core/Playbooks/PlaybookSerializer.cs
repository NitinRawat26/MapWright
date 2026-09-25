using System.Text.Json;

namespace MapWright.Core.Playbooks;

public static class PlaybookSerializer
{
    public static Playbook Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Playbook>(json, MapWrightJson.Options)
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

    public static Playbook Load(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (PlaybookException ex)
        {
            throw new PlaybookException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    public static void Save(Playbook playbook, string path) =>
        File.WriteAllText(path, Serialize(playbook) + Environment.NewLine);
}

public sealed class PlaybookException(string message, Exception? inner = null) : Exception(message, inner);
