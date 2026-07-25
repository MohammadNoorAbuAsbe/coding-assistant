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

            var accumulatedToolCalls = new Dictionary<int, ToolCallAccumulator>();
            string? responseContent = null;

            await foreach (var update in ChatService.GetCompletionStreaming(client, messages, options))
            {
                if (update.ContentUpdate != null)
                {
                    for (int i = 0; i < update.ContentUpdate.Count; i++)
                    {
                        var part = update.ContentUpdate[i];
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            Console.Write(part.Text);
                            responseContent = (responseContent ?? "") + part.Text;
                        }
                    }
                }

                if (update.ToolCallUpdates != null)
                {
                    foreach (var toolUpdate in update.ToolCallUpdates)
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
                        {
                            string argsStr = toolUpdate.FunctionArgumentsUpdate.ToString();
                            acc.Arguments += argsStr;
                        }
                    }
                }
            }

            if (accumulatedToolCalls.Count == 0)
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    Console.WriteLine();
                }
                break;
            }

            var assistantToolCalls = accumulatedToolCalls.Values
                .Select(acc => ChatToolCall.CreateFunctionToolCall(acc.Id, acc.FunctionName, BinaryData.FromString(acc.Arguments)))
                .ToList();
            messages.Add(new AssistantChatMessage(assistantToolCalls));

            var toolResultMessages = ResponseHandler.ProcessToolCalls(assistantToolCalls);
            messages.AddRange(toolResultMessages);

            messages = ContextManager.TruncateMessages(messages, contextWindowSize);
        }
    }
}

public class ToolCallAccumulator
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string Arguments { get; set; } = "";
}
