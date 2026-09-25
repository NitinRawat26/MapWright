using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Store;

namespace MapWright.Tests;

public sealed class StoreTests : IDisposable
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new();
    private readonly MapWrightDatabase _database;

    public StoreTests() => _database = new(new() { DatabasePath = ":memory:" }, _time);

    public void Dispose() => _database.Dispose();

    private PlaybookStore Playbooks => new(_database);

    private static string Json(FieldMapping row) => System.Text.Json.JsonSerializer.Serialize(row, MapWright.Core.MapWrightJson.Options);

    private PlaybookStore Imported()
    {
        var store = Playbooks;
        store.Import(StarterPlaybooks.Library().All, "seed");
        return store;
    }

    [Fact]
    public void Import_keeps_status_and_skips_versions_already_stored()
    {
        var store = Playbooks;

        var first = store.Import(StarterPlaybooks.Library().All, "seed");
        var second = store.Import(StarterPlaybooks.Library().All, "seed");

        Assert.Equal(6, first.Count);
        Assert.Empty(second);
        Assert.All(store.List(), s => Assert.Equal((PlaybookStatus.Published, "seed"), (s.Status, s.CreatedBy)));
        Assert.Equal(["domain/channel-mix", "domain/entity-type", "domain/principals", "domain/processing-volume", "domain/tax-id", "process/onboard-new-system"],
            store.List().Select(s => s.Id));
        Assert.Equal(5, store.Library().Domains.Count());
        Assert.Equal("imported", Assert.Single(store.History("domain/tax-id")).Action);
    }

    [Fact]
    public void Playbook_lifecycle_runs_draft_review_publish_and_retires_the_previous_version()
    {
        var store = Imported();

        var draft = store.DraftNewVersion("domain/tax-id", "1.0.0", null, "ana", "Add TIN term.");
        Assert.Equal(("1.1.0", PlaybookStatus.Draft), (draft.Version, draft.Status));
        Assert.Equal(("1.1.0", "ana", "Add TIN term."), (draft.ChangeNotes[^1].Version, draft.ChangeNotes[^1].Author, draft.ChangeNotes[^1].Description));

        var edited = draft with { Description = "Tax identifiers (EIN, SSN, TIN)." };
        store.UpdateDraft("domain/tax-id", "1.1.0", edited, "ana");
        Assert.Equal("Tax identifiers (EIN, SSN, TIN).", store.Get("domain/tax-id", "1.1.0").Description);

        store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.InReview, "ana", null);
        var selfPublish = Assert.Throws<StoreException>(() => store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.Published, "ANA", null));
        Assert.Equal(StoreError.Conflict, selfPublish.Error);
        Assert.Contains("another reviewer", selfPublish.Message);

        _time.Now = _time.Now.AddHours(1);
        var published = store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.Published, "ben", "Looks good.");

        Assert.Equal(PlaybookStatus.Published, published.Status);
        Assert.Equal(PlaybookStatus.Published, store.Get("domain/tax-id", "1.1.0").Status);
        Assert.Equal(PlaybookStatus.Retired, store.Get("domain/tax-id", "1.0.0").Status);
        Assert.Equal("1.1.0", store.Library().Active.Single(p => p.Id == "domain/tax-id").Version);
        Assert.Equal([("1.0.0", PlaybookStatus.Published), ("1.1.0", PlaybookStatus.Published)],
            store.Versions("domain/tax-id").Select(v => (v.Version, v.Status == PlaybookStatus.Retired ? PlaybookStatus.Published : v.Status)));

        var history = store.History("domain/tax-id");
        Assert.Equal(["imported", "drafted", "edited", "submitted", "retired", "published"], history.Select(e => e.Action));
        Assert.Equal(("ben", "Looks good.", _time.Now), (history[^1].Actor, history[^1].Note, history[^1].OccurredAt));
        Assert.Equal(("1.0.0", "Replaced by 1.1.0."), (history[^2].Version, history[^2].Note));

        store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.Retired, "ben", null);
        Assert.DoesNotContain(store.Library().Active, p => p.Id == "domain/tax-id");
    }

    [Fact]
    public void Only_drafts_are_editable_and_transitions_follow_the_lifecycle()
    {
        var store = Imported();
        var published = store.Get("domain/tax-id", "1.0.0");

        Assert.Equal(StoreError.Conflict, Assert.Throws<StoreException>(() => store.UpdateDraft("domain/tax-id", "1.0.0", published, "ana")).Error);
        Assert.Equal(StoreError.Conflict, Assert.Throws<StoreException>(() => store.Transition("domain/tax-id", "1.0.0", PlaybookStatus.Draft, "ana", null)).Error);
        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() => store.UpdateDraft("domain/tax-id", "9.9.9", published, "ana")).Error);
        Assert.Equal(StoreError.NotFound, Assert.Throws<StoreException>(() => store.Get("domain/nope", "1.0.0")).Error);
        Assert.Equal(StoreError.NotFound, Assert.Throws<StoreException>(() => store.Versions("domain/nope")).Error);

        store.DraftNewVersion("domain/tax-id", "1.0.0", "2.0.0", "ana", null);
        var second = Assert.Throws<StoreException>(() => store.DraftNewVersion("domain/tax-id", "1.0.0", "3.0.0", "ana", null));
        Assert.Contains("open version 2.0.0 (Draft)", second.Message);
        Assert.Equal(StoreError.Conflict, Assert.Throws<StoreException>(() => store.DraftNewVersion("domain/tax-id", "1.0.0", "1.0.0", "ana", null)).Error);

        store.Transition("domain/tax-id", "2.0.0", PlaybookStatus.InReview, "ana", null);
        store.Transition("domain/tax-id", "2.0.0", PlaybookStatus.Draft, "ben", "Needs a test for TIN.");
        store.Transition("domain/tax-id", "2.0.0", PlaybookStatus.Retired, "ana", "Abandoned.");
        Assert.Equal(["imported", "drafted", "submitted", "changesRequested", "abandoned"], store.History("domain/tax-id").Select(e => e.Action));
        Assert.Equal("1.0.0", store.Library().Active.Single(p => p.Id == "domain/tax-id").Version);
    }

    [Fact]
    public void Default_new_version_skips_versions_that_already_exist()
    {
        var store = Imported();
        Assert.Equal("1.1.0", store.DraftNewVersion("domain/tax-id", "1.0.0", null, "ana", null).Version);
        store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.Retired, "ana", "Abandoned.");

        Assert.Equal("1.2.0", store.DraftNewVersion("domain/tax-id", "1.0.0", null, "ana", null).Version);
        store.Transition("domain/tax-id", "1.2.0", PlaybookStatus.Retired, "ana", "Abandoned.");

        Assert.Equal("1.3.0", store.DraftNewVersion("domain/tax-id", "1.1.0", null, "ana", null).Version);
    }

    [Fact]
    public void Invalid_playbooks_and_failing_tests_are_rejected_with_their_issues()
    {
        var store = Imported();
        var draft = store.DraftNewVersion("domain/tax-id", "1.0.0", null, "ana", null);

        var invalid = Assert.Throws<StoreException>(() => store.UpdateDraft(draft.Id, draft.Version, draft with { Name = " " }, "ana"));
        Assert.Equal(StoreError.Invalid, invalid.Error);
        Assert.Contains(invalid.Issues, i => i.Code == "PB004");

        var failingTest = draft.Domain!.Tests.First(t => t.Expect is not null) with { Expect = null };
        var withFailingTest = draft with { Domain = draft.Domain with { Tests = [.. draft.Domain.Tests.Select(t => t.Id == failingTest.Id ? failingTest : t)] } };
        store.UpdateDraft(draft.Id, draft.Version, withFailingTest, "ana");
        var submit = Assert.Throws<StoreException>(() => store.Transition(draft.Id, draft.Version, PlaybookStatus.InReview, "ana", null));
        Assert.Contains(submit.Issues, i => i.Code == "PBTEST" && i.Location.Contains(failingTest.Id, StringComparison.Ordinal));
        Assert.Equal(PlaybookStatus.Draft, store.Get(draft.Id, draft.Version).Status);

        var created = Assert.Throws<StoreException>(() => store.Create(StarterPlaybooks.Get("domain/principals") with { Id = "domain/owners-v2" }, "ana"));
        Assert.Contains("starts as a draft", created.Message);
        var brandNew = StarterPlaybooks.Get("domain/principals") with { Id = "domain/owners-v2", Status = PlaybookStatus.Draft };
        store.Create(brandNew, "ana");
        Assert.Equal(StoreError.Conflict, Assert.Throws<StoreException>(() => store.Create(brandNew, "ana")).Error);
        Assert.Equal(PlaybookStatus.Draft, store.Versions("domain/owners-v2").Single().Status);
    }

    [Fact]
    public void Yaml_comments_survive_new_versions_status_changes_and_edits()
    {
        var store = Playbooks;
        store.Import(PlaybookLibrary.Read([StarterPlaybooks.Directory]), "seed");
        var file = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.yaml"));
        Assert.Equal(file, store.GetYaml("domain/tax-id", "1.0.0"));

        store.DraftNewVersion("domain/tax-id", "1.0.0", null, "ana", "Add VAT.");
        var drafted = store.GetYaml("domain/tax-id", "1.1.0");
        Assert.StartsWith("# Domain playbook: Tax ID.", drafted, StringComparison.Ordinal);
        Assert.Equal(PlaybookSerializer.Serialize(store.Get("domain/tax-id", "1.1.0")), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(drafted)));

        var edited = drafted.Replace("# Names systems use", "# Reviewed by ana.\n  # Names systems use", StringComparison.Ordinal)
            .Replace("\nstatus: draft", "\nstatus: published", StringComparison.Ordinal);
        var saved = store.UpdateDraft("domain/tax-id", "1.1.0", PlaybookSerializer.Deserialize(edited), "ana", edited);
        Assert.Equal(PlaybookStatus.Draft, saved.Status);
        var stored = store.GetYaml("domain/tax-id", "1.1.0");
        Assert.Contains("# Reviewed by ana.", stored, StringComparison.Ordinal);
        Assert.Contains("\nstatus: draft\n", stored, StringComparison.Ordinal);

        store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.InReview, "ana", null);
        store.Transition("domain/tax-id", "1.1.0", PlaybookStatus.Published, "ben", null);
        Assert.Contains("# Reviewed by ana.", store.GetYaml("domain/tax-id", "1.1.0"), StringComparison.Ordinal);
        Assert.Contains("\nstatus: published\n", store.GetYaml("domain/tax-id", "1.1.0"), StringComparison.Ordinal);
        Assert.Contains("\nstatus: retired\n", store.GetYaml("domain/tax-id", "1.0.0"), StringComparison.Ordinal);
        Assert.StartsWith("# Domain playbook: Tax ID.", store.GetYaml("domain/tax-id", "1.0.0"), StringComparison.Ordinal);
    }

    [Fact]
    public void Playbooks_without_yaml_are_served_as_generated_yaml()
    {
        var store = Imported();

        var yaml = store.GetYaml("domain/principals", "1.0.0");

        Assert.DoesNotContain("#", yaml.Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal(PlaybookSerializer.Serialize(store.Get("domain/principals", "1.0.0")), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(yaml)));
    }

    [Fact]
    public void Databases_from_before_yaml_gain_the_yaml_column()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("mapwright-db-").FullName, "old.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE playbook_versions (
                        id TEXT NOT NULL, version TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, status TEXT NOT NULL,
                        owner TEXT, json TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT NOT NULL,
                        updated_at TEXT NOT NULL, updated_by TEXT NOT NULL, PRIMARY KEY (id, version));
                    """;
                command.ExecuteNonQuery();
            }

            using var database = new MapWrightDatabase(new() { DatabasePath = path }, _time);
            var store = new PlaybookStore(database);
            store.Import(PlaybookLibrary.Read([StarterPlaybooks.Directory]), "seed");
            Assert.StartsWith("# Domain playbook: Tax ID.", store.GetYaml("domain/tax-id", "1.0.0"), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Database_file_keeps_data_between_connections()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("mapwright-db-").FullName, "nested", "mapwright.db");
        try
        {
            using (var database = new MapWrightDatabase(new() { DatabasePath = path }, _time))
            {
                new PlaybookStore(database).Import(StarterPlaybooks.Library().All, "seed");
            }

            using var reopened = new MapWrightDatabase(new() { DatabasePath = path }, _time);
            Assert.Equal(6, new PlaybookStore(reopened).List(PlaybookStatus.Published).Count);
            Assert.Empty(new PlaybookStore(reopened).List(PlaybookStatus.Draft));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, recursive: true);
        }
    }

    [Fact]
    public void Profiles_are_saved_listed_replaced_and_deleted()
    {
        var store = new ProfileStore(_database);
        var profile = ProfileSerializer.Load(Path.Combine(AppContext.BaseDirectory, "samples", "systems", "uw-core", "profile.json"));

        store.Save("uw-core", profile, "ana");
        _time.Now = _time.Now.AddMinutes(5);
        store.Save("uw-core", profile with { Version = "4.3" }, "ben");

        var summary = Assert.Single(store.List());
        Assert.Equal(("uw-core", "UW Core", "4.3", PayloadFormat.Xml, profile.Fields.Count, "ben"),
            (summary.Id, summary.System, summary.Version, summary.Format, summary.FieldCount, summary.UpdatedBy));
        Assert.True(summary.UpdatedAt > summary.CreatedAt);
        Assert.Equal(ProfileSerializer.Serialize(profile with { Version = "4.3" }), ProfileSerializer.Serialize(store.Get("uw-core")));

        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() => store.Save("UW Core", profile, "ana")).Error);
        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() => store.Save("unnamed", profile with { System = " " }, "ana")).Error);
        store.Delete("uw-core");
        Assert.Null(store.Find("uw-core"));
        Assert.Equal(StoreError.NotFound, Assert.Throws<StoreException>(() => store.Delete("uw-core")).Error);
        Assert.Equal("salesalpha-crm", MapWrightDatabase.Slug("  SalesAlpha CRM! "));
    }

    [Fact]
    public void Review_decisions_update_the_row_and_are_recorded()
    {
        var store = new MappingStore(_database);
        var mapping = TestSpecs.Sample();
        store.Save(mapping, "ana");
        var row = mapping.Mappings.First(m => m.Review.Status == ReviewStatus.NeedsReview);

        var approved = store.Decide(mapping.Id, row.Id, ReviewDecisionKind.Approve, "ben", "Confirmed with UW.");
        Assert.Equal((ReviewStatus.Approved, "ben", new DateOnly(2026, 9, 24), "Confirmed with UW."),
            (approved.Row.Review.Status, approved.Row.Review.Reviewer, approved.Row.Review.ReviewedOn, approved.Row.Review.Comments));
        Assert.Equal(ReviewStatus.NeedsReview, approved.PreviousStatus);

        var replacement = row with { ConfidencePercent = 100, Reasoning = "Reviewer set the rule." };
        var overridden = store.Decide(mapping.Id, row.Id, ReviewDecisionKind.Override, "ben", null, replacement);
        Assert.Equal((ReviewStatus.Overridden, 100), (overridden.Row.Review.Status, overridden.Row.ConfidencePercent));

        var stored = store.Get(mapping.Id);
        Assert.Equal(Json(overridden.Row), Json(stored.Mappings.Single(m => m.Id == row.Id)));
        Assert.Equal(mapping.ChangeLog.Count + 2, stored.ChangeLog.Count);
        Assert.Contains($"{row.Id} {row.Target.Path}: Approved (Confirmed with UW.).", stored.ChangeLog[^2].Description);

        var decisions = store.Decisions(mapping.Id);
        Assert.Equal([ReviewDecisionKind.Approve, ReviewDecisionKind.Override], decisions.Select(d => d.Decision));
        Assert.Equal(ReviewStatus.Approved, decisions[1].PreviousStatus);
        Assert.Equal(Json(overridden.Row), Json(decisions[1].Row));

        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() => store.Decide(mapping.Id, row.Id, ReviewDecisionKind.Override, "ben", null)).Error);
        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() =>
            store.Decide(mapping.Id, row.Id, ReviewDecisionKind.Override, "ben", null, row with { Id = "M999" })).Error);
        Assert.Equal(StoreError.Invalid, Assert.Throws<StoreException>(() =>
            store.Decide(mapping.Id, row.Id, ReviewDecisionKind.Approve, "ben", null, row)).Error);
        Assert.Equal(StoreError.NotFound, Assert.Throws<StoreException>(() => store.Decide(mapping.Id, "M999", ReviewDecisionKind.Approve, "ben", null)).Error);
        Assert.Equal(StoreError.NotFound, Assert.Throws<StoreException>(() => store.Decisions("nope")).Error);

        var summary = Assert.Single(store.List());
        Assert.Equal((mapping.Id, mapping.Source.Name, mapping.Target.Name, "ben"), (summary.Id, summary.SourceSystem, summary.TargetSystem, summary.UpdatedBy));
        store.Delete(mapping.Id);
        Assert.Empty(store.List());
    }
}
