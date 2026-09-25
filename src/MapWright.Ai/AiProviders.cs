namespace MapWright.Ai;

/// <summary>Builds the configured providers: Vertex AI first, then Ollama. Returns null when neither is configured.</summary>
public static class AiProviders
{
    public const string VertexProjectVariable = "MAPWRIGHT_VERTEX_PROJECT";
    public const string VertexLocationVariable = "MAPWRIGHT_VERTEX_LOCATION";
    public const string VertexModelVariable = "MAPWRIGHT_VERTEX_MODEL";
    public const string OllamaUrlVariable = "MAPWRIGHT_OLLAMA_URL";
    public const string OllamaModelVariable = "MAPWRIGHT_OLLAMA_MODEL";
    public const string TimeoutVariable = "MAPWRIGHT_AI_TIMEOUT_SECONDS";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    public static TimeSpan Timeout(Func<string, string?> environment) =>
        int.TryParse(environment(TimeoutVariable), out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultTimeout;

    public static IAiProvider? FromEnvironment(Func<string, string?> environment, HttpClient http)
    {
        var providers = new List<IAiProvider>();

        if (Value(environment, VertexProjectVariable) is { } project)
        {
            providers.Add(VertexAiProvider.WithApplicationDefaultCredentials(http, new()
            {
                Project = project,
                Location = Value(environment, VertexLocationVariable) ?? VertexAiOptions.DefaultLocation,
                Model = Value(environment, VertexModelVariable) ?? VertexAiOptions.DefaultModel,
            }));
        }

        if (Value(environment, OllamaUrlVariable) is { } url)
        {
            if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var baseUrl)
                || baseUrl.Scheme is not ("http" or "https"))
            {
                throw new AiProviderException($"{OllamaUrlVariable} must be an http(s) URL.");
            }

            providers.Add(new OllamaProvider(http, new() { BaseUrl = baseUrl, Model = Value(environment, OllamaModelVariable) ?? OllamaOptions.DefaultModel }));
        }

        return providers.Count switch
        {
            0 => null,
            1 => providers[0],
            _ => new FallbackAiProvider(providers),
        };
    }

    private static string? Value(Func<string, string?> environment, string name) =>
        environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
