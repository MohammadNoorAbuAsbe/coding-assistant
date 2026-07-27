using System.Text.Json;

namespace TerminalAiAssistant;

public static class Configuration
{
    private const string OllamaName = "ollama";

    private static string? _provider;
    private static string? _model;
    private static string? _apiKey;
    private static string? _baseUrl;

    internal static Dictionary<string, ProviderConfig> Providers { get; private set; } = new();

    public static void LoadEnvFile()
    {
        var dir = Environment.CurrentDirectory;
        string? envPath = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate))
            {
                envPath = candidate;
                break;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent == null) break;
            dir = parent;
        }

        if (envPath == null) return;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public static void LoadProviderConfigs()
    {
        var builtIn = new Dictionary<string, ProviderConfig>
        {
            [OllamaName] = new() { Id = OllamaName, DisplayName = "Ollama (Local)", BaseUrl = "http://localhost:11434/v1", DefaultModel = "qwen3:8b" },
            ["openrouter"] = new() { Id = "openrouter", DisplayName = "OpenRouter (Cloud)", BaseUrl = "https://openrouter.ai/api/v1", DefaultModel = "openrouter/free", NeedsApiKey = true, ApiKeyEnvVar = "OPENROUTER_API_KEY", SiteUrlEnvVar = "OPENROUTER_SITE_URL", SiteNameEnvVar = "OPENROUTER_SITE_NAME" },
            ["openai"] = new() { Id = "openai", DisplayName = "OpenAI (Cloud)", BaseUrl = "https://api.openai.com/v1", DefaultModel = "gpt-4o", NeedsApiKey = true, ApiKeyEnvVar = "OPENAI_API_KEY" },
        };

        var configPath = FindConfigJson();
        if (configPath != null)
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var configFile = JsonSerializer.Deserialize<ConfigFile>(json, options);
                if (configFile?.Providers != null)
                {
                    foreach (var (key, provider) in configFile.Providers)
                    {
                        provider.Id = key;
                        builtIn[key] = provider;
                    }
                }
            }
            catch { }
        }

        Providers = builtIn;

        var envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");
        _provider = envProvider != null && Providers.ContainsKey(envProvider)
            ? envProvider
            : OllamaName;
        _model = Providers[_provider].DefaultModel;
    }

    private static string? FindConfigJson()
    {
        var dir = Environment.CurrentDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "config.json");
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent == null) break;
            dir = parent;
        }
        return null;
    }

    public static void SetProvider(string providerId)
    {
        _provider = providerId;
        _model = Providers[providerId].DefaultModel;
        _apiKey = null;
        _baseUrl = null;
    }

    public static void SetModel(string model) => _model = model;

    public static string GetProvider() => _provider ?? OllamaName;

    public static string GetApiKey()
    {
        var config = Providers[GetProvider()];
        if (!config.NeedsApiKey) return OllamaName;
        if (_apiKey != null) return _apiKey;

        _apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvVar!)
            ?? throw new Exception($"{config.ApiKeyEnvVar} is not set. Set it in a .env file (recommended) or as an environment variable.");
        return _apiKey;
    }

    public static string GetBaseUrl()
    {
        if (_baseUrl != null) return _baseUrl;
        var config = Providers[GetProvider()];
        var envVar = $"{GetProvider().ToUpper()}_BASE_URL";
        _baseUrl = Environment.GetEnvironmentVariable(envVar) ?? config.BaseUrl;
        return _baseUrl;
    }

    public static string GetModel() => _model ?? Providers[GetProvider()].DefaultModel;

    public static string? GetSiteUrl()
    {
        var config = Providers[GetProvider()];
        return config.SiteUrlEnvVar != null
            ? Environment.GetEnvironmentVariable(config.SiteUrlEnvVar)
            : null;
    }

    public static string? GetSiteName()
    {
        var config = Providers[GetProvider()];
        return config.SiteNameEnvVar != null
            ? Environment.GetEnvironmentVariable(config.SiteNameEnvVar)
            : null;
    }

    public static int GetMaxIterations()
    {
        var value = Environment.GetEnvironmentVariable("MAX_ITERATIONS");
        return int.TryParse(value, out var result) ? result : 20;
    }

    public static int GetContextWindowSize()
    {
        var value = Environment.GetEnvironmentVariable("CONTEXT_WINDOW_SIZE");
        if (int.TryParse(value, out var result)) return result;
        return GetProvider() == OllamaName ? 32768 : 128000;
    }

    public static int GetMaxToolResultTokens()
    {
        var value = Environment.GetEnvironmentVariable("MAX_TOOL_RESULT_TOKENS");
        if (int.TryParse(value, out var result)) return result;
        return (int)(GetContextWindowSize() * 0.4);
    }
}

internal class ConfigFile
{
    public string? DefaultProvider { get; set; }
    public Dictionary<string, ProviderConfig>? Providers { get; set; }
}
