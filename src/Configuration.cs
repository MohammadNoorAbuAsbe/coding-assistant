namespace TerminalAiAssistant;

public static class Configuration
{
    private static string? _provider;

    public static string GetProvider()
    {
        if (_provider != null) return _provider;
        _provider = (Environment.GetEnvironmentVariable("AI_PROVIDER") ?? "openrouter").ToLower();
        return _provider;
    }

    public static string GetApiKey()
    {
        if (GetProvider() == "ollama") return "ollama";
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        return string.IsNullOrEmpty(apiKey)
            ? throw new Exception("OPENROUTER_API_KEY is not set. Set AI_PROVIDER=ollama for local models.")
            : apiKey;
    }

    public static string GetBaseUrl()
    {
        if (GetProvider() == "ollama")
        {
            return Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434/v1";
        }
        return Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1";
    }

    public static string GetModel()
    {
        if (GetProvider() == "ollama")
        {
            return Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3:8b";
        }
        return Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "anthropic/claude-haiku-4.5";
    }
}
