using System.Text.Json;
using System.Text.RegularExpressions;
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
public sealed partial class SuggestionStore(MapWrightDatabase database, PlaybookStore playbooks)
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
    /// Needed when the AI proposed a concept no playbook defines yet, unless <paramref name="create"/> is set.
    /// </param>
    /// <param name="create">
    /// Draft what no published playbook has yet: a new domain playbook for an unknown concept, or a new attribute
    /// on the concept's playbook. The draft still goes through review and publishing.
    /// </param>
    public (Suggestion Suggestion, Playbook Draft, bool Created) Approve(long id, string reviewer, string? concept, string? comment, bool create = false)
    {
        var suggestion = Get(id);
        RequirePending(suggestion);
        var content = suggestion.Content;
        var chosen = !string.IsNullOrWhiteSpace(concept) ? concept.Trim()
            : content.BusinessConcept ?? (create && !string.IsNullOrWhiteSpace(content.ProposedConcept) ? content.ProposedConcept.Trim() : null);
        if (chosen is null)
        {
            throw new StoreException(
                StoreError.Invalid,
                $"The AI proposed a new concept '{content.ProposedConcept}'. Send create: true to draft it as a new playbook concept, " +
                "send the existing 'concept' to file the field under, or reject the suggestion.");
        }

        var parts = chosen.Split('.', 2);
        var term = string.Join(' ', NameTokens.Split(content.FieldName));
        if (term.Length == 0)
        {
            throw new StoreException(StoreError.Invalid, $"The field name '{content.FieldName}' has no letters or digits to use as a term.");
        }

        var candidates = playbooks.Library().Domains.Where(p => Same(p.Domain!.Concept.Name, parts[0])).ToList();
        if (candidates.Count == 0)
        {
            if (!create)
            {
                throw new StoreException(StoreError.Invalid, $"No published domain playbook defines the concept '{parts[0]}'; send create: true to draft a new playbook for it.");
            }

            var (created, isNew) = DraftConcept(suggestion, Pascal(parts[0]), Pascal(parts.Length == 2 ? parts[1] : content.FieldName), term, reviewer);
            Decide(id, SuggestionStatus.Approved, reviewer, comment, created.Reference);
            return (Get(id), created, isNew);
        }

        if (parts.Length == 2)
        {
            var owners = candidates.Where(p => p.Domain!.Concept.Attributes.Any(a => Same(a.Name, parts[1]))).ToList();
            if (owners.Count == 0)
            {
                if (!create)
                {
                    throw new StoreException(StoreError.Invalid, $"No published domain playbook defines the attribute '{parts[1]}' on {parts[0]}; send create: true to add it to a draft.");
                }

                if (candidates.Count > 1)
                {
                    throw new StoreException(
                        StoreError.Invalid,
                        $"'{parts[0]}' is defined by {string.Join(" and ", candidates.Select(p => p.Id))}; add the attribute '{parts[1]}' to one of them by hand.");
                }

                var open = OpenDraft(candidates[0], reviewer, id);
                var extended = playbooks.UpdateDraft(open.Id, open.Version, WithAttribute(open, Pascal(parts[1]), term, suggestion, reviewer), reviewer);
                Decide(id, SuggestionStatus.Approved, reviewer, comment, extended.Reference);
                return (Get(id), extended, false);
            }

            candidates = owners;
        }

        var published = candidates.Count == 1 ? candidates[0]
            : candidates.FirstOrDefault(p => chosen == content.BusinessConcept && p.Reference == content.DomainPlaybook)
            ?? throw new StoreException(
                StoreError.Invalid,
                $"'{chosen}' is defined by {string.Join(" and ", candidates.Select(p => p.Id))}; send the concept as Concept.Attribute to pick one.");
        var attribute = parts.Length == 2 ? published.Domain!.Concept.Attributes.First(a => Same(a.Name, parts[1])).Name : null;

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
                    Term(term, attribute, suggestion, reviewer),
                ],
            },
        };
        var saved = playbooks.UpdateDraft(edited.Id, edited.Version, edited, reviewer);
        Decide(id, SuggestionStatus.Approved, reviewer, comment, saved.Reference);
        return (Get(id), saved, false);
    }

    private static VocabularyTerm Term(string term, string? attribute, Suggestion suggestion, string reviewer) => new()
    {
        Term = term,
        AppliesTo = attribute,
        Note = $"{suggestion.System} field {suggestion.Content.Path}; AI suggestion {suggestion.Id} ({suggestion.Content.Provider}/{suggestion.Content.Model}) approved by {reviewer}.",
    };

    /// <summary>Adds the attribute if the concept lacks it, and the field's name as its term unless it is the attribute's own name.</summary>
    private static Playbook WithAttribute(Playbook draft, string attribute, string term, Suggestion suggestion, string reviewer)
    {
        var domain = draft.Domain!;
        var existing = domain.Concept.Attributes.FirstOrDefault(a => Same(a.Name, attribute));
        var name = existing?.Name ?? attribute;
        var key = string.Concat(NameTokens.Split(term));
        var needsTerm = !NameTokens.Split(name).SequenceEqual(NameTokens.Split(term))
            && !domain.Vocabulary.Any(v => string.Concat(NameTokens.Split(v.Term)) == key);
        return draft with
        {
            Domain = domain with
            {
                Concept = existing is not null ? domain.Concept : domain.Concept with
                {
                    Attributes = [.. domain.Concept.Attributes, new ConceptAttribute { Name = attribute, Description = suggestion.Content.Meaning }],
                },
                Vocabulary = needsTerm ? [.. domain.Vocabulary, Term(term, name, suggestion, reviewer)] : domain.Vocabulary,
            },
        };
    }

    /// <summary>A new draft domain playbook "domain/&lt;concept&gt;" at 0.1.0, or its open draft when an earlier approval started one.</summary>
    private (Playbook Draft, bool Created) DraftConcept(Suggestion suggestion, string concept, string attribute, string term, string reviewer)
    {
        var id = "domain/" + Slug(concept);
        var versions = playbooks.List().Where(v => v.Id == id).ToList();
        var open = versions.FirstOrDefault(v => v.Status is PlaybookStatus.Draft or PlaybookStatus.InReview);
        if (open?.Status == PlaybookStatus.InReview)
        {
            throw new StoreException(StoreError.Conflict, $"'{open.Reference}' is in review; finish that review before approving more suggestions for it.");
        }

        if (open is not null)
        {
            var draft = playbooks.Get(id, open.Version);
            if (draft.Domain is null || !Same(draft.Domain.Concept.Name, concept))
            {
                throw new StoreException(StoreError.Conflict, $"'{draft.Reference}' is not about {concept}; add the concept to a playbook by hand.");
            }

            return (playbooks.UpdateDraft(id, open.Version, WithAttribute(draft, attribute, term, suggestion, reviewer), reviewer), false);
        }

        if (versions.Count > 0)
        {
            throw new StoreException(StoreError.Conflict, $"'{id}' exists but has no published or open version; draft a new version of it first.");
        }

        const string version = "0.1.0";
        var note = $"Drafted from AI suggestion {suggestion.Id} for {suggestion.System} field {suggestion.Content.Path}.";
        var playbook = new Playbook
        {
            SpecVersion = Playbook.CurrentSpecVersion,
            Id = id,
            Name = Spaced(concept),
            Kind = PlaybookKind.Domain,
            Version = version,
            Status = PlaybookStatus.Draft,
            Owner = reviewer,
            Description = $"{note} Add detection signals and tests before submitting it for review.",
            ChangeNotes = [new() { Version = version, Date = DateOnly.FromDateTime(database.Time.GetUtcNow().UtcDateTime), Author = reviewer, Description = note }],
            Domain = new()
            {
                Concept = new() { Name = concept, Attributes = [] },
            },
        };
        return (playbooks.Create(WithAttribute(playbook, attribute, term, suggestion, reviewer), reviewer), true);
    }

    /// <summary>"merchant.sales_rep" → "Merchant", "SalesRep"; names already in PascalCase are kept.</summary>
    private static string Pascal(string name)
    {
        var parts = NonAlphanumeric().Split(name.Trim()).Where(p => p.Length > 0).ToList();
        var pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return pascal.Length == 0 || !char.IsLetter(pascal[0])
            ? throw new StoreException(StoreError.Invalid, $"'{name}' cannot be used as a concept or attribute name; it must start with a letter.")
            : pascal;
    }

    private static string Slug(string pascal) => WordStart().Replace(pascal, "-").ToLowerInvariant();

    private static string Spaced(string pascal) => WordStart().Replace(pascal, " ");

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordStart();

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
