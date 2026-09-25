using System.Net;
using MapWright.Core.Matching;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class MappingApiTests : IDisposable
{
    private readonly ApiFactory _api = new();

    public void Dispose() => _api.Dispose();

    private static string Systems(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "samples", "systems", .. parts]);

    private static string Mappings(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "samples", "mappings", "sales-alpha__uw-core", .. parts]);

    private static IEnumerable<string> Files(string directory) => Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal);

    private async Task<HttpClient> WithProfiles()
    {
        var ana = _api.As("ana");
        foreach (var system in new[] { "sales-alpha", "uw-core" })
        {
            var put = await ana.PutJson($"/api/profiles/{system}", await File.ReadAllTextAsync(Systems(system, "profile.json")));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        return ana;
    }

    [Fact]
    public async Task Profiles_are_built_from_uploaded_samples_and_contracts()
    {
        var ana = _api.As("ana");

        var created = await ana.Upload(
            "/api/profiles", [.. Files(Systems("sales-alpha", "samples")), .. Files(Systems("sales-alpha", "contracts"))],
            ("system", "SalesAlpha CRM"), ("root", "submitApplication"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/profiles/salesalpha-crm", created.Headers.Location?.OriginalString);
        var profile = ProfileSerializer.Deserialize(await created.Content.ReadAsStringAsync());
        Assert.Equal((36, 0, 6), (profile.Fields.Count, profile.Findings.Count, profile.Inputs.Count));

        var again = await ana.Upload("/api/profiles", Files(Systems("sales-alpha", "samples")), ("system", "SalesAlpha CRM"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var replaced = await ana.Upload("/api/profiles", Files(Systems("sales-alpha", "samples")), ("system", "SalesAlpha CRM"), ("replace", "true"));
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);

        var xml = await ana.Upload(
            "/api/profiles", [.. Files(Systems("uw-core", "samples")), Systems("uw-core", "contracts", "uw-core-intake.xsd")],
            ("system", "UW Core"), ("id", "uw-core"));
        Assert.Equal(PayloadFormat.Xml, ProfileSerializer.Deserialize(await xml.Content.ReadAsStringAsync()).Format);

        var list = (await (await ana.GetAsync("/api/profiles")).Node()).AsArray();
        Assert.Equal(["salesalpha-crm", "uw-core"], list.Select(p => p!["id"].Text()).Order(StringComparer.Ordinal));

        var noSystem = await ana.Upload("/api/profiles", Files(Systems("sales-alpha", "samples")));
        Assert.Equal(HttpStatusCode.BadRequest, noSystem.StatusCode);
        var unsupported = await ana.Upload("/api/profiles", [Path.Combine(AppContext.BaseDirectory, "MapWright.Tests.dll")], ("system", "X"));
        Assert.Contains("not a supported file", (await unsupported.Node())["detail"].Text());

        Assert.Equal(HttpStatusCode.NoContent, (await ana.DeleteAsync("/api/profiles/uw-core")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.GetAsync("/api/profiles/uw-core")).StatusCode);
    }

    [Fact]
    public async Task Detect_uses_the_published_playbooks()
    {
        var client = await WithProfiles();

        var report = await (await client.PostAsync("/api/profiles/sales-alpha/detect", null)).Node();

        Assert.Equal("SalesAlpha CRM", report["system"].Text());
        Assert.Contains(report["recognised"]!.AsArray(), r => r!["path"].Text() == "$.account.taxId");
        Assert.NotEmpty(report["remaining"]!.AsArray());
    }

    [Fact]
    public async Task Generated_mapping_matches_the_engine_and_exports()
    {
        var client = await WithProfiles();

        var created = await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = MappingSpecSerializer.Deserialize(await created.Content.ReadAsStringAsync());
        var expected = MappingGenerator.Generate(
            ProfileSerializer.Load(Systems("sales-alpha", "profile.json")), ProfileSerializer.Load(Systems("uw-core", "profile.json")),
            StarterPlaybooks.Library(), new() { Id = "sales-alpha__uw-core", CreatedAt = document.CreatedAt });
        Assert.Equal(MappingSpecSerializer.Serialize(expected), MappingSpecSerializer.Serialize(document));

        Assert.Equal(HttpStatusCode.Conflict, (await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Post("/api/mappings", new { source = "sales-alpha", target = "nope" })).StatusCode);

        var summary = await (await client.GetAsync("/api/mappings/sales-alpha__uw-core/summary")).Node();
        Assert.Equal(document.Mappings.Count, summary["totalTargetFields"]!.GetValue<int>());

        var xlsx = await client.GetAsync("/api/mappings/sales-alpha__uw-core/export/xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        Assert.True((await xlsx.Content.ReadAsByteArrayAsync()).Length > 1000);
        var csv = await client.GetAsync("/api/mappings/sales-alpha__uw-core/export/csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/mappings/sales-alpha__uw-core/export/pdf")).StatusCode);
    }

    [Fact]
    public async Task Replay_builds_the_committed_target_payloads_and_records_runs()
    {
        var client = await WithProfiles();
        await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core" });

        var replay = await client.Upload(
            "/api/mappings/sales-alpha__uw-core/replay", Files(Systems("sales-alpha", "samples")),
            ("target", "uw-core"), ("xmlNamespace", "urn:uwcore:intake:4.2"), ("record", "true"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var body = await replay.Node();

        Assert.True(body["recorded"]!.GetValue<bool>());
        var samples = body["samples"]!.AsArray();
        Assert.Equal(["V001", "V002", "V003"], samples.Select(s => s!["run"]!["id"].Text()));
        foreach (var sample in samples)
        {
            var name = Path.ChangeExtension(sample!["sample"].Text(), ".xml");
            Assert.Equal((await File.ReadAllTextAsync(Mappings("replay", name))).TrimEnd(), sample["payload"].Text());
        }

        var stored = MappingSpecSerializer.Deserialize(await client.GetStringAsync("/api/mappings/sales-alpha__uw-core"));
        Assert.Equal(3, stored.ValidationRuns.Count);

        var masked = await (await client.Upload(
            "/api/mappings/sales-alpha__uw-core/replay", Files(Systems("sales-alpha", "samples")), ("target", "uw-core"), ("mask", "true"))).Node();
        Assert.Equal((true, false), (masked["masked"]!.GetValue<bool>(), masked["recorded"]!.GetValue<bool>()));
        var soleProp = masked["samples"]!.AsArray().Single(s => s!["sample"].Text() == "sole-prop.json")!;
        Assert.Contains("<SSN>*****5566</SSN>", soleProp["payload"].Text());
        Assert.DoesNotContain("900445566", soleProp["payload"].Text());
        Assert.Equal(
            samples.Select(s => s!["run"]!["results"]!.AsArray().Select(r => r!["outcome"].Text())),
            masked["samples"]!.AsArray().Select(s => s!["run"]!["results"]!.AsArray().Select(r => r!["outcome"].Text())));
        Assert.Equal(3, MappingSpecSerializer.Deserialize(await client.GetStringAsync("/api/mappings/sales-alpha__uw-core")).ValidationRuns.Count);

        var wrongTarget = await client.Upload("/api/mappings/sales-alpha__uw-core/replay", Files(Systems("sales-alpha", "samples")), ("target", "sales-alpha"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongTarget.StatusCode);
        var wrongSample = await client.Upload("/api/mappings/sales-alpha__uw-core/replay", Files(Systems("uw-core", "samples")), ("target", "uw-core"));
        Assert.Contains("mapping's source is Json", (await wrongSample.Node())["detail"].Text());
    }

    [Fact]
    public async Task Rows_are_reviewed_and_the_decisions_kept()
    {
        var client = await WithProfiles();
        var created = await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core" });
        var document = MappingSpecSerializer.Deserialize(await created.Content.ReadAsStringAsync());
        var row = document.Mappings.First(m => m.Review.Status == ReviewStatus.NeedsReview && m.Sources.Count > 0);

        var approved = await client.Post($"/api/mappings/sales-alpha__uw-core/rows/{row.Id}/review", new { decision = "approve", comment = "Checked." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("needsReview", (await approved.Node())["previousStatus"].Text());

        var overridden = await client.Post(
            $"/api/mappings/sales-alpha__uw-core/rows/{row.Id}/review",
            new { decision = "override", row = row with { ConfidencePercent = 100, Reasoning = "Confirmed with the UW team." } });
        Assert.Equal(HttpStatusCode.OK, overridden.StatusCode);
        var badOverride = await client.Post(
            $"/api/mappings/sales-alpha__uw-core/rows/{row.Id}/review", new { decision = "override", row = row with { Id = "M999" } });
        Assert.Equal(HttpStatusCode.BadRequest, badOverride.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Post("/api/mappings/sales-alpha__uw-core/rows/M999/review", new { decision = "approve" })).StatusCode);

        var stored = MappingSpecSerializer.Deserialize(await client.GetStringAsync("/api/mappings/sales-alpha__uw-core"));
        var reviewed = stored.Mappings.Single(m => m.Id == row.Id);
        Assert.Equal((ReviewStatus.Overridden, "ana", "Confirmed with the UW team."), (reviewed.Review.Status, reviewed.Review.Reviewer, reviewed.Reasoning));

        var decisions = (await (await client.GetAsync("/api/mappings/sales-alpha__uw-core/reviews")).Node()).AsArray();
        Assert.Equal(["approve", "override"], decisions.Select(d => d!["decision"].Text()));
    }
}
