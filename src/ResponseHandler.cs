using System.Diagnostics;
using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ResponseHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
            ToolHandler.BashFunctionName => ProcessBashCall(toolCall),
            ToolHandler.GrepFunctionName => ProcessGrepCall(toolCall),
            _ => CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.BashFunctionName}, {ToolHandler.GrepFunctionName}.")
        };
    }

    private static ToolChatMessage CreateErrorResult(ChatToolCall toolCall, string errorMessage)
    {
        Console.Error.WriteLine($"[tool error] {errorMessage}");
        return new ToolChatMessage(toolCall.Id, errorMessage);
    }

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool called with no arguments. Expected format: {\"file_path\": \"<path>\"}");
        }

        ToolHandler.ReadFileCall? readFileCall;
        try
        {
            readFileCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.ReadFileCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Read tool arguments: {ex.Message}. Expected format: {{\"file_path\": \"<path>\"}}");
        }

        if (readFileCall?.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
        }

        try
        {
            string fileText = System.IO.File.ReadAllText(readFileCall.file_path);
            int maxTokens = Configuration.GetMaxToolResultTokens();
            fileText = ContextManager.TruncateToolResult(fileText, maxTokens);
            return new ToolChatMessage(toolCall.Id, fileText);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error reading file '{readFileCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessWriteFileCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool called with no arguments. Expected format: {\"file_path\": \"<path>\", \"content\": \"<content>\"}");
        }

        ToolHandler.WriteFileCall? writeFileCall;
        try
        {
            writeFileCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.WriteFileCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Write tool arguments: {ex.Message}. Expected format: {{\"file_path\": \"<path>\", \"content\": \"<content>\"}}");
        }

        if (writeFileCall?.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'file_path'.");
        }

        if (writeFileCall?.content == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'content'.");
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(writeFileCall.file_path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(writeFileCall.file_path, writeFileCall.content);
            return new ToolChatMessage(toolCall.Id, $"Successfully wrote content to {writeFileCall.file_path}");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error writing file '{writeFileCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessBashCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Bash tool called with no arguments. Expected format: {\"command\": \"<command>\"}");
        }

        ToolHandler.BashCommandCall? bashCall;
        try
        {
            bashCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.BashCommandCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Bash tool arguments: {ex.Message}. Expected format: {{\"command\": \"<command>\"}}");
        }

        if (bashCall?.command == null)
        {
            return CreateErrorResult(toolCall, "Error: Bash tool missing required parameter 'command'.");
        }

        try
        {
            bool isWindows = OperatingSystem.IsWindows();
            string shell = isWindows ? "powershell.exe" : "bash";
            string argumentsPrefix = isWindows ? "-Command" : "-c";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"{argumentsPrefix} \"{bashCall.command}\"",
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
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error executing command '{bashCall.command}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessGrepCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Grep tool called with no arguments. Expected format: {\"pattern\": \"<regex>\"}");
        }

        ToolHandler.GrepCall? grepCall;
        try
        {
            grepCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.GrepCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Grep tool arguments: {ex.Message}. Expected format: {{\"pattern\": \"<regex>\"}}");
        }

        if (grepCall?.pattern == null)
        {
            return CreateErrorResult(toolCall, "Error: Grep tool missing required parameter 'pattern'.");
        }

        try
        {
            string rgPath = FindRipgrep() ?? "";
            if (string.IsNullOrEmpty(rgPath))
            {
                return CreateErrorResult(toolCall, "Error: ripgrep (rg) not found. Install it with: winget install BurntSushi.ripgrep.MSVC");
            }

            string arguments = BuildRipgrepArguments(grepCall);

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
                return CreateErrorResult(toolCall, $"Error: ripgrep invalid pattern '{grepCall.pattern}': {stderr}");
            }

            string result;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                result = $"No matches found for pattern: {grepCall.pattern}";
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
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error running ripgrep: {ex.Message}");
        }
    }

    private static string? FindRipgrep()
    {
        bool isWindows = OperatingSystem.IsWindows();
        string executableName = isWindows ? "rg.exe" : "rg";

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                string fullPath = Path.Combine(dir, executableName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        if (isWindows)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] knownPaths =
            [
                Path.Combine(localAppData, @"Microsoft\WinGet\Packages"),
                Path.Combine(localAppData, @"Programs\ripgrep"),
                Path.Combine(programFiles, "ripgrep"),
                Path.Combine(programFilesX86, "ripgrep")
            ];

            return knownPaths
                .Where(Directory.Exists)
                .Select(basePath => FindFileInDirectory(basePath, executableName))
                .FirstOrDefault(found => found != null);
        }

        return null;
    }

    private static string? FindFileInDirectory(string basePath, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(basePath, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string BuildRipgrepArguments(ToolHandler.GrepCall grepCall)
    {
        var args = new List<string>();

        args.Add("--max-count");
        args.Add("50");
        args.Add("--max-columns");
        args.Add("200");
        args.Add("--max-columns-preview");
        args.Add("-n");

        if (string.Equals(grepCall.case_insensitive, "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-i");
        }

        if (!string.IsNullOrEmpty(grepCall.context_lines) && int.TryParse(grepCall.context_lines, out int ctx) && ctx > 0)
        {
            args.Add("-C");
            args.Add(ctx.ToString());
        }

        if (!string.IsNullOrEmpty(grepCall.exclude))
        {
            args.Add("--glob");
            args.Add($"!{grepCall.exclude}");
        }

        if (!string.IsNullOrEmpty(grepCall.include))
        {
            args.Add("--glob");
            args.Add(grepCall.include);
        }

        args.Add($"\"{grepCall.pattern}\"");

        if (!string.IsNullOrEmpty(grepCall.path))
        {
            args.Add($"\"{grepCall.path}\"");
        }

        return string.Join(" ", args);
    }
}
