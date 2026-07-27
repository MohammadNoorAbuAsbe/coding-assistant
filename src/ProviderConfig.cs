using System.Text.Json.Serialization;

namespace TerminalAiAssistant;

public class ProviderConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("defaultModel")]
    public string DefaultModel { get; set; } = "";

    [JsonPropertyName("needsApiKey")]
    public bool NeedsApiKey { get; set; }

    [JsonPropertyName("apiKeyEnvVar")]
    public string? ApiKeyEnvVar { get; set; }

    [JsonPropertyName("siteUrlEnvVar")]
    public string? SiteUrlEnvVar { get; set; }

    [JsonPropertyName("siteNameEnvVar")]
    public string? SiteNameEnvVar { get; set; }

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();
}
