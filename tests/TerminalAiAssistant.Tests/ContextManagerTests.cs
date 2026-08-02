using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ContextManagerTests
{
    public ContextManagerTests()
    {
        Configuration.LoadProviderConfigs();
    }

    [Fact]
    public void EstimateTokens_Empty_ReturnsZero()
    {
        Assert.Equal(0, ContextManager.EstimateTokens(""));
        Assert.Equal(0, ContextManager.EstimateTokens(null!));
    }

    [Fact]
    public void EstimateTokens_NonEmpty_Positive()
    {
        Assert.True(ContextManager.EstimateTokens("hello world") > 0);
    }

    [Fact]
    public void EstimateTokens_ShortAscii_ExactCount()
    {
        Assert.Equal(2, ContextManager.EstimateTokens("hello world"));
    }

    [Fact]
    public void EstimateTokens_ScalesWithText()
    {
        string small = "hello world";
        string big = string.Join(' ', Enumerable.Repeat("hello world", 100));
        Assert.True(ContextManager.EstimateTokens(big) > ContextManager.EstimateTokens(small));
    }

    [Fact]
    public void EstimateMessageTokens_SystemMessage_IncludesOverhead()
    {
        var msg = new SystemChatMessage("hi");
        Assert.Equal(4 + ContextManager.EstimateTokens("hi"), ContextManager.EstimateMessageTokens(msg));
    }

    [Fact]
    public void EstimateMessageTokens_UserMessage_IncludesOverhead()
    {
        var msg = new UserChatMessage("hi");
        Assert.Equal(4 + ContextManager.EstimateTokens("hi"), ContextManager.EstimateMessageTokens(msg));
    }

    [Fact]
    public void EstimateMessageTokens_AssistantMessage_IncludesOverhead()
    {
        var msg = new AssistantChatMessage("hi");
        Assert.Equal(4 + ContextManager.EstimateTokens("hi"), ContextManager.EstimateMessageTokens(msg));
    }

    [Fact]
    public void EstimateMessageTokens_ToolMessage_IncludesOverhead()
    {
        var msg = new ToolChatMessage("call-1", "hi");
        Assert.Equal(4 + ContextManager.EstimateTokens("hi"), ContextManager.EstimateMessageTokens(msg));
    }

    [Fact]
    public void EstimateMessageTokens_AssistantWithToolCalls_OnlyCountsTextContent()
    {
        var msg = new AssistantChatMessage("hi");
        Assert.Equal(4 + ContextManager.EstimateTokens("hi"), ContextManager.EstimateMessageTokens(msg));
    }

    [Fact]
    public void TruncateMessages_UnderLimit_KeepsAllInOrder()
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system"),
            new UserChatMessage("first"),
            new UserChatMessage("second"),
            new UserChatMessage("third")
        };

        var result = ContextManager.TruncateMessages(messages, 1_000_000);

        Assert.Equal(4, result.Count);
        Assert.Equal("system", Assert.IsType<SystemChatMessage>(result[0]).Content?[0].Text);
        Assert.Equal("first", Assert.IsType<UserChatMessage>(result[1]).Content?[0].Text);
        Assert.Equal("second", Assert.IsType<UserChatMessage>(result[2]).Content?[0].Text);
        Assert.Equal("third", Assert.IsType<UserChatMessage>(result[3]).Content?[0].Text);
    }

    [Fact]
    public void TruncateMessages_OverLimit_DropsOldestKeepsSystemAndNewest()
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("older message that will be dropped"),
            new UserChatMessage("second message that will be dropped"),
            new UserChatMessage("newest message that survives")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        int limit = systemTokens + newestTokens;

        var result = ContextManager.TruncateMessages(messages, limit);

        Assert.Equal(2, result.Count);
        Assert.Equal("system message", Assert.IsType<SystemChatMessage>(result[0]).Content?[0].Text);
        Assert.Equal("newest message that survives", Assert.IsType<UserChatMessage>(result[1]).Content?[0].Text);
    }

    [Fact]
    public void TruncateMessages_PartialOverLimit_KeepsWhatFitsFromNewest()
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("sys"),
            new UserChatMessage("alpha alpha alpha alpha"),
            new UserChatMessage("beta beta beta beta"),
            new UserChatMessage("gamma gamma gamma gamma")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int gammaTokens = ContextManager.EstimateMessageTokens(messages[3]);
        int betaTokens = ContextManager.EstimateMessageTokens(messages[2]);
        int limit = systemTokens + gammaTokens + betaTokens;

        var result = ContextManager.TruncateMessages(messages, limit);

        Assert.Equal(3, result.Count);
        Assert.Equal("sys", Assert.IsType<SystemChatMessage>(result[0]).Content?[0].Text);
        Assert.Equal("beta beta beta beta", Assert.IsType<UserChatMessage>(result[1]).Content?[0].Text);
        Assert.Equal("gamma gamma gamma gamma", Assert.IsType<UserChatMessage>(result[2]).Content?[0].Text);
    }

    [Fact]
    public void TruncateMessages_OnlySystem_KeepsSystemEvenWhenOverLimit()
    {
        var messages = new List<ChatMessage> { new SystemChatMessage("a very long system message") };

        var result = ContextManager.TruncateMessages(messages, 1);

        Assert.Single(result);
    }

    [Fact]
    public void TruncateMessages_Empty_ReturnsEmpty()
    {
        var result = ContextManager.TruncateMessages(new List<ChatMessage>(), 1000);

        Assert.Empty(result);
    }

    [Fact]
    public void TruncateToolResult_Empty_Unchanged()
    {
        Assert.Equal("", ContextManager.TruncateToolResult("", 100));
        Assert.Null(ContextManager.TruncateToolResult(null!, 100));
    }

    [Fact]
    public void TruncateToolResult_Short_Unchanged()
    {
        string content = "short result";
        Assert.Equal(content, ContextManager.TruncateToolResult(content, 1000));
    }

    [Fact]
    public void TruncateToolResult_Long_TruncatedWithMarker()
    {
        string content = string.Join(" ", Enumerable.Repeat("word", 500));
        string result = ContextManager.TruncateToolResult(content, 10);

        Assert.True(result.Length < content.Length);
        Assert.EndsWith("... [truncated, content exceeded token limit]", result);
    }

    [Fact]
    public void TruncateToolResult_FitsExactly_Unchanged()
    {
        string content = "hello world";
        int tokens = ContextManager.EstimateTokens(content);

        Assert.Equal(content, ContextManager.TruncateToolResult(content, tokens));
    }

    [Fact]
    public void ExtractText_JoinsTextParts()
    {
        var content = ChatMessageContentPart.CreateTextPart("foo");
        var content2 = ChatMessageContentPart.CreateTextPart("bar");
        var msg = new UserChatMessage([content, content2]);

        Assert.Equal("foobar", ContextManager.ExtractText(msg.Content!));
    }
}
