using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ContextManager
{
    internal const string SummaryMarker = "[Session context (older messages trimmed)]";

    private const int CompactionSummaryMaxTokens = 800;

    // ---- Token estimation -------------------------------------------------

    public static int EstimateTokens(string? text) => TokenEstimator.Estimate(text);

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

    /// <summary>
    /// Returns the character index in <paramref name="text"/> where the token
    /// count first exceeds <paramref name="maxTokens"/>, or the full length if
    /// the text fits within the budget.
    /// </summary>
    public static int GetTokenLimitIndex(string text, int maxTokens)
    {
        return TokenEstimator.GetIndexByTokenCount(text, maxTokens);
    }

    // ---- Compaction -------------------------------------------------------

    /// <summary>
    /// Test seam: replaces the default LLM summarizer. The delegate receives
    /// the dropped messages (chronological), the previous summary message (or
    /// null), and a cancellation token; it returns summary text or null to
    /// trigger the template fallback.
    /// </summary>
    internal static Func<List<ChatMessage>, ChatMessage?, CancellationToken, Task<string?>>? SummarizerOverride;

    /// <summary>
    /// Truncates message history to fit <paramref name="maxTokens"/> using the
    /// deterministic template summary. Kept for tests and as the conservative
    /// path; production callers use TruncateMessagesAsync.
    /// </summary>
    public static List<ChatMessage> TruncateMessages(List<ChatMessage> messages, int maxTokens)
    {
        return TruncateMessagesCore(messages, maxTokens);
    }

    /// <summary>
    /// Truncates message history to fit <paramref name="maxTokens"/> with an
    /// LLM-generated summary of the dropped turns (when enabled). Falls back
    /// to the template summary on any failure or when LLM_COMPACTION=0.
    /// </summary>
    public static async Task<List<ChatMessage>> TruncateMessagesAsync(
        List<ChatMessage> messages, int maxTokens, CancellationToken cancellationToken = default)
    {
        if (!Configuration.GetLlmCompactionEnabled())
        {
            return TruncateMessagesCore(messages, maxTokens);
        }

        var summarize = SummarizerOverride ?? LlmCompactionSummarizer.SummarizeAsync;

        var rounds = BuildRounds(messages, out var existingSummary);
        var plan = PlanKeepSet(rounds, maxTokens);
        if (plan.NothingDropped)
        {
            return messages;
        }

        string? summaryText;
        try
        {
            summaryText = await summarize(plan.DroppedMessages, existingSummary, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            summaryText = null;
        }

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            summaryText = BuildTemplateSummary(messages, existingSummary);
        }

        var summary = CreateSummaryMessage(summaryText, plan.DroppedReadFiles);
        return Assemble(plan, summary, maxTokens);
    }

    private static List<ChatMessage> TruncateMessagesCore(List<ChatMessage> messages, int maxTokens)
    {
        var rounds = BuildRounds(messages, out var existingSummary);
        var plan = PlanKeepSet(rounds, maxTokens);
        if (plan.NothingDropped)
        {
            return messages;
        }

        string summaryText = BuildTemplateSummary(messages, existingSummary);
        var summary = CreateSummaryMessage(summaryText, plan.DroppedReadFiles);
        return Assemble(plan, summary, maxTokens);
    }

    private static ChatMessage? CreateSummaryMessage(string summaryText, List<string> droppedReadFiles)
    {
        if (string.IsNullOrWhiteSpace(summaryText)) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append(SummaryMarker);
        sb.Append(" — earlier conversation was dropped to fit the context window.");

        if (droppedReadFiles.Count > 0)
        {
            sb.AppendLine();
            sb.Append("Earlier Read results dropped: ");
            sb.Append(string.Join(", ", droppedReadFiles.Distinct().Take(10)));
            sb.Append(" — re-Read these files if you need their contents again.");
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(summaryText);

        int maxTokens = Math.Max(CompactionSummaryMaxTokens, Configuration.GetMaxCompactionTokens());
        return new UserChatMessage(TruncateToolResult(sb.ToString(), maxTokens));
    }

    private static List<ChatMessage> Assemble(KeepPlan plan, ChatMessage? summary, int maxTokens)
    {
        var result = plan.KeptRounds
            .OrderBy(r => r.Index)
            .SelectMany(r => r.Messages)
            .ToList();

        if (summary != null)
        {
            int keptTokens = plan.KeptRounds.Sum(r => r.Tokens);
            if (EstimateMessageTokens(summary) <= maxTokens - keptTokens)
            {
                int insertPos = result.TakeWhile(m => m is SystemChatMessage).Count();
                result.Insert(insertPos, summary);
            }
        }

        return result;
    }

    private static string BuildTemplateSummary(List<ChatMessage> messages, ChatMessage? existingSummary)
    {
        var parts = new List<string>();

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

        return summary;
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
            if (text.Contains(SummaryMarker))
            {
                continue;
            }
            if (text.StartsWith("You described the code changes above but did not apply them"))
            {
                continue;
            }
            return text.Trim();
        }

        // No ordinary user message survived (e.g. the entire early history is a
        // marker from a previously LLM-compressed session). Fall back to the
        // first marker message so the original request survives another round.
        foreach (var msg in messages)
        {
            if (msg is not UserChatMessage userMsg || userMsg.Content == null) continue;
            string text = ExtractText(userMsg.Content);
            if (text.Contains(SummaryMarker))
            {
                return text.Length > 800 ? text[..800] + "…" : text;
            }
        }

        return null;
    }

    // ---- Round-based eviction ---------------------------------------------

    private sealed class Round
    {
        public int Index;
        public readonly List<ChatMessage> Messages = new();
        public bool IsSystem;
        public bool IsToolRound;
        public bool IsReadRound;
        public int Tokens;
    }

    private sealed class KeepPlan
    {
        public readonly List<Round> KeptRounds = new();
        public readonly List<Round> DroppedRounds = new();
        public readonly List<ChatMessage> DroppedMessages = new();
        public readonly List<string> DroppedReadFiles = new();
        public bool NothingDropped;
    }

    /// <summary>
    /// Splits history into atomic rounds. An assistant tool-call message and
    /// its tool results always belong to the same round, so trimming can never
    /// orphan a tool result from its preceding assistant message (which
    /// providers reject with HTTP 400).
    /// </summary>
    private static List<Round> BuildRounds(List<ChatMessage> messages, out ChatMessage? existingSummary)
    {
        existingSummary = messages.FirstOrDefault(m =>
            m is UserChatMessage && ExtractText(m.Content).Contains(SummaryMarker));

        var rounds = new List<Round>();
        int index = 0;

        foreach (var msg in messages)
        {
            if (existingSummary != null && ReferenceEquals(msg, existingSummary))
            {
                continue;
            }

            if (msg is SystemChatMessage)
            {
                rounds.Add(new Round { Index = index++, IsSystem = true, Messages = { msg } });
            }
            else if (msg is AssistantChatMessage assistant && assistant.ToolCalls is { Count: > 0 })
            {
                var round = new Round { Index = index++, IsToolRound = true, Messages = { msg } };
                round.IsReadRound = assistant.ToolCalls.All(tc => tc.FunctionName == ToolHandler.ReadFunctionName);
                rounds.Add(round);
            }
            else if (msg is ToolChatMessage && rounds.Count > 0 && rounds[^1].IsToolRound)
            {
                // Tool results join the assistant round that produced them.
                rounds[^1].Messages.Add(msg);
            }
            else
            {
                rounds.Add(new Round { Index = index++, Messages = { msg } });
            }
        }

        foreach (var round in rounds)
        {
            round.Tokens = round.Messages.Sum(EstimateMessageTokens);
        }

        return rounds;
    }

    /// <summary>
    /// Decides what survives. System rounds are always kept. Conversation
    /// rounds (user/assistant reasoning, non-read tool rounds) are kept from
    /// the newest backwards while they fit. Read rounds are deferred and
    /// re-added last: large, re-fetchable file contents are the first thing
    /// evicted under pressure, and read rounds survive only when conversation
    /// history did not consume the whole budget.
    /// </summary>
    private static KeepPlan PlanKeepSet(List<Round> rounds, int maxTokens)
    {
        var plan = new KeepPlan();
        int total = 0;

        var rest = new List<Round>();
        foreach (var round in rounds)
        {
            if (round.IsSystem)
            {
                plan.KeptRounds.Add(round);
                total += round.Tokens;
            }
            else
            {
                rest.Add(round);
            }
        }

        var skippedReads = new List<Round>();

        for (int i = rest.Count - 1; i >= 0; i--)
        {
            var round = rest[i];
            if (round.IsReadRound)
            {
                skippedReads.Insert(0, round);
                continue;
            }

            if (total + round.Tokens > maxTokens)
            {
                for (int j = i; j >= 0; j--)
                {
                    plan.DroppedRounds.Add(rest[j]);
                }
                break;
            }

            plan.KeptRounds.Add(round);
            total += round.Tokens;
        }

        foreach (var round in skippedReads)
        {
            if (total + round.Tokens <= maxTokens)
            {
                plan.KeptRounds.Add(round);
                total += round.Tokens;
            }
            else
            {
                plan.DroppedRounds.Add(round);
            }
        }

        plan.NothingDropped = plan.DroppedRounds.Count == 0;
        if (!plan.NothingDropped)
        {
            foreach (var round in plan.DroppedRounds.OrderBy(r => r.Index))
            {
                plan.DroppedMessages.AddRange(round.Messages);
                CollectDroppedReadFiles(round, plan.DroppedReadFiles);
            }
        }

        return plan;
    }

    private static void CollectDroppedReadFiles(Round round, List<string> files)
    {
        if (!round.IsReadRound) return;

        foreach (var msg in round.Messages)
        {
            if (msg is not AssistantChatMessage assistant || assistant.ToolCalls == null) continue;
            foreach (var toolCall in assistant.ToolCalls)
            {
                string? path = ExtractFilePath(toolCall);
                if (path != null) files.Add(path);
            }
        }
    }

    private static string? ExtractFilePath(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null) return null;
        string raw = toolCall.FunctionArguments.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(raw, "\"file_path\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ---- Tool result truncation -------------------------------------------

    public static string TruncateToolResult(string content, int maxTokens)
    {
        if (string.IsNullOrEmpty(content)) return content;

        int currentTokens = EstimateTokens(content);
        if (currentTokens <= maxTokens) return content;

        int index = TokenEstimator.GetIndexByTokenCount(content, maxTokens);
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
