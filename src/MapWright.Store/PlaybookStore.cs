using MapWright.Core.Playbooks;
using MapWright.Core.Spec;
using Microsoft.Data.Sqlite;

namespace MapWright.Store;

public sealed record PlaybookSummary(
    string Id,
    string Version,
    string Name,
    PlaybookKind Kind,
    PlaybookStatus Status,
    string? Owner,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    public string Reference => $"{Id}@{Version}";
}

public sealed record PlaybookEvent(
    long Sequence,
    string Id,
    string Version,
    string Actor,
    string Action,
    PlaybookStatus? From,
    PlaybookStatus? To,
    string? Note,
    DateTimeOffset OccurredAt);

/// <summary>
/// Versioned playbooks with a Draft → In Review → Published → Retired lifecycle; a draft can be abandoned, or deleted
/// while it has never been submitted. Only drafts are editable; a
/// published version is changed by drafting a new version from it. Publishing retires the previous published version.
/// Each version is stored as JSON, plus the YAML it was written in (when it came as YAML), so the comments survive
/// status changes, new versions and approved suggestions.
/// </summary>
public sealed class PlaybookStore(MapWrightDatabase database)
{
    private const string SummaryColumns = "id, version, name, kind, status, owner, created_at, created_by, updated_at, updated_by";

    public IReadOnlyList<PlaybookSummary> List(PlaybookStatus? status = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns} FROM playbook_versions" + (status is null ? "" : " WHERE status = $status");
        if (status is { } s)
        {
            command.Parameters.AddWithValue("$status", s.ToString());
        }

