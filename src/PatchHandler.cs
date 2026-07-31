using System.Text;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
    private const int MaxDiffCells = 4_000_000;
    private const int MaxDiffHunks = 20;

    internal static async Task<ToolChatMessage?> ProcessApplyPatchCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.ApplyPatchCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"patch\": \"<unified diff>\"}",
            "applying patch",
            async args =>
            {
                if (args.file_path == null)
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: ApplyPatch tool missing required parameter 'file_path'.");
                if (args.patch == null)
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: ApplyPatch tool missing required parameter 'patch'.");

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                var hunks = ParseHunks(args.patch, out string? parseError);
                if (hunks == null)
                    return ResponseHandler.CreateErrorResult(toolCall, $"Error: {parseError}");

                if (!System.IO.File.Exists(safePath))
                {
                    return CreateNewFile(toolCall, safePath, args.file_path, hunks);
                }

                return ApplyHunks(toolCall, safePath, args.file_path, hunks);
            });
    }

    internal static async Task<ToolChatMessage?> ProcessDiffCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.DiffCall>(
            toolCall,
            "Expected format: {\"file_path\": \"<path>\", \"new_content\": \"<content>\"}",
            "generating diff",
            async args =>
            {
                if (args.file_path == null)
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Diff tool missing required parameter 'file_path'.");
                if (args.new_content == null)
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Diff tool missing required parameter 'new_content'.");

                string safePath = PathValidator.ValidatePath(args.file_path, Environment.CurrentDirectory);

                if (!System.IO.File.Exists(safePath))
                {
                    return ResponseHandler.CreateErrorResult(toolCall, $"Error: file not found '{args.file_path}'. The Diff tool compares the current file on disk with new_content; the file must exist.");
                }

                string oldText = System.IO.File.ReadAllText(safePath);
                string diff = GenerateUnifiedDiff(oldText, args.new_content, args.file_path);

                if (string.IsNullOrEmpty(diff))
                {
                    return new ToolChatMessage(toolCall.Id, $"No differences between '{args.file_path}' and new_content — the file is unchanged.");
                }

                diff = ContextManager.TruncateToolResult(diff, Configuration.GetMaxToolResultTokens());
                return new ToolChatMessage(toolCall.Id, diff);
            });
    }

    private static ToolChatMessage CreateNewFile(ChatToolCall toolCall, string safePath, string displayPath, List<Hunk> hunks)
    {
        foreach (var hunk in hunks)
        {
            if (hunk.ContextCount > 0 || hunk.RemovedCount > 0)
            {
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: file not found '{displayPath}'. A patch that creates a new file must contain only '+' (added) lines and no '-' or context lines. To modify an existing file, make sure the file exists or use the Write tool to create it first.");
            }
        }

        var lines = hunks
            .SelectMany(h => h.Lines.Where(e => e.Type == '+').Select(e => e.Text))
            .Select(TrimCR)
            .ToList();
        string content = JoinLines(lines, "\n", trailingNewline: true);
        System.IO.File.WriteAllText(safePath, content);

        int added = lines.Count;
        return new ToolChatMessage(toolCall.Id, $"Created new file {displayPath} ({hunks.Count} hunk(s), +{added} lines).");
    }

    private static ToolChatMessage ApplyHunks(ChatToolCall toolCall, string safePath, string displayPath, List<Hunk> hunks)
    {
        string raw = System.IO.File.ReadAllText(safePath);
        var originalLines = SplitLines(raw).ToList();
        var matchLines = originalLines.Select(TrimCR).ToList();
        bool fileEndsWithNewline = raw.EndsWith("\n");

        var placements = new List<(Hunk Hunk, int Index, bool Fuzzy, bool ReachesEnd)>();
        for (int i = 0; i < hunks.Count; i++)
        {
            var hunk = hunks[i];
            int? matchIndex = FindHunkMatch(hunk, matchLines, out bool fuzzy, out string? ambiguity);
            if (ambiguity != null)
            {
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}': {ambiguity}");
            }

            if (matchIndex == null)
            {
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}' — the changed lines were not found (declared around line {Math.Max(hunk.OldStart, 1)}). The file may have changed or the context lines do not match. Read the file to get the current content and retry with corrected context lines.");
            }

            bool reachesEnd = matchIndex + hunk.SearchBlockCount >= matchLines.Count;
            placements.Add((hunk, matchIndex.Value, fuzzy, reachesEnd));
        }

        foreach (var (hunk, index, _, _) in placements.OrderByDescending(p => p.Index))
        {
            ApplyHunkToLines(originalLines, hunk, index);
        }

        bool trailingNewline = fileEndsWithNewline;
        var lastHunk = placements.OrderBy(p => p.Index).Last();
        if (lastHunk.ReachesEnd)
        {
            trailingNewline = !lastHunk.Hunk.LastEntryHasNoNewlineMarker;
        }
        else if (lastHunk.Hunk.LastEntryHasNoNewlineMarker)
        {
            trailingNewline = false;
        }

        string eol = raw.Contains("\r\n") ? "\r\n" : "\n";
        string content = JoinLines(originalLines.Select(TrimCR), eol, trailingNewline);
        System.IO.File.WriteAllText(safePath, content);

        int totalAdded = placements.Sum(p => p.Hunk.AddedCount);
        int totalRemoved = placements.Sum(p => p.Hunk.RemovedCount);
        string fuzzyNote = placements.Any(p => p.Fuzzy)
            ? " (some hunks matched with fuzzy whitespace comparison)"
            : "";
        return new ToolChatMessage(toolCall.Id, $"Successfully applied {placements.Count} hunk(s) to {displayPath} (+{totalAdded} -{totalRemoved} lines){fuzzyNote}.");
    }

    private static int? FindHunkMatch(Hunk hunk, List<string> matchLines, out bool fuzzy, out string? ambiguity)
    {
        ambiguity = null;
        fuzzy = false;

        var searchBlock = hunk.Lines
            .Where(e => e.Type != '+')
            .Select(e => e.Text)
            .ToList();

        if (searchBlock.Count == 0)
        {
            int declared = Math.Clamp(hunk.OldStart - 1, 0, matchLines.Count);
            return declared;
        }

        int declaredIndex = hunk.OldStart - 1;

        var exact = FindLineSequence(matchLines, searchBlock, ExactCompare);
        if (exact.Count > 0)
            return ResolveCandidates(exact, declaredIndex, ref ambiguity, ref fuzzy);

        var normalized = FindLineSequence(matchLines, searchBlock, NormalizedCompare);
        if (normalized.Count > 0)
        {
            fuzzy = true;
            return ResolveCandidates(normalized, declaredIndex, ref ambiguity, ref fuzzy);
        }

        var stripped = FindLineSequence(matchLines, searchBlock, StrippedCompare);
        if (stripped.Count > 0)
        {
            fuzzy = true;
            return ResolveCandidates(stripped, declaredIndex, ref ambiguity, ref fuzzy);
        }

        return null;
    }

    private static int? ResolveCandidates(List<int> candidates, int declaredIndex, ref string? ambiguity, ref bool fuzzy)
    {
        if (candidates.Count == 1)
            return candidates[0];

        int bestDistance = candidates.Min(c => Math.Abs(c - declaredIndex));
        var best = candidates.Where(c => Math.Abs(c - declaredIndex) == bestDistance).ToList();
        if (best.Count == 1)
            return best[0];

        ambiguity = $"the context matched at multiple locations (lines {string.Join(", ", best.Select(b => b + 1))}). Include more unique context lines or use the Edit tool instead.";
        return null;
    }

    private static void ApplyHunkToLines(List<string> originalLines, Hunk hunk, int matchIndex)
    {
        var result = new List<string>();
        int contextCursor = matchIndex;
        foreach (var entry in hunk.Lines)
        {
            switch (entry.Type)
            {
                case '+':
                    result.Add(entry.Text);
                    break;
                case '-':
                    contextCursor++;
                    break;
                default:
                    result.Add(originalLines[contextCursor]);
                    contextCursor++;
                    break;
            }
        }

        int searchCount = hunk.SearchBlockCount;
        originalLines.RemoveRange(matchIndex, searchCount);
        originalLines.InsertRange(matchIndex, result);
    }

    internal static string GenerateUnifiedDiff(string oldText, string newText, string filePath)
    {
        var oldLines = SplitLines(oldText).Select(TrimCR).ToList();
        var newLines = SplitLines(newText).Select(TrimCR).ToList();

        if ((long)oldLines.Count * newLines.Count > MaxDiffCells)
        {
            throw new InvalidOperationException(
                $"file is too large for the Diff tool ({oldLines.Count} vs {newLines.Count} lines). Use the Edit or ApplyPatch tools instead, or diff a smaller section.");
        }

        var (changes, _, _) = ComputeEditScript(oldLines, newLines);
        if (!changes.Any(c => c.Type != ' '))
            return "";

        var hunks = GroupChangesIntoHunks(changes, contextLines: 3);

        var sb = new StringBuilder();
        sb.AppendLine($"--- {filePath}");
        sb.AppendLine($"+++ {filePath}");

        bool truncated = hunks.Count > MaxDiffHunks;
        foreach (var hunk in hunks.Take(MaxDiffHunks))
        {
            AppendHunk(sb, hunk);
        }

        if (truncated)
        {
            sb.AppendLine($"... [diff truncated, showing {MaxDiffHunks} of {hunks.Count} hunks]");
        }

        return sb.ToString();
    }

    private static void AppendHunk(StringBuilder sb, DiffHunk hunk)
    {
        string oldHeader = hunk.OldCount == 1 ? $"{hunk.OldStart}" : $"{hunk.OldStart},{hunk.OldCount}";
        string newHeader = hunk.NewCount == 1 ? $"{hunk.NewStart}" : $"{hunk.NewStart},{hunk.NewCount}";
        sb.AppendLine($"@@ -{oldHeader} +{newHeader} @@");

        foreach (var entry in hunk.Entries)
        {
            sb.Append(entry.Type);
            sb.AppendLine(entry.Text);
        }
    }

    private static (List<DiffChange> Changes, int OldCount, int NewCount) ComputeEditScript(List<string> oldLines, List<string> newLines)
    {
        int n = oldLines.Count;
        int m = newLines.Count;

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var changes = new List<DiffChange>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                changes.Add(new DiffChange(' ', oldLines[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                changes.Add(new DiffChange('-', oldLines[x]));
                x++;
            }
            else
            {
                changes.Add(new DiffChange('+', newLines[y]));
                y++;
            }
        }

        while (x < n)
        {
            changes.Add(new DiffChange('-', oldLines[x]));
            x++;
        }
        while (y < m)
        {
            changes.Add(new DiffChange('+', newLines[y]));
            y++;
        }

        return (changes, n, m);
    }

    private static List<DiffHunk> GroupChangesIntoHunks(List<DiffChange> changes, int contextLines)
    {
        var hunks = new List<DiffHunk>();
        int i = 0;
        while (i < changes.Count)
        {
            if (changes[i].Type != ' ')
            {
                int start = Math.Max(0, i - contextLines);
                int end = i;
                int lastChange = i;

                while (end < changes.Count)
                {
                    while (end < changes.Count && changes[end].Type != ' ')
                    {
                        lastChange = end;
                        end++;
                    }

                    int next = end + contextLines;
                    bool hasFollowingChange = false;
                    for (int j = end; j < next && j < changes.Count; j++)
                    {
                        if (changes[j].Type != ' ')
                        {
                            hasFollowingChange = true;
                            break;
                        }
                    }

                    if (!hasFollowingChange)
                        break;

                    end = next;
                    i = end;
                }

                end = Math.Min(changes.Count, lastChange + 1 + contextLines);
                hunks.Add(BuildDiffHunk(changes, start, end));
                i = end;
            }
            else
            {
                i++;
            }
        }

        return hunks;
    }

    private static DiffHunk BuildDiffHunk(List<DiffChange> changes, int start, int end)
    {
        int oldLinesSeen = 0, newLinesSeen = 0;

        for (int k = 0; k < start; k++)
        {
            if (changes[k].Type != '+')
                oldLinesSeen++;
            if (changes[k].Type != '-')
                newLinesSeen++;
        }

        int oldStart = oldLinesSeen + 1;
        int newStart = newLinesSeen + 1;

        var entries = changes.Skip(start).Take(end - start).ToList();

        int oldInHunk = entries.Count(e => e.Type != '+');
        int newInHunk = entries.Count(e => e.Type != '-');

        if (oldInHunk == 0)
            oldStart = oldLinesSeen == 0 ? 0 : oldLinesSeen;
        if (newInHunk == 0)
            newStart = newLinesSeen == 0 ? 0 : newLinesSeen;

        return new DiffHunk(oldStart, oldInHunk, newStart, newInHunk, entries);
    }

    private static List<int> FindLineSequence(List<string> content, List<string> pattern, Func<string, string, bool> compare)
    {
        var matches = new List<int>();
        for (int i = 0; i <= content.Count - pattern.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Count; j++)
            {
                if (!compare(content[i + j], pattern[j]))
                {
                    match = false;
                    break;
                }
            }
            if (match) matches.Add(i);
        }
        return matches;
    }

    private static bool ExactCompare(string a, string b) => a == b;

    private static bool NormalizedCompare(string a, string b) => NormalizeLine(a) == NormalizeLine(b);

    private static bool StrippedCompare(string a, string b) => StripLine(a) == StripLine(b);

    private static string NormalizeLine(string line)
    {
        var sb = new StringBuilder();
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
        return sb.ToString().Trim();
    }

    private static string StripLine(string line) =>
        string.Concat(line.Where(c => !char.IsWhiteSpace(c)));

    private static string TrimCR(string line) =>
        line.Length > 0 && line[line.Length - 1] == '\r' ? line.Substring(0, line.Length - 1) : line;

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl == -1)
            {
                lines.Add(text.Substring(pos));
                break;
            }
            lines.Add(text.Substring(pos, nl - pos));
            pos = nl + 1;
        }
        return lines;
    }

    private static string JoinLines(IEnumerable<string> lines, string eol, bool trailingNewline)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var line in lines)
        {
            if (!first) sb.Append(eol);
            sb.Append(line);
            first = false;
        }
        if (trailingNewline && sb.Length > 0)
            sb.Append(eol);
        return sb.ToString();
    }

    private static List<Hunk>? ParseHunks(string patch, out string? error)
    {
        error = null;
        var hunks = new List<Hunk>();
        Hunk? current = null;

        foreach (var rawLine in SplitLines(patch))
        {
            string line = TrimCR(rawLine);

            if (line.StartsWith("@@"))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                {
                    error = $"malformed hunk header '{line}'. Expected format: @@ -start,count +start,count @@";
                    return null;
                }

                current = new Hunk
                {
                    OldStart = int.Parse(match.Groups[1].Value),
                    NewStart = int.Parse(match.Groups[3].Value)
                };
                hunks.Add(current);
                continue;
            }

            if (current == null)
            {
                if (line.Length == 0 || line[0] != ' ')
                    continue;
                error = $"expected a @@ hunk header but found '{line}'. The patch must contain at least one @@ -start,count +start,count @@ hunk.";
                return null;
            }

            if (line.StartsWith("\\ No newline at end of file"))
            {
                if (current.Lines.Count > 0)
                    current.LastEntryHasNoNewlineMarker = true;
                continue;
            }

            if (line.Length == 0)
                continue;

            char prefix = line[0];
            if (prefix != ' ' && prefix != '-' && prefix != '+')
            {
                error = $"unexpected line '{line}' inside hunk. Expected lines starting with ' ' (context), '-' (removed), or '+' (added).";
                return null;
            }

            string text = line.Substring(1);
            current.Lines.Add(new PatchLine(prefix, text));
        }

        if (hunks.Count == 0)
        {
            error = "no hunks found in the patch. Expected one or more @@ -start,count +start,count @@ sections followed by ' ' (context), '-' (removed), and '+' (added) lines.";
            return null;
        }

        return hunks;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    private sealed class Hunk
    {
        public int OldStart { get; set; }
        public int NewStart { get; set; }
        public List<PatchLine> Lines { get; } = [];

        public int SearchBlockCount => Lines.Count(e => e.Type != '+');
        public int ContextCount => Lines.Count(e => e.Type == ' ');
        public int RemovedCount => Lines.Count(e => e.Type == '-');
        public int AddedCount => Lines.Count(e => e.Type == '+');
        public bool LastEntryHasNoNewlineMarker { get; set; }
    }

    private sealed record PatchLine(char Type, string Text);

    private sealed record DiffChange(char Type, string Text);

    private sealed class DiffHunk
    {
        public DiffHunk(int oldStart, int oldCount, int newStart, int newCount, List<DiffChange> entries)
        {
            OldStart = oldStart;
            OldCount = oldCount;
            NewStart = newStart;
            NewCount = newCount;
            Entries = entries;
        }

        public int OldStart { get; }
        public int OldCount { get; }
        public int NewStart { get; }
        public int NewCount { get; }
        public List<DiffChange> Entries { get; }
    }
}
