using System.Net;
using System.Text.Json;
using MapWright.Ai;
using MapWright.Core.Playbooks;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class AiApiTests
{
    private static string Systems(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "samples", "systems", .. parts]);

    private static FakeProvider Fake() => new("fake", prompt => prompt.ResponseSchema["properties"]?["pairings"] is not null
        ? JsonSerializer.Serialize(new
        {
            pairings = new[]
            {
                new { target = "/UnderwritingRequest/Merchant/EstablishedDate", sources = new[] { "$.account.incorporationDate" }, confidence = 95, reasoning = "Both are the date the business started." },
            },
        })
        : AiSamples.Answer(
            new { path = "$.account.legalName", concept = "LegalEntity", meaning = "Registered business name", confidence = 90, reasoning = "Name under account." },
            new { path = "$.account.phone", concept = "ChannelMix.Moto", meaning = "Phone orders", confidence = 90, reasoning = "Phone." },
            new { path = "$.account.mcc", newConcept = "Merchant.Mcc", meaning = "Merchant category code", confidence = 88, reasoning = "4-digit codes." }));

    private static async Task<HttpClient> WithProfiles(ApiFactory api)
    {
        var ana = api.As("ana");
        foreach (var system in new[] { "sales-alpha", "uw-core" })
        {
            Assert.Equal(HttpStatusCode.OK, (await ana.PutJson($"/api/profiles/{system}", await File.ReadAllTextAsync(Systems(system, "profile.json")))).StatusCode);
        }

        return ana;
    }

    [Fact]
    public async Task Ai_is_off_unless_configured_and_asked_for()
    {
        using var none = new ApiFactory();
        var client = await WithProfiles(none);
        var status = await (await client.GetAsync("/api/ai")).Node();
        Assert.False(status["available"]!.GetValue<bool>());
        Assert.Equal(70, status["maxConfidence"]!.GetValue<int>());

        var refused = await client.Post("/api/profiles/sales-alpha/detect", new { useAi = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("No AI provider is configured", (await refused.Node())["detail"].Text());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", useAi = true })).StatusCode);

        var fake = Fake();
        using var api = new ApiFactory(fake);
        client = await WithProfiles(api);
        Assert.Equal("fake", (await (await client.GetAsync("/api/ai")).Node())["provider"].Text());
        var playbooksOnly = await (await client.PostAsync("/api/profiles/sales-alpha/detect", null)).Node();
        Assert.Empty(playbooksOnly["suggestions"]!.AsArray());
        Assert.Equal(HttpStatusCode.Created, (await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core" })).StatusCode);
        Assert.Empty(fake.Prompts);
    }

    [Fact]
    public async Task Approved_suggestions_become_draft_playbook_terms()
    {
        using var api = new ApiFactory(Fake());
        var ana = await WithProfiles(api);
        var ben = api.As("ben");

        Assert.Equal(HttpStatusCode.BadRequest, (await api.CreateClient().Post("/api/profiles/sales-alpha/detect", new { useAi = true })).StatusCode);
        var detect = await (await ana.Post("/api/profiles/sales-alpha/detect", new { useAi = true })).Node();
        var filed = detect["suggestions"]!.AsArray();
        Assert.Equal(["$.account.legalName", "$.account.phone", "$.account.mcc"], filed.Select(s => s!["content"]!["path"].Text()));
        Assert.All(filed, s => Assert.True(s!["content"]!["confidencePercent"]!.GetValue<int>() <= 70));
        Assert.All(filed, s => Assert.Equal("pending", s!["status"].Text()));
        var ids = filed.Select(s => s!["id"]!.GetValue<long>()).ToList();
        Assert.Equal(3, (await (await ben.GetAsync("/api/suggestions?status=pending&profile=sales-alpha")).Node()).AsArray().Count);

        var approved = await ben.Post($"/api/suggestions/{ids[0]}/approve", new { comment = "Yes, the registered name." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await approved.Node();
        var playbook = body["playbookId"].Text();
        Assert.StartsWith(playbook + "@", filed[0]!["content"]!["domainPlaybook"].Text());
        Assert.Equal(("1.1.0", "approved", "ben", $"{playbook}@1.1.0"),
            (body["version"].Text(), body["suggestion"]!["status"].Text(), body["suggestion"]!["decidedBy"].Text(), body["suggestion"]!["playbook"].Text()));
        var draft = PlaybookSerializer.Deserialize(await ben.GetStringAsync($"/api/playbooks/{playbook}/1.1.0"));
        Assert.Equal(PlaybookStatus.Draft, draft.Status);
        var term = Assert.Single(draft.Domain!.Vocabulary, v => v.Term == "legal name");
        Assert.Null(term.AppliesTo);
        Assert.Contains("approved by ben", term.Note);
        var draftYaml = await ben.GetStringAsync($"/api/playbooks/{playbook}/1.1.0?format=yaml");
        Assert.StartsWith("# Domain playbook:", draftYaml, StringComparison.Ordinal);
        Assert.Contains("- term: legal name\n", draftYaml, StringComparison.Ordinal);
        Assert.Equal(PlaybookStatus.Published, PlaybookSerializer.Deserialize(await ben.GetStringAsync($"/api/playbooks/{playbook}/1.0.0")).Status);

        Assert.Equal(HttpStatusCode.Conflict, (await ben.Post($"/api/suggestions/{ids[0]}/reject", new { })).StatusCode);
        var duplicate = await ben.Post($"/api/suggestions/{ids[1]}/approve", new { });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already a term", (await duplicate.Node())["detail"].Text());

        var newConcept = await ben.Post($"/api/suggestions/{ids[2]}/approve", new { });
        Assert.Contains("new concept 'Merchant.Mcc'", (await newConcept.Node())["detail"].Text());
        Assert.Equal(HttpStatusCode.BadRequest, (await ben.Post($"/api/suggestions/{ids[2]}/approve", new { concept = "LegalEntity.Mcc" })).StatusCode);
        Assert.Contains("send the concept as Concept.Attribute", (await (await ben.Post($"/api/suggestions/{ids[2]}/approve", new { concept = "LegalEntity" })).Node())["detail"].Text());
        var filedUnder = await (await ben.Post($"/api/suggestions/{ids[2]}/approve", new { concept = "legalentity.taxidtype" })).Node();
        Assert.Equal(("domain/tax-id", "1.1.0"), (filedUnder["playbookId"].Text(), filedUnder["version"].Text()));
        draft = PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/tax-id/1.1.0"));
        Assert.Equal("TaxIdType", Assert.Single(draft.Domain!.Vocabulary, v => v.Term == "mcc").AppliesTo);

        var rejected = await (await ben.Post($"/api/suggestions/{ids[1]}/reject", new { comment = "Already covered." })).Node();
        Assert.Equal(("rejected", "Already covered."), (rejected["status"].Text(), rejected["comment"].Text()));
        Assert.Empty((await (await ben.GetAsync("/api/suggestions?status=pending")).Node()).AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await ben.GetAsync("/api/suggestions?status=maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ben.GetAsync("/api/suggestions/999")).StatusCode);
    }

    [Fact]
    public async Task Ai_pairs_unmapped_targets_for_review_only()
    {
        using var api = new ApiFactory(Fake());
        var client = await WithProfiles(api);

        var created = await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", useAi = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = MappingSpecSerializer.Deserialize(await created.Content.ReadAsStringAsync());
        var row = document.Mappings.Single(m => m.Target.Path == "/UnderwritingRequest/Merchant/EstablishedDate");
        Assert.Equal(("$.account.incorporationDate", 70, ReviewStatus.NeedsReview), (row.Sources.Single().Path, row.ConfidencePercent, row.Review.Status));
        Assert.Contains(row.Evidence, e => e.Kind == EvidenceKind.AiSuggestion);
    }

    [Fact]
    public async Task A_failing_provider_saves_nothing()
    {
        using var api = new ApiFactory(FakeProvider.Failing("fake"));
        var client = await WithProfiles(api);

        var failed = await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", useAi = true });
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("fake: down", (await failed.Node())["detail"].Text());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/mappings/sales-alpha__uw-core")).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await client.Post("/api/profiles/sales-alpha/detect", new { useAi = true })).StatusCode);
        Assert.Empty((await (await client.GetAsync("/api/suggestions")).Node()).AsArray());
    }
}
