using System.Net;

namespace MapWright.Tests;

public sealed class WebUiTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("mapwright-ui-").FullName;

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    [Fact]
    public async Task The_built_ui_is_served_with_deep_links_but_never_instead_of_the_api()
    {
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<app-root></app-root>");
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "main.js"), "console.log('ui');");
        using var api = new ApiFactory(webRoot: _webRoot);
        var client = api.CreateClient();

        foreach (var page in new[] { "/", "/playbooks/domain/tax-id/1.0.0", "/mappings/sales-alpha__uw-core" })
        {
            var response = await client.GetAsync(page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("<app-root></app-root>", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("console.log('ui');", await client.GetStringAsync("/main.js"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nothing-here")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/v2/swagger.json")).StatusCode);
        Assert.Equal("application/json", (await client.GetAsync("/api/playbooks")).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Without_a_built_ui_only_the_api_answers()
    {
        using var api = new ApiFactory(webRoot: _webRoot);
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/playbooks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
