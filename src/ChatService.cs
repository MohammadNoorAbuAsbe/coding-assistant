using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace TerminalAiAssistant;

public static class ChatService
{
    public static ChatClient CreateClient()
    {
        var apiKey = Configuration.GetApiKey();
        var baseUrl = Configuration.GetBaseUrl();
        var model = Configuration.GetModel();

        return new ChatClient(
            model: model,
            credential: new ApiKeyCredential(apiKey),
            options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
        );
    }

    public static ChatCompletion GetCompletion(ChatClient client, List<ChatMessage> chatMessages, ChatCompletionOptions options)
    {
        return client.CompleteChat(chatMessages, options);
    }
}
