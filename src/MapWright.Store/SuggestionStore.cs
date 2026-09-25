using System.Text.Json;
using MapWright.Core;
using MapWright.Core.Playbooks;
using Microsoft.Data.Sqlite;

namespace MapWright.Store;

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>What an AI provider said about one field; never applied until a reviewer approves it.</summary>
public sealed record SuggestionContent
{
    public required string Path { get; init; }
    public required string FieldName { get; init; }
    public string? BusinessConcept { get; init; }
    public string? DomainPlaybook { get; init; }
    public string? ProposedConcept { get; init; }
    public required string Meaning { get; init; }
    public required int ConfidencePercent { get; init; }
    public required string Reasoning { get; init; }
    public string? Question { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
}

/// <param name="Playbook">The draft playbook version the approval changed.</param>
public sealed record Suggestion(
    long Id,
    string ProfileId,
    string System,
    SuggestionContent Content,
    SuggestionStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Comment,
    string? Playbook);

/// <summary>
/// The AI suggestions inbox. Approving a suggestion adds the field's name as a vocabulary term to a draft of the
/// domain playbook that defines the concept; the draft still goes through review and publishing like any other edit.
/// </summary>
public sealed class SuggestionStore(MapWrightDatabase database, PlaybookStore playbooks)
{
    private const string Columns = "seq, profile_id, system, json, status, created_by, created_at, decided_by, decided_at, comment, playbook_ref";

    public IReadOnlyList<Suggestion> Add(string profileId, string system, IEnumerable<SuggestionContent> suggestions, string actor)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var ids = new List<long>();
        foreach (var suggestion in suggestions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ai_suggestions (profile_id, system, path, status, json, created_at, created_by)
                VALUES ($profile, $system, $path, $status, $json, $now, $actor);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$system", system);
            command.Parameters.AddWithValue("$path", suggestion.Path);
            command.Parameters.AddWithValue("$status", nameof(SuggestionStatus.Pending));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(suggestion, MapWrightJson.Options));
            command.Parameters.AddWithValue("$now", database.Now());
            command.Parameters.AddWithValue("$actor", actor);
            ids.Add((long)command.ExecuteScalar()!);
        }

