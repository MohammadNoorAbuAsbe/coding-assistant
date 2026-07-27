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
            ToolHandler.EditFunctionName => ProcessEditFileCall(toolCall),
            ToolHandler.EditLineFunctionName => ProcessEditLineCall(toolCall),
            ToolHandler.BashFunctionName => ProcessBashCall(toolCall),
            ToolHandler.GrepFunctionName => ProcessGrepCall(toolCall),
            _ => CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.EditFunctionName}, {ToolHandler.EditLineFunctionName}, {ToolHandler.BashFunctionName}, {ToolHandler.GrepFunctionName}.")
        };
    }

    private static ToolChatMessage CreateErrorResult(ChatToolCall toolCall, string errorMessage)
    {
        Console.Error.WriteLine($"[tool error] {errorMessage}");
        return new ToolChatMessage(toolCall.Id, errorMessage);
    }

    private static T? ValidateAndDeserialize<T>(ChatToolCall toolCall) where T : class
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

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        var readFileCall = ValidateAndDeserialize<ToolHandler.ReadFileCall>(toolCall);
        if (readFileCall == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool called with invalid arguments. Expected format: {\"file_path\": \"<path>\"}");
        }

        if (readFileCall.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
        }

        try
        {
            string[] lines = System.IO.File.ReadAllLines(readFileCall.file_path);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.AppendLine($"{i + 1}: {lines[i]}");
            }
            string fileText = sb.ToString();
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
        var writeFileCall = ValidateAndDeserialize<ToolHandler.WriteFileCall>(toolCall);
        if (writeFileCall == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool called with invalid arguments. Expected format: {\"file_path\": \"<path>\", \"content\": \"<content>\"}");
        }

        if (writeFileCall.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'file_path'.");
        }

        if (writeFileCall.content == null)
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

    private static ToolChatMessage? ProcessEditFileCall(ChatToolCall toolCall)
    {
        var editCall = ValidateAndDeserialize<ToolHandler.EditFileCall>(toolCall);
        if (editCall == null)
        {
            return CreateErrorResult(toolCall, "Error: Edit tool called with invalid arguments. Expected format: {\"file_path\": \"<path>\", \"old_string\": \"<text>\", \"new_string\": \"<text>\"}");
        }

        if (editCall.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'file_path'.");
        }

        if (editCall.old_string == null)
        {
            return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'old_string'.");
        }

        if (editCall.new_string == null)
        {
            return CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'new_string'.");
        }

        try
        {
            if (!System.IO.File.Exists(editCall.file_path))
            {
                return CreateErrorResult(toolCall, $"Error: file not found '{editCall.file_path}'.");
            }

            string content = System.IO.File.ReadAllText(editCall.file_path);

            var match = FindBestMatch(content, editCall.old_string);
            if (match == null)
            {
                return CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{editCall.file_path}'. Use the EditLine tool instead - Read the file first to see line numbers, then use EditLine with start_line and end_line.");
            }

            string newContent = content.Substring(0, match.Index) + editCall.new_string + content.Substring(match.Index + match.Length);
            System.IO.File.WriteAllText(editCall.file_path, newContent);

            string note = match.Strategy == MatchStrategy.Exact ? "" : $" (matched using {match.Strategy} comparison)";
            return new ToolChatMessage(toolCall.Id, $"Successfully edited {editCall.file_path}{note}.");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error editing file '{editCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessEditLineCall(ChatToolCall toolCall)
    {
        var editCall = ValidateAndDeserialize<ToolHandler.EditLineCall>(toolCall);
        if (editCall == null)
        {
            return CreateErrorResult(toolCall, "Error: EditLine tool called with invalid arguments. Expected format: {\"file_path\": \"<path>\", \"start_line\": \"<number>\", \"end_line\": \"<number>\", \"new_content\": \"<text>\"}");
        }

        if (editCall.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: EditLine tool missing required parameter 'file_path'.");
        }

        if (!int.TryParse(editCall.start_line, out int startLine) || startLine < 1)
        {
            return CreateErrorResult(toolCall, "Error: EditLine tool 'start_line' must be a positive integer.");
        }

        if (!int.TryParse(editCall.end_line, out int endLine) || endLine < startLine)
        {
            return CreateErrorResult(toolCall, "Error: EditLine tool 'end_line' must be >= start_line.");
        }

        if (editCall.new_content == null)
        {
            return CreateErrorResult(toolCall, "Error: EditLine tool missing required parameter 'new_content'.");
        }

        try
        {
            if (!System.IO.File.Exists(editCall.file_path))
            {
                return CreateErrorResult(toolCall, $"Error: file not found '{editCall.file_path}'.");
            }

            string[] lines = System.IO.File.ReadAllLines(editCall.file_path);

            if (startLine > lines.Length)
            {
                return CreateErrorResult(toolCall, $"Error: start_line {startLine} exceeds file length ({lines.Length} lines).");
            }

            int endIdx = Math.Min(endLine, lines.Length);
            var newLines = new List<string>();
            newLines.AddRange(lines.Take(startLine - 1));
            newLines.Add(editCall.new_content);
            newLines.AddRange(lines.Skip(endIdx));

            System.IO.File.WriteAllLines(editCall.file_path, newLines);
            int newTotalLines = newLines.Count;
            int newContentStart = startLine;
            int newContentEnd = startLine + editCall.new_content.Split('\n').Length - 1;
            return new ToolChatMessage(toolCall.Id, $"Successfully edited {editCall.file_path} (replaced lines {startLine}-{endLine} with {newContentStart}-{newContentEnd}). File now has {newTotalLines} lines. ALWAYS re-read the file before your next edit to get fresh line numbers.");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error editing file '{editCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessBashCall(ChatToolCall toolCall)
    {
        var bashCall = ValidateAndDeserialize<ToolHandler.BashCommandCall>(toolCall);
        if (bashCall == null)
        {
            return CreateErrorResult(toolCall, "Error: Bash tool called with invalid arguments. Expected format: {\"command\": \"<command>\"}");
        }

        if (bashCall.command == null)
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
        var grepCall = ValidateAndDeserialize<ToolHandler.GrepCall>(toolCall);
        if (grepCall == null)
        {
            return CreateErrorResult(toolCall, "Error: Grep tool called with invalid arguments. Expected format: {\"pattern\": \"<regex>\"}");
        }

        if (grepCall.pattern == null)
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

    private enum MatchStrategy
    {
        Exact,
        CaseInsensitive,
        NormalizedWhitespace
    }

    private record MatchResult(int Index, int Length, MatchStrategy Strategy);

    private static MatchResult? FindBestMatch(string content, string oldString)
    {
        int firstIndex = content.IndexOf(oldString, StringComparison.Ordinal);
        if (firstIndex != -1)
        {
            int lastIndex = content.LastIndexOf(oldString, StringComparison.Ordinal);
            if (firstIndex == lastIndex)
                return new MatchResult(firstIndex, oldString.Length, MatchStrategy.Exact);
        }

        firstIndex = content.IndexOf(oldString, StringComparison.OrdinalIgnoreCase);
        if (firstIndex != -1)
        {
            int lastIndex = content.LastIndexOf(oldString, StringComparison.OrdinalIgnoreCase);
            if (firstIndex == lastIndex)
                return new MatchResult(firstIndex, oldString.Length, MatchStrategy.CaseInsensitive);
        }

        string normalizedContent = NormalizeWhitespace(content);
        string normalizedOld = NormalizeWhitespace(oldString);
        firstIndex = normalizedContent.IndexOf(normalizedOld, StringComparison.Ordinal);
        if (firstIndex != -1)
        {
            int lastIndex = normalizedContent.LastIndexOf(normalizedOld, StringComparison.Ordinal);
            if (firstIndex == lastIndex)
            {
                int originalIndex = FindOriginalPosition(content, normalizedContent, firstIndex);
                return new MatchResult(originalIndex, oldString.Length, MatchStrategy.NormalizedWhitespace);
            }
        }

        return null;
    }

    private static string NormalizeWhitespace(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    result.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                result.Append(c);
                lastWasSpace = false;
            }
        }

        return result.ToString();
    }

    private static int FindOriginalPosition(string original, string normalized, int normalizedIndex)
    {
        int origPos = 0;
        int normPos = 0;

        while (normPos < normalizedIndex && origPos < original.Length)
        {
            if (char.IsWhiteSpace(original[origPos]))
            {
                if (normPos < normalized.Length && normalized[normPos] == ' ')
                    normPos++;
                while (origPos < original.Length && char.IsWhiteSpace(original[origPos]))
                    origPos++;
            }
            else
            {
                if (normPos < normalized.Length && original[origPos] == normalized[normPos])
                    normPos++;
                origPos++;
            }
        }

        return origPos;
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
