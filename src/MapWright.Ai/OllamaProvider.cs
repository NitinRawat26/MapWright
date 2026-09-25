using System.Text.Json.Nodes;

namespace MapWright.Ai;

public sealed record OllamaOptions
{
    public const string DefaultModel = "qwen3";

    public required Uri BaseUrl { get; init; }
    public string Model { get; init; } = DefaultModel;
}

/// <summary>A self-hosted Ollama server (<c>/api/chat</c>) with structured output.</summary>
public sealed class OllamaProvider(HttpClient http, OllamaOptions options) : IAiProvider
{
    public string Name => "ollama";

    public OllamaOptions Options => options;

    public async Task<AiReply> GenerateJsonAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = false,
            ["format"] = prompt.ResponseSchema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt.Instructions },
                new JsonObject { ["role"] = "user", ["content"] = prompt.Input }),
        };

        var response = await HttpJson.PostAsync(http, Name, new Uri(options.BaseUrl, "api/chat"), body, bearerToken: null, cancellationToken)
            .ConfigureAwait(false);

        return new(Name, options.Model, HttpJson.RequireJson(Name, response["message"]?["content"]?.GetValue<string>()));
    }
}
