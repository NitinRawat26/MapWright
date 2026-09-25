using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MapWright.Ai;
using MapWright.Cli;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

internal sealed class FakeProvider(string name, Func<AiPrompt, string> answer) : IAiProvider
{
    public List<AiPrompt> Prompts { get; } = [];

    public string Name => name;

    public Task<AiReply> GenerateJsonAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(new AiReply(name, "fake-model", answer(prompt)));
    }

    public static FakeProvider Failing(string name) => new(name, _ => throw new AiProviderException($"{name}: down"));
}

internal sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public JsonNode? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal static class AiSamples
{
    public static SystemProfile SalesAlpha() =>
        ProfileSerializer.Load(Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-alpha", "profile.json"));

    public static AiPrompt Prompt() => new("instructions", "{\"fields\":[]}", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string" } },
    });

    public static string Answer(params object[] suggestions) => JsonSerializer.Serialize(new { suggestions });

    public static JsonNode Input(AiPrompt prompt) => JsonNode.Parse(prompt.Input)!;

    public static JsonObject Field(AiPrompt prompt, string path) =>
        Input(prompt)["fields"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == path)!.AsObject();
}

public sealed class AiProviderTests
{
    [Fact]
    public async Task Vertex_posts_generate_content_with_an_upper_case_schema_and_bearer_token()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"thinking","thought":true},{"text":"{\"answer\":"},{"text":"\"ok\"}"}]}}]}
            """);
        var provider = new VertexAiProvider(new HttpClient(handler), new() { Project = "acq-dev", Location = "us-central1", Model = "gemini-x" },
            _ => Task.FromResult("token-1"));

        var reply = await provider.GenerateJsonAsync(AiSamples.Prompt());

        Assert.Equal("{\"answer\":\"ok\"}", reply.Json);
        Assert.Equal(("vertex-ai", "gemini-x"), (reply.Provider, reply.Model));
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/acq-dev/locations/us-central1/publishers/google/models/gemini-x:generateContent",
            handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer token-1", handler.Request.Headers.Authorization!.ToString());
        var body = handler.Body!;
        Assert.Equal("instructions", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("user", body["contents"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("application/json", body["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.Equal(0, body["generationConfig"]!["temperature"]!.GetValue<int>());
        var schema = body["generationConfig"]!["responseSchema"]!;
        Assert.Equal("OBJECT", schema["type"]!.GetValue<string>());
        Assert.Equal("STRING", schema["properties"]!["answer"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Vertex_global_location_uses_the_global_host()
    {
        var options = new VertexAiOptions { Project = "p" };

        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/p/locations/global/publishers/google/models/" + VertexAiOptions.DefaultModel + ":generateContent",
            options.Endpoint.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"denied\"}}", "HTTP 403")]
    [InlineData(HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"not json\"}]}}]}", "valid JSON")]
    [InlineData(HttpStatusCode.OK, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", "blocked")]
    public async Task Vertex_failures_raise_provider_exceptions(HttpStatusCode status, string body, string expected)
    {
        var provider = new VertexAiProvider(new HttpClient(new FakeHandler(status, body)), new() { Project = "p" }, _ => Task.FromResult("t"));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateJsonAsync(AiSamples.Prompt()));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Ollama_posts_a_non_streaming_chat_with_the_json_schema_as_format()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"model":"qwen3","message":{"role":"assistant","content":"{\"answer\":\"ok\"}"},"done":true}
            """);
        var provider = new OllamaProvider(new HttpClient(handler), new() { BaseUrl = new("http://ollama.internal:11434/") });

        var reply = await provider.GenerateJsonAsync(AiSamples.Prompt());

        Assert.Equal("{\"answer\":\"ok\"}", reply.Json);
        Assert.Equal(("ollama", OllamaOptions.DefaultModel), (reply.Provider, reply.Model));
        Assert.Equal("http://ollama.internal:11434/api/chat", handler.Request!.RequestUri!.ToString());
        Assert.Null(handler.Request.Headers.Authorization);
        var body = handler.Body!;
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal("object", body["format"]!["type"]!.GetValue<string>());
        Assert.Equal(["system", "user"], body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Ollama_connection_failure_is_a_provider_exception()
    {
        var provider = new OllamaProvider(new HttpClient(new ThrowingHandler()), new() { BaseUrl = new("http://localhost:1/") });

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateJsonAsync(AiSamples.Prompt()));

        Assert.StartsWith("ollama:", ex.Message);
    }

    [Fact]
    public async Task Fallback_uses_the_next_provider_when_the_first_fails()
    {
        var ollama = new FakeProvider("ollama", _ => "{}");
        var provider = new FallbackAiProvider([FakeProvider.Failing("vertex-ai"), ollama]);

        var reply = await provider.GenerateJsonAsync(AiSamples.Prompt());

        Assert.Equal("ollama", reply.Provider);
        Assert.Equal("vertex-ai → ollama", provider.Name);
        Assert.Single(ollama.Prompts);
    }

    [Fact]
    public async Task Fallback_reports_every_failure_when_all_fail()
    {
        var provider = new FallbackAiProvider([FakeProvider.Failing("vertex-ai"), FakeProvider.Failing("ollama")]);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateJsonAsync(AiSamples.Prompt()));

        Assert.Equal("vertex-ai: down; ollama: down", ex.Message);
    }

    [Fact]
    public void No_configuration_means_no_provider()
    {
        Assert.Null(AiProviders.FromEnvironment(_ => null, new HttpClient()));
        Assert.Null(AiProviders.FromEnvironment(_ => "  ", new HttpClient()));
    }

    [Fact]
    public void Vertex_is_tried_before_ollama()
    {
        var env = new Dictionary<string, string>
        {
            [AiProviders.VertexProjectVariable] = "acq-dev",
            [AiProviders.VertexModelVariable] = "gemini-x",
            [AiProviders.OllamaUrlVariable] = "http://ollama:11434",
            [AiProviders.OllamaModelVariable] = "llama-x",
        };

        var provider = Assert.IsType<FallbackAiProvider>(AiProviders.FromEnvironment(env.GetValueOrDefault, new HttpClient()));

        var vertex = Assert.IsType<VertexAiProvider>(provider.Providers[0]);
        var ollama = Assert.IsType<OllamaProvider>(provider.Providers[1]);
        Assert.Equal(("acq-dev", VertexAiOptions.DefaultLocation, "gemini-x"), (vertex.Options.Project, vertex.Options.Location, vertex.Options.Model));
        Assert.Equal(("http://ollama:11434/", "llama-x"), (ollama.Options.BaseUrl.ToString(), ollama.Options.Model));
    }

    [Fact]
    public void A_single_configured_provider_is_used_directly()
    {
        var provider = AiProviders.FromEnvironment(n => n == AiProviders.OllamaUrlVariable ? "http://localhost:11434/" : null, new HttpClient());

        Assert.IsType<OllamaProvider>(provider);
    }

    [Theory]
    [InlineData("ftp://ollama")]
    [InlineData("not a url")]
    public void Invalid_ollama_url_is_rejected(string url)
    {
        Assert.Throws<AiProviderException>(() =>
            AiProviders.FromEnvironment(n => n == AiProviders.OllamaUrlVariable ? url : null, new HttpClient()));
    }

    [Theory]
    [InlineData(null, 180)]
    [InlineData("30", 30)]
    [InlineData("-5", 180)]
    [InlineData("soon", 180)]
    public void Timeout_comes_from_the_environment(string? value, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), AiProviders.Timeout(n => n == AiProviders.TimeoutVariable ? value : null));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}