        return Order(ReadSummaries(command));
    }

    public IReadOnlyList<PlaybookSummary> Versions(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns} FROM playbook_versions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var versions = Order(ReadSummaries(command));
        return versions.Count == 0 ? throw StoreException.NotFound($"Playbook '{id}'") : versions;
    }

    public Playbook Get(string id, string version) =>
        Find(id, version) ?? throw StoreException.NotFound($"Playbook '{id}@{version}'");

    public Playbook? Find(string id, string version)
    {
        using var connection = database.Open();
        return Find(connection, null, id, version);
    }

    /// <summary>The version as YAML: the text it was written in, or generated from the stored JSON.</summary>
    public string GetYaml(string id, string version)
    {
        using var connection = database.Open();
        var (playbook, yaml) = FindWithYaml(connection, null, id, version) ?? throw StoreException.NotFound($"Playbook '{id}@{version}'");
        return yaml ?? PlaybookSerializer.SerializeYaml(playbook);
    }

    /// <summary>Every stored version; <see cref="PlaybookLibrary.Active"/> picks the one to apply per id.</summary>
    public PlaybookLibrary Library()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM playbook_versions ORDER BY id, version";
        var playbooks = new List<Playbook>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            playbooks.Add(PlaybookSerializer.Deserialize(reader.GetString(0)));
        }

        return new(playbooks);
    }

    public IReadOnlyList<PlaybookEvent> History(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, id, version, actor, action, from_status, to_status, note, occurred_at FROM playbook_events WHERE id = $id ORDER BY seq";
        command.Parameters.AddWithValue("$id", id);
        var events = new List<PlaybookEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Enum.Parse<PlaybookStatus>(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Enum.Parse<PlaybookStatus>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                MapWrightDatabase.ParseTime(reader.GetString(8))));
        }

        return events.Count == 0 ? throw StoreException.NotFound($"Playbook '{id}'") : events;
    }

    /// <summary>Adds playbooks from files (e.g. the starter set) with their own status; versions already stored are skipped.</summary>
    public IReadOnlyList<string> Import(IReadOnlyList<Playbook> playbooks, string actor) =>
        Import([.. playbooks.Select(p => new PlaybookFile("", p, null))], actor);

    /// <summary>As <see cref="Import(IReadOnlyList{Playbook}, string)"/>, keeping the YAML of files written in YAML.</summary>
    public IReadOnlyList<string> Import(IReadOnlyList<PlaybookFile> files, string actor)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var existing = AllPlaybooks(connection, transaction);
        var added = files.Where(f => !existing.Any(e => e.Reference == f.Playbook.Reference)).ToList();
        RequireValid(PlaybookValidator.Validate([.. existing, .. added.Select(f => f.Playbook)]), "Imported playbooks");

        foreach (var (_, playbook, yaml) in added)
        {
            Insert(connection, transaction, playbook, yaml, actor);
            Log(connection, transaction, playbook.Id, playbook.Version, actor, "imported", null, playbook.Status, null);
        }

        transaction.Commit();
        return [.. added.Select(f => f.Playbook.Reference)];
    }

    /// <param name="yaml">The YAML the playbook was read from, if any; kept so its comments are served back.</param>
    public Playbook Create(Playbook playbook, string actor, string? yaml = null)
    {
        if (playbook.Status != PlaybookStatus.Draft)
        {
            throw new StoreException(StoreError.Invalid, "A new playbook starts as a draft.");
        }

        RequireValid(PlaybookValidator.Validate(playbook), playbook.Reference);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (Find(connection, transaction, playbook.Id, playbook.Version) is not null)
        {
            throw new StoreException(StoreError.Conflict, $"Playbook '{playbook.Reference}' already exists.");
        }

        RequireNoOpenVersion(connection, transaction, playbook.Id);
        Insert(connection, transaction, playbook, yaml, actor);
        Log(connection, transaction, playbook.Id, playbook.Version, actor, "created", null, PlaybookStatus.Draft, null);
        transaction.Commit();
        return playbook;
    }

    /// <param name="yaml">
    /// The YAML the playbook was read from. Without it the change is applied to the stored YAML where that keeps
    /// its comments (e.g. an added term), otherwise the YAML is regenerated.
    /// </param>
    public Playbook UpdateDraft(string id, string version, Playbook playbook, string actor, string? yaml = null)
    {
        if (playbook.Id != id || playbook.Version != version)
        {
            throw new StoreException(StoreError.Invalid, $"The body is '{playbook.Reference}' but the address is '{id}@{version}'.");
        }

        var updated = playbook with { Status = PlaybookStatus.Draft };
        RequireValid(PlaybookValidator.Validate(updated), updated.Reference);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var (current, currentYaml) = FindWithYaml(connection, transaction, id, version) ?? throw StoreException.NotFound($"Playbook '{id}@{version}'");
        if (current.Status != PlaybookStatus.Draft)
        {
            throw new StoreException(StoreError.Conflict, $"'{current.Reference}' is {current.Status}; only drafts can be edited. Draft a new version instead.");
        }

        var source = yaml is null ? PlaybookYaml.Update(currentYaml, current, updated) : PlaybookYaml.Update(yaml, playbook, updated);
        Update(connection, transaction, updated, source, actor);
        Log(connection, transaction, id, version, actor, "edited", PlaybookStatus.Draft, PlaybookStatus.Draft, null);
        transaction.Commit();
        return updated;
    }

    /// <summary>Copies a version into a new draft (next unused minor version by default) with a change note.</summary>
    public Playbook DraftNewVersion(string id, string fromVersion, string? newVersion, string actor, string? note)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var (source, sourceYaml) = FindWithYaml(connection, transaction, id, fromVersion) ?? throw StoreException.NotFound($"Playbook '{id}@{fromVersion}'");
        var version = newVersion ?? NextMinor(source.Version);
        while (newVersion is null && Find(connection, transaction, id, version) is not null)
        {
            version = NextMinor(version);
        }

        if (Find(connection, transaction, id, version) is not null)
        {
            throw new StoreException(StoreError.Conflict, $"Playbook '{id}@{version}' already exists.");
        }

        RequireNoOpenVersion(connection, transaction, id);
        var draft = source with
        {
            Version = version,
            Status = PlaybookStatus.Draft,
            ChangeNotes =
            [
                .. source.ChangeNotes,
                new PlaybookChange
                {
                    Version = version,
                    Date = DateOnly.FromDateTime(database.Time.GetUtcNow().UtcDateTime),
                    Author = actor,
                    Description = note ?? $"Drafted from {source.Version}.",
                },
            ],
        };

        RequireValid(PlaybookValidator.Validate(draft), draft.Reference);
        Insert(connection, transaction, draft, PlaybookYaml.Update(sourceYaml, source, draft), actor);
        Log(connection, transaction, id, version, actor, "drafted", null, PlaybookStatus.Draft, $"From {source.Version}. {note}".Trim());
        transaction.Commit();
        return draft;
    }

    /// <summary>
    /// Draft → InReview (valid and tests pass), InReview → Draft (changes requested), InReview → Published (valid,
    /// tests pass, reviewed by someone other than the submitter), Published → Retired, Draft → Abandoned (sending
    /// Retired for a draft abandons it too).
    /// </summary>
    public Playbook Transition(string id, string version, PlaybookStatus to, string actor, string? note)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var (current, currentYaml) = FindWithYaml(connection, transaction, id, version) ?? throw StoreException.NotFound($"Playbook '{id}@{version}'");
        var fromStatus = current.Status;
        if (fromStatus == PlaybookStatus.Draft && to == PlaybookStatus.Retired)
        {
            to = PlaybookStatus.Abandoned;
        }

        var action = (fromStatus, to) switch
        {
            (PlaybookStatus.Draft, PlaybookStatus.InReview) => "submitted",
            (PlaybookStatus.InReview, PlaybookStatus.Draft) => "changesRequested",
            (PlaybookStatus.InReview, PlaybookStatus.Published) => "published",
            (PlaybookStatus.Published, PlaybookStatus.Retired) => "retired",
            (PlaybookStatus.Draft, PlaybookStatus.Abandoned) => "abandoned",
            _ => throw new StoreException(StoreError.Conflict, $"'{current.Reference}' is {fromStatus} and cannot move to {to}."),
        };

        var next = current with { Status = to };
        if (to is PlaybookStatus.InReview or PlaybookStatus.Published)
        {
            var issues = Validate(connection, transaction, next);
            var failed = PlaybookTestRunner.Run(next).Where(r => !r.Passed)
                .Select(r => new SpecIssue(IssueSeverity.Error, "PBTEST", $"{r.Kind} {r.Id}", r.Message ?? "Test failed."));
            RequireValid([.. issues, .. failed], current.Reference);
        }

        if (to == PlaybookStatus.Published && database.Options.RequireIndependentReview
            && Submitter(connection, transaction, id, version) is { } submitter
            && string.Equals(submitter, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreException(StoreError.Conflict, $"'{current.Reference}' was submitted by {submitter}; another reviewer must publish it.");
        }

        if (to == PlaybookStatus.Published)
        {
            foreach (var previous in AllPlaybooks(connection, transaction).Where(p => p.Id == id && p.Status == PlaybookStatus.Published))
            {
                var previousYaml = FindWithYaml(connection, transaction, id, previous.Version)?.Yaml;
                var retired = previous with { Status = PlaybookStatus.Retired };
                Update(connection, transaction, retired, PlaybookYaml.Update(previousYaml, previous, retired), actor);
                Log(connection, transaction, id, previous.Version, actor, "retired", PlaybookStatus.Published, PlaybookStatus.Retired, $"Replaced by {version}.");
            }
        }

        Update(connection, transaction, next, PlaybookYaml.Update(currentYaml, current, next), actor);
        Log(connection, transaction, id, version, actor, action, fromStatus, to, note);
        transaction.Commit();
        return next;
    }

    /// <summary>
    /// Removes a draft that was never submitted for review, freeing its version number. The history keeps a
    /// "deleted" entry. Drafts that were reviewed, or changed by an approved AI suggestion, are abandoned instead.
    /// </summary>
    public void Delete(string id, string version, string actor, string? note)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var current = Find(connection, transaction, id, version) ?? throw StoreException.NotFound($"Playbook '{id}@{version}'");
        if (current.Status != PlaybookStatus.Draft)
        {
            throw new StoreException(StoreError.Conflict, $"'{current.Reference}' is {current.Status}; only drafts can be deleted.");
        }

        if (Submitter(connection, transaction, id, version) is not null)
        {
            throw new StoreException(StoreError.Conflict, $"'{current.Reference}' has been submitted for review; abandon it instead so its review history is kept.");
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM ai_suggestions WHERE playbook_ref = $ref";
            command.Parameters.AddWithValue("$ref", current.Reference);
            if ((long)command.ExecuteScalar()! > 0)
            {
                throw new StoreException(StoreError.Conflict, $"'{current.Reference}' holds approved AI suggestions; abandon it instead so they stay traceable.");
            }

            command.CommandText = "DELETE FROM playbook_versions WHERE id = $id AND version = $version";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }

        Log(connection, transaction, id, version, actor, "deleted", PlaybookStatus.Draft, null, note);
        transaction.Commit();
    }

    /// <summary>Validates the playbook on its own and against the published versions of the other playbooks.</summary>
    public IReadOnlyList<SpecIssue> Validate(Playbook playbook)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        return Validate(connection, transaction, playbook);
    }

    private static IReadOnlyList<SpecIssue> Validate(SqliteConnection connection, SqliteTransaction transaction, Playbook playbook)
    {
        var others = AllPlaybooks(connection, transaction).Where(p => p.Status == PlaybookStatus.Published && p.Id != playbook.Id);
        return PlaybookValidator.Validate([.. others, playbook]);
    }

    private static void RequireValid(IReadOnlyList<SpecIssue> issues, string what)
    {
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new StoreException(StoreError.Invalid, $"{what}: {errors.Count} error(s).", errors);
        }
    }

    private static void RequireNoOpenVersion(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, status FROM playbook_versions WHERE id = $id AND status IN ('Draft', 'InReview') LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new StoreException(StoreError.Conflict, $"'{id}' already has an open version {reader.GetString(0)} ({reader.GetString(1)}); finish or retire it first.");
        }
    }

    private static string NextMinor(string version) =>
        System.Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor + 1}.0" : version + ".1";

    private static string? Submitter(SqliteConnection connection, SqliteTransaction transaction, string id, string version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT actor FROM playbook_events WHERE id = $id AND version = $version AND action = 'submitted' ORDER BY seq DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() as string;
    }

    private static Playbook? Find(SqliteConnection connection, SqliteTransaction? transaction, string id, string version) =>
        FindWithYaml(connection, transaction, id, version)?.Playbook;

    private static (Playbook Playbook, string? Yaml)? FindWithYaml(SqliteConnection connection, SqliteTransaction? transaction, string id, string version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json, yaml FROM playbook_versions WHERE id = $id AND version = $version";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (PlaybookSerializer.Deserialize(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    private static List<Playbook> AllPlaybooks(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM playbook_versions";
        var playbooks = new List<Playbook>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            playbooks.Add(PlaybookSerializer.Deserialize(reader.GetString(0)));
        }

        return playbooks;
    }

    private void Insert(SqliteConnection connection, SqliteTransaction transaction, Playbook playbook, string? yaml, string actor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playbook_versions (id, version, kind, name, status, owner, json, yaml, created_at, created_by, updated_at, updated_by)
            VALUES ($id, $version, $kind, $name, $status, $owner, $json, $yaml, $now, $actor, $now, $actor)
            """;
        Bind(command, playbook, yaml, actor);
        command.ExecuteNonQuery();
    }

    private void Update(SqliteConnection connection, SqliteTransaction transaction, Playbook playbook, string? yaml, string actor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE playbook_versions SET kind = $kind, name = $name, status = $status, owner = $owner, json = $json,
                yaml = $yaml, updated_at = $now, updated_by = $actor
            WHERE id = $id AND version = $version
            """;
        Bind(command, playbook, yaml, actor);
        command.ExecuteNonQuery();
    }

    private void Bind(SqliteCommand command, Playbook playbook, string? yaml, string actor)
    {
        command.Parameters.AddWithValue("$id", playbook.Id);
        command.Parameters.AddWithValue("$version", playbook.Version);
        command.Parameters.AddWithValue("$kind", playbook.Kind.ToString());
        command.Parameters.AddWithValue("$name", playbook.Name);
        command.Parameters.AddWithValue("$status", playbook.Status.ToString());
        command.Parameters.AddWithValue("$owner", (object?)playbook.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", PlaybookSerializer.Serialize(playbook));
        command.Parameters.AddWithValue("$yaml", (object?)yaml ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", database.Now());
        command.Parameters.AddWithValue("$actor", actor);
    }

    private void Log(
        SqliteConnection connection, SqliteTransaction transaction, string id, string version, string actor, string action,
        PlaybookStatus? from, PlaybookStatus? to, string? note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playbook_events (id, version, actor, action, from_status, to_status, note, occurred_at)
            VALUES ($id, $version, $actor, $action, $from, $to, $note, $now)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$from", (object?)from?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note);
        command.Parameters.AddWithValue("$now", database.Now());
        command.ExecuteNonQuery();
    }

    private static List<PlaybookSummary> ReadSummaries(SqliteCommand command)
    {
        var summaries = new List<PlaybookSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<PlaybookKind>(reader.GetString(3)),
                Enum.Parse<PlaybookStatus>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                MapWrightDatabase.ParseTime(reader.GetString(6)),
                reader.GetString(7),
                MapWrightDatabase.ParseTime(reader.GetString(8)),
                reader.GetString(9)));
        }

        return summaries;
    }

    private static List<PlaybookSummary> Order(List<PlaybookSummary> summaries) =>
        [.. summaries
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ThenBy(s => System.Version.TryParse(s.Version, out var v) ? v : new System.Version())
            .ThenBy(s => s.Version, StringComparer.Ordinal)];
}
