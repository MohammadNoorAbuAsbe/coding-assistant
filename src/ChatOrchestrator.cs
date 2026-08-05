using System.ClientModel;
using System.Text;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ChatOrchestrator
{
    public static async Task Run(ChatSession session, string prompt, CancellationToken cancellationToken = default)
    {
        var client = ChatService.CreateClient();
        var options = ToolHandler.CreateCompletionOptions();
        var provider = Configuration.GetProvider();
        var maxIterations = Configuration.GetMaxIterations();
        var contextWindowSize = Configuration.GetContextWindowSize();

        if (!session.SessionStarted)
        {
            session.Messages = [new SystemChatMessage(SystemPrompt.GetPrompt(provider))];
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
            {
                var ctxSource = Configuration.GetContextWindowSource();
                var ctxLabel = ctxSource == null ? $"{contextWindowSize}" : $"{contextWindowSize} ({ctxSource})";
                await Console.Error.WriteLineAsync($"{provider} · {Configuration.GetModel()} · ctx={ctxLabel} · max_iter={maxIterations?.ToString() ?? "unlimited"}");
            }
            session.SessionStarted = true;
        }

        session.Messages.Add(new UserChatMessage(prompt));
        await RunAgentLoop(client, session.Messages, options, maxIterations, contextWindowSize, cancellationToken);
        session.Messages = ContextManager.TruncateMessages(session.Messages, contextWindowSize);
    }

    internal static async Task<string> RunSubAgent(ChatClient client, List<ChatMessage> messages, int? maxIterations, int contextWindowSize, CancellationToken cancellationToken = default)
    {
        var options = ToolHandler.CreateSubAgentCompletionOptions();
        string? finalResponse = null;

        for (int iteration = 0; maxIterations == null || iteration < maxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync(maxIterations == null
                    ? $"  [sub-agent iteration {iteration + 1}]"
                    : $"  [sub-agent {iteration + 1}/{maxIterations}]");

            var (accumulatedToolCalls, responseContent) = await FetchWithEmptyResponseRetryAsync(client, messages, options, cancellationToken);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    Console.WriteLine();
                    messages.Add(new AssistantChatMessage(responseContent));
                    finalResponse = responseContent;
                }
                break;
            }

            await Console.Error.WriteLineAsync();
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

        for (int iteration = 0; maxIterations == null || iteration < maxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                    await Console.Error.WriteLineAsync("\n[Cancelled]");
                break;
            }

            using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
                await Console.Error.WriteAsync(maxIterations == null ? $"[{iteration + 1}]" : $"[{iteration + 1}/{maxIterations}]");
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync(" Thinking...");

            var (accumulatedToolCalls, responseContent) = await FetchWithEmptyResponseRetryAsync(client, messages, options, cancellationToken);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    Console.WriteLine();

                    if (LooksLikeSkippedEdit(responseContent)
                        && (maxIterations == null || iteration < maxIterations - 1)
                        && !UserRequestedPreviewOnly(messages)
                        && skippedEditNudges < MaxSkippedEditNudges
                        && !PreviousTurnHadToolCalls(messages))
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

            await Console.Error.WriteLineAsync();
            messages = await FinalizeToolCallsAsync(accumulatedToolCalls, messages, contextWindowSize, cancellationToken);
        }
    }

    private static async Task<(Dictionary<int, ToolCallAccumulator> ToolCalls, string? Content)> ProcessStreamingUpdates(
        ChatClient client, List<ChatMessage> messages, ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var accumulatedToolCalls = new Dictionary<int, ToolCallAccumulator>();
        string? responseContent = null;
        var lineBuffer = new StringBuilder();
        bool inCodeBlock = false;
        bool reasoningOnLine = false;

        try
        {
            await foreach (var update in ChatService.GetCompletionStreaming(client, messages, options, cancellationToken))
            {
                DrainReasoning(ref reasoningOnLine);
                ProcessContentUpdate(update.ContentUpdate, ref responseContent, lineBuffer, ref inCodeBlock, ref reasoningOnLine);
                ProcessToolCallUpdates(update.ToolCallUpdates, accumulatedToolCalls);
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

        DrainReasoning(ref reasoningOnLine);

        if (responseContent == null && accumulatedToolCalls.Count == 0)
        {
            throw new EmptyResponseException();
        }

        if (lineBuffer.Length > 0)
        {
            string remaining = lineBuffer.ToString();
            string rendered = AnsiRenderer.Render(remaining, ref inCodeBlock);
            Console.Write(rendered);
            await Console.Out.FlushAsync(cancellationToken);
            lineBuffer.Clear();
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

                using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                    await Console.Error.WriteLineAsync($"    [empty response — retrying ({attempt + 1}/{EmptyResponseMaxRetries})...]");
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

    private static void ProcessContentUpdate(IList<ChatMessageContentPart>? contentUpdate, ref string? responseContent, StringBuilder lineBuffer, ref bool inCodeBlock, ref bool reasoningOnLine)
    {
        if (contentUpdate == null)
            return;

        foreach (var text in contentUpdate.Where(p => !string.IsNullOrEmpty(p.Text)).Select(p => p.Text))
        {
            if (reasoningOnLine)
            {
                reasoningOnLine = false;
                Console.Error.WriteLine();
                Console.Error.Flush();
            }

            responseContent = (responseContent ?? "") + text;
            lineBuffer.Append(text);

            string buf = lineBuffer.ToString();
            int lastNewline = buf.LastIndexOf('\n');
            if (lastNewline >= 0)
            {
                string completeLines = buf[..lastNewline];
                lineBuffer.Clear();
                lineBuffer.Append(buf[(lastNewline + 1)..]);

                foreach (var line in completeLines.Split('\n'))
                {
                    string rendered = AnsiRenderer.Render(line, ref inCodeBlock);
                    Console.WriteLine(rendered);
                }
                Console.Out.Flush();
            }
        }
    }

    private static void DrainReasoning(ref bool reasoningOnLine)
    {
        if (!ReasoningTapPolicy.Enabled)
            return;

        while (ReasoningTapPolicy.Pending.TryDequeue(out string? fragment))
        {
            if (!reasoningOnLine)
            {
                using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                    Console.Error.Write("┆ ");
                reasoningOnLine = true;
            }
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                Console.Error.Write(fragment);
        }
        if (reasoningOnLine)
            Console.Error.Flush();
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

            UpdateExistingToolCall(toolUpdate, accumulatedToolCalls[index]);
        }
    }

    private static void AddNewToolCall(StreamingChatToolCallUpdate toolUpdate, int index, Dictionary<int, ToolCallAccumulator> accumulatedToolCalls)
    {
        accumulatedToolCalls[index] = new ToolCallAccumulator
        {
            Id = toolUpdate.ToolCallId ?? "",
            FunctionName = toolUpdate.FunctionName ?? "",
            ExtraContent = GetToolCallExtraContent(toolUpdate)
        };
        if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
            DisplayToolName(toolUpdate.FunctionName);
    }

    private static void UpdateExistingToolCall(StreamingChatToolCallUpdate toolUpdate, ToolCallAccumulator acc)
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
                DisplayToolName(toolUpdate.FunctionName);
        }

        if (toolUpdate.FunctionArgumentsUpdate != null && toolUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
        {
            acc.Arguments += toolUpdate.FunctionArgumentsUpdate.ToString();
            if (!acc.ArgDisplayed)
            {
                string? primaryArg = ExtractFirstStringValue(acc.Arguments);
                if (primaryArg != null)
                {
                    acc.ArgDisplayed = true;
                    Console.Error.Write(primaryArg);
                    Console.Error.Flush();
                }
            }
        }
        if (Environment.GetEnvironmentVariable("VERBOSE_TOOLS") == "1" &&
            !string.IsNullOrEmpty(acc.Arguments) &&
            !acc.ArgsLogged)
        {
            acc.ArgsLogged = true;
            Console.Error.WriteLine($"\n[v] {acc.FunctionName}: {acc.Arguments}");
            Console.Error.Flush();
        }
    }

    private static void DisplayToolName(string functionName)
    {
        using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
            Console.Error.Write($"\n[Tool: ");
        using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
            Console.Error.Write($"{functionName}");
        using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
            Console.Error.Write($"] ");
    }

    private static string? ExtractFirstStringValue(string json)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, @"""[^""\\]+"":\s*""((?:[^""\\]|\\.)*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<List<ChatMessage>> FinalizeToolCallsAsync(
        Dictionary<int, ToolCallAccumulator> accumulatedToolCalls,
        List<ChatMessage> messages,
        int contextWindowSize,
        CancellationToken cancellationToken)
    {
        var assistantToolCalls = accumulatedToolCalls.Values
            .Select(acc =>
            {
                var toolCall = ChatToolCall.CreateFunctionToolCall(acc.Id, acc.FunctionName, BinaryData.FromString(acc.Arguments));
                AttachToolCallExtraContent(toolCall, acc.ExtraContent);
                return toolCall;
            })
            .ToList();

        messages.Add(new AssistantChatMessage(assistantToolCalls));

        using (ConsoleStyler.WithColor(ConsoleColor.Blue))
            await Console.Error.WriteLineAsync("\n— Results —");
        var toolResultMessages = new List<ChatMessage>();
        bool fileModified = false;
        foreach (var toolCall in assistantToolCalls)
        {
            var result = await ResponseHandler.ProcessSingleToolCallAsync(toolCall, cancellationToken);
            if (result != null)
            {
                toolResultMessages.Add(result);
                LogToolResult(toolCall, result);
            }

            if (BuildVerifier.IsFileModifyingFunction(toolCall.FunctionName)
                && result != null
                && !ContextManager.ExtractText(result.Content).StartsWith("Error:"))
            {
                fileModified = true;
            }
        }

        messages.AddRange(toolResultMessages);

        if (fileModified && Configuration.GetAutoVerify() && !cancellationToken.IsCancellationRequested)
        {
            if (BuildVerifier.HasDotnetProject())
            {
                using (ConsoleStyler.WithColor(ConsoleColor.Blue))
                    await Console.Error.WriteLineAsync("\n— Build Verification —");
                var verifyMessage = await BuildVerifier.RunAsync(cancellationToken);
                messages.Add(verifyMessage);
                LogBuildVerification(verifyMessage);
            }
            else
            {
                using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                    await Console.Error.WriteLineAsync("  Build verification skipped: no .sln or .csproj found in workspace (set AUTO_VERIFY=false to disable).");
            }
        }

        return ContextManager.TruncateMessages(messages, contextWindowSize);
    }

    private static void LogBuildVerification(ChatMessage message)
    {
        if (message is not UserChatMessage userMsg)
            return;

        string content = ContextManager.ExtractText(userMsg.Content);
        bool succeeded = content.Contains("Build succeeded");
        string symbol = succeeded ? "✓" : "✗";
        using (ConsoleStyler.WithColor(succeeded ? ConsoleColor.Green : ConsoleColor.Red))
            Console.Error.WriteLine($"  {symbol} Auto-verify: {(succeeded ? "build passed" : "build failed")}");
    }

    private static void LogToolResult(ChatToolCall toolCall, ChatMessage result)
    {
        if (result is not ToolChatMessage toolMsg)
            return;

        string content = ContextManager.ExtractText(toolMsg.Content);
        bool isError = content.StartsWith("Error:");
        string symbol = isError ? "✗" : "✓";
        string? primaryArg = ExtractFirstStringValue(toolCall.FunctionArguments?.ToString() ?? "");

        if (!isError)
        {
            string tokensStr = $"{ContextManager.EstimateTokens(content):N0}";
            string argPart = !string.IsNullOrEmpty(primaryArg) ? $" — {TruncateForDisplay(primaryArg, 80)}" : "";
            using (ConsoleStyler.WithColor(ConsoleColor.Green))
                Console.Error.Write($"  {symbol} ");
            using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
                Console.Error.Write(toolCall.FunctionName);
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                Console.Error.WriteLine($" ({tokensStr} tok){argPart}");
        }
        else
        {
            using (ConsoleStyler.WithColor(ConsoleColor.Red))
                Console.Error.WriteLine($"  {symbol} {toolCall.FunctionName} → {TruncateForDisplay(content, 80)}");
        }
    }

    private static string TruncateForDisplay(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "…";
    }

    // Gemini 3.x thinking models attach an opaque thought_signature to each tool call
    // (serialized as tool_calls[].extra_content.google.thought_signature in the OpenAI-compat
    // layer). The signature must be echoed back on the next request or Gemini rejects the
    // message history with HTTP 400. The OpenAI SDK preserves unknown fields in JsonPatch;
    // we lift the raw extra_content JSON from the streaming update and re-attach it to the
    // rebuilt ChatToolCall so it is replayed verbatim.
    // Gemini 3.x thinking models attach an opaque thought_signature to each tool call
    // (tool_calls[].extra_content.google.thought_signature in the OpenAI-compat layer) and
    // require it to be echoed back, or they reject the history with HTTP 400. The SDK keeps
    // unknown fields in JsonPatch; lift the raw extra_content JSON from the streaming update
    // and re-attach it to the rebuilt ChatToolCall so it is replayed verbatim.
#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
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
        await Console.Out.WriteLineAsync();
        using (ConsoleStyler.WithColor(ConsoleColor.Red))
            await Console.Error.WriteLineAsync(message);
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
            Console.Error.WriteLine($"Failed to parse error message from API response: {ex.Message}");
        }
        return null;
    }

    private const int MaxSkippedEditNudges = 2;

    // Only nudge when the model's previous turn was text-only. If it already
    // executed tool calls, a fenced before/after block in the summary is a
    // legitimate diff display — not a skipped edit — and nudging it would put
    // the model in an unbreakable loop.
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
    public string FunctionName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public BinaryData? ExtraContent { get; set; }
    public bool ArgDisplayed { get; set; }
    public bool ArgsLogged { get; set; }
}
