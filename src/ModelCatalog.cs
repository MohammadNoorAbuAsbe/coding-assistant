namespace TerminalAiAssistant;

/// <summary>
/// Static catalog of known model context window sizes, used as an offline
/// fallback when the provider API cannot be queried (e.g. OpenAI/Gemini
/// direct endpoints do not expose context length, or the network is down).
/// First matching pattern wins, so specific patterns must come first.
/// </summary>
public static class ModelCatalog
{
    private static readonly (string Pattern, int ContextWindow)[] Entries =
    {
        ("gpt-4.1", 1_000_000),
        ("gpt-4o", 128_000),
        ("gpt-4-turbo", 128_000),
        ("gpt-4-32k", 32_768),
        ("gpt-4.5", 128_000),
        ("gpt-4", 8_192),
        ("gpt-5", 400_000),
        ("gpt-oss", 131_072),
        ("o1-mini", 128_000),
        ("o1", 200_000),
        ("o3", 200_000),
        ("o4-mini", 200_000),
        ("claude-sonnet-4", 1_000_000),
        ("claude-opus-4", 1_000_000),
        ("claude-4", 1_000_000),
        ("claude-3", 200_000),
        ("claude", 200_000),
        ("gemini", 1_048_576),
        ("qwen3", 32_768),
        ("qwen2.5-coder", 32_768),
        ("qwen2", 32_768),
        ("qwq", 32_768),
        ("llama3.1", 128_000),
        ("llama3.2", 128_000),
        ("llama3.3", 128_000),
        ("llama4", 1_000_000),
        ("llama3", 8_192),
        ("llama", 8_192),
        ("mistral-nemo", 131_072),
        ("mistral-large", 131_072),
        ("mistral-small", 131_072),
        ("ministral", 128_000),
        ("mistral", 32_768),
        ("mixtral", 32_768),
        ("codestral", 32_768),
        ("deepseek-r1", 65_536),
        ("deepseek", 131_072),
        ("codegemma", 8_192),
        ("gemma", 8_192),
        ("phi-4", 16_384),
        ("phi-3", 4_096),
        ("phi", 16_384),
        ("command-r", 128_000),
        ("nemotron", 131_072),
        ("glm-4", 131_072),
        ("glm", 131_072),
        ("kimi", 131_072),
        ("grok", 131_072),
        ("granite", 128_000),
    };

    /// <summary>
    /// Returns the known context window size for a model, or null if unknown.
    /// </summary>
    public static int? Lookup(string? model)
    {
        var normalized = model?.ToLowerInvariant() ?? "";
        if (normalized.Length == 0) return null;

        foreach (var (pattern, contextWindow) in Entries)
        {
            if (normalized.Contains(pattern, StringComparison.Ordinal))
                return contextWindow;
        }
        return null;
    }
}
