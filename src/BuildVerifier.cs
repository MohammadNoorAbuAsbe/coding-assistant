using System.Diagnostics;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class BuildVerifier
{
    internal static bool IsFileModifyingFunction(string functionName)
    {
        return functionName == ToolHandler.EditFunctionName
            || functionName == ToolHandler.ApplyPatchFunctionName
            || functionName == ToolHandler.WriteFunctionName;
    }

    internal static bool HasDotnetProject()
    {
        string cwd = Environment.CurrentDirectory;
        if (Directory.EnumerateFiles(cwd, "*.sln").Any() || Directory.EnumerateFiles(cwd, "*.csproj").Any())
        {
            return true;
        }

        return Directory.EnumerateDirectories(cwd)
            .Any(dir => Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.csproj").Any());
    }

    internal static async Task<UserChatMessage> RunAsync(CancellationToken cancellationToken)
    {
        string command = Configuration.GetVerifyCommand();
        int timeoutMs = Configuration.GetVerifyTimeout();
        var (stdout, stderr, exitCode, timedOut) = await RunProcessAsync(command, timeoutMs, cancellationToken);

        string output;
        if (timedOut)
        {
            output = $"Build verification timed out after {timeoutMs / 1000} seconds.\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
        }
        else if (exitCode == 0)
        {
            output = "Build succeeded. Your changes compile.";
        }
        else
        {
            output = $"Build FAILED with exit code {exitCode}.\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
        }

        output = ContextManager.TruncateToolResult(output, Configuration.GetMaxToolResultTokens());
        return new UserChatMessage($"Automatic build verification ({command}):\n{output}\n\nIf the build failed, fix the errors with Edit or ApplyPatch, then verify with PowerShell until it succeeds. If the errors are unrelated to your changes, do not keep trying to fix them — tell the user.");
    }

    private static async Task<(string stdout, string stderr, int exitCode, bool timedOut)> RunProcessAsync(
        string command, int timeoutMs, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var startInfo = new ProcessStartInfo
        {
            FileName = parts[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var arg in parts.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd(), cancellationToken);
        var stderrTask = Task.Run(() => process.StandardError.ReadToEnd(), cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return (await stdoutTask, await stderrTask, process.ExitCode, false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var stdout = await SafeReadTask(stdoutTask);
            var stderr = await SafeReadTask(stderrTask);
            return (stdout, stderr, -1, true);
        }
    }

    private static async Task<string> SafeReadTask(Task<string> task)
    {
        try { return await task; }
        catch { return ""; }
    }
}
