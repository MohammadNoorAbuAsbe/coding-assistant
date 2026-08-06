using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ContextManagerCompactionTests
{
    public ContextManagerCompactionTests()
    {
        Configuration.LoadProviderConfigs();
        Configuration.SetProvider("ollama");
        Configuration.SetModel("qwen3:8b");
        ContextManager.SummarizerOverride = null;
        Environment.SetEnvironmentVariable("LLM_COMPACTION", null);
    }

    [Fact]
    public async Task TruncateMessagesAsync_EverythingFits_DoesNotCallSummarizer()
    {
        int calls = 0;
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
        {
            calls++;
            return Task.FromResult<string?>("never used");
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system"),
            new UserChatMessage("task"),
            new UserChatMessage("newest")
        };

        var result = await ContextManager.TruncateMessagesAsync(messages, 1_000_000);

        Assert.Equal(3, result.Count);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TruncateMessagesAsync_SummarizerText_UsedAsSummary()
    {
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
            Task.FromResult<string?>("LLM summary of dropped turns");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        var result = await ContextManager.TruncateMessagesAsync(messages, systemTokens + newestTokens + 500);

        Assert.Equal(3, result.Count);
        string summaryText = ContextManager.ExtractText(result[1].Content!);
        Assert.Contains("[Session context (older messages trimmed)]", summaryText);
        Assert.Contains("LLM summary of dropped turns", summaryText);
    }

    [Fact]
    public async Task TruncateMessagesAsync_SummarizerReturnsNull_FallsBackToTemplate()
    {
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
            Task.FromResult<string?>(null);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        var result = await ContextManager.TruncateMessagesAsync(messages, systemTokens + newestTokens + 500);

        string summaryText = ContextManager.ExtractText(result[1].Content!);
        Assert.Contains("[Session context (older messages trimmed)]", summaryText);
        Assert.Contains("Original request: original task text", summaryText);
    }

    [Fact]
    public async Task TruncateMessagesAsync_SummarizerThrows_FallsBackToTemplate()
    {
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
            throw new InvalidOperationException("model unavailable");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        var result = await ContextManager.TruncateMessagesAsync(messages, systemTokens + newestTokens + 500);

        string summaryText = ContextManager.ExtractText(result[1].Content!);
        Assert.Contains("Original request: original task text", summaryText);
    }

    [Fact]
    public async Task TruncateMessagesAsync_SummarizerThrowsCancellation_Propagates()
    {
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
            throw new OperationCanceledException();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system"),
            new UserChatMessage("task"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ContextManager.TruncateMessagesAsync(messages, systemTokens + newestTokens + 500));
    }

    [Fact]
    public async Task TruncateMessagesAsync_LlmCompactionDisabled_UsesTemplate()
    {
        int calls = 0;
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
        {
            calls++;
            return Task.FromResult<string?>("should not be called");
        };
        Environment.SetEnvironmentVariable("LLM_COMPACTION", "0");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        var result = await ContextManager.TruncateMessagesAsync(messages, systemTokens + newestTokens + 500);

        Assert.Equal(0, calls);
        string summaryText = ContextManager.ExtractText(result[1].Content!);
        Assert.Contains("Original request: original task text", summaryText);
    }

    [Fact]
    public void TruncateMessages_ReadRoundDroppedFirst_NewerConversationSurvives()
    {
        var (assistant, toolResult) = MakeReadRound("call-1", "src/Foo.cs",
            string.Join(" ", Enumerable.Repeat("filedata", 1000)));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            assistant,
            toolResult,
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int taskTokens = ContextManager.EstimateMessageTokens(messages[1]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[4]);
        int limit = systemTokens + taskTokens + newestTokens + 500;

        var result = ContextManager.TruncateMessages(messages, limit);

        // Read round evicted, conversation kept.
        Assert.DoesNotContain(result, m => m is ToolChatMessage);
        Assert.Equal("newest message", ContextManager.ExtractText(result[^1].Content!));

        string summaryText = ContextManager.ExtractText(result[1].Content!);
        Assert.Contains("[Session context (older messages trimmed)]", summaryText);
        Assert.Contains("Earlier Read results dropped: src/Foo.cs", summaryText);
    }

    [Fact]
    public void TruncateMessages_ToolRoundNeverSplit_OrphanedToolResultPrevented()
    {
        // The assistant read message is tiny, the tool result alone fits the
        // budget, but the pair does not. The whole round must be dropped.
        var (assistant, toolResult) = MakeReadRound("call-1", "src/Foo.cs",
            string.Join(" ", Enumerable.Repeat("filedata", 1000)));

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            assistant,
            toolResult,
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[4]);
        int toolResultTokens = ContextManager.EstimateMessageTokens(toolResult);
        int limit = systemTokens + newestTokens + toolResultTokens;

        var result = ContextManager.TruncateMessages(messages, limit);

        // No orphaned tool result: either both halves survive or neither does.
        int toolMessages = result.Count(m => m is ToolChatMessage);
        int toolCallAssistants = result.Count(m => m is AssistantChatMessage a && a.ToolCalls is { Count: > 0 });
        Assert.Equal(toolMessages, toolCallAssistants);
        Assert.DoesNotContain(result, m => m is ToolChatMessage);
        Assert.Contains("newest message", ContextManager.ExtractText(result[^1].Content!));
    }

    [Fact]
    public async Task TruncateMessagesAsync_SecondCompaction_ReplacesPreviousSummary()
    {
        ContextManager.SummarizerOverride = (dropped, previous, ct) =>
            Task.FromResult<string?>("LLM summary #2");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("system message"),
            new UserChatMessage("original task text"),
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000))),
            new UserChatMessage("newest message")
        };

        int systemTokens = ContextManager.EstimateMessageTokens(messages[0]);
        int newestTokens = ContextManager.EstimateMessageTokens(messages[3]);
        int limit = systemTokens + newestTokens + 500;

        var first = await ContextManager.TruncateMessagesAsync(messages, limit);
        Assert.Contains("LLM summary #2", ContextManager.ExtractText(first[1].Content!));

        var secondList = new List<ChatMessage>(first)
        {
            new UserChatMessage(string.Join(" ", Enumerable.Repeat("word", 1000)))
        };
        var second = await ContextManager.TruncateMessagesAsync(secondList, limit);

        int summaries = second.Count(m => m is UserChatMessage u &&
            ContextManager.ExtractText(u.Content!).Contains("[Session context (older messages trimmed)]"));
        Assert.Equal(1, summaries);
        Assert.Contains("LLM summary #2", ContextManager.ExtractText(second[1].Content!));
    }

    private static (AssistantChatMessage Assistant, ToolChatMessage Result) MakeReadRound(
        string callId, string filePath, string content)
    {
        var toolCall = ChatToolCall.CreateFunctionToolCall(callId, ToolHandler.ReadFunctionName,
            BinaryData.FromString("{\"file_path\": \"" + filePath + "\"}"));
        return (new AssistantChatMessage([toolCall]), new ToolChatMessage(callId, content));
    }
}
