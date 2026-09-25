using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MapWright.Api;
using MapWright.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MapWright.Tests;

/// <summary>The API over a fresh in-memory store seeded with the starter playbooks.</summary>
internal sealed class ApiFactory : WebApplicationFactory<ApiOptions>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MapWright:DatabasePath", ":memory:");
        builder.UseSetting("MapWright:SeedPlaybooks", StarterPlaybooks.Directory);
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

    public static string Text(this JsonNode? node) => node?.GetValue<string>() ?? "";

    public static JsonSerializerOptions Options => MapWrightJson.Options;
}
