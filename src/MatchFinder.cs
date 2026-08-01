namespace TerminalAiAssistant;

internal enum MatchStrategy
{
    Exact,
    NormalizedWhitespace,
    UnicodeNormalized,
    LineLcs
}

internal sealed record MatchResult(int Index, int Length, MatchStrategy Strategy, double? Confidence = null);

internal static class MatchFinder
{
    internal const int MaxLcsCells = 4_000_000;
    internal const int MaxLcsPatternLines = 512;
    internal const int MaxLcsSkipLines = 8;
    internal const double MinLcsConfidence = 0.7;

    internal static MatchResult? FindBestMatch(string content, string oldString)
    {
        if (string.IsNullOrEmpty(oldString)) return null;

        var result = TryMatch(content, oldString, MatchStrategy.Exact);
        if (result != null) return result;

        result = TryMatchLineNormalized(content, oldString);
        if (result != null) return result;

        result = TryMatchLineUnicode(content, oldString);
        if (result != null) return result;

        result = TryMatchLineLcs(content, oldString);
        if (result != null) return result;

        return null;
    }

    private static MatchResult? TryMatch(string content, string oldString, MatchStrategy strategy)
    {
        int first = content.IndexOf(oldString, StringComparison.Ordinal);
        if (first == -1) return null;

        int last = content.LastIndexOf(oldString, StringComparison.Ordinal);
        return first == last ? new MatchResult(first, oldString.Length, strategy) : null;
    }

    private static MatchResult? TryMatchLineNormalized(string content, string oldString) =>
        TryMatchLineNormalizedCore(content, oldString, NormalizeLine, MatchStrategy.NormalizedWhitespace);

    private static MatchResult? TryMatchLineUnicode(string content, string oldString) =>
        TryMatchLineNormalizedCore(content, oldString, UnicodeNormalizeLine, MatchStrategy.UnicodeNormalized);

    private static MatchResult? TryMatchLineLcs(string content, string oldString)
    {
        var (contentLines, lineOffsets) = SplitIntoLines(content);
        var oldLines = SplitIntoLines(oldString).Lines;
        if (oldLines.Length == 0 || oldLines.Length > MaxLcsPatternLines) return null;
        if ((long)contentLines.Length * oldLines.Length > MaxLcsCells) return null;

        var contentNorm = contentLines.Select(UnicodeNormalizeLine).ToArray();
        var oldNorm = oldLines.Select(UnicodeNormalizeLine).ToArray();

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

        int? bestStart = null;
        int? bestEndLine = null;
        int bestMatched = -1;
        bool ambiguous = false;

        for (int s = 0; s < contentNorm.Length; s++)
        {
            int cursor = s;
            int matched = 0;
            int endLine = -1;

            foreach (string pattern in oldNorm)
            {
                if (!index.TryGetValue(pattern, out var list)) continue;
                int idx = list.BinarySearch(cursor);
                if (idx < 0) idx = ~idx;
                if (idx >= list.Count) continue;
                cursor = list[idx] + 1;
                matched++;
                endLine = list[idx];
            }

            if (matched == 0 || endLine - s > oldLines.Length + MaxLcsSkipLines) continue;
            if ((double)matched / oldLines.Length < MinLcsConfidence) continue;

            if (matched > bestMatched)
            {
                bestMatched = matched;
                bestStart = s;
                bestEndLine = endLine;
                ambiguous = false;
            }
            else if (matched == bestMatched)
            {
                ambiguous = true;
            }
        }

        if (ambiguous || bestStart == null || bestEndLine == null) return null;

        double confidence = (double)bestMatched / oldLines.Length;
        return BuildLineLcsResult(content, contentLines, lineOffsets, bestStart.Value, bestEndLine.Value, confidence);
    }

    private static MatchResult? BuildLineLcsResult(string content, string[] contentLines, int[] lineOffsets, int startLine, int endLine, double confidence)
    {
        int startOffset = lineOffsets[startLine];
        int endOffset = lineOffsets[endLine] + contentLines[endLine].Length;
        int length = endOffset - startOffset;
        if (startOffset + length > content.Length) return null;
        return new MatchResult(startOffset, length, MatchStrategy.LineLcs, confidence);
    }

    private static MatchResult? TryMatchLineNormalizedCore(string content, string oldString, Func<string, string> normalize, MatchStrategy strategy)
    {
        var (contentLines, lineOffsets) = SplitIntoLines(content);
        var oldLines = SplitIntoLines(oldString).Lines;
        if (oldLines.Length == 0) return null;

        var contentNorm = contentLines.Select(normalize).ToArray();
        var oldNorm = oldLines.Select(normalize).ToArray();

        var matches = FindLineSequence(contentNorm, oldNorm);
        if (matches.Count == 0) return null;
        if (matches.Count > 1) return null;

        return BuildLineMatchResult(content, contentLines, oldLines, lineOffsets, matches[0], strategy, normalize);
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

        return line.Trim();
    }

    private static string UnicodeNormalizeLine(string line) => NormalizeLine(UnicodeNormalize(line));

    private static string UnicodeNormalize(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
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

    private static MatchResult? BuildLineMatchResult(string content, string[] contentLines, string[] oldLines, int[] lineOffsets, int matchLine, MatchStrategy strategy, Func<string, string> normalize)
    {
        int posInLine = FindPositionInLine(contentLines[matchLine], oldLines[0], normalize(oldLines[0]), normalize);
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
            normalize(actualFirstLine) == normalize(oldLines[0]);

        if (!firstLineMatches) return null;

        return new MatchResult(absolutePos, contentLength, strategy);
    }

    private static int FindPositionInLine(string contentLine, string oldFirstLine, string oldFirstNormalized, Func<string, string> normalize)
    {
        int idx = contentLine.IndexOf(oldFirstLine, StringComparison.Ordinal);
        if (idx != -1) return idx;

        string contentNorm = normalize(contentLine);
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
