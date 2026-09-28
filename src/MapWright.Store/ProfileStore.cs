using MapWright.Core.Profile;
using MapWright.Core.Spec;
using Microsoft.Data.Sqlite;

namespace MapWright.Store;

public sealed record ProfileSummary(
    string Id,
    string System,
    string? Version,
    PayloadFormat Format,
    int FieldCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>An uploaded file kept next to a stored profile (original sample or contract document).</summary>
public sealed record StoredProfileInput(string Name, byte[] Content);

/// <summary>Stored system profiles, addressed by a slug id (e.g. "sales-alpha-crm").</summary>
public sealed class ProfileStore(MapWrightDatabase database)
{
    public IReadOnlyList<ProfileSummary> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, system, version, format, field_count, created_at, updated_at, updated_by FROM profiles ORDER BY id";
        var summaries = new List<ProfileSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Enum.Parse<PayloadFormat>(reader.GetString(3)),
                reader.GetInt32(4),
                MapWrightDatabase.ParseTime(reader.GetString(5)),
                MapWrightDatabase.ParseTime(reader.GetString(6)),
                reader.GetString(7)));
        }

        return summaries;
    }

    public SystemProfile Get(string id) => Find(id) ?? throw StoreException.NotFound($"Profile '{id}'");

    public SystemProfile? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? ProfileSerializer.Deserialize(json) : null;
    }

    /// <summary>Creates or replaces the profile. Profiles with validation errors are rejected.
    /// Uploaded input files may be stored alongside so the originals can be downloaded later.</summary>
    public void Save(string id, SystemProfile profile, string actor, IReadOnlyList<StoredProfileInput>? inputs = null)
    {
        MapWrightDatabase.RequireId(id, "Profile");
        var errors = SystemProfileValidator.Validate(profile).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new StoreException(StoreError.Invalid, $"Profile '{id}': {errors.Count} error(s).", errors);
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profiles (id, system, version, format, field_count, json, created_at, updated_at, updated_by)
            VALUES ($id, $system, $version, $format, $fields, $json, $now, $now, $actor)
            ON CONFLICT(id) DO UPDATE SET system = excluded.system, version = excluded.version, format = excluded.format,
                field_count = excluded.field_count, json = excluded.json, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$system", profile.System);
        command.Parameters.AddWithValue("$version", (object?)profile.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$format", profile.Format.ToString());
        command.Parameters.AddWithValue("$fields", profile.Fields.Count);
        command.Parameters.AddWithValue("$json", ProfileSerializer.Serialize(profile));
        command.Parameters.AddWithValue("$now", database.Now());
        command.Parameters.AddWithValue("$actor", actor);
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM profile_inputs WHERE profile_id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        foreach (var input in inputs ?? [])
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO profile_inputs (profile_id, name, content) VALUES ($id, $name, $content)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", input.Name);
            command.Parameters.AddWithValue("$content", input.Content);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Names of the uploaded inputs whose original files are stored for download.</summary>
    public IReadOnlyList<string> InputNames(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM profile_inputs WHERE profile_id = $id ORDER BY name";
        command.Parameters.AddWithValue("$id", id);
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>The stored bytes of an uploaded input, or null when no file was kept under that name.</summary>
    public byte[]? InputContent(string id, string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM profile_inputs WHERE profile_id = $id AND name = $name";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as byte[];
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
        {
            throw StoreException.NotFound($"Profile '{id}'");
        }
    }
}
