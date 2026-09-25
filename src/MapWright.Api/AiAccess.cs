using MapWright.Ai;
using MapWright.Core.Playbooks;
using MapWright.Store;

namespace MapWright.Api;

/// <summary>
/// The configured AI provider (Vertex AI first, then Ollama), or none. AI runs only when a request asks for it
/// with <c>useAi: true</c>; its answers are always capped and left for review.
/// </summary>
public sealed class AiAccess : IDisposable
{
    private readonly HttpClient? _http;
    private readonly string? _error;

    public AiAccess(IAiProvider? provider) => Provider = provider;

    public AiAccess(IConfiguration configuration)
    {
        _http = new HttpClient { Timeout = AiProviders.Timeout(name => configuration[name]) };
        try
        {
            Provider = AiProviders.FromEnvironment(name => configuration[name], _http);
        }
        catch (AiProviderException ex)
        {
            _error = ex.Message;
        }
    }

    public IAiProvider? Provider { get; }

    public IAiProvider Require() => Provider ?? throw new StoreException(
        StoreError.Invalid,
        _error ?? $"No AI provider is configured (set {AiProviders.VertexProjectVariable} or {AiProviders.OllamaUrlVariable}); send useAi: false to use the playbooks only.");

    /// <summary>The cap set by the process playbook's AI step, or the default.</summary>
    public static int Cap(PlaybookLibrary library) => library.Active
        .SelectMany(p => p.Process?.Steps ?? [])
        .FirstOrDefault(s => s.Kind == StepKind.AiAssist)?.MaxConfidence ?? AiFieldAssistant.DefaultMaxConfidence;

    public void Dispose() => _http?.Dispose();
}
