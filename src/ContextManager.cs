using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ContextManager
{
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (text.Length + 3) / 4;
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

        return result;
    }

    public static string TruncateToolResult(string content, int maxTokens)
    {
        if (string.IsNullOrEmpty(content)) return content;

        int currentTokens = EstimateTokens(content);
        if (currentTokens <= maxTokens) return content;

        int maxChars = maxTokens * 4;
        return content.Substring(0, maxChars) + "\n\n... [truncated, content exceeded token limit]";
    }

    private static string ExtractText(ChatMessageContent content)
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
