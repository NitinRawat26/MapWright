using System.Text.Json;

namespace MapWright.Core.Profile;

public static class ProfileSerializer
{
    public static SystemProfile Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SystemProfile>(json, MapWrightJson.Options)
                ?? throw new ProfileException("System profile is empty (null).");
        }
        catch (JsonException ex)
        {
            var location = ex.Path is null ? "" : $" at '{ex.Path}'";
            throw new ProfileException($"Invalid system profile{location}: {ex.Message}", ex);
        }
    }

    public static string Serialize(SystemProfile profile) =>
        JsonSerializer.Serialize(profile, MapWrightJson.Options);

    public static SystemProfile Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(SystemProfile profile, string path) =>
        File.WriteAllText(path, Serialize(profile) + Environment.NewLine);
}

public sealed class ProfileException(string message, Exception? inner = null) : Exception(message, inner);
