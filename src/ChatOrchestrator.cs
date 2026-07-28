using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ChatOrchestrator
{
    public static async Task Run(string prompt)
    {
        var client = ChatService.CreateClient();
        var options = ToolHandler.CreateCompletionOptions();
        var provider = Configuration.GetProvider();
        var maxIterations = Configuration.GetMaxIterations();
        var contextWindowSize = Configuration.GetContextWindowSize();
        var systemMessage = new SystemChatMessage(SystemPrompt.GetPrompt(provider));
        List<ChatMessage> messages = [systemMessage, new UserChatMessage(prompt)];

        await Console.Error.WriteLineAsync("====================");
        await Console.Error.WriteLineAsync($"Provider: {provider}");
        await Console.Error.WriteLineAsync($"Model: {Configuration.GetModel()}");
        await Console.Error.WriteLineAsync($"Context window: {contextWindowSize} tokens");
        await Console.Error.WriteLineAsync($"Max tool result tokens: {Configuration.GetMaxToolResultTokens()}");
        await Console.Error.WriteLineAsync($"Max iterations: {maxIterations}");
        await Console.Error.WriteLineAsync($"System prompt length: {ContextManager.EstimateTokens(SystemPrompt.GetPrompt(provider))} tokens");
        await Console.Error.WriteLineAsync($"User prompt: {prompt}");
        await Console.Error.WriteLineAsync($"User prompt tokens: {ContextManager.EstimateTokens(prompt)}");
        await Console.Error.WriteLineAsync("===================");

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            await Console.Error.WriteLineAsync($"[iteration {iteration + 1}/{maxIterations}]");

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
                }
                break;
            }

            await Console.Error.WriteLineAsync($"\n--- Tool Calls ({accumulatedToolCalls.Count}) ---");
            foreach (var acc in accumulatedToolCalls.Values)
            {
                await Console.Error.WriteLineAsync($"  Tool: {acc.FunctionName}");
                await Console.Error.WriteLineAsync($"  ID: {acc.Id}");
                await Console.Error.WriteLineAsync($"  Args: {acc.Arguments}");
                await Console.Error.WriteLineAsync("  ---");
            }

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
            }

            var acc = accumulatedToolCalls[index];
            if (!string.IsNullOrEmpty(toolUpdate.ToolCallId)) acc.Id = toolUpdate.ToolCallId;
            if (!string.IsNullOrEmpty(toolUpdate.FunctionName)) acc.FunctionName = toolUpdate.FunctionName;
            if (toolUpdate.FunctionArgumentsUpdate != null && toolUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
            {
                acc.Arguments += toolUpdate.FunctionArgumentsUpdate.ToString();
            }
        }
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

        var toolResultMessages = ResponseHandler.ProcessToolCalls(assistantToolCalls);

        Console.Error.WriteLine($"\n--- Tool Results ({toolResultMessages.Count}) ---");
        foreach (var msg in toolResultMessages)
        {
            if (msg is ToolChatMessage toolMsg)
            {
                string content = ContextManager.ExtractText(toolMsg.Content);
                int tokens = ContextManager.EstimateTokens(content);
                Console.Error.WriteLine($"  Tool Call ID: {toolMsg.ToolCallId}");
                Console.Error.WriteLine($"  Result ({tokens} tokens): {content[..Math.Min(200, content.Length)]}{(content.Length > 200 ? "..." : "")}");
                Console.Error.WriteLine("  ---");
            }
        }

        messages.AddRange(toolResultMessages);

        int totalTokens = 0;
        var truncated = ContextManager.TruncateMessages(messages, contextWindowSize);

        Console.Error.WriteLine($"\n--- Message Summary ({truncated.Count} messages) ---");
        for (int i = 0; i < truncated.Count; i++)
        {
            var msg = truncated[i];
            int tokens = ContextManager.EstimateMessageTokens(msg);
            totalTokens += tokens;
            string typeName = msg.GetType().Name;
            string preview = "";
            if (msg is UserChatMessage u) preview = ContextManager.ExtractText(u.Content)[..Math.Min(50, ContextManager.ExtractText(u.Content).Length)];
            else if (msg is AssistantChatMessage a && a.Content != null) preview = ContextManager.ExtractText(a.Content)[..Math.Min(50, ContextManager.ExtractText(a.Content).Length)];
            else if (msg is ToolChatMessage t) preview = $"[tool result: {ContextManager.ExtractText(t.Content)[..Math.Min(50, ContextManager.ExtractText(t.Content).Length)]}]";

            Console.Error.WriteLine($"  [{i}] {typeName} ({tokens} tok): {preview}...");
        }
        Console.Error.WriteLine($"  Total estimated tokens: {totalTokens} / {contextWindowSize}");
        Console.Error.WriteLine("====================\n");

        return truncated;
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
}
