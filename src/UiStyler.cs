using System;

namespace TerminalAiAssistant;

public static class UiStyler
{
    private static readonly string[] CyberBanner = [
        "    ╔════════════════════════════════════════════════════════════════════════════╗",
        "    ║                                                                            ║",
        "    ║   ████████╗███████╗██████╗ ███╗   ███╗██╗███╗   ██╗ █████╗ ██╗             ║",
        "    ║   ╚══██╔══╝██╔════╝██╔══██╗████╗ ████║██║████╗  ██║██╔══██╗██║             ║",
        "    ║      ██║   █████╗  ██████╔╝██╔████╔██║██║██╔██╗ ██║███████║██║             ║",
        "    ║      ██║   ██╔══╝  ██╔══██╗██║╚██╔╝██║██║██║╚██╗██║██╔══██║██║             ║",
        "    ║      ██║   ███████╗██║  ██║██║ ╚═╝ ██║██║██║ ╚████║██║  ██║███████╗        ║",
        "    ║      ╚═╝   ╚══════╝╚═╝  ╚═╝╚═╝     ╚═╝╚═╝╚═╝  ╚═══╝╚═╝  ╚═╝╚══════╝        ║",
        "    ║                                                                            ║",
        "    ╚════════════════════════════════════════════════════════════════════════════╝"
    ];

    public static void ShowBanner()
    {
        Console.WriteLine();
        int colorIdx = 0;
        ConsoleColor[] gradient = [ThemeManager.Primary, ThemeManager.Secondary, ThemeManager.Accent, ThemeManager.Primary];
        foreach (var line in CyberBanner)
        {
            using (ConsoleStyler.WithColor(gradient[colorIdx % gradient.Length]))
            {
                Console.WriteLine(line);
            }
            colorIdx++;
        }
        Console.WriteLine();
        using (ConsoleStyler.WithColor(ThemeManager.Accent))
        {
            Console.WriteLine($"        🔮   N E X T - G E N   A I   C O D I N G   E CＯＳＹＳＴＥМ   [{ThemeManager.ThemeName.ToUpper()}]   🔮");
        }
        using (ConsoleStyler.WithColor(ThemeManager.MutedText))
        {
            Console.WriteLine("        ──────────────────────────────────────────────────────────────────────────");
        }
        Console.WriteLine();
    }

    public static void ShowStatusCard(string provider, string model, string workspace, string contextInfo)
    {
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔════════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  🚀 CYBERPUNK NEURAL ENVIRONMENT HUD                                       ║");
            Console.WriteLine("  ╠════════════════════════════════════════════════════════════════════════════╣");
        }

        PrintStatusRow("Provider", provider, ThemeManager.Accent);
        PrintStatusRow("Model", model, ThemeManager.Secondary);
        PrintStatusRow("Context", contextInfo, ThemeManager.Primary);
        PrintStatusRow("Workspace", Truncate(workspace, 62), ConsoleColor.White);
        PrintStatusRow("Theme", ThemeManager.ThemeName + " (/theme to cycle)", ConsoleColor.Yellow);

        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╚════════════════════════════════════════════════════════════════════════════╝");
        }
        Console.WriteLine();
    }

    private static void PrintStatusRow(string label, string value, ConsoleColor valueColor)
    {
        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Write("  ║  ");
        }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray))
        {
            Console.Write($"{label,-11}: ");
        }
        using (ConsoleStyler.WithColor(valueColor))
        {
            Console.Write($"{value,-62}");
        }
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("║");
        }
    }

    public static void ShowStatusBar(string statusText)
    {
        using (ConsoleStyler.WithColor(ThemeManager.MutedText))
        {
            Console.WriteLine($"  ⚡ [ SYSTEM STATUS: {statusText} ]");
        }
    }

    public static void ShowHelp()
    {
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔══ 💡 NEURAL COMMAND MATRIX & SHORTCUTS ════════════════════════════════════╗");
            Console.WriteLine("  ║                                                                            ║");
            Console.WriteLine("  ║   /exit or /quit   • Terminate secure neural link                          ║");
            Console.WriteLine("  ║   /new or /reset   • Purge context history & re-initialize core            ║");
            Console.WriteLine("  ║   /autopilot       • Engage autonomous self-evolution swarm mode           ║");
            Console.WriteLine("  ║   /theme           • Cycle gorgeous UI color themes & palettes             ║");
            Console.WriteLine("  ║   /undo            • Rollback latest quantum file state modification       ║");
            Console.WriteLine("  ║   /history         • Inspect telemetry ledger of file modifications        ║");
            Console.WriteLine("  ║   /help            • Summon neural command matrix                          ║");
            Console.WriteLine("  ║                                                                            ║");
            Console.WriteLine("  ╚════════════════════════════════════════════════════════════════════════════╝");
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
