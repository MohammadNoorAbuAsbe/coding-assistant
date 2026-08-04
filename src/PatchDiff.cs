using System.Text;

namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
    private const int MaxDiffCells = 4_000_000;
    private const int MaxDiffHunks = 20;

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

        if (!newText.EndsWith('\n') && changes.Count > 0)
        {
            int lastNewIndex = changes.Count - 1;
            while (lastNewIndex >= 0 && changes[lastNewIndex].Type == '-')
                lastNewIndex--;
            if (lastNewIndex >= 0)
                changes[lastNewIndex] = changes[lastNewIndex] with { MarkerAfter = true };
        }

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
            if (entry.MarkerAfter)
                sb.AppendLine("\\ No newline at end of file");
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
                int end = ExtendHunkToChanges(changes, i, contextLines, out int lastChange);
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

    private static int ExtendHunkToChanges(List<DiffChange> changes, int end, int contextLines, out int lastChange)
    {
        lastChange = end;
        while (end < changes.Count)
        {
            while (end < changes.Count && changes[end].Type != ' ')
            {
                lastChange = end;
                end++;
            }

            if (!HasChangeWithin(changes, end, end + contextLines))
                break;

            end += contextLines;
        }

        return end;
    }

    private static bool HasChangeWithin(List<DiffChange> changes, int from, int toExclusive)
    {
        int limit = Math.Min(toExclusive, changes.Count);
        for (int j = from; j < limit; j++)
        {
            if (changes[j].Type != ' ')
                return true;
        }
        return false;
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
}
