using System.Diagnostics;
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
            ToolHandler.ReadFunctionName => Task.FromResult<ToolChatMessage?>(ProcessReadFileCall(toolCall)),
            ToolHandler.WriteFunctionName => Task.FromResult<ToolChatMessage?>(ProcessWriteFileCall(toolCall)),
            ToolHandler.EditFunctionName => Task.FromResult<ToolChatMessage?>(ProcessEditFileCall(toolCall)),
            ToolHandler.ApplyPatchFunctionName => PatchHandler.ProcessApplyPatchCallAsync(toolCall, cancellationToken),
            ToolHandler.DiffFunctionName => PatchHandler.ProcessDiffCallAsync(toolCall, cancellationToken),
            ToolHandler.BashFunctionName => ProcessBashCallAsync(toolCall, cancellationToken),
            ToolHandler.GlobFunctionName => Task.FromResult<ToolChatMessage?>(ProcessGlobCall(toolCall)),
            ToolHandler.GrepFunctionName => ProcessGrepCallAsync(toolCall, cancellationToken),
            ToolHandler.WebFetchFunctionName => WebToolHandlers.ProcessWebFetchCallAsync(toolCall, cancellationToken),
            ToolHandler.WebSearchFunctionName => WebToolHandlers.ProcessWebSearchCallAsync(toolCall, cancellationToken),
            ToolHandler.QuestionFunctionName => Task.FromResult<ToolChatMessage?>(QuestionHandler.ProcessQuestionCall(toolCall)),
            ToolHandler.TaskFunctionName => TaskHandler.ProcessTaskCallAsync(toolCall, cancellationToken),
            ToolHandler.TodoWriteFunctionName => Task.FromResult<ToolChatMessage?>(TodoWriteHandler.ProcessTodoWriteCall(toolCall)),
            _ => Task.FromResult<ToolChatMessage?>(CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.EditFunctionName}, {ToolHandler.ApplyPatchFunctionName}, {ToolHandler.DiffFunctionName}, {ToolHandler.BashFunctionName}, {ToolHandler.GlobFunctionName}, {ToolHandler.GrepFunctionName}, {ToolHandler.WebFetchFunctionName}, {ToolHandler.WebSearchFunctionName}, {ToolHandler.QuestionFunctionName}, {ToolHandler.TaskFunctionName}, {ToolHandler.TodoWriteFunctionName}."))
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

        try
        {
            return toolCall.FunctionArguments.ToObjectFromJson<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                string[] lines = System.IO.File.ReadAllLines(safePath);
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

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                string? directory = System.IO.Path.GetDirectoryName(safePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllText(safePath, args.content);
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

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                if (!System.IO.File.Exists(safePath))
                {
                    return CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'.");
                }

                string content = System.IO.File.ReadAllText(safePath);

                var match = MatchFinder.FindBestMatch(content, args.old_string);
                if (match == null)
                {
                    return CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{args.file_path}'. Use the ApplyPatch tool instead — Read the file first, then submit a patch with correct context lines.");
                }

                string newContent = content.Substring(0, match.Index) + args.new_string + content.Substring(match.Index + match.Length);
                System.IO.File.WriteAllText(safePath, newContent);

                string note = match.Strategy == MatchStrategy.Exact ? "" : $" (matched using {match.Strategy} comparison)";
                return new ToolChatMessage(toolCall.Id, $"Successfully edited {args.file_path}{note}.");
            });
    }

    private static async Task<ToolChatMessage?> ProcessBashCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ExecuteToolCallAsync<ToolHandler.BashCommandCall>(
            toolCall,
            "Expected format: {\"command\": \"<command>\"}",
            "executing command",
            args => ExecuteBashCommandAsync(toolCall, args, cancellationToken));
    }

    private static async Task<ToolChatMessage> ExecuteBashCommandAsync(ChatToolCall toolCall, ToolHandler.BashCommandCall args, CancellationToken cancellationToken)
    {
        if (args.command == null)
            return CreateErrorResult(toolCall, "Error: Bash tool missing required parameter 'command'.");

        bool isWindows = OperatingSystem.IsWindows();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "powershell.exe" : "bash",
                Arguments = $"{(isWindows ? "-Command" : "-c")} \"{args.command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            }
        };
        process.Start();

        int timeoutMs = Configuration.GetBashTimeout();
        var (stdout, stderr, timedOut) = await RunProcessWithTimeoutAsync(process, timeoutMs, cancellationToken);

        if (timedOut)
        {
            string partialOutput = $"stdout:\n{stdout}\n\nstderr:\n{stderr}";
            partialOutput = ContextManager.TruncateToolResult(partialOutput, Configuration.GetMaxToolResultTokens());
            if (cancellationToken.IsCancellationRequested)
                return CreateErrorResult(toolCall, $"Error: command was cancelled by the user.\n\n{partialOutput}");
            return CreateErrorResult(toolCall, $"Error: command timed out after {timeoutMs / 1000} seconds.\n\n{partialOutput}");
        }

        string result = process.ExitCode == 0
            ? stdout
            : $"Exit code: {process.ExitCode}\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";

        result = ContextManager.TruncateToolResult(result, Configuration.GetMaxToolResultTokens());
        return new ToolChatMessage(toolCall.Id, result);
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

                string safePath = args.path != null
                    ? PathValidator.ValidatePath(args.path, Environment.CurrentDirectory)
                    : Environment.CurrentDirectory;

                string result = GlobHelper.FindFiles(args.pattern, safePath);

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    private static async Task<ToolChatMessage?> ProcessGrepCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ExecuteToolCallAsync<ToolHandler.GrepCall>(
            toolCall,
            "Expected format: {\"pattern\": \"<regex>\"}",
            "running ripgrep",
            args => ExecuteGrepCommandAsync(toolCall, args, cancellationToken));
    }

    private static async Task<ToolChatMessage> ExecuteGrepCommandAsync(ChatToolCall toolCall, ToolHandler.GrepCall args, CancellationToken cancellationToken)
    {
        if (args.pattern == null)
            return CreateErrorResult(toolCall, "Error: Grep tool missing required parameter 'pattern'.");

        string rgPath = RipgrepHelper.FindRipgrep() ?? "";
        if (string.IsNullOrEmpty(rgPath))
            return CreateErrorResult(toolCall, "Error: ripgrep (rg) not found. Install it with: winget install BurntSushi.ripgrep.MSVC");

        string safePath = args.path != null
            ? PathValidator.ValidatePath(args.path, Environment.CurrentDirectory)
            : Environment.CurrentDirectory;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = rgPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            }
        };
        foreach (var arg in RipgrepHelper.BuildRipgrepArguments(args, safePath))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();

        var (stdout, stderr, timedOut) = await RunProcessWithTimeoutAsync(process, 10000, cancellationToken);

        if (timedOut)
        {
            if (cancellationToken.IsCancellationRequested)
                return CreateErrorResult(toolCall, "Error: ripgrep search was cancelled by the user.");
            return CreateErrorResult(toolCall, "Error: ripgrep search timed out after 10 seconds. Try a more specific pattern or path.");
        }

        if (process.ExitCode == 2)
            return CreateErrorResult(toolCall, $"Error: ripgrep invalid pattern '{args.pattern}': {stderr}");

        string result;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            result = $"No matches found for pattern: {args.pattern}";
        }
        else
        {
            string[] lines = stdout.Trim().Split('\n');
            result = lines.Length > 100
                ? string.Join("\n", lines.Take(100)) + $"\n\n... [showing 100 of {lines.Length} matches, refine your pattern to narrow results]"
                : stdout.Trim();
        }

        result = ContextManager.TruncateToolResult(result, Configuration.GetMaxToolResultTokens());
        return new ToolChatMessage(toolCall.Id, result);
    }

    private static async Task<(string stdout, string stderr, bool timedOut)> RunProcessWithTimeoutAsync(
        Process process, int timeoutMs, CancellationToken cancellationToken)
    {
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return (await stdoutTask, await stderrTask, false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var stdout = await SafeReadTask(stdoutTask);
            var stderr = await SafeReadTask(stderrTask);
            return (stdout, stderr, true);
        }
    }

    private static async Task<string> SafeReadTask(Task<string> task)
    {
        try { return await task; }
        catch { return ""; }
    }
}
