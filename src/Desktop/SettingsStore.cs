using System.Text.Json;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Persists lightweight UI preferences (provider / model selection) so the
/// desktop app remembers the user's choice between launches.
/// </summary>
internal static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodingAssistant",
        "settings.json");

    public static string? Provider { get; private set; }
    public static string? Model { get; private set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String)
                Provider = p.GetString();
            if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                Model = m.GetString();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
    }

    public static void Save(string? provider, string? model)
    {
        if (provider != null) Provider = provider;
        if (model != null) Model = model;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                provider = Provider,
                model = Model
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Persistence is best-effort.
        }
    }
}
