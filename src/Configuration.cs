using System.Text.Json;

namespace TerminalAiAssistant;

public static class Configuration
{
    private const string OllamaName = "ollama";
    private const string OllamaBaseUrl = "http://localhost:11434/v1";
    private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    private const string OpenAiBaseUrl = "https://api.openai.com/v1";
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    private static string? _provider;
    private static string? _model;
    private static string? _apiKey;
    private static string? _baseUrl;
    private static int? _contextWindowSizeCache;
    private static string? _contextWindowSource;

    internal static Dictionary<string, ProviderConfig> Providers { get; private set; } = new();

    public static void LoadEnvFile()
    {
        var envPath = FindEnvFile();
        if (envPath == null) return;

        try
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                TrySetEnvironmentVariable(line);
            }
        }
        catch (Exception ex)
        {
            using (ConsoleStyler.WithColor(ConsoleColor.Red))
                Console.Error.WriteLine($"Error loading .env file at {envPath}: {ex.Message}");
        }
    }

    private static string? FindEnvFile()
    {
        var dir = Environment.CurrentDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent == null) break;
            dir = parent;
        }
        return null;
    }

    private static void TrySetEnvironmentVariable(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) return;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return;
        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        Environment.SetEnvironmentVariable(key, value);
    }

    public static void LoadProviderConfigs()
    {
        var builtIn = new Dictionary<string, ProviderConfig>
        {
            [OllamaName] = new() { Id = OllamaName, DisplayName = "Ollama (Local)", BaseUrl = OllamaBaseUrl, DefaultModel = "qwen3:8b" },
            ["openrouter"] = new() { Id = "openrouter", DisplayName = "OpenRouter (Cloud)", BaseUrl = OpenRouterBaseUrl, DefaultModel = "openrouter/free", NeedsApiKey = true, ApiKeyEnvVar = "OPENROUTER_API_KEY", SiteUrlEnvVar = "OPENROUTER_SITE_URL", SiteNameEnvVar = "OPENROUTER_SITE_NAME" },
            ["openai"] = new() { Id = "openai", DisplayName = "OpenAI (Cloud)", BaseUrl = OpenAiBaseUrl, DefaultModel = "gpt-4o", NeedsApiKey = true, ApiKeyEnvVar = "OPENAI_API_KEY" },
            ["gemini"] = new() { Id = "gemini", DisplayName = "Google Gemini (Cloud)", BaseUrl = GeminiBaseUrl, DefaultModel = "gemini-3.6-flash", NeedsApiKey = true, ApiKeyEnvVar = "GEMINI_API_KEY" },
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
            catch
            {
                // Ignore malformed config.json; built-in defaults will be used
            }
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
        _contextWindowSizeCache = null;
    }

    public static void SetModel(string model)
    {
        _model = model;
        _contextWindowSizeCache = null;
    }

    public static string GetProvider() => _provider ?? OllamaName;

    public static string GetApiKey()
    {
        var config = Providers[GetProvider()];
        if (!config.NeedsApiKey) return OllamaName;
        if (_apiKey != null) return _apiKey;

        _apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvVar!)
            ?? throw new InvalidOperationException($"{config.ApiKeyEnvVar} is not set. Set it in a .env file (recommended) or as an environment variable.");
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

    public static int? GetMaxIterations()
    {
        var value = Environment.GetEnvironmentVariable("MAX_ITERATIONS");
        return int.TryParse(value, out var result) ? result : null;
    }

    public static int GetContextWindowSize()
    {
        var value = Environment.GetEnvironmentVariable("CONTEXT_WINDOW_SIZE");
        if (int.TryParse(value, out var result)) return result;
        if (_contextWindowSizeCache.HasValue) return _contextWindowSizeCache.Value;
        return ModelCatalog.Lookup(GetModel()) ?? (GetProvider() == OllamaName ? 32768 : 128000);
    }

    /// <summary>
    /// Resolves the context window size for the current model and caches it:
    /// explicit CONTEXT_WINDOW_SIZE env override first, then the provider API
    /// (Ollama/OpenRouter), then the static model catalog. Never throws; on
    /// failure the catalog/defaults are used and GetContextWindowSource()
    /// reports where the value came from.
    /// </summary>
    public static async Task RefreshContextWindowSizeAsync(CancellationToken cancellationToken = default)
    {
        _contextWindowSizeCache = null;
        _contextWindowSource = null;

        var value = Environment.GetEnvironmentVariable("CONTEXT_WINDOW_SIZE");
        if (int.TryParse(value, out var result))
        {
            _contextWindowSizeCache = result;
            _contextWindowSource = "env";
            return;
        }

        string? apiKey = null;
        try
        {
            apiKey = GetApiKey();
        }
        catch
        {
            // Discovery must not fail startup when the key is missing;
            // the catalog fallback will be used instead.
        }

        var discovered = await ContextWindowDiscovery.FetchAsync(GetProvider(), GetBaseUrl(), GetModel(), apiKey, cancellationToken);
        if (discovered.HasValue)
        {
            _contextWindowSizeCache = discovered.Value;
            _contextWindowSource = "api";
            return;
        }

        var catalog = ModelCatalog.Lookup(GetModel());
        if (catalog.HasValue)
        {
            _contextWindowSizeCache = catalog.Value;
            _contextWindowSource = "catalog";
            return;
        }

        _contextWindowSizeCache = GetProvider() == OllamaName ? 32768 : 128000;
        _contextWindowSource = "default";
    }

    /// <summary>
    /// Where the cached context window size came from: "env", "api", "catalog",
    /// "default", or null if RefreshContextWindowSizeAsync has not been called.
    /// </summary>
    public static string? GetContextWindowSource() => _contextWindowSource;

    public static int GetMaxToolResultTokens()
    {
        var value = Environment.GetEnvironmentVariable("MAX_TOOL_RESULT_TOKENS");
        if (int.TryParse(value, out var result)) return result;
        // Local models (Ollama) get a tighter budget: large blobs overwhelm
        // small models' attention, so they should read in focused ranges.
        return (int)(GetContextWindowSize() * (GetProvider() == OllamaName ? 0.2 : 0.4));
    }

    public static float GetTemperature()
    {
        var value = Environment.GetEnvironmentVariable("MODEL_TEMPERATURE");
        return float.TryParse(value, out var result) ? result : 0f;
    }

    public static int GetBashTimeout()
    {
        var value = Environment.GetEnvironmentVariable("BASH_TIMEOUT");
        return int.TryParse(value, out var result) ? result : 120000;
    }

    public static int GetUndoHistoryLimit()
    {
        var value = Environment.GetEnvironmentVariable("UNDO_HISTORY_LIMIT");
        return int.TryParse(value, out var result) && result > 0 ? result : 100;
    }

    public static bool GetAutoVerify()
    {
        // Autopilot mode edits the codebase it runs from; the running process
        // locks the build output, so a post-edit build would fail with file-lock
        // errors regardless of correctness. Verification is always off there.
        if (Autopilot.IsActive)
            return false;

        var value = Environment.GetEnvironmentVariable("AUTO_VERIFY");
        if (bool.TryParse(value, out var result)) return result;
        // On by default: a post-edit build surfaces compile errors immediately,
        // which is the strongest guard against hallucinated refactors. Disable
        // with AUTO_VERIFY=false on very slow hardware.
        return true;
    }

    /// <summary>
    /// Whether to start the assistant directly in autonomous (autopilot) mode.
    /// Set AUTOPILOT=1 to skip the interactive prompt loop and have the agent
    /// continuously improve the project until the user stops it with Ctrl+C.
    /// </summary>
    public static bool IsAutopilotEnabled()
    {
        var value = Environment.GetEnvironmentVariable("AUTOPILOT");
        return value == "1" || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Fraction of the context window reserved for the next model response.
    /// Message history is trimmed to this fraction so the API request never
    /// fills the window and leaves no room for output.
    /// </summary>
    public static double GetContextUsageFraction()
    {
        var value = Environment.GetEnvironmentVariable("MAX_CONTEXT_USAGE");
        if (double.TryParse(value, out var result) && result > 0 && result <= 1)
            return result;
        return 0.8;
    }

    /// <summary>
    /// Whether dropped history is summarized by the model (true by default).
    /// Set LLM_COMPACTION=0 to use the deterministic template summary.
    /// </summary>
    public static bool GetLlmCompactionEnabled()
    {
        var value = Environment.GetEnvironmentVariable("LLM_COMPACTION");
        if (!string.IsNullOrEmpty(value))
        {
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>
    /// Token ceiling for the compaction summary inserted into history.
    /// </summary>
    public static int GetMaxCompactionTokens()
    {
        var value = Environment.GetEnvironmentVariable("MAX_COMPACTION_TOKENS");
        return int.TryParse(value, out var result) && result > 0 ? result : 1200;
    }

    /// <summary>
    /// Whether to print real per-request token usage and the calibration
    /// factor after each model call (VERBOSE_CTX=1).
    /// </summary>
    public static bool GetVerboseCtx()
    {
        return Environment.GetEnvironmentVariable("VERBOSE_CTX") == "1";
    }

    public static string GetVerifyCommand()
    {
        return Environment.GetEnvironmentVariable("VERIFY_COMMAND") ?? "dotnet build --nologo -v q";
    }

    /// <summary>
    /// Returns the VERIFY_COMMAND environment variable when explicitly set,
    /// or null when the default dotnet command should be used instead.
    /// </summary>
    public static string? GetVerifyCommandOverride()
    {
        return Environment.GetEnvironmentVariable("VERIFY_COMMAND");
    }

    public static int GetVerifyTimeout()
    {
        var value = Environment.GetEnvironmentVariable("VERIFY_TIMEOUT");
        return int.TryParse(value, out var result) ? result : 120000;
    }

    public static string GetTavilyApiKey()
    {
        return Environment.GetEnvironmentVariable("TAVILY_API_KEY")
            ?? throw new InvalidOperationException("TAVILY_API_KEY is not set. Set it in a .env file (recommended) or as an environment variable. Get a key at https://tavily.com.");
    }
}

internal class ConfigFile
{
    public string? DefaultProvider { get; set; }
    public Dictionary<string, ProviderConfig>? Providers { get; set; }
}
