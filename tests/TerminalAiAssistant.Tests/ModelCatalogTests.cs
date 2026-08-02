using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ModelCatalogTests
{
    [Theory]
    [InlineData("gpt-4o", 128000)]
    [InlineData("gpt-4o-mini", 128000)]
    [InlineData("gpt-4-turbo", 128000)]
    [InlineData("gpt-4", 8192)]
    [InlineData("gpt-4.1", 1000000)]
    [InlineData("gpt-5", 400000)]
    [InlineData("o1", 200000)]
    [InlineData("o1-mini", 128000)]
    [InlineData("o3", 200000)]
    [InlineData("o4-mini", 200000)]
    [InlineData("claude-sonnet-4-20250514", 1000000)]
    [InlineData("claude-3-7-sonnet-20250219", 200000)]
    [InlineData("gemini-3.6-flash", 1048576)]
    [InlineData("qwen3:8b", 32768)]
    [InlineData("qwen2.5-coder:7b", 32768)]
    [InlineData("llama3.1:70b", 128000)]
    [InlineData("llama3:8b", 8192)]
    [InlineData("mistral:7b", 32768)]
    [InlineData("deepseek-v3", 131072)]
    [InlineData("deepseek-r1", 65536)]
    public void Lookup_KnownModels_ReturnsExpected(string model, int expected)
    {
        Assert.Equal(expected, ModelCatalog.Lookup(model));
    }

    [Theory]
    [InlineData("")]
    [InlineData("openrouter/free")]
    [InlineData("totally-unknown-model-xyz")]
    public void Lookup_UnknownModels_ReturnsNull(string model)
    {
        Assert.Null(ModelCatalog.Lookup(model));
    }
}
