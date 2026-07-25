using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ChatOrchestrator
{
    public static void Run(string prompt)
    {
        var client = ChatService.CreateClient();
        var options = ToolHandler.CreateCompletionOptions();

        List<ChatMessage> messages = new List<ChatMessage>
        {
            new UserChatMessage(prompt)
        };

        while (true)
        {
            var response = ChatService.GetCompletion(client, messages, options);
            messages.Add(new AssistantChatMessage(response));

            var toolResultMessages = ResponseHandler.ProcessToolCalls(response);

            if (toolResultMessages.Count == 0)
            {
                ResponseHandler.DisplayConsoleContent(response);
                break;
            }

            messages.AddRange(toolResultMessages);
        }
    }
}