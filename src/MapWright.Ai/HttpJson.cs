using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapWright.Ai;

internal static class HttpJson
{
    public static async Task<JsonNode> PostAsync(
        HttpClient http, string provider, Uri uri, JsonObject body, string? bearerToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new("Bearer", bearerToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException($"{provider}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{provider}: request timed out", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiProviderException($"{provider}: HTTP {(int)response.StatusCode} {Truncate(text)}");
            }

            try
            {
                return JsonNode.Parse(text) ?? throw new AiProviderException($"{provider}: empty response");
            }
            catch (JsonException ex)
            {
                throw new AiProviderException($"{provider}: response is not JSON", ex);
            }
        }
    }

    public static string RequireJson(string provider, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiProviderException($"{provider}: the model returned no content");
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new AiProviderException($"{provider}: the model did not return valid JSON", ex);
        }

        return text;
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
