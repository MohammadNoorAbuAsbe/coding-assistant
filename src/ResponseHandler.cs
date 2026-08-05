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
            ToolHandler.PowershellFunctionName => ProcessPowershellCallAsync(toolCall, cancellationToken),
            ToolHandler.GlobFunctionName => Task.FromResult<ToolChatMessage?>(ProcessGlobCall(toolCall)),
            ToolHandler.GrepFunctionName => ProcessGrepCallAsync(toolCall, cancellationToken),
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

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.ReadFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"start_line\": \"<int>\", \"end_line\": \"<int>\"}",
            "reading file",
            args =>
            {
                if (args.file_path == null)
                {
                    return CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
                }

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                if (System.IO.Directory.Exists(safePath))
                {
                    return ListDirectory(toolCall, args.file_path, safePath);
                }

                if (!System.IO.File.Exists(safePath))
                {
                    return CreateErrorResult(toolCall, BuildFileNotFoundMessage(args.file_path, safePath));
                }

                if (IsBinaryFile(safePath))
                {
                    return CreateErrorResult(toolCall, $"Error reading file: '{args.file_path}' appears to be a binary file and cannot be read as text.");
                }

                string[] lines = System.IO.File.ReadAllLines(safePath);
                FileStateJournal.RecordRead(safePath, string.Join("\n", lines));

                int startLine = ParseLineNumber(args.start_line, 1);
                if (startLine < 1) startLine = 1;
                if (startLine > lines.Length) startLine = lines.Length + 1;
                int endLine = ParseLineNumber(args.end_line, lines.Length);
                if (endLine > lines.Length) endLine = lines.Length;
                if (endLine < startLine) endLine = startLine;

                string expansionNote = "";
                if (args.end_line == null && args.start_line != null)
                {
                    var range = FindEnclosingMethodRange(lines, startLine - 1);
                    if (range.HasValue)
                    {
                        startLine = range.Value.Start + 1;
                        endLine = range.Value.End + 1;
                        expansionNote = $"[Read expanded to enclosing method: lines {startLine}-{endLine}]\n";
                    }
                }

                var sb = new System.Text.StringBuilder();
                sb.Append(expansionNote);
                for (int i = startLine - 1; i < endLine && i < lines.Length; i++)
                {
                    sb.AppendLine($"{i + 1}: {lines[i]}");
                }

                string fileText = sb.ToString();
                int maxTokens = Configuration.GetMaxToolResultTokens();
                if (ContextManager.EstimateTokens(fileText) > maxTokens)
                {
                    int idx = ContextManager.GetTokenLimitIndex(fileText, maxTokens);
                    int cut = idx > 0 ? fileText.LastIndexOf('\n', idx - 1) : -1;
                    if (cut < 0) cut = Math.Max(0, idx);
                    string shown = fileText[..cut];
                    int shownLines = shown.Count(c => c == '\n');
                    int lastShown = startLine + shownLines;
                    if (lastShown > endLine) lastShown = endLine;
                    fileText = shown + $"\n\n... [truncated: showing lines {startLine}-{lastShown} of {lines.Length}. Use Read with start_line/end_line to fetch the remaining lines in ranges.]";
                }

                return new ToolChatMessage(toolCall.Id, fileText);
            });
    }

    private static string BuildFileNotFoundMessage(string displayPath, string safePath)
    {
        string message = $"Error reading file: '{displayPath}' was not found.";

        string? dir = System.IO.Path.GetDirectoryName(safePath);
        string? baseName = System.IO.Path.GetFileName(safePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName) || !System.IO.Directory.Exists(dir))
        {
            return message;
        }

        var candidates = new List<(string Name, double Score)>();
        try
        {
            foreach (string sibling in System.IO.Directory.EnumerateFiles(dir))
            {
                string name = System.IO.Path.GetFileName(sibling);
                if (name == null) continue;
                double score = NameSimilarity(name, baseName);
                if (score >= 0.4)
                {
                    candidates.Add((name, score));
                }
            }
        }
        catch
        {
            return message;
        }

        if (candidates.Count == 0) return message;

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        string suggestions = string.Join("\n", candidates.Take(3).Select(c => $"  {c.Name}"));
        return $"{message} Did you mean one of these?\n{suggestions}";
    }

    private static double NameSimilarity(string a, string b)
    {
        string la = a.ToLowerInvariant();
        string lb = b.ToLowerInvariant();
        if (la == lb) return 1.0;
        if (la.Contains(lb, StringComparison.Ordinal) || lb.Contains(la, StringComparison.Ordinal)) return 0.75;

        int maxLen = Math.Max(la.Length, lb.Length);
        if (maxLen == 0) return 0;

        int prefix = 0;
        int minLen = Math.Min(la.Length, lb.Length);
        while (prefix < minLen && la[prefix] == lb[prefix]) prefix++;
        double prefixRatio = (double)prefix / maxLen;

        int overlap = 0;
        foreach (char c in lb)
        {
            if (la.Contains(c, StringComparison.Ordinal)) overlap++;
        }
        double overlapRatio = (double)overlap / lb.Length;

        return Math.Max(prefixRatio, overlapRatio * 0.8);
    }

    private static ToolChatMessage ListDirectory(ChatToolCall toolCall, string displayPath, string safePath)
    {
        var entries = new List<string>();
        try
        {
            foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(safePath))
            {
                string name = System.IO.Path.GetFileName(entry) ?? entry;
                entries.Add(System.IO.Directory.Exists(entry) ? name + "/" : name);
            }
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error reading directory '{displayPath}': {ex.Message}");
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        string result = $"Directory listing of {displayPath} ({entries.Count} entries):\n{string.Join("\n", entries)}";
        result = ContextManager.TruncateToolResult(result, Configuration.GetMaxToolResultTokens());
        return new ToolChatMessage(toolCall.Id, result);
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".class", ".jar", ".war", ".bin", ".dat", ".obj", ".o", ".a", ".lib", ".wasm",
        ".pyc", ".pyo", ".pyd",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tiff", ".svgz",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".flac", ".ogg",
        ".ttf", ".otf", ".woff", ".woff2", ".eot"
    };

    private static bool IsBinaryFile(string safePath)
    {
        string ext = System.IO.Path.GetExtension(safePath);
        if (BinaryExtensions.Contains(ext)) return true;

        try
        {
            using var fs = new System.IO.FileStream(safePath, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            if (fs.Length == 0) return false;
            int toRead = (int)Math.Min(8192, fs.Length);
            var buffer = new byte[toRead];
            int read = fs.Read(buffer, 0, toRead);
            if (read == 0) return false;

            // UTF-16 BOM => text
            if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
                return false;

            int nonPrintable = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
                if (buffer[i] < 9 || (buffer[i] > 13 && buffer[i] < 32)) nonPrintable++;
            }
            return (double)nonPrintable / read > 0.3;
        }
        catch
        {
            return false;
        }
    }

    private static int ParseLineNumber(string? value, int fallback)
    {
        return int.TryParse(value, out int n) ? n : fallback;
    }

    private static (int Start, int End)? FindEnclosingMethodRange(string[] lines, int targetLine)
    {
        if (lines.Length == 0 || targetLine < 0 || targetLine >= lines.Length) return null;

        int braceLine = -1;
        for (int i = targetLine; i >= 0; i--)
        {
            if (!lines[i].TrimEnd().EndsWith('{')) continue;
            if (IsMethodOpeningBrace(lines, i))
            {
                braceLine = i;
                break;
            }
        }
        if (braceLine < 0) return null;

        int signatureStart = braceLine;
        for (int i = braceLine - 1; i >= 0; i--)
        {
            string trimmed = lines[i].TrimEnd();
            if (trimmed.Length == 0) break;
            char last = trimmed[^1];
            if (last == '(' || last == ')' || last == ',')
            {
                signatureStart = i;
            }
            else
            {
                break;
            }
        }

        int depth = 0;
        bool inString = false;
        for (int i = braceLine; i < lines.Length; i++)
        {
            string line = lines[i];
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (inString)
                {
                    if (ch == '"' && !IsEscaped(line, c)) inString = false;
                    continue;
                }
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') break;
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
                else if (ch == '"') inString = true;
            }
            if (depth == 0 && i > braceLine)
            {
                return (signatureStart, i);
            }
        }
        return null;
    }

    private static bool IsMethodOpeningBrace(string[] lines, int braceIndex)
    {
        for (int i = braceIndex - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) return false;
            if (line.EndsWith('{') || line.EndsWith('}') || line.EndsWith(';')) return false;
            if (line.Contains('('))
            {
                return !IsControlKeywordStart(line);
            }
        }
        return false;
    }

    private static bool IsControlKeywordStart(string line)
    {
        foreach (string keyword in new[] { "if ", "for ", "while ", "foreach ", "switch ", "catch ", "using ", "lock ", "else ", "return ", "do " })
        {
            if (line.StartsWith(keyword, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsEscaped(string line, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && line[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
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

                bool existed = System.IO.File.Exists(safePath);
                UndoJournal.Record(safePath, existed ? System.IO.File.ReadAllText(safePath) : null, existed, ToolHandler.WriteFunctionName);
                string written = RepairContentEncoding(args.content);
                System.IO.File.WriteAllText(safePath, written);
                FileStateJournal.RecordWrite(safePath, written);
                return new ToolChatMessage(toolCall.Id, $"Successfully wrote content to {args.file_path}");
            });
    }

    private static ToolChatMessage? ProcessEditFileCall(ChatToolCall toolCall)
    {
        return ExecuteToolCall<ToolHandler.EditFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"old_string\": \"<text>\", \"new_string\": \"<text>\"}",
            "editing file",
            args => ExecuteEditFile(toolCall, args));
    }

    private static ToolChatMessage ExecuteEditFile(ChatToolCall toolCall, ToolHandler.EditFileCall args)
    {
        var validationError = ValidateEditArgs(toolCall, args);
        if (validationError != null)
        {
            return validationError;
        }

        string safePath = PathValidator.ValidatePath(args.file_path!, Environment.CurrentDirectory);
        if (!System.IO.File.Exists(safePath))
        {
            return CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'.");
        }

        string oldString = RepairContentEncoding(args.old_string!);
        string newString = RepairContentEncoding(args.new_string!);

        string content = System.IO.File.ReadAllText(safePath);

        if (!FileStateJournal.HasState(safePath))
        {
            return CreateErrorResult(toolCall, $"Error: Edit tool cannot edit '{args.file_path}' because the file was not read in this session. Use the Read tool to read the file first, then retry the Edit with old_string copied verbatim from the Read output.");
        }

        string? notice = null;
        if (FileStateJournal.IsStale(safePath, content))
        {
            notice = $"Warning: '{args.file_path}' changed on disk since the session last read or wrote it. The edit was applied to the current content, but Read the file to verify the result.";
        }

        if (args.replace_all != null &&
            (args.replace_all.Equals("true", StringComparison.OrdinalIgnoreCase) || args.replace_all == "1"))
        {
            return ApplyReplaceAllEditAndCreateResult(toolCall, args.file_path!, safePath, content, oldString, newString, notice);
        }

        var exactMatches = MatchFinder.FindAllExactMatches(content, oldString);
        if (exactMatches.Count > 1)
        {
            return CreateErrorResult(toolCall, $"Error: the specified 'old_string' matches the file in more than one place ({exactMatches.Count} occurrences). Add more surrounding context (adjacent lines) to the old_string to make it unique, or set replace_all to 'true' to replace all occurrences.");
        }

        var match = MatchFinder.FindBestMatch(content, oldString);
        if (match == null)
        {
            string region = BuildClosestRegionSuggestion(content, oldString);
            string hint = string.IsNullOrEmpty(region)
                ? ""
                : $" Closest region in the file (copy from here verbatim — line-number prefixes are stripped automatically):\n{region}";
            return CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{args.file_path}'. The old_string does not match any text in the file — it likely contains lines you did not actually read, or text you invented. Use Read with start_line/end_line to fetch the exact target lines, then retry with old_string copied verbatim from the Read output. Never invent or reconstruct lines from memory.{hint}");
        }

        if (IsDisproportionateMatch(content, match, oldString))
        {
            int matchedLines = content.Substring(match.Index, match.Length).Split('\n').Length;
            int oldLines = oldString.Split('\n').Length;
            return CreateErrorResult(toolCall, $"Error: refusing the edit to '{args.file_path}' because the matched span is much larger than old_string (matched {matchedLines} lines for an old_string of {oldLines} lines). The old_string probably matched in an unexpected location, which would delete unrelated code. Re-read the file and provide the full exact oldString for the intended replacement.");
        }

        return ApplyEditAndCreateResult(toolCall, args.file_path!, safePath, content, match, newString, notice);
    }

    private static bool IsDisproportionateMatch(string content, MatchResult match, string oldString)
    {
        int oldLines = oldString.Split('\n').Length;
        string matched = content.Substring(match.Index, match.Length);
        int matchedLines = matched.Split('\n').Length;
        if (matchedLines >= Math.Max(oldLines + 3, oldLines * 2)) return true;
        if (oldLines == 1) return false;
        return matched.Trim().Length > Math.Max(oldString.Trim().Length + 500, oldString.Trim().Length * 4);
    }

    private static string BuildClosestRegionSuggestion(string content, string oldString)
    {
        string? target = null;
        foreach (string line in oldString.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                target = trimmed;
                break;
            }
        }
        if (target == null) return "";

        string[] contentLines = content.Split('\n');
        int bestIdx = -1;
        double bestScore = 0;
        for (int i = 0; i < contentLines.Length; i++)
        {
            string line = contentLines[i].Trim();
            if (line.Length == 0) continue;
            if (line == target)
            {
                bestIdx = i;
                bestScore = 1.0;
                break;
            }

            int maxLen = Math.Min(line.Length, target.Length);
            int prefix = 0;
            while (prefix < maxLen && line[prefix] == target[prefix]) prefix++;
            double score = (double)prefix / Math.Max(line.Length, target.Length);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        if (bestIdx < 0 || bestScore <= 0.15) return "";

        int start = Math.Max(0, bestIdx - 4);
        int end = Math.Min(contentLines.Length - 1, bestIdx + 3);
        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end; i++)
        {
            sb.AppendLine($"{i + 1}: {contentLines[i].TrimEnd('\r')}");
        }
        string snippet = sb.ToString();
        if (snippet.Length > 700) snippet = snippet[..700] + "\n...";
        return snippet;
    }

    private static ToolChatMessage? ValidateEditArgs(ChatToolCall toolCall, ToolHandler.EditFileCall args)
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
        return null;
    }

    private static ToolChatMessage ApplyEditAndCreateResult(
        ChatToolCall toolCall,
        string filePath,
        string safePath,
        string content,
        MatchResult match,
        string newString,
        string? notice = null)
    {
        string eol = content.Contains("\r\n") ? "\r\n" : "\n";
        string normalizedNew = newString.Replace("\r\n", "\n");
        if (eol == "\r\n")
        {
            normalizedNew = normalizedNew.Replace("\n", "\r\n");
        }

        string rawOld = content.Substring(match.Index, match.Length);
        if (normalizedNew.Length == 0 && !rawOld.EndsWith('\n') && !rawOld.EndsWith('\r'))
        {
            int after = match.Index + match.Length;
            if (after + 1 < content.Length && content[after] == '\r' && content[after + 1] == '\n')
                match = match with { Length = match.Length + 2 };
            else if (after < content.Length && content[after] == '\n')
                match = match with { Length = match.Length + 1 };
        }

        string newContent = content.Substring(0, match.Index) + normalizedNew + content.Substring(match.Index + match.Length);
        UndoJournal.Record(safePath, content, existedBefore: true, ToolHandler.EditFunctionName);
        System.IO.File.WriteAllText(safePath, newContent);
        FileStateJournal.RecordWrite(safePath, newContent);

        string note = GetMatchNote(match);
        string? diff = TryGenerateDiff(content, newContent, filePath);

        string matched = content.Substring(match.Index, match.Length);
        int startLine = CountNewlines(content, 0, match.Index) + 1;
        int oldLines = CountNewlines(matched, 0, matched.Length) + 1;
        int newLines = CountNewlines(normalizedNew, 0, normalizedNew.Length) + 1;
        int oldTotal = CountNewlines(content, 0, content.Length) + 1;
        int newTotal = CountNewlines(newContent, 0, newContent.Length) + 1;

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(notice))
        {
            sb.Append(notice).Append("\n\n");
        }
        sb.Append($"Successfully edited {filePath}{note}.");
        if (match.Strategy != MatchStrategy.Exact)
        {
            sb.Append("\nCAUTION: old_string matched approximately — verify this edit landed where you intended (re-Read the region if unsure).");
        }
        sb.Append($"\nEdit location: lines {startLine}-{startLine + oldLines - 1} ({oldLines} line(s) replaced with {newLines} line(s)).");
        sb.Append($"\nFile now has {newTotal} lines (was {oldTotal}).");
        if (!string.IsNullOrEmpty(diff))
        {
            sb.Append("\n\n").Append(diff);
        }

        return new ToolChatMessage(toolCall.Id, ContextManager.TruncateToolResult(sb.ToString(), Configuration.GetMaxToolResultTokens()));
    }

    private static int CountNewlines(string text, int start, int count)
    {
        int total = 0;
        for (int i = start; i < start + count; i++)
        {
            if (text[i] == '\n') total++;
        }
        return total;
    }

    private static ToolChatMessage ApplyReplaceAllEditAndCreateResult(
        ChatToolCall toolCall,
        string filePath,
        string safePath,
        string content,
        string oldString,
        string newString,
        string? notice = null)
    {
        var matches = MatchFinder.FindAllExactMatches(content, oldString);
        if (matches.Count == 0)
        {
            return CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{filePath}'. The old_string does not match any text in the file — it likely contains lines you did not actually read, or text you invented. Use Read with start_line/end_line to fetch the exact target lines, then retry with old_string copied verbatim from the Read output. Never invent or reconstruct lines from memory.");
        }

        string eol = content.Contains("\r\n") ? "\r\n" : "\n";
        string normalizedNew = newString.Replace("\r\n", "\n");
        if (eol == "\r\n")
        {
            normalizedNew = normalizedNew.Replace("\n", "\r\n");
        }

        var sb = new System.Text.StringBuilder(content);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            sb.Remove(matches[i].Index, matches[i].Length);
            sb.Insert(matches[i].Index, normalizedNew);
        }
        string newContent = sb.ToString();

        UndoJournal.Record(safePath, content, existedBefore: true, ToolHandler.EditFunctionName);
        System.IO.File.WriteAllText(safePath, newContent);
        FileStateJournal.RecordWrite(safePath, newContent);

        string? diff = TryGenerateDiff(content, newContent, filePath);
        int oldTotal = CountNewlines(content, 0, content.Length) + 1;
        int newTotal = CountNewlines(newContent, 0, newContent.Length) + 1;

        var msg = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(notice))
        {
            msg.Append(notice).Append("\n\n");
        }
        msg.Append($"Successfully edited {filePath}: replaced {matches.Count} occurrence(s) of the old_string.");
        msg.Append($"\nFile now has {newTotal} lines (was {oldTotal}).");
        if (!string.IsNullOrEmpty(diff))
        {
            msg.Append("\n\n").Append(diff);
        }

        return new ToolChatMessage(toolCall.Id, ContextManager.TruncateToolResult(msg.ToString(), Configuration.GetMaxToolResultTokens()));
    }

    private static string GetMatchNote(MatchResult match) => match.Strategy switch
    {
        MatchStrategy.Exact => "",
        MatchStrategy.LineLcs when match.Confidence is double c => $" (matched using LCS comparison, confidence {c:0.00})",
        _ => $" (matched using {match.Strategy} comparison)"
    };

    private static string? TryGenerateDiff(string content, string newContent, string filePath)
    {
        try
        {
            return PatchHandler.GenerateUnifiedDiff(content, newContent, filePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<ToolChatMessage?> ProcessPowershellCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ExecuteToolCallAsync<ToolHandler.PowershellCommandCall>(
            toolCall,
            "Expected format: {\"command\": \"<command>\"}",
            "executing command",
            args => ExecutePowershellCommandAsync(toolCall, args, cancellationToken));
    }

    private static async Task<ToolChatMessage> ExecutePowershellCommandAsync(ChatToolCall toolCall, ToolHandler.PowershellCommandCall args, CancellationToken cancellationToken)
    {
        if (args.command == null)
            return CreateErrorResult(toolCall, "Error: PowerShell tool missing required parameter 'command'.");

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

                string safePath = !string.IsNullOrWhiteSpace(args.path)
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
