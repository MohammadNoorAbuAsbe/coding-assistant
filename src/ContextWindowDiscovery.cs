using System.Net.Http.Json;
using System.Text.Json;

namespace TerminalAiAssistant;

/// <summary>
/// Queries provider APIs for the real context window size of a model.
/// Ollama exposes it via POST /api/show; OpenRouter via GET /models/{id};
/// Gemini via the native GET /v1beta/models/{id} (inputTokenLimit).
/// OpenAI's endpoint does not expose it, so that provider falls back to
/// the static catalog in ModelCatalog.
/// All failures are swallowed and return null so discovery is never fatal.
/// </summary>
public static class ContextWindowDiscovery
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<int?> FetchAsync(string provider, string baseUrl, string model, string? apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            return provider.ToLowerInvariant() switch
            {
                "ollama" => await FetchOllamaAsync(baseUrl, model, cancellationToken),
                "openrouter" => await FetchOpenRouterAsync(baseUrl, model, cancellationToken),
                "gemini" => await FetchGeminiAsync(baseUrl, model, apiKey, cancellationToken),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> FetchGeminiAsync(string baseUrl, string model, string? apiKey, CancellationToken cancellationToken)
    {
        var apiRoot = baseUrl.TrimEnd('/');
        if (apiRoot.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            apiRoot = apiRoot[..^6];

        var endpoint = apiRoot + "/models/" + Uri.EscapeDataString(model);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            endpoint += "?key=" + Uri.EscapeDataString(apiKey);
            request.RequestUri = new Uri(endpoint);
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseGeminiModelResponse(json);
    }

    /// <summary>
    /// Parses a Gemini /v1beta/models/{id} response:
    /// { "name": "models/gemini-3.6-flash", "inputTokenLimit": 1048576, ... }
    /// </summary>
    internal static int? ParseGeminiModelResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("inputTokenLimit", out var limit) &&
                TryParseTokenCount(limit, out var count))
            {
                return count;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static async Task<int?> FetchOllamaAsync(string baseUrl, string model, CancellationToken cancellationToken)
    {
        var apiRoot = baseUrl.TrimEnd('/');
        if (apiRoot.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            apiRoot = apiRoot[..^3];

        using var response = await Http.PostAsJsonAsync(apiRoot + "/api/show", new { model }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOllamaShowResponse(json);
    }

    private static async Task<int?> FetchOpenRouterAsync(string baseUrl, string model, CancellationToken cancellationToken)
    {
        var endpoint = baseUrl.TrimEnd('/') + "/models/" + model;
        using var response = await Http.GetAsync(endpoint, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var single = ParseOpenRouterModelResponse(json);
            if (single.HasValue) return single;
        }

        using var listResponse = await Http.GetAsync(baseUrl.TrimEnd('/') + "/models", cancellationToken);
        if (!listResponse.IsSuccessStatusCode) return null;
        var listJson = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenRouterModelsResponse(listJson, model);
    }

    /// <summary>
    /// Parses an Ollama /api/show response. Prefers the configured
    /// num_ctx parameter (the effective runtime window) and otherwise
    /// reads the architecture-specific context_length from model_info
    /// (e.g. "llama.context_length", "qwen3.context_length").
    /// </summary>
    internal static int? ParseOllamaShowResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("parameters", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("num_ctx", out var numCtx) &&
                TryParseTokenCount(numCtx, out var configured))
            {
                return configured;
            }

            if (root.TryGetProperty("model_info", out var modelInfo) &&
                modelInfo.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in modelInfo.EnumerateObject())
                {
                    if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) &&
                        TryParseTokenCount(property.Value, out var count))
                    {
                        return count;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static int? ParseOpenRouterModelResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("context_length", out var contextLength) &&
                TryParseTokenCount(contextLength, out var count))
            {
                return count;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static int? ParseOpenRouterModelsResponse(string json, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!id.GetString()!.Equals(model, StringComparison.OrdinalIgnoreCase)) continue;

                if (entry.TryGetProperty("context_length", out var contextLength) &&
                    TryParseTokenCount(contextLength, out var count))
                {
                    return count;
                }
                return null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool TryParseTokenCount(JsonElement element, out int count)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                count = element.GetInt32();
                return count > 0;
            case JsonValueKind.String:
                if (int.TryParse(element.GetString(), out count) && count > 0) return true;
                break;
        }
        count = 0;
        return false;
    }
}
