namespace TerminalAiAssistant;

internal enum MatchStrategy
{
    Exact,
    CaseInsensitive,
    NormalizedWhitespace,
    LineNormalized,
    LineFuzzy
}

internal sealed record MatchResult(int Index, int Length, MatchStrategy Strategy);

internal static class MatchFinder
{
    internal static MatchResult? FindBestMatch(string content, string oldString)
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
        return sb.ToString().Trim();
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
}