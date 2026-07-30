using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;

namespace TerminalAiAssistant;

public static class ChatService
{
    public static ChatClient CreateClient()
    {
        var apiKey = Configuration.GetApiKey();
        var baseUrl = Configuration.GetBaseUrl();
        var model = Configuration.GetModel();

        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };

        var siteUrl = Configuration.GetSiteUrl();
        var siteName = Configuration.GetSiteName();
        if (siteUrl != null || siteName != null)
        {
            options.AddPolicy(new OpenRouterHeaderPolicy(siteUrl, siteName), PipelinePosition.PerCall);
        }

        return new ChatClient(
            model: model,
            credential: new ApiKeyCredential(apiKey),
            options: options
        );
    }

    public static ChatCompletion GetCompletion(ChatClient client, List<ChatMessage> chatMessages, ChatCompletionOptions options)
    {
        return client.CompleteChat(chatMessages, options);
    }

    public static async IAsyncEnumerable<StreamingChatCompletionUpdate> GetCompletionStreaming(ChatClient client, List<ChatMessage> chatMessages, ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in client.CompleteChatStreamingAsync(chatMessages, options, cancellationToken))
        {
            yield return update;
        }
    }
}

internal class OpenRouterHeaderPolicy : PipelinePolicy
{
    private readonly string? _siteUrl;
    private readonly string? _siteName;

    public OpenRouterHeaderPolicy(string? siteUrl, string? siteName)
    {
        _siteUrl = siteUrl;
        _siteName = siteName;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (_siteUrl != null)
            message.Request.Headers.Set("HTTP-Referer", _siteUrl);
        if (_siteName != null)
            message.Request.Headers.Set("X-Title", _siteName);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (_siteUrl != null)
            message.Request.Headers.Set("HTTP-Referer", _siteUrl);
        if (_siteName != null)
            message.Request.Headers.Set("X-Title", _siteName);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }
}
