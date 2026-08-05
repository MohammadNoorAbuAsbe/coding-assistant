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

    /// <summary>
    /// Picks the verification command for the workspace, or null to skip.
    /// An explicit VERIFY_COMMAND env override always wins; otherwise the
    /// project type is detected: .NET solution/project, TypeScript project
    /// with a local tsc binary, or no known build system.
    /// </summary>
    internal static string? ResolveVerifyCommand()
    {
        string? overrideCmd = Configuration.GetVerifyCommandOverride();
        if (!string.IsNullOrWhiteSpace(overrideCmd)) return overrideCmd;

        if (HasDotnetProject()) return "dotnet build --nologo -v q";

        string tsconfig = Path.Combine(Environment.CurrentDirectory, "tsconfig.json");
        if (System.IO.File.Exists(tsconfig))
        {
            string tscDir = Path.Combine(Environment.CurrentDirectory, "node_modules", ".bin");
            if (System.IO.File.Exists(Path.Combine(tscDir, "tsc.cmd")) || System.IO.File.Exists(Path.Combine(tscDir, "tsc")))
            {
                return $"\"{Path.Combine(tscDir, "tsc.cmd")}\" --noEmit -p \"{tsconfig}\"";
            }
        }

        return null;
    }

    internal static async Task<UserChatMessage> RunAsync(CancellationToken cancellationToken)
    {
        string? command = ResolveVerifyCommand();
        if (command == null)
        {
            string skipped = "Build verification skipped: no supported build system detected (.sln/.csproj or tsconfig.json with a local tsc). Set the VERIFY_COMMAND environment variable to enable automatic verification for this project.";
            skipped = ContextManager.TruncateToolResult(skipped, Configuration.GetMaxToolResultTokens());
            return new UserChatMessage($"Automatic build verification:\n{skipped}");
        }

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
            output = $"Build FAILED with exit code {exitCode}.";
            string errors = ParseBuildErrors(stdout + "\n" + stderr);
            if (!string.IsNullOrEmpty(errors))
            {
                output += $"\n\nErrors:\n{errors}";
            }
            output += $"\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
        }

        output = ContextManager.TruncateToolResult(output, Configuration.GetMaxToolResultTokens());
        return new UserChatMessage($"Automatic build verification ({command}):\n{output}\n\nIf the build failed, fix the errors with Edit or ApplyPatch, then verify with PowerShell until it succeeds. If the errors are unrelated to your changes, do not keep trying to fix them — tell the user.");
    }

    /// <summary>
    /// Extracts compiler error lines ("path(line,col): error CSxxxx: message")
    /// into a compact, scannable list, capped at 15 lines.
    /// </summary>
    internal static string ParseBuildErrors(string output)
    {
        const int MaxErrors = 15;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.Length > 400) continue;
            if (!line.Contains(": error ", StringComparison.OrdinalIgnoreCase)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(line, @"\(\d+(,\d+)?\):")) continue;

            if (seen.Add(line) && errors.Count < MaxErrors)
            {
                errors.Add("  " + line);
            }
        }

        if (errors.Count == 0) return "";
        return string.Join("\n", errors) + (seen.Count > MaxErrors ? $"\n  ... and {seen.Count - MaxErrors} more" : "");
    }

    private static async Task<(string stdout, string stderr, int exitCode, bool timedOut)> RunProcessAsync(
        string command, int timeoutMs, CancellationToken cancellationToken)
    {
        var parts = SplitCommandLine(command);
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

    private static string[] SplitCommandLine(string command)
    {
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in command)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens.ToArray();
    }
}
