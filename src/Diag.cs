namespace TerminalAiAssistant;

/// <summary>
/// Lightweight file log used to trace engine → UI event flow. GUI apps have
/// no console, so engine-side diagnostics go through this hook (assigned by
/// the host at startup).
/// </summary>
internal static class Diag
{
    public static Action<string>? Hook { get; set; }

    public static void Log(string line)
    {
        try
        {
            Hook?.Invoke(line);
        }
        catch
        {
            // Diagnostics must never break the engine.
        }
    }
}
