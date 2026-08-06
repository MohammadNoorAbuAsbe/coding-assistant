using System;

namespace TerminalAiAssistant;

public static class UiStyler
{
    public static void ShowBanner()
    {
        Console.WriteLine();
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("    ████████╗███████╗██████╗ ███╗   ███╗██╗███╗   ██╗ █████╗ ██╗");
            Console.WriteLine("    ╚══██╔══╝██╔════╝██╔══██╗████╗ ████║██║████╗  ██║██╔══██╗██║");
            Console.WriteLine("       ██║   █████╗  ██████╔╝██╔████╔██║██║██╔██╗ ██║███████║██║");
            Console.WriteLine("       ██║   ██╔══╝  ██╔══██╗██║╚██╔╝██║██║██║╚██╗██║██╔══██║██║");
            Console.WriteLine("       ██║   ███████╗██║  ██║██║ ╚═╝ ██║██║██║ ╚████║██║  ██║███████╗");
            Console.WriteLine("       ╚═╝   ╚══════╝╚═╝  ╚═╝╚═╝     ╚═╝╚═╝╚═╝  ╚═══╝╚═╝  ╚═╝╚══════╝");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.DarkCyan))
        {
            Console.WriteLine("         🌟 A I   C O D I N G   A S S I S T A N T   &   A G E N T  🌟");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
        {
            Console.WriteLine("         ──────────────────────────────────────────────────────");
        }
        Console.WriteLine();
    }

    public static void ShowStatusCard(string provider, string model, string workspace, string contextInfo)
    {
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("  ╭─────────────────────────────────────────────────────────────────╮");
            Console.WriteLine("  │  🚀 ACTIVE SESSION STATUS                                       │");
            Console.WriteLine("  ├─────────────────────────────────────────────────────────────────┤");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.DarkCyan))
        {
            Console.Write("  │  ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray))
        {
            Console.Write("Provider  : ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Green))
        {
            Console.Write($"{provider,-52}");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("│");
        }

        using (ConsoleStyler.WithColor(ConsoleColor.DarkCyan))
        {
            Console.Write("  │  ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray))
        {
            Console.Write("Model     : ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Yellow))
        {
            Console.Write($"{model,-52}");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("│");
        }

        using (ConsoleStyler.WithColor(ConsoleColor.DarkCyan))
        {
            Console.Write("  │  ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray))
        {
            Console.Write("Context   : ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Magenta))
        {
            Console.Write($"{contextInfo,-52}");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("│");
        }

        using (ConsoleStyler.WithColor(ConsoleColor.DarkCyan))
        {
            Console.Write("  │  ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray))
        {
            Console.Write("Workspace : ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.White))
        {
            Console.Write($"{Truncate(workspace, 52),-52}");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("│");
            Console.WriteLine("  ╰─────────────────────────────────────────────────────────────────╯");
        }
        Console.WriteLine();
    }

    public static void ShowStatusBar(string statusText)
    {
        using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
        {
            Console.WriteLine($"  [ STATUS: {statusText} ]");
        }
    }

    public static void ShowHelp()
    {
        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
        {
            Console.WriteLine("  ╔═ 💡 Quick Commands ═════════════════════════════════════════════╗");
            Console.WriteLine("  ║                                                                 ║");
            Console.WriteLine("  ║   /exit or /quit   • Exit the assistant session                 ║");
            Console.WriteLine("  ║   /new or /reset   • Reset conversation session and history     ║");
            Console.WriteLine("  ║   /autopilot       • Launch fully autonomous mode               ║");
            Console.WriteLine("  ║   /undo            • Undo the last file modification            ║");
            Console.WriteLine("  ║   /history         • View session file modification history     ║");
            Console.WriteLine("  ║   /help            • Show this quick command guide              ║");
            Console.WriteLine("  ║                                                                 ║");
            Console.WriteLine("  ╚═════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxLen) return text;
        return "…" + text[(text.Length - maxLen + 1)..];
    }
}
