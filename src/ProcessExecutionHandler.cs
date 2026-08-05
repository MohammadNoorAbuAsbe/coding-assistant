using System.Diagnostics;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class ProcessExecutionHandler
{
    internal static async Task<ToolChatMessage?> ProcessPowershellCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.PowershellCommandCall>(
            toolCall,
            "Expected format: {\"command\": \"<command>\"}",
            "executing command",
            args => ExecutePowershellCommandAsync(toolCall, args, cancellationToken));
    }

    private static async Task<ToolChatMessage> ExecutePowershellCommandAsync(ChatToolCall toolCall, ToolHandler.PowershellCommandCall args, CancellationToken cancellationToken)
    {
        if (args.command == null)
            return ResponseHandler.CreateErrorResult(toolCall, "Error: PowerShell tool missing required parameter 'command'.");

        bool isWindows = OperatingSystem.IsWindows();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "powershell.exe" : "bash",
                Arguments = $"{(isWindows ? "-Command" : "-c")} \"{args.command.Replace("\"", "\\\"")}\"",
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
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: command was cancelled by the user.\n\n{partialOutput}");
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: command timed out after {timeoutMs / 1000} seconds.\n\n{partialOutput}");
        }

        string result = process.ExitCode == 0
            ? stdout
            : $"Exit code: {process.ExitCode}\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";

        result = ContextManager.TruncateToolResult(result, Configuration.GetMaxToolResultTokens());
        return new ToolChatMessage(toolCall.Id, result);
    }

    internal static ToolChatMessage? ProcessGlobCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.GlobCall>(
            toolCall,
            "Expected format: {\"pattern\": \"<glob>\", \"path\": \"<directory>\"}",
            "running glob",
            args =>
            {
                if (args.pattern == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Glob tool missing required parameter 'pattern'.");
                }

                string safePath = !string.IsNullOrWhiteSpace(args.path)
                    ? PathValidator.ValidatePath(args.path, Environment.CurrentDirectory)
                    : Environment.CurrentDirectory;

                string result = GlobHelper.FindFiles(args.pattern, safePath);

                int maxTokens = Configuration.GetMaxToolResultTokens();
                result = ContextManager.TruncateToolResult(result, maxTokens);

                return new ToolChatMessage(toolCall.Id, result);
            });
    }

    internal static async Task<ToolChatMessage?> ProcessGrepCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.GrepCall>(
            toolCall,
            "Expected format: {\"pattern\": \"<regex>\"}",
            "running ripgrep",
            args => ExecuteGrepCommandAsync(toolCall, args, cancellationToken));
    }

    private static async Task<ToolChatMessage> ExecuteGrepCommandAsync(ChatToolCall toolCall, ToolHandler.GrepCall args, CancellationToken cancellationToken)
    {
        if (args.pattern == null)
            return ResponseHandler.CreateErrorResult(toolCall, "Error: Grep tool missing required parameter 'pattern'.");

        string rgPath = RipgrepHelper.FindRipgrep() ?? "";
        if (string.IsNullOrEmpty(rgPath))
            return ResponseHandler.CreateErrorResult(toolCall, "Error: ripgrep (rg) not found. Install it with: winget install BurntSushi.ripgrep.MSVC");

        string safePath = !string.IsNullOrWhiteSpace(args.path)
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
                return ResponseHandler.CreateErrorResult(toolCall, "Error: ripgrep search was cancelled by the user.");
            return ResponseHandler.CreateErrorResult(toolCall, "Error: ripgrep search timed out after 10 seconds. Try a more specific pattern or path.");
        }

        if (process.ExitCode == 2)
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: ripgrep invalid pattern '{args.pattern}': {stderr}");

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
        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd(), cancellationToken);
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd(), cancellationToken);

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
            await process.WaitForExitAsync(CancellationToken.None);
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
