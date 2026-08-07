using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        Diag.Log("streaming:start model=" + Configuration.GetModel());
        int count = 0;
        await foreach (var update in StreamInner(client, chatMessages, options, cancellationToken))
        {
            count++;
            yield return update;
        }
        Diag.Log("streaming:done updates=" + count);
    }

    private static async IAsyncEnumerable<StreamingChatCompletionUpdate> StreamInner(
        ChatClient client, List<ChatMessage> chatMessages, ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    // TPM limits per model (free tier). Key = model pattern (lowercase)
    private static readonly Dictionary<string, int> ModelTpmLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-3.5-flash-lite"] = 250_000,
        ["gemini-3.5-flash"] = 250_000,
        ["gemini-3.1-flash-lite"] = 250_000,
        ["gemini-3.1-flash"] = 250_000,
        ["gemini-3.6-flash"] = 250_000,
        ["gemini-2.5-flash"] = 250_000,
        ["gemini-2.5-pro"] = 250_000,
        ["gemini"] = 250_000,
        // OpenAI models (Tier 1 paid limits, much higher)
        ["gpt-4o"] = 4_000_000,
        ["gpt-4o-mini"] = 4_000_000,
        ["o1"] = 4_000_000,
        ["o3"] = 4_000_000,
        ["o4-mini"] = 4_000_000,
        // OpenRouter defaults
        ["openrouter"] = 1_000_000,
    };

    public RateLimitPolicy(TimeSpan minInterval)
    {
        _minInterval = minInterval;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Throttle(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Throttle(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Throttle(PipelineMessage message)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRequest;

            // Estimate tokens from request body
            int estimatedTokens = EstimateRequestTokens(message);
            int tpmLimit = GetTpmLimit();
            
            // Calculate minimum delay needed for TPM: (tokens / TPM_limit) * 60 seconds
            double tpmDelaySeconds = (estimatedTokens / (double)tpmLimit) * 60.0;
            var tpmDelay = TimeSpan.FromSeconds(Math.Max(tpmDelaySeconds, 0));

            // Use the longer of RPM delay (10s) or TPM delay
            var requiredDelay = _minInterval > tpmDelay ? _minInterval : tpmDelay;

            if (elapsed < requiredDelay)
            {
                var delay = requiredDelay - elapsed;
                Thread.Sleep(delay);
            }
            _lastRequest = DateTime.UtcNow;
        }
    }

    private int EstimateRequestTokens(PipelineMessage message)
    {
        try
        {
            var content = message.Request.Content;
            if (content == null) return 0;

            string? json = content.ToString();
            if (string.IsNullOrEmpty(json)) return 0;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("messages", out var messagesElement) &&
                messagesElement.ValueKind == JsonValueKind.Array)
            {
                int totalTokens = 0;
                foreach (var msg in messagesElement.EnumerateArray())
                {
                    if (msg.TryGetProperty("content", out var contentElement) &&
                        contentElement.ValueKind == JsonValueKind.String &&
                        contentElement.GetString() != null)
                    {
                        totalTokens += TokenEstimator.Estimate(contentElement.GetString()!);
                    }
                }
                return totalTokens;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private int GetTpmLimit()
    {
        var model = Configuration.GetModel()?.ToLowerInvariant() ?? "";
        var provider = Configuration.GetProvider()?.ToLowerInvariant() ?? "";

        // Try exact model match first
        foreach (var kvp in ModelTpmLimits)
        {
            if (model.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Try provider match
        foreach (var kvp in ModelTpmLimits)
        {
            if (provider.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        // Default conservative limit
        return 250_000;
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
