using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class FileReadHandler
{
    internal static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.ReadFileCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"start_line\": \"<int>\", \"end_line\": \"<int>\"}",
            "reading file",
            args =>
            {
                if (args.file_path == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
                }

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);
                return ReadFileCore(toolCall, args, safePath);
            });
    }

    private static ToolChatMessage ReadFileCore(ChatToolCall toolCall, ToolHandler.ReadFileCall args, string safePath)
    {
        if (System.IO.Directory.Exists(safePath))
        {
            return ListDirectory(toolCall, args.file_path, safePath);
        }

        if (!System.IO.File.Exists(safePath))
        {
            return ResponseHandler.CreateErrorResult(toolCall, BuildFileNotFoundMessage(args.file_path, safePath));
        }

        if (IsBinaryFile(safePath))
        {
            return ResponseHandler.CreateErrorResult(toolCall, $"Error reading file: '{args.file_path}' appears to be a binary file and cannot be read as text.");
        }

        string[] lines = System.IO.File.ReadAllLines(safePath);

        const int startLine = 1;
        int endLine = lines.Length;

        // Read dedup: if this file was already read this session and the
        // file has not changed on disk, the content is still in the
        // conversation. Return a short pointer instead of re-injecting tokens.
        if (SessionContext.FileState.TryGetReadCoverage(safePath, out int knownStart, out int knownEnd)
            && !SessionContext.FileState.IsStale(safePath, string.Join("\n", lines))
            && startLine >= knownStart && endLine <= knownEnd)
        {
            string coverage = knownStart == knownEnd
                ? $"line {knownStart}"
                : $"lines {knownStart}-{knownEnd}";
            return new ToolChatMessage(toolCall.Id,
                $"[Read skipped: '{args.file_path}' was already read this session ({coverage}) and is unchanged on disk — content as previously returned.]");
        }

        string fileText = FormatReadLines(lines, startLine, endLine, "");
        if (SessionContext.FileState.HasState(safePath) && SessionContext.FileState.IsStale(safePath, string.Join("\n", lines)))
        {
            fileText = "[File changed on disk since last read — content below is current.]\n" + fileText;
        }

        SessionContext.FileState.RecordRead(safePath, string.Join("\n", lines), startLine, endLine);
        return new ToolChatMessage(toolCall.Id, TruncateToTokenLimit(fileText, lines, startLine, endLine));
    }

    private static (int Start, int End, string Note) ComputeReadRange(ToolHandler.ReadFileCall args, string[] lines)
    {
        return (1, lines.Length, "");
    }

    private static string FormatReadLines(string[] lines, int startLine, int endLine, string note)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(note);
        for (int i = startLine - 1; i < endLine && i < lines.Length; i++)
        {
            sb.AppendLine($"{i + 1}: {lines[i]}");
        }
        return sb.ToString();
    }

    private static string TruncateToTokenLimit(string fileText, string[] lines, int startLine, int endLine)
    {
        int maxTokens = Configuration.GetMaxToolResultTokens();
        if (ContextManager.EstimateTokens(fileText) <= maxTokens)
        {
            return fileText;
        }

        int idx = ContextManager.GetTokenLimitIndex(fileText, maxTokens);
        int cut = idx > 0 ? fileText.LastIndexOf('\n', idx - 1) : -1;
        if (cut < 0) cut = Math.Max(0, idx);
        string shown = fileText[..cut];
        int lastShown = startLine + shown.Count(c => c == '\n');
        if (lastShown > endLine) lastShown = endLine;
        return shown + $"\n\n... [truncated: showing lines {startLine}-{lastShown} of {lines.Length}. This file exceeds the tool-result token budget and cannot be returned in full.]";
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

        int overlap = lb.Count(c => la.Contains(c, StringComparison.Ordinal));
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
            return ResponseHandler.CreateErrorResult(toolCall, $"Error reading directory '{displayPath}': {ex.Message}");
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
            return read > 0 && IsBinaryContent(buffer, read);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBinaryContent(byte[] buffer, int read)
    {
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
}
