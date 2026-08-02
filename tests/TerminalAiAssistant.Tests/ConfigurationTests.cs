using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ConfigurationTests
{
    private static void SetEnv(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    [Fact]
    public void BuiltinProviders_HaveExpectedDefaults()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");

        Configuration.LoadProviderConfigs();

        Assert.True(Configuration.Providers.ContainsKey("ollama"));
        Assert.True(Configuration.Providers.ContainsKey("openrouter"));
        Assert.True(Configuration.Providers.ContainsKey("openai"));
        Assert.True(Configuration.Providers.ContainsKey("gemini"));

        var ollama = Configuration.Providers["ollama"];
        Assert.False(ollama.NeedsApiKey);
        Assert.Equal("http://localhost:11434/v1", ollama.BaseUrl);
        Assert.Equal("qwen3:8b", ollama.DefaultModel);

        var openrouter = Configuration.Providers["openrouter"];
        Assert.True(openrouter.NeedsApiKey);
        Assert.Equal("OPENROUTER_API_KEY", openrouter.ApiKeyEnvVar);
        Assert.Equal("OPENROUTER_SITE_URL", openrouter.SiteUrlEnvVar);
        Assert.Equal("OPENROUTER_SITE_NAME", openrouter.SiteNameEnvVar);

        var openai = Configuration.Providers["openai"];
        Assert.True(openai.NeedsApiKey);
        Assert.Equal("OPENAI_API_KEY", openai.ApiKeyEnvVar);
        Assert.Equal("https://api.openai.com/v1", openai.BaseUrl);

        var gemini = Configuration.Providers["gemini"];
        Assert.True(gemini.NeedsApiKey);
        Assert.Equal("GEMINI_API_KEY", gemini.ApiKeyEnvVar);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", gemini.BaseUrl);
        Assert.Equal("gemini-3.6-flash", gemini.DefaultModel);
    }

    [Fact]
    public void ConfigJson_OverridesBuiltinAndAddsProviders()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");
        ws.WriteFile("config.json", """
            {
              "providers": {
                "openai": { "baseUrl": "https://custom-openai.example.com/v1", "defaultModel": "custom-model", "needsApiKey": true, "apiKeyEnvVar": "OPENAI_API_KEY" },
                "custom": { "displayName": "Custom", "baseUrl": "http://localhost:9999/v1", "defaultModel": "local-model" }
              }
            }
            """);

        Configuration.LoadProviderConfigs();

        Assert.Equal("https://custom-openai.example.com/v1", Configuration.Providers["openai"].BaseUrl);
        Assert.Equal("custom-model", Configuration.Providers["openai"].DefaultModel);
        Assert.True(Configuration.Providers.ContainsKey("custom"));
        Assert.Equal("http://localhost:9999/v1", Configuration.Providers["custom"].BaseUrl);
        Assert.Equal("local-model", Configuration.Providers["custom"].DefaultModel);
    }

    [Fact]
    public void AiProvider_EnvRespected()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");
        SetEnv("AI_PROVIDER", "openai");

        Configuration.LoadProviderConfigs();

        Assert.Equal("openai", Configuration.GetProvider());
        Assert.Equal("gpt-4o", Configuration.GetModel());
    }

    [Fact]
    public void AiProvider_Invalid_FallsBackToOllama()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");
        SetEnv("AI_PROVIDER", "bogus-provider");

        Configuration.LoadProviderConfigs();

        Assert.Equal("ollama", Configuration.GetProvider());
    }

    [Fact]
    public void SetProvider_UpdatesModelToDefault()
    {
        using var ws = new TempWorkspace();

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.Equal("openai", Configuration.GetProvider());
        Assert.Equal("gpt-4o", Configuration.GetModel());
    }

    [Fact]
    public void GetApiKey_Ollama_ReturnsProviderName()
    {
        using var ws = new TempWorkspace();

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("ollama");

        Assert.Equal("ollama", Configuration.GetApiKey());
    }

    [Fact]
    public void GetApiKey_MissingKey_Throws()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("OPENAI_API_KEY");
        SetEnv("OPENAI_API_KEY", null);

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.Throws<InvalidOperationException>(() => Configuration.GetApiKey());
    }

    [Fact]
    public void GetApiKey_ReadsEnvironmentVariable()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("OPENAI_API_KEY");
        SetEnv("OPENAI_API_KEY", "sk-test-key-123");

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.Equal("sk-test-key-123", Configuration.GetApiKey());
    }

    [Fact]
    public void GetBaseUrl_Default()
    {
        using var ws = new TempWorkspace();

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.Equal("https://api.openai.com/v1", Configuration.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_EnvOverride()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("OPENAI_BASE_URL");
        SetEnv("OPENAI_BASE_URL", "https://example.com/v1");

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openai");

        Assert.Equal("https://example.com/v1", Configuration.GetBaseUrl());
    }

    [Fact]
    public void GetSiteUrl_OnlyWhenConfigured()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("OPENROUTER_SITE_URL");
        SetEnv("OPENROUTER_SITE_URL", "https://mysite.com");

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("openrouter");
        Assert.Equal("https://mysite.com", Configuration.GetSiteUrl());

        Configuration.SetProvider("openai");
        Assert.Null(Configuration.GetSiteUrl());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("5", 5)]
    [InlineData("abc", null)]
    [InlineData("-1", -1)]
    public void GetMaxIterations_Parsing(string? value, int? expected)
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("MAX_ITERATIONS");
        SetEnv("MAX_ITERATIONS", value);

        Assert.Equal(expected, Configuration.GetMaxIterations());
    }

    [Fact]
    public void GetContextWindowSize_DefaultPerProvider()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");
        ws.SaveEnv("CONTEXT_WINDOW_SIZE");
        SetEnv("CONTEXT_WINDOW_SIZE", null);

        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("ollama");
        Assert.Equal(32768, Configuration.GetContextWindowSize());

        Configuration.SetProvider("openai");
        Assert.Equal(128000, Configuration.GetContextWindowSize());
    }

    [Fact]
    public void GetContextWindowSize_EnvOverride()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("CONTEXT_WINDOW_SIZE");
        SetEnv("CONTEXT_WINDOW_SIZE", "5000");

        Assert.Equal(5000, Configuration.GetContextWindowSize());
    }

    [Fact]
    public void GetMaxToolResultTokens_DefaultIsFortyPercent()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("CONTEXT_WINDOW_SIZE");
        ws.SaveEnv("MAX_TOOL_RESULT_TOKENS");
        SetEnv("MAX_TOOL_RESULT_TOKENS", null);
        SetEnv("CONTEXT_WINDOW_SIZE", "10000");

        Assert.Equal(4000, Configuration.GetMaxToolResultTokens());
    }

    [Fact]
    public void GetMaxToolResultTokens_EnvOverride()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("MAX_TOOL_RESULT_TOKENS");
        SetEnv("MAX_TOOL_RESULT_TOKENS", "123");

        Assert.Equal(123, Configuration.GetMaxToolResultTokens());
    }

    [Fact]
    public void GetBashTimeout_DefaultAndOverride()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("BASH_TIMEOUT");
        SetEnv("BASH_TIMEOUT", null);
        Assert.Equal(120000, Configuration.GetBashTimeout());

        SetEnv("BASH_TIMEOUT", "99");
        Assert.Equal(99, Configuration.GetBashTimeout());
    }

    [Fact]
    public void GetTavilyApiKey_Missing_Throws()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("TAVILY_API_KEY");
        SetEnv("TAVILY_API_KEY", null);

        Assert.Throws<InvalidOperationException>(() => Configuration.GetTavilyApiKey());
    }

    [Fact]
    public void GetTavilyApiKey_ReadsEnvironmentVariable()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("TAVILY_API_KEY");
        SetEnv("TAVILY_API_KEY", "tvly-test");

        Assert.Equal("tvly-test", Configuration.GetTavilyApiKey());
    }

    [Fact]
    public void LoadEnvFile_ParsesValuesCommentsAndQuotes()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("TAA_UNQUOTED");
        ws.SaveEnv("TAA_QUOTED");
        ws.SaveEnv("TAA_COMMENTED");
        ws.WriteFile(".env", """
            # this is a comment
            TAA_UNQUOTED=plain value

            TAA_QUOTED="value with spaces"
            TAA_COMMENTED=should-be-set
            # TAA_COMMENTED2=ignored
            LINE_WITHOUT_EQUALS
            """);

        Configuration.LoadEnvFile();

        Assert.Equal("plain value", Environment.GetEnvironmentVariable("TAA_UNQUOTED"));
        Assert.Equal("value with spaces", Environment.GetEnvironmentVariable("TAA_QUOTED"));
        Assert.Equal("should-be-set", Environment.GetEnvironmentVariable("TAA_COMMENTED"));
        Assert.Null(Environment.GetEnvironmentVariable("LINE_WITHOUT_EQUALS"));
    }

    [Fact]
    public void LoadProviderConfigs_MalformedConfigJson_Ignored()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("AI_PROVIDER");
        ws.WriteFile("config.json", "{ not valid json !!!");

        Configuration.LoadProviderConfigs();

        Assert.True(Configuration.Providers.ContainsKey("ollama"));
        Assert.True(Configuration.Providers.ContainsKey("openai"));
        Assert.Equal("ollama", Configuration.GetProvider());
    }
}
