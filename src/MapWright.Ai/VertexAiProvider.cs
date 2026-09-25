using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;

namespace MapWright.Ai;

public sealed record VertexAiOptions
{
    public const string DefaultLocation = "global";
    public const string DefaultModel = "gemini-3.5-flash";

    public required string Project { get; init; }
    public string Location { get; init; } = DefaultLocation;
    public string Model { get; init; } = DefaultModel;

    public Uri Endpoint => new(
        $"https://{(Location == "global" ? "" : Location + "-")}aiplatform.googleapis.com/v1/projects/{Uri.EscapeDataString(Project)}" +
        $"/locations/{Uri.EscapeDataString(Location)}/publishers/google/models/{Uri.EscapeDataString(Model)}:generateContent");
}

/// <summary>Gemini on Vertex AI (<c>generateContent</c>) with a response schema.</summary>
public sealed class VertexAiProvider(HttpClient http, VertexAiOptions options, Func<CancellationToken, Task<string>> accessToken) : IAiProvider
{
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    public string Name => "vertex-ai";

    public VertexAiOptions Options => options;

    /// <summary>Authenticates with Application Default Credentials, e.g. a service-account key file named by GOOGLE_APPLICATION_CREDENTIALS.</summary>
    public static VertexAiProvider WithApplicationDefaultCredentials(HttpClient http, VertexAiOptions options)
    {
        var credential = new Lazy<Task<ITokenAccess>>(async () =>
            (await GoogleCredential.GetApplicationDefaultAsync().ConfigureAwait(false)).CreateScoped(Scope));

        return new(http, options, async ct =>
        {
            try
            {
                var token = await credential.Value.ConfigureAwait(false);
                return await token.GetAccessTokenForRequestAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TokenResponseException)
            {
                throw new AiProviderException($"vertex-ai: no usable Google credentials: {ex.Message.TrimEnd('.')}", ex);
            }
        });
    }

    public async Task<AiReply> GenerateJsonAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.Instructions }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.Input }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = ToOpenApiSchema(prompt.ResponseSchema),
            },
        };

        var token = await accessToken(cancellationToken).ConfigureAwait(false);
        var response = await HttpJson.PostAsync(http, Name, options.Endpoint, body, token, cancellationToken).ConfigureAwait(false);

        var parts = response["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var text = parts is null
            ? null
            : string.Concat(parts.Where(p => p?["thought"]?.GetValue<bool>() != true).Select(p => p?["text"]?.GetValue<string>()));
        if (text is null && response["promptFeedback"]?["blockReason"] is { } reason)
        {
            throw new AiProviderException($"{Name}: prompt blocked ({reason})");
        }

        return new(Name, options.Model, HttpJson.RequireJson(Name, text));
    }

    /// <summary>Vertex AI schemas use upper-case OpenAPI type names.</summary>
    internal static JsonNode ToOpenApiSchema(JsonNode schema)
    {
        var copy = schema.DeepClone();
        Upper(copy);
        return copy;

        static void Upper(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key == "type" && value is JsonValue v && v.TryGetValue<string>(out var type))
                        {
                            obj[key] = type.ToUpperInvariant();
                        }
                        else
                        {
                            Upper(value);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Upper(item);
                    }

                    break;
            }
        }
    }
}
