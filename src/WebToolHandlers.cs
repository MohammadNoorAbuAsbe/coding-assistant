using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenAI.Chat;
using ReverseMarkdown;

namespace TerminalAiAssistant;

internal static class WebToolHandlers
{
    private static readonly HttpClientHandler WebClientHandler = new()
    {
        AllowAutoRedirect = false
    };

    private static readonly HttpClient WebClient = new(WebClientHandler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Converter HtmlToMarkdown = new();

    private const string TavilyApiUrl = "https://api.tavily.com/search";
    private const int MaxRedirects = 5;

    internal static async Task<ToolChatMessage?> ProcessWebFetchCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.WebFetchCall>(
            toolCall,
            "Expected format: {\"url\": \"<url>\"}",
            "fetching URL",
            async args =>
            {
                if (args.url == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: WebFetch tool missing required parameter 'url'.");
                }

                using var response = await SendWithRedirectValidationAsync(args.url, cancellationToken: cancellationToken);
                response.EnsureSuccessStatusCode();

                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                string raw = await response.Content.ReadAsStringAsync(cancellationToken);

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

    private static async Task<HttpResponseMessage> SendWithRedirectValidationAsync(string url, int depth = 0, CancellationToken cancellationToken = default)
    {
        if (depth > MaxRedirects)
            throw new InvalidOperationException("Too many redirects.");

        ValidateUrl(url);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TerminalAiAssistant/1.0");

        var response = await WebClient.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
        {
            string? redirectUrl = response.Headers.Location?.OriginalString;
            if (string.IsNullOrEmpty(redirectUrl))
                throw new InvalidOperationException("Redirect with no Location header.");

            if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out _))
            {
                redirectUrl = new Uri(new Uri(url), redirectUrl).ToString();
            }

            response.Dispose();
            return await SendWithRedirectValidationAsync(redirectUrl, depth + 1, cancellationToken);
        }

        return response;
    }

    private static void ValidateUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid URL format.");

        if (uri.Scheme != "http" && uri.Scheme != "https")
            throw new InvalidOperationException($"Scheme '{uri.Scheme}' is not allowed. Only http and https are permitted.");

        string host = uri.Host.ToLowerInvariant();

        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "0.0.0.0")
            throw new InvalidOperationException("Requests to localhost are not allowed.");

        if (host.StartsWith("metadata.") || host == "metadata")
            throw new InvalidOperationException("Requests to metadata endpoints are not allowed.");

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch
        {
            throw new InvalidOperationException($"Could not resolve host '{host}'.");
        }

        foreach (var addr in addresses)
        {
            if (IsPrivateIp(addr))
                throw new InvalidOperationException($"Requests to private IP addresses are not allowed (resolved to {addr}).");
        }
    }

    private static bool IsPrivateIp(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            else
                return address.Equals(IPAddress.IPv6Loopback) || IsPrivateIPv6(address);
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    private static bool IsPrivateIPv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        if ((bytes[0] & 0xFE) == 0xFC)
            return true;

        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            return true;

        return false;
    }

    internal static async Task<ToolChatMessage?> ProcessWebSearchCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.WebSearchCall>(
            toolCall,
            "Expected format: {\"query\": \"<search query>\"}",
            "searching web",
            async args =>
            {
                if (args.query == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: WebSearch tool missing required parameter 'query'.");
                }

                int maxResults = ParseMaxResults(args.max_results);
                string depth = args.search_depth == "advanced" ? "advanced" : "basic";

                var tavilyResponse = await SearchTavilyAsync(args.query, maxResults, depth, cancellationToken);
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

    private static async Task<TavilyResponse?> SearchTavilyAsync(string query, int maxResults, string depth, CancellationToken cancellationToken = default)
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

        var response = await WebClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
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