public sealed class AiFieldAssistantTests
{
    private static readonly string[] Remaining =
        ["$.account.mcc", "$.account.legalName", "$.account.phone", "$.bank.accountNumber", "$.owners[*].email"];

    [Fact]
    public async Task Prompt_contains_masked_field_details_and_the_playbook_concepts()
    {
        var provider = new FakeProvider("fake", _ => AiSamples.Answer());

        await new AiFieldAssistant(provider).DecodeAsync(AiSamples.SalesAlpha(), Remaining, StarterPlaybooks.Library().Domains);

        var prompt = Assert.Single(provider.Prompts);
        Assert.Equal(Remaining.Order(StringComparer.Ordinal),
            AiSamples.Input(prompt)["fields"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        var mcc = AiSamples.Field(prompt, "$.account.mcc");
        Assert.Equal(["5411", "5814", "7542"], mcc["values"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["account"], mcc["parents"]!.AsArray().Select(v => v!.GetValue<string>()).Take(1));

        var bank = AiSamples.Field(prompt, "$.bank.accountNumber");
        Assert.True(bank["sensitive"]!.GetValue<bool>());
        Assert.Null(bank["values"]);

        Assert.Null(AiSamples.Field(prompt, "$.account.legalName")["values"]);
        Assert.Null(AiSamples.Field(prompt, "$.account.phone")["values"]);
        Assert.Null(AiSamples.Field(prompt, "$.owners[*].email")["values"]);
        foreach (var secret in new[] { "6655", "Northwind", "555-0110", "@northwind", "sampleValue" })
        {
            Assert.DoesNotContain(secret, prompt.Input);
        }

        var concepts = AiSamples.Input(prompt)["concepts"]!.AsArray();
        Assert.Equal(5, concepts.Count);
        Assert.Contains(concepts, c => c!["concept"]!.GetValue<string>() == "Principal" && c["guidance"] is not null);
    }

    [Fact]
    public async Task Suggestions_are_capped_resolved_against_playbooks_and_need_review()
    {
        var provider = new FakeProvider("fake", _ => AiSamples.Answer(
            new { path = "$.owners[*].email", concept = "principal.email", meaning = "Owner e-mail", confidence = 95, reasoning = "Name is email under owners." },
            new { path = "$.account.mcc", concept = "", newConcept = "Merchant.Mcc", meaning = "Merchant category code", confidence = 88, reasoning = "4-digit codes like 5411.", question = "Is this the ISO 18245 MCC?" },
            new { path = "$.account.phone", concept = "Business.Phone", meaning = "Business phone", confidence = -3, reasoning = "" },
            new { path = "$.not.asked", concept = "Principal", meaning = "?", confidence = 50, reasoning = "?" }));

        var result = await new AiFieldAssistant(provider, maxConfidence: 60)
            .DecodeAsync(AiSamples.SalesAlpha(), Remaining, StarterPlaybooks.Library().Domains);

        var email = result.Suggestions.Single(s => s.Path == "$.owners[*].email");
        Assert.Equal(("Principal.Email", "domain/principals@1.0.0", null), (email.BusinessConcept, email.DomainPlaybook, email.ProposedConcept));
        Assert.Equal(60, email.ConfidencePercent);
        Assert.Equal(("fake", "fake-model"), (email.Provider, email.Model));

        var mcc = result.Suggestions.Single(s => s.Path == "$.account.mcc");
        Assert.Equal((null, "Merchant.Mcc", 60), (mcc.BusinessConcept, mcc.ProposedConcept, mcc.ConfidencePercent));
        Assert.Equal("Is this the ISO 18245 MCC?", mcc.Question);

        var phone = result.Suggestions.Single(s => s.Path == "$.account.phone");
        Assert.Equal(("Business.Phone", 0, "No reasoning given."), (phone.ProposedConcept, phone.ConfidencePercent, phone.Reasoning));

        Assert.All(result.Suggestions, s => Assert.Equal(ReviewStatus.NeedsReview, s.Review));
        Assert.Equal(["$.account.legalName", "$.bank.accountNumber"], result.Unresolved.Order(StringComparer.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("$.not.asked"));
    }

    [Fact]
    public async Task Fields_are_sent_in_batches()
    {
        var provider = new FakeProvider("fake", _ => AiSamples.Answer());

        await new AiFieldAssistant(provider, batchSize: 2).DecodeAsync(AiSamples.SalesAlpha(), Remaining, StarterPlaybooks.Library().Domains);

        Assert.Equal([2, 2, 1], provider.Prompts.Select(p => AiSamples.Input(p)["fields"]!.AsArray().Count));
    }

    [Fact]
    public async Task Unreadable_answer_is_a_warning_not_a_crash()
    {
        var provider = new FakeProvider("fake", _ => "{\"suggestions\": \"nope\"}");

        var result = await new AiFieldAssistant(provider).DecodeAsync(AiSamples.SalesAlpha(), Remaining, StarterPlaybooks.Library().Domains);

        Assert.Empty(result.Suggestions);
        Assert.Equal(Remaining.Length, result.Unresolved.Count);
        Assert.Single(result.Warnings);
    }
}

public sealed class DetectAiCliTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private static readonly string Profile = Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-alpha", "profile.json");

    private int Detect(string stdin, IAiProvider? ai, params string[] extra) =>
        CliApp.Run(["playbook", "detect", Profile, "--playbooks", StarterPlaybooks.Directory, .. extra], new StringReader(stdin), _out, _err, ai);

    private static FakeProvider Mcc() => new("fake", _ => AiSamples.Answer(
        new { path = "$.account.mcc", newConcept = "Merchant.Mcc", meaning = "Merchant category code", confidence = 90, reasoning = "4-digit codes." }));

    [Fact]
    public void Asks_before_using_ai_and_runs_it_on_yes()
    {
        var ai = Mcc();

        Assert.Equal(CliApp.Success, Detect("y\n", ai));

        var output = _out.ToString();
        Assert.Contains("Do you want to use AI to decode the remaining", output);
        Assert.Contains("$.account.mcc  new: Merchant.Mcc 70% review (fake/fake-model)", output);
        Assert.Contains("AI suggestion(s)", output);
        var prompt = Assert.Single(ai.Prompts);
        Assert.DoesNotContain(AiSamples.Input(prompt)["fields"]!.AsArray(), f => f!["path"]!.GetValue<string>() == "$.owners");
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")]
    public void Anything_but_yes_skips_ai(string answer)
    {
        var ai = Mcc();

        Assert.Equal(CliApp.Success, Detect(answer, ai));

        Assert.Empty(ai.Prompts);
        Assert.Contains("Skipped AI; continuing with playbooks only.", _out.ToString());
    }

    [Fact]
    public void Ai_yes_skips_the_question_and_ai_no_never_calls()
    {
        var yes = Mcc();
        Assert.Equal(CliApp.Success, Detect("", yes, "--ai", "yes"));
        Assert.Single(yes.Prompts);
        Assert.DoesNotContain("Do you want", _out.ToString());

        var no = Mcc();
        Assert.Equal(CliApp.Success, Detect("y\n", no, "--ai", "no"));
        Assert.Empty(no.Prompts);
    }

    [Fact]
    public void Without_a_provider_detect_uses_playbooks_only()
    {
        Assert.Equal(CliApp.Success, Detect("y\n", ai: null));
        Assert.DoesNotContain("Do you want", _out.ToString());
        Assert.Equal("", _err.ToString());

        Assert.Equal(CliApp.Success, Detect("", ai: null, "--ai", "yes"));
        Assert.Contains("No AI provider is configured", _err.ToString());
    }

    [Fact]
    public void Provider_failure_falls_back_to_playbook_results()
    {
        Assert.Equal(CliApp.Success, Detect("", FakeProvider.Failing("vertex-ai"), "--ai", "yes"));

        Assert.Contains("AI assist failed: vertex-ai: down", _err.ToString());
        Assert.Contains("Continuing with playbooks only.", _err.ToString());
    }

    [Fact]
    public void Out_writes_matches_suggestions_and_remaining_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapwright-{Guid.NewGuid():N}", "decode.json");
        try
        {
            Assert.Equal(CliApp.Success, Detect("", Mcc(), "--ai", "yes", "--out", path));

            var report = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("SalesAlpha CRM", report["system"]!.GetValue<string>());
            Assert.Contains(report["recognised"]!.AsArray(), m => m!["path"]!.GetValue<string>() == "$.owners");
            var suggestion = Assert.Single(report["aiSuggestions"]!.AsArray())!;
            Assert.Equal(("$.account.mcc", "needsReview"), (suggestion["path"]!.GetValue<string>(), suggestion["review"]!.GetValue<string>()));
            var remaining = report["remaining"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
            Assert.DoesNotContain("$.account.mcc", remaining);
            Assert.Contains("$.account.legalName", remaining);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Invalid_ai_mode_is_a_usage_error()
    {
        Assert.Equal(CliApp.UsageError, Detect("", null, "--ai", "maybe"));
    }
}
