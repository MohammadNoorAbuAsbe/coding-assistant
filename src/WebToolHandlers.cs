using System.Text.Json;
using OpenAI.Chat;
using ReverseMarkdown;

namespace TerminalAiAssistant;

internal static class WebToolHandlers
{
    private static readonly HttpClient WebClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Converter HtmlToMarkdown = new();

    private const string TavilyApiUrl = "https://api.tavily.com/search";

    internal static ToolChatMessage? ProcessWebFetchCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.WebFetchCall>(
            toolCall,
            "Expected format: {\"url\": \"<url>\"}",
            "fetching URL",
            args =>
            {
                if (args.url == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: WebFetch tool missing required parameter 'url'.");
                }

                var request = new HttpRequestMessage(HttpMethod.Get, args.url);
                request.Headers.UserAgent.ParseAdd("TerminalAiAssistant/1.0");

                var response = WebClient.Send(request);
                response.EnsureSuccessStatusCode();

                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                string raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                string result = args.format switch
                {
                    "html" => raw,
                    "text" => StripHtml(raw),
                    _ when contentType.Contains("html") => HtmlToMarkdown.Convert(raw),
                    _ => raw
                };

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    internal static ToolChatMessage? ProcessWebSearchCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.WebSearchCall>(
            toolCall,
            "Expected format: {\"query\": \"<search query>\"}",
            "searching web",
            args =>
            {
                if (args.query == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: WebSearch tool missing required parameter 'query'.");
                }

                int maxResults = ParseMaxResults(args.max_results);
                string depth = args.search_depth == "advanced" ? "advanced" : "basic";

                var tavilyResponse = SearchTavily(args.query, maxResults, depth);
                if (tavilyResponse == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: received empty response from Tavily search API.");
                }

                string result = FormatTavilyResults(tavilyResponse);

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static int ParseMaxResults(string? input)
    {
        if (input == null) return 5;
        int.TryParse(input, out int value);
        return Math.Clamp(value, 1, 10);
    }

    private static TavilyResponse? SearchTavily(string query, int maxResults, string depth)
    {
        string apiKey = Configuration.GetTavilyApiKey();

        var body = new
        {
            api_key = apiKey,
            query,
            max_results = maxResults,
            search_depth = depth
        };

        var request = new HttpRequestMessage(HttpMethod.Post, TavilyApiUrl);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, ResponseHandler.JsonOptions),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = WebClient.Send(request);
        response.EnsureSuccessStatusCode();

        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<TavilyResponse>(json, ResponseHandler.JsonOptions);
    }

    private static string FormatTavilyResults(TavilyResponse resp)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(resp.answer))
        {
            sb.AppendLine($"Answer: {resp.answer}");
            sb.AppendLine();
        }

        if (resp.results == null || resp.results.Count == 0)
        {
            sb.Append("No results found.");
            return sb.ToString();
        }

        for (int i = 0; i < resp.results.Count; i++)
        {
            var r = resp.results[i];
            sb.AppendLine($"Title: {r.title}");
            sb.AppendLine($"URL: {r.url}");
            sb.AppendLine($"Content: {r.content}");
            if (i < resp.results.Count - 1)
            {
                sb.AppendLine("---");
            }
        }

        return sb.ToString();
    }

    private static string StripHtml(string html)
    {
        var tagRegex = new System.Text.RegularExpressions.Regex("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);
        string text = tagRegex.Replace(html, "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}
