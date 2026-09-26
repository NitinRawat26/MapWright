using System.Globalization;
using System.Text.RegularExpressions;
using MapWright.Core.Playbooks;
using Microsoft.Data.Sqlite;

namespace MapWright.Store;

public sealed class StoreOptions
{
    /// <summary>SQLite file path; ":memory:" keeps everything in-process (tests).</summary>
    public string DatabasePath { get; set; } = "data/mapwright.db";

    /// <summary>A playbook version cannot be published by the person who submitted it for review.</summary>
    public bool RequireIndependentReview { get; set; } = true;
}

/// <summary>Owns the SQLite connection string and schema. One file holds playbooks, profiles, mappings and reviews.</summary>
public sealed partial class MapWrightDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;

    public MapWrightDatabase(StoreOptions options, TimeProvider? time = null)
    {
        Options = options;
        Time = time ?? TimeProvider.System;
        if (options.DatabasePath == ":memory:")
        {
            _connectionString = $"Data Source=mapwright-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            if (Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            _connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath }.ToString();
        }

        Migrate();
    }

    public StoreOptions Options { get; }

    public TimeProvider Time { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Dispose() => _keepAlive?.Dispose();

    internal string Now() => Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Lower-case slug used for stored profile and mapping ids, e.g. "sales-alpha-crm".</summary>
    public static string Slug(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-', '.', '_');
        return slug.Length == 0 ? "item" : slug[..Math.Min(slug.Length, 64)].TrimEnd('-', '.', '_');
    }

    internal static void RequireId(string id, string what)
    {
        if (!IdPattern().IsMatch(id))
        {
            throw new StoreException(StoreError.Invalid, $"{what} id '{id}' must be 1-64 lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }
    }

    private void Migrate()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS playbook_versions (
                id TEXT NOT NULL,
                version TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                owner TEXT,
                json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                PRIMARY KEY (id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_playbook_versions_status ON playbook_versions(status);
            CREATE TABLE IF NOT EXISTS playbook_events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL,
                version TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                from_status TEXT,
                to_status TEXT,
                note TEXT,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_playbook_events_id ON playbook_events(id, version);
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                system TEXT NOT NULL,
                version TEXT,
                format TEXT NOT NULL,
                field_count INTEGER NOT NULL,
                json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mappings (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                version TEXT NOT NULL,
                source_system TEXT NOT NULL,
                target_system TEXT NOT NULL,
                json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS review_decisions (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                mapping_id TEXT NOT NULL REFERENCES mappings(id) ON DELETE CASCADE,
                row_id TEXT NOT NULL,
                decision TEXT NOT NULL,
                previous_status TEXT NOT NULL,
                reviewer TEXT NOT NULL,
                comment TEXT,
                row_json TEXT NOT NULL,
                decided_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_review_decisions_mapping ON review_decisions(mapping_id);
            CREATE TABLE IF NOT EXISTS ai_suggestions (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                system TEXT NOT NULL,
                path TEXT NOT NULL,
                status TEXT NOT NULL,
                json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                decided_at TEXT,
                decided_by TEXT,
                comment TEXT,
                playbook_ref TEXT,
                mapping_id TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_ai_suggestions_status ON ai_suggestions(status);
            CREATE TABLE IF NOT EXISTS detections (
                profile_id TEXT PRIMARY KEY REFERENCES profiles(id) ON DELETE CASCADE,
                json TEXT NOT NULL,
                used_ai INTEGER NOT NULL,
                playbooks TEXT NOT NULL,
                profile_updated_at TEXT NOT NULL,
                detected_at TEXT NOT NULL,
                detected_by TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('playbook_versions') WHERE name = 'yaml'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = "ALTER TABLE playbook_versions ADD COLUMN yaml TEXT";
            command.ExecuteNonQuery();
        }

        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ai_suggestions') WHERE name = 'mapping_id'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = "ALTER TABLE ai_suggestions ADD COLUMN mapping_id TEXT";
            command.ExecuteNonQuery();
        }

        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_ai_suggestions_mapping ON ai_suggestions(mapping_id)";
        command.ExecuteNonQuery();

        KeepEventsOfDeletedVersions(connection);
        MarkAbandonedDrafts(connection);
    }

    /// <summary>Older databases tied each event to a stored version, so a deleted draft could not keep its history.</summary>
    private static void KeepEventsOfDeletedVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('playbook_events')";
        if ((long)command.ExecuteScalar()! == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE playbook_events_new (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL,
                version TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                from_status TEXT,
                to_status TEXT,
                note TEXT,
                occurred_at TEXT NOT NULL
            );
            INSERT INTO playbook_events_new (seq, id, version, actor, action, from_status, to_status, note, occurred_at)
                SELECT seq, id, version, actor, action, from_status, to_status, note, occurred_at FROM playbook_events;
            DROP TABLE playbook_events;
            ALTER TABLE playbook_events_new RENAME TO playbook_events;
            CREATE INDEX IF NOT EXISTS ix_playbook_events_id ON playbook_events(id, version);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Drafts abandoned before the Abandoned status existed were stored as Retired.</summary>
    private static void MarkAbandonedDrafts(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.id, v.version, v.json, v.yaml FROM playbook_versions v
            WHERE v.status = 'Retired' AND (
                SELECT e.action FROM playbook_events e WHERE e.id = v.id AND e.version = v.version ORDER BY e.seq DESC LIMIT 1) = 'abandoned'
            """;
        var rows = new List<(string Id, string Version, Playbook Before, string? Yaml)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), PlaybookSerializer.Deserialize(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        command.CommandText = "UPDATE playbook_versions SET status = $status, json = $json, yaml = $yaml WHERE id = $id AND version = $version";
        foreach (var (id, version, before, yaml) in rows)
        {
            var after = before with { Status = PlaybookStatus.Abandoned };
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$status", after.Status.ToString());
            command.Parameters.AddWithValue("$json", PlaybookSerializer.Serialize(after));
            command.Parameters.AddWithValue("$yaml", (object?)PlaybookYaml.Update(yaml, before, after) ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "UPDATE playbook_events SET to_status = 'Abandoned' WHERE action = 'abandoned' AND to_status = 'Retired'";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();
}
