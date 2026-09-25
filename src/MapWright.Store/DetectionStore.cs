using Microsoft.Data.Sqlite;

namespace MapWright.Store;

/// <param name="Json">The detection result as the API returned it.</param>
/// <param name="Playbooks">References of the published domain playbooks the detection ran against.</param>
/// <param name="ProfileChanged">Whether the profile has been saved again since the detection ran.</param>
public sealed record SavedDetection(
    string ProfileId,
    string Json,
    bool UsedAi,
    IReadOnlyList<string> Playbooks,
    DateTimeOffset DetectedAt,
    string DetectedBy,
    bool ProfileChanged);

/// <summary>The latest detection result of each profile, kept until the next run or until the profile is deleted.</summary>
public sealed class DetectionStore(MapWrightDatabase database)
{
    public SavedDetection Save(string profileId, string json, bool usedAi, IEnumerable<string> playbooks, string actor)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO detections (profile_id, json, used_ai, playbooks, profile_updated_at, detected_at, detected_by)
            SELECT id, $json, $ai, $playbooks, updated_at, $now, $actor FROM profiles WHERE id = $id
            ON CONFLICT(profile_id) DO UPDATE SET json = excluded.json, used_ai = excluded.used_ai, playbooks = excluded.playbooks,
                profile_updated_at = excluded.profile_updated_at, detected_at = excluded.detected_at, detected_by = excluded.detected_by
            """;
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$ai", usedAi ? 1 : 0);
        command.Parameters.AddWithValue("$playbooks", string.Join('\n', playbooks.Order(StringComparer.Ordinal)));
        command.Parameters.AddWithValue("$now", database.Now());
        command.Parameters.AddWithValue("$actor", actor);
        if (command.ExecuteNonQuery() == 0)
        {
            throw StoreException.NotFound($"Profile '{profileId}'");
        }

        return Find(connection, profileId)!;
    }

    /// <summary>The profile's latest detection, or null when detection has not run since the profile was stored.</summary>
    public SavedDetection? Find(string profileId)
    {
        using var connection = database.Open();
        return Find(connection, profileId);
    }

    private static SavedDetection? Find(SqliteConnection connection, string profileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.profile_id, d.json, d.used_ai, d.playbooks, d.detected_at, d.detected_by, d.profile_updated_at <> p.updated_at
            FROM detections d JOIN profiles p ON p.id = d.profile_id
            WHERE d.profile_id = $id
            """;
        command.Parameters.AddWithValue("$id", profileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var playbooks = reader.GetString(3);
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2) != 0,
            playbooks.Length == 0 ? [] : playbooks.Split('\n'),
            MapWrightDatabase.ParseTime(reader.GetString(4)),
            reader.GetString(5),
            reader.GetInt64(6) != 0);
    }
}
