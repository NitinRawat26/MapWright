using System.Net;
using System.Security.Cryptography;
using System.Text;
using MapWright.Api;

namespace MapWright.Tests;

public sealed class SettingsApiTests
{
    private const string Key = "mw_settings_key_0123456789";
    private const string ProxySecret = "proxy-secret-0123456789";

    [Fact]
    public async Task Settings_show_the_defaults_of_a_local_api()
    {
        using var api = new ApiFactory();
        var settings = await (await api.CreateClient().GetAsync("/api/settings")).Node();

        Assert.False(settings["ai"]!["available"]!.GetValue<bool>());
        Assert.Equal(70, settings["ai"]!["maxConfidence"]!.GetValue<int>());
        Assert.Equal(180, settings["ai"]!["timeoutSeconds"]!.GetValue<int>());
        Assert.Null(settings["ai"]!["ollama"]);
        Assert.Null(settings["ai"]!["vertex"]);
        Assert.False(settings["signIn"]!["required"]!.GetValue<bool>());
        Assert.Empty(settings["signIn"]!["apiKeys"]!.AsArray());
        Assert.True(settings["storage"]!["inMemory"]!.GetValue<bool>());
        Assert.True(settings["storage"]!["requireIndependentReview"]!.GetValue<bool>());
        Assert.Equal(85, settings["confidence"]!["highThreshold"]!.GetValue<int>());
        Assert.Equal(60, settings["confidence"]!["mediumThreshold"]!.GetValue<int>());
        var playbooks = settings["playbooks"]!;
        Assert.True(playbooks["domain"]!.GetValue<int>() > 0);
        Assert.Equal(playbooks["published"]!.GetValue<int>(), playbooks["domain"]!.GetValue<int>() + playbooks["process"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(settings["version"].Text()));
    }

    [Fact]
    public async Task Settings_name_the_configured_methods_but_never_their_secrets()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key)));
        using var api = new ApiFactory(settings: new Dictionary<string, string>
        {
            ["MapWright:Auth:ApiKeys:0:Name"] = "ci-bot",
            ["MapWright:Auth:ApiKeys:0:Sha256"] = hash,
            ["MapWright:Auth:Proxy:UserHeader"] = "X-Forwarded-Email",
            ["MapWright:Auth:Proxy:Secret"] = ProxySecret,
            ["MAPWRIGHT_OLLAMA_URL"] = "http://ana:hunter2@ollama.internal:11434",
            ["MAPWRIGHT_OLLAMA_CONTEXT_TOKENS"] = "8192",
            ["MAPWRIGHT_VERTEX_PROJECT"] = "corp-ai",
            ["MAPWRIGHT_AI_TIMEOUT_SECONDS"] = "600",
        });
        var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);

        client.DefaultRequestHeaders.Add(SignIn.ApiKeyHeader, Key);
        var response = await client.GetAsync("/api/settings");
        var body = await response.Content.ReadAsStringAsync();
        var settings = await response.Node();

        Assert.True(settings["signIn"]!["required"]!.GetValue<bool>());
        Assert.Equal("ci-bot", Assert.Single(settings["signIn"]!["apiKeys"]!.AsArray()).Text());
        Assert.True(settings["signIn"]!["proxy"]!.GetValue<bool>());
        Assert.Equal("X-Forwarded-Email", settings["signIn"]!["proxyUserHeader"].Text());
        Assert.Equal("http://ollama.internal:11434/", settings["ai"]!["ollama"]!["url"].Text());
        Assert.Equal("qwen3", settings["ai"]!["ollama"]!["model"].Text());
        Assert.Equal(8192, settings["ai"]!["ollama"]!["contextTokens"]!.GetValue<int>());
        Assert.Equal("corp-ai", settings["ai"]!["vertex"]!["project"].Text());
        Assert.Equal("global", settings["ai"]!["vertex"]!["location"].Text());
        Assert.Equal(600, settings["ai"]!["timeoutSeconds"]!.GetValue<int>());
        Assert.DoesNotContain(hash, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProxySecret, body);
        Assert.DoesNotContain("hunter2", body);
    }
}
