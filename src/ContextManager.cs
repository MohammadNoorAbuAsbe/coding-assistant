using Microsoft.ML.Tokenizers;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ContextManager
{
    private static TiktokenTokenizer? _tokenizer;
    private static readonly object _lock = new();

    private static TiktokenTokenizer GetTokenizer()
    {
        if (_tokenizer == null)
        {
            lock (_lock)
            {
                if (_tokenizer == null)
                {
                    var model = Configuration.GetModel()?.ToLowerInvariant() ?? "";
                    var encoding = model switch
                    {
                        string m when m.Contains("gpt-4o") || m.Contains("o1") || m.Contains("o3") => "o200k_base",
                        _ => "cl100k_base"
                    };
                    _tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);
                }
            }
        }
        return _tokenizer;
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return GetTokenizer().CountTokens(text, considerPreTokenization: false, considerNormalization: false);
    }

    /// <summary>
    /// Returns the character index in <paramref name="text"/> where the token
    /// count first exceeds <paramref name="maxTokens"/>, or the full length if
    /// the text fits within the budget.
    /// </summary>
    public static int GetTokenLimitIndex(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return 0;

        int current = EstimateTokens(text);
        if (current <= maxTokens) return text.Length;

        return GetTokenizer().GetIndexByTokenCount(text, maxTokens, out _, out _,
            considerPreTokenization: false, considerNormalization: false);
    }

    public static int EstimateMessageTokens(ChatMessage message)
    {
        int tokens = 4;

        if (message is SystemChatMessage sysMsg && sysMsg.Content != null)
        {
            tokens += EstimateTokens(ExtractText(sysMsg.Content));
        }
        else if (message is UserChatMessage userMsg && userMsg.Content != null)
        {
            tokens += EstimateTokens(ExtractText(userMsg.Content));
        }
        else if (message is AssistantChatMessage assistantMsg)
        {
            if (assistantMsg.Content != null)
            {
                tokens += EstimateTokens(ExtractText(assistantMsg.Content));
            }
        }
        else if (message is ToolChatMessage toolMsg && toolMsg.Content != null)
        {
            tokens += EstimateTokens(ExtractText(toolMsg.Content));
        }

        return tokens;
    }

    public static List<ChatMessage> TruncateMessages(List<ChatMessage> messages, int maxTokens)
    {
        const string summaryMarker = "[Session context (older messages trimmed)]";

        var existingSummary = messages.FirstOrDefault(m =>
            m is UserChatMessage && ExtractText(m.Content).Contains(summaryMarker));

        var summary = BuildCompactionSummary(messages, existingSummary);

        if (existingSummary != null)
        {
            messages = messages.Where(m => m != existingSummary).ToList();
        }

        int totalTokens = 0;
        var result = new List<ChatMessage>();

        var systemMessages = messages.Where(m => m is SystemChatMessage).ToList();
        var otherMessages = messages.Where(m => m is not SystemChatMessage).ToList();

        foreach (var msg in systemMessages)
        {
            totalTokens += EstimateMessageTokens(msg);
            result.Add(msg);
        }

        for (int i = otherMessages.Count - 1; i >= 0; i--)
        {
            int msgTokens = EstimateMessageTokens(otherMessages[i]);
            if (totalTokens + msgTokens > maxTokens)
            {
                break;
            }
            totalTokens += msgTokens;
            result.Insert(systemMessages.Count, otherMessages[i]);
        }

        // Only pin the summary when messages were actually dropped AND it fits
        // in the leftover budget; otherwise keep the full surviving history.
        bool dropped = result.Count < systemMessages.Count + otherMessages.Count;
        if (dropped && summary != null && totalTokens + EstimateMessageTokens(summary) <= maxTokens)
        {
            result.Insert(systemMessages.Count, summary);
        }

        return result;
    }

    private const int CompactionSummaryMaxTokens = 800;

    private static ChatMessage? BuildCompactionSummary(List<ChatMessage> messages, ChatMessage? existingSummary)
    {
        var parts = new List<string>();
        parts.Add("[Session context (older messages trimmed)] — earlier conversation was dropped to fit the context window.");

        string? originalTask = ExtractOriginalTask(messages, existingSummary);
        if (!string.IsNullOrEmpty(originalTask))
        {
            originalTask = originalTask.Length > 800 ? originalTask[..800] + "…" : originalTask;
            parts.Add($"Original request: {originalTask}");
        }

        string? todoState = null;
        string? buildState = null;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg is ToolChatMessage toolMsg && toolMsg.Content != null && todoState == null && ExtractText(toolMsg.Content).Contains("## Task List"))
            {
                todoState = ExtractText(toolMsg.Content);
            }
            else if (msg is UserChatMessage userMsg && userMsg.Content != null && buildState == null && ExtractText(userMsg.Content).StartsWith("Automatic build verification", StringComparison.Ordinal))
            {
                buildState = ExtractText(userMsg.Content);
            }
        }

        if (todoState != null)
        {
            parts.Add($"Task list state:\n{todoState}");
        }

        if (buildState != null)
        {
            string verdict = buildState.Split('\n').FirstOrDefault() ?? "";
            parts.Add($"Build status: {verdict}");
        }

        string summary = string.Join("\n\n", parts);
        if (EstimateTokens(summary) > CompactionSummaryMaxTokens)
        {
            summary = TruncateToolResult(summary, CompactionSummaryMaxTokens);
        }

        return new UserChatMessage(summary);
    }

    private static string? ExtractOriginalTask(List<ChatMessage> messages, ChatMessage? existingSummary)
    {
        if (existingSummary != null)
        {
            string text = ExtractText(existingSummary.Content);
            int idx = text.IndexOf("Original request: ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return text[(idx + "Original request: ".Length)..].Trim();
            }
        }

        foreach (var msg in messages)
        {
            if (msg is not UserChatMessage userMsg || userMsg.Content == null) continue;
            string text = ExtractText(userMsg.Content);
            if (text.Contains("[Session context (older messages trimmed)]"))
            {
                continue;
            }
            if (text.StartsWith("You described the code changes above but did not apply them"))
            {
                continue;
            }
            return text.Trim();
        }

        return null;
    }

    public static string TruncateToolResult(string content, int maxTokens)
    {
        if (string.IsNullOrEmpty(content)) return content;

        int currentTokens = EstimateTokens(content);
        if (currentTokens <= maxTokens) return content;

        int index = GetTokenizer().GetIndexByTokenCount(content, maxTokens, out _, out _,
            considerPreTokenization: false, considerNormalization: false);
        if (index >= content.Length) return content;

        return content.Substring(0, Math.Max(0, index)) + "\n\n... [truncated, content exceeded token limit]";
    }

    public static string ExtractText(ChatMessageContent content)
    {
        var parts = new List<string>();
        for (int i = 0; i < content.Count; i++)
        {
            var part = content[i];
            if (!string.IsNullOrEmpty(part.Text))
            {
                parts.Add(part.Text);
            }
        }
        return string.Join("", parts);
    }
}
