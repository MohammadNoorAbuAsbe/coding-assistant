using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ContextWindowDiscoveryTests
{
    [Fact]
    public void ParseOllamaShowResponse_NumCtxOverrideWins()
    {
        const string json = """
            {
              "model_info": { "qwen3.context_length": 32768, "llama.vocab_size": 151936 },
              "parameters": { "num_ctx": "16384", "temperature": 0.7 }
            }
            """;
        Assert.Equal(16384, ContextWindowDiscovery.ParseOllamaShowResponse(json));
    }

    [Fact]
    public void ParseOllamaShowResponse_ModelInfoContextLength()
    {
        const string json = """
            {
              "model_info": { "qwen3.context_length": 32768, "llama.rope.scaling.type": "yarn" }
            }
            """;
        Assert.Equal(32768, ContextWindowDiscovery.ParseOllamaShowResponse(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model_info\": {\"llama.vocab_size\": 151936}}")]
    [InlineData("not json")]
    public void ParseOllamaShowResponse_MissingReturnsNull(string json)
    {
        Assert.Null(ContextWindowDiscovery.ParseOllamaShowResponse(json));
    }

    [Fact]
    public void ParseOpenRouterModelResponse_ReadsContextLength()
    {
        const string json = """
            {
              "data": {
                "id": "openai/gpt-4o",
                "name": "GPT-4o",
                "context_length": 128000
              }
            }
            """;
        Assert.Equal(128000, ContextWindowDiscovery.ParseOpenRouterModelResponse(json));
    }

    [Fact]
    public void ParseOpenRouterModelResponse_NullContextLength()
    {
        const string json = """
            { "data": { "id": "openai/gpt-4o", "context_length": null } }
            """;
        Assert.Null(ContextWindowDiscovery.ParseOpenRouterModelResponse(json));
    }

    [Fact]
    public void ParseOpenRouterModelsResponse_FindsModelById()
    {
        const string json = """
            {
              "data": [
                { "id": "anthropic/claude-3.7-sonnet", "context_length": 200000 },
                { "id": "openai/gpt-4o", "context_length": 128000 }
              ]
            }
            """;
        Assert.Equal(200000, ContextWindowDiscovery.ParseOpenRouterModelsResponse(json, "anthropic/claude-3.7-sonnet"));
        Assert.Equal(128000, ContextWindowDiscovery.ParseOpenRouterModelsResponse(json, "OPENAI/GPT-4O"));
    }

    [Fact]
    public void ParseOpenRouterModelsResponse_MissingModelReturnsNull()
    {
        const string json = """
            { "data": [ { "id": "openai/gpt-4o", "context_length": 128000 } ] }
            """;
        Assert.Null(ContextWindowDiscovery.ParseOpenRouterModelsResponse(json, "no/such-model"));
    }

    [Fact]
    public void ParseGeminiModelResponse_ReadsInputTokenLimit()
    {
        const string json = """
            {
              "name": "models/gemini-3.6-flash",
              "displayName": "Gemini 3.6 Flash",
              "inputTokenLimit": 1048576,
              "outputTokenLimit": 65536
            }
            """;
        Assert.Equal(1048576, ContextWindowDiscovery.ParseGeminiModelResponse(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\": \"models/gemini-3.6-flash\", \"outputTokenLimit\": 65536}")]
    [InlineData("not json")]
    public void ParseGeminiModelResponse_MissingReturnsNull(string json)
    {
        Assert.Null(ContextWindowDiscovery.ParseGeminiModelResponse(json));
    }
}
