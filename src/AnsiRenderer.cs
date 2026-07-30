using System.Text.RegularExpressions;

namespace TerminalAiAssistant;

public static class AnsiRenderer
{
    const string Esc = "\u001b";

    static readonly Regex InlineCode = new(@"`([^`]+)`", RegexOptions.Compiled);
    static readonly Regex BoldItalic = new(@"\*\*\*(.+?)\*\*\*", RegexOptions.Compiled);
    static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    static readonly Regex Italic = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    static readonly Regex Strike = new(@"~~(.+?)~~", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    static readonly Regex Heading = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    static readonly Regex Blockquote = new(@"^>\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex ListPattern = new(@"^[-*+]\s+(.+)$", RegexOptions.Compiled);
    static readonly Regex Hr = new(@"^(-{3,}|\*{3,})$", RegexOptions.Compiled);

    public static string Render(string text, ref bool inCodeBlock)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string trimmed = text.TrimStart();
        int indent = text.Length - trimmed.Length;
        string indentStr = text[..indent];

        if (trimmed.StartsWith("```"))
        {
            inCodeBlock = !inCodeBlock;
            return inCodeBlock ? $"{Esc}[97;100m" : $"{Esc}[0m";
        }

        if (inCodeBlock)
            return $"{Esc}[97;100m{text}{Esc}[0m";

        return ProcessInline(ProcessBlockLevel(text));
    }

    static string ProcessBlockLevel(string text)
    {
        if (Hr.IsMatch(text.Trim()))
            return $"{Esc}[90m{new string('─', Math.Max(40, Console.WindowWidth - 1))}{Esc}[0m";

        var m = Heading.Match(text);
        if (m.Success)
        {
            int level = m.Groups[1].Value.Length;
            string content = m.Groups[2].Value;
            string ansi = level switch
            {
                1 => $"{Esc}[1;93m",
                2 => $"{Esc}[1;33m",
                _ => $"{Esc}[1;36m",
            };
            return $"{ansi}{content}{Esc}[0m";
        }

        m = Blockquote.Match(text);
        if (m.Success)
            return $"{Esc}[2m> {m.Groups[1].Value}{Esc}[22m";

        m = ListPattern.Match(text);
        if (m.Success)
            return $"  {Esc}[2m•{Esc}[22m {m.Groups[1].Value}";

        return text;
    }

    static string ProcessInline(string text)
    {
        text = InlineCode.Replace(text, m => $"{Esc}[97;100m{m.Groups[1].Value}{Esc}[0m");
        text = BoldItalic.Replace(text, m => $"{Esc}[1;3m{m.Groups[1].Value}{Esc}[23;22m");
        text = Bold.Replace(text, m => $"{Esc}[1m{m.Groups[1].Value}{Esc}[22m");
        text = Italic.Replace(text, m => $"{Esc}[3m{m.Groups[1].Value}{Esc}[23m");
        text = Strike.Replace(text, m => $"{Esc}[9m{m.Groups[1].Value}{Esc}[29m");
        text = Link.Replace(text, m => $"{Esc}[4;94m{m.Groups[1].Value}{Esc}[0m ({Esc}[4;94m{m.Groups[2].Value}{Esc}[0m)");
        return text;
    }
}
