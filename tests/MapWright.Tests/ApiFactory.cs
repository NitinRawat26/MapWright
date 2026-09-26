using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MapWright.Ai;
using MapWright.Api;
using MapWright.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MapWright.Tests;

/// <summary>The API over a fresh in-memory store seeded with the starter playbooks.</summary>
internal sealed class ApiFactory(IAiProvider? ai = null, string? webRoot = null, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<ApiOptions>
{
    private static readonly string[] AiVariables =
    [
        AiProviders.VertexProjectVariable, AiProviders.VertexLocationVariable, AiProviders.VertexModelVariable,
        AiProviders.OllamaUrlVariable, AiProviders.OllamaModelVariable, AiProviders.OllamaContextVariable, AiProviders.TimeoutVariable,
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (webRoot is not null)
        {
            builder.UseWebRoot(webRoot);
        }

        builder.UseSetting("MapWright:DatabasePath", ":memory:");
        builder.UseSetting("MapWright:SeedPlaybooks", StarterPlaybooks.Directory);
        foreach (var variable in AiVariables)
        {
            builder.UseSetting(variable, "");
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton(new AiAccess(ai)));
    }

    public HttpClient As(string user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiErrors.UserHeader, user);
        return client;
    }
}

internal static class ApiJson
{
    public static async Task<JsonNode> Node(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public static Task<HttpResponseMessage> PostJson(this HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> PutJson(this HttpClient client, string url, string json) =>
        client.PutAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> Post(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, MapWrightJson.Options);

    public static Task<HttpResponseMessage> Upload(this HttpClient client, string url, IEnumerable<string> files, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            form.Add(new ByteArrayContent(File.ReadAllBytes(file)), "files", Path.GetFileName(file));
        }

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        return client.PostAsync(url, form);
    }

    public static string Text(this JsonNode? node) => node?.GetValue<string>() ?? "";

    public static JsonSerializerOptions Options => MapWrightJson.Options;
}
