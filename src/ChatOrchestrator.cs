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

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            await Console.Error.WriteLineAsync($"[iteration {iteration + 1}/{maxIterations}]");

            var (accumulatedToolCalls, responseContent) = await ProcessStreamingUpdates(client, messages, options);

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                    Console.WriteLine();
                break;
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
            if (toolUpdate.FunctionArgumentsUpdate != null)
                acc.Arguments += toolUpdate.FunctionArgumentsUpdate.ToString();
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
        messages.AddRange(toolResultMessages);

        return ContextManager.TruncateMessages(messages, contextWindowSize);
    }
}

public class ToolCallAccumulator
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string Arguments { get; set; } = "";
}
