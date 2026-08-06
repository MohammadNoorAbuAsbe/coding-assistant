using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class TokenEstimatorTests
{
    public TokenEstimatorTests()
    {
        Configuration.LoadProviderConfigs();
    }

    [Fact]
    public void Estimate_EmptyOrNull_ReturnsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate(""));
        Assert.Equal(0, TokenEstimator.Estimate(null));
    }

    [Fact]
    public void Estimate_Heuristic_AsciiFourCharsPerToken()
    {
        // Default provider (ollama) uses the heuristic: no CJK, ceil(11/4) = 3.
        Assert.Equal(3, TokenEstimator.Estimate("hello world"));
    }

    [Fact]
    public void Estimate_Heuristic_CjkOneTokenPerChar()
    {
        Assert.Equal(2, TokenEstimator.Estimate("你好"));
        Assert.Equal(4, TokenEstimator.Estimate("你好世界"));
    }

    [Fact]
    public void Estimate_Heuristic_MixedCjkAndAscii()
    {
        // 2 CJK + 4 ascii chars (1 token) = 3.
        Assert.Equal(3, TokenEstimator.Estimate("你好 ab"));
    }

    [Fact]
    public void Estimate_Tiktoken_OpenAiModel_Exact()
    {
        Configuration.SetProvider("openai");
        Configuration.SetModel("gpt-4o");
        try
        {
            Assert.Equal(2, TokenEstimator.Estimate("hello world"));
        }
        finally
        {
            Configuration.SetProvider("ollama");
            Configuration.SetModel("qwen3:8b");
        }
    }

    [Fact]
    public void GetIndexByTokenCount_Heuristic_Exceeds_ReturnsCutPoint()
    {
        // "hello world": per-char cost 0.25, cumulative cost >= 1 at index 3.
        Assert.Equal(3, TokenEstimator.GetIndexByTokenCount("hello world", 1));
    }

    [Fact]
    public void GetIndexByTokenCount_Heuristic_Fits_ReturnsFullLength()
    {
        Assert.Equal("hello world".Length, TokenEstimator.GetIndexByTokenCount("hello world", 10));
    }

    [Fact]
    public void GetIndexByTokenCount_EmptyOrZeroBudget_ReturnsZero()
    {
        Assert.Equal(0, TokenEstimator.GetIndexByTokenCount("", 100));
        Assert.Equal(0, TokenEstimator.GetIndexByTokenCount("hello", 0));
        Assert.Equal(0, TokenEstimator.GetIndexByTokenCount(null!, 100));
    }

    [Fact]
    public void GetIndexByTokenCount_Tiktoken_Exact()
    {
        Configuration.SetProvider("openai");
        Configuration.SetModel("gpt-4o");
        try
        {
            // "hello" is 1 token; adding " world" exceeds 1 at index 5.
            Assert.Equal(5, TokenEstimator.GetIndexByTokenCount("hello world", 1));
        }
        finally
        {
            Configuration.SetProvider("ollama");
            Configuration.SetModel("qwen3:8b");
        }
    }
}
