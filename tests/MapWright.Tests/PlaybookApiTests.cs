using System.Net;
using MapWright.Core.Playbooks;

namespace MapWright.Tests;

public sealed class PlaybookApiTests : IDisposable
{
    private readonly ApiFactory _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Starter_playbooks_are_seeded_and_served_in_the_file_format()
    {
        var client = _api.CreateClient();

        var health = await client.GetAsync("/health");
        var list = await (await client.GetAsync("/api/playbooks?status=published")).Node();
        var taxId = await client.GetAsync("/api/playbooks/domain/tax-id/1.0.0");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(6, list.AsArray().Count);
        Assert.All(list.AsArray(), p => Assert.Equal("published", p!["status"].Text()));
        Assert.Equal(PlaybookSerializer.Serialize(StarterPlaybooks.Get("domain/tax-id")), await taxId.Content.ReadAsStringAsync());
        Assert.Equal("imported", (await (await client.GetAsync("/api/playbooks/domain/tax-id/history")).Node())[0]!["action"].Text());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/playbooks/domain/nope/1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/playbooks/other/tax-id/1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/playbooks?status=live")).StatusCode);
        Assert.Empty((await (await client.GetAsync("/api/playbooks?status=draft")).Node()).AsArray());
    }

    [Fact]
    public async Task A_new_version_is_drafted_edited_reviewed_and_published()
    {
        var ana = _api.As("ana");
        var ben = _api.As("ben");

        var drafted = await ana.Post("/api/playbooks/domain/tax-id/1.0.0/versions", new { note = "Add TIN term." });
        Assert.Equal(HttpStatusCode.Created, drafted.StatusCode);
        Assert.Equal("/api/playbooks/domain/tax-id/1.1.0", drafted.Headers.Location?.OriginalString);
        var draft = PlaybookSerializer.Deserialize(await drafted.Content.ReadAsStringAsync());
        Assert.Equal(("1.1.0", PlaybookStatus.Draft), (draft.Version, draft.Status));

        var edited = await ana.PutJson("/api/playbooks/domain/tax-id/1.1.0", PlaybookSerializer.Serialize(draft with { Description = "Tax IDs incl. TIN." }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var validate = await (await ana.GetAsync("/api/playbooks/domain/tax-id/1.1.0/validate")).Node();
        var test = await (await ana.GetAsync("/api/playbooks/domain/tax-id/1.1.0/test")).Node();
        Assert.True(validate["valid"]!.GetValue<bool>());
        Assert.True(test["passed"]!.GetValue<bool>());
        Assert.NotEmpty(test["results"]!.AsArray());

        Assert.Equal(HttpStatusCode.OK, (await ana.Post("/api/playbooks/domain/tax-id/1.1.0/status", new { status = "inReview" })).StatusCode);
        var selfPublish = await ana.Post("/api/playbooks/domain/tax-id/1.1.0/status", new { status = "published" });
        Assert.Equal(HttpStatusCode.Conflict, selfPublish.StatusCode);
        Assert.Contains("another reviewer", (await selfPublish.Node())["detail"].Text());

        var published = await ben.Post("/api/playbooks/domain/tax-id/1.1.0/status", new { status = "published", note = "OK" });
        Assert.Equal("published", (await published.Node())["status"].Text());

        var versions = await (await ben.GetAsync("/api/playbooks/domain/tax-id")).Node();
        Assert.Equal(["1.0.0:retired", "1.1.0:published"], versions.AsArray().Select(v => $"{v!["version"].Text()}:{v["status"].Text()}"));
        Assert.Equal("Tax IDs incl. TIN.", PlaybookSerializer.Deserialize(await ben.GetStringAsync("/api/playbooks/domain/tax-id/1.1.0")).Description);
    }

    [Fact]
    public async Task Writes_need_a_user_and_invalid_requests_return_problems()
    {
        var anonymous = _api.CreateClient();
        var ana = _api.As("ana");
        var starter = StarterPlaybooks.Get("domain/principals");
        var brandNew = starter with { Id = "domain/owners-v2", Status = PlaybookStatus.Draft };

        var noUser = await anonymous.PostJson("/api/playbooks", PlaybookSerializer.Serialize(brandNew));
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        Assert.Contains("X-MapWright-User", (await noUser.Node())["detail"].Text());

        var created = await ana.PostJson("/api/playbooks", PlaybookSerializer.Serialize(brandNew));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ana.PostJson("/api/playbooks", PlaybookSerializer.Serialize(brandNew))).StatusCode);

        var invalid = await ana.PutJson("/api/playbooks/domain/owners-v2/1.0.0", PlaybookSerializer.Serialize(brandNew with { Name = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains((await invalid.Node())["issues"]!.AsArray(), i => i!["code"].Text() == "PB004");

        var malformed = await ana.PostJson("/api/playbooks", "{ \"id\": 1 }");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.StartsWith("Invalid playbook", (await malformed.Node())["detail"].Text());

        var published = await ana.PutJson("/api/playbooks/domain/principals/1.0.0", PlaybookSerializer.Serialize(starter));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        var badTransition = await ana.Post("/api/playbooks/domain/principals/1.0.0/status", new { status = "inReview" });
        Assert.Equal(HttpStatusCode.Conflict, badTransition.StatusCode);
    }

    [Fact]
    public async Task Unsaved_playbooks_can_be_validated_and_tested()
    {
        var client = _api.CreateClient();
        var playbook = StarterPlaybooks.Get("domain/channel-mix");
        var failing = playbook with
        {
            Domain = playbook.Domain! with { Tests = [.. playbook.Domain.Tests.Select((t, i) => i == 0 ? t with { Expect = null } : t)] },
        };

        var valid = await (await client.PostJson("/api/playbooks/validate", PlaybookSerializer.Serialize(playbook))).Node();
        var invalid = await (await client.PostJson("/api/playbooks/validate", PlaybookSerializer.Serialize(playbook with { Version = "one" }))).Node();
        var tests = await (await client.PostJson("/api/playbooks/test", PlaybookSerializer.Serialize(failing))).Node();

        Assert.True(valid["valid"]!.GetValue<bool>());
        Assert.False(invalid["valid"]!.GetValue<bool>());
        Assert.Contains(invalid["issues"]!.AsArray(), i => i!["code"].Text() == "PB003");
        Assert.False(tests["passed"]!.GetValue<bool>());
        Assert.Single(tests["results"]!.AsArray(), r => !r!["passed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Playbooks_are_accepted_and_served_as_yaml_with_their_comments()
    {
        var ana = _api.As("ana");
        var file = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.yaml"));

        var byQuery = await ana.GetAsync("/api/playbooks/domain/tax-id/1.0.0?format=yaml");
        Assert.Equal("application/yaml", byQuery.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file, await byQuery.Content.ReadAsStringAsync());
        using var accept = new HttpRequestMessage(HttpMethod.Get, "/api/playbooks/domain/tax-id/1.0.0");
        accept.Headers.Add("Accept", "application/yaml");
        Assert.Equal(file, await (await ana.SendAsync(accept)).Content.ReadAsStringAsync());
        Assert.StartsWith("{", await ana.GetStringAsync("/api/playbooks/domain/tax-id/1.0.0"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await ana.GetAsync("/api/playbooks/domain/tax-id/1.0.0?format=xml")).StatusCode);

        var yaml = "# Owners, kept separately.\n" + file
            .Replace("\nid: domain/tax-id\n", "\nid: domain/tax-id-v2\n", StringComparison.Ordinal)
            .Replace("\nstatus: published\n", "\nstatus: draft\n", StringComparison.Ordinal);
        var created = await ana.PostAsync("/api/playbooks?format=yaml", new StringContent(yaml, System.Text.Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(yaml, await created.Content.ReadAsStringAsync());

        var validate = await (await ana.PostAsync("/api/playbooks/validate", new StringContent(file, System.Text.Encoding.UTF8, "application/yaml"))).Node();
        Assert.True(validate["valid"]!.GetValue<bool>());

        var edited = yaml.Replace("\nowner: Underwriting BA team\n", "\nowner: Underwriting BA team # changed below\n", StringComparison.Ordinal);
        var put = await ana.PutAsync("/api/playbooks/domain/tax-id-v2/1.0.0", new StringContent(edited, System.Text.Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.StartsWith("{", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var abandoned = await ana.Post("/api/playbooks/domain/tax-id-v2/1.0.0/status?format=yaml", new { status = "retired" });
        Assert.Equal(HttpStatusCode.OK, abandoned.StatusCode);
        var text = await abandoned.Content.ReadAsStringAsync();
        Assert.Contains("# changed below", text, StringComparison.Ordinal);
        Assert.Contains("\nstatus: retired\n", text, StringComparison.Ordinal);

        var malformed = await ana.PostAsync("/api/playbooks", new StringContent("id: [x", System.Text.Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.StartsWith("Invalid YAML at line", (await malformed.Node())["detail"].Text());
    }
}
