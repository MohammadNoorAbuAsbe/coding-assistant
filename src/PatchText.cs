using System.Text;

namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
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
}
