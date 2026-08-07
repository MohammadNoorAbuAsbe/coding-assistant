using System.Text;

namespace TerminalAiAssistant;

public static class UiStyler2026
{
    private static readonly string[] CyberBanner2026 = [
        "    ╔════════════════════════════════════════════════════════════════════════════╗",
        "    ║                                                                            ║",
        "    ║   ⚡ 2026 QUANTUM NEURAL ENGINE · WORLD-CLASS CODING ECOSYSTEM ⚡            ║",
        "    ║   Immersive Reasoning Telemetry & Real-Time Diff Inspection                ║",
        "    ║                                                                            ║",
        "    ╚════════════════════════════════════════════════════════════════════════════╝"
    ];

    public static void ShowBanner()
    {
        Console.WriteLine();
        foreach (var line in CyberBanner2026)
        {
            using (ConsoleStyler.WithColor(ThemeManager.Primary))
            {
                Console.WriteLine(line);
            }
        }
        Console.WriteLine();
    }

    public static void ShowStatusCard(string provider, string model, string workspace, string contextInfo)
    {
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔════════════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  🌐 2026 NEURAL COMMAND NEXUS [HUD]                                        ║");
            Console.WriteLine("  ╠════════════════════════════════════════════════════════════════════════════╣");
        }

        PrintRow("Provider", provider, ThemeManager.Accent);
        PrintRow("Model", model, ThemeManager.Secondary);
        PrintRow("Context", contextInfo, ThemeManager.Primary);
        PrintRow("Workspace", Truncate(workspace, 61), ConsoleColor.White);
        PrintRow("Theme", ThemeManager.ThemeName + " (/theme to cycle)", ConsoleColor.Yellow);

        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╚════════════════════════════════════════════════════════════════════════════╝");
        }
        Console.WriteLine();
    }

    private static void PrintRow(string label, string value, ConsoleColor valColor)
    {
        using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Write("  ║  "); }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray)) { Console.Write($"{label,-11}: "); }
        using (ConsoleStyler.WithColor(valColor)) { Console.Write($"{value,-61}"); }
        using (ConsoleStyler.WithColor(ThemeManager.Primary)) { Console.WriteLine("║"); }
    }

    public static void ShowHelp()
    {
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔══ 🚀 2026 WORLD-CLASS COMMAND MATRIX ════════════════════════════════════╗");
            Console.WriteLine("  ║                                                                            ║");
            Console.WriteLine("  ║   /exit or /quit   • Terminate secure neural link                          ║");
            Console.WriteLine("  ║   /new or /reset   • Purge context history & re-initialize core            ║");
            Console.WriteLine("  ║   /autopilot       • Engage autonomous 2026 self-evolution swarm           ║");
            Console.WriteLine("  ║   /theme           • Cycle futuristic 2026 UI palettes                     ║");
            Console.WriteLine("  ║   /undo            • Rollback latest quantum file modification             ║");
            Console.WriteLine("  ║   /history         • Inspect telemetry ledger of file modifications        ║");
            Console.WriteLine("  ║   /help            • Summon command matrix                                 ║");
            Console.WriteLine("  ║                                                                            ║");
            Console.WriteLine("  ╚════════════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }
    }

    public static void RenderThoughtBox(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        int width = 76;
        try { width = Math.Max(40, Math.Min(120, Console.WindowWidth - 4)); } catch { }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  ╭── 🧠 {title.ToUpper()} " + new string('─', Math.Max(0, width - 8 - title.Length)) + "╮");
        }

        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Error.Write("  │ "); }
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray)) { Console.Error.WriteLine(trimmed.PadRight(width)); }
        }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Error.WriteLine($"  ╰" + new string('─', width - 2) + "╯");
        }
    }

    public static void RenderToolExecutionDetail(string toolName, string argsSummary, string resultSummary, bool isError, string? diffOrDetails)
    {
        int width = 76;
        try { width = Math.Max(40, Math.Min(120, Console.WindowWidth - 4)); } catch { }

        using (ConsoleStyler.WithColor(isError ? ConsoleColor.Red : ThemeManager.Primary))
        {
            Console.Error.WriteLine();
            string headerSymbol = isError ? "❌" : "⚡";
            Console.Error.WriteLine($"  ╭── {headerSymbol} TOOL EXECUTED: {toolName.ToUpper()} " + new string('─', Math.Max(0, width - 18 - toolName.Length)) + "╮");
        }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Error.Write("  │ "); }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray)) { Console.Error.Write("Target: "); }
        using (ConsoleStyler.WithColor(ThemeManager.Accent)) { Console.Error.WriteLine($"{argsSummary}"); }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Error.Write("  │ "); }
        using (ConsoleStyler.WithColor(ConsoleColor.Gray)) { Console.Error.Write("Status: "); }
        using (ConsoleStyler.WithColor(isError ? ConsoleColor.Red : ConsoleColor.Green)) { Console.Error.WriteLine(isError ? $"FAILED → {resultSummary}" : $"SUCCESS ({resultSummary})"); }

        if (!string.IsNullOrEmpty(diffOrDetails))
        {
            using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
            {
                Console.Error.WriteLine($"  ├" + new string('─', width - 2) + "┤");
            }
            foreach (var line in diffOrDetails.Split('\n'))
            {
                string t = line.TrimEnd('\r');
                using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Error.Write("  │ "); }
                
                if (t.StartsWith('+') && !t.StartsWith("+++"))
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.Green)) { Console.Error.WriteLine($"{t}"); }
                }
                else if (t.StartsWith('-') && !t.StartsWith("---"))
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.Red)) { Console.Error.WriteLine($"{t}"); }
                }
                else if (t.StartsWith("@@") || t.StartsWith("---") || t.StartsWith("+++"))
                {
                    using (ConsoleStyler.WithColor(ThemeManager.Secondary)) { Console.Error.WriteLine($"{t}"); }
                }
                else
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.DarkGray)) { Console.Error.WriteLine($"{t}"); }
                }
            }
        }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Error.WriteLine($"  ╰" + new string('─', width - 2) + "╯");
        }
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxLen) return text;
        return "…" + text[(text.Length - maxLen + 1)..];
    }
}
