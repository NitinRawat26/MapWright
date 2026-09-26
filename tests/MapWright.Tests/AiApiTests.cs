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
        var keep = await ben.DeleteAsync($"/api/playbooks/{playbook}/1.1.0");
        Assert.Equal(HttpStatusCode.Conflict, keep.StatusCode);
        Assert.Contains("approved AI suggestions", (await keep.Node())["detail"].Text());

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
    public async Task Approving_with_create_drafts_new_concepts_and_attributes()
    {
        using var api = new ApiFactory(new FakeProvider("fake", _ => AiSamples.Answer(
            new { path = "$.account.mcc", newConcept = "Merchant.Mcc", meaning = "Merchant category code", confidence = 88, reasoning = "4-digit codes." },
            new { path = "$.account.salesRepId", newConcept = "merchant.sales_rep", meaning = "Sales rep", confidence = 80, reasoning = "Rep ids." },
            new { path = "$.account.leadSource", newConcept = "ChannelMix.LeadSource", meaning = "Where the lead came from", confidence = 80, reasoning = "Web, referral." })));
        var ana = await WithProfiles(api);
        var ben = api.As("ben");
        var filed = (await (await ana.Post("/api/profiles/sales-alpha/detect", new { useAi = true })).Node())["suggestions"]!.AsArray();
        Assert.Equal(["$.account.mcc", "$.account.salesRepId", "$.account.leadSource"], filed.Select(s => s!["content"]!["path"].Text()));
        var ids = filed.Select(s => s!["id"]!.GetValue<long>()).ToList();

        var refused = await ben.Post($"/api/suggestions/{ids[0]}/approve", new { concept = "Merchant.Mcc" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("send create: true", (await refused.Node())["detail"].Text());

        var created = await (await ben.Post($"/api/suggestions/{ids[0]}/approve", new { create = true })).Node();
        var saved = await (await ana.GetAsync("/api/profiles/sales-alpha/detection")).Node();
        Assert.True(saved["usedAi"]!.GetValue<bool>());
        Assert.Equal(["approved", "pending", "pending"], saved["suggestions"]!.AsArray().Select(s => s!["status"].Text()));
        Assert.Equal(("domain/merchant", "0.1.0", true), (created["playbookId"].Text(), created["version"].Text(), created["created"]!.GetValue<bool>()));
        var merchant = PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/merchant/0.1.0"));
        Assert.Equal((PlaybookStatus.Draft, "Merchant", "ben"), (merchant.Status, merchant.Domain!.Concept.Name, merchant.Owner));
        Assert.Equal(("Mcc", "Merchant category code"), (Assert.Single(merchant.Domain.Concept.Attributes).Name, merchant.Domain.Concept.Attributes[0].Description));
        Assert.Empty(merchant.Domain.Vocabulary);
        Assert.Contains("AI suggestion", Assert.Single(merchant.ChangeNotes).Description);

        var added = await (await ben.Post($"/api/suggestions/{ids[1]}/approve", new { create = true })).Node();
        Assert.Equal(("domain/merchant", "0.1.0", false), (added["playbookId"].Text(), added["version"].Text(), added["created"]!.GetValue<bool>()));
        merchant = PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/merchant/0.1.0"));
        Assert.Equal(["Mcc", "SalesRep"], merchant.Domain!.Concept.Attributes.Select(a => a.Name));
        Assert.Equal("SalesRep", Assert.Single(merchant.Domain.Vocabulary).AppliesTo);

        Assert.Equal(HttpStatusCode.BadRequest, (await ben.Post($"/api/suggestions/{ids[2]}/approve", new { })).StatusCode);
        var attribute = await (await ben.Post($"/api/suggestions/{ids[2]}/approve", new { create = true })).Node();
        Assert.Equal(("domain/channel-mix", "1.1.0", false), (attribute["playbookId"].Text(), attribute["version"].Text(), attribute["created"]!.GetValue<bool>()));
        var channelMix = PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/channel-mix/1.1.0"));
        Assert.Contains(channelMix.Domain!.Concept.Attributes, a => a.Name == "LeadSource");
        Assert.DoesNotContain(channelMix.Domain.Vocabulary, v => v.AppliesTo == "LeadSource");
        Assert.DoesNotContain(PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/channel-mix/1.0.0")).Domain!.Concept.Attributes, a => a.Name == "LeadSource");

        Assert.Equal(HttpStatusCode.Conflict, (await ben.DeleteAsync("/api/playbooks/domain/merchant/0.1.0")).StatusCode);
        Assert.Equal(["approved", "approved", "approved"], (await (await ben.GetAsync("/api/suggestions")).Node()).AsArray().Select(s => s!["status"].Text()));
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

        var pass = Assert.IsType<AiPass>(document.AiPass);
        Assert.Equal([row.Id], pass.SuggestedRows);
        Assert.Equal(document.Mappings.Where(m => m.Type == MappingType.Unmapped).Select(m => m.Target.Path), pass.Unmatched);
        Assert.NotEmpty(pass.Unmatched);
        var stored = MappingSpecSerializer.Deserialize(await (await client.GetAsync("/api/mappings/sales-alpha__uw-core")).Content.ReadAsStringAsync()).AiPass!;
        Assert.Equal(pass.Provider, stored.Provider);
        Assert.Equal(pass.SuggestedRows, stored.SuggestedRows);
        Assert.Equal(pass.Unmatched, stored.Unmatched);

        var plain = await client.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", id = "plain" });
        Assert.DoesNotContain("aiPass", await plain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_pairings_are_filed_in_the_inbox_and_review_their_rows()
    {
        using var api = new ApiFactory(Fake());
        var ana = await WithProfiles(api);
        var ben = api.As("ben");
        const string target = "/UnderwritingRequest/Merchant/EstablishedDate";

        async Task<(string Row, long Suggestion)> Generate(bool replace)
        {
            var created = await ana.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", useAi = true, replace });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var row = MappingSpecSerializer.Deserialize(await created.Content.ReadAsStringAsync()).Mappings.Single(m => m.Target.Path == target).Id;
            var pending = (await (await ana.GetAsync("/api/suggestions?status=pending")).Node()).AsArray();
            var filed = Assert.Single(pending)!;
            Assert.Equal(("sales-alpha", "$.account.incorporationDate", "incorporationDate"), (filed["profileId"].Text(), filed["content"]!["path"].Text(), filed["content"]!["fieldName"].Text()));
            var pairing = filed["content"]!["mapping"]!;
            Assert.Equal(("sales-alpha__uw-core", row, target, "UW Core"), (pairing["mappingId"].Text(), pairing["rowId"].Text(), pairing["target"].Text(), pairing["targetSystem"].Text()));
            Assert.Equal(["$.account.incorporationDate"], pairing["sources"]!.AsArray().Select(p => p.Text()));
            return (row, filed["id"]!.GetValue<long>());
        }

        async Task<string> Status(string row) =>
            MappingSpecSerializer.Deserialize(await (await ana.GetAsync("/api/mappings/sales-alpha__uw-core")).Content.ReadAsStringAsync())
                .Mappings.Single(m => m.Id == row).Review.Status.ToString();

        var (first, firstId) = await Generate(false);
        var (row, id) = await Generate(true);
        Assert.NotEqual(firstId, id);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.GetAsync($"/api/suggestions/{firstId}")).StatusCode);
        Assert.Equal(first, row);

        var approved = await (await ben.Post($"/api/suggestions/{id}/approve", new { comment = "Same date." })).Node();
        Assert.Equal(("approved", null, null), (approved["suggestion"]!["status"].Text(), approved["playbookId"]?.ToString(), approved["suggestion"]!["playbook"]?.ToString()));
        Assert.Equal(nameof(ReviewStatus.Approved), await Status(row));
        var decision = Assert.Single((await (await ana.GetAsync("/api/mappings/sales-alpha__uw-core/reviews")).Node()).AsArray())!;
        Assert.Equal(("approve", "ben", "Same date."), (decision["decision"].Text(), decision["reviewer"].Text(), decision["comment"].Text()));

        (row, id) = await Generate(true);
        Assert.Equal("approved", (await (await ana.GetAsync("/api/suggestions?status=approved")).Node()).AsArray().Single()!["status"].Text());
        Assert.Equal(HttpStatusCode.OK, (await ben.Post($"/api/mappings/sales-alpha__uw-core/rows/{row}/review", new { decision = "approve" })).StatusCode);
        Assert.Equal("rejected", (await (await ben.Post($"/api/suggestions/{id}/reject", new { comment = "Checked on the mapping page." })).Node())["status"].Text());
        Assert.Equal(nameof(ReviewStatus.Approved), await Status(row));

        (row, id) = await Generate(true);
        Assert.Equal("rejected", (await (await ben.Post($"/api/suggestions/{id}/reject", new { })).Node())["status"].Text());
        Assert.Equal(nameof(ReviewStatus.Rejected), await Status(row));

        await Generate(true);
        Assert.Equal(HttpStatusCode.Created, (await ana.Post("/api/mappings", new { source = "sales-alpha", target = "uw-core", replace = true })).StatusCode);
        Assert.Empty((await (await ana.GetAsync("/api/suggestions?status=pending")).Node()).AsArray());
        await Generate(true);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.DeleteAsync("/api/mappings/sales-alpha__uw-core")).StatusCode);
        Assert.Empty((await (await ana.GetAsync("/api/suggestions?status=pending")).Node()).AsArray());
        Assert.Equal(3, (await (await ana.GetAsync("/api/suggestions")).Node()).AsArray().Count);

        (row, id) = await Generate(false);
        var taught = await (await ben.Post($"/api/suggestions/{id}/approve", new { concept = "Merchant.EstablishedDate", create = true })).Node();
        Assert.Equal(("domain/merchant", "domain/merchant@0.1.0"), (taught["playbookId"].Text(), taught["suggestion"]!["playbook"].Text()));
        Assert.Equal(nameof(ReviewStatus.Approved), await Status(row));
        var draft = await (await ben.GetAsync("/api/playbooks/domain/merchant/0.1.0")).Node();
        Assert.Contains(draft["domain"]!["vocabulary"]!.AsArray(), v => v!["term"].Text() == "incorporation date" && v["appliesTo"].Text() == "EstablishedDate");
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
