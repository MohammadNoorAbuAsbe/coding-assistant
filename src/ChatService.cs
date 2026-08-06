using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TerminalAiAssistant;

public static class ChatService
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(10);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly object _rateLimitLock = new();

    public static ChatClient CreateClient(double timeoutSeconds = 300)
    {
        var apiKey = Configuration.GetApiKey();
        var baseUrl = Configuration.GetBaseUrl();
        var model = Configuration.GetModel();

        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl), NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds) };

        var siteUrl = Configuration.GetSiteUrl();
        var siteName = Configuration.GetSiteName();
        if (siteUrl != null || siteName != null)
        {
            options.AddPolicy(new OpenRouterHeaderPolicy(siteUrl, siteName), PipelinePosition.PerCall);
        }

        options.AddPolicy(new ReasoningTapPolicy(), PipelinePosition.PerCall);

        // Add rate limiting policy for all cloud providers (skip Ollama/local)
        var provider = Configuration.GetProvider();
        if (provider != "ollama")
        {
            options.AddPolicy(new RateLimitPolicy(MinRequestInterval), PipelinePosition.PerCall);
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

internal sealed class RateLimitPolicy : PipelinePolicy
{
    private readonly TimeSpan _minInterval;
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly object _lock = new();

    public RateLimitPolicy(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Throttle();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Throttle();
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Throttle()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRequest;
            if (elapsed < _minInterval)
            {
                var delay = _minInterval - elapsed;
                Thread.Sleep(delay);
            }
            _lastRequest = DateTime.UtcNow;
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
