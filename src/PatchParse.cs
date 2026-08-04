using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
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
                if (!TryParseHunkHeader(line, out current, out error))
                    return null;
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

            if (!TryAddHunkLine(current, line, out error))
                return null;
        }

        if (hunks.Count == 0)
        {
            error = "no hunks found in the patch. Expected one or more @@ -start,count +start,count @@ sections followed by ' ' (context), '-' (removed), and '+' (added) lines.";
            return null;
        }

        return hunks;
    }

    private static bool TryParseHunkHeader(string line, [NotNullWhen(true)] out Hunk? hunk, out string? error)
    {
        var match = HunkHeaderRegex().Match(line);
        if (!match.Success)
        {
            error = $"malformed hunk header '{line}'. Expected format: @@ -start,count +start,count @@ (or a bare @@ separator).";
            hunk = null;
            return false;
        }

        hunk = new Hunk
        {
            OldStart = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : -1,
            OldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : -1,
            NewStart = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : -1,
            NewCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : -1
        };
        error = null;
        return true;
    }

    private static bool TryAddHunkLine(Hunk current, string line, out string? error)
    {
        if (line.StartsWith("\\ No newline at end of file"))
        {
            if (current.Lines.Count > 0)
                current.LastEntryHasNoNewlineMarker = true;
            error = null;
            return true;
        }

        if (line.StartsWith("***"))
        {
            error = null;
            return true;
        }

        if (line.Length == 0)
        {
            error = null;
            return true;
        }

        char prefix = line[0];
        if (prefix != ' ' && prefix != '-' && prefix != '+')
        {
            error = $"unexpected line '{line}' inside hunk. Expected lines starting with ' ' (context), '-' (removed), or '+' (added).";
            return false;
        }

        current.Lines.Add(new PatchLine(prefix, line.Substring(1)));
        error = null;
        return true;
    }

    [GeneratedRegex(@"^@@(?:[ ]+-(\d+)(?:,(\d+))?[ ]+\+(\d+)(?:,(\d+))?)?(?:[ ]*@@(?:[ ]+.*)?)?[ ]*$")]
    private static partial Regex HunkHeaderRegex();
}
