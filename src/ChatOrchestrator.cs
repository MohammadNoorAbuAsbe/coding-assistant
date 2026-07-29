using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ChatOrchestrator
{
    private static List<ChatMessage>? _messages;
    private static bool _sessionStarted;

    public static async Task Run(string prompt)
    {
        var client = ChatService.CreateClient();
        var options = ToolHandler.CreateCompletionOptions();
        var provider = Configuration.GetProvider();
        var maxIterations = Configuration.GetMaxIterations();
        var contextWindowSize = Configuration.GetContextWindowSize();

        if (!_sessionStarted)
        {
            _messages = [new SystemChatMessage(SystemPrompt.GetPrompt(provider))];
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                Console.Error.WriteLine($"{provider} · {Configuration.GetModel()} · ctx={contextWindowSize} · max_iter={maxIterations}");
            _sessionStarted = true;
        }

        _messages!.Add(new UserChatMessage(prompt));
        await RunAgentLoop(client, _messages, options, provider, maxIterations, contextWindowSize);
        _messages = ContextManager.TruncateMessages(_messages, contextWindowSize);
    }

    internal static async Task<string> RunSubAgent(ChatClient client, List<ChatMessage> messages, int maxIterations, int contextWindowSize)
    {
        var options = ToolHandler.CreateSubAgentCompletionOptions();
        string? finalResponse = null;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                Console.Error.WriteLine($"  [sub-agent {iteration + 1}/{maxIterations}]");

            var (accumulatedToolCalls, responseContent) = await ProcessStreamingUpdates(client, messages, options);

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
            messages = FinalizeToolCalls(accumulatedToolCalls, messages, contextWindowSize);
        }

        messages = ContextManager.TruncateMessages(messages, contextWindowSize);
        return finalResponse ?? "Sub-agent completed without producing a text response.";
    }

    private static async Task RunAgentLoop(
        ChatClient client,
        List<ChatMessage> messages,
        ChatCompletionOptions options,
        string provider,
        int maxIterations,
        int contextWindowSize)
    {
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
                Console.Error.Write($"[{iteration + 1}/{maxIterations}]");
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                Console.Error.WriteLine(" Thinking...");

            var (accumulatedToolCalls, responseContent) = await ProcessStreamingUpdates(client, messages, options);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    Console.WriteLine();

                    if (LooksLikeSkippedEdit(responseContent) && iteration < maxIterations - 1)
                    {
                        messages.Add(new AssistantChatMessage(responseContent));
                        messages.Add(new UserChatMessage("You described the code changes above but did not apply them. Execute the necessary Edit or EditLine tool calls now to actually make these changes to the files. Do not repeat the descriptions — just apply them."));
                        continue;
                    }

                    messages.Add(new AssistantChatMessage(responseContent));
                }
                break;
            }

            await Console.Error.WriteLineAsync();
            messages = FinalizeToolCalls(accumulatedToolCalls, messages, contextWindowSize);
        }
    }

    private static async Task<(Dictionary<int, ToolCallAccumulator> ToolCalls, string? Content)> ProcessStreamingUpdates(
        ChatClient client, List<ChatMessage> messages, ChatCompletionOptions options)
    {
        var accumulatedToolCalls = new Dictionary<int, ToolCallAccumulator>();
        string? responseContent = null;

        await foreach (var update in ChatService.GetCompletionStreaming(client, messages, options))
        {
            ProcessContentUpdate(update.ContentUpdate, ref responseContent);
            ProcessToolCallUpdates(update.ToolCallUpdates, accumulatedToolCalls);
        }

        return (accumulatedToolCalls, responseContent);
    }

    private static void ProcessContentUpdate(IList<ChatMessageContentPart>? contentUpdate, ref string? responseContent)
    {
        if (contentUpdate == null)
            return;

        foreach (var text in contentUpdate.Where(p => !string.IsNullOrEmpty(p.Text)).Select(p => p.Text))
        {
            Console.Write(text);
            Console.Out.Flush();
            responseContent = (responseContent ?? "") + text;
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
            {
                accumulatedToolCalls[index] = new ToolCallAccumulator
                {
                    Id = toolUpdate.ToolCallId ?? "",
                    FunctionName = toolUpdate.FunctionName ?? ""
                };
                if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
                        Console.Error.Write($"\n[Tool: ");
                    using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
                        Console.Error.Write($"{toolUpdate.FunctionName}");
                    using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
                        Console.Error.Write($"] ");
                }
            }

            var acc = accumulatedToolCalls[index];
            if (!string.IsNullOrEmpty(toolUpdate.ToolCallId)) acc.Id = toolUpdate.ToolCallId;
            if (!string.IsNullOrEmpty(toolUpdate.FunctionName))
            {
                bool wasEmpty = string.IsNullOrEmpty(acc.FunctionName);
                acc.FunctionName = toolUpdate.FunctionName;
                if (wasEmpty)
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
                        Console.Error.Write($"\n[Tool: ");
                    using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
                        Console.Error.Write($"{toolUpdate.FunctionName}");
                    using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
                        Console.Error.Write($"] ");
                }
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
        }
    }

    private static string? ExtractFirstStringValue(string json)
    {
        var match = System.Text.RegularExpressions.Regex.Match(json, @"""[^""]+"":\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<ChatMessage> FinalizeToolCalls(
        Dictionary<int, ToolCallAccumulator> accumulatedToolCalls,
        List<ChatMessage> messages,
        int contextWindowSize)
    {
        var assistantToolCalls = accumulatedToolCalls.Values
            .Select(acc => ChatToolCall.CreateFunctionToolCall(acc.Id, acc.FunctionName, BinaryData.FromString(acc.Arguments)))
            .ToList();

        messages.Add(new AssistantChatMessage(assistantToolCalls));

        using (ConsoleStyler.WithColor(ConsoleColor.Blue))
            Console.Error.WriteLine("\n— Results —");
        var toolResultMessages = new List<ChatMessage>();
        foreach (var toolCall in assistantToolCalls)
        {
            var result = ResponseHandler.ProcessSingleToolCall(toolCall);
            if (result != null)
            {
                toolResultMessages.Add(result);
                if (result is ToolChatMessage toolMsg)
                {
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
            }
        }

        messages.AddRange(toolResultMessages);

        return ContextManager.TruncateMessages(messages, contextWindowSize);
    }

    private static string TruncateForDisplay(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + "…";
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
}

public class ToolCallAccumulator
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool ArgDisplayed { get; set; }
}
