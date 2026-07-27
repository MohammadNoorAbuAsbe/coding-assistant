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

                var match = FindBestMatch(content, args.old_string);
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

                string rgPath = FindRipgrep() ?? "";
                if (string.IsNullOrEmpty(rgPath))
                {
                    return CreateErrorResult(toolCall, "Error: ripgrep (rg) not found. Install it with: winget install BurntSushi.ripgrep.MSVC");
                }

                string arguments = BuildRipgrepArguments(args);

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
        NormalizedWhitespace,
        LineNormalized,
        LineFuzzy
    }

    private sealed record MatchResult(int Index, int Length, MatchStrategy Strategy);

    private static MatchResult? FindBestMatch(string content, string oldString)
    {
        if (string.IsNullOrEmpty(oldString)) return null;

        var result = TryMatch(content, oldString, StringComparison.Ordinal, MatchStrategy.Exact);
        if (result != null) return result;

        result = TryMatch(content, oldString, StringComparison.OrdinalIgnoreCase, MatchStrategy.CaseInsensitive);
        if (result != null) return result;

        result = TryMatchLineNormalized(content, oldString);
        if (result != null) return result;

        result = TryMatchLineFuzzy(content, oldString);
        if (result != null) return result;

        return null;
    }

    private static MatchResult? TryMatch(string content, string oldString, StringComparison comparison, MatchStrategy strategy)
    {
        int first = content.IndexOf(oldString, comparison);
        if (first == -1) return null;

        int last = content.LastIndexOf(oldString, comparison);
        return first == last ? new MatchResult(first, oldString.Length, strategy) : null;
    }

    private static MatchResult? TryMatchLineNormalized(string content, string oldString)
    {
        var (contentLines, lineOffsets) = SplitIntoLines(content);
        var oldLines = SplitIntoLines(oldString).Lines;
        if (oldLines.Length == 0) return null;

        var contentNorm = contentLines.Select(NormalizeLine).ToArray();
        var oldNorm = oldLines.Select(NormalizeLine).ToArray();

        var matches = FindLineSequence(contentNorm, oldNorm);
        if (matches.Count == 0) return null;
        if (matches.Count > 1) return null;

        return BuildLineMatchResult(content, contentLines, oldLines, oldNorm[0], lineOffsets, matches[0], MatchStrategy.LineNormalized);
    }

    private static MatchResult? TryMatchLineFuzzy(string content, string oldString)
    {
        var (contentLines, lineOffsets) = SplitIntoLines(content);
        var oldLines = SplitIntoLines(oldString).Lines;
        if (oldLines.Length == 0) return null;

        var contentStrip = contentLines.Select(StripLine).ToArray();
        var oldStrip = oldLines.Select(StripLine).ToArray();

        var matches = FindLineSequence(contentStrip, oldStrip);
        if (matches.Count == 0) return null;
        if (matches.Count > 1) return null;

        return BuildLineMatchResult(content, contentLines, oldLines, NormalizeLine(oldLines[0]), lineOffsets, matches[0], MatchStrategy.LineFuzzy);
    }

    private static (string[] Lines, int[] Offsets) SplitIntoLines(string text)
    {
        var lines = new List<string>();
        var offsets = new List<int>();
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl == -1)
            {
                lines.Add(text.Substring(pos));
                offsets.Add(pos);
                break;
            }
            lines.Add(text.Substring(pos, nl - pos));
            offsets.Add(pos);
            pos = nl + 1;
        }
        return ([.. lines], [.. offsets]);
    }

    private static List<int> FindLineSequence(string[] content, string[] pattern)
    {
        var matches = new List<int>();
        for (int i = 0; i <= content.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (content[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) matches.Add(i);
        }
        return matches;
    }

    private static string NormalizeLine(string line)
    {
        if (line.Length > 0 && line[line.Length - 1] == '\r')
            line = line.Substring(0, line.Length - 1);

        var sb = new System.Text.StringBuilder();
        bool lastWasSpace = false;
        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripLine(string line)
    {
        if (line.Length > 0 && line[line.Length - 1] == '\r')
            line = line.Substring(0, line.Length - 1);

        return string.Concat(line.Where(c => !char.IsWhiteSpace(c)));
    }

    private static MatchResult? BuildLineMatchResult(string content, string[] contentLines, string[] oldLines, string oldFirstNormalized, int[] lineOffsets, int matchLine, MatchStrategy strategy)
    {
        int posInLine = FindPositionInLine(contentLines[matchLine], oldLines[0], oldFirstNormalized);
        if (posInLine == -1) return null;

        int absolutePos = lineOffsets[matchLine] + posInLine;

        int lastLineIdx = matchLine + oldLines.Length - 1;
        int endOfMatch = lineOffsets[lastLineIdx] + contentLines[lastLineIdx].Length;
        int contentLength = endOfMatch - absolutePos;

        if (absolutePos + contentLength > content.Length) return null;

        string actual = content.Substring(absolutePos, contentLength);
        string actualFirstLine = actual.Split('\n')[0];

        bool firstLineMatches =
            actualFirstLine == oldLines[0] ||
            NormalizeLine(actualFirstLine) == NormalizeLine(oldLines[0]) ||
            StripLine(actualFirstLine) == StripLine(oldLines[0]);

        if (!firstLineMatches) return null;

        return new MatchResult(absolutePos, contentLength, strategy);
    }

    private static int FindPositionInLine(string contentLine, string oldFirstLine, string oldFirstNormalized)
    {
        int idx = contentLine.IndexOf(oldFirstLine, StringComparison.Ordinal);
        if (idx != -1) return idx;

        idx = contentLine.IndexOf(oldFirstLine, StringComparison.OrdinalIgnoreCase);
        if (idx != -1) return idx;

        string contentNorm = NormalizeLine(contentLine);
        int normIdx = contentNorm.IndexOf(oldFirstNormalized, StringComparison.Ordinal);
        if (normIdx == -1) return -1;

        return MapNormalizedIndexToOriginalPosition(contentLine, contentNorm, normIdx);
    }

    private static int MapNormalizedIndexToOriginalPosition(string contentLine, string contentNorm, int normIdx)
    {
        int origPos = 0, normPos = 0;
        while (normPos < normIdx && origPos < contentLine.Length)
        {
            if (char.IsWhiteSpace(contentLine[origPos]))
            {
                AdvanceOverWhitespace(contentLine, contentNorm, ref origPos, ref normPos);
            }
            else
            {
                AdvanceOverNormalCharacter(contentLine, contentNorm, ref origPos, ref normPos);
            }
        }
        return origPos;
    }

    private static void AdvanceOverWhitespace(string contentLine, string contentNorm, ref int origPos, ref int normPos)
    {
        if (normPos < contentNorm.Length && contentNorm[normPos] == ' ')
            normPos++;
        while (origPos < contentLine.Length && char.IsWhiteSpace(contentLine[origPos]))
            origPos++;
    }

    private static void AdvanceOverNormalCharacter(string contentLine, string contentNorm, ref int origPos, ref int normPos)
    {
        if (normPos < contentNorm.Length && contentLine[origPos] == contentNorm[normPos])
            normPos++;
        origPos++;
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
