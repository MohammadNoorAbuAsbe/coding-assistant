using System.ClientModel;
using System.Text;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ChatOrchestrator
{
    private static int ToolCallSeq;

    public static async Task Run(ChatSession session, string prompt, CancellationToken cancellationToken = default)
    {
        SessionContext.Current = session;
        try
        {
            await RunCore(session, prompt, cancellationToken);
        }
        finally
        {
            SessionContext.Current = null;
        }
    }

    private static async Task RunCore(ChatSession session, string prompt, CancellationToken cancellationToken)
    {
        Diag.Log("orchestrator:enter prompt=" + (prompt.Length > 60 ? prompt[..60] + "…" : prompt));
        var client = ChatService.CreateClient();
        Diag.Log("orchestrator:client-ok provider=" + Configuration.GetProvider() + " model=" + Configuration.GetModel());
        var options = ToolHandler.CreateCompletionOptions();
        var provider = Configuration.GetProvider();
        var maxIterations = Configuration.GetMaxIterations();
        var contextWindowSize = Configuration.GetContextWindowSize();
        ContextUsageTracker.EnsureModel(Configuration.GetModel());

        if (!session.SessionStarted)
        {
            session.Messages = [new SystemChatMessage(SystemPrompt.GetPrompt(provider))];
            AppUi.Send("meta", new
            {
                provider,
                model = Configuration.GetModel(),
                context = contextWindowSize,
                workspace = System.Environment.CurrentDirectory
            });
            session.SessionStarted = true;
        }

        session.Messages.Add(new UserChatMessage(prompt));
        Diag.Log("orchestrator:pre-loop messages=" + session.Messages.Count);
        await RunAgentLoop(client, session.Messages, options, maxIterations, contextWindowSize, cancellationToken);
        Diag.Log("orchestrator:loop-done");
        session.Messages = await ContextManager.TruncateMessagesAsync(session.Messages, GetMessageBudget(contextWindowSize), cancellationToken);
        Diag.Log("orchestrator:done");
    }

    private static int GetMessageBudget(int contextWindowSize)
    {
        int raw = (int)(contextWindowSize * Configuration.GetContextUsageFraction());
        int adjusted = ContextUsageTracker.GetAdjustedBudget(raw);
        int cap = (int)(contextWindowSize * 0.95);
        return Math.Min(adjusted, cap);
    }

    internal static async Task<string> RunSubAgent(ChatClient client, List<ChatMessage> messages, int? maxIterations, int contextWindowSize, CancellationToken cancellationToken = default)
    {
        var options = ToolHandler.CreateSubAgentCompletionOptions();
        string? finalResponse = null;

        for (int iteration = 0; maxIterations == null || iteration < maxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            AppUi.Send("subagent", new { active = true, iteration = iteration + 1 });

            var (accumulatedToolCalls, responseContent) = await FetchWithEmptyResponseRetryAsync(client, messages, options, cancellationToken);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    messages.Add(new AssistantChatMessage(responseContent));
                    finalResponse = responseContent;
                }
                break;
            }

            messages = await FinalizeToolCallsAsync(accumulatedToolCalls, messages, contextWindowSize, cancellationToken);
        }

        return finalResponse ?? "Sub-agent completed without producing a text response.";
    }

    private static async Task RunAgentLoop(
        ChatClient client,
        List<ChatMessage> messages,
        ChatCompletionOptions options,
        int? maxIterations,
        int contextWindowSize,
        CancellationToken cancellationToken)
    {
        int skippedEditNudges = 0;
        var stallTracker = new StallTracker();
        int stallInterventions = 0;
        int journalCount = SessionContext.Undo.List().Count;
        int turnsSinceFileChange = 0;

        for (int iteration = 0; maxIterations == null || iteration < maxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AppUi.Send("status", new { message = "Operation cancelled" });
                break;
            }

            AppUi.Send("iter", new { n = iteration + 1, max = maxIterations });

            var (accumulatedToolCalls, responseContent) = await FetchWithEmptyResponseRetryAsync(client, messages, options, cancellationToken);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    if (ShouldNudge(responseContent, messages, iteration, maxIterations, skippedEditNudges))
                    {
                        skippedEditNudges++;
                        messages.Add(new AssistantChatMessage(responseContent));
                        messages.Add(new UserChatMessage("You described the code changes above but did not apply them. Execute the necessary Edit or ApplyPatch tool calls now to actually make these changes to the files. Do not repeat the descriptions — just apply them."));
                        continue;
                    }

                    messages.Add(new AssistantChatMessage(responseContent));
                }
                break;
            }

            messages = await FinalizeToolCallsAsync(accumulatedToolCalls, messages, contextWindowSize, cancellationToken);

            if (Autopilot.IsActive)
            {
                int newJournalCount = SessionContext.Undo.List().Count;
                if (newJournalCount != journalCount)
                {
                    journalCount = newJournalCount;
                    turnsSinceFileChange = 0;
                }
                else if (turnsSinceFileChange >= MaxDecisionTurns)
                {
                    turnsSinceFileChange = 0;
                    AppUi.Send("status", new { message = "No changes recently — injecting autopilot directive" });
                    messages.Add(new UserChatMessage(AutopilotSuggestions.BuildDirective()));
                }
                else
                {
                    turnsSinceFileChange++;
                }
            }

            if (stallTracker.Observe(StallDetector.Fingerprint(messages)))
            {
                stallInterventions++;
                if (stallInterventions >= MaxStallInterventions)
                {
                    AppUi.Send("error", new { message = "Loop detected — stopped after repeated identical tool calls" });
                    messages.Add(new UserChatMessage("You are stuck in a repeating tool-call loop and have ignored prior warnings. STOP calling tools. End your turn now with a short summary of what you accomplished (or state that nothing was accomplished)."));
                    break;
                }

                AppUi.Send("status", new { message = "Loop detected — breaking repetition pattern" });
                messages.Add(stallInterventions == 1
                    ? new UserChatMessage("You are repeating the same tool call. Stop this loop immediately: Read the relevant file fresh (a wide range), fix the problem correctly with a single Edit or ApplyPatch, or abandon it and move on to a different task. Do NOT issue this exact tool call again.")
                    : new UserChatMessage("You are STILL repeating the same tool call. This is your final warning: abandon the current approach entirely. If you made changes, summarize them now. If not, pick a completely different improvement and implement it, or end your turn with a summary. Do not repeat any previous tool call."));
            }
        }
    }

    private static async Task<(Dictionary<int, ToolCallAccumulator> ToolCalls, string? Content)> ProcessStreamingUpdates(
        ChatClient client, List<ChatMessage> messages, ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var accumulatedToolCalls = new Dictionary<int, ToolCallAccumulator>();
        string? responseContent = null;
        ChatTokenUsage? usage = null;
        var uiStream = new StringBuilder();
        var uiReasoning = new StringBuilder();
        var lastUiFlush = DateTime.UtcNow;

        try
        {
            await foreach (var update in ChatService.GetCompletionStreaming(client, messages, options, cancellationToken))
            {
                DrainReasoning(uiReasoning);
                ProcessContentUpdate(update.ContentUpdate, ref responseContent, uiStream);
                ProcessToolCallUpdates(update.ToolCallUpdates, accumulatedToolCalls);
                if (update.Usage != null)
                {
                    usage = update.Usage;
                }
                TryFlushUi(uiStream, uiReasoning, ref lastUiFlush);
            }
        }
        catch (ArgumentOutOfRangeException) when (responseContent == null)
        {
            responseContent = "Error: The API returned an unexpected finish reason (possibly content moderation or a rate limit). Rephrase your request and try again.";
            await DisplayErrorAsync(responseContent);
        }
        catch (ClientResultException ex) when (responseContent == null)
        {
            responseContent = FormatApiError(ex);
            await DisplayErrorAsync(responseContent);
        }
        catch (System.IO.IOException) when (responseContent == null)
        {
            responseContent = "Error: The model connection was interrupted (the response ended prematurely). This is usually a provider or network issue — retry the request, possibly with a simpler or shorter prompt.";
            await DisplayErrorAsync(responseContent);
        }
        catch (OperationCanceledException)
        {
            if (responseContent == null)
            {
                responseContent = "The operation was cancelled by the user.";
                await DisplayErrorAsync(responseContent);
            }
        }

        DrainReasoning(uiReasoning);
        FlushUi(uiStream, uiReasoning);

        if (usage != null)
        {
            long estimated = messages.Sum(m => (long)ContextManager.EstimateMessageTokens(m));
            ContextUsageTracker.Record((long)usage.InputTokenCount, estimated);
            ContextUsageTracker.RecordOutputTokens((long)usage.OutputTokenCount);
            AppUi.Send("telemetry", new
            {
                input = usage.InputTokenCount,
                output = usage.OutputTokenCount,
                context = Configuration.GetContextWindowSize()
            });
        }

        if (responseContent == null && accumulatedToolCalls.Count == 0)
        {
            throw new EmptyResponseException();
        }

        return (accumulatedToolCalls, responseContent);
    }

    private const int EmptyResponseMaxRetries = 2;

    private static async Task<(Dictionary<int, ToolCallAccumulator> ToolCalls, string? Content)> FetchWithEmptyResponseRetryAsync(
        ChatClient client, List<ChatMessage> messages, ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await ProcessStreamingUpdates(client, messages, options, cancellationToken);
            }
            catch (EmptyResponseException)
            {
                if (attempt >= EmptyResponseMaxRetries)
                {
                    string error = "Error: The model returned an empty response (no content and no tool calls) after 3 attempts. This often happens with free-tier or rate-limited endpoints — retry, or switch to a different model.";
                    await DisplayErrorAsync(error);
                    return (new Dictionary<int, ToolCallAccumulator>(), error);
                }

                AppUi.Send("status", new { message = $"Empty response — retrying ({attempt + 1}/{EmptyResponseMaxRetries})" });
                try
                {
                    await Task.Delay(1000 * (attempt + 1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return (new Dictionary<int, ToolCallAccumulator>(), null);
                }

                attempt++;
            }
        }
    }

    public sealed class EmptyResponseException : Exception
    {
    }

    private static void ProcessContentUpdate(IList<ChatMessageContentPart>? contentUpdate, ref string? responseContent, StringBuilder uiStream)
    {
        if (contentUpdate == null)
            return;

        foreach (var text in contentUpdate.Where(p => !string.IsNullOrEmpty(p.Text)).Select(p => p.Text))
        {
            responseContent = (responseContent ?? "") + text;
            uiStream.Append(text);
        }
    }

    private static void DrainReasoning(StringBuilder uiReasoning)
    {
        if (!ReasoningTapPolicy.Enabled)
            return;

        while (ReasoningTapPolicy.Pending.TryDequeue(out string? fragment))
        {
            uiReasoning.Append(fragment);
        }
    }

    private const double UiFlushIntervalMs = 60;
    private const int UiFlushMaxChars = 4096;

    /// <summary>
    /// Coalesces the per-token stream/reasoning event flood into batches so
    /// the UI renders in real time instead of drowning in tens of thousands
    /// of tiny messages. Flushes when the interval elapsed or a buffer grew
    /// large enough, whichever comes first.
    /// </summary>
    private static void TryFlushUi(StringBuilder streamBuffer, StringBuilder reasoningBuffer, ref DateTime lastFlush)
    {
        if (streamBuffer.Length == 0 && reasoningBuffer.Length == 0)
            return;

        bool intervalElapsed = (DateTime.UtcNow - lastFlush).TotalMilliseconds >= UiFlushIntervalMs;
        bool large = streamBuffer.Length >= UiFlushMaxChars || reasoningBuffer.Length >= UiFlushMaxChars;
        if (!intervalElapsed && !large)
            return;

        FlushUi(streamBuffer, reasoningBuffer);
        lastFlush = DateTime.UtcNow;
    }

    private static void FlushUi(StringBuilder streamBuffer, StringBuilder reasoningBuffer)
    {
        if (reasoningBuffer.Length > 0)
        {
            AppUi.Send("reasoning", new { text = reasoningBuffer.ToString() });
            reasoningBuffer.Clear();
        }
        if (streamBuffer.Length > 0)
        {
            AppUi.Send("stream", new { text = streamBuffer.ToString() });
            streamBuffer.Clear();
        }
    }

    private static void ProcessToolCallUpdates(IReadOnlyList<StreamingChatToolCallUpdate>? toolCallUpdates, Dictionary<int, ToolCallAccumulator> accumulatedToolCalls)
    {
        if (toolCallUpdates == null)
            return;

        foreach (var toolUpdate in toolCallUpdates)
        {
            int index = toolUpdate.Index;

            if (!accumulatedToolCalls.ContainsKey(index))
                AddNewToolCall(toolUpdate, index, accumulatedToolCalls);

            UpdateExistingToolCall(toolUpdate, accumulatedToolCalls[index], index);
        }
    }

    private static void AddNewToolCall(StreamingChatToolCallUpdate toolUpdate, int index, Dictionary<int, ToolCallAccumulator> accumulatedToolCalls)
    {
        accumulatedToolCalls[index] = new ToolCallAccumulator
        {
            Id = toolUpdate.ToolCallId ?? "",
            UiId = "call-" + Interlocked.Increment(ref ToolCallSeq),
            FunctionName = toolUpdate.FunctionName ?? "",
            ExtraContent = GetToolCallExtraContent(toolUpdate)
        };
        if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
            DisplayToolName(toolUpdate.FunctionName, accumulatedToolCalls[index].UiId);
    }

    private static void UpdateExistingToolCall(StreamingChatToolCallUpdate toolUpdate, ToolCallAccumulator acc, int index)
    {
        var extraContent = GetToolCallExtraContent(toolUpdate);
        if (extraContent != null)
            acc.ExtraContent = extraContent;

        if (!string.IsNullOrEmpty(toolUpdate.ToolCallId))
            acc.Id = toolUpdate.ToolCallId;

        if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
        {
            bool wasEmpty = string.IsNullOrEmpty(acc.FunctionName);
            acc.FunctionName = toolUpdate.FunctionName;
            if (wasEmpty)
                DisplayToolName(toolUpdate.FunctionName, acc.UiId);
        }

        if (toolUpdate.FunctionArgumentsUpdate != null && toolUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
        {
            acc.Arguments += toolUpdate.FunctionArgumentsUpdate.ToString();
            AppUi.Send("tool:args", new { id = acc.UiId, args = toolUpdate.FunctionArgumentsUpdate.ToString() });
        }
    }

    private static void DisplayToolName(string functionName, string uiId)
    {
        AppUi.Send("tool:start", new { id = uiId, name = functionName });
    }

    internal static string? ExtractPrimaryArg(string functionName, string json)
    {
        string? specific = ExtractStringProperty(json, "file_path")
            ?? ExtractStringProperty(json, "pattern")
            ?? ExtractStringProperty(json, "path")
            ?? ExtractStringProperty(json, "url")
            ?? ExtractStringProperty(json, "query")
            ?? ExtractStringProperty(json, "command");
        return specific ?? ExtractFirstStringValue(json);
    }

    internal static string? ExtractFirstStringValue(string json)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, @"""[^""\\]+"":\s*""((?:[^""\\]|\\.)*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string? ExtractStringProperty(string json, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, $@"""{key}"":\s*""((?:[^""\\]|\\.)*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<List<ChatMessage>> FinalizeToolCallsAsync(
        Dictionary<int, ToolCallAccumulator> accumulatedToolCalls,
        List<ChatMessage> messages,
        int contextWindowSize,
        CancellationToken cancellationToken)
    {
        var assistantToolCalls = new List<ChatToolCall>();
        var uiIds = new List<string>();
        foreach (var acc in accumulatedToolCalls.Values)
        {
            var toolCall = ChatToolCall.CreateFunctionToolCall(acc.Id, acc.FunctionName, BinaryData.FromString(acc.Arguments));
            AttachToolCallExtraContent(toolCall, acc.ExtraContent);
            assistantToolCalls.Add(toolCall);
            uiIds.Add(acc.UiId);
        }

        messages.Add(new AssistantChatMessage(assistantToolCalls));

        var toolResultMessages = new List<ChatMessage>();
        for (int i = 0; i < assistantToolCalls.Count; i++)
        {
            var toolCall = assistantToolCalls[i];
            var result = await ResponseHandler.ProcessSingleToolCallAsync(toolCall, cancellationToken);
            if (result != null)
            {
                toolResultMessages.Add(result);
                LogToolResult(toolCall, result, uiIds[i]);
            }
        }

        messages.AddRange(toolResultMessages);

        return await ContextManager.TruncateMessagesAsync(messages, GetMessageBudget(contextWindowSize), cancellationToken);
    }

    private static void LogToolResult(ChatToolCall toolCall, ChatMessage result, string uiId)
    {
        if (result is not ToolChatMessage toolMsg)
            return;

        string content = ContextManager.ExtractText(toolMsg.Content);
        bool isError = content.StartsWith("Error:");
        string fullArgs = toolCall.FunctionArguments?.ToString() ?? "";

        AppUi.Send("tool:end", new
        {
            id = uiId,
            name = toolCall.FunctionName,
            args = fullArgs,
            result = content,
            ok = !isError,
            isDiff = IsDiffContent(content),
            tokens = ContextManager.EstimateTokens(content)
        });

        if (toolCall.FunctionName == ToolHandler.WriteFunctionName ||
            toolCall.FunctionName == ToolHandler.EditFunctionName ||
            toolCall.FunctionName == ToolHandler.ApplyPatchFunctionName ||
            toolCall.FunctionName == ToolHandler.DiffFunctionName)
        {
            AppUi.PublishChanges();
        }
    }

    internal static bool IsDiffContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        return content.Contains("diff --git", StringComparison.OrdinalIgnoreCase)
            || (content.Contains("@@", StringComparison.Ordinal) && content.Contains("+++", StringComparison.Ordinal));
    }

    private static string TruncateForDisplay(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "…";
    }

#pragma warning disable SCME0001
    private static BinaryData? GetToolCallExtraContent(StreamingChatToolCallUpdate toolUpdate)
    {
        return toolUpdate.Patch.Contains("$.extra_content"u8)
            ? toolUpdate.Patch.GetJson("$.extra_content"u8)
            : null;
    }

    private static void AttachToolCallExtraContent(ChatToolCall toolCall, BinaryData? extraContent)
    {
        if (extraContent != null)
            toolCall.Patch.Set("$.extra_content"u8, extraContent);
    }
#pragma warning restore SCME0001

    private static async Task DisplayErrorAsync(string message)
    {
        AppUi.Send("error", new { message });
    }

    private static string FormatApiError(ClientResultException ex)
    {
        string detail = "";
        string? errorMessage = null;
        try
        {
            var raw = ex.GetRawResponse();
            if (raw?.Content != null)
            {
                string body = raw.Content.ToString();
                if (body.Length > 0)
                {
                    errorMessage = ExtractJsonString(body, "message");
                    detail = $" Details: {TruncateForDisplay(body, 400)}";
                }
            }
        }
        catch (Exception exRaw)
        {
            return $"Error: {exRaw}.";
        }

        if (ex.Status == 429 || (errorMessage?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false))
            return $"Error: The API rate limit was exceeded (HTTP 429).{(errorMessage != null ? $" {errorMessage}" : "")} Wait for the reset window or add credits/upgrade the plan, then retry.";

        if (ex.Status == 401)
            return $"Error: The API key was rejected (HTTP 401).{(errorMessage != null ? $" {errorMessage}" : "")} Check the API key in your configuration and retry.";

        if (ex.Status == 404)
            return $"Error: The model or endpoint was not found (HTTP 404).{(errorMessage != null ? $" {errorMessage}" : "")} Verify the model name in your configuration and retry.";

        return $"Error: The API request failed with HTTP {ex.Status} (Bad Request).{detail} This is often caused by the model rejecting the message history, tool calls, or content policy. Simplify your approach, retry without tools, or switch to a different model.";
    }

    private static string? ExtractJsonString(string json, string propertyName)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == System.Text.Json.JsonValueKind.Object &&
                error.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (System.Exception ex)
        {
            Diag.Log($"Failed to parse error message from API response: {ex.Message}");
        }
        return null;
    }

    private const int MaxSkippedEditNudges = 2;
    private const int MaxStallInterventions = 3;
    private const int MaxDecisionTurns = 12;

    private static bool PreviousTurnHadToolCalls(List<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantChatMessage assistant)
            {
                return assistant.ToolCalls != null && assistant.ToolCalls.Count > 0;
            }
        }
        return false;
    }

    private static bool LooksLikeSkippedEdit(string response)
    {
        if (!response.Contains("```")) return false;

        bool mentionsSourceFile = response.Contains(".cs") ||
            response.Contains(".py") ||
            response.Contains(".js") ||
            response.Contains(".ts") ||
            response.Contains(".csproj") ||
            response.Contains(".json");

        return mentionsSourceFile;
    }

    private static bool ShouldNudge(string responseContent, List<ChatMessage> messages, int iteration, int? maxIterations, int skippedEditNudges)
    {
        return LooksLikeSkippedEdit(responseContent)
            && (maxIterations == null || iteration < maxIterations - 1)
            && !UserRequestedPreviewOnly(messages)
            && skippedEditNudges < MaxSkippedEditNudges
            && !PreviousTurnHadToolCalls(messages);
    }

    private static bool UserRequestedPreviewOnly(List<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => m is UserChatMessage);
        if (lastUser is not UserChatMessage userMsg) return false;

        string text = ContextManager.ExtractText(userMsg.Content);
        return text.Contains("do not apply", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("don't apply", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("do not modify", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("don't modify", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("do not edit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("don't edit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("do not change", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("don't change", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("without writing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("without modifying", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("without changing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("dry run", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("plan only", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("just show", StringComparison.OrdinalIgnoreCase);
    }
}

public class ToolCallAccumulator
{
    public string Id { get; set; } = "";
    public string UiId { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public BinaryData? ExtraContent { get; set; }
    public bool ArgsLogged { get; set; }
}

internal static class StallDetector
{
    internal static string? Fingerprint(List<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantChatMessage assistant &&
                assistant.ToolCalls != null &&
                assistant.ToolCalls.Count > 0)
            {
                var call = assistant.ToolCalls[0];
                string args = call.FunctionArguments?.ToString() ?? "";
                string callKey = BuildCallKey(call.FunctionName, args);
                string resultKey = FindResultKey(messages, i);
                return $"{callKey}|{resultKey}";
            }
        }
        return null;
    }

    private static string BuildCallKey(string functionName, string args)
    {
        string primary = ChatOrchestrator.ExtractPrimaryArg(functionName, args) ?? "?";

        if (functionName == ToolHandler.ReadFunctionName)
        {
            return $"Read|{primary}|{ReadRange(args)}";
        }

        if (functionName == ToolHandler.GrepFunctionName)
        {
            string? include = ChatOrchestrator.ExtractStringProperty(args, "include");
            string? path = ChatOrchestrator.ExtractStringProperty(args, "path");
            return $"Grep|{primary}|{include}|{path}";
        }

        if (functionName == ToolHandler.EditFunctionName)
        {
            string? oldString = ChatOrchestrator.ExtractStringProperty(args, "old_string");
            string oldKey = oldString == null ? "?" : HashText(oldString);
            return $"Edit|{primary}|{oldKey}";
        }

        if (functionName == ToolHandler.ApplyPatchFunctionName)
        {
            string? oldString = ChatOrchestrator.ExtractStringProperty(args, "old_string");
            string oldKey = oldString == null ? "?" : HashText(oldString);
            return $"ApplyPatch|{primary}|{oldKey}";
        }

        return $"{functionName}|{primary}";
    }

    private static string ReadRange(string args)
    {
        string? start = ChatOrchestrator.ExtractStringProperty(args, "start_line");
        string? end = ChatOrchestrator.ExtractStringProperty(args, "end_line");
        return $"{(string.IsNullOrEmpty(start) ? "?" : start)}-{(string.IsNullOrEmpty(end) ? "?" : end)}";
    }

    private static string FindResultKey(List<ChatMessage> messages, int toolCallIndex)
    {
        for (int i = toolCallIndex + 1; i < messages.Count; i++)
        {
            if (messages[i] is AssistantChatMessage)
                break;
            if (messages[i] is ToolChatMessage result)
            {
                string text = ContextManager.ExtractText(result.Content);
                return text.Length == 0 ? "empty" : HashText(text);
            }
        }
        return "?";
    }

    private static string HashText(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..12];
    }
}

internal sealed class StallTracker
{
    internal const int DefaultWindowSize = 5;
    internal const int DefaultMaxRepeats = 3;

    private readonly List<string> _recent = new(DefaultWindowSize);
    private readonly int _windowSize;
    private readonly int _maxRepeats;

    public StallTracker(int windowSize = DefaultWindowSize, int maxRepeats = DefaultMaxRepeats)
    {
        _windowSize = windowSize;
        _maxRepeats = maxRepeats;
    }

    public bool Observe(string? fingerprint)
    {
        if (fingerprint == null)
        {
            _recent.Clear();
            return false;
        }

        _recent.Add(fingerprint);
        if (_recent.Count > _windowSize)
        {
            _recent.RemoveAt(0);
        }

        int count = 0;
        foreach (var f in _recent)
        {
            if (f == fingerprint) count++;
        }

        if (count >= _maxRepeats)
        {
            _recent.Clear();
            return true;
        }

        return false;
    }
}
