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

        var placements = new List<(int HunkNumber, Hunk Hunk, int Index, MatchStrategy Strategy, double? Confidence, bool ReachesEnd, int SpanLength)>();
        int noOps = 0;
        for (int i = 0; i < hunks.Count; i++)
        {
            var hunk = hunks[i];

            if (hunk.RemovedCount == 0 && hunk.AddedCount == 0)
            {
                noOps++;
                continue;
            }

            int? matchIndex = FindHunkMatch(hunk, matchLines, out MatchStrategy strategy, out string? ambiguity, out double? confidence, out int spanLength);
            if (ambiguity != null)
            {
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}': {ambiguity}");
            }

            if (matchIndex == null)
            {
                string location = hunk.OldStart > 0 ? $"declared around line {hunk.OldStart}" : "with no declared position";
                return ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}' — the changed lines were not found ({location}). The file may have changed or the context lines do not match. Read the file to get the current content and retry with corrected context lines.");
            }

            bool reachesEnd = matchIndex + spanLength >= matchLines.Count;
            placements.Add((i + 1, hunk, matchIndex.Value, strategy, confidence, reachesEnd, spanLength));
        }

        if (placements.Count == 0)
        {
            string note = noOps > 0
                ? $"all {noOps} hunk(s) were context-only no-ops; no changes were made"
                : "no changes";
            return new ToolChatMessage(toolCall.Id, $"Successfully applied the patch to {displayPath} ({note}).");
        }

        foreach (var (_, hunk, index, _, _, _, span) in placements.OrderByDescending(p => p.Index))
        {
            ApplyHunkToLines(originalLines, hunk, index, span);
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

        string? diff;
        try
        {
            diff = GenerateUnifiedDiff(raw, content, displayPath);
        }
        catch (InvalidOperationException)
        {
            diff = null;
        }

        var sb = new StringBuilder();
        sb.Append($"Successfully applied {placements.Count} hunk(s) to {displayPath} (+{totalAdded} -{totalRemoved} lines).");
        foreach (var (num, hunk, _, strategy, conf, _, _) in placements)
        {
            sb.Append($"\n  hunk {num}: {DescribeHunkMatch(hunk, strategy, conf)}");
        }
        if (noOps > 0)
        {
            sb.Append($"\n  {noOps} context-only no-op hunk(s) skipped.");
        }
        if (!string.IsNullOrEmpty(diff))
        {
            sb.Append("\n\n").Append(diff);
        }
        return new ToolChatMessage(toolCall.Id, ContextManager.TruncateToolResult(sb.ToString(), Configuration.GetMaxToolResultTokens()));
    }

    private static string DescribeHunkMatch(Hunk hunk, MatchStrategy strategy, double? confidence)
    {
        if (hunk.SearchBlockCount == 0)
            return "matched at declared position";
        return strategy switch
        {
            MatchStrategy.Exact => "matched exactly",
            MatchStrategy.NormalizedWhitespace => "matched fuzzily (whitespace)",
            MatchStrategy.UnicodeNormalized => "matched fuzzily (unicode)",
            MatchStrategy.LineLcs when confidence is double c => $"matched using LCS comparison (confidence {c:0.00})",
            _ => "matched"
        };
    }

    private static int? FindHunkMatch(Hunk hunk, List<string> matchLines, out MatchStrategy strategy, out string? ambiguity, out double? confidence, out int spanLength)
    {
        ambiguity = null;
        strategy = MatchStrategy.Exact;
        confidence = null;
        spanLength = hunk.SearchBlockCount;

        var searchBlock = hunk.Lines
            .Where(e => e.Type != '+')
            .Select(e => e.Text)
            .ToList();

        if (searchBlock.Count == 0)
        {
            if (hunk.OldStart < 1)
            {
                ambiguity = "cannot determine where to insert these lines — the hunk header has no position and the hunk has no context lines. Add context lines or use a '@@ -start,0 +start,count @@' header.";
                return null;
            }
            int declared = Math.Clamp(hunk.OldStart - 1, 0, matchLines.Count);
            spanLength = 0;
            return declared;
        }

        int declaredIndex = hunk.OldStart - 1;

        var exact = FindLineSequence(matchLines, searchBlock, ExactCompare);
        if (exact.Count > 0)
        {
            spanLength = searchBlock.Count;
            return ResolveCandidates(exact, declaredIndex, ref ambiguity);
        }

        var normalized = FindLineSequence(matchLines, searchBlock, NormalizedCompare);
        if (normalized.Count > 0)
        {
            strategy = MatchStrategy.NormalizedWhitespace;
            spanLength = searchBlock.Count;
            return ResolveCandidates(normalized, declaredIndex, ref ambiguity);
        }

        var unicode = FindLineSequence(matchLines, searchBlock, UnicodeNormalizedCompare);
        if (unicode.Count > 0)
        {
            strategy = MatchStrategy.UnicodeNormalized;
            spanLength = searchBlock.Count;
            return ResolveCandidates(unicode, declaredIndex, ref ambiguity);
        }

        var lcs = FindLineSequenceLcs(matchLines, searchBlock, out double lcsConfidence);
        if (lcs.Count > 0)
        {
            strategy = MatchStrategy.LineLcs;
            confidence = lcsConfidence;
            var resolved = ResolveCandidates(lcs.Select(c => c.Start).ToList(), declaredIndex, ref ambiguity);
            if (resolved != null)
            {
                var chosen = lcs.First(c => c.Start == resolved.Value);
                spanLength = chosen.EndLine - chosen.Start + 1;
            }
            return resolved;
        }

        return null;
    }

    private static List<(int Start, int EndLine)> FindLineSequenceLcs(List<string> content, List<string> pattern, out double confidence)
    {
        confidence = 0;
        var bestStarts = new List<(int Start, int EndLine)>();
        if (pattern.Count == 0 || pattern.Count > MatchFinder.MaxLcsPatternLines) return bestStarts;
        if ((long)content.Count * pattern.Count > MatchFinder.MaxLcsCells) return bestStarts;

        var contentNorm = content.Select(NormalizeForLcs).ToArray();
        var patternNorm = pattern.Select(NormalizeForLcs).ToArray();

        var index = new Dictionary<string, List<int>>();
        for (int i = 0; i < contentNorm.Length; i++)
        {
            if (!index.TryGetValue(contentNorm[i], out var list))
            {
                list = new List<int>();
                index[contentNorm[i]] = list;
            }
            list.Add(i);
        }

        int bestMatched = -1;
        for (int s = 0; s < contentNorm.Length; s++)
        {
            int cursor = s;
            int matched = 0;
            int endLine = -1;

            foreach (string p in patternNorm)
            {
                if (!index.TryGetValue(p, out var list)) continue;
                int idx = list.BinarySearch(cursor);
                if (idx < 0) idx = ~idx;
                if (idx >= list.Count) continue;
                cursor = list[idx] + 1;
                matched++;
                endLine = list[idx];
            }

            if (matched == 0 || endLine - s > pattern.Count + MatchFinder.MaxLcsSkipLines) continue;
            if ((double)matched / pattern.Count < MatchFinder.MinLcsConfidence) continue;

            if (matched > bestMatched)
            {
                bestMatched = matched;
                bestStarts.Clear();
                bestStarts.Add((s, endLine));
            }
            else if (matched == bestMatched)
            {
                bestStarts.Add((s, endLine));
            }
        }

        if (bestMatched > 0)
            confidence = (double)bestMatched / pattern.Count;
        return bestStarts;
    }

    private static string NormalizeForLcs(string line) => NormalizeLine(UnicodeNormalize(line));

    private static int? ResolveCandidates(List<int> candidates, int declaredIndex, ref string? ambiguity)
    {
        if (candidates.Count == 1)
            return candidates[0];

        if (declaredIndex < 0)
        {
            ambiguity = $"the context matched at multiple locations (lines {string.Join(", ", candidates.Select(c => c + 1))}). Include more unique context lines or use the Edit tool instead.";
            return null;
        }

        int bestDistance = candidates.Min(c => Math.Abs(c - declaredIndex));
        var best = candidates.Where(c => Math.Abs(c - declaredIndex) == bestDistance).ToList();
        if (best.Count == 1)
            return best[0];

        ambiguity = $"the context matched at multiple locations (lines {string.Join(", ", best.Select(b => b + 1))}). Include more unique context lines or use the Edit tool instead.";
        return null;
    }

    private static void ApplyHunkToLines(List<string> originalLines, Hunk hunk, int matchIndex, int spanLength)
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

        originalLines.RemoveRange(matchIndex, spanLength);
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

    private static bool UnicodeNormalizedCompare(string a, string b) =>
        NormalizeLine(UnicodeNormalize(a)) == NormalizeLine(UnicodeNormalize(b));

    private static string NormalizeLine(string line)
    {
        line = TrimCR(line);
        return line.Trim();
    }

    private static string UnicodeNormalize(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            switch (c)
            {
                case '\u2018':
                case '\u2019':
                    sb.Append('\'');
                    break;
                case '\u201C':
                case '\u201D':
                    sb.Append('"');
                    break;
                case '\u2013':
                case '\u2014':
                    sb.Append('-');
                    break;
                case '\u00A0':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

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

            if (line.StartsWith("```"))
                continue;

            if (line.StartsWith("@@"))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                {
                    error = $"malformed hunk header '{line}'. Expected format: @@ -start,count +start,count @@ (or a bare @@ separator).";
                    return null;
                }

                current = new Hunk
                {
                    OldStart = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : -1,
                    OldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : -1,
                    NewStart = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : -1,
                    NewCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : -1
                };
                hunks.Add(current);
                continue;
            }

            if (current == null)
            {
                if (line.Length == 0 || line[0] != ' ')
                    continue;
                error = $"expected a @@ hunk header but found '{line}'. The patch must contain at least one @@ -start,count +start,count @@ hunk (a bare @@ separator is also accepted).";
                return null;
            }

            if (line.StartsWith("\\ No newline at end of file"))
            {
                if (current.Lines.Count > 0)
                    current.LastEntryHasNoNewlineMarker = true;
                continue;
            }

            if (line.StartsWith("***"))
                continue;

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

    [GeneratedRegex(@"^@@(?:[ ]+-(\d+)(?:,(\d+))?[ ]+\+(\d+)(?:,(\d+))?)?[ ]*(?:@@)?(?:[ ].*)?$")]
    private static partial Regex HunkHeaderRegex();

    private sealed class Hunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
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
