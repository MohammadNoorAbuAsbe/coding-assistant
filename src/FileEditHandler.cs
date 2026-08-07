using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class FileEditHandler
{
    internal static ToolChatMessage? ProcessWriteFileCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.WriteFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"content\": \"<content>\"}",
            "writing file",
            args =>
            {
                if (args.file_path == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'file_path'.");
                }

                if (args.content == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'content'.");
                }

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                string? directory = System.IO.Path.GetDirectoryName(safePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                bool existed = System.IO.File.Exists(safePath);
                SessionContext.Undo.Record(safePath, existed ? System.IO.File.ReadAllText(safePath) : null, existed, ToolHandler.WriteFunctionName);
                string written = ResponseHandler.RepairContentEncoding(args.content);
                System.IO.File.WriteAllText(safePath, written);
                SessionContext.FileState.RecordWrite(safePath, written);
                return new ToolChatMessage(toolCall.Id, $"Successfully wrote content to {args.file_path}");
            });
    }

    internal static ToolChatMessage? ProcessEditFileCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.EditFileCall>(
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

        string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);
        if (!System.IO.File.Exists(safePath))
        {
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'.");
        }

        string oldString = ResponseHandler.RepairContentEncoding(args.old_string);
        string newString = ResponseHandler.RepairContentEncoding(args.new_string);

        string content = System.IO.File.ReadAllText(safePath);

        if (!SessionContext.FileState.HasState(safePath))
        {
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: Edit tool cannot edit '{args.file_path}' because the file was not read in this session. Use the Read tool to read the file first, then retry the Edit with old_string copied verbatim from the Read output.");
        }

        string? notice = null;
        if (SessionContext.FileState.IsStale(safePath, content))
        {
            notice = $"Warning: '{args.file_path}' changed on disk since the session last read or wrote it. The edit was applied to the current content, but Read the file to verify the result.";
        }

        if (args.replace_all != null &&
            (args.replace_all.Equals("true", StringComparison.OrdinalIgnoreCase) || args.replace_all == "1"))
        {
            return ApplyReplaceAllEditAndCreateResult(toolCall, args.file_path, safePath, content, oldString, newString, notice);
        }

        var exactMatches = MatchFinder.FindAllExactMatches(content, oldString);
        if (exactMatches.Count > 1)
        {
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: the specified 'old_string' matches the file in more than one place ({exactMatches.Count} occurrences). Add more surrounding context (adjacent lines) to the old_string to make it unique, or set replace_all to 'true' to replace all occurrences.");
        }

        var match = MatchFinder.FindBestMatch(content, oldString);
        if (match == null)
        {
            string region = BuildClosestRegionSuggestion(content, oldString);
            string hint = string.IsNullOrEmpty(region)
                ? ""
                : $" Closest region in the file (copy from here verbatim — line-number prefixes are stripped automatically):\n{region}";
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{args.file_path}'. The old_string does not match any text in the file — it likely contains lines you did not actually read, or text you invented. Use Read with start_line/end_line to fetch the exact target lines, then retry with old_string copied verbatim from the Read output. Never invent or reconstruct lines from memory.{hint}");
        }

        if (IsDisproportionateMatch(content, match, oldString))
        {
            int matchedLines = content.Substring(match.Index, match.Length).Split('\n').Length;
            int oldLines = oldString.Split('\n').Length;
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: refusing the edit to '{args.file_path}' because the matched span is much larger than old_string (matched {matchedLines} lines for an old_string of {oldLines} lines). The old_string probably matched in an unexpected location, which would delete unrelated code. Re-read the file and provide the full exact oldString for the intended replacement.");
        }

        return ApplyEditAndCreateResult(toolCall, args.file_path, safePath, content, match, newString, notice);
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
        string? target = FindFirstNonEmptyLine(oldString);
        if (target == null) return "";

        string[] contentLines = content.Split('\n');
        int bestIdx = FindClosestLineIndex(contentLines, target);
        if (bestIdx < 0) return "";

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

    private static string? FindFirstNonEmptyLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return null;
    }

    private static int FindClosestLineIndex(string[] lines, string target)
    {
        int bestIdx = -1;
        double bestScore = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line == target)
            {
                return i;
            }

            double score = SimilarityRatio(line, target);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }
        return bestScore > 0.15 ? bestIdx : -1;
    }

    private static double SimilarityRatio(string line, string target)
    {
        int maxLen = Math.Min(line.Length, target.Length);
        int prefix = 0;
        while (prefix < maxLen && line[prefix] == target[prefix]) prefix++;
        return (double)prefix / Math.Max(line.Length, target.Length);
    }

    private static ToolChatMessage? ValidateEditArgs(ChatToolCall toolCall, ToolHandler.EditFileCall args)
    {
        if (args.file_path == null)
        {
            return ResponseHandler.CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'file_path'.");
        }
        if (args.old_string == null)
        {
            return ResponseHandler.CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'old_string'.");
        }
        if (args.new_string == null)
        {
            return ResponseHandler.CreateErrorResult(toolCall, "Error: Edit tool missing required parameter 'new_string'.");
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
        SessionContext.Undo.Record(safePath, content, existedBefore: true, ToolHandler.EditFunctionName);
        System.IO.File.WriteAllText(safePath, newContent);
        SessionContext.FileState.RecordWrite(safePath, newContent);

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
            return ResponseHandler.CreateErrorResult(toolCall, $"Error: Edit tool could not find the specified 'old_string' in '{filePath}'. The old_string does not match any text in the file — it likely contains lines you did not actually read, or text you invented. Use Read with start_line/end_line to fetch the exact target lines, then retry with old_string copied verbatim from the Read output. Never invent or reconstruct lines from memory.");
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

        SessionContext.Undo.Record(safePath, content, existedBefore: true, ToolHandler.EditFunctionName);
        System.IO.File.WriteAllText(safePath, newContent);
        SessionContext.FileState.RecordWrite(safePath, newContent);

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
}
