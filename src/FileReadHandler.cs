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
        FileStateJournal.RecordRead(safePath, string.Join("\n", lines));

        var (startLine, endLine, note) = ComputeReadRange(args, lines);
        string fileText = FormatReadLines(lines, startLine, endLine, note);
        return new ToolChatMessage(toolCall.Id, TruncateToTokenLimit(fileText, lines, startLine, endLine));
    }

    private static (int Start, int End, string Note) ComputeReadRange(ToolHandler.ReadFileCall args, string[] lines)
    {
        int startLine = ParseLineNumber(args.start_line, 1);
        int endLine = ParseLineNumber(args.end_line, lines.Length);

        if (startLine < 1) startLine = 1;
        if (startLine > lines.Length) startLine = lines.Length + 1;
        if (endLine > lines.Length) endLine = lines.Length;
        if (endLine < startLine) endLine = startLine;

        string note = "";
        if (args.end_line == null && args.start_line != null)
        {
            var range = FindEnclosingMethodRange(lines, startLine - 1);
            if (range.HasValue)
            {
                startLine = range.Value.Start + 1;
                endLine = range.Value.End + 1;
                note = $"[Read expanded to enclosing method: lines {startLine}-{endLine}]\n";
            }
        }

        return (startLine, endLine, note);
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
        return shown + $"\n\n... [truncated: showing lines {startLine}-{lastShown} of {lines.Length}. Use Read with start_line/end_line to fetch the remaining lines in ranges.]";
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

    private static int ParseLineNumber(string? value, int fallback)
    {
        return int.TryParse(value, out int n) ? n : fallback;
    }

    private static (int Start, int End)? FindEnclosingMethodRange(string[] lines, int targetLine)
    {
        if (lines.Length == 0 || targetLine < 0 || targetLine >= lines.Length) return null;

        int braceLine = FindMethodOpeningBraceLine(lines, targetLine);
        if (braceLine < 0) return null;

        int signatureStart = FindSignatureStartLine(lines, braceLine);
        int closeLine = FindClosingBraceLine(lines, braceLine);

        return closeLine < 0 ? null : (signatureStart, closeLine);
    }

    private static int FindMethodOpeningBraceLine(string[] lines, int targetLine)
    {
        for (int i = targetLine; i >= 0; i--)
        {
            if (!lines[i].TrimEnd().EndsWith('{')) continue;
            if (IsMethodOpeningBrace(lines, i))
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindSignatureStartLine(string[] lines, int braceLine)
    {
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
        return signatureStart;
    }

    private static int FindClosingBraceLine(string[] lines, int braceLine)
    {
        int depth = 0;
        bool inString = false;
        for (int i = braceLine; i < lines.Length; i++)
        {
            ScanLineForBraces(lines[i], ref depth, ref inString);
            if (depth == 0 && i > braceLine)
            {
                return i;
            }
        }
        return -1;
    }

    private static void ScanLineForBraces(string line, ref int depth, ref bool inString)
    {
        for (int c = 0; c < line.Length; c++)
        {
            char ch = line[c];
            if (inString)
            {
                if (ch == '"' && !IsEscaped(line, c)) inString = false;
                continue;
            }
            if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/') return;
            if (ch == '{') depth++;
            else if (ch == '}') depth--;
            else if (ch == '"') inString = true;
        }
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

    private static readonly string[] ControlKeywords =
    {
        "if ", "for ", "while ", "foreach ", "switch ", "catch ", "using ", "lock ", "else ", "return ", "do "
    };

    private static bool IsControlKeywordStart(string line)
    {
        return ControlKeywords.Any(k => line.StartsWith(k, StringComparison.Ordinal));
    }

    private static bool IsEscaped(string line, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && line[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }
}
