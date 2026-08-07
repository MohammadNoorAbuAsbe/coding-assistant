using System.Text;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
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

                var hunks = ParseHunks(ResponseHandler.RepairContentEncoding(args.patch), out string? parseError);
                if (hunks == null)
                    return ResponseHandler.CreateErrorResult(toolCall, $"Error: {parseError}");

                if (!System.IO.File.Exists(safePath))
                {
                    return CreateNewFile(toolCall, safePath, args.file_path, hunks);
                }

                string raw = System.IO.File.ReadAllText(safePath);
                if (!SessionContext.FileState.HasState(safePath))
                {
                    return ResponseHandler.CreateErrorResult(toolCall, $"Error: ApplyPatch cannot modify '{args.file_path}' because the file was not read in this session. Use the Read tool to read the file first, then retry the patch.");
                }

                string? notice = null;
                if (SessionContext.FileState.IsStale(safePath, raw))
                {
                    notice = $"Warning: '{args.file_path}' changed on disk since the session last read or wrote it. The patch was applied to the current content, but Read the file to verify the result.";
                }

                return ApplyHunks(toolCall, safePath, args.file_path, hunks, notice);
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

                string oldText = await System.IO.File.ReadAllTextAsync(safePath, cancellationToken);
                string diff = GenerateUnifiedDiff(oldText, ResponseHandler.RepairContentEncoding(args.new_content), args.file_path);

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
        SessionContext.Undo.Record(safePath, beforeContent: null, existedBefore: false, ToolHandler.ApplyPatchFunctionName);
        System.IO.File.WriteAllText(safePath, content);

        int added = lines.Count;
        return new ToolChatMessage(toolCall.Id, $"Created new file {displayPath} ({hunks.Count} hunk(s), +{added} lines).");
    }

    private static ToolChatMessage ApplyHunks(ChatToolCall toolCall, string safePath, string displayPath, List<Hunk> hunks, string? notice = null)
    {
        string raw = System.IO.File.ReadAllText(safePath);
        var originalLines = SplitLines(raw).ToList();
        var matchLines = originalLines.Select(TrimCR).ToList();

        var placements = ComputePlacements(toolCall, displayPath, hunks, matchLines, out int noOps, out ToolChatMessage? placementError);
        if (placementError != null)
            return placementError;

        if (placements.Count == 0)
        {
            string note = noOps > 0
                ? $"all {noOps} hunk(s) were context-only no-ops; no changes were made"
                : "no changes";
            return new ToolChatMessage(toolCall.Id, $"Successfully applied the patch to {displayPath} ({note}).");
        }

        foreach (var placement in placements.OrderByDescending(p => p.Index))
        {
            ApplyHunkToLines(originalLines, placement.Hunk, placement.Index, placement.SpanLength);
        }

        string eol = raw.Contains("\r\n") ? "\r\n" : "\n";
        bool trailingNewline = DetermineTrailingNewline(raw.EndsWith('\n'), placements);
        string content = JoinLines(originalLines.Select(TrimCR), eol, trailingNewline);
        SessionContext.Undo.Record(safePath, raw, existedBefore: true, ToolHandler.ApplyPatchFunctionName);
        System.IO.File.WriteAllText(safePath, content);
        SessionContext.FileState.RecordWrite(safePath, content);

        return BuildApplySummary(toolCall, displayPath, raw, content, placements, noOps, notice);
    }

    private static List<HunkPlacement> ComputePlacements(ChatToolCall toolCall, string displayPath, List<Hunk> hunks, List<string> matchLines, out int noOps, out ToolChatMessage? error)
    {
        var placements = new List<HunkPlacement>();
        noOps = 0;
        error = null;

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
                error = ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}': {ambiguity}");
                return placements;
            }

            if (matchIndex == null)
            {
                string location = hunk.OldStart > 0 ? $"declared around line {hunk.OldStart}" : "with no declared position";
                error = ResponseHandler.CreateErrorResult(toolCall, $"Error: could not apply hunk {i + 1} to '{displayPath}' — the changed lines were not found ({location}). The file may have changed or the context lines do not match. Read the file to get the current content and retry with corrected context lines.");
                return placements;
            }

            bool reachesEnd = matchIndex.Value + spanLength >= matchLines.Count;
            placements.Add(new HunkPlacement(i + 1, hunk, matchIndex.Value, strategy, confidence, reachesEnd, spanLength));
        }

        return placements;
    }

    private static bool DetermineTrailingNewline(bool fileEndsWithNewline, List<HunkPlacement> placements)
    {
        var lastHunk = placements.OrderBy(p => p.Index).Last();
        if (lastHunk.ReachesEnd)
            return !lastHunk.Hunk.LastEntryHasNoNewlineMarker;
        if (lastHunk.Hunk.LastEntryHasNoNewlineMarker)
            return false;
        return fileEndsWithNewline;
    }

    private static ToolChatMessage BuildApplySummary(ChatToolCall toolCall, string displayPath, string raw, string content, List<HunkPlacement> placements, int noOps, string? notice = null)
    {
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

        int oldTotal = CountNewlines(raw);
        int newTotal = CountNewlines(content);

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(notice))
        {
            sb.Append(notice).Append("\n\n");
        }
        sb.Append($"Successfully applied {placements.Count} hunk(s) to {displayPath} (+{totalAdded} -{totalRemoved} lines).");
        sb.Append($"\nFile now has {newTotal} lines (was {oldTotal}).");
        foreach (var placement in placements)
        {
            sb.Append($"\n  hunk {placement.HunkNumber}: {DescribeHunkMatch(placement.Hunk, placement.Strategy, placement.Confidence)}");
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

    private static int CountNewlines(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
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

        var index = BuildLineIndex(contentNorm);

        int bestMatched = -1;
        for (int s = 0; s < contentNorm.Length; s++)
        {
            int matched = MatchPatternGreedily(index, patternNorm, s, out int endLine);
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

    private static Dictionary<string, List<int>> BuildLineIndex(string[] lines)
    {
        var index = new Dictionary<string, List<int>>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!index.TryGetValue(lines[i], out var list))
            {
                list = new List<int>();
                index[lines[i]] = list;
            }
            list.Add(i);
        }
        return index;
    }

    private static int MatchPatternGreedily(Dictionary<string, List<int>> index, string[] patternNorm, int start, out int endLine)
    {
        int cursor = start;
        int matched = 0;
        endLine = -1;

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

        return matched;
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

    private sealed record HunkPlacement(int HunkNumber, Hunk Hunk, int Index, MatchStrategy Strategy, double? Confidence, bool ReachesEnd, int SpanLength);
}
