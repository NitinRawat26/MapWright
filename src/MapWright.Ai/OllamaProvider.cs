using System.Text.Json.Nodes;

namespace MapWright.Ai;

public sealed record OllamaOptions
{
    public const string DefaultModel = "qwen3";
    public const int DefaultContextTokens = 16384;
    public const int DefaultMaxOutputTokens = 4096;

    public required Uri BaseUrl { get; init; }
    public string Model { get; init; } = DefaultModel;

    /// <summary>
    /// Context window for prompt plus answer (<c>num_ctx</c>). Ollama's own default can be as small as 4096
    /// tokens, which cuts off a mapping prompt without saying so.
    /// </summary>
    public int ContextTokens { get; init; } = DefaultContextTokens;

    /// <summary>Longest answer (<c>num_predict</c>), so a model that keeps repeating itself stops.</summary>
    public int MaxOutputTokens { get; init; } = DefaultMaxOutputTokens;
}

/// <summary>
/// A self-hosted Ollama server (<c>/api/chat</c>) with structured output. Thinking is turned off: a thinking model
/// such as qwen3 otherwise spends the answer budget on reasoning and leaves the JSON answer empty.
/// </summary>
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
            ["think"] = false,
            ["format"] = prompt.ResponseSchema.DeepClone(),
            ["options"] = new JsonObject
            {
                ["temperature"] = 0,
                ["num_ctx"] = options.ContextTokens,
                ["num_predict"] = options.MaxOutputTokens,
            },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt.Instructions },
                new JsonObject { ["role"] = "user", ["content"] = prompt.Input }),
        };

        var response = await HttpJson.PostAsync(http, Name, new Uri(options.BaseUrl, "api/chat"), body, bearerToken: null, cancellationToken)
            .ConfigureAwait(false);

        if (response["done_reason"]?.GetValue<string>() == "length")
        {
            throw new AiProviderException(
                $"{Name}: the answer was cut off after {options.MaxOutputTokens} tokens, or the prompt did not fit in {options.ContextTokens}; " +
                $"raise {AiProviders.OllamaContextVariable} or use a larger model.");
        }

        return new(Name, options.Model, HttpJson.RequireJson(Name, response["message"]?["content"]?.GetValue<string>()));
    }
}
