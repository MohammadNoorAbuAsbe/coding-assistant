using System.Text;
using OpenAI.Chat;

namespace TerminalAiAssistant;

/// <summary>
/// Production compaction summarizer: sends the dropped conversation turns to
/// the configured model and returns a condensed narrative. Any failure returns
/// null so ContextManager falls back to the deterministic template summary —
/// compaction must never break the session.
/// </summary>
internal static class LlmCompactionSummarizer
{
    private const int MaxInputTokens = 32_000;
    private const int TimeoutSeconds = 60;

    private const string CompactionSystemPrompt = """
        You are a conversation-compression engine for a coding assistant. Your only job is to produce a concise but complete summary of the conversation turns you are given, so the assistant can keep working without them.

        Preserve, in order of importance:
        - the original request and its goal;
        - the current task list / todo state;
        - build or verification status and any errors;
        - which files were read, edited, or created, and what changed;
        - any decisions, open problems, or instructions still pending.

        Write plain prose. No headings, no markdown, no code fences, no tool-call syntax. Never invent facts. If a previous summary is included, merge it with the new turns — do not repeat or discard earlier context. If there is nothing worth preserving, output nothing.
        """;

    public static async Task<string?> SummarizeAsync(
        List<ChatMessage> droppedMessages,
        ChatMessage? existingSummary,
        CancellationToken cancellationToken)
    {
        string userPrompt = BuildPrompt(droppedMessages, existingSummary);
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return null;
        }

        ChatClient client;
        try
        {
            client = ChatService.CreateClient(timeoutSeconds: TimeoutSeconds);
        }
        catch
        {
            return null;
        }

        var options = new ChatCompletionOptions
        {
            Temperature = 0,
            MaxOutputTokenCount = 1024
        };

        try
        {
            var response = await Task.Run(() =>
                ChatService.GetCompletion(
                    client,
                    [new SystemChatMessage(CompactionSystemPrompt), new UserChatMessage(userPrompt)],
                    options),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested) return null;

            var parts = response.Content
                .Where(p => !string.IsNullOrEmpty(p.Text))
                .Select(p => p.Text);
            string summary = string.Join("", parts).Trim();
            return summary.Length == 0 ? null : summary;
        }
        catch
        {
            return null;
        }
    }

    internal static string BuildPrompt(List<ChatMessage> droppedMessages, ChatMessage? existingSummary)
    {
        var sb = new StringBuilder();

        if (existingSummary != null)
        {
            string previous = ContextManager.ExtractText(existingSummary.Content);
            if (!string.IsNullOrWhiteSpace(previous))
            {
                sb.AppendLine("PREVIOUS SUMMARY:");
                sb.AppendLine(previous.Trim());
                sb.AppendLine();
            }
        }

        sb.AppendLine("NEW TURNS:");
        foreach (var message in droppedMessages)
        {
            string text = ContextManager.ExtractText(message.Content);
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.Append(RoleName(message)).AppendLine(":");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        string prompt = sb.ToString().Trim();
        if (prompt.Length == 0) return "";

        int maxChars = EstimateMaxChars(MaxInputTokens);
        if (prompt.Length > maxChars)
        {
            prompt = prompt[..maxChars];
            int lastBreak = prompt.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (lastBreak > maxChars / 2)
            {
                prompt = prompt[..lastBreak];
            }
            prompt += "\n\n[rest of the conversation omitted — too large]";
        }

        return prompt;
    }

    private static int EstimateMaxChars(int maxTokens)
    {
        // Characters per token varies by script; 4 chars/token is a safe upper
        // bound for the mostly-ASCII tool output that dominates dropped turns.
        return maxTokens * 4;
    }

    private static string RoleName(ChatMessage message)
    {
        return message switch
        {
            SystemChatMessage => "SYSTEM",
            UserChatMessage => "USER",
            AssistantChatMessage => "ASSISTANT",
            ToolChatMessage => "TOOL RESULT",
            _ => "MESSAGE"
        };
    }
}
