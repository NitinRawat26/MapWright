using System.Text.Json.Nodes;

namespace MapWright.Ai;

/// <summary>A request for a JSON answer that matches <see cref="ResponseSchema"/> (JSON Schema, lower-case types).</summary>
public sealed record AiPrompt(string Instructions, string Input, JsonObject ResponseSchema);

public sealed record AiReply(string Provider, string Model, string Json);

public interface IAiProvider
{
    string Name { get; }

    Task<AiReply> GenerateJsonAsync(AiPrompt prompt, CancellationToken cancellationToken = default);
}

public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);
