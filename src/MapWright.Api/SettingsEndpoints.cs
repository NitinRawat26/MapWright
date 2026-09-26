using System.Reflection;
using MapWright.Ai;
using MapWright.Core.Playbooks;
using MapWright.Core.Spec;
using MapWright.Store;
using Microsoft.Extensions.Options;

namespace MapWright.Api;

/// <summary>A read-only view of how this API is configured. Secrets (key hashes, signing keys, proxy secrets) are never included.</summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (
                IConfiguration configuration,
                IOptions<ApiOptions> api,
                IOptions<AuthOptions> auth,
                AiAccess ai,
                PlaybookStore playbooks) => View(configuration, api.Value, auth.Value, ai, playbooks))
            .WithTags("Settings")
            .WithSummary("How this API is configured: AI provider, sign-in methods, storage and confidence thresholds. Secrets are left out.");
    }

    private static SettingsView View(IConfiguration configuration, ApiOptions api, AuthOptions auth, AiAccess ai, PlaybookStore playbooks)
    {
        string? Value(string name) => configuration[name] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var published = playbooks.List(PlaybookStatus.Published);
        var inMemory = api.DatabasePath == ":memory:";
        var databasePath = inMemory ? api.DatabasePath : Path.GetFullPath(api.DatabasePath);
        var policy = new ConfidencePolicy();

        return new SettingsView(
            Version: typeof(SettingsEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Ai: new AiSettings(
                Available: ai.Provider is not null,
                Provider: ai.Provider?.Name,
                Error: ai.Error,
                MaxConfidence: AiAccess.Cap(playbooks.Library()),
                TimeoutSeconds: (int)AiProviders.Timeout(name => configuration[name]).TotalSeconds,
                Ollama: Value(AiProviders.OllamaUrlVariable) is { } url
                    ? new OllamaSettings(
                        WithoutUserInfo(url),
                        Value(AiProviders.OllamaModelVariable) ?? OllamaOptions.DefaultModel,
                        int.TryParse(Value(AiProviders.OllamaContextVariable), out var tokens) ? tokens : OllamaOptions.DefaultContextTokens)
                    : null,
                Vertex: Value(AiProviders.VertexProjectVariable) is { } project
                    ? new VertexSettings(
                        project,
                        Value(AiProviders.VertexLocationVariable) ?? VertexAiOptions.DefaultLocation,
                        Value(AiProviders.VertexModelVariable) ?? VertexAiOptions.DefaultModel)
                    : null),
            SignIn: new SignInSettings(
                Required: auth.Enabled,
                ApiKeys: [.. auth.Keys.Select(k => k.Name.Trim())],
                Jwt: auth.Jwt.Enabled,
                JwtAuthority: auth.Jwt.Authority,
                JwtAudience: auth.Jwt.Enabled ? auth.Jwt.Audience : null,
                Proxy: auth.Proxy.Enabled,
                ProxyUserHeader: auth.Proxy.UserHeader),
            Storage: new StorageSettings(
                DatabasePath: databasePath,
                InMemory: inMemory,
                SizeBytes: !inMemory && File.Exists(databasePath) ? new FileInfo(databasePath).Length : null,
                SeedPlaybooks: api.SeedPlaybooks,
                RequireIndependentReview: api.RequireIndependentReview,
                Host: Value("RENDER") is not null ? "render" : null),
            Confidence: new ConfidenceSettings(policy.HighThreshold, policy.MediumThreshold),
            Playbooks: new PlaybookSettings(
                Published: published.Count,
                Domain: published.Count(p => p.Kind == PlaybookKind.Domain),
                Process: published.Count(p => p.Kind == PlaybookKind.Process)));
    }

    /// <summary>Drops any user:password from the URL, so it is safe to show.</summary>
    private static string WithoutUserInfo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : url;
}

public sealed record SettingsView(
    string Version,
    AiSettings Ai,
    SignInSettings SignIn,
    StorageSettings Storage,
    ConfidenceSettings Confidence,
    PlaybookSettings Playbooks);

public sealed record AiSettings(
    bool Available,
    string? Provider,
    string? Error,
    int MaxConfidence,
    int TimeoutSeconds,
    OllamaSettings? Ollama,
    VertexSettings? Vertex);

public sealed record OllamaSettings(string Url, string Model, int ContextTokens);

public sealed record VertexSettings(string Project, string Location, string Model);

public sealed record SignInSettings(
    bool Required,
    IReadOnlyList<string> ApiKeys,
    bool Jwt,
    string? JwtAuthority,
    string? JwtAudience,
    bool Proxy,
    string? ProxyUserHeader);

public sealed record StorageSettings(
    string DatabasePath,
    bool InMemory,
    long? SizeBytes,
    string? SeedPlaybooks,
    bool RequireIndependentReview,
    string? Host);

public sealed record ConfidenceSettings(int HighThreshold, int MediumThreshold);

public sealed record PlaybookSettings(int Published, int Domain, int Process);
