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
