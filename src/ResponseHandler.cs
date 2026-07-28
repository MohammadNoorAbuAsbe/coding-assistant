using System.Diagnostics;
using System.Text.Json;
using OpenAI.Chat;
using ReverseMarkdown;

namespace TerminalAiAssistant;

public static class ResponseHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient WebClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Converter HtmlToMarkdown = new();

    public static List<ChatMessage> ProcessToolCalls(ChatCompletion response)
    {
        return ProcessToolCalls(response.ToolCalls);
    }

    public static List<ChatMessage> ProcessToolCalls(IReadOnlyList<ChatToolCall>? toolCalls)
    {
        var toolResultMessages = new List<ChatMessage>();

        if (toolCalls == null || toolCalls.Count == 0)
        {
            return toolResultMessages;
        }

        foreach (var toolCall in toolCalls)
        {
            var result = ProcessToolCall(toolCall);
            if (result != null)
            {
                toolResultMessages.Add(result);
            }
        }

        return toolResultMessages;
    }

    private static ToolChatMessage? ProcessToolCall(ChatToolCall toolCall)
    {
        if (string.IsNullOrEmpty(toolCall.FunctionName))
        {
            return CreateErrorResult(toolCall, "Error: received tool call with no function name.");
        }

        return toolCall.FunctionName switch
        {
            ToolHandler.ReadFunctionName => ProcessReadFileCall(toolCall),
            ToolHandler.WriteFunctionName => ProcessWriteFileCall(toolCall),
            ToolHandler.EditFunctionName => ProcessEditFileCall(toolCall),
            ToolHandler.EditLineFunctionName => ProcessEditLineCall(toolCall),
            ToolHandler.BashFunctionName => ProcessBashCall(toolCall),
            ToolHandler.GlobFunctionName => ProcessGlobCall(toolCall),
            ToolHandler.GrepFunctionName => ProcessGrepCall(toolCall),
            ToolHandler.WebFetchFunctionName => ProcessWebFetchCall(toolCall),
            ToolHandler.WebSearchFunctionName => ProcessWebSearchCall(toolCall),
            _ => CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.EditFunctionName}, {ToolHandler.EditLineFunctionName}, {ToolHandler.BashFunctionName}, {ToolHandler.GlobFunctionName}, {ToolHandler.GrepFunctionName}, {ToolHandler.WebFetchFunctionName}, {ToolHandler.WebSearchFunctionName}.")
        };
    }

    private static ToolChatMessage CreateErrorResult(ChatToolCall toolCall, string errorMessage)
    {
        Console.Error.WriteLine($"[tool error] {errorMessage}");
        return new ToolChatMessage(toolCall.Id, errorMessage);
    }

    private static T? DeserializeToolArguments<T>(ChatToolCall toolCall) where T : class
    {
        if (toolCall.FunctionArguments == null)
        {
            return null;
        }

        try
        {
            return toolCall.FunctionArguments.ToObjectFromJson<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolChatMessage? ExecuteToolCall<T>(
        ChatToolCall toolCall,
        string formatError,
        string errorPrefix,
        Func<T, ToolChatMessage> execute) where T : class
    {
        var arguments = DeserializeToolArguments<T>(toolCall);
        if (arguments == null)
        {
            return CreateErrorResult(toolCall, $"Error: {toolCall.FunctionName} tool called with invalid arguments. {formatError}");
        }

        try
        {
            return execute(arguments);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error {errorPrefix}: {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.ReadFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\"}",
            "reading file",
            args =>
            {
                if (args.file_path == null)
                {
                    return CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
                }

                string[] lines = System.IO.File.ReadAllLines(args.file_path);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    sb.AppendLine($"{i + 1}: {lines[i]}");
                }
                string fileText = sb.ToString();
                int maxTokens = Configuration.GetMaxToolResultTokens();
                fileText = ContextManager.TruncateToolResult(fileText, maxTokens);
                return new ToolChatMessage(toolCall.Id, fileText);
            });
    }

    private static ToolChatMessage? ProcessWriteFileCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.WriteFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"content\": \"<content>\"}",
            "writing file",
            args =>
            {
                if (args.file_path == null)
                {
                    return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'file_path'.");
                }

                if (args.content == null)
                {
                    return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'content'.");
                }

                string? directory = System.IO.Path.GetDirectoryName(args.file_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllText(args.file_path, args.content);
                return new ToolChatMessage(toolCall.Id, $"Successfully wrote content to {args.file_path}");
            });
    }

    private static ToolChatMessage? ProcessEditFileCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.EditFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"old_string\": \"<text>\", \"new_string\": \"<text>\"}",
            "editing file",
            args =>
            {
                if (args.file_path == null)
                {
                    return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'file_path'.");
                }

                if (args.old_string == null)
                {
                    return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'old_string'.");
                }

                if (args.new_string == null)
                {
                    return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'new_string'.");
                }

                if (!System.IO.File.Exists(args.file_path))
                {
                    return CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'.");
                }

                string content = System.IO.File.ReadAllText(args.file_path);

                var match = MatchFinder.FindBestMatch(content, args.old_string);
                if (match == null)
                {
                    return CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{args.file_path}'. Use the EditLine tool instead - Read the file first to see line numbers, then use EditLine with start_line and end_line.");
                }

                string newContent = content.Substring(0, match.Index) + args.new_string + content.Substring(match.Index + match.Length);
                System.IO.File.WriteAllText(args.file_path, newContent);

                string note = match.Strategy == MatchStrategy.Exact ? "" : $" (matched using {match.Strategy} comparison)";
                return new ToolChatMessage(toolCall.Id, $"Successfully edited {args.file_path}{note}.");
            });
    }

    private static ToolChatMessage? ProcessEditLineCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.EditLineCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"start_line\": \"<number>\", \"end_line\": \"<number>\", \"new_content\": \"<text>\"}",
            "editing file",
            args =>
            {
                if (args.file_path == null)
                {
                    return CreateErrorResult(toolCall, "Error: EditLine tool missing required parameter 'file_path'.");
                }

                if (!int.TryParse(args.start_line, out int startLine) || startLine < 1)
                {
                    return CreateErrorResult(toolCall, "Error: EditLine tool 'start_line' must be a positive integer.");
                }

                if (!int.TryParse(args.end_line, out int endLine) || endLine < startLine)
                {
                    return CreateErrorResult(toolCall, "Error: EditLine tool 'end_line' must be >= start_line.");
                }

                if (args.new_content == null)
                {
                    return CreateErrorResult(toolCall, "Error: EditLine tool missing required parameter 'new_content'.");
                }

                if (!System.IO.File.Exists(args.file_path))
                {
                    return CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'.");
                }

                string[] lines = System.IO.File.ReadAllLines(args.file_path);

                if (startLine > lines.Length)
                {
                    return CreateErrorResult(toolCall, $"Error: start_line {startLine} exceeds file length ({lines.Length} lines).");
                }

                int endIdx = Math.Min(endLine, lines.Length);
                var newLines = new List<string>();
                newLines.AddRange(lines.Take(startLine - 1));
                newLines.Add(args.new_content);
                newLines.AddRange(lines.Skip(endIdx));

                System.IO.File.WriteAllLines(args.file_path, newLines);
                int newTotalLines = newLines.Count;
                int newContentStart = startLine;
                int newContentEnd = startLine + args.new_content.Split('\n').Length - 1;
                return new ToolChatMessage(toolCall.Id, $"Successfully edited {args.file_path} (replaced lines {startLine}-{endLine} with {newContentStart}-{newContentEnd}). File now has {newTotalLines} lines. ALWAYS re-read the file before your next edit to get fresh line numbers.");
            });
    }

    private static ToolChatMessage? ProcessBashCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.BashCommandCall>(
            toolCall,
            "Expected format: {\"command\": \"<command>\"}",
            "executing command",
            args =>
            {
                if (args.command == null)
                {
                    return CreateErrorResult(toolCall, "Error: Bash tool missing required parameter 'command'.");
                }

                bool isWindows = OperatingSystem.IsWindows();
                string shell = isWindows ? "powershell.exe" : "bash";
                string argumentsPrefix = isWindows ? "-Command" : "-c";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    Arguments = $"{argumentsPrefix} \"{args.command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                using var process = new Process { StartInfo = processStartInfo };
                process.Start();

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                process.WaitForExit();

                string result;
                if (process.ExitCode == 0)
                {
                    result = stdout;
                }
                else
                {
                    result = $"Exit code: {process.ExitCode}\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
                }

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static ToolChatMessage? ProcessGlobCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.GlobCall>(
            toolCall,
            "Expected format: {\"pattern\": \"<glob>\", \"path\": \"<directory>\"}",
            "running glob",
            args =>
            {
                if (args.pattern == null)
                {
                    return CreateErrorResult(toolCall, "Error: Glob tool missing required parameter 'pattern'.");
                }

                string result = GlobHelper.FindFiles(args.pattern, args.path);

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static ToolChatMessage? ProcessGrepCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.GrepCall>(
            toolCall,
            "Expected format: {\"pattern\": \"<regex>\"}",
            "running ripgrep",
            args =>
            {
                if (args.pattern == null)
                {
                    return CreateErrorResult(toolCall, "Error: Grep tool missing required parameter 'pattern'.");
                }

                string rgPath = RipgrepHelper.FindRipgrep() ?? "";
                if (string.IsNullOrEmpty(rgPath))
                {
                    return CreateErrorResult(toolCall, "Error: ripgrep (rg) not found. Install it with: winget install BurntSushi.ripgrep.MSVC");
                }

                string arguments = RipgrepHelper.BuildRipgrepArguments(args);

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = rgPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                using var process = new Process { StartInfo = processStartInfo };
                process.Start();

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                bool finished = process.WaitForExit(10000);

                if (!finished)
                {
                    process.Kill();
                    return CreateErrorResult(toolCall, "Error: ripgrep search timed out after 10 seconds. Try a more specific pattern or path.");
                }

                if (process.ExitCode == 2)
                {
                    return CreateErrorResult(toolCall, $"Error: ripgrep invalid pattern '{args.pattern}': {stderr}");
                }

                string result;
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    result = $"No matches found for pattern: {args.pattern}";
                }
                else
                {
                    string[] lines = stdout.Trim().Split('\n');
                    if (lines.Length > 100)
                    {
                        result = string.Join("\n", lines.Take(100)) + $"\n\n... [showing 100 of {lines.Length} matches, refine your pattern to narrow results]";
                    }
                    else
                    {
                        result = stdout.Trim();
                    }
                }

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static ToolChatMessage? ProcessWebFetchCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.WebFetchCall>(
            toolCall,
            "Expected format: {\"url\": \"<url>\"}",
            "fetching URL",
            args =>
            {
                if (args.url == null)
                {
                    return CreateErrorResult(toolCall, "Error: WebFetch tool missing required parameter 'url'.");
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

    private static ToolChatMessage? ProcessWebSearchCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.WebSearchCall>(
            toolCall,
            "Expected format: {\"query\": \"<search query>\"}",
            "searching web",
            args =>
            {
                if (args.query == null)
                {
                    return CreateErrorResult(toolCall, "Error: WebSearch tool missing required parameter 'query'.");
                }

                string apiKey = Configuration.GetTavilyApiKey();

                int maxResults = 5;
                if (args.max_results != null)
                {
                    int.TryParse(args.max_results, out maxResults);
                    maxResults = Math.Clamp(maxResults, 1, 10);
                }

                string depth = args.search_depth switch
                {
                    "advanced" => "advanced",
                    _ => "basic"
                };

                var body = new
                {
                    api_key = apiKey,
                    query = args.query,
                    max_results = maxResults,
                    search_depth = depth
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body, JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = WebClient.Send(request);
                response.EnsureSuccessStatusCode();

                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                var tavilyResponse = JsonSerializer.Deserialize<TavilyResponse>(json, JsonOptions);
                if (tavilyResponse == null)
                {
                    return CreateErrorResult(toolCall, "Error: received empty response from Tavily search API.");
                }

                var sb = new System.Text.StringBuilder();

                if (!string.IsNullOrWhiteSpace(tavilyResponse.answer))
                {
                    sb.AppendLine($"Answer: {tavilyResponse.answer}");
                    sb.AppendLine();
                }

                if (tavilyResponse.results == null || tavilyResponse.results.Count == 0)
                {
                    sb.Append("No results found.");
                }
                else
                {
                    for (int i = 0; i < tavilyResponse.results.Count; i++)
                    {
                        var r = tavilyResponse.results[i];
                        sb.AppendLine($"Title: {r.title}");
                        sb.AppendLine($"URL: {r.url}");
                        sb.AppendLine($"Content: {r.content}");
                        if (i < tavilyResponse.results.Count - 1)
                        {
                            sb.AppendLine("---");
                        }
                    }
                }

                string result = sb.ToString();

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static string StripHtml(string html)
    {
        System.Text.RegularExpressions.Regex tagRegex = new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);
        string text = tagRegex.Replace(html, "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private class TavilyResponse
    {
        public string? answer { get; set; }
        public List<TavilyResult>? results { get; set; }
    }

    private class TavilyResult
    {
        public string? title { get; set; }
        public string? url { get; set; }
        public string? content { get; set; }
        public double? score { get; set; }
    }
}