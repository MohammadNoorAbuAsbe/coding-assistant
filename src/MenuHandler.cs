using System.Diagnostics;

namespace TerminalAiAssistant;

public static class MenuHandler
{
    public static string SelectProvider(Dictionary<string, ProviderConfig> providers)
    {
        var list = providers.Values.ToList();
        var keys = providers.Keys.ToList();

        Console.WriteLine("\nSelect provider:");
        for (int i = 0; i < list.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {list[i].DisplayName}");
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
            Console.WriteLine($"\nModel: {models[0]}");
            return models[0];
        }

        Console.WriteLine("\nSelect model:");
        for (int i = 0; i < models.Count; i++)
        {
            var note = models[i] == config.DefaultModel ? " (default)" : "";
            Console.WriteLine($"  {i + 1}. {models[i]}{note}");
        }

        var choice = GetChoice(1, models.Count);
        return models[choice - 1];
    }

    public static string GetPrompt()
    {
        Console.Write("> ");
        var prompt = Console.ReadLine();
        return prompt?.Trim() ?? "";
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
            Console.Write($"Enter choice ({min}-{max}): ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out int choice) && choice >= min && choice <= max)
                return choice;
            Console.WriteLine($"Invalid input. Please enter a number between {min} and {max}.");
        }
    }
}
