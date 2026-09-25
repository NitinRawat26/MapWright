using System.Text.Json;
using MapWright.Core;
using MapWright.Core.Spec;
using Microsoft.Data.Sqlite;

namespace MapWright.Store;

public sealed record MappingSummaryItem(
    string Id,
    string Title,
    string Version,
    string SourceSystem,
    string TargetSystem,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public enum ReviewDecisionKind
{
    Approve,
    Reject,
    Override,
}

/// <summary>One reviewer decision on a mapping row, with the row as it was after the decision.</summary>
public sealed record ReviewDecision(
    long Sequence,
    string MappingId,
    string RowId,
    ReviewDecisionKind Decision,
    ReviewStatus PreviousStatus,
    string Reviewer,
    string? Comment,
    FieldMapping Row,
    DateTimeOffset DecidedAt);

/// <summary>Stored mapping specs and the review decisions made on their rows.</summary>
public sealed class MappingStore(MapWrightDatabase database)
{
    public IReadOnlyList<MappingSummaryItem> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, version, source_system, target_system, created_at, updated_at, updated_by FROM mappings ORDER BY id";
        var summaries = new List<MappingSummaryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                MapWrightDatabase.ParseTime(reader.GetString(5)),
                MapWrightDatabase.ParseTime(reader.GetString(6)),
                reader.GetString(7)));
        }

        return summaries;
    }

    public MappingDocument Get(string id) => Find(id) ?? throw StoreException.NotFound($"Mapping '{id}'");

    public MappingDocument? Find(string id)
    {
        using var connection = database.Open();
        return Find(connection, null, id);
    }

    /// <summary>Creates or replaces the mapping under its own id. Specs with validation errors are rejected.</summary>
    public void Save(MappingDocument document, string actor)
    {
        MapWrightDatabase.RequireId(document.Id, "Mapping");
        RequireValid(document);
        using var connection = database.Open();
        Upsert(connection, null, document, actor);
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mappings WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
        {
            throw StoreException.NotFound($"Mapping '{id}'");
        }
    }

    /// <summary>
    /// Approves, rejects or overrides one row. An override replaces the row (same id and target path). The row's
    /// review block and the mapping's change log record who decided what and when.
    /// </summary>
    public ReviewDecision Decide(
        string mappingId, string rowId, ReviewDecisionKind decision, string reviewer, string? comment, FieldMapping? replacement = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var document = Find(connection, transaction, mappingId) ?? throw StoreException.NotFound($"Mapping '{mappingId}'");
        var index = document.Mappings.ToList().FindIndex(m => m.Id == rowId);
        if (index < 0)
        {
            throw StoreException.NotFound($"Row '{rowId}' in mapping '{mappingId}'");
        }

        var current = document.Mappings[index];
        if (decision == ReviewDecisionKind.Override)
        {
            if (replacement is null)
            {
                throw new StoreException(StoreError.Invalid, "An override needs the replacement row.");
            }

            if (replacement.Id != rowId || replacement.Target.Path != current.Target.Path)
            {
                throw new StoreException(StoreError.Invalid, $"The replacement row must keep id '{rowId}' and target '{current.Target.Path}'.");
            }
        }
        else if (replacement is not null)
        {
            throw new StoreException(StoreError.Invalid, "Only an override takes a replacement row.");
        }

        var today = DateOnly.FromDateTime(database.Time.GetUtcNow().UtcDateTime);
        var status = decision switch
        {
            ReviewDecisionKind.Approve => ReviewStatus.Approved,
            ReviewDecisionKind.Reject => ReviewStatus.Rejected,
            _ => ReviewStatus.Overridden,
        };
        var row = (replacement ?? current) with
        {
            Review = new()
            {
                Status = status,
                Reviewer = reviewer,
                ReviewedOn = today,
                Comments = comment,
                OpenQuestion = decision == ReviewDecisionKind.Reject ? current.Review.OpenQuestion : null,
            },
        };

        List<FieldMapping> rows = [.. document.Mappings];
        rows[index] = row;
        var updated = document with
        {
            Mappings = rows,
            ChangeLog =
            [
                .. document.ChangeLog,
                new ChangeLogEntry
                {
                    Version = document.Version,
                    Date = today,
                    Author = reviewer,
                    Description = $"{rowId} {current.Target.Path}: {status}{(string.IsNullOrWhiteSpace(comment) ? "" : $" ({comment})")}.",
                },
            ],
        };

        RequireValid(updated);
        Upsert(connection, transaction, updated, reviewer);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO review_decisions (mapping_id, row_id, decision, previous_status, reviewer, comment, row_json, decided_at)
            VALUES ($mapping, $row, $decision, $previous, $reviewer, $comment, $json, $now);
            SELECT last_insert_rowid();
            """;
        var now = database.Now();
        command.Parameters.AddWithValue("$mapping", mappingId);
        command.Parameters.AddWithValue("$row", rowId);
        command.Parameters.AddWithValue("$decision", decision.ToString());
        command.Parameters.AddWithValue("$previous", current.Review.Status.ToString());
        command.Parameters.AddWithValue("$reviewer", reviewer);
        command.Parameters.AddWithValue("$comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, MapWrightJson.Options));
        command.Parameters.AddWithValue("$now", now);
        var sequence = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return new(sequence, mappingId, rowId, decision, current.Review.Status, reviewer, comment, row, MapWrightDatabase.ParseTime(now));
    }

    public IReadOnlyList<ReviewDecision> Decisions(string mappingId)
    {
        using var connection = database.Open();
        if (Find(connection, null, mappingId) is null)
        {
            throw StoreException.NotFound($"Mapping '{mappingId}'");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, mapping_id, row_id, decision, previous_status, reviewer, comment, row_json, decided_at
            FROM review_decisions WHERE mapping_id = $mapping ORDER BY seq
            """;
        command.Parameters.AddWithValue("$mapping", mappingId);
        var decisions = new List<ReviewDecision>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            decisions.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<ReviewDecisionKind>(reader.GetString(3)),
                Enum.Parse<ReviewStatus>(reader.GetString(4)),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<FieldMapping>(reader.GetString(7), MapWrightJson.Options)!,
                MapWrightDatabase.ParseTime(reader.GetString(8))));
        }

        return decisions;
    }

    private static void RequireValid(MappingDocument document)
    {
        var errors = MappingSpecValidator.Validate(document).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new StoreException(StoreError.Invalid, $"Mapping '{document.Id}': {errors.Count} error(s).", errors);
        }
    }

    private static MappingDocument? Find(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM mappings WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? MappingSpecSerializer.Deserialize(json) : null;
    }

    private void Upsert(SqliteConnection connection, SqliteTransaction? transaction, MappingDocument document, string actor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mappings (id, title, version, source_system, target_system, json, created_at, updated_at, updated_by)
            VALUES ($id, $title, $version, $source, $target, $json, $now, $now, $actor)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, version = excluded.version, source_system = excluded.source_system,
                target_system = excluded.target_system, json = excluded.json, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """;
        command.Parameters.AddWithValue("$id", document.Id);
        command.Parameters.AddWithValue("$title", document.Title);
        command.Parameters.AddWithValue("$version", document.Version);
        command.Parameters.AddWithValue("$source", document.Source.Name);
        command.Parameters.AddWithValue("$target", document.Target.Name);
        command.Parameters.AddWithValue("$json", MappingSpecSerializer.Serialize(document));
        command.Parameters.AddWithValue("$now", database.Now());
        command.Parameters.AddWithValue("$actor", actor);
        command.ExecuteNonQuery();
    }
}