        transaction.Commit();
        return [.. ids.Select(Get)];
    }

    public IReadOnlyList<Suggestion> List(SuggestionStatus? status = null, string? profileId = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM ai_suggestions
            WHERE ($status IS NULL OR status = $status) AND ($profile IS NULL OR profile_id = $profile)
            ORDER BY seq
            """;
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status.ToString());
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var list = new List<Suggestion>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    public Suggestion Get(long id)
    {
        using var connection = database.Open();
        return Find(connection, id) ?? throw StoreException.NotFound($"Suggestion {id}");
    }

    public Suggestion Reject(long id, string reviewer, string? comment)
    {
        RequirePending(Get(id));
        Decide(id, SuggestionStatus.Rejected, reviewer, comment, null);
        return Get(id);
    }

    /// <param name="concept">
    /// <c>Concept</c> or <c>Concept.Attribute</c> to file the field under; defaults to the concept the AI suggested.
    /// Needed when the AI proposed a concept no playbook defines yet.
    /// </param>
    public (Suggestion Suggestion, Playbook Draft) Approve(long id, string reviewer, string? concept, string? comment)
    {
        var suggestion = Get(id);
        RequirePending(suggestion);
        var content = suggestion.Content;
        var chosen = string.IsNullOrWhiteSpace(concept) ? content.BusinessConcept : concept.Trim();
        if (chosen is null)
        {
            throw new StoreException(
                StoreError.Invalid,
                $"The AI proposed a new concept '{content.ProposedConcept}'. Send the existing 'concept' to file the field under, " +
                "or reject the suggestion and add the concept to a playbook by hand.");
        }

        var parts = chosen.Split('.', 2);
        var candidates = playbooks.Library().Domains.Where(p => Same(p.Domain!.Concept.Name, parts[0])).ToList();
        if (candidates.Count == 0)
        {
            throw new StoreException(StoreError.Invalid, $"No published domain playbook defines the concept '{parts[0]}'.");
        }

        if (parts.Length == 2)
        {
            candidates = [.. candidates.Where(p => p.Domain!.Concept.Attributes.Any(a => Same(a.Name, parts[1])))];
            if (candidates.Count == 0)
            {
                throw new StoreException(StoreError.Invalid, $"No published domain playbook defines the attribute '{parts[1]}' on {parts[0]}.");
            }
        }

        var published = candidates.Count == 1 ? candidates[0]
            : candidates.FirstOrDefault(p => chosen == content.BusinessConcept && p.Reference == content.DomainPlaybook)
            ?? throw new StoreException(
                StoreError.Invalid,
                $"'{chosen}' is defined by {string.Join(" and ", candidates.Select(p => p.Id))}; send the concept as Concept.Attribute to pick one.");
        var attribute = parts.Length == 2 ? published.Domain!.Concept.Attributes.First(a => Same(a.Name, parts[1])).Name : null;

        var term = string.Join(' ', NameTokens.Split(content.FieldName));
        if (term.Length == 0)
        {
            throw new StoreException(StoreError.Invalid, $"The field name '{content.FieldName}' has no letters or digits to use as a term.");
        }

        var key = string.Concat(NameTokens.Split(term));
        var existing = published.Domain!.Vocabulary.FirstOrDefault(v => string.Concat(NameTokens.Split(v.Term)) == key);
        if (existing is not null || NameTokens.Split(attribute ?? published.Domain.Concept.Name).SequenceEqual(NameTokens.Split(term)))
        {
            throw new StoreException(
                StoreError.Conflict,
                $"'{term}' is already a term in '{published.Reference}'{(existing?.AppliesTo is { } at ? $" for {at}" : "")}; reject this suggestion instead.");
        }

        var draft = OpenDraft(published, reviewer, id);
        var domain = draft.Domain!;
        var edited = draft with
        {
            Domain = domain with
            {
                Vocabulary =
                [
                    .. domain.Vocabulary,
                    new VocabularyTerm
                    {
                        Term = term,
                        AppliesTo = attribute,
                        Note = $"{suggestion.System} field {content.Path}; AI suggestion {id} ({content.Provider}/{content.Model}) approved by {reviewer}.",
                    },
                ],
            },
        };
        var saved = playbooks.UpdateDraft(edited.Id, edited.Version, edited, reviewer);
        Decide(id, SuggestionStatus.Approved, reviewer, comment, saved.Reference);
        return (Get(id), saved);
    }

    private Playbook OpenDraft(Playbook published, string reviewer, long suggestion)
    {
        var open = playbooks.Versions(published.Id).FirstOrDefault(v => v.Status is PlaybookStatus.Draft or PlaybookStatus.InReview);
        if (open is null)
        {
            return playbooks.DraftNewVersion(published.Id, published.Version, null, reviewer, $"Approved AI suggestion {suggestion}.");
        }

        if (open.Status == PlaybookStatus.InReview)
        {
            throw new StoreException(
                StoreError.Conflict,
                $"'{published.Id}@{open.Version}' is in review; finish that review before approving more suggestions for it.");
        }

        return playbooks.Get(published.Id, open.Version);
    }

    private static void RequirePending(Suggestion suggestion)
    {
        if (suggestion.Status != SuggestionStatus.Pending)
        {
            throw new StoreException(StoreError.Conflict, $"Suggestion {suggestion.Id} is already {suggestion.Status}.");
        }
    }

    private void Decide(long id, SuggestionStatus status, string reviewer, string? comment, string? playbook)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_suggestions SET status = $status, decided_by = $reviewer, decided_at = $now, comment = $comment, playbook_ref = $playbook
            WHERE seq = $id AND status = 'Pending'
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$reviewer", reviewer);
        command.Parameters.AddWithValue("$now", database.Now());
        command.Parameters.AddWithValue("$comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment);
        command.Parameters.AddWithValue("$playbook", (object?)playbook ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new StoreException(StoreError.Conflict, $"Suggestion {id} was decided by someone else.");
        }
    }

    private static Suggestion? Find(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ai_suggestions WHERE seq = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Suggestion Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        JsonSerializer.Deserialize<SuggestionContent>(reader.GetString(3), MapWrightJson.Options)!,
        Enum.Parse<SuggestionStatus>(reader.GetString(4)),
        reader.GetString(5),
        MapWrightDatabase.ParseTime(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : MapWrightDatabase.ParseTime(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
