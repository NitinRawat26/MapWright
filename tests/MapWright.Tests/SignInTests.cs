using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MapWright.Api;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MapWright.Tests;

public sealed class SignInTests
{
    private const string Key = "mw_test_key_0123456789";
    private const string SigningKey = "a-shared-hmac-secret-of-at-least-32-bytes";
    private const string Issuer = "https://idp.example.test";
    private const string ProxySecret = "proxy-secret-0123456789";

    private static readonly Dictionary<string, string> AllMethods = new()
    {
        ["MapWright:Auth:ApiKeys:0:Name"] = "ci-bot",
        ["MapWright:Auth:ApiKeys:0:Sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key))),
        ["MapWright:Auth:Jwt:SigningKey"] = SigningKey,
        ["MapWright:Auth:Jwt:Issuer"] = Issuer,
        ["MapWright:Auth:Jwt:Audience"] = "mapwright",
        ["MapWright:Auth:Proxy:UserHeader"] = "X-Forwarded-Email",
        ["MapWright:Auth:Proxy:Secret"] = ProxySecret,
    };

    private static string Token(string audience = "mapwright", DateTime? expires = null, string claim = "preferred_username") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { [claim] = "ana@corp.example" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
        });

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Without_sign_in_configured_the_name_header_is_trusted()
    {
        using var factory = new ApiFactory();
        var me = await (await factory.As("ana").GetAsync(SignIn.MePath)).Node();

        Assert.Equal("ana", me["name"].Text());
        Assert.Equal("header", me["method"].Text());
        Assert.False(me["signInRequired"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/playbooks")).StatusCode);
    }

    [Fact]
    public async Task Blank_key_entries_leave_sign_in_off()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string>
        {
            ["MapWright:Auth:ApiKeys:0:Name"] = "",
            ["MapWright:Auth:ApiKeys:0:Sha256"] = "",
            ["MapWright:Auth:Jwt:Authority"] = "",
        });

        Assert.Equal("header", (await (await factory.As("ana").GetAsync(SignIn.MePath)).Node())["method"].Text());
    }

    [Fact]
    public async Task Api_keys_sign_in_and_the_key_name_is_recorded_whatever_name_is_sent()
    {
        using var factory = new ApiFactory(settings: AllMethods);
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/playbooks");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("X-Api-Key", (await anonymous.Node())["detail"].Text());
        Assert.Contains("ApiKey", anonymous.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var me = await (await client.GetAsync(SignIn.MePath)).Node();
        Assert.True(me["signInRequired"]!.GetValue<bool>());
        Assert.Null(me["name"]);

        var wrong = await Get(client, "/api/playbooks", (SignIn.ApiKeyHeader, "not-the-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("The API key is not valid.", (await wrong.Node())["detail"].Text());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, SignIn.MePath, (SignIn.ApiKeyHeader, "not-the-key"))).StatusCode);

        var signedIn = await (await Get(client, SignIn.MePath, ("Authorization", $"ApiKey {Key}"))).Node();
        Assert.Equal("ci-bot", signedIn["name"].Text());
        Assert.Equal("apiKey", signedIn["method"].Text());

        client.DefaultRequestHeaders.Add(SignIn.ApiKeyHeader, Key);
        client.DefaultRequestHeaders.Add(ApiErrors.UserHeader, "mallory");
        Assert.Equal(HttpStatusCode.Created, (await client.PostJson("/api/playbooks/domain/tax-id/1.0.0/versions", """{"note":"by key"}""")).StatusCode);
        var history = (await (await client.GetAsync("/api/playbooks/domain/tax-id/history")).Node()).AsArray();
        Assert.Equal("ci-bot", history.Last()!["actor"].Text());
        Assert.DoesNotContain(history, e => e!["actor"].Text() == "mallory");
    }

    [Fact]
    public async Task Bearer_tokens_are_verified_and_named_from_their_claims()
    {
        using var factory = new ApiFactory(settings: AllMethods);
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var me = await (await client.GetAsync(SignIn.MePath)).Node();
        Assert.Equal("ana@corp.example", me["name"].Text());
        Assert.Equal("bearer", me["method"].Text());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(claim: "sub"));
        Assert.Equal("ana@corp.example", (await (await client.GetAsync(SignIn.MePath)).Node())["name"].Text());

        foreach (var bad in new[] { Token(audience: "another-app"), Token(expires: DateTime.UtcNow.AddMinutes(-30)), Token() + "x", "not-a-jwt" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bad);
            var rejected = await client.GetAsync("/api/playbooks");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.StartsWith("The bearer token was rejected", (await rejected.Node())["detail"].Text());
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(claim: "tenant"));
        Assert.Contains("none of the claims", (await (await client.GetAsync("/api/playbooks")).Node())["detail"].Text());
    }

    [Fact]
    public async Task Bearer_tokens_are_refused_when_only_api_keys_are_configured()
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string>
        {
            ["MapWright:Auth:ApiKeys:0:Name"] = "ci-bot",
            ["MapWright:Auth:ApiKeys:0:Sha256"] = AllMethods["MapWright:Auth:ApiKeys:0:Sha256"],
        });

        var response = await Get(factory.CreateClient(), "/api/playbooks", ("Authorization", $"Bearer {Token()}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("use an API key", (await response.Node())["detail"].Text());
    }

    [Fact]
    public async Task The_sign_in_proxy_user_is_trusted_only_with_its_secret()
    {
        using var factory = new ApiFactory(settings: AllMethods);
        var client = factory.CreateClient();

        var me = await (await Get(client, SignIn.MePath, ("X-Forwarded-Email", "bo@corp.example"), ("X-MapWright-Proxy-Secret", ProxySecret))).Node();
        Assert.Equal("bo@corp.example", me["name"].Text());
        Assert.Equal("proxy", me["method"].Text());

        var spoofed = await Get(client, "/api/playbooks", ("X-Forwarded-Email", "bo@corp.example"), ("X-MapWright-Proxy-Secret", "guess"));
        Assert.Equal(HttpStatusCode.Unauthorized, spoofed.StatusCode);
        Assert.Contains("only accepted from the sign-in proxy", (await spoofed.Node())["detail"].Text());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, "/api/playbooks", ("X-Forwarded-Email", "bo@corp.example"))).StatusCode);
    }

    [Theory]
    [InlineData("MapWright:Auth:Proxy:UserHeader", "X-Forwarded-Email", "Proxy:Secret")]
    [InlineData("MapWright:Auth:ApiKeys:0:Name", "ci-bot", "ApiKeys:0:Sha256")]
    [InlineData("MapWright:Auth:Jwt:Authority", "https://idp.example.test", "Jwt:Audience")]
    [InlineData("MapWright:Auth:Jwt:SigningKey", "short", "Jwt:SigningKey must be at least 32 bytes")]
    public void Incomplete_sign_in_settings_stop_the_api_from_starting(string key, string value, string message)
    {
        using var factory = new ApiFactory(settings: new Dictionary<string, string> { [key] = value });

        var error = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(error);
        Assert.Contains(message, error.ToString());
    }
}
