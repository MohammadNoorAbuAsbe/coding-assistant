using System.Diagnostics;

namespace TerminalAiAssistant;

public static class MenuHandler
{
    public static string SelectProvider(Dictionary<string, ProviderConfig> providers)
    {
        var list = providers.Values.ToList();
        var keys = providers.Keys.ToList();

        Console.WriteLine();
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔══ 🤖 SELECT NEURAL LLM PROVIDER ═════════════════════════════════════════╗");
            Console.WriteLine("  ║   Choose your neural intelligence backend:                               ║");
            Console.WriteLine("  ╠══════════════════════════════════════════════════════════════════════════╣");
        }
        for (int i = 0; i < list.Count; i++)
        {
            using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Write("  ║  "); }
            using (ConsoleStyler.WithColor(ThemeManager.Accent)) { Console.Write($"[{i + 1}] "); }
            using (ConsoleStyler.WithColor(ConsoleColor.White)) { Console.WriteLine($"{list[i].DisplayName,-66} ║"); }
        }
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════════════════════╝");
        }

        var choice = GetChoice(1, list.Count);
        return keys[choice - 1];
    }

    public static string SelectModel(string providerId, ProviderConfig config)
    {
        var models = new List<string>();

        if (providerId == "ollama")
        {
            models = DiscoverOllamaModels();
        }

        if (models.Count == 0 && config.Models.Count > 0)
        {
            models = config.Models;
        }

        if (models.Count == 0)
        {
            models.Add(config.DefaultModel);
        }

        if (models.Count == 1)
        {
            Console.WriteLine();
            using (ConsoleStyler.WithColor(ThemeManager.MutedText))
            {
                Console.WriteLine($"  ⚡ [Neural Link] Model locked to: {models[0]}");
            }
            return models[0];
        }

        Console.WriteLine();
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╔══ 🧠 SELECT NEURAL AI MODEL ═════════════════════════════════════════════╗");
            Console.WriteLine("  ║   Select model architecture for optimal reasoning:                       ║");
            Console.WriteLine("  ╠══════════════════════════════════════════════════════════════════════════╣");
        }
        for (int i = 0; i < models.Count; i++)
        {
            var note = models[i] == config.DefaultModel ? " (default)" : "";
            using (ConsoleStyler.WithColor(ThemeManager.BorderColor)) { Console.Write("  ║  "); }
            using (ConsoleStyler.WithColor(ThemeManager.Accent)) { Console.Write($"[{i + 1}] "); }
            using (ConsoleStyler.WithColor(models[i] == config.DefaultModel ? ConsoleColor.Green : ConsoleColor.White))
            {
                Console.WriteLine($"{models[i] + note,-66} ║");
            }
        }
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════════════════════╝");
        }

        var choice = GetChoice(1, models.Count);
        return models[choice - 1];
    }

    public static string GetPrompt()
    {
        using (ConsoleStyler.WithColor(ThemeManager.MutedText))
        {
            Console.WriteLine("  ───────────────────────────────────────────────────────────────────────────");
        }
        using (ConsoleStyler.WithColor(ThemeManager.Primary))
        {
            Console.WriteLine("  💬 Enter neural prompt (multi-line supported; send blank line to transmit):");
        }
        var lines = new List<string>();
        while (true)
        {
            using (ConsoleStyler.WithColor(ThemeManager.Secondary))
            {
                Console.Write("  ❯ ");
            }
            var line = Console.ReadLine();
            if (line is null) break;
            if (lines.Count == 0 && string.IsNullOrWhiteSpace(line)) return "";
            if (string.IsNullOrWhiteSpace(line)) break;
            if (lines.Count == 0 && line.TrimStart().StartsWith('/')) return line.Trim();
            lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static List<string> DiscoverOllamaModels()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "list",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0) return new List<string>();

            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static int GetChoice(int min, int max)
    {
        while (true)
        {
            using (ConsoleStyler.WithColor(ThemeManager.Accent))
            {
                Console.Write($"  Enter choice ({min}-{max}): ");
            }
            var input = Console.ReadLine();
            if (int.TryParse(input, out int choice) && choice >= min && choice <= max)
                return choice;
            using (ConsoleStyler.WithColor(ConsoleColor.Red))
            {
                Console.WriteLine($"  Invalid selection. Please enter a number between {min} and {max}.");
            }
        }
    }
}
