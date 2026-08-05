using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ResponseHandler
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<List<ChatMessage>> ProcessToolCallsAsync(ChatCompletion response, CancellationToken cancellationToken = default)
    {
        return await ProcessToolCallsAsync(response.ToolCalls, cancellationToken);
    }

    public static async Task<List<ChatMessage>> ProcessToolCallsAsync(IReadOnlyList<ChatToolCall>? toolCalls, CancellationToken cancellationToken = default)
    {
        var toolResultMessages = new List<ChatMessage>();

        if (toolCalls == null || toolCalls.Count == 0)
        {
            return toolResultMessages;
        }

        foreach (var toolCall in toolCalls)
        {
            var result = await ProcessToolCall(toolCall, cancellationToken);
            if (result != null)
            {
                toolResultMessages.Add(result);
            }
        }

        return toolResultMessages;
    }

    private static async Task<ToolChatMessage?> ProcessToolCall(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(toolCall.FunctionName))
        {
            return CreateErrorResult(toolCall, "Error: received tool call with no function name.");
        }

        Task<ToolChatMessage?> task = toolCall.FunctionName switch
        {
            ToolHandler.ReadFunctionName => Task.FromResult<ToolChatMessage?>(FileReadHandler.ProcessReadFileCall(toolCall)),
            ToolHandler.WriteFunctionName => Task.FromResult<ToolChatMessage?>(FileEditHandler.ProcessWriteFileCall(toolCall)),
            ToolHandler.EditFunctionName => Task.FromResult<ToolChatMessage?>(FileEditHandler.ProcessEditFileCall(toolCall)),
            ToolHandler.ApplyPatchFunctionName => PatchHandler.ProcessApplyPatchCallAsync(toolCall, cancellationToken),
            ToolHandler.DiffFunctionName => PatchHandler.ProcessDiffCallAsync(toolCall, cancellationToken),
            ToolHandler.PowershellFunctionName => ProcessExecutionHandler.ProcessPowershellCallAsync(toolCall, cancellationToken),
            ToolHandler.GlobFunctionName => Task.FromResult<ToolChatMessage?>(ProcessExecutionHandler.ProcessGlobCall(toolCall)),
            ToolHandler.GrepFunctionName => ProcessExecutionHandler.ProcessGrepCallAsync(toolCall, cancellationToken),
            ToolHandler.WebFetchFunctionName => WebToolHandlers.ProcessWebFetchCallAsync(toolCall, cancellationToken),
            ToolHandler.WebSearchFunctionName => WebToolHandlers.ProcessWebSearchCallAsync(toolCall, cancellationToken),
            ToolHandler.QuestionFunctionName => Task.FromResult<ToolChatMessage?>(QuestionHandler.ProcessQuestionCall(toolCall)),
            ToolHandler.TaskFunctionName => TaskHandler.ProcessTaskCallAsync(toolCall, cancellationToken),
            ToolHandler.TodoWriteFunctionName => Task.FromResult<ToolChatMessage?>(TodoWriteHandler.ProcessTodoWriteCall(toolCall)),
            _ => Task.FromResult<ToolChatMessage?>(CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.EditFunctionName}, {ToolHandler.ApplyPatchFunctionName}, {ToolHandler.DiffFunctionName}, {ToolHandler.PowershellFunctionName}, {ToolHandler.GlobFunctionName}, {ToolHandler.GrepFunctionName}, {ToolHandler.WebFetchFunctionName}, {ToolHandler.WebSearchFunctionName}, {ToolHandler.QuestionFunctionName}, {ToolHandler.TaskFunctionName}, {ToolHandler.TodoWriteFunctionName}."))
        };
        return await task;
    }

    internal static async Task<ToolChatMessage?> ProcessSingleToolCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return await ProcessToolCall(toolCall, cancellationToken);
    }

    internal static ToolChatMessage CreateErrorResult(ChatToolCall toolCall, string errorMessage)
    {
        ConsoleStyler.WriteLine($"[tool error] {errorMessage}", ConsoleColor.Red, Console.Error);
        return new ToolChatMessage(toolCall.Id, errorMessage);
    }

    internal static T? DeserializeToolArguments<T>(ChatToolCall toolCall) where T : class
    {
        if (toolCall.FunctionArguments == null)
        {
            return null;
        }

        string raw = toolCall.FunctionArguments.ToString();

        T? parsed = TryParseJson<T>(raw);
        if (parsed != null)
        {
            return parsed;
        }

        string? decoded = DecodeOnce(raw);
        if (decoded != null)
        {
            parsed = TryParseJson<T>(decoded);
            if (parsed != null)
            {
                return parsed;
            }
        }

        foreach (var repaired in RepairCandidates(raw))
        {
            parsed = TryParseJson<T>(repaired);
            if (parsed != null)
            {
                return parsed;
            }

            string? repairedDecoded = DecodeOnce(repaired);
            if (repairedDecoded != null)
            {
                parsed = TryParseJson<T>(repairedDecoded);
                if (parsed != null)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static T? TryParseJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DecodeOnce(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> RepairCandidates(string raw)
    {
        string candidate = raw.Trim();

        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            candidate = candidate[3..].Trim();
            if (candidate.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[4..].TrimStart();
            }
            if (candidate.EndsWith("```", StringComparison.Ordinal))
            {
                candidate = candidate[..^3].Trim();
            }
        }

        int start = candidate.IndexOf('{');
        if (start < 0)
        {
            yield break;
        }

        int end = candidate.LastIndexOf('}');
        if (end < start) end = candidate.Length - 1;

        string json = candidate[start..(end + 1)];

        // Small models often emit single-quoted strings; only rewrite when the
        // payload contains no double quotes at all, so real strings are not damaged.
        if (!json.Contains('"'))
        {
            json = json.Replace('\'', '"');
        }

        json = FixUnquotedKeys(json);
        json = FixTrailingCommas(json);
        yield return json;

        // Progressive repair for truncated output: drop trailing key/value
        // segments until the remaining object parses (or the braces balance).
        string progressive = json;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            progressive = BalanceBraces(progressive);
            yield return progressive;

            int lastComma = progressive.LastIndexOf(',');
            if (lastComma <= 0) break;
            progressive = progressive[..lastComma].TrimEnd();
        }
    }

    private static string FixUnquotedKeys(string json)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            json,
            @"([\{,])\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*:",
            "$1\"$2\":");
    }

    private static string FixTrailingCommas(string json)
    {
        return System.Text.RegularExpressions.Regex.Replace(json, @",(\s*[}\]])", "$1");
    }

    private static string BalanceBraces(string json)
    {
        int open = 0;
        foreach (char c in json)
        {
            if (c == '{') open++;
            else if (c == '}') open--;
        }
        return open > 0 ? json + new string('}', open) : json;
    }

    internal static string RepairContentEncoding(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("\\\""))
        {
            return value;
        }

        return value.Replace("\\\"", "\"");
    }

    internal static ToolChatMessage? ExecuteToolCall<T>(
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

    internal static async Task<ToolChatMessage?> ExecuteToolCallAsync<T>(
        ChatToolCall toolCall,
        string formatError,
        string errorPrefix,
        Func<T, Task<ToolChatMessage>> execute) where T : class
    {
        var arguments = DeserializeToolArguments<T>(toolCall);
        if (arguments == null)
        {
            return CreateErrorResult(toolCall, $"Error: {toolCall.FunctionName} tool called with invalid arguments. {formatError}");
        }

        try
        {
            return await execute(arguments);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error {errorPrefix}: {ex.Message}");
        }
    }
}
